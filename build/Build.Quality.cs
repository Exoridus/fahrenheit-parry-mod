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

    Target Lint => _ => _.Executes(() => RunLintCore(Config));

    Target Smoke => _ => _.Executes(() => RunSmokeCore(Payload, Config));

    void RunCleanCore(bool full)
    {
        var directories = new List<string>
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

        if (full)
        {
            directories.Add(ResolvePath(".release"));
        }

        var removed = 0;
        foreach (var path in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(path))
            {
                continue;
            }

            Directory.Delete(path, recursive: true);
            removed++;
            Log.Information($"Removed: {path}");
        }

        Log.Information(removed == 0
            ? "Clean completed: nothing to remove."
            : $"Clean completed: removed {removed} director{(removed == 1 ? "y" : "ies")}.");
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
            LogDoctorCheck("MSBuild", hasMsbuild, required: false, "Needed only for --payload full.");
            if (!hasMsbuild) optionalWarnings.Add("msbuild");

            var hasVcpkg = !string.IsNullOrWhiteSpace(FindVcpkgExecutable());
            LogDoctorCheck("vcpkg", hasVcpkg, required: false, "Needed only for --payload full.");
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
        ValidateJsonConfigsCore();

        var buildProject = Path.Combine(RootDirectory, "build", "Build.csproj");
        if (!File.Exists(buildProject))
        {
            Fail($"Missing build project: {buildProject}");
        }

        RunChecked(
            "dotnet",
            $"build {Quote(buildProject)} --configuration {Quote(configuration)} --nologo -warnaserror",
            "Lint compile check (build orchestration)");

        var modProject = Path.Combine(RootDirectory, "Fahrenheit.Mods.Parry.csproj");
        RunChecked(
            "dotnet",
            $"build {Quote(modProject)} --configuration {Quote(configuration)} --nologo",
            "Lint compile check (mod project)");

        var testsProject = Path.Combine(RootDirectory, "tests", "Parry.Tests", "Parry.Tests.csproj");
        if (File.Exists(testsProject))
        {
            RunChecked(
                "dotnet",
                $"build {Quote(testsProject)} --configuration {Quote(configuration)} --nologo",
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
            $"format {Quote(modProject)} --no-restore",
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

    void RunSmokeCore(string payload, string configuration)
    {
        var normalizedPayload = (payload ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedPayload != "mod" && normalizedPayload != "full")
        {
            Fail($"Invalid payload '{payload}'. Use mod or full.");
        }

        var normalizedConfig = configuration.Equals("Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        var deployConfig = normalizedConfig.Equals("Release", StringComparison.OrdinalIgnoreCase) ? "rel" : "dbg";
        var effectiveFahrenheitRef = ResolveFahrenheitRef(useReleaseRef: false);

        if (normalizedPayload == "full")
        {
            RunBuildProjTarget("Build", normalizedConfig, includeNativeMsbuild: true, fahrenheitRef: effectiveFahrenheitRef);
        }
        else
        {
            RunBuildProjTarget("BuildModOnly", normalizedConfig, includeNativeMsbuild: false, fahrenheitRef: effectiveFahrenheitRef);
        }

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

        if (normalizedPayload == "full")
        {
            var stage0 = Path.Combine(ResolvePath(FahrenheitDir), "artifacts", "deploy", deployConfig, "bin", "fhstage0.exe");
            if (!File.Exists(stage0))
            {
                Fail($"Smoke check failed. Missing stage0 loader: {stage0}");
            }
        }

        Log.Information($"Smoke checks passed for payload={normalizedPayload}, config={normalizedConfig}.");
    }

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
