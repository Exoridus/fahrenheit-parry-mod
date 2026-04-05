using Serilog;
using static Nuke.Common.Assert;

internal sealed partial class BuildScript
{
    void EnsureLocalBuildPrerequisitesForSetup()
    {
        EnsureGitInstalled();
        EnsureDotNetSdk10Installed();
        EnsureMsbuildInstalled();
        EnsureVcpkgInstalledAndIntegrated();
    }

    bool EnsureProjectWorkspaceSetup(string resolvedConfiguration)
    {
        if (!ShouldRunProjectWorkspaceSetup())
        {
            Log.Information("Project workspace already initialized. Skipping build.proj Setup.");
            return false;
        }

        RunBuildProjTarget(
            "Setup",
            resolvedConfiguration,
            includeNativeMsbuild: false,
            fahrenheitRef: ResolveFahrenheitRef(useReleaseRef: false));
        return true;
    }

    bool ShouldRunProjectWorkspaceSetup()
    {
        var fahrenheitRoot = ResolvePath(FahrenheitDir);
        var gitDir = Path.Combine(fahrenheitRoot, ".git");
        if (!Directory.Exists(gitDir))
        {
            return true;
        }

        var managedCoreProject = Path.Combine(fahrenheitRoot, "core", "fh", "Fahrenheit.csproj");
        var managedRuntimeProject = Path.Combine(fahrenheitRoot, "core", "runtime", "Fahrenheit.Runtime.csproj");
        if (!File.Exists(managedCoreProject) || !File.Exists(managedRuntimeProject))
        {
            return true;
        }

        return false;
    }

    void EnsureGameDirConfiguredForSetup()
    {
        var cfg = LoadWorkspaceConfig();
        var resolvedGameDir = ResolveGameDirForAutoDeploySetup(cfg, requiredForSetup: true);
        if (!IsValidGameDir(resolvedGameDir))
        {
            Fail("Could not resolve a valid GameDir for setup. Pass --game-dir <path> (must contain FFX.exe).");
            return;
        }

        var normalized = NormalizePathOrEmpty(resolvedGameDir);
        if (!normalized.Equals(NormalizePathOrEmpty(cfg.GameDir), StringComparison.OrdinalIgnoreCase))
        {
            cfg.GameDir = normalized;
            SaveWorkspaceConfig(cfg);
            Log.Information($"Saved GameDir in workspace config: {cfg.GameDir}");
        }
    }

    void SetupAutoDeployCore()
    {
        var cfg = LoadWorkspaceConfig();
        var hasPathOverride = !string.IsNullOrWhiteSpace(GameDir);
        var hasValidPathOverride = false;

        if (hasPathOverride)
        {
            var normalizedPath = NormalizePathOrEmpty(GameDir);
            if (!IsValidGameDir(normalizedPath))
            {
                Log.Warning($"Provided --game-dir is invalid: {GameDir}");
            }
            else
            {
                cfg.GameDir = normalizedPath;
                hasValidPathOverride = true;
            }
        }

        if (!IsValidGameDir(cfg.GameDir))
        {
            cfg.GameDir = string.Empty;
        }

        var alreadyConfigured = cfg.AutoDeploy == true && IsValidGameDir(cfg.GameDir);
        if (alreadyConfigured && !hasPathOverride && !RefreshGameDir)
        {
            SaveWorkspaceConfig(cfg);
            Log.Information($"Auto deploy already configured: GameDir={cfg.GameDir}");
            return;
        }

        if (InteractiveSession && !alreadyConfigured && !hasValidPathOverride)
        {
            if (!AskYesNo("Would you like to setup automatic build deployment into the game installation path?", defaultYes: true))
            {
                cfg.AutoDeploy = false;
                SaveWorkspaceConfig(cfg);
                Log.Warning("Automatic deployment setup skipped for now.");
                Log.Information("You can configure it later with: build.cmd auto-deploy");
                return;
            }
        }

        if (!IsValidGameDir(cfg.GameDir))
        {
            var resolvedGameDir = ResolveGameDirForAutoDeploySetup(cfg, requiredForSetup: false);
            if (IsValidGameDir(resolvedGameDir))
            {
                cfg.GameDir = resolvedGameDir;
            }
        }

        if (!IsValidGameDir(cfg.GameDir))
        {
            cfg.GameDir = string.Empty;
            cfg.AutoDeploy = false;
            SaveWorkspaceConfig(cfg);
            Log.Warning("No valid game installation path was configured.");
            Log.Information("Automatic deployment setup was skipped. You can configure it later with: build.cmd auto-deploy");
            return;
        }

        cfg.AutoDeploy = true;
        SaveWorkspaceConfig(cfg);
        Log.Information($"Configured automatic deployment: GameDir={cfg.GameDir}");
    }

    string ResolveGameDirForAutoDeploySetup(WorkspaceConfig cfg, bool requiredForSetup)
    {
        var fromArg = NormalizePathOrEmpty(GameDir);
        if (!string.IsNullOrWhiteSpace(fromArg))
        {
            if (IsValidGameDir(fromArg))
            {
                return fromArg;
            }

            if (!InteractiveSession)
            {
                Fail($"Invalid --game-dir value '{GameDir}' (FFX.exe not found).");
            }

            if (!InteractiveSession || requiredForSetup)
            {
                Log.Warning($"Provided --game-dir is invalid: {fromArg}");
            }
        }

        if (!RefreshGameDir)
        {
            var fromConfig = NormalizePathOrEmpty(cfg.GameDir);
            if (IsValidGameDir(fromConfig))
            {
                return fromConfig;
            }
        }

        var detected = DetectGameDir();
        if (IsValidGameDir(detected))
        {
            if (!InteractiveSession)
            {
                return detected;
            }

            if (AskYesNo($"Detected game path '{detected}'. Use this path?", defaultYes: true))
            {
                return detected;
            }
        }

        if (!InteractiveSession)
        {
            if (requiredForSetup)
            {
                Fail(
                    "Could not resolve GameDir in non-interactive setup mode. " +
                    "Pass --game-dir <path> (must contain FFX.exe).");
            }

            return string.Empty;
        }

        while (true)
        {
            Console.Write("Enter game installation directory (must contain FFX.exe): ");
            var manual = NormalizePathOrEmpty(Console.ReadLine());
            if (IsValidGameDir(manual))
            {
                return manual;
            }

            Log.Warning($"Invalid game directory: {manual}");
            if (!requiredForSetup && !AskYesNo("Try entering GameDir again?", defaultYes: true))
            {
                return string.Empty;
            }
        }
    }
}


