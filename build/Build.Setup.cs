using Serilog;

internal sealed partial class BuildScript
{
    void SetupAutoDeployCore()
    {
        var cfg = LoadLocalConfig();
        var hasPathOverride = !string.IsNullOrWhiteSpace(GameDir);

        if (hasPathOverride)
        {
            var normalizedPath = NormalizePathOrEmpty(GameDir);
            if (!IsValidGameDir(normalizedPath))
            {
                Log.Warning($"Provided --game-dir is invalid: {GameDir}");
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

        var alreadyConfigured = cfg.AutoDeploy == true && IsValidGameDir(cfg.GameDir);
        if (alreadyConfigured && !hasPathOverride && !RefreshGameDir)
        {
            SaveLocalConfig(cfg);
            Log.Information($"Auto deploy already configured: GameDir={cfg.GameDir}");
            return;
        }

        if (InteractiveSession && !alreadyConfigured && !hasPathOverride)
        {
            if (!AskYesNo("Would you like to setup automatic build deployment into the game installation path?", defaultYes: true))
            {
                cfg.AutoDeploy = false;
                SaveLocalConfig(cfg);
                Log.Warning("Automatic deployment setup skipped for now.");
                Log.Information("You can configure it later with: build.cmd auto-deploy");
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
            cfg.AutoDeploy = false;
            SaveLocalConfig(cfg);
            Log.Warning("No valid game installation path was configured.");
            Log.Information("Automatic deployment setup was skipped. You can configure it later with: build.cmd auto-deploy");
            return;
        }

        cfg.AutoDeploy = true;
        SaveLocalConfig(cfg);
        Log.Information($"Configured automatic deployment: GameDir={cfg.GameDir}");
    }
}


