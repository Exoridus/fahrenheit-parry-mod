using System.Text;

internal sealed partial class BuildScript
{
    readonly record struct DiscordChannelTarget(string ChannelId, string Label);
    readonly record struct DiscordExportOutcome(DiscordExportStatus Status, string Message);
    readonly record struct DiscordRemoteFetchTarget(string SourceUrl, string FetchUrl, string SourceType, string DetectedLanguage);
    readonly record struct DiscordFetchResult(bool Ok, string Text, string Error, string Sha256, int Bytes)
    {
        public static DiscordFetchResult Success(string text)
        {
            var normalized = text ?? string.Empty;
            return new DiscordFetchResult(
                Ok: true,
                Text: normalized,
                Error: string.Empty,
                Sha256: ComputeSha256Hex(normalized),
                Bytes: Encoding.UTF8.GetByteCount(normalized));
        }

        public static DiscordFetchResult Failed(string error) => new(
            Ok: false,
            Text: string.Empty,
            Error: error ?? "error",
            Sha256: string.Empty,
            Bytes: 0);
    }

    readonly record struct DiscordMessageRefEntry(
        string MessageId,
        string MessageTimestamp,
        string Kind,
        string SourceUrl,
        string FetchUrl,
        string RefPath,
        string DetectedLanguage,
        int? Confidence,
        string Sha256,
        int Bytes,
        string Status,
        string Error);

    readonly record struct DiscordServerRefsChannelSummary(
        string GuildId,
        string GuildName,
        string ChannelId,
        string ChannelName,
        string ExportPath,
        string RefsPath,
        int RefCount,
        string LastMessageId,
        Dictionary<string, int> RefKinds);

    readonly record struct DiscordExportIndexEntry(
        string Path,
        long LastWriteUtcTicks,
        string NewestMessageId,
        string GuildId,
        bool Inaccessible,
        string InaccessibleReason);

    sealed class DiscordExportIndexCacheFile
    {
        public string UpdatedAtUtc { get; set; } = string.Empty;
        public Dictionary<string, DiscordExportIndexCacheEntry> Channels { get; set; } = new(StringComparer.Ordinal);
    }

    sealed class DiscordExportIndexCacheEntry
    {
        public string Path { get; set; } = string.Empty;
        public long LastWriteUtcTicks { get; set; }
        public string NewestMessageId { get; set; } = string.Empty;
        public string GuildId { get; set; } = string.Empty;
        public bool Inaccessible { get; set; }
        public string InaccessibleReason { get; set; } = string.Empty;
    }

    sealed class DiscordWorkflowConfig
    {
        public List<string> BlacklistedChannelIds { get; set; } = [];
        public List<string> GuildIds { get; set; } = [];
    }

    sealed class DiscordServerRefsMetadata
    {
        public string GeneratedAtUtc { get; set; } = string.Empty;
        public string GuildId { get; set; } = string.Empty;
        public string GuildName { get; set; } = string.Empty;
        public List<string> Blacklist { get; set; } = [];
        public List<DiscordServerRefsChannelSummary> Channels { get; set; } = [];
        public int TotalRefs { get; set; }
    }

    readonly record struct DiscordSyncSettings(
        string Token,
        string MediaDirectory,
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
