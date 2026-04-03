using Serilog;

internal sealed partial class BuildScript
{
    void DeleteFileMaybeWithAccounting(string path, CleanupAccountingResult result)
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
            if (IsLogVerbosityAtLeast(BuildLogVerbosity.Detailed))
            {
                Log.Information($"[DRY-RUN] Delete file: {path}");
            }

            result.FilesRemoved++;
            result.BytesReclaimed += Math.Max(0, size);
            return;
        }

        File.Delete(path);
        if (IsLogVerbosityAtLeast(BuildLogVerbosity.Detailed))
        {
            Log.Information($"Deleted file: {path}");
        }

        result.FilesRemoved++;
        result.BytesReclaimed += Math.Max(0, size);
    }

    void DeleteDirectoryMaybeWithAccounting(string path, CleanupAccountingResult result)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        var size = MeasureDirectoryBytes(path);
        if (DryRun)
        {
            if (IsLogVerbosityAtLeast(BuildLogVerbosity.Detailed))
            {
                Log.Information($"[DRY-RUN] Delete directory: {path}");
            }

            result.DirectoriesRemoved++;
            result.BytesReclaimed += size;
            return;
        }

        Directory.Delete(path, recursive: true);
        if (IsLogVerbosityAtLeast(BuildLogVerbosity.Detailed))
        {
            Log.Information($"Deleted directory: {path}");
        }

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

    sealed class CleanupAccountingResult
    {
        public int FilesRemoved { get; set; }
        public int DirectoriesRemoved { get; set; }
        public long BytesReclaimed { get; set; }
    }
}
