using static Nuke.Common.Assert;

internal sealed partial class BuildScript
{
    void RunVerifyCore(string configuration)
    {
        BuildCore("mod", configuration, useReleaseRef: false);
        RunTestsIfAny(configuration);
    }

    void DeployCore(string target, string mode, string configuration)
    {
        var t = target.Trim().ToLowerInvariant();
        var m = NormalizeManualDeployMode(mode);
        if (t != "mod" && t != "full")
        {
            Fail($"Invalid deploy target '{target}'. Use mod or full.");
        }

        var gameDir = ResolveGameDir(promptIfMissing: true, persist: false);
        DeployFromArtifacts(gameDir, configuration, t, m, failOnError: true, reason: "Manual deploy");
    }
}
