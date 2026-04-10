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
        return [];
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
