using Serilog;

internal sealed partial class BuildScript
{
    void SetupAutoDeployCore()
    {
        var cfg = LoadLocalConfig();
        var modeOverride = NormalizeAutoDeployModeOrEmpty(AutoDeployMode);
        var hasModeOverride = !string.IsNullOrWhiteSpace(modeOverride);
        var hasPathOverride = !string.IsNullOrWhiteSpace(GameDir);

        if (hasModeOverride)
        {
            cfg.DeployMode = modeOverride;
        }

        if (hasModeOverride && modeOverride == "none" && !hasPathOverride)
        {
            cfg.DeployMode = "none";
            SaveLocalConfig(cfg);
            Log.Warning("Automatic deployment mode is set to 'none'.");
            Log.Information("You can enable it later with: build.cmd setupautodeploy --autodeploymode update");
            return;
        }

        if (hasPathOverride)
        {
            var normalizedPath = NormalizePathOrEmpty(GameDir);
            if (!IsValidGameDir(normalizedPath))
            {
                Log.Warning($"Provided --gamedir is invalid: {GameDir}");
                cfg.GameDir = string.Empty;
            }
            else
            {
                cfg.GameDir = normalizedPath;
            }
        }

        if (!IsValidGameDir(cfg.GameDir))
        {
            cfg.GameDir = string.Empty;
        }

        var normalizedMode = NormalizeAutoDeployModeOrEmpty(cfg.DeployMode);
        cfg.DeployMode = string.IsNullOrWhiteSpace(normalizedMode) ? "none" : normalizedMode;

        var alreadyConfigured = IsValidGameDir(cfg.GameDir) && cfg.DeployMode != "none";
        if (alreadyConfigured && !hasModeOverride && !hasPathOverride)
        {
            SaveLocalConfig(cfg);
            Log.Information($"Auto deploy already configured: GAME_DIR={cfg.GameDir}, DEPLOY_MODE={cfg.DeployMode}");
            return;
        }

        if (InteractiveSession && !alreadyConfigured && !hasModeOverride)
        {
            if (!AskYesNo("Would you like to setup automatic build deployment into the game installation path?", defaultYes: true))
            {
                cfg.DeployMode = "none";
                SaveLocalConfig(cfg);
                Log.Warning("Automatic deployment setup skipped for now.");
                Log.Information("You can configure it later with: build.cmd setupautodeploy");
                return;
            }
        }

        if (!IsValidGameDir(cfg.GameDir))
        {
            var resolvedGameDir = ResolveGameDirForAutoDeploySetup(cfg);
            if (IsValidGameDir(resolvedGameDir))
            {
                cfg.GameDir = resolvedGameDir;
            }
        }

        if (!IsValidGameDir(cfg.GameDir))
        {
            cfg.GameDir = string.Empty;
            cfg.DeployMode = "none";
            SaveLocalConfig(cfg);
            Log.Warning("No valid game installation path was configured.");
            Log.Information("Automatic deployment setup was skipped. You can configure it later with: build.cmd setupautodeploy");
            return;
        }

        if (!hasModeOverride && cfg.DeployMode == "none")
        {
            var resolvedMode = ResolveAutoDeployModeForSetup(cfg);
            cfg.DeployMode = string.IsNullOrWhiteSpace(resolvedMode) ? "none" : resolvedMode;
        }

        if (cfg.DeployMode == "none")
        {
            SaveLocalConfig(cfg);
            Log.Warning("Automatic deployment mode is set to 'none'.");
            Log.Information("You can enable it later with: build.cmd setupautodeploy --autodeploymode update");
            return;
        }

        SaveLocalConfig(cfg);
        Log.Information($"Configured automatic deployment: GAME_DIR={cfg.GameDir}, DEPLOY_MODE={cfg.DeployMode}");
    }
}
