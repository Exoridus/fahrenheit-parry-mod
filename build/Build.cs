using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using static Nuke.Common.Assert;

internal sealed partial class BuildScript : NukeBuild
{
    [Parameter] readonly string Configuration = IsServerBuild ? "Release" : "Debug";
    [Parameter] readonly string FahrenheitRepo = "https://github.com/peppy-enterprises/fahrenheit.git";
    [Parameter] readonly string FahrenheitDir = ".workspace/fahrenheit";
    [Parameter] readonly string FahrenheitRef = string.Empty;
    [Parameter] readonly string NativeMSBuildExe = string.Empty;
    [Parameter] readonly string ModId = "fhparry";

    [Parameter] readonly string BuildTarget = "mod";
    [Parameter] readonly string DeployTarget = "mod";
    [Parameter] readonly string DeployMode = "merge";
    [Parameter] readonly string AutoDeployMode = string.Empty;

    [Parameter] readonly string GameDir = string.Empty;
    [Parameter] readonly string Repository = string.Empty;
    [Parameter] readonly string Bump = "patch";

    [Parameter] readonly bool Full;
    [Parameter] readonly bool DryRun;
    [Parameter] readonly bool NonInteractive;

    [Parameter] readonly string CommitType = "chore";
    [Parameter] readonly string CommitScope = string.Empty;
    [Parameter] readonly string CommitMessage = string.Empty;
    [Parameter] readonly bool CommitBreaking;

    [Parameter] readonly string Range = string.Empty;
    [Parameter] readonly string CommitFile = string.Empty;
    [Parameter] readonly string Message = string.Empty;

    [Parameter] readonly string Tag = string.Empty;
    [Parameter] readonly string Output = ".release/release-notes.txt";
    [Parameter] readonly string DeployDir = ".workspace/fahrenheit/artifacts/deploy/rel";
    [Parameter] readonly string OutDir = ".release";

    public static int Main() => Execute<BuildScript>(x => x.Help);

    bool InteractiveSession => !NonInteractive && !IsServerBuild && Environment.UserInteractive;
    AbsolutePath WorkspaceDir => RootDirectory / ".workspace";
    AbsolutePath LocalConfigPath => WorkspaceDir / "dev.local.json";
    AbsolutePath ReleaseFahrenheitRefPath => RootDirectory / "fahrenheit.release.ref";
    AbsolutePath ManifestPath => RootDirectory / "fhparry.manifest.json";

    Target Help => _ => _
        .Executes(() =>
        {
            Log.Information("Targets:");
            Log.Information("  build.cmd install [--full] [--dry-run]");
            Log.Information("  build.cmd setup");
            Log.Information("  build.cmd setupautodeploy [--gamedir path] [--autodeploymode none|update|replace|mod-only]");
            Log.Information("  build.cmd verify");
            Log.Information("  build.cmd build [--buildtarget mod|full]");
            Log.Information("  build.cmd deploy [--deploytarget mod|full] [--deploymode merge|replace] [--gamedir path]");
            Log.Information("  build.cmd releaseversion [--bump patch|minor|major]");
            Log.Information("  build.cmd releaseready [--repository owner/repo] [--tag vX.Y.Z]");
            Log.Information("  build.cmd packagerelease --tag vX.Y.Z");
            Log.Information("  build.cmd generatereleasenotes --tag vX.Y.Z --repository owner/repo");
            Log.Information("  build.cmd commit [--committype feat] [--commitscope ui] [--commitmessage \"message\"] [--commitbreaking true]");
            Log.Information("  build.cmd validatecommitmessage --commitfile .git/COMMIT_EDITMSG");
            Log.Information("  build.cmd validatecommitrange --range origin/main..HEAD");
        });

    Target Install => _ => _
        .Executes(() =>
        {
            EnsureWingetAvailable();
            EnsureGitInstalled();
            EnsureDotNetSdk10Installed();

            if (Full)
            {
                EnsureMsbuildInstalled();
                EnsureVcpkgInstalledAndIntegrated();
            }

            Log.Information("Prerequisite check/install finished.");
        });

    Target SetupHooks => _ => _
        .Executes(() =>
        {
            RequireGitRepository();
            if (!File.Exists(RootDirectory / ".githooks" / "commit-msg"))
            {
                Fail("Missing .githooks/commit-msg.");
            }

            RunChecked("git", "config --local core.hooksPath .githooks", "Setup hooks");
        });

    Target SetupGameDir => _ => _
        .Executes(() =>
        {
            var resolved = ResolveGameDir(promptIfMissing: true, persist: true);
            Log.Information($"GAME_DIR={resolved}");
        });

    Target SetupAutoDeploy => _ => _
        .Executes(SetupAutoDeployCore);

    Target Setup => _ => _
        .DependsOn(SetupHooks)
        .Executes(() =>
        {
            RunBuildProjTarget("Setup", Configuration, includeNativeMsbuild: false, fahrenheitRef: ResolveFahrenheitRef(useReleaseRef: false));

            SetupAutoDeployCore();

            if (InteractiveSession && AskYesNo("Run first full build now? (Recommended)", defaultYes: true))
            {
                RunBuildProjTarget("Build", Configuration, includeNativeMsbuild: true, fahrenheitRef: ResolveFahrenheitRef(useReleaseRef: false));
            }
        });

    Target Verify => _ => _
        .Executes(() =>
        {
            if (!IsValidConventionalCommit("feat: selftest commit format") || IsValidConventionalCommit("invalid message"))
            {
                Fail("Commit validator selftest failed.");
            }

            RunVerifyCore(Configuration);
        });

    Target Build => _ => _.Executes(() => BuildCore(BuildTarget, Configuration, useReleaseRef: false));
    Target BuildMod => _ => _.Executes(() => BuildCore("mod", Configuration, useReleaseRef: false));
    Target BuildFull => _ => _.Executes(() => BuildCore("full", Configuration, useReleaseRef: false));
    Target BuildRelease => _ => _.Executes(() => BuildCore("full", "Release", useReleaseRef: true));

    Target Deploy => _ => _.Executes(() => DeployCore(DeployTarget, DeployMode, Configuration));
    Target DeployMod => _ => _.Executes(() => DeployCore("mod", "merge", Configuration));
    Target DeployFull => _ => _.Executes(() => DeployCore("full", "merge", Configuration));

    Target BuildAndDeploy => _ => _.Executes(() =>
    {
        BuildCore("mod", Configuration, useReleaseRef: false);
        DeployCore("mod", "merge", Configuration);
    });

    Target BuildAndDeployMod => _ => _.DependsOn(BuildAndDeploy);

    Target BuildAndDeployFull => _ => _.Executes(() =>
    {
        BuildCore("full", Configuration, useReleaseRef: false);
        DeployCore("full", "merge", Configuration);
    });

    Target Changelog => _ => _.Executes(() =>
    {
        GenerateChangelogCore(
            tagOverride: string.IsNullOrWhiteSpace(Tag) ? null : Tag,
            outputPath: RootDirectory / "CHANGELOG.md",
            repositorySlug: ResolveRepositorySlug(Repository));
    });

    Target GenerateReleaseNotes => _ => _.Executes(() =>
    {
        if (string.IsNullOrWhiteSpace(Tag))
        {
            Fail("Missing --tag.");
        }

        var repoSlug = ResolveRepositorySlug(Repository);
        if (string.IsNullOrWhiteSpace(repoSlug))
        {
            Fail("Missing --repository owner/repo.");
        }

        GenerateReleaseNotesCore(
            tag: Tag,
            repositorySlug: repoSlug,
            outputPath: ResolvePath(Output));
    });

    Target PackageRelease => _ => _.Executes(() =>
    {
        if (string.IsNullOrWhiteSpace(Tag))
        {
            Fail("Missing --tag.");
        }

        PackageReleaseCore(Tag, ResolvePath(DeployDir), ResolvePath(OutDir), ModId);
    });

    Target ReleaseReady => _ => _.Executes(ReleaseReadyCore);

    Target ReleaseVersion => _ => _.Executes(ReleaseVersionCore);

    Target Commit => _ => _.Executes(() =>
    {
        var requestedMessage = CommitMessage;
        var requestedType = CommitType;
        var requestedScope = CommitScope;
        var requestedBreaking = CommitBreaking;

        if (string.IsNullOrWhiteSpace(requestedMessage))
        {
            if (!InteractiveSession)
            {
                Fail("Missing --commitmessage. In interactive mode, you can run build.cmd commit without arguments.");
            }

            var wizard = RunCommitWizard();
            if (!wizard.Confirmed)
            {
                Fail("Commit canceled.");
            }

            requestedType = wizard.Type;
            requestedScope = wizard.Scope;
            requestedMessage = wizard.Message;
            requestedBreaking = wizard.Breaking;
        }

        var subject = BuildCommitSubject(requestedType, requestedScope, requestedMessage, requestedBreaking);
        if (!IsValidConventionalCommit(subject))
        {
            Fail($"Invalid Conventional Commit subject: {subject}");
        }

        RunChecked("git", $"commit -m {Quote(subject)}", "Create commit");
    });

    Target ValidateCommitMessage => _ => _.Executes(() =>
    {
        if (!string.IsNullOrWhiteSpace(CommitFile))
        {
            ValidateCommitMessageFromFile(ResolvePath(CommitFile));
            return;
        }

        if (string.IsNullOrWhiteSpace(Message))
        {
            Fail("Missing --commitfile or --message.");
        }

        ValidateCommitMessageString(Message);
    });

    Target ValidateCommitRange => _ => _.Executes(() =>
    {
        if (string.IsNullOrWhiteSpace(Range))
        {
            Fail("Missing --range BASE..HEAD.");
        }

        ValidateCommitRangeCore(Range);
    });

    void BuildCore(string target, string configuration, bool useReleaseRef)
    {
        var effectiveFahrenheitRef = ResolveFahrenheitRef(useReleaseRef);
        var t = target.Trim().ToLowerInvariant();
        if (t == "mod")
        {
            RunBuildProjTarget("BuildModOnly", configuration, includeNativeMsbuild: false, fahrenheitRef: effectiveFahrenheitRef);
        }
        else if (t == "full")
        {
            RunBuildProjTarget("Build", configuration, includeNativeMsbuild: true, fahrenheitRef: effectiveFahrenheitRef);
        }
        else
        {
            Fail($"Invalid build target '{target}'. Use mod or full.");
        }

        TryAutoDeployAfterBuild(t, configuration, useReleaseRef);
    }

    void RunBuildProjTarget(string target, string configuration, bool includeNativeMsbuild, string fahrenheitRef)
    {
        var args = new StringBuilder();
        args.Append("msbuild ");
        args.Append(Quote(RootDirectory / "build.proj"));
        args.Append(" -nologo -verbosity:minimal");
        args.Append($" -t:{target}");
        args.Append($" -p:Configuration={Quote(configuration)}");
        args.Append($" -p:FahrenheitRepo={Quote(FahrenheitRepo)}");
        args.Append($" -p:FahrenheitDir={Quote(ResolvePath(FahrenheitDir))}");
        args.Append($" -p:FahrenheitRef={Quote(fahrenheitRef)}");

        if (includeNativeMsbuild && !string.IsNullOrWhiteSpace(NativeMSBuildExe))
        {
            args.Append($" -p:NativeMSBuildExe={Quote(NativeMSBuildExe)}");
        }

        RunChecked("dotnet", args.ToString(), $"MSBuild target {target}");
    }

    string ResolveFahrenheitRef(bool useReleaseRef)
    {
        if (!string.IsNullOrWhiteSpace(FahrenheitRef))
        {
            return FahrenheitRef.Trim();
        }

        if (useReleaseRef)
        {
            if (!File.Exists(ReleaseFahrenheitRefPath))
            {
                Fail($"Missing release ref file: {ReleaseFahrenheitRefPath}. Run build.cmd releaseversion first.");
            }

            var pinned = File.ReadAllText(ReleaseFahrenheitRefPath).Trim();
            if (string.IsNullOrWhiteSpace(pinned))
            {
                Fail($"Release ref file is empty: {ReleaseFahrenheitRefPath}");
            }

            return pinned;
        }

        return "origin/main";
    }

    void ReleaseVersionCore()
    {
        RequireGitRepository();
        EnsureCleanWorkingTree();

        var bump = Bump.Trim().ToLowerInvariant();
        if (bump != "major" && bump != "minor" && bump != "patch")
        {
            Fail("Invalid bump level. Use patch, minor, or major.");
        }

        var latestTag = TryGetLatestSemverTag();
        var currentVersion = latestTag is null ? new SemVersion(0, 0, 0) : ParseSemVersion(latestTag);
        var nextVersion = bump switch
        {
            "major" => new SemVersion(currentVersion.Major + 1, 0, 0),
            "minor" => new SemVersion(currentVersion.Major, currentVersion.Minor + 1, 0),
            _ => new SemVersion(currentVersion.Major, currentVersion.Minor, currentVersion.Patch + 1)
        };

        var newTag = $"v{nextVersion.Major}.{nextVersion.Minor}.{nextVersion.Patch}";
        if (GitRefExists(newTag))
        {
            Fail($"Tag already exists: {newTag}");
        }

        var repoSlug = ResolveRepositorySlug(Repository);
        GenerateChangelogCore(tagOverride: newTag, outputPath: RootDirectory / "CHANGELOG.md", repositorySlug: repoSlug);
        UpdateManifestVersion(nextVersion, repoSlug);
        PinReleaseFahrenheitRef();

        var filesToStage = new[]
        {
            Quote(RootDirectory / "CHANGELOG.md"),
            Quote(ManifestPath),
            Quote(ReleaseFahrenheitRefPath)
        };
        RunChecked("git", $"add {string.Join(" ", filesToStage)}", "Stage release files");

        var commitMessage = $"chore(release): {newTag}";
        var commitResult = RunProcess("git", $"commit -m {Quote(commitMessage)}", "Create release commit", silent: true);
        if (commitResult.ExitCode != 0)
        {
            RunChecked("git", $"commit --allow-empty -m {Quote(commitMessage)}", "Create empty release commit");
        }

        RunChecked("git", $"tag -a {Quote(newTag)} -m {Quote(commitMessage)}", "Create release tag");
        Log.Information($"Created release commit and tag: {newTag}");
        Log.Information("Next step: git push origin main --follow-tags");
    }

    void PinReleaseFahrenheitRef()
    {
        var lsRemote = RunProcess(
            "git",
            $"ls-remote {Quote(FahrenheitRepo)} refs/heads/main",
            "Resolve Fahrenheit main ref",
            silent: true);

        if (lsRemote.ExitCode != 0)
        {
            Fail($"Failed to resolve Fahrenheit main ref.{Environment.NewLine}{lsRemote.StdErr}");
        }

        var firstLine = lsRemote.StdOut
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstLine))
        {
            Fail("Could not parse Fahrenheit main ref from git ls-remote output.");
        }

        var hash = firstLine!.Split('\t', ' ').FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(hash))
        {
            Fail("Could not extract Fahrenheit commit hash from ls-remote output.");
        }

        File.WriteAllText(ReleaseFahrenheitRefPath, hash + Environment.NewLine);
        Log.Information($"Pinned release Fahrenheit ref: {hash}");
    }

    void GenerateChangelogCore(string? tagOverride, string outputPath, string repositorySlug)
    {
        RequireGitRepository();

        var currentLabel = ResolveCurrentChangelogLabel(tagOverride);
        var currentRef = currentLabel == "Initial Commit"
            ? "HEAD"
            : (GitRefExists(currentLabel) ? currentLabel : "HEAD");
        var currentCommit = GitSingleLineOrFallback($"rev-parse {Quote(currentRef)}", "rev-parse HEAD", "HEAD");
        var previousTag = currentLabel == "Initial Commit" ? string.Empty : ResolvePreviousSemverTag(currentLabel);
        var repoUrl = string.IsNullOrWhiteSpace(repositorySlug) ? string.Empty : $"https://github.com/{repositorySlug}";

        string range;
        if (currentLabel == "Initial Commit")
        {
            range = "HEAD";
        }
        else if (!string.IsNullOrWhiteSpace(previousTag))
        {
            range = $"{previousTag}..{currentRef}";
        }
        else
        {
            range = currentRef;
        }

        var releaseDate = GitSingleLineOrFallback($"log -1 --date=short --format=%ad {Quote(currentRef)}", "log -1 --date=short --format=%ad", DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var commits = CollectCommitLines(range, repoUrl);
        if (commits.Count == 0)
        {
            commits.Add("- Initial commit.");
        }

        var content = new StringBuilder();
        content.AppendLine("# Changelog");
        content.AppendLine();

        if (currentLabel == "Initial Commit")
        {
            if (!string.IsNullOrWhiteSpace(repoUrl))
            {
                content.AppendLine($"## [Initial Commit]({repoUrl}/tree/{currentCommit}) ({releaseDate})");
                content.AppendLine($"[Commit History]({repoUrl}/commits/{currentCommit})");
            }
            else
            {
                content.AppendLine($"## Initial Commit ({releaseDate})");
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(repoUrl))
            {
                content.AppendLine($"## [{currentLabel}]({repoUrl}/releases/tag/{currentLabel}) ({releaseDate})");
                if (!string.IsNullOrWhiteSpace(previousTag))
                {
                    content.AppendLine($"[Full Changelog]({repoUrl}/compare/{previousTag}...{currentLabel}) | [Previous Releases]({repoUrl}/releases)");
                }
                else
                {
                    content.AppendLine($"[Initial Release Commits]({repoUrl}/commits/{currentCommit}) | [All Releases]({repoUrl}/releases)");
                }
            }
            else
            {
                content.AppendLine($"## {currentLabel} ({releaseDate})");
            }
        }

        content.AppendLine();
        foreach (var commit in commits)
        {
            content.AppendLine(commit);
        }

        File.WriteAllText(outputPath, content.ToString().Replace("\r\n", "\n"));
        Log.Information($"Changelog generated: {outputPath}");
    }

    void GenerateReleaseNotesCore(string tag, string repositorySlug, string outputPath)
    {
        RequireGitRepository();

        var repoUrl = $"https://github.com/{repositorySlug}";
        var previousTag = ResolvePreviousAnyTag(tag);
        var releaseDate = GitSingleLineOrFallback($"log -1 --date=short --format=%ad {Quote(tag)}^{{commit}}", "log -1 --date=short --format=%ad", DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var range = string.IsNullOrWhiteSpace(previousTag) ? tag : $"{previousTag}..{tag}";
        var commits = CollectCommitLines(range, repoUrl);
        if (commits.Count == 0)
        {
            commits.Add("- Initial release");
        }

        var content = new StringBuilder();
        var project = repositorySlug.Split('/').LastOrDefault() ?? repositorySlug;
        var fullPackage = $"{repoUrl}/releases/download/{tag}/fahrenheit-full-{tag}.zip";
        var modPackage = $"{repoUrl}/releases/download/{tag}/{ModId}-mod-{tag}.zip";
        content.AppendLine($"# {project} {tag} ({releaseDate})");
        content.AppendLine();
        content.AppendLine("This release provides pre-built ZIP packages for Windows (FFX/X-2 HD Remaster + Fahrenheit):");
        content.AppendLine();
        content.AppendLine($"- Full package: [fahrenheit-full-{tag}.zip]({fullPackage})");
        content.AppendLine($"  - SHA256: [fahrenheit-full-{tag}.zip.sha256]({fullPackage}.sha256)");
        content.AppendLine($"- Mod-only package: [{ModId}-mod-{tag}.zip]({modPackage})");
        content.AppendLine($"  - SHA256: [{ModId}-mod-{tag}.zip.sha256]({modPackage}.sha256)");
        content.AppendLine();
        content.AppendLine("## Installation");
        content.AppendLine();
        content.AppendLine("1. Download one of the ZIP packages above.");
        content.AppendLine("2. Extract into your game directory (folder containing `FFX.exe`).");
        content.AppendLine("3. Launch through your normal Fahrenheit flow.");
        content.AppendLine();
        content.AppendLine("## Changes in This Release");
        content.AppendLine();
        content.AppendLine($"[View this tag]({repoUrl}/releases/tag/{tag}) | [All Releases]({repoUrl}/releases)");
        content.AppendLine();
        foreach (var commit in commits)
        {
            content.AppendLine(commit);
        }
        content.AppendLine();
        content.AppendLine("---");
        content.AppendLine();

        if (!string.IsNullOrWhiteSpace(previousTag))
        {
            content.AppendLine($"Full Changelog: {repoUrl}/compare/{previousTag}...{tag} | [README]({repoUrl}/blob/main/README.md)");
        }
        else
        {
            content.AppendLine($"Full Changelog: {repoUrl}/commits/{tag} | [README]({repoUrl}/blob/main/README.md)");
        }

        File.WriteAllText(outputPath, content.ToString().Replace("\r\n", "\n"));
        Log.Information($"Release notes generated: {outputPath}");
    }

    string ResolveRepositorySlug(string preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred.Trim();
        }

        var remote = GitSingleLineOrFallback("remote get-url origin", "remote get-url origin", string.Empty);
        if (string.IsNullOrWhiteSpace(remote))
        {
            return string.Empty;
        }

        if (remote.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            return remote["git@github.com:".Length..].Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        if (remote.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            return remote["https://github.com/".Length..].Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return string.Empty;
    }

    string ResolveCurrentChangelogLabel(string? tagOverride)
    {
        if (!string.IsNullOrWhiteSpace(tagOverride))
        {
            return tagOverride;
        }

        return TryGetLatestSemverTag() ?? "Initial Commit";
    }

    string ResolvePreviousSemverTag(string currentTag)
    {
        var tags = GetSemverTagsDescending();
        foreach (var tag in tags)
        {
            if (!tag.Equals(currentTag, StringComparison.OrdinalIgnoreCase))
            {
                return tag;
            }
        }

        return string.Empty;
    }

    string ResolvePreviousAnyTag(string currentTag)
    {
        var result = RunProcess("git", "tag --sort=-v:refname", "List tags", silent: true);
        if (result.ExitCode != 0)
        {
            return string.Empty;
        }

        foreach (var tag in result.StdOut.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = tag.Trim();
            if (!trimmed.Equals(currentTag, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }
        }

        return string.Empty;
    }

    bool GitRefExists(string reference)
    {
        var result = RunProcess("git", $"rev-parse {Quote(reference)}^{{commit}}", "Check git ref", silent: true);
        return result.ExitCode == 0;
    }

    List<string> CollectCommitLines(string range, string repoUrl)
    {
        var format = string.IsNullOrWhiteSpace(repoUrl)
            ? "- %s (%h)"
            : $"- %s ([%h]({repoUrl}/commit/%H))";

        var result = RunProcess("git", $"log --pretty=format:{Quote(format)} --no-merges {Quote(range)}", "Collect commits", silent: true);
        if (result.ExitCode != 0)
        {
            return new List<string>();
        }

        return result.StdOut
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }

    string? TryGetLatestSemverTag()
    {
        var tags = GetSemverTagsDescending();
        return tags.Count == 0 ? null : tags[0];
    }

    List<string> GetSemverTagsDescending()
    {
        var result = RunProcess("git", "tag --list v* --sort=-v:refname", "List semver tags", silent: true);
        if (result.ExitCode != 0)
        {
            return new List<string>();
        }

        var tags = new List<string>();
        foreach (var line in result.StdOut.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var tag = line.Trim();
            if (TryParseSemVersion(tag, out _))
            {
                tags.Add(tag);
            }
        }

        return tags;
    }

    SemVersion ParseSemVersion(string tag)
    {
        if (!TryParseSemVersion(tag, out var version))
        {
            Fail($"Invalid semantic version tag: {tag}");
        }

        return version;
    }

    static bool TryParseSemVersion(string tag, out SemVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(tag) || !tag.StartsWith("v", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = tag[1..].Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var patch))
        {
            return false;
        }

        version = new SemVersion(major, minor, patch);
        return true;
    }

    string GitSingleLineOrFallback(string primaryArgs, string fallbackArgs, string fallbackValue)
    {
        var primary = RunProcess("git", primaryArgs, "Read git value", silent: true);
        if (primary.ExitCode == 0)
        {
            var line = primary.StdOut.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line.Trim();
            }
        }

        var fallback = RunProcess("git", fallbackArgs, "Read fallback git value", silent: true);
        if (fallback.ExitCode == 0)
        {
            var line = fallback.StdOut.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line.Trim();
            }
        }

        return fallbackValue;
    }

    void EnsureCleanWorkingTree()
    {
        RunChecked("git", "update-index -q --refresh", "Refresh git index", silent: true);

        if (RunProcess("git", "diff --quiet --exit-code", "Check unstaged changes", silent: true).ExitCode != 0)
        {
            Fail("Working tree has unstaged changes.");
        }

        if (RunProcess("git", "diff --cached --quiet --exit-code", "Check staged changes", silent: true).ExitCode != 0)
        {
            Fail("Working tree has staged but uncommitted changes.");
        }

        var untracked = RunProcess("git", "ls-files --others --exclude-standard", "Check untracked files", silent: true);
        if (untracked.ExitCode != 0)
        {
            Fail($"Failed to inspect untracked files.{Environment.NewLine}{untracked.StdErr}");
        }

        if (untracked.StdOut.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Length > 0)
        {
            Fail("Working tree has untracked files.");
        }
    }

    void UpdateManifestVersion(SemVersion version, string repoSlug)
    {
        if (!File.Exists(ManifestPath))
        {
            Fail($"Manifest file not found: {ManifestPath}");
        }

        var jsonText = File.ReadAllText(ManifestPath);
        var json = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonText) ?? new Dictionary<string, object>();
        json["Version"] = version.ToString();
        if (!string.IsNullOrWhiteSpace(repoSlug))
        {
            json["Link"] = $"https://github.com/{repoSlug}";
        }

        var output = JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ManifestPath, output + Environment.NewLine);
    }

    void ValidateCommitMessageString(string message)
    {
        if (!IsValidConventionalCommit(message))
        {
            Fail($"Invalid commit subject: {message}");
        }
    }

    void ValidateCommitMessageFromFile(string path)
    {
        if (!File.Exists(path))
        {
            Fail($"Commit message file not found: {path}");
        }

        var firstSubject = File.ReadLines(path)
            .Select(line => line.Trim())
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith("#", StringComparison.Ordinal));

        if (string.IsNullOrWhiteSpace(firstSubject))
        {
            Fail("No commit subject found in commit message file.");
        }

        ValidateCommitMessageString(firstSubject!);
    }

    void EnsureWingetAvailable()
    {
        if (!CommandExists("winget"))
        {
            Fail("winget is required for automated prerequisite install. Install App Installer first.");
        }
    }

    void EnsureGitInstalled()
    {
        if (CommandExists("git"))
        {
            Log.Information("[OK] Git is already installed.");
            return;
        }

        PromptInstallOrFail(
            title: "Git not found.",
            detail: "Git is required for clone/update, changelog generation, tags, and release workflows.",
            adminRequired: true);

        InstallWingetPackage("Git.Git", "Git", overrideArgs: null);

        if (!CommandExists("git"))
        {
            Fail("Git install completed but command not found on PATH yet. Open a new terminal and retry.");
        }

        Log.Information("Git installation verified.");
    }

    void EnsureDotNetSdk10Installed()
    {
        if (CommandExists("dotnet") && DotNetSdkMajorInstalled(10))
        {
            Log.Information("[OK] .NET SDK 10.x is already installed.");
            return;
        }

        PromptInstallOrFail(
            title: ".NET SDK 10.x not found.",
            detail: ".NET SDK 10.x is required to run NUKE and to build this project.",
            adminRequired: true);

        InstallWingetPackage("Microsoft.DotNet.SDK.10", ".NET SDK 10.x", overrideArgs: null);

        if (!CommandExists("dotnet") || !DotNetSdkMajorInstalled(10))
        {
            Fail(".NET SDK 10.x verification failed after installation.");
        }

        Log.Information(".NET SDK 10.x installation verified.");
    }

    void EnsureMsbuildInstalled()
    {
        if (CommandExists("msbuild"))
        {
            Log.Information("[OK] MSBuild is already available.");
            return;
        }

        PromptInstallOrFail(
            title: "MSBuild not found.",
            detail: "Full builds require Visual Studio Build Tools with C++ and .NET desktop workloads.",
            adminRequired: true);

        InstallWingetPackage(
            packageId: "Microsoft.VisualStudio.2022.BuildTools",
            label: "Visual Studio Build Tools",
            overrideArgs: "--wait --quiet --norestart --nocache --add Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools --add Microsoft.VisualStudio.Workload.VCTools");

        if (!CommandExists("msbuild"))
        {
            Log.Warning("MSBuild may need a new terminal session to appear on PATH.");
        }
    }

    void EnsureVcpkgInstalledAndIntegrated()
    {
        var vcpkgExe = FindVcpkgExecutable();
        if (string.IsNullOrWhiteSpace(vcpkgExe))
        {
            PromptInstallOrFail(
                title: "vcpkg not found.",
                detail: "A local vcpkg clone will be bootstrapped under .workspace/vcpkg and integrated for this user.",
                adminRequired: false);

            if (!DryRun)
            {
                var vcpkgRoot = WorkspaceDir / "vcpkg";
                EnsureDir(WorkspaceDir);

                if (!Directory.Exists(vcpkgRoot))
                {
                    RunChecked("git", $"clone https://github.com/microsoft/vcpkg {Quote(vcpkgRoot)}", "Clone vcpkg", showSpinner: true, silent: true);
                }

                var bootstrap = vcpkgRoot / "bootstrap-vcpkg.bat";
                if (!File.Exists(bootstrap))
                {
                    Fail($"Missing bootstrap script: {bootstrap}");
                }

                RunChecked("cmd", $"/c \"\"{bootstrap}\" -disableMetrics\"", "Bootstrap vcpkg", workingDirectory: vcpkgRoot, showSpinner: true, silent: true);
            }

            vcpkgExe = FindVcpkgExecutable();
        }

        if (string.IsNullOrWhiteSpace(vcpkgExe))
        {
            Fail("vcpkg could not be located after bootstrap.");
        }

        if (DryRun)
        {
            Log.Information($"[DRY-RUN] {vcpkgExe} integrate install");
            return;
        }

        RunChecked(vcpkgExe, "integrate install", "Integrate vcpkg", showSpinner: true, silent: true);
        Log.Information("vcpkg integration complete.");
    }

    void PromptInstallOrFail(string title, string detail, bool adminRequired)
    {
        Log.Warning(title);
        Log.Information(detail);
        if (adminRequired)
        {
            Log.Warning("This install may require administrator privileges and can trigger a UAC prompt.");
        }

        if (!InteractiveSession)
        {
            Fail("Missing prerequisite in non-interactive mode. Install prerequisites first or run interactively.");
        }

        if (!AskYesNo("Install now?", defaultYes: false))
        {
            Fail("Installation declined. Aborting.");
        }
    }

    void InstallWingetPackage(string packageId, string label, string? overrideArgs)
    {
        var args = new StringBuilder();
        args.Append($"install --id {Quote(packageId)} -e --source winget --accept-source-agreements --accept-package-agreements --silent");
        if (!string.IsNullOrWhiteSpace(overrideArgs))
        {
            args.Append($" --override {Quote(overrideArgs)}");
        }

        if (DryRun)
        {
            Log.Information($"[DRY-RUN] winget {args}");
            return;
        }

        RunChecked("winget", args.ToString(), $"Install {label}", showSpinner: true, silent: true);
    }

    string ResolveGameDirForAutoDeploySetup(LocalConfig cfg)
    {
        var fromArg = NormalizePathOrEmpty(GameDir);
        if (!string.IsNullOrWhiteSpace(fromArg))
        {
            if (IsValidGameDir(fromArg))
            {
                return fromArg;
            }

            Log.Warning($"Provided --gamedir is invalid: {fromArg}");
        }

        var fromConfig = NormalizePathOrEmpty(cfg.GameDir);
        if (IsValidGameDir(fromConfig))
        {
            return fromConfig;
        }

        var detected = DetectGameDir();
        if (IsValidGameDir(detected))
        {
            if (!InteractiveSession)
            {
                return detected;
            }

            if (AskYesNo($"Detected game path '{detected}'. Use this path?", defaultYes: true))
            {
                return detected;
            }
        }

        if (InteractiveSession)
        {
            Console.Write("Enter game installation directory (must contain FFX.exe): ");
            var manual = NormalizePathOrEmpty(Console.ReadLine());
            if (IsValidGameDir(manual))
            {
                return manual;
            }

            if (!string.IsNullOrWhiteSpace(manual))
            {
                Log.Warning($"Invalid game directory: {manual}");
            }
        }

        return string.Empty;
    }

    string ResolveAutoDeployModeForSetup(LocalConfig cfg)
    {
        var prefilled = NormalizeAutoDeployModeOrEmpty(AutoDeployMode);
        if (!string.IsNullOrWhiteSpace(AutoDeployMode) && string.IsNullOrWhiteSpace(prefilled))
        {
            Fail($"Invalid --autodeploymode '{AutoDeployMode}'. Use none, update, replace, or mod-only.");
        }

        if (!string.IsNullOrWhiteSpace(prefilled))
        {
            return prefilled;
        }

        var fromConfig = NormalizeAutoDeployModeOrEmpty(cfg.DeployMode);
        if (!string.IsNullOrWhiteSpace(fromConfig) && fromConfig != "none")
        {
            return fromConfig;
        }

        if (!InteractiveSession)
        {
            return "none";
        }

        Log.Information("How should full local builds be auto-deployed?");
        Log.Information("  1) update (default) - copy and overwrite, no deletions");
        Log.Information("  2) replace - replace full deployed directory");
        Log.Information("  3) mod-only - only deploy mod payload, even for full builds");
        Log.Information("  4) none - disable automatic deployment");
        Console.Write("Select deploy mode [1/2/3/4] (default 1): ");
        var input = (Console.ReadLine() ?? string.Empty).Trim().ToLowerInvariant();

        return input switch
        {
            "" or "1" or "update" or "merge" => "update",
            "2" or "replace" => "replace",
            "3" or "mod-only" or "modonly" or "mod" => "mod-only",
            "4" or "none" or "off" or "disable" => "none",
            _ => "none"
        };
    }

    void TryAutoDeployAfterBuild(string buildTarget, string configuration, bool useReleaseRef)
    {
        if (useReleaseRef || IsServerBuild)
        {
            return;
        }

        var cfg = LoadLocalConfig();
        var configuredMode = NormalizeAutoDeployModeOrEmpty(AutoDeployMode);
        if (!string.IsNullOrWhiteSpace(AutoDeployMode) && string.IsNullOrWhiteSpace(configuredMode))
        {
            Fail($"Invalid --autodeploymode '{AutoDeployMode}'. Use none, update, replace, or mod-only.");
        }

        if (string.IsNullOrWhiteSpace(configuredMode))
        {
            configuredMode = NormalizeAutoDeployModeOrEmpty(cfg.DeployMode);
        }

        if (string.IsNullOrWhiteSpace(configuredMode))
        {
            configuredMode = "none";
        }

        if (configuredMode == "none")
        {
            return;
        }

        var gameDir = NormalizePathOrEmpty(GameDir);
        if (!string.IsNullOrWhiteSpace(GameDir) && !IsValidGameDir(gameDir))
        {
            Log.Warning($"Invalid --gamedir value '{GameDir}' (FFX.exe not found). Skipping automatic deploy.");
            return;
        }

        if (!IsValidGameDir(gameDir))
        {
            gameDir = NormalizePathOrEmpty(cfg.GameDir);
        }

        if (!IsValidGameDir(gameDir))
        {
            Log.Warning("Automatic deploy mode is set but GAME_DIR is missing/invalid. Skipping automatic deploy.");
            Log.Information("Run: build.cmd setupautodeploy");
            return;
        }

        var deployTarget = "mod";
        var deployMode = "merge";

        if (buildTarget == "full")
        {
            if (configuredMode == "replace")
            {
                deployTarget = "full";
                deployMode = "replace";
            }
            else if (configuredMode == "mod-only")
            {
                deployTarget = "mod";
                deployMode = "merge";
            }
            else
            {
                deployTarget = "full";
                deployMode = "merge";
            }
        }

        var ok = DeployFromArtifacts(gameDir, configuration, deployTarget, deployMode, failOnError: false, reason: "Automatic deploy");
        if (!ok)
        {
            Log.Warning("Automatic deploy failed and was skipped.");
        }
    }

    string NormalizeManualDeployMode(string mode)
    {
        var m = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return m switch
        {
            "merge" or "update" => "merge",
            "replace" => "replace",
            _ => FailWithReturn<string>($"Invalid deploy mode '{mode}'. Use merge or replace.")
        };
    }

    string NormalizeAutoDeployModeOrEmpty(string mode)
    {
        var m = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return m switch
        {
            "" => string.Empty,
            "none" or "off" or "disable" => "none",
            "update" or "merge" => "update",
            "replace" => "replace",
            "mod-only" or "modonly" or "mod" => "mod-only",
            _ => string.Empty
        };
    }

    bool DeployFromArtifacts(string gameDir, string configuration, string target, string mode, bool failOnError, string reason)
    {
        var normalizedTarget = target.Trim().ToLowerInvariant();
        var normalizedMode = NormalizeManualDeployMode(mode);
        var normalizedGameDir = NormalizePathOrEmpty(gameDir);
        if (!IsValidGameDir(normalizedGameDir))
        {
            var msg = $"Invalid GAME_DIR '{gameDir}' (FFX.exe not found).";
            if (failOnError) Fail(msg);
            Log.Warning(msg);
            return false;
        }

        var deployCfg = configuration.Equals("Release", StringComparison.OrdinalIgnoreCase) ? "rel" : "dbg";
        var sourceRoot = Path.Combine(ResolvePath(FahrenheitDir), "artifacts", "deploy", deployCfg);
        if (!Directory.Exists(sourceRoot))
        {
            var msg = $"Build output not found: {sourceRoot}";
            if (failOnError) Fail(msg);
            Log.Warning(msg);
            return false;
        }

        var targetRoot = Path.Combine(normalizedGameDir, "fahrenheit");
        var sourcePath = normalizedTarget == "full"
            ? sourceRoot
            : Path.Combine(sourceRoot, "mods", ModId);
        var destinationPath = normalizedTarget == "full"
            ? targetRoot
            : Path.Combine(targetRoot, "mods", ModId);

        if (!Directory.Exists(sourcePath))
        {
            var msg = $"Deploy source path does not exist: {sourcePath}";
            if (failOnError) Fail(msg);
            Log.Warning(msg);
            return false;
        }

        try
        {
            if (normalizedMode == "replace" && Directory.Exists(destinationPath))
            {
                Directory.Delete(destinationPath, recursive: true);
            }

            CopyDirectoryRecursive(sourcePath, destinationPath);
            EnsureLoadOrderEntry(Path.Combine(targetRoot, "mods", "loadorder"), ModId);
            Log.Information($"{reason}: deployed {normalizedTarget} ({normalizedMode}) to {destinationPath}");
            CleanupReleaseDirAfterDeploy();
            return true;
        }
        catch (Exception ex)
        {
            if (failOnError)
            {
                Fail($"{reason} failed: {ex.Message}");
            }

            Log.Warning($"{reason} failed: {ex.Message}");
            return false;
        }
    }

    void EnsureLoadOrderEntry(string loadOrderPath, string entry)
    {
        EnsureDir(Path.GetDirectoryName(loadOrderPath) ?? string.Empty);
        var existing = File.Exists(loadOrderPath)
            ? File.ReadAllLines(loadOrderPath)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList()
            : new List<string>();

        if (!existing.Any(x => x.Equals(entry, StringComparison.OrdinalIgnoreCase)))
        {
            existing.Add(entry);
            File.WriteAllLines(loadOrderPath, existing);
        }
    }

    string ResolveGameDir(bool promptIfMissing, bool persist)
    {
        var cfg = LoadLocalConfig();

        var fromArg = NormalizePathOrEmpty(GameDir);
        if (!string.IsNullOrWhiteSpace(GameDir) && !IsValidGameDir(fromArg))
        {
            Fail($"Invalid --gamedir value '{GameDir}' (FFX.exe not found).");
        }

        if (IsValidGameDir(fromArg))
        {
            if (persist)
            {
                cfg.GameDir = fromArg;
                SaveLocalConfig(cfg);
            }

            return fromArg;
        }

        var fromConfig = NormalizePathOrEmpty(cfg.GameDir);
        if (IsValidGameDir(fromConfig))
        {
            return fromConfig;
        }

        var detected = DetectGameDir();
        if (IsValidGameDir(detected))
        {
            Log.Information($"Auto-detected game directory: {detected}");
            if (persist)
            {
                cfg.GameDir = detected;
                SaveLocalConfig(cfg);
            }

            return detected;
        }

        if (promptIfMissing && InteractiveSession)
        {
            while (true)
            {
                Console.Write("Enter game install directory (must contain FFX.exe): ");
                var input = NormalizePathOrEmpty(Console.ReadLine());
                if (IsValidGameDir(input))
                {
                    if (persist)
                    {
                        cfg.GameDir = input;
                        SaveLocalConfig(cfg);
                    }

                    return input;
                }

                Log.Warning($"Invalid path: {input}");
            }
        }

        Fail("Could not resolve GAME_DIR. Pass --gamedir or run SetupGameDir.");
        return string.Empty;
    }

    void CleanupReleaseDirAfterDeploy()
    {
        var releaseDir = ResolvePath(".release");
        if (!Directory.Exists(releaseDir))
        {
            return;
        }

        try
        {
            Directory.Delete(releaseDir, recursive: true);
            Log.Information($"Cleaned up release directory: {releaseDir}");
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not clean release directory '{releaseDir}': {ex.Message}");
        }
    }

    string DetectGameDir()
    {
        foreach (var candidate in GameDirCandidates())
        {
            if (IsValidGameDir(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    IEnumerable<string> GameDirCandidates()
    {
        yield return @"C:\Games\Final Fantasy X-X2 - HD Remaster";
        yield return @"C:\Games\Final Fantasy X_X-2 HD Remaster";
        yield return @"C:\Games\FINAL FANTASY X_X-2 HD Remaster";

        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(pf86))
        {
            yield return Path.Combine(pf86, "Steam", "steamapps", "common", "FINAL FANTASY X_X-2 HD Remaster");
        }

        if (!string.IsNullOrWhiteSpace(pf))
        {
            yield return Path.Combine(pf, "Steam", "steamapps", "common", "FINAL FANTASY X_X-2 HD Remaster");
        }

        foreach (var drive in new[] { "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P" })
        {
            yield return $"{drive}:\\SteamLibrary\\steamapps\\common\\FINAL FANTASY X_X-2 HD Remaster";
            yield return $"{drive}:\\Games\\Final Fantasy X-X2 - HD Remaster";
        }
    }

    LocalConfig LoadLocalConfig()
    {
        if (!File.Exists(LocalConfigPath))
        {
            return new LocalConfig();
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(LocalConfigPath));
            var root = doc.RootElement;
            var cfg = new LocalConfig
            {
                GameDir = ReadJsonString(root, "GAME_DIR", "GameDir"),
                DeployMode = NormalizeAutoDeployModeOrEmpty(ReadJsonString(root, "DEPLOY_MODE", "DeployMode"))
            };

            if (string.IsNullOrWhiteSpace(cfg.DeployMode))
            {
                // Backward compatibility with deprecated AUTO_DEPLOY toggle.
                var legacyToggle = ReadJsonBool(root, "AUTO_DEPLOY", "AutoDeploy");
                cfg.DeployMode = legacyToggle == true ? "update" : "none";
            }

            return cfg;
        }
        catch
        {
            return new LocalConfig();
        }
    }

    void SaveLocalConfig(LocalConfig cfg)
    {
        EnsureDir(WorkspaceDir);
        var normalizedGameDir = NormalizePathOrEmpty(cfg.GameDir);
        var normalizedMode = NormalizeAutoDeployModeOrEmpty(cfg.DeployMode);
        if (string.IsNullOrWhiteSpace(normalizedMode))
        {
            normalizedMode = "none";
        }

        var payload = new Dictionary<string, object>
        {
            ["GAME_DIR"] = normalizedGameDir,
            ["DEPLOY_MODE"] = normalizedMode
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(LocalConfigPath, json + Environment.NewLine);
    }

    static string ReadJsonString(JsonElement root, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    static bool? ReadJsonBool(JsonElement root, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (root.TryGetProperty(key, out var value))
            {
                if (value.ValueKind == JsonValueKind.True) return true;
                if (value.ValueKind == JsonValueKind.False) return false;
                if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed)) return parsed;
            }
        }

        return null;
    }

    static T FailWithReturn<T>(string message)
    {
        Fail(message);
        return default!;
    }

    string FindVcpkgExecutable()
    {
        if (CommandExists("vcpkg"))
        {
            return "vcpkg";
        }

        var local = WorkspaceDir / "vcpkg" / "vcpkg.exe";
        return File.Exists(local) ? local : string.Empty;
    }

    void PackageReleaseCore(string tag, string deployRoot, string outRoot, string modId)
    {
        var modSource = Path.Combine(deployRoot, "mods", modId);
        if (!Directory.Exists(deployRoot) || !Directory.Exists(modSource))
        {
            Fail($"Deploy output not found. Expected {deployRoot} and {modSource}.");
        }

        var stage = Path.Combine(outRoot, "stage");
        var fullStage = Path.Combine(stage, "full");
        var modStage = Path.Combine(stage, "mod");
        var fullPayload = Path.Combine(fullStage, "fahrenheit");
        var modPayload = Path.Combine(modStage, modId);

        RecreateDir(stage);
        EnsureDir(fullPayload);
        EnsureDir(modPayload);

        CopyDirectoryRecursive(deployRoot, fullPayload);
        CopyDirectoryRecursive(modSource, modPayload);

        EnsureDir(outRoot);
        var fullZip = Path.Combine(outRoot, $"fahrenheit-full-{tag}.zip");
        var modZip = Path.Combine(outRoot, $"{modId}-mod-{tag}.zip");
        DeleteIfExists(fullZip);
        DeleteIfExists(modZip);

        ZipFile.CreateFromDirectory(fullStage, fullZip, CompressionLevel.Optimal, includeBaseDirectory: false);
        ZipFile.CreateFromDirectory(modStage, modZip, CompressionLevel.Optimal, includeBaseDirectory: false);

        WriteSha256(fullZip);
        WriteSha256(modZip);

        RecreateDir(stage);

        Log.Information($"Package output:{Environment.NewLine}  {fullZip}{Environment.NewLine}  {modZip}{Environment.NewLine}  {fullZip}.sha256{Environment.NewLine}  {modZip}.sha256");
    }

    void RunTestsIfAny(string configuration)
    {
        var projects = Directory.EnumerateFiles(RootDirectory, "*.csproj", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Contains("test", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.workspace{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (projects.Count == 0)
        {
            Log.Information("No test projects found outside .workspace. Skipping tests.");
            return;
        }

        foreach (var project in projects)
        {
            RunChecked("dotnet", $"test {Quote(project)} --configuration {Quote(configuration)} --nologo", $"Run tests for {Path.GetFileName(project)}");
        }
    }

    void ValidateCommitRangeCore(string range)
    {
        RequireGitRepository();

        var result = RunProcess("git", $"log --format=%s --no-merges {Quote(range)}", "Read commit range", showSpinner: false, silent: true);
        if (result.ExitCode != 0)
        {
            Fail($"Failed to read commit range '{range}'.{Environment.NewLine}{result.StdErr}");
        }

        var invalid = result.StdOut
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !IsValidConventionalCommit(x))
            .ToList();

        if (invalid.Count > 0)
        {
            Fail("Invalid commit subject(s):" + Environment.NewLine + string.Join(Environment.NewLine, invalid.Select(x => "  - " + x)));
        }

        Log.Information($"Commit messages valid for range {range}.");
    }

    bool DotNetSdkMajorInstalled(int major)
    {
        var result = RunProcess("dotnet", "--list-sdks", "Check SDKs", showSpinner: false, silent: true);
        if (result.ExitCode != 0) return false;

        return result.StdOut
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.TrimStart().StartsWith(major + ".", StringComparison.OrdinalIgnoreCase));
    }

    bool CommandExists(string command)
    {
        var result = RunProcess("where", Quote(command), "Probe command", showSpinner: false, silent: true);
        return result.ExitCode == 0;
    }

    static bool IsValidGameDir(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, "FFX.exe"));

    static string NormalizePathOrEmpty(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path.Trim()); }
        catch { return string.Empty; }
    }

    static bool AskYesNo(string question, bool defaultYes)
    {
        Console.Write($"{question} {(defaultYes ? "[Y/n]" : "[y/N]")}: ");
        var input = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(input)) return defaultYes;
        return input.Equals("y", StringComparison.OrdinalIgnoreCase) || input.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    void RequireGitRepository()
    {
        var result = RunProcess("git", "rev-parse --git-dir", "Check git repo", showSpinner: false, silent: true);
        if (result.ExitCode != 0)
        {
            Fail("This target must run inside a git repository.");
        }
    }

    void RunChecked(string fileName, string args, string description, string? workingDirectory = null, bool showSpinner = false, bool silent = false)
    {
        var result = RunProcess(fileName, args, description, workingDirectory, showSpinner, silent);
        if (result.ExitCode != 0)
        {
            Fail($"{description} failed with code {result.ExitCode}.{Environment.NewLine}Command: {fileName} {args}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StdErr}{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StdOut}");
        }
    }

    ProcessResult RunProcess(
        string fileName,
        string args,
        string description,
        string? workingDirectory = null,
        bool showSpinner = false,
        bool silent = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            WorkingDirectory = workingDirectory ?? RootDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            if (!process.Start()) return new ProcessResult(-1, string.Empty, "Failed to start process.");
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, string.Empty, ex.ToString());
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (showSpinner && !Console.IsOutputRedirected)
        {
            var frames = new[] { '|', '/', '-', '\\' };
            var i = 0;
            while (!process.WaitForExit(120))
            {
                Console.Write($"\r{description} {frames[i++ % frames.Length]}");
            }

            Console.Write("\r");
            Console.Write(new string(' ', Math.Min(Console.WindowWidth > 0 ? Console.WindowWidth - 1 : 80, 120)));
            Console.Write("\r");
        }
        else
        {
            process.WaitForExit();
        }

        process.WaitForExit();

        if (!silent)
        {
            foreach (var line in stdout.ToString().Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)) Log.Information(line);
            foreach (var line in stderr.ToString().Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)) Log.Warning(line);
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    static void CopyDirectoryRecursive(string source, string destination)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
            File.Copy(file, target, overwrite: true);
        }
    }

    static void WriteSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        var hash = sha.ComputeHash(stream);
        var hex = Convert.ToHexString(hash);
        File.WriteAllText(path + ".sha256", $"{hex}  {Path.GetFileName(path)}{Environment.NewLine}");
    }

    static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    static void EnsureDir(string path)
    {
        if (!string.IsNullOrWhiteSpace(path)) Directory.CreateDirectory(path);
    }

    static void RecreateDir(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
    }

    AbsolutePath ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
        return Path.GetFullPath(Path.Combine(RootDirectory, path));
    }

    static string Quote(string value)
    {
        var escaped = value.Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    sealed class LocalConfig
    {
        public string GameDir { get; set; } = string.Empty;
        public string DeployMode { get; set; } = "none";
    }

    readonly record struct SemVersion(int Major, int Minor, int Patch)
    {
        public override string ToString() => $"{Major}.{Minor}.{Patch}";
    }

    readonly record struct CommitWizardResult(string Type, string Scope, string Message, bool Breaking, bool Confirmed);

    readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);
}

