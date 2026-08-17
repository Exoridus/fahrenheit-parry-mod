using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using static Nuke.Common.Assert;

internal sealed partial class BuildScript : NukeBuild
{
    [Parameter(Name = "config-path")] readonly string ConfigPath = string.Empty;
    [Parameter(Name = "configuration")] readonly string ConfigurationOverride = string.Empty;
    [Parameter(Name = "fahrenheit-repo")] readonly string FahrenheitRepo = "https://github.com/peppy-enterprises/fahrenheit.git";
    [Parameter(Name = "fahrenheit-dir")] readonly string FahrenheitDir = ".workspace/fahrenheit";
    [Parameter(Name = "fahrenheit-ref")] readonly string FahrenheitRef = string.Empty;
    [Parameter(Name = "native-msbuild-exe")] readonly string NativeMSBuildExe = string.Empty;
    [Parameter(Name = "toolset")] readonly string Toolset = string.Empty;
    [Parameter(Name = "mod-id")] readonly string ModId = "fhparry";

    [Parameter(Name = "game-dir")] readonly string GameDir = string.Empty;
    [Parameter(Name = "repo")] readonly string Repo = string.Empty;
    [Parameter(Name = "bump")] readonly string Bump = "patch";
    [Parameter(Name = "workflow")] readonly string Workflow = string.Empty;

    [Parameter(Name = "full")] readonly bool Full;
    [Parameter(Name = "log-verbosity")] readonly string LogVerbosity = string.Empty;
    [Parameter(Name = "analysis")] readonly bool CleanAnalysis;
    [Parameter(Name = "exports")] readonly bool CleanExports;
    [Parameter(Name = "game-data")] readonly bool CleanGameData;
    [Parameter(Name = "purge-tools")] readonly bool CleanTools;
    [Parameter(Name = "purge")] readonly bool Purge;
    [Parameter(Name = "yes")] readonly bool Yes;
    [Parameter(Name = "dry-run")] readonly bool DryRun;
    [Parameter(Name = "non-interactive")] readonly bool NonInteractive;
    [Parameter(Name = "elevated")] readonly bool Elevated;
    [Parameter(Name = "auto-deploy")] readonly bool? AutoDeployOverride;
    [Parameter(Name = "refresh-game-dir")] readonly bool RefreshGameDir;

    [Parameter(Name = "type")] readonly string Type = "chore";
    [Parameter(Name = "scope")] readonly string Scope = string.Empty;
    [Parameter(Name = "subject")] readonly string Subject = string.Empty;
    [Parameter(Name = "breaking")] readonly bool Breaking;

    [Parameter(Name = "range")] readonly string Range = string.Empty;
    [Parameter(Name = "commit-file")] readonly string CommitFile = string.Empty;
    [Parameter(Name = "message")] readonly string Message = string.Empty;

    [Parameter(Name = "tag")] readonly string Tag = string.Empty;
    [Parameter(Name = "out")] readonly string Out = ".release/release-notes.txt";
    [Parameter(Name = "deploy-dir")] readonly string DeployDir = ".workspace/fahrenheit/artifacts/deploy/rel";
    [Parameter(Name = "out-dir")] readonly string OutDir = ".release";

    public static int Main() => Execute<BuildScript>(x => x.Help);

    bool InteractiveSession => !NonInteractive && !IsServerBuild && Environment.UserInteractive;
    AbsolutePath WorkspaceDir => RootDirectory / ".workspace";
    AbsolutePath WorkspaceConfigPath => ResolveWorkspaceConfigPath();
    AbsolutePath ReleaseFahrenheitRefPath => RootDirectory / "fahrenheit.release.ref";
    AbsolutePath ManifestPath => RootDirectory / "fhparry.manifest.json";
    static readonly Regex InstallPathNamePattern = new(
        @"^Final Fantasy X[-_ ]X-?2(?:\s*-\s*)?HD Remaster$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex SteamLibraryPathRegex = new(
        "\"path\"\\s*\"(?<path>[^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex SteamLibraryLegacyPathRegex = new(
        "^\\s*\"\\d+\"\\s*\"(?<path>[^\"]+)\"\\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);
    static readonly Regex SteamAppManifestInstallDirRegex = new(
        "\"installdir\"\\s*\"(?<dir>[^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    const string SteamAppIdFfx = "359870";
    // A full deploy mirrors the whole Fahrenheit tree and deletes whatever the source lacks.
    // "state" holds Fahrenheit's runtime state and every mod's persisted settings, so leaving it
    // out silently resets user configuration on each deploy. "logs" is the session log directory.
    static readonly string[] DefaultPreservePaths = ["logs", "mods/loadorder", "saves", "state"];
    bool _isCapturingWorkflowHelp;
    WorkflowHelpBlock? _capturedWorkflowHelp;
    BuildLogVerbosity? _resolvedLogVerbosity;

    string RequestedConfiguration =>
        ConfigurationOverride;

    string RequestedConfigPath =>
        ConfigPath;

    bool CleanToolsRequested => CleanTools;


    new Target Help => _ => _
        .Executes(() =>
        {
            ValidateVerbosityFlags();
            if (string.IsNullOrWhiteSpace(Workflow))
            {
                ShowHelpSummary();
            }
            else
            {
                ShowHelpWorkflow(Workflow);
            }
        });

    Target Cli => _ => _
        .Executes(RunCliWorkflow);

    Target ToolsHelp => _ => _
        .Executes(() =>
        {
            ValidateVerbosityFlags();
            if (string.IsNullOrWhiteSpace(Workflow))
            {
                ShowToolsHelpSummary();
            }
            else
            {
                ShowToolsHelpWorkflow(Workflow);
            }
        });

    Target ToolsCli => _ => _
        .Executes(RunToolsCliWorkflow);

    Target AutoDeploy => _ => _
        .Executes(SetupAutoDeployCore);

    Target DocsSync => _ => _
        .Executes(SyncAutomationDocsCore);

    Target Setup => _ => _.Executes(ExecuteSetupWorkflow);

    Target Clean => _ => _.Executes(ExecuteCleanWorkflow);

    void SetupHooksCore()
    {
        RequireGitRepository();
        if (!File.Exists(RootDirectory / ".githooks" / "commit-msg"))
        {
            Fail("Missing .githooks/commit-msg.");
        }

        RunChecked("git", "config --local core.hooksPath .githooks", "Setup hooks");
    }

    Target Verify => _ => _.Executes(ExecuteVerifyWorkflow);

    Target Build => _ => _.Executes(ExecuteBuildWorkflow);

    Target Deploy => _ => _.Executes(ExecuteDeployWorkflow);
    Target Start => _ => _.Executes(ExecuteStartWorkflow);

    Target ReleaseNotes => _ => _.Executes(() =>
    {
        if (string.IsNullOrWhiteSpace(Tag))
        {
            Fail("Missing --tag.");
        }

        var repoSlug = ResolveRepositorySlug(Repo);
        if (string.IsNullOrWhiteSpace(repoSlug))
        {
            Fail("Missing --repo owner/repo.");
        }

        GenerateReleaseNotesCore(
            tag: Tag,
            repositorySlug: repoSlug,
            outputPath: ResolvePath(Out));
    });

    Target ReleasePack => _ => _.Executes(() =>
    {
        if (string.IsNullOrWhiteSpace(Tag))
        {
            Fail("Missing --tag.");
        }

        PackageReleaseCore(Tag, ResolvePath(DeployDir), ResolvePath(OutDir), ModId);
    });

    Target ReleaseReady => _ => _.Executes(ReleaseReadyCore);

    Target ReleaseBump => _ => _.Executes(ReleaseVersionCore);

    Target Commit => _ => _.Executes(() =>
    {
        var requestedMessage = Subject;
        var requestedType = Type;
        var requestedScope = Scope;
        var requestedBreaking = Breaking;

        if (string.IsNullOrWhiteSpace(requestedMessage))
        {
            if (!InteractiveSession)
            {
                Fail("Missing --subject. In interactive mode, you can run build.cmd commit without arguments.");
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

    Target CommitCheck => _ => _.Executes(ExecuteCommitCheckWorkflow);


    void RunCliWorkflow()
    {
        ValidateVerbosityFlags();

        var workflow = (Workflow ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(workflow))
        {
            ShowHelpSummary();
            return;
        }

        if (workflow.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            ShowHelpSummary();
            return;
        }

        if (workflow.Equals("install", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("Workflow 'install' was removed. Redirecting to 'build.cmd setup'.");
            ExecuteSetupWorkflow();
            return;
        }

        var definition = TryGetBuildWorkflow(workflow);
        if (definition is not null)
        {
            definition.Execute();
            return;
        }

        if (IsToolWorkflow(workflow))
        {
            FailWorkflowMovedToTools(workflow);
            return;
        }

        Fail($"Unknown workflow '{workflow}'. Use: build.cmd help");
    }
    void ShowHelpSummary()
    {
        WriteHelpLine("Usage: build.cmd <workflow> [options]");
        WriteHelpLine("Detailed help: build.cmd -h <workflow>");
        WriteHelpLine("Bool options: --flag (true), --no-flag (false)");
        WriteHelpLine("Global verbosity: --verbosity|-v quiet|minimal|normal|detailed|diagnostic (default: normal)");
        WriteHelpLine("Recommended escalation: quiet -> normal -> detailed -> diagnostic");
        WriteHelpLine("Global config path: --config-path (shorthand: -c)");
        WriteHelpLine("Common shorthand: -c <config-path>, -n (--dry-run), -h (help)");
        WriteHelpLine("Agent guidance: use --verbosity quiet for routine verify/build/deploy runs.");

        foreach (var section in BuildWorkflowSections)
        {
            WriteHelpLine(string.Empty);
            WriteHelpLine($"{section.Heading}:");
            foreach (var workflow in section.Workflows)
            {
                WriteHelpLine($"  {workflow.Name,-14} {workflow.Summary}");
            }
        }

        WriteHelpLine(string.Empty);
        WriteHelpLine("  tools.cmd    Local-only tooling workflows (data/discord/ghidra moved to ffx-forensics-pipeline)");
        WriteHelpLine("  Use: build.cmd -h <workflow> for detailed parameters and examples.");
    }
    void ShowHelpWorkflow(string workflowRaw)
    {
        var workflow = (workflowRaw ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(workflow))
        {
            ShowHelpSummary();
            return;
        }

        if (TryGetBuildWorkflow(workflow) is { } definition)
        {
            PrintHelpBlock(
                definition.Name,
                definition.Purpose,
                definition.Parameters,
                definition.Examples);
            return;
        }

        if (workflow.Equals("install", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("Workflow 'install' was removed. Use: build.cmd setup");
            if (TryGetBuildWorkflow("setup") is { } setupDefinition)
            {
                PrintHelpBlock(
                    setupDefinition.Name,
                    setupDefinition.Purpose,
                    setupDefinition.Parameters,
                    setupDefinition.Examples);
                return;
            }
        }

        if (IsToolWorkflow(workflow))
        {
            FailWorkflowMovedToTools(workflow);
            return;
        }

        Log.Warning($"Unknown workflow: {workflowRaw}");
        ShowHelpSummary();
    }
    WorkflowHelpBlock CaptureWorkflowHelpBlock(string workflowRaw)
    {
        var normalizedWorkflow = (workflowRaw ?? string.Empty).Trim().ToLowerInvariant();
        _isCapturingWorkflowHelp = true;
        _capturedWorkflowHelp = null;
        try
        {
            ShowHelpWorkflow(normalizedWorkflow);
            return _capturedWorkflowHelp ?? new WorkflowHelpBlock(
                normalizedWorkflow,
                "Unknown workflow.",
                [],
                []);
        }
        finally
        {
            _isCapturingWorkflowHelp = false;
            _capturedWorkflowHelp = null;
        }
    }

    void PrintHelpBlock(string name, string purpose, IEnumerable<string> parameters, IEnumerable<string> examples)
    {
        var parameterList = parameters?.ToList() ?? [];
        var exampleList = examples?.ToList() ?? [];

        _capturedWorkflowHelp = new WorkflowHelpBlock(
            Workflow: name,
            Purpose: purpose,
            Parameters: parameterList,
            Examples: exampleList);

        if (_isCapturingWorkflowHelp)
        {
            return;
        }

        WriteHelpLine($"Workflow: {name}");
        WriteHelpLine($"Purpose: {purpose}");
        WriteHelpLine("Global options:");
        WriteHelpLine("  - --verbosity|-v quiet|minimal|normal|detailed|diagnostic (default: normal)");
        WriteHelpLine("  - --config-path <path> (shorthand: -c)");
        WriteHelpLine("  - -n (--dry-run), -h (help)");
        WriteHelpLine("Parameters:");
        foreach (var line in parameterList)
        {
            WriteHelpLine($"  - {line}");
        }

        WriteHelpLine("Examples:");
        foreach (var line in exampleList)
        {
            WriteHelpLine($"  {line}");
        }
    }

    void BuildCore(string target, string configuration, bool useReleaseRef, bool allowAutoDeploy = true)
    {
        var resolvedConfiguration = ResolveBuildConfiguration(configuration);
        var effectiveFahrenheitRef = ResolveFahrenheitRef(useReleaseRef);
        var t = target.Trim().ToLowerInvariant();
        if (t == "mod")
        {
            RunBuildProjTarget("BuildModOnly", resolvedConfiguration, includeNativeMsbuild: false, fahrenheitRef: effectiveFahrenheitRef);
        }
        else if (t == "full")
        {
            RunBuildProjTarget("Build", resolvedConfiguration, includeNativeMsbuild: true, fahrenheitRef: effectiveFahrenheitRef);
        }
        else
        {
            Fail($"Invalid build target '{target}'. Use mod or full.");
        }

        if (allowAutoDeploy)
        {
            TryAutoDeployAfterBuild(t, resolvedConfiguration, useReleaseRef);
        }
    }

    void StartCore()
    {
        EnsureStageRuntimePrerequisites();

        var gameDir = ResolveGameDir(promptIfMissing: true, persist: false);
        var binDir = Path.Combine(gameDir, "fahrenheit", "bin");
        var stage0Exe = Path.Combine(binDir, "fhstage0.exe");
        if (!File.Exists(stage0Exe))
        {
            Fail($"Missing stage0 loader: {stage0Exe}{Environment.NewLine}Run build+deploy first.");
        }

        Log.Information($"Launching Fahrenheit loader from: {binDir}{(Elevated ? " (elevated)" : string.Empty)}");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = stage0Exe,
                Arguments = "..\\..\\FFX.exe",
                WorkingDirectory = binDir,
                UseShellExecute = true,
                Verb = Elevated ? "runas" : "open"
            };

            var process = Process.Start(psi);
            if (process == null)
            {
                Fail("Failed to start fhstage0.exe.");
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Fail("Start canceled by user (UAC prompt declined).");
        }
        catch (Exception ex)
        {
            Fail($"Failed to start fhstage0.exe: {ex.Message}");
        }
    }

    void EnsureStageRuntimePrerequisites()
    {
        if (HasDotNetRuntime10HostFxr())
        {
            return;
        }

        Fail(
            "Missing Microsoft .NET Runtime 10, required by Fahrenheit stage1 loader."
            + Environment.NewLine
            + "Install with:"
            + Environment.NewLine
            + "  winget install --id Microsoft.DotNet.Runtime.10 --exact --accept-package-agreements --accept-source-agreements");
    }

    static bool HasDotNetRuntime10HostFxr()
    {
        return EnumerateHostFxrCandidates(major: 10).Any();
    }

    static IEnumerable<string> EnumerateHostFxrCandidates(int major)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        }
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            var fxrRoot = Path.Combine(root, "dotnet", "host", "fxr");
            if (!Directory.Exists(fxrRoot))
            {
                continue;
            }

            foreach (var dir in Directory.EnumerateDirectories(fxrRoot))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(major + ".", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var dll = Path.Combine(dir, "hostfxr.dll");
                if (File.Exists(dll))
                {
                    yield return dll;
                }
            }
        }
    }


    AbsolutePath ResolveWorkspaceConfigPath()
    {
        var fromArg = (RequestedConfigPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(fromArg))
        {
            return WorkspaceDir / "config.local.json";
        }

        return ResolvePath(fromArg);
    }

    string ResolveBuildConfiguration(string configuration)
    {
        var fromArg = (configuration ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(fromArg))
        {
            if (fromArg.Equals("Debug", StringComparison.OrdinalIgnoreCase))
            {
                return "Debug";
            }

            if (fromArg.Equals("Release", StringComparison.OrdinalIgnoreCase))
            {
                return "Release";
            }

            Fail($"Invalid --configuration value '{configuration}'. Use Debug or Release.");
        }

        var cfg = LoadWorkspaceConfig();
        var fromConfig = (cfg.Configuration ?? string.Empty).Trim();
        if (fromConfig.Equals("Debug", StringComparison.OrdinalIgnoreCase))
        {
            return "Debug";
        }

        if (fromConfig.Equals("Release", StringComparison.OrdinalIgnoreCase))
        {
            return "Release";
        }

        Fail($"Invalid Configuration '{cfg.Configuration}' in '{WorkspaceConfigPath}'. Use Debug or Release.");
        return string.Empty;
    }

    void RunBuildProjTarget(string target, string configuration, bool includeNativeMsbuild, string fahrenheitRef)
    {
        var resolvedConfiguration = ResolveBuildConfiguration(configuration);
        var args = new StringBuilder();
        args.Append("msbuild ");
        args.Append(Quote(RootDirectory / "build.proj"));
        args.Append($" -nologo -verbosity:{ResolveMsBuildVerbosity()}");
        args.Append($" -t:{target}");
        args.Append($" -p:Configuration={Quote(resolvedConfiguration)}");
        args.Append($" -p:FahrenheitRepo={Quote(FahrenheitRepo)}");
        args.Append($" -p:FahrenheitDir={Quote(ResolvePath(FahrenheitDir))}");
        args.Append($" -p:FahrenheitRef={Quote(fahrenheitRef)}");

        if (includeNativeMsbuild && !string.IsNullOrWhiteSpace(NativeMSBuildExe))
        {
            args.Append($" -p:NativeMSBuildExe={Quote(NativeMSBuildExe)}");
        }

        if (includeNativeMsbuild)
        {
            var resolvedToolset = ResolveNativePlatformToolset();
            if (!string.IsNullOrWhiteSpace(resolvedToolset))
            {
                args.Append($" -p:NativePlatformToolset={Quote(resolvedToolset)}");
            }
        }

        RunChecked("dotnet", args.ToString(), $"MSBuild target {target}");
    }

    string ResolveNativePlatformToolset()
    {
        if (!OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(Toolset))
        {
            return Toolset.Trim();
        }

        var vswhere = ResolveVsWherePath();
        if (string.IsNullOrWhiteSpace(vswhere) || !File.Exists(vswhere))
        {
            return string.Empty;
        }

        var probe = RunProcess(
            vswhere,
            "-latest -products * -requires Microsoft.Component.MSBuild -property installationPath",
            "Resolve Visual Studio installation path",
            showSpinner: false,
            silent: true);

        if (probe.ExitCode != 0)
        {
            return string.Empty;
        }

        var installPath = probe.StdOut
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
        {
            return string.Empty;
        }

        var vcRoot = Path.Combine(installPath, "MSBuild", "Microsoft", "VC");
        if (!Directory.Exists(vcRoot))
        {
            return string.Empty;
        }

        var candidates = new[] { "v145", "v144", "v143", "v142" };
        var vcVersions = Directory.EnumerateDirectories(vcRoot, "v*")
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var vcVersion in vcVersions)
        {
            var platformToolsetsRoot = Path.Combine(vcVersion, "Platforms", "Win32", "PlatformToolsets");
            if (!Directory.Exists(platformToolsetsRoot))
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                var propsPath = Path.Combine(platformToolsetsRoot, candidate, "Toolset.props");
                if (File.Exists(propsPath))
                {
                    Log.Information($"Using native platform toolset override: {candidate}");
                    return candidate;
                }
            }
        }

        return string.Empty;
    }

    static string ResolveVsWherePath()
    {
        var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (string.IsNullOrWhiteSpace(programFilesX86))
        {
            return string.Empty;
        }

        return Path.Combine(programFilesX86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
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
                Fail($"Missing release ref file: {ReleaseFahrenheitRefPath}. Run build.cmd release-bump first.");
            }

            var pinned = File.ReadAllText(ReleaseFahrenheitRefPath).Trim();
            if (string.IsNullOrWhiteSpace(pinned))
            {
                Fail($"Release ref file is empty: {ReleaseFahrenheitRefPath}");
            }

            return pinned;
        }

        // Default builds pin to the committed ref (fahrenheit.release.ref) so a plain
        // `build.cmd build` is deterministic and does NOT silently drift to origin/main
        // (currently the in-flight alpha11 line). Explicit --fahrenheit-ref still overrides;
        // origin/main remains the fallback only if the pin file is missing/empty.
        if (File.Exists(ReleaseFahrenheitRefPath))
        {
            var committed = File.ReadAllText(ReleaseFahrenheitRefPath).Trim();
            if (!string.IsNullOrWhiteSpace(committed))
            {
                return committed;
            }
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

        var repoSlug = ResolveRepositorySlug(Repo);
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
        var commitResult = RunProcess("git", $"-c core.hooksPath=NUL commit -m {Quote(commitMessage)}", "Create release commit", silent: true);
        if (commitResult.ExitCode != 0)
        {
            RunChecked("git", $"-c core.hooksPath=NUL commit --allow-empty -m {Quote(commitMessage)}", "Create empty release commit");
        }

        RunChecked("git", $"tag -a {Quote(newTag)} -m {Quote(commitMessage)}", "Create release tag");
        Log.Information($"Created release commit and tag: {newTag}");
        Log.Information("Next step: git push origin main --follow-tags");
    }

    void PinReleaseFahrenheitRef()
    {
        // Honor a deliberately committed pin: if the release ref file already holds a
        // concrete 40-char commit SHA, keep it. Only auto-resolve Fahrenheit main HEAD
        // when the pin is missing, empty, or symbolic (e.g. a branch name). Without this,
        // release-bump silently drags every release onto Fahrenheit main HEAD.
        if (File.Exists(ReleaseFahrenheitRefPath))
        {
            var existing = File.ReadAllText(ReleaseFahrenheitRefPath).Trim();
            if (existing.Length == 40 && existing.All(Uri.IsHexDigit))
            {
                Log.Information($"Keeping committed release Fahrenheit ref: {existing}");
                return;
            }
        }

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
        content.AppendLine("Prerequisite: Microsoft .NET Runtime 10 (x86 recommended).");
        content.AppendLine("If missing, `fahrenheit/start-fahrenheit.cmd` will prompt to install it via winget.");
        content.AppendLine();
        content.AppendLine("1. Download one of the ZIP packages above.");
        content.AppendLine("2. Extract into your game directory (folder containing `FFX.exe`).");
        content.AppendLine("3. For the full package, start via `fahrenheit/start-fahrenheit.cmd` (recommended).");
        content.AppendLine("4. Alternatively run `fahrenheit\\bin\\fhstage0.exe ..\\..\\FFX.exe` from `fahrenheit\\bin`.");
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

        EnsureWingetAvailable();
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

        EnsureWingetAvailable();
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

        // MSBuild.exe is only on PATH inside a Developer Command Prompt. A regular
        // Visual Studio install still ships it, and that is what build.proj's
        // ResolveNativeMSBuild target uses, so discover it the same way before
        // concluding anything is missing.
        var msbuildExe = ResolveMsbuildExePath();
        if (!string.IsNullOrWhiteSpace(msbuildExe))
        {
            Log.Information($"[OK] MSBuild is already available: {msbuildExe}");
            return;
        }

        EnsureWingetAvailable();
        PromptInstallOrFail(
            title: "MSBuild not found.",
            detail: "Full builds require Visual Studio Build Tools with C++ and .NET desktop workloads.",
            adminRequired: true);

        InstallWingetPackage(
            packageId: "Microsoft.VisualStudio.2022.BuildTools",
            label: "Visual Studio Build Tools",
            overrideArgs: "--wait --quiet --norestart --nocache --add Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools --add Microsoft.VisualStudio.Workload.VCTools");

        if (!CommandExists("msbuild") && string.IsNullOrWhiteSpace(ResolveMsbuildExePath()))
        {
            Log.Warning("MSBuild may need a new terminal session to appear on PATH.");
        }
    }

    string ResolveMsbuildExePath()
    {
        var vswhere = ResolveVsWherePath();
        if (string.IsNullOrWhiteSpace(vswhere) || !File.Exists(vswhere))
        {
            return string.Empty;
        }

        var probe = RunProcess(
            vswhere,
            "-latest -products * -requires Microsoft.Component.MSBuild -property installationPath",
            "Resolve MSBuild installation path",
            showSpinner: false,
            silent: true);

        if (probe.ExitCode != 0)
        {
            return string.Empty;
        }

        var installPath = probe.StdOut
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(installPath))
        {
            return string.Empty;
        }

        var msbuildExe = Path.Combine(installPath, "MSBuild", "Current", "Bin", "MSBuild.exe");
        return File.Exists(msbuildExe) ? msbuildExe : string.Empty;
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

    void TryAutoDeployAfterBuild(string buildTarget, string configuration, bool useReleaseRef)
    {
        if (useReleaseRef || IsServerBuild)
        {
            return;
        }

        var cfg = LoadWorkspaceConfig();
        if (AutoDeployOverride.HasValue && !AutoDeployOverride.Value)
        {
            Log.Information("Automatic deployment was disabled by --no-auto-deploy.");
            return;
        }

        bool autoDeployEnabled;
        if (AutoDeployOverride.HasValue)
        {
            autoDeployEnabled = AutoDeployOverride.Value;
        }
        else if (cfg.AutoDeploy.HasValue)
        {
            autoDeployEnabled = cfg.AutoDeploy.Value;
        }
        else if (InteractiveSession)
        {
            autoDeployEnabled = AskYesNo("Enable automatic deployment after build?", defaultYes: true);
            cfg.AutoDeploy = autoDeployEnabled;
            SaveWorkspaceConfig(cfg);
        }
        else
        {
            Log.Information("AutoDeploy is not configured (null) in non-interactive mode. Skipping automatic deploy.");
            return;
        }

        if (!autoDeployEnabled)
        {
            return;
        }

        var gameDir = NormalizePathOrEmpty(GameDir);
        if (!string.IsNullOrWhiteSpace(GameDir) && !IsValidGameDir(gameDir))
        {
            Log.Warning($"Invalid --game-dir value '{GameDir}' (FFX.exe not found). Skipping automatic deploy.");
            return;
        }

        if (!IsValidGameDir(gameDir) && !RefreshGameDir)
        {
            gameDir = NormalizePathOrEmpty(cfg.GameDir);
        }

        if (!IsValidGameDir(gameDir))
        {
            gameDir = DetectGameDir();
        }

        if (!IsValidGameDir(gameDir) && InteractiveSession)
        {
            Console.Write("Enter game installation directory for auto-deploy (must contain FFX.exe): ");
            var manual = NormalizePathOrEmpty(Console.ReadLine());
            if (IsValidGameDir(manual))
            {
                gameDir = manual;
            }
        }

        if (!IsValidGameDir(gameDir))
        {
            Log.Warning("Automatic deploy is enabled but no valid GameDir could be resolved. Skipping automatic deploy.");
            Log.Information("Run: build.cmd auto-deploy --game-dir <path>");
            return;
        }

        var normalizedGameDir = NormalizePathOrEmpty(gameDir);
        if (!normalizedGameDir.Equals(NormalizePathOrEmpty(cfg.GameDir), StringComparison.OrdinalIgnoreCase))
        {
            cfg.GameDir = normalizedGameDir;
            SaveWorkspaceConfig(cfg);
        }

        var normalizedBuildTarget = (buildTarget ?? string.Empty).Trim().ToLowerInvariant();
        var deployTarget = normalizedBuildTarget switch
        {
            "full" => "full",
            "mod" => "mod",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(deployTarget))
        {
            Log.Warning($"Unknown build target '{buildTarget}'. Skipping automatic deploy.");
            return;
        }

        var ok = DeployFromArtifacts(normalizedGameDir, configuration, deployTarget, failOnError: false, reason: "Automatic deploy");
        if (!ok)
        {
            Log.Warning("Automatic deploy failed and was skipped.");
        }
    }

    bool DeployFromArtifacts(string gameDir, string configuration, string target, bool failOnError, string reason)
    {
        var normalizedTarget = target.Trim().ToLowerInvariant();
        if (normalizedTarget != "mod" && normalizedTarget != "full")
        {
            var msg = $"Invalid deploy target '{target}'. Use mod or full.";
            if (failOnError) Fail(msg);
            Log.Warning(msg);
            return false;
        }

        var normalizedGameDir = NormalizePathOrEmpty(gameDir);
        if (!IsValidGameDir(normalizedGameDir))
        {
            var msg = $"Invalid GameDir '{gameDir}' (FFX.exe not found).";
            if (failOnError) Fail(msg);
            Log.Warning(msg);
            return false;
        }

        var resolvedConfiguration = ResolveBuildConfiguration(configuration);
        var deployCfg = resolvedConfiguration.Equals("Release", StringComparison.OrdinalIgnoreCase) ? "rel" : "dbg";
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

        var cfg = LoadWorkspaceConfig();
        var preservePaths = cfg.DeployPreservePaths ?? [];

        try
        {
            SyncDirectoryRecursiveWithPreservePaths(sourcePath, destinationPath, targetRoot, preservePaths);
            if (DryRun)
            {
                Log.Information($"{reason}: [DRY-RUN] simulated deploy {normalizedTarget} to {destinationPath}");
            }
            else
            {
                Log.Information($"{reason}: deployed {normalizedTarget} to {destinationPath}");
                CleanupReleaseDirAfterDeploy();
            }
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

    string ResolveGameDir(bool promptIfMissing, bool persist)
    {
        var cfg = LoadWorkspaceConfig();

        var fromArg = NormalizePathOrEmpty(GameDir);
        if (!string.IsNullOrWhiteSpace(GameDir) && !IsValidGameDir(fromArg))
        {
            Fail($"Invalid --game-dir value '{GameDir}' (FFX.exe not found).");
        }

        if (IsValidGameDir(fromArg))
        {
            if (persist)
            {
                cfg.GameDir = fromArg;
                SaveWorkspaceConfig(cfg);
            }

            return fromArg;
        }

        if (!RefreshGameDir)
        {
            var fromConfig = NormalizePathOrEmpty(cfg.GameDir);
            if (IsValidGameDir(fromConfig))
            {
                return fromConfig;
            }
        }

        var detected = DetectGameDir();
        if (IsValidGameDir(detected))
        {
            Log.Information($"Auto-detected game directory: {detected}");
            if (persist)
            {
                cfg.GameDir = detected;
                SaveWorkspaceConfig(cfg);
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
                        SaveWorkspaceConfig(cfg);
                    }

                    return input;
                }

                Log.Warning($"Invalid path: {input}");
            }
        }

        Fail("Could not resolve GameDir. Pass --game-dir or run build.cmd auto-deploy.");
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
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in DiscoverGameDirCandidates())
        {
            var normalized = NormalizePathOrEmpty(candidate);
            if (string.IsNullOrWhiteSpace(normalized)) continue;
            if (seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    IEnumerable<string> DiscoverGameDirCandidates()
    {
        foreach (var candidate in DiscoverGameDirCandidatesFromSteamAppManifest())
        {
            yield return candidate;
        }

        foreach (var candidate in DiscoverGameDirCandidatesFromGamesFallbackByPattern())
        {
            yield return candidate;
        }
    }

    IEnumerable<string> DiscoverGameDirCandidatesFromSteamAppManifest()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var libraryRoot in DiscoverSteamLibraryRoots())
        {
            var steamApps = Path.Combine(libraryRoot, "steamapps");
            var appManifestPath = Path.Combine(steamApps, $"appmanifest_{SteamAppIdFfx}.acf");
            if (!File.Exists(appManifestPath))
            {
                continue;
            }

            var installDir = TryReadInstallDirFromAppManifest(appManifestPath);
            if (string.IsNullOrWhiteSpace(installDir))
            {
                continue;
            }

            var candidate = NormalizePathOrEmpty(Path.Combine(steamApps, "common", installDir));
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    IEnumerable<string> DiscoverGameDirCandidatesFromGamesFallbackByPattern()
    {
        var gamesRoot = @"X:\Games";
        if (!Directory.Exists(gamesRoot))
        {
            yield break;
        }

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(gamesRoot, "*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            yield break;
        }

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!InstallPathNamePattern.IsMatch(name)) continue;
            yield return dir;
        }
    }

    IEnumerable<string> DiscoverSteamLibraryRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var installRoot in DiscoverSteamInstallRoots())
        {
            if (seen.Add(installRoot))
            {
                yield return installRoot;
            }

            foreach (var libraryRoot in DiscoverSteamLibraryRootsFromLibraryFolders(installRoot))
            {
                if (seen.Add(libraryRoot))
                {
                    yield return libraryRoot;
                }
            }
        }
    }

    IEnumerable<string> DiscoverSteamInstallRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in DiscoverSteamInstallRootsFromRegistry())
        {
            var normalized = NormalizePathOrEmpty(root);
            if (!string.IsNullOrWhiteSpace(normalized) && Directory.Exists(normalized) && seen.Add(normalized))
            {
                yield return normalized;
            }
        }

        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        foreach (var fallback in new[]
                 {
                     string.IsNullOrWhiteSpace(pf86) ? string.Empty : Path.Combine(pf86, "Steam"),
                     string.IsNullOrWhiteSpace(pf) ? string.Empty : Path.Combine(pf, "Steam")
                 })
        {
            var normalized = NormalizePathOrEmpty(fallback);
            if (!string.IsNullOrWhiteSpace(normalized) && Directory.Exists(normalized) && seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    IEnumerable<string> DiscoverSteamInstallRootsFromRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        var keyPaths = new[]
        {
            @"SOFTWARE\Valve\Steam",
            @"SOFTWARE\WOW6432Node\Valve\Steam"
        };
        var valueNames = new[] { "InstallPath", "SteamPath" };

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                RegistryKey? baseKey = null;
                try
                {
                    baseKey = RegistryKey.OpenBaseKey(hive, view);
                }
                catch
                {
                    // Ignore unavailable registry views/hives.
                }

                if (baseKey is null)
                {
                    continue;
                }

                using (baseKey)
                {
                    foreach (var keyPath in keyPaths)
                    {
                        using var steamKey = baseKey.OpenSubKey(keyPath);
                        if (steamKey is null) continue;

                        foreach (var valueName in valueNames)
                        {
                            var raw = steamKey.GetValue(valueName) as string;
                            var normalized = NormalizePathOrEmpty(raw);
                            if (!string.IsNullOrWhiteSpace(normalized))
                            {
                                yield return normalized;
                            }
                        }
                    }
                }
            }
        }
    }

    IEnumerable<string> DiscoverSteamLibraryRootsFromLibraryFolders(string steamRoot)
    {
        var libraryFoldersPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(libraryFoldersPath))
        {
            yield break;
        }

        string text;
        try
        {
            text = File.ReadAllText(libraryFoldersPath);
        }
        catch
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in SteamLibraryPathRegex.Matches(text))
        {
            var rawPath = match.Groups["path"].Value;
            var normalizedLibraryRoot = NormalizeSteamLibraryPath(rawPath);
            if (string.IsNullOrWhiteSpace(normalizedLibraryRoot) || !Directory.Exists(normalizedLibraryRoot)) continue;
            if (seen.Add(normalizedLibraryRoot))
            {
                yield return normalizedLibraryRoot;
            }
        }

        foreach (Match match in SteamLibraryLegacyPathRegex.Matches(text))
        {
            var rawPath = match.Groups["path"].Value;
            var normalizedLibraryRoot = NormalizeSteamLibraryPath(rawPath);
            if (string.IsNullOrWhiteSpace(normalizedLibraryRoot) || !Directory.Exists(normalizedLibraryRoot)) continue;
            if (seen.Add(normalizedLibraryRoot))
            {
                yield return normalizedLibraryRoot;
            }
        }
    }

    string TryReadInstallDirFromAppManifest(string appManifestPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(appManifestPath);
        }
        catch
        {
            return string.Empty;
        }

        var match = SteamAppManifestInstallDirRegex.Match(text);
        if (!match.Success)
        {
            return string.Empty;
        }

        var installDir = match.Groups["dir"].Value
            .Trim()
            .Replace(@"\\", @"\")
            .Replace('/', '\\')
            .Trim('\\');

        return string.IsNullOrWhiteSpace(installDir) ? string.Empty : installDir;
    }

    static string NormalizeSteamLibraryPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return string.Empty;
        }

        var unescaped = rawPath
            .Trim()
            .Replace(@"\\", @"\")
            .Replace('/', '\\');

        return NormalizePathOrEmpty(unescaped);
    }

    WorkspaceConfig LoadWorkspaceConfig()
    {
        EnsureDir(Path.GetDirectoryName(WorkspaceConfigPath) ?? string.Empty);

        if (!File.Exists(WorkspaceConfigPath))
        {
            var defaults = CreateDefaultWorkspaceConfig();
            SaveWorkspaceConfig(defaults);
            Log.Information($"Created default workspace config: {WorkspaceConfigPath}");
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(WorkspaceConfigPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            ValidateWorkspaceConfigSchema(root);

            var cfg = ParseWorkspaceConfig(root);
            cfg = NormalizeWorkspaceConfig(cfg);
            SaveWorkspaceConfig(cfg);
            return cfg;
        }
        catch (Exception ex)
        {
            Fail($"Invalid workspace config '{WorkspaceConfigPath}': {ex.Message}");
            return CreateDefaultWorkspaceConfig();
        }
    }

    void SaveWorkspaceConfig(WorkspaceConfig cfg)
    {
        EnsureDir(Path.GetDirectoryName(WorkspaceConfigPath) ?? string.Empty);
        var normalized = NormalizeWorkspaceConfig(cfg);
        var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(WorkspaceConfigPath, json + Environment.NewLine);
    }

    WorkspaceConfig CreateDefaultWorkspaceConfig()
    {
        return new WorkspaceConfig
        {
            Configuration = "Release",
            GameDir = string.Empty,
            AutoDeploy = null,
            DeployPreservePaths = CreateDefaultPreservePaths(),
            VisionApiUrl = string.Empty,
            VisionApiKey = string.Empty,
            VisionModel = string.Empty,
            FetchRetries = 2,
            DiscordToken = string.Empty
        };
    }

    static List<string> CreateDefaultPreservePaths() => DefaultPreservePaths.ToList();

    WorkspaceConfig ParseWorkspaceConfig(JsonElement root)
    {
        var defaults = CreateDefaultWorkspaceConfig();
        var deployPreservePaths = ReadStringArrayWithLegacy(
            root,
            canonicalKey: "DeployPreservePaths",
            legacyKeys: ["PreservePaths"],
            fallback: defaults.DeployPreservePaths ?? CreateDefaultPreservePaths());

        return new WorkspaceConfig
        {
            Configuration = ReadStringWithLegacy(root, "Configuration", ["BuildTarget"], defaults.Configuration),
            GameDir = ReadStringWithLegacy(root, "GameDir", ["InstallPath"], defaults.GameDir),
            AutoDeploy = ReadNullableBool(root, "AutoDeploy", defaults.AutoDeploy),
            DeployPreservePaths = deployPreservePaths,
            VisionApiUrl = ReadStringWithLegacy(root, "VisionApiUrl", ["OpenApiUrl"], defaults.VisionApiUrl),
            VisionApiKey = ReadStringWithLegacy(root, "VisionApiKey", ["OpenApiKey"], defaults.VisionApiKey),
            VisionModel = ReadStringWithLegacy(root, "VisionModel", ["OpenApiModel"], defaults.VisionModel),
            FetchRetries = ReadIntWithLegacy(root, "FetchRetries", ["FetchRetryCount"], defaults.FetchRetries),
            DiscordToken = ReadStringWithLegacy(root, "DiscordToken", [], defaults.DiscordToken)
        };
    }

    void ValidateWorkspaceConfigSchema(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            Fail($"Workspace config '{WorkspaceConfigPath}' must be a JSON object.");
        }

        var allowedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "Configuration",
            "BuildTarget",
            "GameDir",
            "InstallPath",
            "AutoDeploy",
            "DeployPreservePaths",
            "PreservePaths",
            "VisionApiUrl",
            "VisionApiKey",
            "VisionModel",
            "OpenApiUrl",
            "OpenApiKey",
            "OpenApiModel",
            "FetchRetries",
            "FetchRetryCount",
            "DiscordToken"
        };

        foreach (var property in root.EnumerateObject())
        {
            if (!allowedKeys.Contains(property.Name))
            {
                Fail($"Unsupported workspace config key '{property.Name}' in '{WorkspaceConfigPath}'.");
            }
        }

        ValidateStringPropertyIfPresent(root, "Configuration");
        ValidateStringPropertyIfPresent(root, "BuildTarget");
        ValidateStringPropertyIfPresent(root, "GameDir");
        ValidateStringPropertyIfPresent(root, "InstallPath");

        if (root.TryGetProperty("AutoDeploy", out var autoDeployElement)
            && (autoDeployElement.ValueKind is not JsonValueKind.True
                && autoDeployElement.ValueKind is not JsonValueKind.False
                && autoDeployElement.ValueKind is not JsonValueKind.Null))
        {
            Fail($"Workspace config '{WorkspaceConfigPath}' must contain 'AutoDeploy' with value true, false, or null.");
        }

        ValidateStringArrayPropertyIfPresent(root, "DeployPreservePaths");
        ValidateStringArrayPropertyIfPresent(root, "PreservePaths");
        ValidateStringPropertyIfPresent(root, "VisionApiUrl");
        ValidateStringPropertyIfPresent(root, "VisionApiKey");
        ValidateStringPropertyIfPresent(root, "VisionModel");
        ValidateStringPropertyIfPresent(root, "OpenApiUrl");
        ValidateStringPropertyIfPresent(root, "OpenApiKey");
        ValidateStringPropertyIfPresent(root, "OpenApiModel");
        ValidateIntegerPropertyIfPresent(root, "FetchRetries");
        ValidateIntegerPropertyIfPresent(root, "FetchRetryCount");
        ValidateStringPropertyIfPresent(root, "DiscordToken");
    }

    WorkspaceConfig NormalizeWorkspaceConfig(WorkspaceConfig cfg)
    {
        var normalized = cfg ?? CreateDefaultWorkspaceConfig();

        normalized.Configuration = NormalizeConfiguration(normalized.Configuration);
        normalized.GameDir = NormalizePathOrEmpty(normalized.GameDir);
        normalized.DeployPreservePaths ??= CreateDefaultPreservePaths();
        normalized.DeployPreservePaths = normalized.DeployPreservePaths
            .Select(NormalizePreservePathEntry)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalized.DeployPreservePaths.Count == 0)
        {
            normalized.DeployPreservePaths = CreateDefaultPreservePaths();
        }

        normalized.VisionApiUrl = NormalizeVisionApiUrl(normalized.VisionApiUrl);
        normalized.VisionApiKey = (normalized.VisionApiKey ?? string.Empty).Trim();
        normalized.VisionModel = (normalized.VisionModel ?? string.Empty).Trim();
        normalized.FetchRetries = Math.Max(0, normalized.FetchRetries);
        normalized.DiscordToken = (normalized.DiscordToken ?? string.Empty).Trim();

        return normalized;
    }

    static string NormalizeConfiguration(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Equals("Debug", StringComparison.OrdinalIgnoreCase))
        {
            return "Debug";
        }

        if (trimmed.Equals("Release", StringComparison.OrdinalIgnoreCase))
        {
            return "Release";
        }

        Fail($"Invalid Configuration '{value}'. Use Debug or Release.");
        return string.Empty;
    }

    static string NormalizeVisionApiUrl(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Fail($"Invalid VisionApiUrl '{value}'. Use an absolute http(s) URL.");
        }

        return trimmed.TrimEnd('/');
    }

    static bool TryGetPropertyWithLegacy(JsonElement root, string canonicalKey, IReadOnlyList<string> legacyKeys, out JsonElement value)
    {
        if (root.TryGetProperty(canonicalKey, out value))
        {
            return true;
        }

        for (var i = 0; i < legacyKeys.Count; i++)
        {
            if (root.TryGetProperty(legacyKeys[i], out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    static string ReadStringWithLegacy(JsonElement root, string canonicalKey, IReadOnlyList<string> legacyKeys, string fallback)
    {
        if (!TryGetPropertyWithLegacy(root, canonicalKey, legacyKeys, out var element))
        {
            return fallback;
        }

        return element.ValueKind == JsonValueKind.String ? (element.GetString() ?? string.Empty) : fallback;
    }

    static bool? ReadNullableBool(JsonElement root, string key, bool? fallback)
    {
        if (!root.TryGetProperty(key, out var element))
        {
            return fallback;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => fallback
        };
    }

    static int ReadIntWithLegacy(JsonElement root, string canonicalKey, IReadOnlyList<string> legacyKeys, int fallback)
    {
        if (!TryGetPropertyWithLegacy(root, canonicalKey, legacyKeys, out var element))
        {
            return fallback;
        }

        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) ? value : fallback;
    }

    static List<string> ReadStringArrayWithLegacy(JsonElement root, string canonicalKey, IReadOnlyList<string> legacyKeys, IReadOnlyList<string> fallback)
    {
        if (!TryGetPropertyWithLegacy(root, canonicalKey, legacyKeys, out var element))
        {
            return fallback.ToList();
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return fallback.ToList();
        }

        return element.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? string.Empty)
            .ToList();
    }

    void ValidateStringPropertyIfPresent(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var element) && element.ValueKind != JsonValueKind.String)
        {
            Fail($"Workspace config '{WorkspaceConfigPath}' property '{key}' must be a string.");
        }
    }

    void ValidateIntegerPropertyIfPresent(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var element)
            && (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out _)))
        {
            Fail($"Workspace config '{WorkspaceConfigPath}' property '{key}' must be an integer.");
        }
    }

    void ValidateStringArrayPropertyIfPresent(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var element))
        {
            return;
        }

        if (element.ValueKind != JsonValueKind.Array || element.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
        {
            Fail($"Workspace config '{WorkspaceConfigPath}' property '{key}' must be an array of strings.");
        }
    }

    string ResolveConfigEnvValue(string value, string propertyName)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        const string envPrefix = "$env:";
        if (!trimmed.StartsWith(envPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var envName = trimmed[envPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(envName))
        {
            Log.Warning($"Config property '{propertyName}' references an empty environment variable name.");
            return string.Empty;
        }

        var resolved = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            Log.Warning($"Config property '{propertyName}' references environment variable '{envName}', but it is not set.");
            return string.Empty;
        }

        return resolved.Trim();
    }

    string NormalizePreservePathEntry(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            return NormalizePathOrEmpty(trimmed);
        }

        return trimmed.Replace('\\', '/').Trim('/');
    }

    void LogConfiguredPreservePathEntries(string preserveRoot, IReadOnlyList<string> normalizedPreservePaths)
    {
        foreach (var entry in normalizedPreservePaths)
        {
            var display = GetPreservePathDisplay(preserveRoot, entry);
            var kind = ClassifyPreservePathEntry(preserveRoot, entry);
            if (kind == "directory")
            {
                Log.Information($"Deploy preserve-paths: preserving directory {display}");
            }
            else
            {
                Log.Information($"Deploy preserve-paths: preserving file {display}");
            }
        }
    }

    string GetPreservePathDisplay(string preserveRoot, string entry)
    {
        if (!Path.IsPathRooted(entry))
        {
            return entry;
        }

        var normalizedRoot = NormalizePathOrEmpty(preserveRoot).Replace('\\', '/').TrimEnd('/');
        var normalizedEntry = NormalizePathOrEmpty(entry).Replace('\\', '/').TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(normalizedRoot)
            && normalizedEntry.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = normalizedEntry[(normalizedRoot.Length + 1)..];
            return string.IsNullOrWhiteSpace(relative) ? "." : relative;
        }

        return normalizedEntry;
    }

    string ClassifyPreservePathEntry(string preserveRoot, string entry)
    {
        if (IsGlobPattern(entry))
        {
            return "file";
        }

        var endsWithSeparator = entry.EndsWith("/", StringComparison.Ordinal) || entry.EndsWith("\\", StringComparison.Ordinal);
        var resolved = Path.IsPathRooted(entry)
            ? NormalizePathOrEmpty(entry)
            : NormalizePathOrEmpty(Path.Combine(preserveRoot, entry.Replace('/', Path.DirectorySeparatorChar)));

        if (Directory.Exists(resolved))
        {
            return "directory";
        }

        if (File.Exists(resolved))
        {
            return "file";
        }

        if (endsWithSeparator)
        {
            return "directory";
        }

        return Path.HasExtension(entry) ? "file" : "directory";
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
        WriteFullPackageHelperScripts(fullPayload);

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

    static void WriteFullPackageHelperScripts(string fullPayload)
    {
        var startPath = Path.Combine(fullPayload, "start-fahrenheit.cmd");

        var startScript = """
@echo off
setlocal

set "_PAUSE_ON_EXIT=1"
if /I "%~1"=="--no-pause" set "_PAUSE_ON_EXIT="

set "GAME_EXE=%~dp0..\FFX.exe"
set "STAGE0=%~dp0bin\fhstage0.exe"
set "_DOTNET_ROOT_X86=%DOTNET_ROOT(x86)%"
set "_RUNTIME_PRECHECK_MISSING="

if not exist "%GAME_EXE%" (
  set "GAME_EXE=%~dp0FFX.exe"
  if not exist "%GAME_EXE%" (
    echo [ERROR] FFX.exe not found. Expected either:
    echo   "%~dp0..\FFX.exe"
    echo   "%~dp0FFX.exe"
    set "RC=1"
    goto :finish
  )
)

if not exist "%STAGE0%" (
  echo [ERROR] fhstage0.exe not found: "%STAGE0%"
  set "RC=1"
  goto :finish
)

set "_HAS_FXR10="
call :check_fxr10 "%ProgramFiles(x86)%\dotnet\host\fxr"
call :check_fxr10 "%ProgramFiles%\dotnet\host\fxr"
call :check_fxr10 "%DOTNET_ROOT%\host\fxr"
call :check_fxr10 "%_DOTNET_ROOT_X86%\host\fxr"
call :check_dotnet_runtime10
call :check_registry_runtime10
if not defined _HAS_FXR10 (
  echo.
  echo [WARN] Microsoft .NET Runtime 10 is missing.
  call :prompt_install_runtime
  set "_HAS_FXR10="
  call :check_fxr10 "%ProgramFiles(x86)%\dotnet\host\fxr"
  call :check_fxr10 "%ProgramFiles%\dotnet\host\fxr"
  call :check_fxr10 "%DOTNET_ROOT%\host\fxr"
  call :check_fxr10 "%_DOTNET_ROOT_X86%\host\fxr"
  call :check_dotnet_runtime10
  call :check_registry_runtime10
  if not defined _HAS_FXR10 (
    echo [WARN] Runtime 10 still not detected by precheck. Continuing launch anyway.
    echo        If launch fails with load_hostfxr, install manually from:
    echo          https://dotnet.microsoft.com/en-us/download/dotnet/10.0
    set "_RUNTIME_PRECHECK_MISSING=1"
  )
)

pushd "%~dp0bin"
.\fhstage0.exe ..\..\FFX.exe
set "RC=%ERRORLEVEL%"
popd

if not "%RC%"=="0" (
  echo.
  echo [ERROR] Fahrenheit startup failed with exit code %RC%.
  if defined _RUNTIME_PRECHECK_MISSING (
    echo Runtime 10 precheck was inconclusive or missing.
  )
  echo If output contains "load_hostfxr() failed", rerun this script and allow runtime install,
  echo or install manually from:
  echo   https://dotnet.microsoft.com/en-us/download/dotnet/10.0
  goto :finish
)

goto :finish

:prompt_install_runtime
set "_INSTALL_RT="
set /p "_INSTALL_RT=Install Microsoft .NET Runtime 10 now using winget? [Y/n]: "
if /I "%_INSTALL_RT%"=="N" (
  echo [INFO] Skipping auto-install. Launch will continue.
  exit /b 0
)
where winget >NUL 2>&1
if errorlevel 1 (
  echo [WARN] winget is not available on this system.
  echo Install .NET Runtime 10 manually from:
  echo   https://dotnet.microsoft.com/en-us/download/dotnet/10.0
  exit /b 0
)
echo Installing Microsoft .NET Runtime 10 (x86 preferred)...
winget install --id Microsoft.DotNet.Runtime.10 --exact --architecture x86 --accept-package-agreements --accept-source-agreements
if errorlevel 1 (
  echo.
  echo x86 install failed or unavailable. Retrying default architecture...
  winget install --id Microsoft.DotNet.Runtime.10 --exact --accept-package-agreements --accept-source-agreements
)
if errorlevel 1 (
  echo.
  echo [WARN] Runtime installation failed. Launch will continue.
  exit /b 0
)
echo Runtime installation complete.
exit /b 0

:check_dotnet_runtime10
where dotnet >NUL 2>&1
if errorlevel 1 goto :eof
for /f "tokens=1,2,*" %%A in ('dotnet --list-runtimes 2^>NUL ^| findstr /R /I "^Microsoft\.NETCore\.App 10\."') do (
  set "_HAS_FXR10=1"
)
goto :eof

:check_registry_runtime10
reg query "HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App" /s /f 10. /d 2>NUL | findstr /I "10." >NUL && set "_HAS_FXR10=1"
reg query "HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x86\sharedfx\Microsoft.NETCore.App" /s /f 10. /d 2>NUL | findstr /I "10." >NUL && set "_HAS_FXR10=1"
reg query "HKLM\SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x86\sharedfx\Microsoft.NETCore.App" /s /f 10. /d 2>NUL | findstr /I "10." >NUL && set "_HAS_FXR10=1"
goto :eof

:finish
if not defined RC set "RC=0"
if defined _PAUSE_ON_EXIT (
  echo.
  if "%RC%"=="0" (
    echo Fahrenheit launcher finished. Press any key to close this window.
  ) else (
    echo Launcher exited with code %RC%. Press any key to close this window.
  )
  pause >NUL
)
exit /b %RC%

:check_fxr10
if "%~1"=="" goto :eof
if not exist "%~1" goto :eof
for /d %%D in ("%~1\10.*") do (
  if exist "%%~fD\hostfxr.dll" set "_HAS_FXR10=1"
)
goto :eof
""";

        File.WriteAllText(startPath, startScript.Replace("\n", Environment.NewLine), new UTF8Encoding(false));
    }

    void RunTestsIfAny(string configuration)
    {
        var projects = new List<string>();
        var testRoot = Path.Combine(RootDirectory, "tests");

        if (Directory.Exists(testRoot))
        {
            projects.AddRange(Directory.EnumerateFiles(testRoot, "*.csproj", SearchOption.AllDirectories));
        }

        projects.AddRange(Directory.EnumerateFiles(RootDirectory, "*.csproj", SearchOption.TopDirectoryOnly));

        projects = projects
            .Where(path => Path.GetFileName(path).Contains("test", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (projects.Count == 0)
        {
            Log.Information("No test projects found outside .workspace. Skipping tests.");
            return;
        }

        foreach (var project in projects)
        {
            RunChecked(
                "dotnet",
                $"test {Quote(project)} --configuration {Quote(configuration)} --nologo --verbosity {ResolveDotNetCliVerbosity()}",
                $"Run tests for {Path.GetFileName(project)}");
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
        bool silent = false,
        bool showStdOutOnSuccess = false,
        bool showStdErrOnSuccess = false)
    {
        var effectiveWorkingDirectory = workingDirectory ?? RootDirectory;
        if (!silent && IsLogVerbosityAtLeast(BuildLogVerbosity.Diagnostic))
        {
            Log.Information($"[TRACE] EXEC {fileName} {args}");
            Log.Information($"[TRACE] CWD  {effectiveWorkingDirectory}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            WorkingDirectory = effectiveWorkingDirectory,
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

        var result = new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        EmitProcessOutput(
            description,
            result,
            silent,
            showStdOutOnSuccess,
            showStdErrOnSuccess);
        return result;
    }

    void EmitProcessOutput(
        string description,
        ProcessResult result,
        bool silent,
        bool showStdOutOnSuccess,
        bool showStdErrOnSuccess)
    {
        if (silent)
        {
            return;
        }

        var level = ResolveLogVerbosity();
        var failed = result.ExitCode != 0;
        var stdoutLines = SplitProcessOutputLines(result.StdOut);
        var stderrLines = SplitProcessOutputLines(result.StdErr);

        var emitStdOut = failed
                         || level is BuildLogVerbosity.Detailed or BuildLogVerbosity.Diagnostic
                         || (level == BuildLogVerbosity.Normal && showStdOutOnSuccess);
        var emitStdErr = failed
                         || level is BuildLogVerbosity.Detailed or BuildLogVerbosity.Diagnostic
                         || (level == BuildLogVerbosity.Normal && showStdErrOnSuccess);

        if (!failed && (level is BuildLogVerbosity.Quiet or BuildLogVerbosity.Minimal))
        {
            emitStdOut = false;
            emitStdErr = false;
        }

        if (emitStdOut)
        {
            foreach (var line in stdoutLines)
            {
                if (level == BuildLogVerbosity.Diagnostic)
                {
                    Log.Information($"[TRACE][{description}][stdout] {line}");
                }
                else
                {
                    Log.Information(line);
                }
            }
        }

        if (emitStdErr)
        {
            foreach (var line in stderrLines)
            {
                if (level == BuildLogVerbosity.Diagnostic)
                {
                    Log.Warning($"[TRACE][{description}][stderr] {line}");
                }
                else
                {
                    Log.Warning(line);
                }
            }
        }
    }

    static IReadOnlyList<string> SplitProcessOutputLines(string value)
    {
        return value
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
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

    void MirrorDirectoryRecursive(string source, string destination)
    {
        EnsureDirectoryMaybe(destination);
        var destinationExists = Directory.Exists(destination);

        if (destinationExists)
        {
            foreach (var file in Directory.GetFiles(destination, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(destination, file);
                var sourceFile = Path.Combine(source, relative);
                if (!File.Exists(sourceFile))
                {
                    DeleteFileMaybe(file);
                }
            }

            foreach (var dir in Directory.GetDirectories(destination, "*", SearchOption.AllDirectories)
                         .OrderByDescending(x => x.Length))
            {
                var relative = Path.GetRelativePath(destination, dir);
                var sourceDir = Path.Combine(source, relative);
                if (!Directory.Exists(sourceDir))
                {
                    DeleteDirectoryMaybe(dir);
                }
            }
        }

        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, dir);
            EnsureDirectoryMaybe(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(parent)) EnsureDirectoryMaybe(parent);
            CopyFileMaybe(file, target, overwrite: true);
        }
    }

    void SyncDirectoryRecursiveWithPreservePaths(string source, string destination, string preserveRoot, IReadOnlyList<string> preservePaths)
    {
        var normalizedPreservePaths = (preservePaths ?? [])
            .Select(NormalizePreservePathEntry)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedPreservePaths.Count == 0)
        {
            MirrorDirectoryRecursive(source, destination);
            return;
        }

        LogConfiguredPreservePathEntries(preserveRoot, normalizedPreservePaths);

        EnsureDirectoryMaybe(destination);
        var destinationExists = Directory.Exists(destination);

        var relativeEntries = normalizedPreservePaths
            .Where(x => !Path.IsPathRooted(x))
            .Where(x => !IsGlobPattern(x))
            .Select(NormalizeRelativeForComparison)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        var relativeGlobEntries = normalizedPreservePaths
            .Where(x => !Path.IsPathRooted(x))
            .Where(IsGlobPattern)
            .Select(NormalizeRelativeForComparison)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        var absoluteEntries = normalizedPreservePaths
            .Where(Path.IsPathRooted)
            .Where(x => !IsGlobPattern(x))
            .Select(x => NormalizePathOrEmpty(x).Replace('\\', '/').TrimEnd('/'))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        var absoluteGlobEntries = normalizedPreservePaths
            .Where(Path.IsPathRooted)
            .Where(IsGlobPattern)
            .Select(x => NormalizePathOrEmpty(x).Replace('\\', '/').TrimEnd('/'))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        var relativeGlobPatterns = relativeGlobEntries
            .Select(CreateGlobRegex)
            .ToList();
        var absoluteGlobPatterns = absoluteGlobEntries
            .Select(CreateGlobRegex)
            .ToList();
        var relativeGlobPrefixes = relativeGlobEntries
            .Select(GetGlobLiteralPrefix)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var absoluteGlobPrefixes = absoluteGlobEntries
            .Select(GetGlobLiteralPrefix)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (destinationExists)
        {
            foreach (var file in Directory.GetFiles(destination, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(destination, file);
                var sourceFile = Path.Combine(source, relative);
                if (File.Exists(sourceFile))
                {
                    continue;
                }

                if (IsTargetPathPreserved(file, preserveRoot, relativeEntries, absoluteEntries, relativeGlobPatterns, absoluteGlobPatterns, out _))
                {
                    continue;
                }

                DeleteFileMaybe(file);
            }

            foreach (var dir in Directory.GetDirectories(destination, "*", SearchOption.AllDirectories)
                         .OrderByDescending(x => x.Length))
            {
                var relative = Path.GetRelativePath(destination, dir);
                var sourceDir = Path.Combine(source, relative);
                if (Directory.Exists(sourceDir))
                {
                    continue;
                }

                if (IsTargetPathPreserved(dir, preserveRoot, relativeEntries, absoluteEntries, relativeGlobPatterns, absoluteGlobPatterns, out _))
                {
                    continue;
                }

                var normalizedDirAbsolute = NormalizePathOrEmpty(dir).Replace('\\', '/').TrimEnd('/');
                var normalizedRelativeToRoot = NormalizeRelativeForComparison(Path.GetRelativePath(preserveRoot, dir));
                if (DirectoryContainsPreservedPath(normalizedRelativeToRoot, normalizedDirAbsolute, relativeEntries, absoluteEntries, relativeGlobPrefixes, absoluteGlobPrefixes))
                {
                    continue;
                }

                DeleteDirectoryMaybe(dir);
            }
        }

        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, dir);
            var target = Path.Combine(destination, relative);
            if (IsTargetPathPreserved(target, preserveRoot, relativeEntries, absoluteEntries, relativeGlobPatterns, absoluteGlobPatterns, out _))
            {
                continue;
            }

            EnsureDirectoryMaybe(target);
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            if (IsTargetPathPreserved(target, preserveRoot, relativeEntries, absoluteEntries, relativeGlobPatterns, absoluteGlobPatterns, out _))
            {
                continue;
            }

            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(parent)) EnsureDirectoryMaybe(parent);
            CopyFileMaybe(file, target, overwrite: true);
        }
    }

    void EnsureDirectoryMaybe(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (Directory.Exists(path))
        {
            return;
        }

        if (DryRun)
        {
            if (IsLogVerbosityAtLeast(BuildLogVerbosity.Detailed))
            {
                Log.Information($"[DRY-RUN] Create directory: {path}");
            }

            return;
        }

        Directory.CreateDirectory(path);
    }

    void DeleteFileMaybe(string path)
    {
        if (DryRun)
        {
            if (IsLogVerbosityAtLeast(BuildLogVerbosity.Detailed))
            {
                Log.Information($"[DRY-RUN] Delete file: {path}");
            }

            return;
        }

        File.Delete(path);
    }

    void DeleteDirectoryMaybe(string path)
    {
        if (DryRun)
        {
            if (IsLogVerbosityAtLeast(BuildLogVerbosity.Detailed))
            {
                Log.Information($"[DRY-RUN] Delete directory: {path}");
            }

            return;
        }

        Directory.Delete(path, recursive: true);
    }

    void CopyFileMaybe(string source, string target, bool overwrite)
    {
        if (DryRun)
        {
            if (IsLogVerbosityAtLeast(BuildLogVerbosity.Detailed))
            {
                Log.Information($"[DRY-RUN] Copy file: {source} -> {target}");
            }

            return;
        }

        File.Copy(source, target, overwrite: overwrite);
    }

    bool IsTargetPathPreserved(
        string targetPath,
        string preserveRoot,
        IReadOnlyList<string> relativeEntries,
        IReadOnlyList<string> absoluteEntries,
        IReadOnlyList<Regex> relativeGlobPatterns,
        IReadOnlyList<Regex> absoluteGlobPatterns,
        out string relativeToRootDisplay)
    {
        var normalizedTargetAbsolute = NormalizePathOrEmpty(targetPath).Replace('\\', '/').TrimEnd('/');
        var normalizedRelativeToRoot = NormalizeRelativeForComparison(Path.GetRelativePath(preserveRoot, targetPath));
        relativeToRootDisplay = string.IsNullOrWhiteSpace(normalizedRelativeToRoot) ? "." : normalizedRelativeToRoot;
        return IsPreservedPath(normalizedRelativeToRoot, normalizedTargetAbsolute, relativeEntries, absoluteEntries, relativeGlobPatterns, absoluteGlobPatterns);
    }

    static bool IsPreservedPath(
        string relativeToRoot,
        string targetAbsolute,
        IReadOnlyList<string> relativeEntries,
        IReadOnlyList<string> absoluteEntries,
        IReadOnlyList<Regex> relativeGlobPatterns,
        IReadOnlyList<Regex> absoluteGlobPatterns)
    {
        foreach (var absoluteEntry in absoluteEntries)
        {
            if (targetAbsolute.Equals(absoluteEntry, StringComparison.OrdinalIgnoreCase) ||
                targetAbsolute.StartsWith(absoluteEntry + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var absolutePattern in absoluteGlobPatterns)
        {
            if (absolutePattern.IsMatch(targetAbsolute))
            {
                return true;
            }
        }

        if (relativeToRoot.StartsWith("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var relativeEntry in relativeEntries)
        {
            if (relativeToRoot.Equals(relativeEntry, StringComparison.OrdinalIgnoreCase) ||
                relativeToRoot.StartsWith(relativeEntry + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var relativePattern in relativeGlobPatterns)
        {
            if (relativePattern.IsMatch(relativeToRoot))
            {
                return true;
            }
        }

        return false;
    }

    static bool DirectoryContainsPreservedPath(
        string directoryRelativeToRoot,
        string directoryAbsolute,
        IReadOnlyList<string> relativeEntries,
        IReadOnlyList<string> absoluteEntries,
        IReadOnlyList<string> relativeGlobPrefixes,
        IReadOnlyList<string> absoluteGlobPrefixes)
    {
        foreach (var absoluteEntry in absoluteEntries)
        {
            if (absoluteEntry.Equals(directoryAbsolute, StringComparison.OrdinalIgnoreCase) ||
                absoluteEntry.StartsWith(directoryAbsolute + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (directoryRelativeToRoot.StartsWith("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(directoryRelativeToRoot))
        {
            return relativeEntries.Count > 0 || relativeGlobPrefixes.Count > 0;
        }

        foreach (var relativeEntry in relativeEntries)
        {
            if (relativeEntry.Equals(directoryRelativeToRoot, StringComparison.OrdinalIgnoreCase) ||
                relativeEntry.StartsWith(directoryRelativeToRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var relativePrefix in relativeGlobPrefixes)
        {
            if (string.IsNullOrWhiteSpace(relativePrefix))
            {
                return true;
            }

            if (relativePrefix.Equals(directoryRelativeToRoot, StringComparison.OrdinalIgnoreCase) ||
                relativePrefix.StartsWith(directoryRelativeToRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var absolutePrefix in absoluteGlobPrefixes)
        {
            if (string.IsNullOrWhiteSpace(absolutePrefix))
            {
                return true;
            }

            if (absolutePrefix.Equals(directoryAbsolute, StringComparison.OrdinalIgnoreCase) ||
                absolutePrefix.StartsWith(directoryAbsolute + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    static bool IsGlobPattern(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && (value.Contains('*') || value.Contains('?') || value.Contains('['));
    }

    static Regex CreateGlobRegex(string pattern)
    {
        var normalized = (pattern ?? string.Empty).Replace('\\', '/').Trim();
        var sb = new StringBuilder("^");

        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (c == '*')
            {
                var isDoubleStar = i + 1 < normalized.Length && normalized[i + 1] == '*';
                if (isDoubleStar)
                {
                    sb.Append(".*");
                    i++;
                }
                else
                {
                    sb.Append("[^/]*");
                }

                continue;
            }

            if (c == '?')
            {
                sb.Append("[^/]");
                continue;
            }

            sb.Append(Regex.Escape(c.ToString()));
        }

        sb.Append("$");
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    static string GetGlobLiteralPrefix(string pattern)
    {
        var normalized = (pattern ?? string.Empty).Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var wildcardIndex = normalized.IndexOfAny(['*', '?', '[']);
        if (wildcardIndex < 0)
        {
            return normalized;
        }

        if (wildcardIndex == 0)
        {
            return string.Empty;
        }

        return normalized[..wildcardIndex].TrimEnd('/');
    }

    static string NormalizeRelativeForComparison(string relativePath)
    {
        var normalized = (relativePath ?? string.Empty).Replace('\\', '/').Trim();
        if (normalized == ".")
        {
            return string.Empty;
        }

        return normalized.Trim('/');
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

    void ValidateVerbosityFlags() => _ = ResolveLogVerbosity();

    BuildLogVerbosity ResolveLogVerbosity()
    {
        if (_resolvedLogVerbosity is { } cached)
        {
            return cached;
        }

        var fromParameter = (LogVerbosity ?? string.Empty).Trim();
        _resolvedLogVerbosity = string.IsNullOrWhiteSpace(fromParameter)
            ? BuildLogVerbosity.Normal
            : ParseLogVerbosity(fromParameter);
        return _resolvedLogVerbosity.Value;
    }

    static BuildLogVerbosity ParseLogVerbosity(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is "quiet" or "q")
            return BuildLogVerbosity.Quiet;
        if (normalized is "minimal" or "min" or "m")
            return BuildLogVerbosity.Minimal;
        if (normalized is "normal" or "n")
            return BuildLogVerbosity.Normal;
        if (normalized is "detailed" or "detail" or "d")
            return BuildLogVerbosity.Detailed;
        if (normalized is "diagnostic" or "diag")
            return BuildLogVerbosity.Diagnostic;

        Fail($"Invalid verbosity '{value}'. Use: quiet, minimal, normal, detailed, diagnostic.");
        return BuildLogVerbosity.Normal;
    }

    bool IsLogVerbosityAtLeast(BuildLogVerbosity level) => ResolveLogVerbosity() >= level;

    static void WriteHelpLine(string value) => Console.WriteLine(value);

    string ResolveMsBuildVerbosity()
    {
        return ResolveLogVerbosity() switch
        {
            BuildLogVerbosity.Quiet => "quiet",
            BuildLogVerbosity.Minimal => "minimal",
            BuildLogVerbosity.Normal => "minimal",
            BuildLogVerbosity.Detailed => "normal",
            BuildLogVerbosity.Diagnostic => "diagnostic",
            _ => "minimal"
        };
    }

    string ResolveDotNetCliVerbosity()
    {
        return ResolveLogVerbosity() switch
        {
            BuildLogVerbosity.Quiet => "quiet",
            BuildLogVerbosity.Minimal => "minimal",
            BuildLogVerbosity.Normal => "minimal",
            BuildLogVerbosity.Detailed => "normal",
            BuildLogVerbosity.Diagnostic => "detailed",
            _ => "minimal"
        };
    }

    enum BuildLogVerbosity
    {
        Quiet = 0,
        Minimal = 1,
        Normal = 2,
        Detailed = 3,
        Diagnostic = 4
    }

    sealed class WorkspaceConfig
    {
        public string Configuration { get; set; } = "Release";
        public string GameDir { get; set; } = string.Empty;
        public bool? AutoDeploy { get; set; }
        public List<string>? DeployPreservePaths { get; set; }
        public string VisionApiUrl { get; set; } = string.Empty;
        public string VisionApiKey { get; set; } = string.Empty;
        public string VisionModel { get; set; } = string.Empty;
        public int FetchRetries { get; set; } = 2;
        public string DiscordToken { get; set; } = string.Empty;
    }

    readonly record struct WorkflowHelpBlock(
        string Workflow,
        string Purpose,
        IReadOnlyList<string> Parameters,
        IReadOnlyList<string> Examples);

    readonly record struct SemVersion(int Major, int Minor, int Patch)
    {
        public override string ToString() => $"{Major}.{Minor}.{Patch}";
    }

    readonly record struct CommitWizardResult(string Type, string Scope, string Message, bool Breaking, bool Confirmed);

    readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);
}



