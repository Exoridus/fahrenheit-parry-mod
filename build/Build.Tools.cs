using Serilog;
using static Nuke.Common.Assert;

internal sealed partial class BuildScript
{
    void RunToolsCliWorkflow()
    {
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
        Log.Information("Usage: tools.cmd <workflow> [options]");
        Log.Information("Detailed help: tools.cmd -h <workflow>");
        Log.Information("Bool options: --flag (true), --no-flag (false)");

        foreach (var section in ToolsWorkflowSections)
        {
            Log.Information(string.Empty);
            Log.Information($"{section.Heading}:");
            foreach (var workflow in section.Workflows)
            {
                Log.Information($"  {workflow.Name,-14} {workflow.Summary}");
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
}
