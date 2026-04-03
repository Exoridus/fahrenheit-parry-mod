using Serilog;
using static Nuke.Common.Assert;

internal sealed partial class BuildScript
{
    sealed record WorkflowDefinition(
        string Name,
        string Section,
        string Summary,
        string Purpose,
        IReadOnlyList<string> Parameters,
        IReadOnlyList<string> Examples,
        Action Execute);

    static readonly string[] BuildSectionOrder = ["Core", "Release", "Commit", "Utility"];
    static readonly string[] ToolsSectionOrder = ["Tooling Workflows (local-only)"];

    IReadOnlyList<WorkflowDefinition>? _buildWorkflowDefinitions;
    IReadOnlyList<WorkflowDefinition>? _toolsWorkflowDefinitions;
    IReadOnlyDictionary<string, WorkflowDefinition>? _buildWorkflowLookup;
    IReadOnlyDictionary<string, WorkflowDefinition>? _toolsWorkflowLookup;

    IReadOnlyList<WorkflowDefinition> BuildWorkflowDefinitions =>
        _buildWorkflowDefinitions ??= CreateBuildWorkflowDefinitions();

    IReadOnlyList<WorkflowDefinition> ToolsWorkflowDefinitions =>
        _toolsWorkflowDefinitions ??= CreateToolsWorkflowDefinitions();

    IReadOnlyDictionary<string, WorkflowDefinition> BuildWorkflowLookup =>
        _buildWorkflowLookup ??= BuildWorkflowDefinitions.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

    IReadOnlyDictionary<string, WorkflowDefinition> ToolsWorkflowLookup =>
        _toolsWorkflowLookup ??= ToolsWorkflowDefinitions.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

    IReadOnlyList<(string Heading, IReadOnlyList<WorkflowDefinition> Workflows)> BuildWorkflowSections =>
        BuildSectionOrder
            .Select(section => (Heading: section, Workflows: (IReadOnlyList<WorkflowDefinition>)BuildWorkflowDefinitions.Where(x => x.Section.Equals(section, StringComparison.Ordinal)).ToList()))
            .Where(x => x.Workflows.Count > 0)
            .ToList();

    IReadOnlyList<(string Heading, IReadOnlyList<WorkflowDefinition> Workflows)> ToolsWorkflowSections =>
        ToolsSectionOrder
            .Select(section => (Heading: section, Workflows: (IReadOnlyList<WorkflowDefinition>)ToolsWorkflowDefinitions.Where(x => x.Section.Equals(section, StringComparison.Ordinal)).ToList()))
            .Where(x => x.Workflows.Count > 0)
            .ToList();

    bool IsBuildWorkflow(string workflow) => BuildWorkflowLookup.ContainsKey(workflow);
    bool IsToolWorkflow(string workflow) => ToolsWorkflowLookup.ContainsKey(workflow);
    WorkflowDefinition? TryGetBuildWorkflow(string workflow) => BuildWorkflowLookup.TryGetValue(workflow, out var definition) ? definition : null;
    WorkflowDefinition? TryGetToolWorkflow(string workflow) => ToolsWorkflowLookup.TryGetValue(workflow, out var definition) ? definition : null;

    IReadOnlyList<WorkflowDefinition> CreateBuildWorkflowDefinitions()
    {
        return
        [
            new("install", "Core", "Install/check prerequisites", "Install/check local prerequisites.", ["--full (optional).", "--dry-run (optional)."], ["build.cmd install", "build.cmd install --full"], ExecuteInstallWorkflow),
            new("setup", "Core", "Configure repo hooks + Fahrenheit setup + optional auto-deploy setup", "Prepare repository for local development.", [], ["build.cmd setup"], ExecuteSetupWorkflow),
            new("clean", "Core", "Remove local caches/artifacts (safe default)", "Default clean removes cache + build artifacts. Add explicit flags for analysis/exports/game-data/tools or use --purge for full local cleanup.", ["--analysis (optional).", "--exports (optional).", "--game-data (optional).", "--tools (optional).", "--purge (optional, requires --yes).", "--yes (required with --purge).", "--dry-run (optional)."], ["build.cmd clean", "build.cmd clean --analysis", "build.cmd clean --exports --game-data", "build.cmd clean --purge --yes"], RunCleanCore),
            new("auto-deploy", "Core", "Configure automatic post-build deploy", "Configure automatic post-build deployment.", ["--game-dir <path> (optional).", "--refresh-game-dir (optional)."], ["build.cmd auto-deploy", "build.cmd auto-deploy --game-dir \"C:\\Games\\Final Fantasy X-X2 - HD Remaster\""], SetupAutoDeployCore),
            new("doctor", "Core", "Diagnose local toolchain/environment state", "Diagnose local toolchain and environment state.", ["--full (optional)."], ["build.cmd doctor", "build.cmd doctor --full"], RunDoctorCore),
            new("format", "Core", "Auto-fix code formatting/style", "Apply code formatting/style fixes using dotnet format.", [], ["build.cmd format"], RunFormatFixCore),
            new("lint", "Core", "Run fast lint/compile checks", "Run fast lint/compile checks for build, mod, and tests projects.", ["--target Debug|Release (optional).", "--config <path-to-config.local.json> (optional)."], ["build.cmd lint"], () => RunLintCore(BuildTargetOverride)),
            new("smoke", "Core", "Run quick end-to-end sanity checks", "Run quick sanity checks against a full build.", ["--target Debug|Release (optional).", "--config <path-to-config.local.json> (optional)."], ["build.cmd smoke"], () => RunSmokeCore(BuildTargetOverride)),
            new("verify", "Core", "Build full payload (config BuildTarget by default) + run tests", "Run local validation without deploy side effects.", ["--target Debug|Release (optional).", "--config <path-to-config.local.json> (optional).", "--repo owner/repo (optional)."], ["build.cmd verify"], ExecuteVerifyWorkflow),
            new("build", "Core", "Build full payload", "Build full Fahrenheit payload.", ["--target Debug|Release (optional).", "--config <path-to-config.local.json> (optional).", "--auto-deploy or --no-auto-deploy (optional).", "--dry-run (optional)."], ["build.cmd build", "build.cmd build --target Release"], () => BuildCore("full", BuildTargetOverride, useReleaseRef: false)),
            new("deploy", "Core", "Deploy artifacts to game directory", "Deploy full build artifacts into InstallPath.", ["--game-dir <path> (optional).", "--refresh-game-dir (optional).", "--target Debug|Release (optional).", "--config <path-to-config.local.json> (optional).", "--dry-run (optional)."], ["build.cmd deploy"], () => DeployCore("full", BuildTargetOverride)),
            new("start", "Core", "Launch fhstage0.exe", "Launch the game via deployed Fahrenheit stage0 loader.", ["--game-dir <path> (optional).", "--refresh-game-dir (optional).", "--elevated (optional)."], ["build.cmd start"], StartCore),
            new("release-bump", "Release", "Bump version + changelog + tag", "Bump version and create release commit/tag.", ["--bump patch|minor|major (optional)."], ["build.cmd release-bump"], ReleaseVersionCore),
            new("release-ready", "Release", "Preflight checks/build/package/notes", "Run release preflight.", ["--target Debug|Release (optional).", "--config <path-to-config.local.json> (optional).", "--repo owner/repo (optional)."], ["build.cmd release-ready"], ReleaseReadyCore),
            new("release-pack", "Release", "Create release ZIP assets", "Package built release payloads into ZIP archives.", ["--tag vX.Y.Z (required).", "--deploy-dir <path> (optional).", "--out-dir <path> (optional)."], ["build.cmd release-pack --tag v0.0.1"], ExecuteReleasePackWorkflow),
            new("release-notes", "Release", "Generate release notes markdown", "Generate release-notes markdown/text for a tag.", ["--tag vX.Y.Z (required).", "--repo owner/repo (required).", "--out <path> (optional)."], ["build.cmd release-notes --tag v0.0.1 --repo owner/repo"], ExecuteReleaseNotesWorkflow),
            new("commit", "Commit", "Interactive/non-interactive Conventional Commit", "Create a Conventional Commit.", ["--type feat|fix|... (optional).", "--scope <scope> (optional).", "--subject \"message\" (required in non-interactive mode).", "--breaking (optional)."], ["build.cmd commit"], ExecuteCommitWorkflow),
            new("commit-check", "Commit", "Validate one commit message", "Validate one commit message.", ["--commit-file <path> or --message \"...\"."], ["build.cmd commit-check --message \"feat: x\""], ExecuteCommitCheckWorkflow),
            new("commit-range", "Commit", "Validate commit subjects in a range", "Validate commit subjects in a git range.", ["--range <BASE..HEAD> (required)."], ["build.cmd commit-range --range origin/main..HEAD"], ExecuteCommitRangeWorkflow),
            new("docs-sync", "Utility", "Regenerate build/tools automation docs", "Regenerate docs/automation.md and docs/tools-automation.md from workflow metadata.", [], ["build.cmd docs-sync"], SyncAutomationDocsCore)
        ];
    }

    IReadOnlyList<WorkflowDefinition> CreateToolsWorkflowDefinitions()
    {
        return
        [
            new("discord-sync", "Tooling Workflows (local-only)", "Export Discord JSON/Markdown + media + enrichment", "Export Discord channels/threads into .workspace/discord.", ["--guild <serverId> (required).", "--channels <id1,id2,...> (optional).", "--full (optional).", "--discord-utc or --no-discord-utc (optional).", "Workspace config uses strict PascalCase keys in .workspace/config.local.json (DiscordToken, OpenApiUrl, OpenApiKey, OpenApiModel, FetchRetryCount).", "Discord workflow config uses strict PascalCase keys in .workspace/discord/config.local.json (Blacklist[], Guilds[])."], ["tools.cmd discord-sync --guild 612363389003366405"], DiscordSyncCore),
            new("data-setup", "Tooling Workflows (local-only)", "Install/update local data tooling", "Install/update data tooling (VBFTool + FFXDataParser).", ["--parser-repo <url> (optional).", "--parser-dir <path> (optional).", "--parser-ref <git-ref> (optional).", "--vbf-api <url> (optional).", "--vbf-dir <path> (optional)."], ["tools.cmd data-setup"], () =>
            {
                SetupVbfExtractorCore();
                SetupDataParserCore();
            }),
            new("data-extract", "Tooling Workflows (local-only)", "Extract VBF archives", "Extract FFX/FFX-2 data archives with VBFTool.", ["--vbf-game-dir <path> (optional).", "--extract-out <path> (optional).", "--extract-meta-menu or --no-extract-meta-menu (optional)."], ["tools.cmd data-extract"], () =>
            {
                SetupVbfExtractorCore();
                ExtractGameDataCore();
            }),
            new("data-parse", "Tooling Workflows (local-only)", "Run one parser mode", "Run one parser mode and capture output as txt.", ["--data-mode <MODE> (optional).", "--data-args \"<arg1> <arg2>\" (optional).", "--data-root <path> (optional).", "--data-out <path> (optional)."], ["tools.cmd data-parse --data-mode READ_ALL_COMMANDS"], () =>
            {
                SetupVbfExtractorCore();
                SetupDataParserCore();
                ParseDataCore();
            }),
            new("data-parse-all", "Tooling Workflows (local-only)", "Run parser mode batch", "Run configured parser mode batch and capture all outputs.", ["--data-batch \"MODE1;MODE2 arg\" (optional).", "--data-root <path> (optional).", "--data-out <path> (optional)."], ["tools.cmd data-parse-all"], () =>
            {
                SetupVbfExtractorCore();
                SetupDataParserCore();
                ParseDataAllCore();
            }),
            new("map-import", "Tooling Workflows (local-only)", "Generate canonical mapping JSON", "Generate canonical locale/domain mapping JSON from parser outputs.", ["--map-source <path> (optional).", "--locales us,de,... (optional).", "--data-out <path> (optional)."], ["tools.cmd map-import"], () =>
            {
                SetupVbfExtractorCore();
                SetupDataParserCore();
                ImportLocalizedMappingsCore();
            }),
            new("map-build", "Tooling Workflows (local-only)", "Build runtime mapping bundles", "Build runtime mapping bundles from canonical mapping JSON.", ["--map-source <path> (optional).", "--map-out <path> (optional).", "--map-publish <path> (optional).", "--locales us,de,... (optional)."], ["tools.cmd map-build"], BuildLocalizedBundlesCore),
            new("data-inventory", "Tooling Workflows (local-only)", "Generate DATA_TREE reports", "Generate DATA_TREE.txt summaries for extracted data folders.", ["--data-root-dir <path> (optional).", "--folders \"name1;name2\" (optional)."], ["tools.cmd data-inventory"], DataInventoryCore),
            new("data-offload", "Tooling Workflows (local-only)", "Copy/move data folders to NAS", "Move or copy large extracted data folders to NAS and optionally keep junctions.", ["--nas-dir <unc-path> (required).", "--offload-mode move|copy (optional).", "--keep-data-junction or --no-keep-data-junction (optional).", "--data-root-dir <path> (optional).", "--folders \"name1;name2\" (optional)."], ["tools.cmd data-offload --nas-dir \"\\\\10.0.10.50\\data\\archive\\final-fantasy-assets\""], () =>
            {
                DataInventoryCore();
                OffloadDataCore();
            }),
            new("ghidra-setup", "Tooling Workflows (local-only)", "Install/update Ghidra", "Install/update Ghidra into a repo-local tools directory.", ["--ghidra-api <url> (optional).", "--ghidra-dir <path> (optional)."], ["tools.cmd ghidra-setup"], SetupGhidraCore),
            new("ghidra-start", "Tooling Workflows (local-only)", "Start Ghidra", "Start the repo-local Ghidra launcher.", ["--ghidra-dir <path> (optional)."], ["tools.cmd ghidra-start"], StartGhidraCore)
        ];
    }

    void ExecuteInstallWorkflow()
    {
        EnsureWingetAvailable();
        EnsureGitInstalled();
        EnsureDotNetSdk10Installed();
        if (Full)
        {
            EnsureMsbuildInstalled();
            EnsureVcpkgInstalledAndIntegrated();
        }

        Log.Information("Prerequisite check/install finished.");
    }

    void ExecuteSetupWorkflow()
    {
        var resolvedConfiguration = ResolveBuildConfiguration(BuildTargetOverride);
        SetupHooksCore();
        RunBuildProjTarget("Setup", resolvedConfiguration, includeNativeMsbuild: false, fahrenheitRef: ResolveFahrenheitRef(useReleaseRef: false));
        SetupAutoDeployCore();
        if (InteractiveSession && AskYesNo("Run first full build now? (Recommended)", defaultYes: true))
        {
            RunBuildProjTarget("Build", resolvedConfiguration, includeNativeMsbuild: true, fahrenheitRef: ResolveFahrenheitRef(useReleaseRef: false));
        }
    }

    void ExecuteVerifyWorkflow()
    {
        if (!IsValidConventionalCommit("feat: selftest commit format") || IsValidConventionalCommit("invalid message"))
        {
            Fail("Commit validator selftest failed.");
        }

        RunVerifyCore(BuildTargetOverride);
    }

    void ExecuteReleasePackWorkflow()
    {
        if (string.IsNullOrWhiteSpace(Tag))
        {
            Fail("Missing --tag.");
        }

        PackageReleaseCore(Tag, ResolvePath(DeployDir), ResolvePath(OutDir), ModId);
    }

    void ExecuteReleaseNotesWorkflow()
    {
        if (string.IsNullOrWhiteSpace(Tag))
        {
            Fail("Missing --tag.");
        }

        var repoSlug = ResolveRepositorySlug(Repo);
        if (string.IsNullOrWhiteSpace(repoSlug))
        {
            Fail("Missing --repo owner/repo.");
        }

        GenerateReleaseNotesCore(
            tag: Tag,
            repositorySlug: repoSlug,
            outputPath: ResolvePath(Out));
    }

    void ExecuteCommitWorkflow()
    {
        var requestedMessage = Subject;
        var requestedType = Type;
        var requestedScope = Scope;
        var requestedBreaking = Breaking;
        if (string.IsNullOrWhiteSpace(requestedMessage))
        {
            if (!InteractiveSession)
            {
                Fail("Missing --subject. In interactive mode, you can run build.cmd commit without arguments.");
            }
            var wizard = RunCommitWizard();
            if (!wizard.Confirmed)
            {
                Fail("Commit canceled.");
            }
            requestedType = wizard.Type;
            requestedScope = wizard.Scope;
            requestedMessage = wizard.Message;
            requestedBreaking = wizard.Breaking;
        }
        var subject = BuildCommitSubject(requestedType, requestedScope, requestedMessage, requestedBreaking);
        if (!IsValidConventionalCommit(subject))
        {
            Fail($"Invalid Conventional Commit subject: {subject}");
        }
        RunChecked("git", $"commit -m {Quote(subject)}", "Create commit");
    }

    void ExecuteCommitCheckWorkflow()
    {
        if (!string.IsNullOrWhiteSpace(CommitFile))
        {
            ValidateCommitMessageFromFile(ResolvePath(CommitFile));
            return;
        }
        if (string.IsNullOrWhiteSpace(Message))
        {
            Fail("Missing --commit-file or --message.");
        }
        ValidateCommitMessageString(Message);
    }

    void ExecuteCommitRangeWorkflow()
    {
        if (string.IsNullOrWhiteSpace(Range))
        {
            Fail("Missing --range BASE..HEAD.");
        }
        ValidateCommitRangeCore(Range);
    }
}
