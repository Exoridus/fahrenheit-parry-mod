using Serilog;

internal sealed partial class BuildScript
{
    void SetupAutoDeployCore()
    {
        var cfg = LoadLocalConfig();
        var prefilledAutoDeploy = ParseOptionalBool(AutoDeploy);
        var shouldConfigure = prefilledAutoDeploy
            ?? (InteractiveSession && AskYesNo("Would you like to setup automatic build deployment into the game installation path?", defaultYes: true));

        if (!shouldConfigure)
        {
            cfg.AutoDeploy = false;
            SaveLocalConfig(cfg);
            Log.Warning("Automatic deployment setup skipped for now.");
            Log.Information("You can configure it later with: build.cmd setupautodeploy");
            return;
        }

        var resolvedGameDir = ResolveGameDirForAutoDeploySetup(cfg);
        if (!IsValidGameDir(resolvedGameDir))
        {
            cfg.AutoDeploy = false;
            SaveLocalConfig(cfg);
            Log.Warning("No valid game installation path was configured.");
            Log.Information("Automatic deployment setup was skipped. You can configure it later with: build.cmd setupautodeploy");
            return;
        }

        var resolvedMode = ResolveAutoDeployModeForSetup(cfg);
        if (string.IsNullOrWhiteSpace(resolvedMode))
        {
            cfg.AutoDeploy = false;
            SaveLocalConfig(cfg);
            Log.Warning("Automatic deployment mode was not selected.");
            Log.Information("Automatic deployment setup was skipped. You can configure it later with: build.cmd setupautodeploy");
            return;
        }

        cfg.GameDir = resolvedGameDir;
        cfg.DeployMode = resolvedMode;
        cfg.AutoDeploy = true;
        SaveLocalConfig(cfg);

        Log.Information($"Configured automatic deployment: AUTO_DEPLOY=true, GAME_DIR={cfg.GameDir}, DEPLOY_MODE={cfg.DeployMode}");
    }
}
