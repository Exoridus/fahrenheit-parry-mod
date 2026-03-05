using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Serilog;
using static Nuke.Common.Assert;

internal sealed partial class BuildScript
{
    void SetupGhidraCore()
    {
        EnsureJavaInstalled();

        var ghidraRoot = ResolvePath(GhidraDir);
        EnsureDir(ghidraRoot);

        var existingLauncher = FindGhidraLauncher(ghidraRoot);
        if (!string.IsNullOrWhiteSpace(existingLauncher))
        {
            Log.Information($"Ghidra is ready: {existingLauncher}");
            return;
        }

        if (DryRun)
        {
            Log.Information($"[DRY-RUN] Would download latest Ghidra release from {GhidraApi}");
            Log.Information($"[DRY-RUN] Would extract to {ghidraRoot}");
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"ghidra-{Guid.NewGuid():N}");
        var zipPath = Path.Combine(tempRoot, "ghidra.zip");
        var unpackDir = Path.Combine(tempRoot, "unzipped");
        EnsureDir(tempRoot);
        EnsureDir(unpackDir);

        try
        {
            var zipUrl = ResolveLatestGhidraZipUrl(GhidraApi);
            DownloadFile(zipUrl, zipPath);
            ZipFile.ExtractToDirectory(zipPath, unpackDir);

            var launchers = Directory.EnumerateFiles(unpackDir, "ghidraRun.bat", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (launchers.Count == 0)
            {
                Fail($"Downloaded archive did not contain ghidraRun.bat: {zipUrl}");
            }

            var sourceRoot = Path.GetDirectoryName(launchers[0]) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            {
                Fail("Could not resolve extracted Ghidra directory.");
            }

            var folderName = Path.GetFileName(sourceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(folderName))
            {
                folderName = "ghidra";
            }

            var destinationRoot = Path.Combine(ghidraRoot, folderName);
            if (Directory.Exists(destinationRoot))
            {
                Directory.Delete(destinationRoot, recursive: true);
            }

            EnsureDir(destinationRoot);
            CopyDirectoryRecursive(sourceRoot, destinationRoot);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        var launcher = FindGhidraLauncher(ghidraRoot);
        if (string.IsNullOrWhiteSpace(launcher))
        {
            Fail($"Ghidra setup finished but no launcher was found in {ghidraRoot}.");
        }

        Log.Information($"Ghidra ready: {launcher}");
        Log.Information("Start with: build.cmd ghidrastart");
    }

    void StartGhidraCore()
    {
        EnsureJavaInstalled();

        var ghidraRoot = ResolvePath(GhidraDir);
        var launcher = FindGhidraLauncher(ghidraRoot);
        if (string.IsNullOrWhiteSpace(launcher))
        {
            Fail($"Ghidra launcher not found under {ghidraRoot}. Run build.cmd ghidrasetup first.");
        }

        if (DryRun)
        {
            Log.Information($"[DRY-RUN] Would launch: {launcher}");
            return;
        }

        var workingDirectory = Path.GetDirectoryName(launcher) ?? ghidraRoot;
        var psi = new ProcessStartInfo
        {
            FileName = launcher,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true
        };

        var process = Process.Start(psi);
        if (process is null)
        {
            Fail($"Failed to start Ghidra launcher: {launcher}");
        }

        Log.Information($"Started Ghidra: {launcher}");
    }

    static string FindGhidraLauncher(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return string.Empty;
        }

        var candidates = Directory.EnumerateFiles(root, "ghidraRun.bat", SearchOption.AllDirectories)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0)
        {
            return string.Empty;
        }

        return candidates[0];
    }

    static string ResolveLatestGhidraZipUrl(string releaseApiUrl)
    {
        using var client = CreateGitHubHttpClient();
        var json = client.GetStringAsync(releaseApiUrl).GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            Fail($"Invalid GitHub API response: missing assets array. URL={releaseApiUrl}");
        }

        string? preferred = null;
        string? fallback = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(url) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.Contains("PUBLIC", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("src", StringComparison.OrdinalIgnoreCase))
            {
                preferred = url;
                break;
            }

            fallback ??= url;
        }

        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred;
        }

        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return fallback;
        }

        Fail($"No suitable .zip asset found in latest Ghidra release. URL={releaseApiUrl}");
        return string.Empty;
    }
}
