using System.Text;
using Serilog;

internal sealed partial class BuildScript
{
    static readonly (string Heading, string[] Workflows)[] WorkflowDocSections =
    [
        ("Core Workflows", ["install", "setup", "clean", "auto-deploy", "doctor", "format", "docs-sync", "lint", "smoke", "verify", "build", "deploy", "start"]),
        ("Data + Mappings", ["discord-sync", "data-setup", "ghidra-setup", "ghidra-start", "data-extract", "data-parse", "data-parse-all", "map-import", "map-build", "data-inventory", "data-offload"]),
        ("Release Workflows", ["release-bump", "release-ready", "release-pack", "release-notes"]),
        ("Commit Workflows", ["commit", "commit-check", "commit-range"])
    ];

    void SyncAutomationDocsCore()
    {
        var docsPath = ResolvePath("docs/automation.md");

        var sb = new StringBuilder();
        sb.AppendLine("# Automation Overview");
        sb.AppendLine();
        sb.AppendLine("`build.cmd` is the single local entrypoint.");
        sb.AppendLine();
        sb.AppendLine("_Auto-generated from build help metadata. Do not edit manually; run `.\\build.cmd docs-sync`._");
        sb.AppendLine();
        sb.AppendLine("Quick command discovery:");
        sb.AppendLine("- `.\\build.cmd help`");
        sb.AppendLine("- `.\\build.cmd -h <workflow>`");
        sb.AppendLine("- Bool parameters support both `--flag` and `--no-flag`.");
        sb.AppendLine();

        foreach (var section in WorkflowDocSections)
        {
            sb.AppendLine($"## {section.Heading}");
            sb.AppendLine();

            foreach (var workflow in section.Workflows)
            {
                var help = CaptureWorkflowHelpBlock(workflow);

                sb.AppendLine($"- `.\\build.cmd {help.Workflow}`");
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
                        var normalizedExample = example.Replace("build.cmd", ".\\build.cmd", StringComparison.OrdinalIgnoreCase);
                        sb.AppendLine($"  - `{normalizedExample}`");
                    }
                }

                sb.AppendLine();
            }
        }

        File.WriteAllText(docsPath, sb.ToString());
        Log.Information($"Updated automation docs: {docsPath}");
    }
}
