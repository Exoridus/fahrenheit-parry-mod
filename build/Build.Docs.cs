using System.Text;
using Serilog;
using static Nuke.Common.Assert;

internal sealed partial class BuildScript
{
    void SyncAutomationDocsCore()
    {
        var buildDocsPath = ResolvePath("docs/automation.md");
        var toolsDocsPath = ResolvePath("docs/tools-automation.md");

        File.WriteAllText(buildDocsPath, GenerateBuildAutomationDocs());
        File.WriteAllText(toolsDocsPath, GenerateToolsAutomationDocs());

        Log.Information($"Updated automation docs: {buildDocsPath}");
        Log.Information($"Updated tools automation docs: {toolsDocsPath}");
    }

    void AssertAutomationDocsUpToDate()
    {
        AssertDocsMatchGenerated("docs/automation.md", GenerateBuildAutomationDocs());
        AssertDocsMatchGenerated("docs/tools-automation.md", GenerateToolsAutomationDocs());
    }

    void AssertDocsMatchGenerated(string relativePath, string expectedContent)
    {
        var fullPath = ResolvePath(relativePath);
        if (!File.Exists(fullPath))
        {
            Fail($"Missing generated docs file '{relativePath}'. Run: .\\build.cmd docs-sync");
        }

        var currentContent = File.ReadAllText(fullPath);
        if (!string.Equals(currentContent, expectedContent, StringComparison.Ordinal))
        {
            Fail($"Generated docs '{relativePath}' are stale. Run: .\\build.cmd docs-sync");
        }
    }

    string GenerateBuildAutomationDocs()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Automation Overview");
        sb.AppendLine();
        sb.AppendLine("`build.cmd` is the project lifecycle entrypoint.");
        sb.AppendLine();
        sb.AppendLine("_Auto-generated from build workflow metadata. Do not edit manually; run `.\\build.cmd docs-sync`._");
        sb.AppendLine();
        sb.AppendLine("Quick command discovery:");
        sb.AppendLine("- `.\\build.cmd help`");
        sb.AppendLine("- `.\\build.cmd -h <workflow>`");
        sb.AppendLine("- Bool parameters support both `--flag` and `--no-flag`.");
        sb.AppendLine("- Local research/tooling workflows moved to `tools.cmd` (`tools.cmd help`).");
        sb.AppendLine();

        foreach (var section in BuildWorkflowSections)
        {
            sb.AppendLine($"## {section.Heading} Workflows");
            sb.AppendLine();

            foreach (var workflow in section.Workflows)
            {
                var help = CaptureWorkflowHelpBlock(workflow.Name);
                AppendWorkflowHelpBlock(sb, ".\\build.cmd", help, "build.cmd");
            }
        }

        return sb.ToString();
    }

    string GenerateToolsAutomationDocs()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Tools Automation Overview");
        sb.AppendLine();
        sb.AppendLine("`tools.cmd` contains local-only tooling workflows.");
        sb.AppendLine();
        sb.AppendLine("_Auto-generated from tools workflow metadata. Do not edit manually; run `.\\build.cmd docs-sync`._");
        sb.AppendLine();
        sb.AppendLine("Quick command discovery:");
        sb.AppendLine("- `.\\tools.cmd help`");
        sb.AppendLine("- `.\\tools.cmd -h <workflow>`");
        sb.AppendLine("- Bool parameters support both `--flag` and `--no-flag`.");
        sb.AppendLine();

        foreach (var section in ToolsWorkflowSections)
        {
            sb.AppendLine($"## {section.Heading}");
            sb.AppendLine();

            foreach (var workflow in section.Workflows)
            {
                var help = CaptureToolsWorkflowHelpBlock(workflow.Name);
                AppendWorkflowHelpBlock(sb, ".\\tools.cmd", help, "tools.cmd");
            }
        }

        return sb.ToString();
    }

    static void AppendWorkflowHelpBlock(StringBuilder sb, string commandPrefix, WorkflowHelpBlock help, string replaceToken)
    {
        sb.AppendLine($"- `{commandPrefix} {help.Workflow}`");
        sb.AppendLine($"  - {help.Purpose}");

        if (help.Parameters.Count > 0)
        {
            sb.AppendLine("  - Parameters:");
            foreach (var parameter in help.Parameters)
            {
                sb.AppendLine($"  - {parameter}");
            }
        }

        if (help.Examples.Count > 0)
        {
            sb.AppendLine("  - Examples:");
            foreach (var example in help.Examples)
            {
                var normalizedExample = example.Replace(replaceToken, commandPrefix, StringComparison.OrdinalIgnoreCase);
                sb.AppendLine($"  - `{normalizedExample}`");
            }
        }

        sb.AppendLine();
    }
}
