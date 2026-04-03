using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nuke.Common;
using Serilog;
using static Nuke.Common.Assert;

internal sealed partial class BuildScript
{
    static readonly Regex DiscordChannelLineRegex = new(
        @"^\s*(?<thread>\*)?\s*(?<id>\d+)\s+\|\s+(?<label>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex DiscordGuildLineRegex = new(
        @"^\s*(?<id>\d+)\s+\|\s+(?<name>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex DiscordExportFileRegex = new(
        @"(?:_|[(])(?<id>\d{17,20})\)?\.(?:json|md)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    static readonly Regex DiscordUrlRegex = new(
        @"https?://[^\s<>""'\]\)]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    static readonly HttpClient DiscordFetchHttpClient = CreateDiscordFetchHttpClient();
    const int DiscordCliTimeoutMs = 30 * 60 * 1000; // 30 minutes
    const string DiscordOutputRootRelative = ".workspace/discord";
    const string DiscordConfigRelativePath = ".workspace/discord/config.local.json";
    const string DiscordExportIndexCacheRelativePath = ".workspace/discord/_index/export-index.cache.json";
    const string DiscordPrimaryToolsRelativeDir = ".workspace/tools";
    const string DiscordOcrOutRelativeDir = ".workspace/analysis/discord-ocr";
    const int DiscordTesseractMinDimension = 640;
    const int MaxFetchedTextBytes = 800000;
    const int MaxRenderedRefCharsPerMessage = 16000;

    [Parameter(Name = "guild")] readonly string Guild = string.Empty;
    [Parameter(Name = "channels")] readonly string Channels = string.Empty;
    [Parameter(Name = "discord-utc")] readonly bool? DiscordUtc;
    [Parameter(Name = "discord-cleanup-staging")] readonly bool DiscordCleanupStaging = true;
    [Parameter(Name = "discord-register-guild")] readonly bool DiscordRegisterGuild = false;

    Target DiscordSync => _ => _
        .Executes(DiscordSyncCore);

    static string GetSlug(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unknown";
        var cleanName = Regex.Replace(name, @"\s\(\d+\)$", "");
        var slug = Regex.Replace(cleanName.ToLowerInvariant(), @"[^a-z0-9]", "-");
        slug = Regex.Replace(slug, @"-+", "-");
        return slug.Trim('-');
    }

    string ResolveDiscordOutputRoot() => ResolvePath(DiscordOutputRootRelative);
    string ResolveDiscordConfigPath() => ResolvePath(DiscordConfigRelativePath);
    string ResolveDiscordExportIndexCachePath() => ResolvePath(DiscordExportIndexCacheRelativePath);

    string ResolveDiscordToolDirectory(string toolName) => ResolvePath(Path.Combine(DiscordPrimaryToolsRelativeDir, toolName));

    void CleanupRedundantDiscordFiles(string outputRoot, string currentGuildId)
    {
        Log.Information($"Cleaning up redundant files for guild {currentGuildId}...");
        var guildDirs = Directory.GetDirectories(outputRoot, $"*_{currentGuildId}", SearchOption.TopDirectoryOnly);
        foreach (var guildDir in guildDirs)
        {
            var files = Directory.GetFiles(guildDir, "*.json", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(guildDir, "*.md", SearchOption.AllDirectories))
                .ToList();

            var byId = new Dictionary<string, List<string>>();
            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                if (TryGetDiscordExportIdFromFileName(fileName, out var channelId, out var extension))
                {
                    var id = $"{channelId}|{extension}";
                    if (!byId.ContainsKey(id)) byId[id] = new List<string>();
                    byId[id].Add(file);
                }
            }

            foreach (var idGroup in byId.Values)
            {
                if (idGroup.Count <= 1) continue;

                var sorted = idGroup
                    .OrderByDescending(f => ScoreDiscordCanonicalExportPath(guildDir, f))
                    .ThenBy(f => Path.GetDirectoryName(f)?.Length ?? 0)
                    .ThenBy(f => Path.GetFileName(f).Length)
                    .ThenByDescending(f => new FileInfo(f).Length)
                    .ToList();
                var toKeep = sorted[0];
                foreach (var toDelete in sorted.Skip(1))
                {
                    Log.Information($"  Deleting redundant file: {Path.GetFileName(toDelete)} (kept {Path.GetFileName(toKeep)})");
                    File.Delete(toDelete);
                }
            }
        }
    }

    static int ScoreDiscordCanonicalExportPath(string guildRoot, string path)
    {
        var score = 0;
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        if (string.Equals(directory, guildRoot, StringComparison.OrdinalIgnoreCase))
        {
            score += 5;
        }

        var fileName = Path.GetFileName(path);
        if (Regex.IsMatch(fileName, "^[a-z0-9-]+_[0-9]{17,20}\\.(json|md)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
        {
            score += 5;
        }

        var relative = Path.GetRelativePath(guildRoot, path);
        if (!relative.Contains(Path.DirectorySeparatorChar) && !relative.Contains(Path.AltDirectorySeparatorChar))
        {
            score += 2;
        }

        return score;
    }

    Target FixDiscordExtensions => _ => _
        .Executes(() =>
        {
            var outputRoot = ResolveDiscordOutputRoot();
            if (!Directory.Exists(outputRoot))
            {
                Log.Warning($"Discord output directory not found: {outputRoot}");
                return;
            }

            Log.Information($"Fixing Discord asset extensions in {outputRoot}...");

            var jsonFiles = Directory.GetFiles(outputRoot, "*.json", SearchOption.AllDirectories)
                .Where(path => !IsDiscordHousekeepingPath(outputRoot, path) && !IsDiscordAssetPath(outputRoot, path))
                .ToList();

            var assetDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var jsonPath in jsonFiles)
            {
                var assetDir = GetDiscordAssetDirectory(jsonPath);
                if (Directory.Exists(assetDir))
                {
                    assetDirs.Add(assetDir);
                }
            }

            foreach (var guildDir in Directory.GetDirectories(outputRoot, "*_*", SearchOption.TopDirectoryOnly))
            {
                var mediaDir = Path.Combine(guildDir, "Media");
                if (Directory.Exists(mediaDir))
                {
                    assetDirs.Add(mediaDir);
                }
            }

            foreach (var assetDir in assetDirs)
            {
                FixAssetExtensionsInDirectory(outputRoot, assetDir, jsonFiles);
            }

            Log.Information("Finished fixing Discord asset extensions.");
        });

    Target DiscordEnrich => _ => _
        .Executes(() =>
        {
            var outputRoot = ResolveDiscordOutputRoot();
            var workspaceConfig = LoadWorkspaceConfig();
            var discordConfig = LoadDiscordConfig();
            if (!Directory.Exists(outputRoot))
            {
                Log.Warning($"Discord output directory not found: {outputRoot}");
                return;
            }

            var guildDirs = Directory.GetDirectories(outputRoot, "*_*", SearchOption.TopDirectoryOnly)
                .Where(d => !IsDiscordHousekeepingPath(outputRoot, d))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (guildDirs.Count == 0)
            {
                Log.Warning("No guild directories found for Discord enrichment.");
                return;
            }

            Log.Information($"Starting Discord enrichment pipeline for {guildDirs.Count} guild(s).");

            Parallel.ForEach(guildDirs, new ParallelOptions { MaxDegreeOfParallelism = 2 }, guildDir =>
            {
                EnrichDiscordGuild(guildDir, workspaceConfig);
                GenerateDiscordReferencesForGuild(guildDir, workspaceConfig, discordConfig.BlacklistedChannelIds);
                RegenerateDiscordMarkdownForGuild(guildDir);
            });

            Log.Information("Discord enrichment pipeline complete.");
        });

    static bool IsSupportedOcrImageFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".tif", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase);
    }

    void EnrichDiscordGuild(string guildDir, WorkspaceConfig workspaceConfig)
    {
        if (!Directory.Exists(guildDir))
        {
            return;
        }

        var guildName = Path.GetFileName(guildDir);
        Log.Information($"Enriching {guildName}...");
        RunDiscordOcrPipeline(guildDir, workspaceConfig, Full);
    }

    void RunDiscordEnrichmentForGuild(string guildDir, WorkspaceConfig workspaceConfig)
    {
        if (!Directory.Exists(guildDir))
        {
            return;
        }

        EnrichDiscordGuild(guildDir, workspaceConfig);
    }

    void DiscordSyncCore()
    {
        var outputRoot = ResolveDiscordOutputRoot();
        var workspaceConfig = LoadWorkspaceConfig();
        var baseSettings = ResolveDiscordSettings(workspaceConfig);
        var exportIndexCachePath = ResolveDiscordExportIndexCachePath();
        var exportIndex = LoadDiscordExportIndexCache(exportIndexCachePath);
        if (exportIndex.Count == 0 && Directory.Exists(outputRoot))
        {
            Log.Information("Discord export index cache missing or empty. Building initial cache from disk...");
            exportIndex = BuildDiscordExportIndex(outputRoot);
            SaveDiscordExportIndexCache(exportIndexCachePath, exportIndex);
        }

        var guildsToSync = new List<string>();

        if (!string.IsNullOrWhiteSpace(Guild))
        {
            guildsToSync.Add(Guild.Trim());
        }
        else if (baseSettings.GuildIds.Count > 0)
        {
            guildsToSync.AddRange(baseSettings.GuildIds);
            Log.Information($"No guild specified; syncing {guildsToSync.Count} guilds from configuration.");
        }
        else
        {
            Fail("Missing --guild <serverId> and no guilds found in configuration.");
        }

        var cliPath = ResolveDiscordCliPath();

        foreach (var currentGuildId in guildsToSync)
        {
            Log.Information($"--- Starting sync for Guild {currentGuildId} ---");

            var guildName = ResolveDiscordGuildName(cliPath, currentGuildId, baseSettings.Token);
            var serverSlug = GetSlug(guildName);
            var guildRoot = Path.Combine(outputRoot, $"{serverSlug}_{currentGuildId}");
            var settings = baseSettings;
            if (string.IsNullOrWhiteSpace(settings.MediaDirectory))
            {
                settings = settings with { MediaDirectory = Path.Combine(guildRoot, "Media") };
            }

            var stagingRoot = Path.Combine(outputRoot, "_staging", $"{serverSlug}_{currentGuildId}");

            EnsureDir(outputRoot);
            EnsureDir(stagingRoot);
            if (!string.IsNullOrWhiteSpace(settings.MediaDirectory))
            {
                EnsureDir(settings.MediaDirectory);
            }

            var targets = ResolveDiscordTargets(cliPath, currentGuildId, settings);
            if (targets.Count == 0)
            {
                Log.Warning($"No channels were selected or discovered for guild {currentGuildId}.");
                continue;
            }

            var syncSucceeded = false;
            var skippedChannels = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            var assetDirsToFix = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var newestMessageIdIndex = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

            try
            {
                Parallel.ForEach(targets, new ParallelOptions { MaxDegreeOfParallelism = 2 }, target =>
                {
                    var channelSlug = GetSlug(target.Label);
                    var fileName = $"{channelSlug}_{target.ChannelId}.json";
                    var finalPath = Path.Combine(guildRoot, fileName);
                    var existingPath = string.Empty;
                    var afterValue = string.Empty;
                    var cachedEntry = GetDiscordIndexedExportEntry(exportIndex, target.ChannelId);
                    if (cachedEntry.HasValue)
                    {
                        existingPath = cachedEntry.Value.Path;
                        afterValue = cachedEntry.Value.NewestMessageId;
                    }

                    if (!cachedEntry.HasValue && !Full && File.Exists(finalPath))
                    {
                        existingPath = finalPath;
                    }

                    if (!Full && cachedEntry.HasValue && cachedEntry.Value.Inaccessible)
                    {
                        skippedChannels.TryAdd(target.ChannelId, $"cached inaccessible: {cachedEntry.Value.InaccessibleReason}");
                        return;
                    }

                    var mode = "full";
                    var stageOutput = Path.Combine(stagingRoot, fileName);

                    if (!Full && !string.IsNullOrWhiteSpace(existingPath))
                    {
                        if (string.IsNullOrWhiteSpace(afterValue))
                        {
                            afterValue = newestMessageIdIndex.GetOrAdd(
                                target.ChannelId,
                                _ => ReadNewestMessageId(existingPath));
                        }
                        else
                        {
                            newestMessageIdIndex[target.ChannelId] = afterValue;
                        }

                        if (!string.IsNullOrWhiteSpace(afterValue))
                        {
                            mode = "delta";
                        }
                    }

                    EnsureDir(Path.GetDirectoryName(stageOutput) ?? string.Empty);
                    DeleteDiscordStageOutputsForChannel(stagingRoot, target.ChannelId);

                    Log.Information($"{target.Label} [{target.ChannelId}] -> {mode}");

                    var exportOutcome = ExportDiscordChannel(
                        cliPath: cliPath,
                        guildId: currentGuildId,
                        channelId: target.ChannelId,
                        settings: settings,
                        stageOutput: stageOutput,
                        afterValue: mode == "delta" ? afterValue : null);

                    if (exportOutcome.Status is DiscordExportStatus.SkippedForbidden or DiscordExportStatus.SkippedUnsupported)
                    {
                        skippedChannels.TryAdd(target.ChannelId, exportOutcome.Message);
                        UpdateDiscordExportIndexInaccessible(exportIndex, target.ChannelId, currentGuildId, exportOutcome.Message);
                        return;
                    }

                    var stagePath = ResolveStageExportPath(stagingRoot, stageOutput, target.ChannelId);
                    if (string.IsNullOrWhiteSpace(stagePath) || !File.Exists(stagePath))
                    {
                        if (mode == "delta")
                        {
                            Log.Information($"No new messages for {target.ChannelId}.");
                            return;
                        }

                        Fail($"Export completed without producing an output file for channel {target.ChannelId}.");
                    }

                    EnsureDir(Path.GetDirectoryName(finalPath) ?? string.Empty);

                    if (string.IsNullOrWhiteSpace(existingPath) || mode == "full")
                    {
                        InstallDiscordExport(stagePath, finalPath);
                    }
                    else
                    {
                        MergeDiscordExport(existingPath, stagePath, finalPath);
                        CopyStageAssets(stagePath, finalPath, replaceExisting: false);
                    }

                    NormalizeDiscordExportJson(finalPath);
                    assetDirsToFix.TryAdd(GetDiscordAssetDirectory(finalPath), 0);
                    var newestMessageId = ReadNewestMessageId(finalPath);
                    newestMessageIdIndex[target.ChannelId] = newestMessageId;
                    UpdateDiscordExportIndex(exportIndex, target.ChannelId, currentGuildId, finalPath, newestMessageId);
                });

                foreach (var skipped in skippedChannels.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    RememberBlacklistedChannel(settings, skipped.Key, skipped.Value);
                    Log.Warning($"Skipping inaccessible channel {skipped.Key}: {skipped.Value}");
                }

                CleanupRedundantDiscordFiles(outputRoot, currentGuildId);

                if (!string.IsNullOrWhiteSpace(settings.MediaDirectory))
                {
                    assetDirsToFix.TryAdd(settings.MediaDirectory, 0);
                }

                var jsonFiles = Directory.GetFiles(outputRoot, "*.json", SearchOption.AllDirectories)
                    .Where(path => !IsDiscordHousekeepingPath(outputRoot, path) && !IsDiscordAssetPath(outputRoot, path))
                    .ToList();

                foreach (var assetDir in assetDirsToFix.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    FixAssetExtensionsInDirectory(outputRoot, assetDir, jsonFiles);
                }

                RunDiscordEnrichmentForGuild(guildRoot, workspaceConfig);
                GenerateDiscordReferencesForGuild(guildRoot, workspaceConfig, settings.BlacklistedChannelIds);
                RegenerateDiscordMarkdownForGuild(guildRoot);

                syncSucceeded = true;
                Log.Information($"Discord sync finished for guild {currentGuildId}.");

                if (!Full)
                {
                    Log.Information("Delta mode only captures new messages after the newest stored message ID. Use --full periodically to reconcile edits, deletions, and older reaction changes.");
                }

                if (!baseSettings.GuildIds.Contains(currentGuildId) && DiscordRegisterGuild)
                {
                    PersistDiscordGuildId(settings.ConfigPath, currentGuildId);
                    baseSettings.GuildIds.Add(currentGuildId);
                }

                SaveDiscordExportIndexCache(exportIndexCachePath, exportIndex);
            }
            finally
            {
                if (syncSucceeded && DiscordCleanupStaging)
                {
                    CleanupDiscordStaging(outputRoot, stagingRoot);
                }
            }
        }
    }

    void PersistDiscordGuildId(string configPath, string guildId)
    {
        var config = LoadDiscordConfig();
        if (config.GuildIds.Any(x => string.Equals(x, guildId, StringComparison.Ordinal)))
        {
            return;
        }

        config.GuildIds.Add(guildId);
        SaveDiscordConfig(configPath, config);
        Log.Information($"Added guild {guildId} to Discord configuration.");
    }

    static bool IsDiscordAssetPath(string outputRoot, string candidatePath)
    {
        var relativePath = Path.GetRelativePath(outputRoot, candidatePath);
        var segments = relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(s =>
            s.Equals("Media", StringComparison.OrdinalIgnoreCase)
            || s.EndsWith("_Files", StringComparison.OrdinalIgnoreCase));
    }

    List<DiscordChannelTarget> ResolveDiscordTargets(string cliPath, string guildId, DiscordSyncSettings settings)
    {
        var requestedChannelIds = ParseRequestedChannelIds(Channels);
        List<DiscordChannelTarget> targets;
        if (requestedChannelIds.Count > 0)
        {
            targets = requestedChannelIds
                .Select(id => new DiscordChannelTarget(id, id))
                .ToList();
        }
        else
        {
            targets = DiscoverDiscordTargets(cliPath, guildId, settings);
        }

        var filteredTargets = targets
            .Where(x => !settings.BlacklistedChannelIds.Contains(x.ChannelId))
            .ToList();

        var skippedByBlacklist = targets.Count - filteredTargets.Count;
        if (skippedByBlacklist > 0)
        {
            Log.Information($"Filtered {skippedByBlacklist} blacklisted channel(s) before Discord sync.");
        }

        return filteredTargets;
    }

    List<DiscordChannelTarget> DiscoverDiscordTargets(string cliPath, string guildId, DiscordSyncSettings settings)
    {
        var args = new StringBuilder();
        args.Append("channels");
        args.Append(" --guild ").Append(guildId);
        args.Append(" --include-vc true");
        args.Append(" --include-threads All");
        args.Append(" --respect-rate-limits true");

        var result = RunDiscordCli(
            cliPath,
            args.ToString(),
            "List Discord channels",
            settings.Token,
            silent: true);

        if (result.ExitCode != 0)
        {
            Fail(
                $"List Discord channels failed with code {result.ExitCode}.{Environment.NewLine}" +
                $"STDERR:{Environment.NewLine}{result.StdErr}{Environment.NewLine}" +
                $"STDOUT:{Environment.NewLine}{result.StdOut}");
        }

        var targets = new List<DiscordChannelTarget>();
        foreach (var rawLine in result.StdOut.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = DiscordChannelLineRegex.Match(rawLine);
            if (!match.Success)
            {
                continue;
            }

            var channelId = match.Groups["id"].Value.Trim();
            if (string.IsNullOrWhiteSpace(channelId))
            {
                continue;
            }

            var label = match.Groups["label"].Value.Trim();
            targets.Add(new DiscordChannelTarget(channelId, label));
        }

        return targets
            .DistinctBy(x => x.ChannelId)
            .OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    DiscordExportOutcome ExportDiscordChannel(string cliPath, string guildId, string channelId, DiscordSyncSettings settings, string stageOutput, string? afterValue)
    {
        var args = new StringBuilder();
        args.Append("export");
        args.Append(" --channel ").Append(channelId);
        args.Append(" --output ").Append(Quote(stageOutput));
        args.Append(" --format Json");
        args.Append(" --media");
        args.Append(" --reuse-media");

        if (!string.IsNullOrWhiteSpace(settings.MediaDirectory))
        {
            args.Append(" --media-dir ").Append(Quote(settings.MediaDirectory));
        }

        if (settings.Utc)
        {
            args.Append(" --utc");
        }

        args.Append(" --respect-rate-limits true");

        if (!string.IsNullOrWhiteSpace(afterValue))
        {
            args.Append(" --after ").Append(Quote(afterValue));
        }

        var result = RunDiscordCli(
            cliPath,
            args.ToString(),
            $"Export Discord channel {channelId} from guild {guildId}",
            settings.Token,
            silent: true);

        var combinedError = $"{result.StdErr}{Environment.NewLine}{result.StdOut}";
        if (result.ExitCode != 0 && combinedError.Contains("forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return new DiscordExportOutcome(DiscordExportStatus.SkippedForbidden, "forbidden");
        }

        if (result.ExitCode != 0 &&
            combinedError.Contains("forum", StringComparison.OrdinalIgnoreCase) &&
            combinedError.Contains("cannot be exported directly", StringComparison.OrdinalIgnoreCase))
        {
            return new DiscordExportOutcome(DiscordExportStatus.SkippedUnsupported, "forum container");
        }

        if (result.ExitCode != 0)
        {
            Fail(
                $"Export Discord channel {channelId} failed with code {result.ExitCode}.{Environment.NewLine}" +
                $"STDERR:{Environment.NewLine}{result.StdErr}{Environment.NewLine}" +
                $"STDOUT:{Environment.NewLine}{result.StdOut}");
        }

        return new DiscordExportOutcome(DiscordExportStatus.Success, string.Empty);
    }

    string ResolveDiscordCliPath()
    {
        var toolDir = ResolveDiscordToolDirectory("DiscordChatExporter");
        var cliPath = Path.Combine(toolDir, "DiscordChatExporter.Cli.exe");
        if (!File.Exists(cliPath))
        {
            var expected = ResolvePath(Path.Combine(DiscordPrimaryToolsRelativeDir, "DiscordChatExporter", "DiscordChatExporter.Cli.exe"));
            Fail($"DiscordChatExporter CLI not found at: {expected}");
        }

        return cliPath;
    }

    string ResolveDiscordGuildName(string cliPath, string guildId, string token)
    {
        var result = RunDiscordCli(
            cliPath,
            "guilds --respect-rate-limits true",
            $"Resolve Discord guild name for {guildId}",
            token,
            silent: true);

        if (result.ExitCode != 0)
        {
            return string.Empty;
        }

        foreach (var rawLine in result.StdOut.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = DiscordGuildLineRegex.Match(rawLine);
            if (!match.Success)
            {
                continue;
            }

            if (!string.Equals(match.Groups["id"].Value.Trim(), guildId, StringComparison.Ordinal))
            {
                continue;
            }

            return match.Groups["name"].Value.Trim();
        }

        return string.Empty;
    }

    DiscordSyncSettings ResolveDiscordSettings(WorkspaceConfig workspaceConfig)
    {
        var config = LoadDiscordConfig();
        var token = ResolveConfigEnvValue(workspaceConfig.DiscordToken, nameof(WorkspaceConfig.DiscordToken));

        if (string.IsNullOrWhiteSpace(token))
        {
            Fail(
                $"Missing Discord token. Set 'DiscordToken' in '{WorkspaceConfigPath}'.");
        }

        var utc = DiscordUtc ?? false;
        var blacklistedChannelIds = config.BlacklistedChannelIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var guildIds = config.GuildIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new DiscordSyncSettings(
            Token: token,
            MediaDirectory: string.Empty,
            Utc: utc,
            ConfigPath: ResolveDiscordConfigPath(),
            BlacklistedChannelIds: blacklistedChannelIds,
            GuildIds: guildIds);
    }

    DiscordWorkflowConfig LoadDiscordConfig()
    {
        var configPath = ResolveDiscordConfigPath();
        var config = new DiscordWorkflowConfig();
        if (!File.Exists(configPath))
        {
            return config;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = doc.RootElement;
            ValidateDiscordConfigSchema(root, configPath);

            if (root.TryGetProperty("Blacklist", out var blacklistElement))
            {
                foreach (var channelId in blacklistElement.EnumerateArray()
                             .Select(x => x.GetString()?.Trim() ?? string.Empty)
                             .Where(x => !string.IsNullOrWhiteSpace(x))
                             .Distinct(StringComparer.Ordinal))
                {
                    config.BlacklistedChannelIds.Add(channelId);
                }
            }

            if (root.TryGetProperty("Guilds", out var guildsElement))
            {
                foreach (var guildId in guildsElement.EnumerateArray()
                             .Select(x => x.GetString()?.Trim() ?? string.Empty)
                             .Where(x => !string.IsNullOrWhiteSpace(x))
                             .Distinct(StringComparer.Ordinal))
                {
                    config.GuildIds.Add(guildId);
                }
            }

            return config;
        }
        catch (Exception ex)
        {
            Fail($"Failed to parse Discord config '{configPath}': {ex.Message}");
            return new DiscordWorkflowConfig();
        }
    }

    static void ValidateDiscordConfigSchema(JsonElement root, string path)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            Fail($"Discord config '{path}' must be a JSON object.");
        }

        var allowedRootKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "Blacklist",
            "Guilds"
        };

        foreach (var property in root.EnumerateObject())
        {
            if (!allowedRootKeys.Contains(property.Name))
            {
                Fail($"Unsupported Discord config key '{property.Name}' in '{path}'. Use only: Blacklist, Guilds.");
            }
        }

        if (root.TryGetProperty("Blacklist", out var blacklistElement))
        {
            if (blacklistElement.ValueKind != JsonValueKind.Array
                || blacklistElement.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
            {
                Fail($"Discord config '{path}' property 'Blacklist' must be an array of strings.");
            }
        }

        if (root.TryGetProperty("Guilds", out var guildsElement))
        {
            if (guildsElement.ValueKind != JsonValueKind.Array
                || guildsElement.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
            {
                Fail($"Discord config '{path}' property 'Guilds' must be an array of strings.");
            }
        }
    }

    static List<string> ParseRequestedChannelIds(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw
            .Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    void SaveDiscordConfig(string configPath, DiscordWorkflowConfig config)
    {
        EnsureDir(Path.GetDirectoryName(configPath) ?? string.Empty);

        var normalizedBlacklist = config.BlacklistedChannelIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        var normalizedGuilds = config.GuildIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var payload = new DiscordWorkflowConfig
        {
            BlacklistedChannelIds = normalizedBlacklist,
            GuildIds = normalizedGuilds
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        var output = JsonSerializer.Serialize(payload, options);
        File.WriteAllText(configPath, output + Environment.NewLine, Utf8NoBom);
    }

    void RememberBlacklistedChannel(DiscordSyncSettings settings, string channelId, string reason)
    {
        if (string.IsNullOrWhiteSpace(channelId) || settings.BlacklistedChannelIds.Contains(channelId))
        {
            return;
        }

        settings.BlacklistedChannelIds.Add(channelId);
        PersistDiscordBlacklist(settings.ConfigPath, settings.BlacklistedChannelIds);
        Log.Information($"Added channel {channelId} to Discord blacklist ({reason}).");
    }

    void PersistDiscordBlacklist(string configPath, IEnumerable<string> blacklistedChannelIds)
    {
        var config = LoadDiscordConfig();
        config.BlacklistedChannelIds = blacklistedChannelIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        SaveDiscordConfig(configPath, config);
    }

    static string ResolveStageExportPath(string stagingRoot, string stageOutput, string channelId)
    {
        if (File.Exists(stageOutput))
        {
            return stageOutput;
        }

        if (!Directory.Exists(stagingRoot))
        {
            return string.Empty;
        }

        var matches = Directory
            .GetFiles(stagingRoot, "*.json", SearchOption.AllDirectories)
            .Where(path => IsDiscordExportForChannel(path, channelId))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();

        return matches.FirstOrDefault() ?? string.Empty;
    }

    static void DeleteDiscordStageOutputsForChannel(string stagingRoot, string channelId)
    {
        if (!Directory.Exists(stagingRoot))
        {
            return;
        }

        foreach (var path in Directory
                     .GetFiles(stagingRoot, "*.json", SearchOption.AllDirectories)
                     .Where(path => IsDiscordExportForChannel(path, channelId)))
        {
            File.Delete(path);
        }
    }

    static ConcurrentDictionary<string, DiscordExportIndexEntry> BuildDiscordExportIndex(string outputRoot)
    {
        var index = new ConcurrentDictionary<string, DiscordExportIndexEntry>(StringComparer.Ordinal);
        if (!Directory.Exists(outputRoot))
        {
            return index;
        }

        foreach (var path in Directory.GetFiles(outputRoot, "*.json", SearchOption.AllDirectories))
        {
            if (IsDiscordHousekeepingPath(outputRoot, path) || IsDiscordAssetPath(outputRoot, path))
            {
                continue;
            }

            var fileName = Path.GetFileName(path);
            if (!TryGetDiscordExportIdFromFileName(fileName, out var channelId, out _))
            {
                continue;
            }

            var entry = new DiscordExportIndexEntry(
                Path: path,
                LastWriteUtcTicks: File.GetLastWriteTimeUtc(path).Ticks,
                NewestMessageId: ReadNewestMessageId(path),
                GuildId: TryReadGuildId(path),
                Inaccessible: false,
                InaccessibleReason: string.Empty);
            index.AddOrUpdate(
                channelId,
                entry,
                (_, current) => entry.LastWriteUtcTicks > current.LastWriteUtcTicks ? entry : current);
        }

        return index;
    }

    static DiscordExportIndexEntry? GetDiscordIndexedExportEntry(ConcurrentDictionary<string, DiscordExportIndexEntry> index, string channelId)
    {
        if (!index.TryGetValue(channelId, out var entry))
        {
            return null;
        }

        if (entry.Inaccessible)
        {
            return entry;
        }

        if (File.Exists(entry.Path))
        {
            return entry;
        }

        index.TryRemove(channelId, out _);
        return null;
    }

    static void UpdateDiscordExportIndex(
        ConcurrentDictionary<string, DiscordExportIndexEntry> index,
        string channelId,
        string guildId,
        string path,
        string newestMessageId)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var entry = new DiscordExportIndexEntry(
            Path: path,
            LastWriteUtcTicks: File.GetLastWriteTimeUtc(path).Ticks,
            NewestMessageId: newestMessageId ?? string.Empty,
            GuildId: guildId ?? string.Empty,
            Inaccessible: false,
            InaccessibleReason: string.Empty);
        index.AddOrUpdate(
            channelId,
            entry,
            (_, current) => entry.LastWriteUtcTicks >= current.LastWriteUtcTicks ? entry : current);
    }

    static void UpdateDiscordExportIndexInaccessible(
        ConcurrentDictionary<string, DiscordExportIndexEntry> index,
        string channelId,
        string guildId,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return;
        }

        var entry = new DiscordExportIndexEntry(
            Path: string.Empty,
            LastWriteUtcTicks: DateTime.UtcNow.Ticks,
            NewestMessageId: string.Empty,
            GuildId: guildId ?? string.Empty,
            Inaccessible: true,
            InaccessibleReason: (reason ?? string.Empty).Trim());
        index[channelId] = entry;
    }

    ConcurrentDictionary<string, DiscordExportIndexEntry> LoadDiscordExportIndexCache(string cachePath)
    {
        var result = new ConcurrentDictionary<string, DiscordExportIndexEntry>(StringComparer.Ordinal);
        if (!File.Exists(cachePath))
        {
            return result;
        }

        try
        {
            var json = File.ReadAllText(cachePath);
            var cache = JsonSerializer.Deserialize<DiscordExportIndexCacheFile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = false
            });
            if (cache?.Channels is null)
            {
                return result;
            }

            foreach (var pair in cache.Channels)
            {
                var channelId = (pair.Key ?? string.Empty).Trim();
                var row = pair.Value;
                if (string.IsNullOrWhiteSpace(channelId) || row is null)
                {
                    continue;
                }

                var path = NormalizePathOrEmpty(row.Path);
                var inaccessible = row.Inaccessible;
                if (!inaccessible && string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (!inaccessible && !File.Exists(path))
                {
                    continue;
                }

                result[channelId] = new DiscordExportIndexEntry(
                    Path: inaccessible ? string.Empty : path,
                    LastWriteUtcTicks: row.LastWriteUtcTicks,
                    NewestMessageId: (row.NewestMessageId ?? string.Empty).Trim(),
                    GuildId: (row.GuildId ?? string.Empty).Trim(),
                    Inaccessible: inaccessible,
                    InaccessibleReason: (row.InaccessibleReason ?? string.Empty).Trim());
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to load Discord export index cache '{cachePath}': {ex.Message}");
        }

        return result;
    }

    void SaveDiscordExportIndexCache(string cachePath, ConcurrentDictionary<string, DiscordExportIndexEntry> index)
    {
        try
        {
            var payload = new DiscordExportIndexCacheFile
            {
                UpdatedAtUtc = DateTime.UtcNow.ToString("O"),
                Channels = index
                    .OrderBy(x => x.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        x => x.Key,
                        x => new DiscordExportIndexCacheEntry
                        {
                            Path = x.Value.Path,
                            LastWriteUtcTicks = x.Value.LastWriteUtcTicks,
                            NewestMessageId = x.Value.NewestMessageId,
                            GuildId = x.Value.GuildId,
                            Inaccessible = x.Value.Inaccessible,
                            InaccessibleReason = x.Value.InaccessibleReason
                        },
                        StringComparer.Ordinal)
            };

            EnsureDir(Path.GetDirectoryName(cachePath) ?? string.Empty);
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(cachePath, json + Environment.NewLine, Utf8NoBom);
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to save Discord export index cache '{cachePath}': {ex.Message}");
        }
    }

    static bool IsDiscordExportForChannel(string path, string channelId)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(channelId))
        {
            return false;
        }

        var fileName = Path.GetFileName(path);
        return TryGetDiscordExportIdFromFileName(fileName, out var discoveredId, out _)
               && string.Equals(discoveredId, channelId, StringComparison.Ordinal);
    }

    static bool TryGetDiscordExportIdFromFileName(string fileName, out string channelId, out string extension)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            channelId = string.Empty;
            extension = string.Empty;
            return false;
        }

        var match = DiscordExportFileRegex.Match(fileName);
        if (!match.Success)
        {
            channelId = string.Empty;
            extension = string.Empty;
            return false;
        }

        channelId = match.Groups["id"].Value;
        extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return !string.IsNullOrWhiteSpace(channelId);
    }

    static bool IsDiscordHousekeepingPath(string outputRoot, string candidatePath)
    {
        var relativePath = Path.GetRelativePath(outputRoot, candidatePath);
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.StartsWith("..", StringComparison.Ordinal))
        {
            return false;
        }

        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var firstSegment = relativePath.Split(separators, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return !string.IsNullOrWhiteSpace(firstSegment) && firstSegment.StartsWith("_", StringComparison.Ordinal);
    }

    static void CleanupDiscordStaging(string outputRoot, string guildStagingRoot)
    {
        if (Directory.Exists(guildStagingRoot))
        {
            Directory.Delete(guildStagingRoot, recursive: true);
        }

        var stagingRoot = Path.Combine(outputRoot, "_staging");
        if (!Directory.Exists(stagingRoot))
        {
            return;
        }

        var hasEntries = Directory.EnumerateFileSystemEntries(stagingRoot).Any();
        if (!hasEntries)
        {
            Directory.Delete(stagingRoot, recursive: false);
        }
    }

    static string ReadNewestMessageId(string exportPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(exportPath));
        if (!doc.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        ulong best = 0;
        var found = false;

        foreach (var message in messages.EnumerateArray())
        {
            if (!message.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var id = idValue.GetString();
            if (!ulong.TryParse(id, out var parsed))
            {
                continue;
            }

            if (!found || parsed > best)
            {
                best = parsed;
                found = true;
            }
        }

        return found ? best.ToString() : string.Empty;
    }

    static string TryReadGuildId(string exportPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(exportPath));
            if (!doc.RootElement.TryGetProperty("guild", out var guild)
                || guild.ValueKind != JsonValueKind.Object
                || !guild.TryGetProperty("id", out var guildIdNode))
            {
                return string.Empty;
            }

            return guildIdNode.ValueKind == JsonValueKind.String
                ? guildIdNode.GetString()?.Trim() ?? string.Empty
                : guildIdNode.ToString().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    static void NormalizeDiscordExportJson(string exportPath)
    {
        var root = JsonNode.Parse(File.ReadAllText(exportPath));
        if (root is null)
        {
            return;
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(exportPath, root.ToJsonString(options) + Environment.NewLine, Utf8NoBom);
    }

    void GenerateDiscordReferencesForGuild(string guildDir, WorkspaceConfig workspaceConfig, IEnumerable<string> blacklistedChannelIds)
    {
        if (!Directory.Exists(guildDir))
        {
            return;
        }

        var outputRoot = ResolveDiscordOutputRoot();
        var exports = Directory.GetFiles(guildDir, "*.json", SearchOption.AllDirectories)
            .Where(path => !IsDiscordHousekeepingPath(outputRoot, path) && !IsDiscordAssetPath(outputRoot, path))
            .Where(path => TryGetDiscordExportIdFromFileName(Path.GetFileName(path), out _, out _))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (exports.Count == 0)
        {
            return;
        }

        var fetchCache = new Dictionary<string, DiscordFetchResult>(StringComparer.OrdinalIgnoreCase);
        var channelSummaries = new List<DiscordServerRefsChannelSummary>();
        var guildId = string.Empty;
        var guildName = string.Empty;

        foreach (var exportPath in exports)
        {
            try
            {
                var channelRefs = BuildDiscordRefsForExport(exportPath, workspaceConfig.FetchRetryCount, fetchCache, out var summary);
                if (string.IsNullOrWhiteSpace(guildId))
                {
                    guildId = summary.GuildId;
                    guildName = summary.GuildName;
                }

                WriteDiscordRefsJsonl(GetDiscordRefsPath(exportPath), channelRefs);
                channelSummaries.Add(summary with { RefCount = channelRefs.Count });
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to generate refs for export {exportPath}: {ex.Message}");
            }
        }

        WriteDiscordServerRefsMetadata(guildDir, guildId, guildName, blacklistedChannelIds, channelSummaries);
    }

    List<DiscordMessageRefEntry> BuildDiscordRefsForExport(
        string exportPath,
        int fetchRetryCount,
        Dictionary<string, DiscordFetchResult> fetchCache,
        out DiscordServerRefsChannelSummary summary)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(exportPath));
        var root = doc.RootElement;
        var guildId = TryGetNestedJsonString(root, "guild", "id");
        var guildName = TryGetNestedJsonString(root, "guild", "name");
        var channelId = TryGetNestedJsonString(root, "channel", "id");
        var channelName = TryGetNestedJsonString(root, "channel", "name");

        var refs = new List<DiscordMessageRefEntry>();
        if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in messages.EnumerateArray())
            {
                var messageId = TryGetJsonString(message, "id");
                if (string.IsNullOrWhiteSpace(messageId))
                {
                    continue;
                }

                var messageTimestamp = TryGetJsonString(message, "timestamp");

                foreach (var imagePath in EnumerateDiscordMessageLocalImagePaths(message, exportPath))
                {
                    var ocrSidecarPath = imagePath + ".ocr.txt";
                    var ocrText = SafeReadAllText(ocrSidecarPath);
                    if (string.IsNullOrWhiteSpace(ocrText))
                    {
                        continue;
                    }

                    refs.Add(new DiscordMessageRefEntry(
                        MessageId: messageId,
                        MessageTimestamp: messageTimestamp,
                        Kind: "ocr",
                        SourceUrl: string.Empty,
                        FetchUrl: string.Empty,
                        RefPath: ToRelativePathFromExport(exportPath, ocrSidecarPath),
                        DetectedLanguage: "text",
                        Confidence: null,
                        Sha256: ComputeSha256Hex(ocrText),
                        Bytes: Encoding.UTF8.GetByteCount(ocrText),
                        Status: "ok",
                        Error: string.Empty));
                }

                var remoteUrls = EnumerateDiscordMessageRemoteUrls(message).ToList();
                foreach (var originalUrl in remoteUrls)
                {
                    if (!TryResolveDiscordRemoteFetchTarget(originalUrl, out var target))
                    {
                        continue;
                    }

                    if (!fetchCache.TryGetValue(target.FetchUrl, out var fetched))
                    {
                        fetched = FetchRemoteSourceText(target.FetchUrl, fetchRetryCount);
                        fetchCache[target.FetchUrl] = fetched;
                    }

                    if (!fetched.Ok || string.IsNullOrWhiteSpace(fetched.Text))
                    {
                        refs.Add(new DiscordMessageRefEntry(
                            MessageId: messageId,
                            MessageTimestamp: messageTimestamp,
                            Kind: "fetched_source",
                            SourceUrl: originalUrl,
                            FetchUrl: target.FetchUrl,
                            RefPath: string.Empty,
                            DetectedLanguage: target.DetectedLanguage,
                            Confidence: null,
                            Sha256: string.Empty,
                            Bytes: 0,
                            Status: "error",
                            Error: fetched.Error ?? "fetch_failed"));
                        continue;
                    }

                    var refsDir = Path.Combine(GetDiscordAssetDirectory(exportPath), "refs");
                    EnsureDir(refsDir);
                    var assetFileName = $"{fetched.Sha256[..16]}.src.txt";
                    var sourcePath = Path.Combine(refsDir, assetFileName);
                    if (!File.Exists(sourcePath))
                    {
                        File.WriteAllText(sourcePath, fetched.Text, Utf8NoBom);
                    }

                    refs.Add(new DiscordMessageRefEntry(
                        MessageId: messageId,
                        MessageTimestamp: messageTimestamp,
                        Kind: "fetched_source",
                        SourceUrl: originalUrl,
                        FetchUrl: target.FetchUrl,
                        RefPath: ToRelativePathFromExport(exportPath, sourcePath),
                        DetectedLanguage: target.DetectedLanguage,
                        Confidence: null,
                        Sha256: fetched.Sha256,
                        Bytes: fetched.Bytes,
                        Status: "ok",
                        Error: string.Empty));
                }
            }
        }

        refs = refs
            .DistinctBy(x => $"{x.MessageId}|{x.Kind}|{x.SourceUrl}|{x.FetchUrl}|{x.RefPath}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => ParseSnowflake(x.MessageId))
            .ThenBy(x => x.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.RefPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.SourceUrl, StringComparer.OrdinalIgnoreCase)
            .ToList();

        summary = new DiscordServerRefsChannelSummary(
            GuildId: guildId,
            GuildName: guildName,
            ChannelId: channelId,
            ChannelName: channelName,
            ExportPath: Path.GetFileName(exportPath),
            RefsPath: Path.GetFileName(GetDiscordRefsPath(exportPath)),
            RefCount: refs.Count,
            LastMessageId: ReadNewestMessageId(exportPath),
            RefKinds: refs
                .GroupBy(x => x.Kind, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase));

        return refs;
    }

    void WriteDiscordRefsJsonl(string refsPath, IReadOnlyList<DiscordMessageRefEntry> entries)
    {
        EnsureDir(Path.GetDirectoryName(refsPath) ?? string.Empty);
        if (entries.Count == 0)
        {
            if (File.Exists(refsPath))
            {
                File.Delete(refsPath);
            }

            return;
        }

        var lines = entries
            .Select(entry => JsonSerializer.Serialize(entry))
            .ToArray();
        var payload = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        File.WriteAllText(refsPath, payload, Utf8NoBom);
    }

    void WriteDiscordServerRefsMetadata(
        string guildDir,
        string guildId,
        string guildName,
        IEnumerable<string> blacklistedChannelIds,
        IReadOnlyList<DiscordServerRefsChannelSummary> channelSummaries)
    {
        var metadataPath = Path.Combine(guildDir, "server.refs.json");
        var payload = new DiscordServerRefsMetadata
        {
            GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
            GuildId = guildId,
            GuildName = guildName,
            Blacklist = (blacklistedChannelIds ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList(),
            Channels = channelSummaries
                .OrderBy(x => x.ChannelName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ChannelId, StringComparer.Ordinal)
                .ToList(),
            TotalRefs = channelSummaries.Sum(x => x.RefCount)
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(metadataPath, json + Environment.NewLine, Utf8NoBom);
    }

    Dictionary<string, List<DiscordMessageRefEntry>> LoadDiscordRefsByMessageId(string exportPath)
    {
        var refsPath = GetDiscordRefsPath(exportPath);
        var refsByMessageId = new Dictionary<string, List<DiscordMessageRefEntry>>(StringComparer.Ordinal);
        if (!File.Exists(refsPath))
        {
            return refsByMessageId;
        }

        foreach (var line in File.ReadLines(refsPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<DiscordMessageRefEntry>(line);
                if (string.IsNullOrWhiteSpace(entry.MessageId))
                {
                    continue;
                }

                if (!refsByMessageId.TryGetValue(entry.MessageId, out var bucket))
                {
                    bucket = [];
                    refsByMessageId[entry.MessageId] = bucket;
                }

                bucket.Add(entry);
            }
            catch
            {
                // Ignore malformed lines and continue.
            }
        }

        return refsByMessageId;
    }

    static string GetDiscordRefsPath(string exportPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(exportPath);
        var parent = Path.GetDirectoryName(exportPath) ?? string.Empty;
        return Path.Combine(parent, fileName + ".refs.jsonl");
    }

    static IEnumerable<string> EnumerateDiscordMessageLocalImagePaths(JsonElement message, string exportPath)
    {
        var baseDir = Path.GetDirectoryName(exportPath) ?? string.Empty;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (message.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
        {
            foreach (var attachment in attachments.EnumerateArray())
            {
                var url = TryGetJsonString(attachment, "url");
                foreach (var path in AddIfFileExistsIterator(url))
                {
                    yield return path;
                }
            }
        }

        if (message.TryGetProperty("embeds", out var embeds) && embeds.ValueKind == JsonValueKind.Array)
        {
            foreach (var embed in embeds.EnumerateArray())
            {
                if (embed.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object)
                {
                    var url = TryGetJsonString(image, "url");
                    foreach (var path in AddIfFileExistsIterator(url))
                    {
                        yield return path;
                    }
                }

                if (embed.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
                {
                    foreach (var imageNode in images.EnumerateArray())
                    {
                        var url = TryGetJsonString(imageNode, "url");
                        foreach (var path in AddIfFileExistsIterator(url))
                        {
                            yield return path;
                        }
                    }
                }
            }
        }

        IEnumerable<string> AddIfFileExistsIterator(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                yield break;
            }

            var normalized = candidate.Trim();
            if (!Path.IsPathRooted(normalized))
            {
                normalized = Path.GetFullPath(Path.Combine(baseDir, normalized));
            }
            else
            {
                normalized = Path.GetFullPath(normalized);
            }

            if (!File.Exists(normalized) || !IsSupportedOcrImageFile(normalized))
            {
                yield break;
            }

            if (seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    static IEnumerable<string> EnumerateDiscordMessageRemoteUrls(JsonElement message)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in ExtractUrlsFromText(TryGetJsonString(message, "content")))
        {
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }

        if (message.TryGetProperty("embeds", out var embeds) && embeds.ValueKind == JsonValueKind.Array)
        {
            foreach (var embed in embeds.EnumerateArray())
            {
                var embedUrl = NormalizeDiscordUrl(TryGetJsonString(embed, "url"));
                if (!string.IsNullOrWhiteSpace(embedUrl) && seen.Add(embedUrl))
                {
                    yield return embedUrl;
                }
            }
        }
    }

    static IEnumerable<string> ExtractUrlsFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return DiscordUrlRegex.Matches(text)
            .Select(match => NormalizeDiscordUrl(match.Value))
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    static string NormalizeDiscordUrl(string url)
    {
        var value = (url ?? string.Empty).Trim();
        while (value.Length > 0 && ".,;:)]}>".Contains(value[^1]))
        {
            value = value[..^1];
        }

        return value;
    }

    static bool TryResolveDiscordRemoteFetchTarget(string url, out DiscordRemoteFetchTarget target)
    {
        target = default;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var normalized = NormalizeDiscordUrl(url);
        var fetchUrl = normalized;
        var sourceType = "direct";

        if (TryConvertGitHubBlobToRaw(normalized, out var blobRaw))
        {
            fetchUrl = blobRaw;
            sourceType = "github_blob_raw";
        }
        else if (TryConvertGitHubCommitToPatch(normalized, out var commitPatch))
        {
            fetchUrl = commitPatch;
            sourceType = "github_commit_patch";
        }
        else if (TryConvertGitHubPullToPatch(normalized, out var pullPatch))
        {
            fetchUrl = pullPatch;
            sourceType = "github_pull_patch";
        }
        else if (TryConvertGistPageToRaw(normalized, out var gistRaw))
        {
            fetchUrl = gistRaw;
            sourceType = "gist_raw";
        }
        else if (!IsFetchableCodeUrl(normalized))
        {
            return false;
        }

        target = new DiscordRemoteFetchTarget(
            SourceUrl: normalized,
            FetchUrl: fetchUrl,
            SourceType: sourceType,
            DetectedLanguage: DetectLanguageFromUrl(fetchUrl));
        return true;
    }

    static bool TryConvertGitHubBlobToRaw(string url, out string rawUrl)
    {
        rawUrl = string.Empty;
        var match = Regex.Match(
            url.Split('#')[0],
            @"^https?://github\.com/([^/]+)/([^/]+)/blob/([^/]+)/(.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        rawUrl = $"https://raw.githubusercontent.com/{match.Groups[1].Value}/{match.Groups[2].Value}/{match.Groups[3].Value}/{match.Groups[4].Value}";
        return true;
    }

    static bool TryConvertGitHubCommitToPatch(string url, out string patchUrl)
    {
        patchUrl = string.Empty;
        var normalized = url.Split('#')[0];
        var match = Regex.Match(
            normalized,
            @"^https?://github\.com/[^/]+/[^/]+/commit/[0-9a-fA-F]+$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        patchUrl = normalized + ".patch";
        return true;
    }

    static bool TryConvertGitHubPullToPatch(string url, out string patchUrl)
    {
        patchUrl = string.Empty;
        var normalized = url.Split('#')[0];
        var match = Regex.Match(
            normalized,
            @"^https?://github\.com/[^/]+/[^/]+/pull/[0-9]+$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        patchUrl = normalized + ".patch";
        return true;
    }

    static bool TryConvertGistPageToRaw(string url, out string rawUrl)
    {
        rawUrl = string.Empty;
        var normalized = url.Split('#')[0].TrimEnd('/');
        var match = Regex.Match(
            normalized,
            @"^https?://gist\.github\.com/[^/]+/[0-9a-fA-F]+(?:/[^/?#]+)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        rawUrl = normalized + "/raw";
        return true;
    }

    static bool IsFetchableCodeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Host.EndsWith("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("gist.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        return IsCodeLikeExtension(extension);
    }

    static bool IsCodeLikeExtension(string extension)
    {
        var ext = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(ext))
        {
            return false;
        }

        return ext is ".txt" or ".log" or ".md" or ".markdown" or ".rst" or ".ini" or ".cfg" or ".conf" or ".toml" or ".yaml" or ".yml" or ".json" or ".jsonc" or ".xml" or ".csv"
            or ".cs" or ".csproj" or ".sln" or ".c" or ".h" or ".cpp" or ".hpp" or ".cc" or ".cxx" or ".hh" or ".java" or ".kt" or ".kts" or ".go" or ".rs" or ".py" or ".lua"
            or ".js" or ".jsx" or ".ts" or ".tsx" or ".php" or ".swift" or ".vb" or ".fs" or ".fsi" or ".m" or ".mm" or ".sh" or ".bash" or ".zsh" or ".ps1" or ".bat" or ".cmd"
            or ".sql" or ".patch" or ".diff" or ".hexpat" or ".ebp" or ".tbl" or ".dat";
    }

    static string DetectLanguageFromUrl(string url)
    {
        var extension = string.Empty;
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            }
            else
            {
                extension = Path.GetExtension(url).ToLowerInvariant();
            }
        }
        catch
        {
            extension = string.Empty;
        }

        return extension switch
        {
            ".cs" or ".csproj" or ".sln" => "csharp",
            ".json" or ".jsonc" => "json",
            ".xml" => "xml",
            ".yaml" or ".yml" => "yaml",
            ".ps1" => "powershell",
            ".bat" or ".cmd" or ".sh" or ".bash" or ".zsh" => "bash",
            ".py" => "python",
            ".js" or ".jsx" => "javascript",
            ".ts" or ".tsx" => "typescript",
            ".cpp" or ".hpp" or ".cc" or ".cxx" or ".hh" => "cpp",
            ".c" or ".h" => "c",
            ".go" => "go",
            ".rs" => "rust",
            ".java" => "java",
            ".lua" => "lua",
            ".sql" => "sql",
            ".md" or ".markdown" => "markdown",
            ".patch" or ".diff" => "diff",
            _ => "text"
        };
    }

    static DiscordFetchResult FetchRemoteSourceText(string fetchUrl, int fetchRetryCount)
    {
        var maxAttempts = Math.Max(1, fetchRetryCount + 1);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, fetchUrl);
                using var response = DiscordFetchHttpClient.Send(request);
                if (!response.IsSuccessStatusCode)
                {
                    lastError = new HttpRequestException($"HTTP {(int)response.StatusCode}");
                    if (attempt < maxAttempts)
                    {
                        Thread.Sleep(Math.Min(5000, attempt * 1500));
                        continue;
                    }

                    break;
                }

                var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (bytes.Length == 0 || bytes.Length > MaxFetchedTextBytes)
                {
                    return DiscordFetchResult.Failed("empty_or_too_large");
                }

                if (LooksLikeBinary(bytes))
                {
                    return DiscordFetchResult.Failed("binary_content");
                }

                var text = Encoding.UTF8.GetString(bytes);
                if (LooksLikeHtml(text))
                {
                    return DiscordFetchResult.Failed("html_content");
                }

                return DiscordFetchResult.Success(text);
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < maxAttempts)
                {
                    Thread.Sleep(Math.Min(5000, attempt * 1500));
                }
            }
        }

        return DiscordFetchResult.Failed(lastError?.Message ?? "request_failed");
    }

    static bool LooksLikeBinary(byte[] bytes)
    {
        var limit = Math.Min(bytes.Length, 12000);
        for (var i = 0; i < limit; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    static bool LooksLikeHtml(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("<html", StringComparison.OrdinalIgnoreCase)
               || text.Contains("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase);
    }

    static HttpClient CreateDiscordFetchHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Fahrenheit-Discord-Refs/1.0");
        return client;
    }

    static string ComputeSha256Hex(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    static string SafeReadAllText(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch
        {
            return string.Empty;
        }
    }

    static string ToRelativePathFromExport(string exportPath, string targetPath)
    {
        var exportDir = Path.GetDirectoryName(exportPath) ?? string.Empty;
        var relative = Path.GetRelativePath(exportDir, targetPath);
        return relative.Replace('\\', '/');
    }

    void RegenerateDiscordMarkdownForGuild(string guildDir)
    {
        if (!Directory.Exists(guildDir))
        {
            return;
        }

        var outputRoot = ResolveDiscordOutputRoot();
        var exports = Directory.GetFiles(guildDir, "*.json", SearchOption.AllDirectories)
            .Where(path => !IsDiscordHousekeepingPath(outputRoot, path) && !IsDiscordAssetPath(outputRoot, path))
            .Where(path => TryGetDiscordExportIdFromFileName(Path.GetFileName(path), out _, out _))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var exportPath in exports)
        {
            try
            {
                WriteDiscordMarkdownSidecar(exportPath);
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to regenerate markdown sidecar for {exportPath}: {ex.Message}");
            }
        }
    }

    void WriteDiscordMarkdownSidecar(string exportPath)
    {
        var refsByMessageId = LoadDiscordRefsByMessageId(exportPath);

        using var doc = JsonDocument.Parse(File.ReadAllText(exportPath));
        var root = doc.RootElement;
        var guildId = TryGetNestedJsonString(root, "guild", "id");
        var guildName = TryGetNestedJsonString(root, "guild", "name");
        var channelId = TryGetNestedJsonString(root, "channel", "id");
        var channelName = TryGetNestedJsonString(root, "channel", "name");
        var messageCount = root.TryGetProperty("messageCount", out var messageCountNode)
            ? messageCountNode.ToString()
            : "0";

        var builder = new StringBuilder();
        builder.AppendLine($"# {guildName} / {channelName}");
        builder.AppendLine();
        builder.AppendLine($"- guildId: `{guildId}`");
        builder.AppendLine($"- channelId: `{channelId}`");
        builder.AppendLine($"- messages: `{messageCount}`");
        builder.AppendLine();

        if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in messages.EnumerateArray())
            {
                var messageId = TryGetJsonString(message, "id");
                var timestamp = TryGetJsonString(message, "timestamp");
                var authorName = TryGetNestedJsonString(message, "author", "nickname");
                if (string.IsNullOrWhiteSpace(authorName))
                {
                    authorName = TryGetNestedJsonString(message, "author", "name");
                }
                var authorId = TryGetNestedJsonString(message, "author", "id");
                var content = StripLegacyEmbeddedMessageContent(TryGetJsonString(message, "content"));

                builder.AppendLine($"## {timestamp} | {authorName} ({authorId})");
                builder.AppendLine($"- messageId: `{messageId}`");
                builder.AppendLine();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    builder.AppendLine(content.TrimEnd());
                    builder.AppendLine();
                }

                if (message.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
                {
                    foreach (var attachment in attachments.EnumerateArray())
                    {
                        var fileName = TryGetJsonString(attachment, "fileName");
                        var url = TryGetJsonString(attachment, "url");
                        if (!string.IsNullOrWhiteSpace(fileName) || !string.IsNullOrWhiteSpace(url))
                        {
                            builder.AppendLine($"- attachment: `{fileName}` {url}");
                        }
                    }
                }

                if (refsByMessageId.TryGetValue(messageId, out var refsForMessage) && refsForMessage.Count > 0)
                {
                    builder.AppendLine();
                    builder.AppendLine("### Enrichment References");
                    foreach (var reference in refsForMessage
                                 .OrderBy(x => x.Kind, StringComparer.OrdinalIgnoreCase)
                                 .ThenBy(x => x.RefPath, StringComparer.OrdinalIgnoreCase))
                    {
                        var confidenceLabel = reference.Confidence.HasValue ? $" | confidence={reference.Confidence.Value}" : string.Empty;
                        var sourceUrlLabel = !string.IsNullOrWhiteSpace(reference.SourceUrl) ? $" | source={reference.SourceUrl}" : string.Empty;
                        builder.AppendLine($"- kind={reference.Kind} | language={reference.DetectedLanguage}{confidenceLabel}{sourceUrlLabel}");
                        builder.AppendLine($"  - ref: `{reference.RefPath}`");

                        var refTextPath = Path.IsPathRooted(reference.RefPath)
                            ? reference.RefPath
                            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exportPath) ?? string.Empty, reference.RefPath));
                        if (!File.Exists(refTextPath))
                        {
                            continue;
                        }

                        var refText = SafeReadAllText(refTextPath);
                        if (string.IsNullOrWhiteSpace(refText))
                        {
                            continue;
                        }

                        var normalizedText = refText.TrimEnd();
                        if (normalizedText.Length > MaxRenderedRefCharsPerMessage)
                        {
                            normalizedText = normalizedText[..MaxRenderedRefCharsPerMessage]
                                             + Environment.NewLine
                                             + "[TRUNCATED]";
                        }

                        var fenceLanguage = MapReferenceLanguageToFence(reference.DetectedLanguage);
                        builder.AppendLine($"```{fenceLanguage}");
                        builder.AppendLine(normalizedText);
                        builder.AppendLine("```");
                    }
                }

                builder.AppendLine();
            }
        }

        var markdownPath = Path.ChangeExtension(exportPath, ".md");
        File.WriteAllText(markdownPath, builder.ToString(), Utf8NoBom);
    }

    static string MapReferenceLanguageToFence(string detectedLanguage)
    {
        var lang = (detectedLanguage ?? string.Empty).Trim().ToLowerInvariant();
        return lang switch
        {
            "csharp" => "csharp",
            "json" => "json",
            "xml" => "xml",
            "yaml" => "yaml",
            "powershell" => "powershell",
            "bash" => "bash",
            "python" => "python",
            "javascript" => "javascript",
            "typescript" => "typescript",
            "cpp" => "cpp",
            "c" => "c",
            "go" => "go",
            "rust" => "rust",
            "java" => "java",
            "lua" => "lua",
            _ => "text"
        };
    }

    static string StripLegacyEmbeddedMessageContent(string content)
    {
        var value = content ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        const string startMarker = "<!-- BEGIN EMBEDDED_CODE_SNIPPETS -->";
        const string endMarker = "<!-- END EMBEDDED_CODE_SNIPPETS -->";
        var result = value;
        while (true)
        {
            var start = result.IndexOf(startMarker, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            var end = result.IndexOf(endMarker, start, StringComparison.Ordinal);
            if (end < 0)
            {
                result = result[..start];
                break;
            }

            result = result.Remove(start, end + endMarker.Length - start);
        }

        return result.TrimEnd();
    }

    static string TryGetNestedJsonString(JsonElement root, string parentProperty, string childProperty)
    {
        if (!root.TryGetProperty(parentProperty, out var parent) || parent.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return TryGetJsonString(parent, childProperty);
    }

    static string TryGetJsonString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? string.Empty
            : property.ToString().Trim();
    }

    static bool MergeDiscordExport(string existingPath, string deltaPath, string finalPath)
    {
        var existingRoot = JsonNode.Parse(File.ReadAllText(existingPath))?.AsObject()
            ?? throw new InvalidOperationException($"Failed to parse existing export JSON: {existingPath}");
        var deltaRoot = JsonNode.Parse(File.ReadAllText(deltaPath))?.AsObject()
            ?? throw new InvalidOperationException($"Failed to parse delta export JSON: {deltaPath}");

        var existingMessages = existingRoot["messages"]?.AsArray() ?? new JsonArray();
        var deltaMessages = deltaRoot["messages"]?.AsArray() ?? new JsonArray();

        var byId = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var message in existingMessages)
        {
            var id = ReadMessageId(message);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var clone = message?.DeepClone();
            if (clone is null)
            {
                continue;
            }

            byId[id] = clone;
        }

        var changed = false;
        foreach (var message in deltaMessages)
        {
            var id = ReadMessageId(message);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var clone = message?.DeepClone();
            if (clone is null)
            {
                continue;
            }

            if (!byId.TryGetValue(id, out var existingMessage) || !JsonNodesEqual(existingMessage, clone))
            {
                byId[id] = clone;
                changed = true;
            }
        }

        var mergedMessages = new JsonArray();
        foreach (var message in byId
                     .OrderBy(x => ParseSnowflake(x.Key))
                     .ThenBy(x => x.Key, StringComparer.Ordinal)
                     .Select(x => x.Value))
        {
            mergedMessages.Add(message);
        }

        existingRoot["messages"] = mergedMessages;
        existingRoot["messageCount"] = mergedMessages.Count;
        existingRoot["exportedAt"] = deltaRoot["exportedAt"]?.DeepClone() ?? JsonValue.Create(DateTimeOffset.Now.ToString("O"));
        existingRoot["dateRange"] = new JsonObject
        {
            ["after"] = null,
            ["before"] = null
        };

        if (deltaRoot["guild"] is not null)
        {
            existingRoot["guild"] = deltaRoot["guild"]!.DeepClone();
        }

        if (deltaRoot["channel"] is not null)
        {
            existingRoot["channel"] = deltaRoot["channel"]!.DeepClone();
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        var output = existingRoot.ToJsonString(options);
        File.WriteAllText(finalPath, output + Environment.NewLine, Utf8NoBom);
        return changed;
    }

    static string ReadMessageId(JsonNode? messageNode)
    {
        if (messageNode is not JsonObject messageObject)
        {
            return string.Empty;
        }

        if (!messageObject.TryGetPropertyValue("id", out var idNode))
        {
            return string.Empty;
        }

        return idNode?.GetValue<string>()?.Trim() ?? string.Empty;
    }

    static ulong ParseSnowflake(string value)
    {
        return ulong.TryParse(value, out var parsed) ? parsed : 0;
    }

    static bool JsonNodesEqual(JsonNode? left, JsonNode? right)
    {
        return string.Equals(
            left?.ToJsonString(),
            right?.ToJsonString(),
            StringComparison.Ordinal);
    }

    static void InstallDiscordExport(string stagePath, string finalPath)
    {
        EnsureDir(Path.GetDirectoryName(finalPath) ?? string.Empty);
        File.Copy(stagePath, finalPath, overwrite: true);
        CopyStageAssets(stagePath, finalPath, replaceExisting: true);
    }

    static void CopyStageAssets(string stagePath, string finalPath, bool replaceExisting)
    {
        var sourceAssetDir = GetDiscordAssetDirectory(stagePath);
        if (!Directory.Exists(sourceAssetDir))
        {
            return;
        }

        var targetAssetDir = GetDiscordAssetDirectory(finalPath);
        if (replaceExisting && Directory.Exists(targetAssetDir))
        {
            Directory.Delete(targetAssetDir, recursive: true);
        }

        EnsureDir(targetAssetDir);
        CopyDirectoryRecursive(sourceAssetDir, targetAssetDir);
    }

    static string GetDiscordAssetDirectory(string exportPath) => exportPath + "_Files";

    static void FixAssetExtensionsInDirectory(string outputRoot, string assetDir, List<string> jsonFiles)
    {
        if (!Directory.Exists(assetDir)) return;

        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(assetDir, "*", SearchOption.AllDirectories))
        {
            var currentExt = Path.GetExtension(file);
            var detectedExt = DetectDiscordExtension(file);

            if (detectedExt != null && !string.Equals(currentExt, detectedExt, StringComparison.OrdinalIgnoreCase))
            {
                var newPath = string.IsNullOrEmpty(currentExt)
                    ? file + detectedExt
                    : Path.ChangeExtension(file, detectedExt);

                if (File.Exists(newPath))
                {
                    File.Delete(file);
                }
                else
                {
                    File.Move(file, newPath);
                }
                renames[file] = newPath;
            }
            else if (!string.IsNullOrEmpty(currentExt))
            {
                // Recovery: file already has an extension, add to renames so we can fix JSONs that weren't updated
                var pathWithoutExt = file.Substring(0, file.Length - currentExt.Length);
                renames[pathWithoutExt] = file;
            }
        }

        if (renames.Count == 0) return;

        Log.Information($"Checking {renames.Count} potential path updates in {jsonFiles.Count} JSON files for {Path.GetRelativePath(outputRoot, assetDir)}...");

        foreach (var jsonPath in jsonFiles)
        {
            if (!File.Exists(jsonPath)) continue;

            try
            {
                var jsonText = File.ReadAllText(jsonPath);
                var json = JsonNode.Parse(jsonText);
                if (json is null) continue;

                bool changed = false;
                UpdateJsonPaths(json, renames, ref changed);

                if (changed)
                {
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    File.WriteAllText(jsonPath, json.ToJsonString(options) + Environment.NewLine, Utf8NoBom);
                    Log.Information($"Updated references in {Path.GetFileName(jsonPath)}.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to parse or update JSON file {jsonPath}: {ex.Message}");
            }
        }
    }

    static string? DetectDiscordExtension(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 4) return null;

            if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
            if (bytes.Length >= 4 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38) return ".gif";
            if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return ".webp";
            if (bytes.Length >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46) return ".pdf";
            if (bytes.Length >= 12 && bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70) return ".mp4";
            if (bytes.Length >= 3 && bytes[0] == 0x49 && bytes[1] == 0x44 && bytes[2] == 0x33) return ".mp3";
            if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                bytes[8] == 0x57 && bytes[9] == 0x41 && bytes[10] == 0x56 && bytes[11] == 0x45) return ".wav";
            if (bytes.Length >= 4 && bytes[0] == 0x4F && bytes[1] == 0x67 && bytes[2] == 0x67 && bytes[3] == 0x53) return ".ogg";

            var text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 1024));
            if (text.Contains("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("<html", StringComparison.OrdinalIgnoreCase)) return ".html";
            if (text.TrimStart().StartsWith("{") || text.TrimStart().StartsWith("[")) return ".json";
            if (text.Contains("<?xml", StringComparison.OrdinalIgnoreCase) || text.Contains("<svg", StringComparison.OrdinalIgnoreCase)) return ".svg";
        }
        catch { }

        return null;
    }

    static void UpdateJsonPaths(JsonNode? node, Dictionary<string, string> renames, ref bool changed)
    {
        if (node is JsonObject obj)
        {
            var keys = obj.Select(x => x.Key).ToList();
            foreach (var key in keys)
            {
                var value = obj[key];
                if (value is JsonValue val && val.TryGetValue<string>(out var str))
                {
                    if (renames.TryGetValue(str, out var newPath))
                    {
                        obj[key] = JsonValue.Create(newPath);
                        changed = true;
                    }
                }
                else if (value is not null)
                {
                    UpdateJsonPaths(value, renames, ref changed);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                var value = arr[i];
                if (value is JsonValue val && val.TryGetValue<string>(out var str))
                {
                    if (renames.TryGetValue(str, out var newPath))
                    {
                        arr[i] = JsonValue.Create(newPath);
                        changed = true;
                    }
                }
                else if (value is not null)
                {
                    UpdateJsonPaths(value, renames, ref changed);
                }
            }
        }
    }

    ProcessResult RunDiscordCli(string cliPath, string args, string description, string token, bool silent)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = args,
            WorkingDirectory = Path.GetDirectoryName(cliPath) ?? RootDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.Environment["DISCORD_TOKEN"] = token;

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            if (!process.Start())
            {
                return new ProcessResult(-1, string.Empty, "Failed to start process.");
            }
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, string.Empty, ex.ToString());
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(DiscordCliTimeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort timeout cleanup.
            }

            return new ProcessResult(
                -2,
                stdout.ToString(),
                $"Process timed out after {DiscordCliTimeoutMs / 1000} seconds.");
        }

        // Ensure async output readers flush remaining buffered lines.
        process.WaitForExit();

        if (!silent)
        {
            foreach (var line in stdout.ToString().Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                Log.Information(line);
            }

            foreach (var line in stderr.ToString().Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                Log.Warning(line);
            }
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

}

