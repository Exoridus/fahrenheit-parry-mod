using Serilog;
using static Nuke.Common.Assert;

internal sealed partial class BuildScript
{
    void RunToolsCliWorkflow()
    {
        ValidateVerbosityFlags();

        var workflow = (Workflow ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(workflow) || workflow.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            ShowToolsHelpSummary();
            return;
        }

        var definition = TryGetToolWorkflow(workflow);
        if (definition is not null)
        {
            definition.Execute();
            return;
        }

        if (IsBuildWorkflow(workflow))
        {
            Fail($"Workflow '{workflow}' belongs to build.cmd. Use: build.cmd {workflow}");
        }

        Fail($"Unknown tools workflow '{workflow}'. Use: tools.cmd help");
    }

    void ShowToolsHelpSummary()
    {
        WriteHelpLine("Usage: tools.cmd <workflow> [options]");
        WriteHelpLine("Detailed help: tools.cmd -h <workflow>");
        WriteHelpLine("Bool options: --flag (true), --no-flag (false)");
        WriteHelpLine("Global verbosity: --verbosity|-v quiet|minimal|normal|detailed|diagnostic (default: normal)");
        WriteHelpLine("Recommended escalation: quiet -> normal -> detailed -> diagnostic");
        WriteHelpLine("Global config path: --config-path (shorthand: -c)");
        WriteHelpLine("Common shorthand: -c <config-path>, -n (--dry-run), -h (help)");
        WriteHelpLine("Agent guidance: use --verbosity quiet for routine tooling runs.");

        foreach (var section in ToolsWorkflowSections)
        {
            WriteHelpLine(string.Empty);
            WriteHelpLine($"{section.Heading}:");
            foreach (var workflow in section.Workflows)
            {
                WriteHelpLine($"  {workflow.Name,-14} {workflow.Summary}");
            }
        }
    }

    void ShowToolsHelpWorkflow(string workflowRaw)
    {
        var workflow = (workflowRaw ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(workflow))
        {
            ShowToolsHelpSummary();
            return;
        }

        if (TryGetToolWorkflow(workflow) is { } definition)
        {
            PrintHelpBlock(
                definition.Name,
                definition.Purpose,
                definition.Parameters,
                definition.Examples);
            return;
        }

        if (IsBuildWorkflow(workflow))
        {
            Fail($"Workflow '{workflow}' belongs to build.cmd. Use: build.cmd {workflow}");
        }

        Log.Warning($"Unknown tools workflow: {workflowRaw}");
        ShowToolsHelpSummary();
    }

    WorkflowHelpBlock CaptureToolsWorkflowHelpBlock(string workflowRaw)
    {
        var normalizedWorkflow = (workflowRaw ?? string.Empty).Trim().ToLowerInvariant();
        _isCapturingWorkflowHelp = true;
        _capturedWorkflowHelp = null;
        try
        {
            ShowToolsHelpWorkflow(normalizedWorkflow);
            return _capturedWorkflowHelp ?? new WorkflowHelpBlock(
                normalizedWorkflow,
                "Unknown workflow.",
                [],
                []);
        }
        finally
        {
            _isCapturingWorkflowHelp = false;
            _capturedWorkflowHelp = null;
        }
    }

    void FailWorkflowMovedToTools(string workflow)
    {
        if (!IsToolWorkflow(workflow))
        {
            Fail($"Workflow '{workflow}' is not a local tooling workflow.");
        }

        Fail($"Workflow '{workflow}' moved to tools.cmd. Use: tools.cmd {workflow}");
    }

    void EnsureLocalToolingReadyForWorkflow(
        string workflowName,
        string dependencyName,
        string setupWorkflowName,
        Func<(bool IsReady, string Details)> readinessProbe,
        Action setupAction)
    {
        var readiness = readinessProbe();
        if (readiness.IsReady)
        {
            return;
        }

        var setupCommand = $".\\tools.cmd {setupWorkflowName}";
        var details = string.IsNullOrWhiteSpace(readiness.Details)
            ? dependencyName
            : readiness.Details;

        if (!InteractiveSession)
        {
            Fail(
                $"{workflowName} requires {dependencyName}, but it is not ready ({details})." + Environment.NewLine +
                $"Run: {setupCommand}");
        }

        Log.Warning($"{workflowName} requires {dependencyName}, but it is not ready ({details}).");
        if (!AskYesNo($"Run '{setupCommand}' now?", defaultYes: true))
        {
            Fail($"{workflowName} canceled because required tooling is missing. Run: {setupCommand}");
        }

        setupAction();

        readiness = readinessProbe();
        if (readiness.IsReady)
        {
            return;
        }

        if (DryRun)
        {
            Log.Information($"[DRY-RUN] {dependencyName} is still missing after simulated setup. Continuing.");
            return;
        }

        details = string.IsNullOrWhiteSpace(readiness.Details)
            ? dependencyName
            : readiness.Details;
        Fail(
            $"{workflowName} setup did not produce a ready {dependencyName} ({details})." + Environment.NewLine +
            $"Run: {setupCommand}");
    }
}
