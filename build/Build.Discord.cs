using System.Diagnostics;
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

    [Parameter(Name = "guild")] readonly string Guild = string.Empty;
    [Parameter(Name = "channels")] readonly string Channels = string.Empty;
    [Parameter(Name = "discord-tool-dir")] readonly string DiscordToolDir = ".workspace/tools/DiscordChatExporter";
    [Parameter(Name = "discord-out-dir")] readonly string DiscordOutDir = ".workspace/discord";
    [Parameter(Name = "discord-config")] readonly string DiscordConfig = ".workspace/discord/config.local.json";
    [Parameter(Name = "discord-media-dir")] readonly string DiscordMediaDir = string.Empty;
    [Parameter(Name = "discord-include-threads")] readonly string DiscordIncludeThreads = string.Empty;
    [Parameter(Name = "discord-include-vc")] readonly bool? DiscordIncludeVc;
    [Parameter(Name = "discord-media")] readonly bool? DiscordMedia;
    [Parameter(Name = "discord-reuse-media")] readonly bool? DiscordReuseMedia;
    [Parameter(Name = "discord-cleanup-staging")] readonly bool DiscordCleanupStaging = true;
    [Parameter(Name = "discord-register-guild")] readonly bool DiscordRegisterGuild = false;

    Target DiscordSync => _ => _
        .Executes(DiscordSyncCore);

    Target FixDiscordExtensions => _ => _
        .Executes(() =>
        {
            var outputRoot = ResolvePath(DiscordOutDir);
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

            foreach (var guildDir in Directory.GetDirectories(outputRoot, "* (*)", SearchOption.TopDirectoryOnly))
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

    Target DiscordCompact => _ => _
        .Executes(() =>
        {
            var outputRoot = ResolvePath(DiscordOutDir);
            var analysisRoot = ResolvePath(".workspace/analysis");
            EnsureDir(analysisRoot);

            var outputFile = Path.Combine(analysisRoot, "discord_messages_compact.csv");
            Log.Information($"Compacting Discord exports to {outputFile}...");

            using var writer = new StreamWriter(outputFile, false, Encoding.UTF8);
            writer.WriteLine("Guild,Channel,Date,Author,Content");

            var jsonFiles = Directory.GetFiles(outputRoot, "*.json", SearchOption.AllDirectories)
                .Where(path => !IsDiscordHousekeepingPath(outputRoot, path) && !IsDiscordAssetPath(outputRoot, path))
                .ToList();

            var messageCount = 0;
            foreach (var jsonPath in jsonFiles)
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                    var root = doc.RootElement;

                    var guildName = root.TryGetProperty("guild", out var g) ? g.GetProperty("name").GetString() ?? "Unknown" : "Unknown";
                    var channelName = root.TryGetProperty("channel", out var c) ? c.GetProperty("name").GetString() ?? "Unknown" : "Unknown";

                    if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var message in messages.EnumerateArray())
                    {
                        var timestamp = message.TryGetProperty("timestamp", out var t) ? t.GetString() ?? "" : "";
                        var author = message.TryGetProperty("author", out var a) ? a.GetProperty("nickname").GetString() ?? a.GetProperty("name").GetString() ?? "Unknown" : "Unknown";
                        var content = message.TryGetProperty("content", out var co) ? co.GetString() ?? "" : "";

                        if (string.IsNullOrWhiteSpace(content) && !message.TryGetProperty("attachments", out var atts))
                            continue;

                        // Add attachment info to content if any
                        if (message.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var att in attachments.EnumerateArray())
                            {
                                var fileName = att.GetProperty("fileName").GetString();
                                content += $" [Attachment: {fileName}]";
                            }
                        }

                        writer.WriteLine($"{EscapeCsv(guildName)},{EscapeCsv(channelName)},{EscapeCsv(timestamp)},{EscapeCsv(author)},{EscapeCsv(content)}");
                        messageCount++;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to process {jsonPath}: {ex.Message}");
                }
            }

            Log.Information($"Successfully compacted {messageCount} messages.");
        });

    static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    void DiscordSyncCore()
    {
        var outputRoot = ResolvePath(DiscordOutDir);
        var baseSettings = ResolveDiscordSettings();
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

            var settings = ResolveGuildScopedDiscordSettings(
                outputRoot,
                cliPath,
                currentGuildId,
                baseSettings);

            var stagingRoot = Path.Combine(outputRoot, "_staging", currentGuildId);

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

            var processed = 0;
            var updated = 0;
            var created = 0;
            var unchanged = 0;
            var skipped = 0;
            var syncSucceeded = false;

            var jsonFiles = Directory.GetFiles(outputRoot, "*.json", SearchOption.AllDirectories)
                .Where(path => !IsDiscordHousekeepingPath(outputRoot, path) && !IsDiscordAssetPath(outputRoot, path))
                .ToList();

            try
            {
                foreach (var target in targets)
                {
                    processed++;

                    var existingPath = FindExistingDiscordExport(outputRoot, target.ChannelId);
                    var mode = "full";
                    var afterValue = string.Empty;
                    var stageOutput = string.Empty;

                    if (!Full && !string.IsNullOrWhiteSpace(existingPath))
                    {
                        afterValue = ReadNewestMessageId(existingPath);
                        if (!string.IsNullOrWhiteSpace(afterValue))
                        {
                            mode = "delta";
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(existingPath))
                    {
                        var relativeExistingPath = Path.GetRelativePath(outputRoot, existingPath);
                        stageOutput = Path.Combine(stagingRoot, relativeExistingPath);
                    }
                    else
                    {
                        stageOutput = BuildDiscordOutputTemplate(stagingRoot);
                    }

                    EnsureDir(Path.GetDirectoryName(stageOutput) ?? string.Empty);
                    DeleteDiscordStageOutputsForChannel(stagingRoot, target.ChannelId);

                    Log.Information($"[{processed}/{targets.Count}] {target.Label} [{target.ChannelId}] -> {mode}");

                    var exportOutcome = ExportDiscordChannel(
                        cliPath: cliPath,
                        guildId: currentGuildId,
                        channelId: target.ChannelId,
                        settings: settings,
                        stageOutput: stageOutput,
                        afterValue: mode == "delta" ? afterValue : null);

                    if (exportOutcome.Status is DiscordExportStatus.SkippedForbidden or DiscordExportStatus.SkippedUnsupported)
                    {
                        RememberBlacklistedChannel(settings, target.ChannelId, exportOutcome.Message);
                        skipped++;
                        Log.Warning($"Skipping inaccessible channel {target.ChannelId}: {exportOutcome.Message}");
                        continue;
                    }

                    var stagePath = ResolveStageExportPath(stagingRoot, stageOutput, target.ChannelId);
                    if (string.IsNullOrWhiteSpace(stagePath) || !File.Exists(stagePath))
                    {
                        if (mode == "delta")
                        {
                            unchanged++;
                            Log.Information($"No new messages for {target.ChannelId}.");
                            continue;
                        }

                        Fail($"Export completed without producing an output file for channel {target.ChannelId}.");
                    }

                    var finalPath = existingPath;
                    if (string.IsNullOrWhiteSpace(finalPath))
                    {
                        finalPath = Path.Combine(outputRoot, Path.GetRelativePath(stagingRoot, stagePath));
                        if (!jsonFiles.Contains(finalPath)) jsonFiles.Add(finalPath);
                    }

                    EnsureDir(Path.GetDirectoryName(finalPath) ?? string.Empty);

                    if (string.IsNullOrWhiteSpace(existingPath) || mode == "full")
                    {
                        InstallDiscordExport(stagePath, finalPath);
                        if (string.IsNullOrWhiteSpace(existingPath))
                        {
                            created++;
                        }
                        else
                        {
                            updated++;
                        }
                    }
                    else
                    {
                        var mergeChanged = MergeDiscordExport(existingPath, stagePath, finalPath);
                        CopyStageAssets(stagePath, finalPath, replaceExisting: false);

                        if (mergeChanged)
                        {
                            updated++;
                        }
                        else
                        {
                            unchanged++;
                        }
                    }

                    FixAssetExtensionsInDirectory(outputRoot, GetDiscordAssetDirectory(finalPath), jsonFiles);
                }

                syncSucceeded = true;
                Log.Information(
                    $"Discord sync finished for guild {currentGuildId}: {processed} targets, {created} created, {updated} updated, {unchanged} unchanged, {skipped} skipped.");

                if (!string.IsNullOrWhiteSpace(settings.MediaDirectory))
                {
                    FixAssetExtensionsInDirectory(outputRoot, settings.MediaDirectory, jsonFiles);
                }

                if (!Full)
                {
                    Log.Information("Delta mode only captures new messages after the newest stored message ID. Use --full periodically to reconcile edits, deletions, and older reaction changes.");
                }

                if (!baseSettings.GuildIds.Contains(currentGuildId) && DiscordRegisterGuild)
                {
                    PersistDiscordGuildId(settings.ConfigPath, currentGuildId);
                    baseSettings.GuildIds.Add(currentGuildId);
                }
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

    static void PersistDiscordGuildId(string configPath, string guildId)
    {
        EnsureDir(Path.GetDirectoryName(configPath) ?? string.Empty);

        JsonObject root;
        if (File.Exists(configPath))
        {
            root = JsonNode.Parse(File.ReadAllText(configPath))?.AsObject() ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        var guilds = root["guilds"]?.AsArray() ?? new JsonArray();
        if (guilds.Any(x => string.Equals(x?.GetValue<string>(), guildId, StringComparison.Ordinal)))
        {
            return;
        }

        guilds.Add(guildId);
        root["guilds"] = guilds;

        var options = new JsonSerializerOptions { WriteIndented = true };
        var output = root.ToJsonString(options);
        File.WriteAllText(configPath, output + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Log.Information($"Added guild {guildId} to Discord configuration.");
    }

    static bool IsDiscordAssetPath(string outputRoot, string candidatePath)
    {
        var relativePath = Path.GetRelativePath(outputRoot, candidatePath);
        var segments = relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(s => s.EndsWith("_Files", StringComparison.OrdinalIgnoreCase) || s.Equals("Media", StringComparison.OrdinalIgnoreCase));
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
        args.Append(" --include-vc ").Append(settings.IncludeVoiceChannels ? "true" : "false");
        args.Append(" --include-threads ").Append(settings.IncludeThreads);
        args.Append(" --respect-rate-limits ").Append(settings.RespectRateLimits ? "true" : "false");

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

        if (settings.Media)
        {
            args.Append(" --media");
            if (settings.ReuseMedia)
            {
                args.Append(" --reuse-media");
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.MediaDirectory))
        {
            args.Append(" --media-dir ").Append(Quote(settings.MediaDirectory));
        }

        if (settings.Utc)
        {
            args.Append(" --utc");
        }

        args.Append(" --respect-rate-limits ").Append(settings.RespectRateLimits ? "true" : "false");

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
        var toolDir = ResolvePath(DiscordToolDir);
        var cliPath = Path.Combine(toolDir, "DiscordChatExporter.Cli.exe");
        if (!File.Exists(cliPath))
        {
            Fail($"DiscordChatExporter CLI not found: {cliPath}");
        }

        return cliPath;
    }

    DiscordSyncSettings ResolveGuildScopedDiscordSettings(string outputRoot, string cliPath, string guildId, DiscordSyncSettings settings)
    {
        if (!settings.Media || !string.IsNullOrWhiteSpace(settings.MediaDirectory))
        {
            return settings;
        }

        var guildRoot = ResolveDiscordGuildRoot(outputRoot, cliPath, guildId, settings.Token);
        if (string.IsNullOrWhiteSpace(guildRoot))
        {
            return settings;
        }

        return settings with { MediaDirectory = Path.Combine(guildRoot, "Media") };
    }

    string ResolveDiscordGuildRoot(string outputRoot, string cliPath, string guildId, string token)
    {
        var existingGuildRoot = Directory
            .GetDirectories(outputRoot, $"*({guildId})", SearchOption.TopDirectoryOnly)
            .Where(path => !IsDiscordHousekeepingPath(outputRoot, path))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(existingGuildRoot))
        {
            return existingGuildRoot;
        }

        var guildName = ResolveDiscordGuildName(cliPath, guildId, token);
        if (string.IsNullOrWhiteSpace(guildName))
        {
            return string.Empty;
        }

        return Path.Combine(outputRoot, $"{SanitizeDiscordPathSegment(guildName)} ({guildId})");
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

    static string SanitizeDiscordPathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            builder.Append(invalidChars.Contains(ch) ? '_' : ch);
        }

        return builder.ToString().Trim().TrimEnd('.');
    }

    DiscordSyncSettings ResolveDiscordSettings()
    {
        var config = LoadDiscordConfig();
        var token = config.Token.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            Fail(
                $"Missing Discord token. Add {{\"token\": \"...\"}} to {ResolvePath(DiscordConfig)}.");
        }

        var mediaDirectory = FirstNonEmpty(DiscordMediaDir, config.Defaults.MediaDirectory);
        mediaDirectory = string.IsNullOrWhiteSpace(mediaDirectory)
            ? string.Empty
            : ResolvePath(mediaDirectory);

        var media = DiscordMedia ?? config.Defaults.Media ?? true;
        var reuseMedia = DiscordReuseMedia ?? config.Defaults.ReuseMedia ?? true;
        if (!media)
        {
            reuseMedia = false;
            mediaDirectory = string.Empty;
        }

        return new DiscordSyncSettings(
            Token: token,
            IncludeVoiceChannels: DiscordIncludeVc ?? config.Defaults.IncludeVoiceChannels ?? true,
            IncludeThreads: NormalizeDiscordIncludeThreads(FirstNonEmpty(DiscordIncludeThreads, config.Defaults.IncludeThreads, "All")),
            Media: media,
            ReuseMedia: reuseMedia,
            MediaDirectory: mediaDirectory,
            RespectRateLimits: config.Defaults.RespectRateLimits ?? true,
            Utc: config.Defaults.Utc ?? false,
            ConfigPath: ResolvePath(DiscordConfig),
            BlacklistedChannelIds: config.BlacklistedChannelIds,
            GuildIds: config.GuildIds);
    }

    DiscordWorkflowConfig LoadDiscordConfig()
    {
        var config = new DiscordWorkflowConfig();
        var configCandidates = new string[]
        {
            ResolvePath(DiscordConfig)
        };

        foreach (var configPath in configCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            MergeDiscordConfig(config, configPath);
        }

        return config;
    }

    static void MergeDiscordConfig(DiscordWorkflowConfig config, string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            config.Token = FirstNonEmpty(
                config.Token,
                ReadJsonString(root, "token", "Token", "DISCORD_TOKEN", "DiscordToken", "discordToken")).Trim();

            var defaultsElement = root;
            if (root.TryGetProperty("defaults", out var explicitDefaults) && explicitDefaults.ValueKind == JsonValueKind.Object)
            {
                defaultsElement = explicitDefaults;
            }

            config.Defaults.IncludeVoiceChannels ??= ReadJsonBool(defaultsElement, "includeVc", "IncludeVc", "includeVoiceChannels", "IncludeVoiceChannels");
            config.Defaults.Media ??= ReadJsonBool(defaultsElement, "media", "Media");
            config.Defaults.ReuseMedia ??= ReadJsonBool(defaultsElement, "reuseMedia", "ReuseMedia");
            config.Defaults.RespectRateLimits ??= ReadJsonBool(defaultsElement, "respectRateLimits", "RespectRateLimits");
            config.Defaults.Utc ??= ReadJsonBool(defaultsElement, "utc", "Utc");
            config.Defaults.IncludeThreads = FirstNonEmpty(
                config.Defaults.IncludeThreads,
                ReadJsonString(defaultsElement, "includeThreads", "IncludeThreads"));
            config.Defaults.MediaDirectory = FirstNonEmpty(
                config.Defaults.MediaDirectory,
                ReadJsonString(defaultsElement, "mediaDir", "MediaDir", "mediaDirectory", "MediaDirectory"));

            foreach (var channelId in ReadJsonStringArray(root, "blacklist", "channelBlacklist", "blacklistedChannels"))
            {
                config.BlacklistedChannelIds.Add(channelId);
            }

            foreach (var guildId in ReadJsonStringArray(root, "guilds", "guildIds", "serverIds"))
            {
                if (!config.GuildIds.Contains(guildId))
                {
                    config.GuildIds.Add(guildId);
                }
            }
        }
        catch
        {
            return;
        }
    }

    static string NormalizeDiscordIncludeThreads(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Equals("none", StringComparison.OrdinalIgnoreCase)) return "None";
        if (normalized.Equals("active", StringComparison.OrdinalIgnoreCase)) return "Active";
        return "All";
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

    static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
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

    static void PersistDiscordBlacklist(string configPath, IEnumerable<string> blacklistedChannelIds)
    {
        EnsureDir(Path.GetDirectoryName(configPath) ?? string.Empty);

        JsonObject root;
        if (File.Exists(configPath))
        {
            root = JsonNode.Parse(File.ReadAllText(configPath))?.AsObject() ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        var blacklist = new JsonArray();
        foreach (var channelId in blacklistedChannelIds
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(x => x, StringComparer.Ordinal))
        {
            blacklist.Add(channelId);
        }

        root["blacklist"] = blacklist;

        var options = new JsonSerializerOptions { WriteIndented = true };
        var output = root.ToJsonString(options);
        File.WriteAllText(configPath, output + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    static List<string> ReadJsonStringArray(JsonElement root, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            return element
                .EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()?.Trim() ?? string.Empty)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        return [];
    }

    static string BuildDiscordOutputTemplate(string root)
    {
        return Path.Combine(root, "%G (%g)", "%T (%t)", "%C (%c).json");
    }

    static string ResolveStageExportPath(string stagingRoot, string stageOutput, string channelId)
    {
        if (File.Exists(stageOutput))
        {
            return stageOutput;
        }

        var matches = Directory
            .GetFiles(stagingRoot, $"*({channelId}).json", SearchOption.AllDirectories)
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

        foreach (var path in Directory.GetFiles(stagingRoot, $"*({channelId}).json", SearchOption.AllDirectories))
        {
            File.Delete(path);
        }
    }

    string FindExistingDiscordExport(string outputRoot, string channelId)
    {
        var matches = Directory
            .GetFiles(outputRoot, $"*({channelId}).json", SearchOption.AllDirectories)
            .Where(path => !IsDiscordHousekeepingPath(outputRoot, path))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();

        if (matches.Length > 1)
        {
            Log.Warning($"Multiple exports found for channel {channelId}; using most recent: {matches[0]}");
        }

        return matches.FirstOrDefault() ?? string.Empty;
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
        File.WriteAllText(finalPath, output + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
                    File.WriteAllText(jsonPath, json.ToJsonString(options) + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
        process.WaitForExit();
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

    readonly record struct DiscordChannelTarget(string ChannelId, string Label);
    readonly record struct DiscordExportOutcome(DiscordExportStatus Status, string Message);

    sealed class DiscordWorkflowConfig
    {
        public string Token { get; set; } = string.Empty;
        public DiscordWorkflowDefaults Defaults { get; } = new();
        public HashSet<string> BlacklistedChannelIds { get; } = new(StringComparer.Ordinal);
        public List<string> GuildIds { get; } = new();
    }

    sealed class DiscordWorkflowDefaults
    {
        public bool? IncludeVoiceChannels { get; set; }
        public bool? Media { get; set; }
        public bool? ReuseMedia { get; set; }
        public bool? RespectRateLimits { get; set; }
        public bool? Utc { get; set; }
        public string IncludeThreads { get; set; } = string.Empty;
        public string MediaDirectory { get; set; } = string.Empty;
    }

    readonly record struct DiscordSyncSettings(
        string Token,
        bool IncludeVoiceChannels,
        string IncludeThreads,
        bool Media,
        bool ReuseMedia,
        string MediaDirectory,
        bool RespectRateLimits,
        bool Utc,
        string ConfigPath,
        HashSet<string> BlacklistedChannelIds,
        List<string> GuildIds);

    enum DiscordExportStatus
    {
        Success,
        SkippedForbidden,
        SkippedUnsupported
    }
}

