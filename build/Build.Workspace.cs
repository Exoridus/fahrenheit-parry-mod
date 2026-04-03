using Serilog;
using static Nuke.Common.Assert;

internal sealed partial class BuildScript
{
    void WorkspacePruneCore()
    {
        var preset = (Preset ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(preset))
        {
            preset = "safe";
        }

        if (preset != "safe" && preset != "deep")
        {
            Fail($"Invalid --preset '{Preset}'. Use safe or deep.");
        }

        var workspaceRoot = ResolvePath(".workspace");
        if (!Directory.Exists(workspaceRoot))
        {
            Log.Warning($"Workspace directory not found: {workspaceRoot}");
            return;
        }

        var result = new WorkspacePruneResult();
        Log.Information($"Starting workspace prune (preset={preset}, dry-run={DryRun.ToString().ToLowerInvariant()}).");

        DeleteDirectoryMaybeWithAccounting(ResolvePath(".workspace/local-build"), result);
        DeleteDirectoryMaybeWithAccounting(ResolvePath(".workspace/analysis"), result);
        DeleteDirectoryMaybeWithAccounting(ResolvePath(".workspace/fahrenheit/artifacts"), result);
        DeleteFileMaybeWithAccounting(ResolvePath(".workspace/dev.local.json"), result);
        DeleteFileMaybeWithAccounting(ResolvePath(".workspace/discord/_index/export-index.cache.json"), result);

        var discordRoot = ResolvePath(".workspace/discord");
        if (Directory.Exists(discordRoot))
        {
            foreach (var markdownPath in Directory.GetFiles(discordRoot, "*.md", SearchOption.AllDirectories)
                         .Where(path => !IsDiscordHousekeepingPath(discordRoot, path) && !IsDiscordAssetPath(discordRoot, path))
                         .Where(path => TryGetDiscordExportIdFromFileName(Path.GetFileName(path), out _, out _)))
            {
                DeleteFileMaybeWithAccounting(markdownPath, result);
            }
        }

        if (preset == "deep")
        {
            if (Directory.Exists(discordRoot))
            {
                foreach (var guildDir in Directory.GetDirectories(discordRoot, "*_*", SearchOption.TopDirectoryOnly)
                             .Where(path => !IsDiscordHousekeepingPath(discordRoot, path))
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    DeleteDirectoryMaybeWithAccounting(guildDir, result);
                }
            }

            DeleteDirectoryMaybeWithAccounting(ResolvePath(".workspace/data/metamenu"), result);
            DeleteDirectoryMaybeWithAccounting(ResolvePath(".workspace/data/ffx-dataparser"), result);
            DeleteDirectoryMaybeWithAccounting(ResolvePath(".workspace/data/parsedScripts"), result);
            DeleteDirectoryMaybeWithAccounting(ResolvePath(".workspace/tools/ghidra"), result);
        }

        Log.Information(
            $"Workspace prune complete ({preset}). Removed files={result.FilesRemoved}, directories={result.DirectoriesRemoved}, reclaimed={FormatBytes(result.BytesReclaimed)}.");
    }

    void DeleteFileMaybeWithAccounting(string path, WorkspacePruneResult result)
    {
        if (!File.Exists(path))
        {
            return;
        }

        long size = 0;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch
        {
            // Best-effort size accounting.
        }

        if (DryRun)
        {
            Log.Information($"[DRY-RUN] Delete file: {path}");
            result.FilesRemoved++;
            result.BytesReclaimed += Math.Max(0, size);
            return;
        }

        File.Delete(path);
        Log.Information($"Deleted file: {path}");
        result.FilesRemoved++;
        result.BytesReclaimed += Math.Max(0, size);
    }

    void DeleteDirectoryMaybeWithAccounting(string path, WorkspacePruneResult result)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        var size = MeasureDirectoryBytes(path);
        if (DryRun)
        {
            Log.Information($"[DRY-RUN] Delete directory: {path}");
            result.DirectoriesRemoved++;
            result.BytesReclaimed += size;
            return;
        }

        Directory.Delete(path, recursive: true);
        Log.Information($"Deleted directory: {path}");
        result.DirectoriesRemoved++;
        result.BytesReclaimed += size;
    }

    static long MeasureDirectoryBytes(string path)
    {
        try
        {
            return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                .Select(file =>
                {
                    try
                    {
                        return new FileInfo(file).Length;
                    }
                    catch
                    {
                        return 0L;
                    }
                })
                .Sum();
        }
        catch
        {
            return 0;
        }
    }

    static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var units = new[] { "KB", "MB", "GB", "TB" };
        double value = bytes;
        var index = -1;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:F2} {units[index]}";
    }

    sealed class WorkspacePruneResult
    {
        public int FilesRemoved { get; set; }
        public int DirectoriesRemoved { get; set; }
        public long BytesReclaimed { get; set; }
    }
}
