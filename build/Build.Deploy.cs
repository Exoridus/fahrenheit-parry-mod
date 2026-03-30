using static Nuke.Common.Assert;

internal sealed partial class BuildScript
{
    void RunVerifyCore(string configuration)
    {
        ValidateJsonConfigsCore();
        BuildCore("mod", configuration, useReleaseRef: false, allowAutoDeploy: false);
        RunTestsIfAny(configuration);
    }

    void DeployCore(string target, string configuration)
    {
        var t = target.Trim().ToLowerInvariant();
        if (t != "mod" && t != "full")
        {
            Fail($"Invalid deploy target '{target}'. Use mod or full.");
        }

        var gameDir = ResolveGameDir(promptIfMissing: true, persist: false);
        DeployFromArtifacts(gameDir, configuration, t, failOnError: true, reason: "Manual deploy");
    }
}

