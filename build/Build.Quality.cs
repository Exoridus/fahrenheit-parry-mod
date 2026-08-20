using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Nuke.Common;
using Serilog;
using static Nuke.Common.Assert;

internal sealed partial class BuildScript
{
    Target Doctor => _ => _.Executes(RunDoctorCore);

    Target Format => _ => _.Executes(RunFormatFixCore);

    Target Lint => _ => _.Executes(() => RunLintCore(RequestedConfiguration));

    Target Smoke => _ => _.Executes(() => RunSmokeCore(RequestedConfiguration));

    void RunCleanCore()
    {
        if (Purge && !Yes)
        {
            Fail("clean --purge is destructive and requires --yes.");
        }

        var includeAnalysis = Purge || CleanAnalysis;
        var includeExports = Purge || CleanExports;
        var includeGameData = Purge || CleanGameData;
        var includeTools = Purge || CleanToolsRequested;
        var includeReleaseRoot = Purge;

        var result = new CleanupAccountingResult();
        Log.Information(
            $"Starting clean (dry-run={DryRun.ToString().ToLowerInvariant()}, purge={Purge.ToString().ToLowerInvariant()}, analysis={includeAnalysis.ToString().ToLowerInvariant()}, exports={includeExports.ToString().ToLowerInvariant()}, game-data={includeGameData.ToString().ToLowerInvariant()}, purge-tools={includeTools.ToString().ToLowerInvariant()}).");

        // Default clean: cache + artifacts.
        DeleteDirectoryMaybeWithAccounting(ResolvePath(".workspace/local-build"), result);

        var artifactDirectories = new[]
        {
            Path.Combine(RootDirectory, "bin"),
            Path.Combine(RootDirectory, "obj"),
            Path.Combine(RootDirectory, "build", "bin"),
            Path.Combine(RootDirectory, "build", "obj"),
            Path.Combine(RootDirectory, "tests", "Parry.Tests", "bin"),
            Path.Combine(RootDirectory, "tests", "Parry.Tests", "obj"),
            Path.Combine(ResolvePath(FahrenheitDir), "artifacts"),
            Path.Combine(ResolvePath(".release"), "stage"),
            Path.Combine(ResolvePath(".release"), "preflight")
        };

        foreach (var path in artifactDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DeleteDirectoryMaybeWithAccounting(path, result);
        }

        if (includeReleaseRoot)
        {
            DeleteDirectoryMaybeWithAccounting(ResolvePath(".release"), result);
        }

        if (includeAnalysis)
        {
            DeleteDirectoryMaybeWithAccounting(ResolvePath(".workspace/analysis"), result);
        }

        if (includeExports)
        {
            DeleteDirectoryMaybeWithAccounting(ResolvePath(".workspace/discord"), result);
        }

        if (includeGameData)
        {
            DeleteDirectoryMaybeWithAccounting(ResolvePath(".workspace/data"), result);
        }

        if (includeTools)
        {
            DeleteDirectoryMaybeWithAccounting(ResolvePath(".workspace/tools"), result);
        }

        Log.Information(
            $"Clean complete. Removed files={result.FilesRemoved}, directories={result.DirectoriesRemoved}, reclaimed={FormatBytes(result.BytesReclaimed)}.");
    }

    void RunDoctorCore()
    {
        var requiredFailures = new List<string>();
        var optionalWarnings = new List<string>();

        Log.Information("Doctor report");
        Log.Information($"  OS: {RuntimeInformation.OSDescription}");
        Log.Information($"  Architecture: {RuntimeInformation.OSArchitecture}");
        Log.Information($"  Runtime: {RuntimeInformation.FrameworkDescription}");

        var hasGit = CommandExists("git");
        LogDoctorCheck("Git", hasGit, required: true, "Required for clone/update, changelog, and tag workflows.");
        if (!hasGit) requiredFailures.Add("Git");

        var hasDotNet = CommandExists("dotnet");
        LogDoctorCheck(".NET SDK CLI", hasDotNet, required: true, "Required for NUKE and managed builds.");
        if (!hasDotNet) requiredFailures.Add(".NET SDK CLI");

        var hasSdk10 = hasDotNet && DotNetSdkMajorInstalled(10);
        LogDoctorCheck(".NET SDK 10.x", hasSdk10, required: true, "Pinned by global.json.");
        if (!hasSdk10) requiredFailures.Add(".NET SDK 10.x");

        var hasWinget = CommandExists("winget");
        LogDoctorCheck("winget", hasWinget, required: false, "Used by build.cmd install for automated prerequisite setup.");
        if (!hasWinget) optionalWarnings.Add("winget");

        var hasJava = CommandExists("java");
        LogDoctorCheck("Java", hasJava, required: false, "Required only for FFXDataParser workflows.");
        if (!hasJava) optionalWarnings.Add("java");

        var hasMaven = CommandExists("mvn");
        LogDoctorCheck("Maven", hasMaven, required: false, "Required only for FFXDataParser workflows.");
        if (!hasMaven) optionalWarnings.Add("mvn");

        if (Full)
        {
            var hasMsbuild = CommandExists("msbuild");
            LogDoctorCheck("MSBuild", hasMsbuild, required: true, "Required for full native Fahrenheit builds.");
            if (!hasMsbuild) requiredFailures.Add("MSBuild");

            var hasVcpkg = !string.IsNullOrWhiteSpace(FindVcpkgExecutable());
            LogDoctorCheck("vcpkg", hasVcpkg, required: true, "Required for native dependency resolution in full builds.");
            if (!hasVcpkg) requiredFailures.Add("vcpkg");
        }
        else
        {
            var hasMsbuild = CommandExists("msbuild");
            LogDoctorCheck("MSBuild", hasMsbuild, required: false, "Needed for full Fahrenheit native builds.");
            if (!hasMsbuild) optionalWarnings.Add("msbuild");

            var hasVcpkg = !string.IsNullOrWhiteSpace(FindVcpkgExecutable());
            LogDoctorCheck("vcpkg", hasVcpkg, required: false, "Needed for full Fahrenheit native builds.");
            if (!hasVcpkg) optionalWarnings.Add("vcpkg");
        }

        if (requiredFailures.Count > 0)
        {
            Fail("Doctor failed. Missing required prerequisites: " + string.Join(", ", requiredFailures));
        }

        if (optionalWarnings.Count > 0)
        {
            Log.Warning("Doctor warnings (optional tools missing): " + string.Join(", ", optionalWarnings));
        }

        Log.Information("Doctor completed: required prerequisites are available.");
    }

    void RunLintCore(string configuration)
    {
        var normalizedConfiguration = ResolveBuildConfiguration(configuration);
        ValidateJsonConfigsCore();

        var buildProject = Path.Combine(RootDirectory, "build", "Build.csproj");
        if (!File.Exists(buildProject))
        {
            Fail($"Missing build project: {buildProject}");
        }

        RunChecked(
            "dotnet",
            $"build {Quote(buildProject)} --configuration {Quote(normalizedConfiguration)} --nologo --verbosity {ResolveDotNetCliVerbosity()} -warnaserror",
            "Lint compile check (build orchestration)");

        var modProject = Path.Combine(RootDirectory, "Fahrenheit.Mods.Parry.csproj");
        RunChecked(
            "dotnet",
            $"build {Quote(modProject)} --configuration {Quote(normalizedConfiguration)} --nologo --verbosity {ResolveDotNetCliVerbosity()}",
            "Lint compile check (mod project)");

        var testsProject = Path.Combine(RootDirectory, "tests", "Parry.Tests", "Parry.Tests.csproj");
        if (File.Exists(testsProject))
        {
            RunChecked(
                "dotnet",
                $"build {Quote(testsProject)} --configuration {Quote(normalizedConfiguration)} --nologo --verbosity {ResolveDotNetCliVerbosity()}",
                "Lint compile check (tests)");
        }

        ValidateCommitMessageString("feat: lint selftest");
        Log.Information("Lint checks passed.");
    }

    void RunFormatFixCore()
    {
        var modProject = Path.Combine(RootDirectory, "Fahrenheit.Mods.Parry.csproj");
        if (!File.Exists(modProject))
        {
            Fail($"Missing mod project: {modProject}");
        }

        RunChecked(
            "dotnet",
            $"format {Quote(modProject)} --no-restore --verbosity {ResolveDotNetCliVerbosity()}",
            "Code style auto-fix (dotnet format)");
    }

    void ValidateJsonConfigsCore()
    {
        ValidateManifestJson();
        ValidateLanguageJson();
        Log.Information("JSON configuration checks passed.");
    }

    void ValidateManifestJson()
    {
        var manifest = ManifestPath.ToString();
        if (!File.Exists(manifest))
        {
            Fail($"Missing manifest file: {manifest}");
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            Fail("Manifest root must be a JSON object.");
        }

        string[] requiredStringFields = ["Id", "Name", "Desc", "Authors", "Version", "Link", "Flags"];
        foreach (var field in requiredStringFields)
        {
            if (!root.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            {
                Fail($"Manifest validation failed: '{field}' must be a non-empty string.");
            }
        }

        var version = root.GetProperty("Version").GetString() ?? string.Empty;
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z\.-]+)?$"))
        {
            Fail($"Manifest validation failed: Version '{version}' is not SemVer-like.");
        }

        ValidateManifestStringArray(root, "Dependencies");
        ValidateManifestStringArray(root, "LoadAfter");
    }

    static void ValidateManifestStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            Fail($"Manifest validation failed: '{propertyName}' must be an array.");
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                Fail($"Manifest validation failed: '{propertyName}' entries must be strings.");
            }
        }
    }

    void ValidateLanguageJson()
    {
        var langDir = Path.Combine(RootDirectory, "lang");
        if (!Directory.Exists(langDir))
        {
            Fail($"Missing lang directory: {langDir}");
        }

        var requiredFiles = new[]
        {
            "en-US.json",
            "de-DE.json"
        };

        foreach (var file in requiredFiles)
        {
            var path = Path.Combine(langDir, file);
            if (!File.Exists(path))
            {
                Fail($"Missing required language file: {path}");
            }
        }

        var baselinePath = Path.Combine(langDir, "en-US.json");
        var baseline = ReadStringMap(baselinePath, allowEmptyValues: false);
        if (baseline.Count == 0)
        {
            Fail("Language validation failed: lang/en-US.json must contain at least one entry.");
        }

        var langFiles = Directory.GetFiles(langDir, "*.json", SearchOption.TopDirectoryOnly);
        foreach (var file in langFiles)
        {
            var map = ReadStringMap(file, allowEmptyValues: false);
            var missingKeys = baseline.Keys.Where(k => !map.ContainsKey(k)).ToList();
            if (missingKeys.Count > 0)
            {
                Fail($"Language validation failed: {Path.GetFileName(file)} is missing keys: {string.Join(", ", missingKeys.Take(10))}{(missingKeys.Count > 10 ? " ..." : string.Empty)}");
            }

            ValidateLanguageGlyphRange(file, map);
        }
    }

    // Fahrenheit builds its ImGui font with io.Fonts.GetGlyphRangesDefault(), which
    // covers Basic Latin plus Latin-1 Supplement and nothing else. Umlauts and the
    // sharp s are inside that range and render; an em dash, a curly quote or an
    // ellipsis character is outside it and draws as a missing glyph in the settings
    // panel, where nobody sees it until a screenshot arrives. The fix is always to
    // pick the ASCII punctuation, never to strip a letter of its diacritic.
    static void ValidateLanguageGlyphRange(string path, Dictionary<string, string> map)
    {
        foreach (var (key, value) in map)
        {
            foreach (var c in value)
            {
                if (c <= 'ÿ') continue;

                Fail($"Language validation failed: key '{key}' in {Path.GetFileName(path)} contains "
                   + $"U+{(int)c:X4} ('{c}'), which the ImGui font cannot render. Only U+0000-U+00FF "
                   + "is available; use ASCII punctuation instead.");
            }
        }
    }

    static Dictionary<string, string> ReadStringMap(string path, bool allowEmptyValues)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            Fail($"Language validation failed: {Path.GetFileName(path)} must contain a JSON object.");
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String)
            {
                Fail($"Language validation failed: key '{prop.Name}' in {Path.GetFileName(path)} must map to a string.");
            }

            var value = prop.Value.GetString() ?? string.Empty;
            if (!allowEmptyValues && string.IsNullOrWhiteSpace(value))
            {
                Fail($"Language validation failed: key '{prop.Name}' in {Path.GetFileName(path)} must not be empty.");
            }

            map[prop.Name] = value;
        }

        return map;
    }

    void RunSmokeCore(string configuration)
    {
        var normalizedConfig = ResolveBuildConfiguration(configuration);
        var deployConfig = normalizedConfig.Equals("Release", StringComparison.OrdinalIgnoreCase) ? "rel" : "dbg";
        var effectiveFahrenheitRef = ResolveFahrenheitRef(useReleaseRef: false);

        RunBuildProjTarget("Build", normalizedConfig, includeNativeMsbuild: true, fahrenheitRef: effectiveFahrenheitRef);

        var localOutput = Path.Combine(RootDirectory, "bin", normalizedConfig, "net10.0", "win-x86");
        AssertFilesExist(
            localOutput,
            "fhparry.dll",
            "fhparry.manifest.json",
            Path.Combine("mappings", "runtime", "ffx-mappings.json"),
            Path.Combine("mappings", "runtime", "ffx-mappings.us.json"));

        var deployModOutput = Path.Combine(ResolvePath(FahrenheitDir), "artifacts", "deploy", deployConfig, "mods", ModId);
        AssertFilesExist(
            deployModOutput,
            "fhparry.dll",
            "fhparry.manifest.json",
            Path.Combine("mappings", "runtime", "ffx-mappings.json"),
            Path.Combine("mappings", "runtime", "ffx-mappings.us.json"));

        var stage0 = Path.Combine(ResolvePath(FahrenheitDir), "artifacts", "deploy", deployConfig, "bin", "fhstage0.exe");
        if (!File.Exists(stage0))
        {
            Fail($"Smoke check failed. Missing stage0 loader: {stage0}");
        }

        RunBuildCliSmokeCore();
        Log.Information($"Smoke checks passed for full payload, config={normalizedConfig}.");
    }

    void RunBuildCliSmokeCore()
    {
        var checks = new[]
        {
            new BuildCliSmokeCheck(
                Command: @".\build.cmd --help",
                MustContain: ["[NUKE] dotnet run --project build\\Build.csproj -- --target Help"],
                MustNotContain: []),
            new BuildCliSmokeCheck(
                Command: @".\build.cmd -h deploy",
                MustContain: ["[NUKE] dotnet run --project build\\Build.csproj -- --target Help --workflow deploy"],
                MustNotContain: []),
            new BuildCliSmokeCheck(
                Command: @".\build.cmd deploy -h",
                MustContain: ["[NUKE] dotnet run --project build\\Build.csproj -- --target Help --workflow deploy"],
                MustNotContain: []),
            new BuildCliSmokeCheck(
                Command: @".\build.cmd build --no-auto-deploy --configuration Debug",
                MustContain: ["[NUKE] dotnet run --project build\\Build.csproj -- --target Cli --workflow build --no-auto-deploy --configuration Debug"],
                MustNotContain: []),
            new BuildCliSmokeCheck(
                Command: @".\build.cmd --target Help --workflow build --dry-run",
                MustContain: ["[NUKE] dotnet run --project build\\Build.csproj -- --target Help --workflow build --dry-run"],
                MustNotContain: []),
            new BuildCliSmokeCheck(
                Command: @".\build.cmd --target Help --workflow deploy --game-dir ""C:\Program Files\Square Enix\Final Fantasy X-X2 - HD Remaster""",
                MustContain: ["--target Help --workflow deploy --game-dir"],
                MustNotContain: [])
        };

        foreach (var check in checks)
        {
            Log.Information($"[CLI-SMOKE] {check.Command}");
            var cmdArgs = $"/c set BUILD_CMD_SMOKE_ONLY=1&& set BUILD_CMD_ALLOW_NESTED=1&& {check.Command}";
            var result = RunProcess(
                "cmd",
                cmdArgs,
                $"CLI smoke: {check.Command}",
                showSpinner: false,
                silent: true);
            var output = result.StdOut + result.StdErr;

            if (result.ExitCode != 0)
            {
                Fail($"CLI smoke command failed ({result.ExitCode}): {check.Command}{Environment.NewLine}{output}");
            }

            foreach (var needle in check.MustContain)
            {
                if (!output.Contains(needle, StringComparison.Ordinal))
                {
                    Fail($"CLI smoke expected output to contain '{needle}' for command: {check.Command}{Environment.NewLine}{output}");
                }
            }

            foreach (var needle in check.MustNotContain)
            {
                if (output.Contains(needle, StringComparison.Ordinal))
                {
                    Fail($"CLI smoke expected output to not contain '{needle}' for command: {check.Command}{Environment.NewLine}{output}");
                }
            }
        }

        Log.Information("[CLI-SMOKE] All CLI checks passed.");
    }

    readonly record struct BuildCliSmokeCheck(string Command, string[] MustContain, string[] MustNotContain);

    static void AssertFilesExist(string rootPath, params string[] relativeFiles)
    {
        if (!Directory.Exists(rootPath))
        {
            Fail($"Missing expected directory: {rootPath}");
        }

        foreach (var relative in relativeFiles)
        {
            var path = Path.Combine(rootPath, relative);
            if (!File.Exists(path))
            {
                Fail($"Missing expected file: {path}");
            }
        }
    }

    static void LogDoctorCheck(string name, bool ok, bool required, string detail)
    {
        var requiredLabel = required ? "required" : "optional";
        if (ok)
        {
            Log.Information($"[OK] {name} ({requiredLabel}) - {detail}");
        }
        else
        {
            Log.Warning($"[MISSING] {name} ({requiredLabel}) - {detail}");
        }
    }
}
