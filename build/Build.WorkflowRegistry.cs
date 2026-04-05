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
            new("setup", "Core", "Primary local onboarding/setup", "Primary local onboarding: prerequisites, config/GameDir repair, hooks, workspace setup, and auto-deploy setup.", [], ["build.cmd setup"], ExecuteSetupWorkflow),
            new("clean", "Core", "Remove local caches/artifacts (safe default)", "Default clean removes cache + build artifacts. Add explicit flags for analysis/exports/game-data/purge-tools or use --purge for full local cleanup.", ["--analysis (optional).", "--exports (optional).", "--game-data (optional).", "--purge-tools (optional).", "--purge (optional, requires --yes).", "--yes (required with --purge).", "--dry-run (optional)."], ["build.cmd clean", "build.cmd clean --analysis", "build.cmd clean --exports --game-data", "build.cmd clean --purge --yes"], ExecuteCleanWorkflow),
            new("auto-deploy", "Core", "Configure automatic post-build deploy", "Configure automatic post-build deployment.", ["--game-dir <path> (optional).", "--refresh-game-dir (optional)."], ["build.cmd auto-deploy", "build.cmd auto-deploy --game-dir \"C:\\Games\\Final Fantasy X-X2 - HD Remaster\""], SetupAutoDeployCore),
            new("doctor", "Core", "Diagnose local toolchain/environment state", "Diagnose local toolchain and environment state.", ["--full (optional)."], ["build.cmd doctor", "build.cmd doctor --full"], RunDoctorCore),
            new("format", "Core", "Auto-fix code formatting/style", "Apply code formatting/style fixes using dotnet format.", [], ["build.cmd format"], RunFormatFixCore),
            new("lint", "Core", "Run fast lint/compile checks", "Run fast lint/compile checks for build, mod, and tests projects.", ["--configuration Debug|Release (optional).", "--config-path <path-to-config.local.json> (optional, shorthand: -c)."], ["build.cmd lint"], () => RunLintCore(RequestedConfiguration)),
            new("smoke", "Core", "Run quick end-to-end sanity checks", "Run quick sanity checks against a full build.", ["--configuration Debug|Release (optional).", "--config-path <path-to-config.local.json> (optional, shorthand: -c)."], ["build.cmd smoke"], () => RunSmokeCore(RequestedConfiguration)),
            new("verify", "Core", "Build full payload (config Configuration by default) + run tests", "Run local validation without deploy side effects.", ["--configuration Debug|Release (optional).", "--config-path <path-to-config.local.json> (optional, shorthand: -c).", "--repo owner/repo (optional)."], ["build.cmd verify"], ExecuteVerifyWorkflow),
            new("build", "Core", "Build full payload", "Build full Fahrenheit payload.", ["--configuration Debug|Release (optional).", "--config-path <path-to-config.local.json> (optional, shorthand: -c).", "--auto-deploy or --no-auto-deploy (optional).", "--dry-run (optional)."], ["build.cmd build", "build.cmd build --configuration Release"], ExecuteBuildWorkflow),
            new("deploy", "Core", "Deploy artifacts to game directory", "Deploy full build artifacts into GameDir.", ["--game-dir <path> (optional).", "--refresh-game-dir (optional).", "--configuration Debug|Release (optional).", "--config-path <path-to-config.local.json> (optional, shorthand: -c).", "--dry-run (optional)."], ["build.cmd deploy"], ExecuteDeployWorkflow),
            new("start", "Core", "Launch fhstage0.exe", "Launch the game via deployed Fahrenheit stage0 loader.", ["--game-dir <path> (optional).", "--refresh-game-dir (optional).", "--elevated (optional)."], ["build.cmd start"], ExecuteStartWorkflow),
            new("release-bump", "Release", "Bump version + changelog + tag", "Bump version and create release commit/tag.", ["--bump patch|minor|major (optional)."], ["build.cmd release-bump"], ReleaseVersionCore),
            new("release-ready", "Release", "Preflight checks/build/package/notes", "Run release preflight.", ["--configuration Debug|Release (optional).", "--config-path <path-to-config.local.json> (optional, shorthand: -c).", "--repo owner/repo (optional)."], ["build.cmd release-ready"], ReleaseReadyCore),
            new("release-pack", "Release", "Create release ZIP assets", "Package built release payloads into ZIP archives.", ["--tag vX.Y.Z (required).", "--deploy-dir <path> (optional).", "--out-dir <path> (optional)."], ["build.cmd release-pack --tag v0.0.1"], ExecuteReleasePackWorkflow),
            new("release-notes", "Release", "Generate release notes markdown", "Generate release-notes markdown/text for a tag.", ["--tag vX.Y.Z (required).", "--repo owner/repo (required).", "--out <path> (optional)."], ["build.cmd release-notes --tag v0.0.1 --repo owner/repo"], ExecuteReleaseNotesWorkflow),
            new("commit", "Commit", "Interactive/non-interactive Conventional Commit", "Create a Conventional Commit.", ["--type feat|fix|... (optional).", "--scope <scope> (optional).", "--subject \"message\" (required in non-interactive mode).", "--breaking (optional)."], ["build.cmd commit"], ExecuteCommitWorkflow),
            new("commit-check", "Commit", "Validate message/file/range commit input", "Validate a commit message string, a commit message file, or a git commit range.", ["--message \"...\" (optional).", "--commit-file <path> (optional).", "--range <BASE..HEAD> (optional)."], ["build.cmd commit-check --message \"feat: x\"", "build.cmd commit-check --range origin/main..HEAD"], ExecuteCommitCheckWorkflow),
            new("docs-sync", "Utility", "Regenerate build/tools automation docs", "Regenerate docs/automation.md and docs/tools-automation.md from workflow metadata.", [], ["build.cmd docs-sync"], SyncAutomationDocsCore)
        ];
    }

    IReadOnlyList<WorkflowDefinition> CreateToolsWorkflowDefinitions()
    {
        return
        [
            new("discord-setup", "Tooling Workflows (local-only)", "Install/update DiscordChatExporter CLI", "Install/update DiscordChatExporter CLI into .workspace/tools/DiscordChatExporter.", ["--discord-api <url> (optional)."], ["tools.cmd discord-setup"], SetupDiscordCore),
            new("discord-sync", "Tooling Workflows (local-only)", "Export Discord JSON/Markdown + media + enrichment", "Export Discord channels/threads into .workspace/discord.", ["--guild <serverId> (required).", "--channels <id1,id2,...> (optional).", "--full (optional).", "Missing DiscordChatExporter is auto-ensured via tools.cmd discord-setup in interactive mode.", "Workspace config uses strict PascalCase keys in .workspace/config.local.json (DiscordToken, VisionApiUrl, VisionApiKey, VisionModel, FetchRetries).", "Discord workflow config uses strict PascalCase keys in .workspace/discord/config.local.json (Blacklist[], Guilds[])."], ["tools.cmd discord-sync --guild 612363389003366405"], ExecuteDiscordSyncWorkflow),
            new("data-setup", "Tooling Workflows (local-only)", "Install/update local data tooling", "Install/update data tooling (VBFTool + FFXDataParser).", ["--parser-repo <url> (optional).", "--parser-dir <path> (optional).", "--parser-ref <git-ref> (optional).", "--vbf-api <url> (optional).", "--vbf-dir <path> (optional)."], ["tools.cmd data-setup"], ExecuteDataSetupWorkflow),
            new("data-extract", "Tooling Workflows (local-only)", "Extract VBF archives", "Extract FFX/FFX-2 data archives with VBFTool.", ["--vbf-game-dir <path> (optional).", "--extract-out <path> (optional).", "--extract-meta-menu or --no-extract-meta-menu (optional)."], ["tools.cmd data-extract"], ExtractGameDataCore),
            new("data-parse", "Tooling Workflows (local-only)", "Run one parser mode", "Run one parser mode and capture output as txt.", ["--data-mode <MODE> (optional).", "--data-args \"<arg1> <arg2>\" (optional).", "--input-dir <path> (optional).", "--out-dir <path> (optional).", "Missing tooling is auto-ensured via tools.cmd data-setup in interactive mode."], ["tools.cmd data-parse --data-mode READ_ALL_COMMANDS"], ExecuteDataParseWorkflow),
            new("data-parse-all", "Tooling Workflows (local-only)", "Run parser mode batch", "Run configured parser mode batch and capture all outputs.", ["--data-batch \"MODE1;MODE2 arg\" (optional).", "--input-dir <path> (optional).", "--out-dir <path> (optional).", "Missing tooling is auto-ensured via tools.cmd data-setup in interactive mode."], ["tools.cmd data-parse-all"], ExecuteDataParseAllWorkflow),
            new("map-import", "Tooling Workflows (local-only)", "Generate canonical mapping JSON", "Generate canonical locale/domain mapping JSON from parser outputs.", ["--map-source <path> (optional).", "--locales us,de,... (optional).", "--out-dir <path> (optional).", "Requires existing parser outputs under --out-dir. Run: .\\tools.cmd data-parse-all"], ["tools.cmd map-import"], ImportLocalizedMappingsCore),
            new("map-build", "Tooling Workflows (local-only)", "Build runtime mapping bundles", "Build runtime mapping bundles from canonical mapping JSON.", ["--map-source <path> (optional).", "--map-out <path> (optional).", "--map-publish <path> (optional).", "--locales us,de,... (optional)."], ["tools.cmd map-build"], BuildLocalizedBundlesCore),
            new("data-inventory", "Tooling Workflows (local-only)", "Generate DATA_TREE reports", "Generate DATA_TREE.txt summaries for extracted data folders.", ["--data-root-dir <path> (optional).", "--folders \"name1;name2\" (optional)."], ["tools.cmd data-inventory"], DataInventoryCore),
            new("data-offload", "Tooling Workflows (local-only)", "Copy/move data folders to NAS", "Move or copy large extracted data folders to NAS and optionally keep junctions.", ["--nas-dir <unc-path> (required).", "--offload-mode move|copy (optional).", "--keep-data-junction or --no-keep-data-junction (optional).", "--data-root-dir <path> (optional).", "--folders \"name1;name2\" (optional)."], ["tools.cmd data-offload --nas-dir \"\\\\10.0.10.50\\data\\archive\\final-fantasy-assets\""], () =>
            {
                DataInventoryCore();
                OffloadDataCore();
            }),
            new("ghidra-setup", "Tooling Workflows (local-only)", "Install/update Ghidra", "Install/update Ghidra into a repo-local tools directory.", ["--ghidra-api <url> (optional).", "--ghidra-dir <path> (optional)."], ["tools.cmd ghidra-setup"], SetupGhidraCore),
            new("ghidra-start", "Tooling Workflows (local-only)", "Start Ghidra", "Start the repo-local Ghidra launcher.", ["--ghidra-dir <path> (optional).", "Missing Ghidra is auto-ensured via tools.cmd ghidra-setup in interactive mode."], ["tools.cmd ghidra-start"], ExecuteGhidraStartWorkflow)
        ];
    }

    void ExecuteDiscordSyncWorkflow()
    {
        DiscordSyncCore();
    }

    void ExecuteDataSetupWorkflow()
    {
        SetupVbfExtractorCore();
        SetupDataParserCore();
    }

    void ExecuteDataParseWorkflow()
    {
        EnsureDataToolingReadyForParse("data-parse");
        ParseDataCore();
    }

    void ExecuteDataParseAllWorkflow()
    {
        EnsureDataToolingReadyForParse("data-parse-all");
        ParseDataAllCore();
    }

    void ExecuteGhidraStartWorkflow()
    {
        EnsureGhidraReadyForStart();
        StartGhidraCore();
    }

    void ExecuteSetupWorkflow()
    {
        EnsureLocalBuildPrerequisitesForSetup();
        EnsureGameDirConfiguredForSetup();
        var resolvedConfiguration = ResolveBuildConfiguration(RequestedConfiguration);
        SetupHooksCore();
        bool ranWorkspaceSetup = EnsureProjectWorkspaceSetup(resolvedConfiguration);
        SetupAutoDeployCore();
        if (ranWorkspaceSetup && InteractiveSession && AskYesNo("Run first full build now? (Recommended)", defaultYes: true))
        {
            RunBuildProjTarget("Build", resolvedConfiguration, includeNativeMsbuild: true, fahrenheitRef: ResolveFahrenheitRef(useReleaseRef: false));
        }
    }

    void ExecuteCleanWorkflow() => RunCleanCore();

    void ExecuteBuildWorkflow() => BuildCore("full", RequestedConfiguration, useReleaseRef: false);

    void ExecuteVerifyWorkflow()
    {
        if (!IsValidConventionalCommit("feat: selftest commit format") || IsValidConventionalCommit("invalid message"))
        {
            Fail("Commit validator selftest failed.");
        }

        RunVerifyCore(RequestedConfiguration);
    }

    void ExecuteDeployWorkflow() => DeployCore("full", RequestedConfiguration);

    void ExecuteStartWorkflow() => StartCore();

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
        if (!string.IsNullOrWhiteSpace(Range))
        {
            ValidateCommitRangeCore(Range);
            return;
        }

        if (!string.IsNullOrWhiteSpace(CommitFile))
        {
            ValidateCommitMessageFromFile(ResolvePath(CommitFile));
            return;
        }
        if (string.IsNullOrWhiteSpace(Message))
        {
            Fail("Missing --message, --commit-file, or --range.");
        }
        ValidateCommitMessageString(Message);
    }

}
