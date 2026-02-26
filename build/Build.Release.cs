using Serilog;

internal sealed partial class BuildScript
{
    void ReleaseReadyCore()
    {
        RequireGitRepository();
        EnsureCleanWorkingTree();

        var commitRange = Range;
        if (string.IsNullOrWhiteSpace(commitRange))
        {
            var latestTag = TryGetLatestSemverTag();
            commitRange = string.IsNullOrWhiteSpace(latestTag) ? "HEAD" : $"{latestTag}..HEAD";
        }

        ValidateCommitRangeCore(commitRange);
        RunVerifyCore("Debug");

        var preflightTag = !string.IsNullOrWhiteSpace(Tag)
            ? Tag.Trim()
            : $"v0.0.0-preflight-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var repoSlug = ResolveRepositorySlug(Repository);
        if (string.IsNullOrWhiteSpace(repoSlug))
        {
            repoSlug = "owner/repo";
        }

        BuildCore("full", "Release", useReleaseRef: false);

        var preflightOut = ResolvePath(".release/preflight");
        RecreateDir(preflightOut);
        PackageReleaseCore(preflightTag, ResolvePath(DeployDir), preflightOut, ModId);
        GenerateReleaseNotesCore(preflightTag, repoSlug, Path.Combine(preflightOut, "release-notes.txt"));

        Log.Information($"Release preflight completed. Artifacts: {preflightOut}");
    }
}
