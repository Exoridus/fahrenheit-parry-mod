using Serilog;

internal sealed partial class BuildScript
{
    void SetupAutoDeployCore()
    {
        var cfg = LoadWorkspaceConfig();
        var hasPathOverride = !string.IsNullOrWhiteSpace(GameDir);

        if (hasPathOverride)
        {
            var normalizedPath = NormalizePathOrEmpty(GameDir);
            if (!IsValidGameDir(normalizedPath))
            {
                Log.Warning($"Provided --game-dir is invalid: {GameDir}");
                cfg.InstallPath = string.Empty;
            }
            else
            {
                cfg.InstallPath = normalizedPath;
            }
        }

        if (!IsValidGameDir(cfg.InstallPath))
        {
            cfg.InstallPath = string.Empty;
        }

        var alreadyConfigured = cfg.DeployAfterBuild == true && IsValidGameDir(cfg.InstallPath);
        if (alreadyConfigured && !hasPathOverride && !RefreshGameDir)
        {
            SaveWorkspaceConfig(cfg);
            Log.Information($"Auto deploy already configured: InstallPath={cfg.InstallPath}");
            return;
        }

        if (InteractiveSession && !alreadyConfigured && !hasPathOverride)
        {
            if (!AskYesNo("Would you like to setup automatic build deployment into the game installation path?", defaultYes: true))
            {
                cfg.DeployAfterBuild = false;
                SaveWorkspaceConfig(cfg);
                Log.Warning("Automatic deployment setup skipped for now.");
                Log.Information("You can configure it later with: build.cmd auto-deploy");
                return;
            }
        }

        if (!IsValidGameDir(cfg.InstallPath))
        {
            var resolvedGameDir = ResolveGameDirForAutoDeploySetup(cfg);
            if (IsValidGameDir(resolvedGameDir))
            {
                cfg.InstallPath = resolvedGameDir;
            }
        }

        if (!IsValidGameDir(cfg.InstallPath))
        {
            cfg.InstallPath = string.Empty;
            cfg.DeployAfterBuild = false;
            SaveWorkspaceConfig(cfg);
            Log.Warning("No valid game installation path was configured.");
            Log.Information("Automatic deployment setup was skipped. You can configure it later with: build.cmd auto-deploy");
            return;
        }

        cfg.DeployAfterBuild = true;
        SaveWorkspaceConfig(cfg);
        Log.Information($"Configured automatic deployment: InstallPath={cfg.InstallPath}");
    }
}


