using System.Text;
using Serilog;
using static Nuke.Common.Assert;

internal sealed partial class BuildScript
{
    bool IsValidConventionalCommit(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return false;

        var trimmed = subject.Trim();
        if (trimmed.StartsWith("Merge ", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.StartsWith("Revert ", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.StartsWith("fixup!", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.StartsWith("squash!", StringComparison.OrdinalIgnoreCase)) return true;

        var idx = trimmed.IndexOf(':');
        if (idx <= 0 || idx + 1 >= trimmed.Length) return false;

        var head = trimmed[..idx];
        var body = trimmed[(idx + 1)..].Trim();
        if (body.Length == 0) return false;

        var typeEnd = head.IndexOf('(');
        var type = typeEnd >= 0 ? head[..typeEnd] : head.TrimEnd('!');
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "build", "chore", "ci", "docs", "feat", "fix", "perf", "refactor", "revert", "style", "test"
        };

        return allowed.Contains(type.Trim('!', ' '));
    }

    string BuildCommitSubject(string type, string scope, string message, bool breaking)
    {
        var sb = new StringBuilder(type.Trim());
        if (!string.IsNullOrWhiteSpace(scope)) sb.Append('(').Append(scope.Trim()).Append(')');
        if (breaking) sb.Append('!');
        sb.Append(": ").Append(message.Trim());
        return sb.ToString();
    }

    CommitWizardResult RunCommitWizard()
    {
        var types = new[]
        {
            "feat", "fix", "docs", "chore", "ci", "build", "refactor", "perf", "test", "style", "revert"
        };

        Log.Information("Interactive Conventional Commit wizard");
        Log.Information("Select commit type:");
        for (var i = 0; i < types.Length; i++)
        {
            var suffix = i == 0 ? " (default)" : string.Empty;
            Log.Information($"  {i + 1}) {types[i]}{suffix}");
        }

        Console.Write("Type [1-11] (default 1): ");
        var typeInput = (Console.ReadLine() ?? string.Empty).Trim();
        var typeIndex = int.TryParse(typeInput, out var idx) ? idx - 1 : 0;
        if (typeIndex < 0 || typeIndex >= types.Length)
        {
            typeIndex = 0;
        }

        var selectedType = types[typeIndex];

        Console.Write("Scope (optional, empty for none): ");
        var scope = (Console.ReadLine() ?? string.Empty).Trim();

        string message;
        while (true)
        {
            Console.Write("Message (required): ");
            message = (Console.ReadLine() ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(message))
            {
                break;
            }

            Log.Warning("Message cannot be empty.");
        }

        var breaking = AskYesNo("Breaking change?", defaultYes: false);
        var subject = BuildCommitSubject(selectedType, scope, message, breaking);
        if (!IsValidConventionalCommit(subject))
        {
            Fail($"Invalid commit subject generated: {subject}");
        }

        Log.Information($"Preview: {subject}");
        var confirmed = AskYesNo("Create this commit?", defaultYes: true);

        return new CommitWizardResult(selectedType, scope, message, breaking, confirmed);
    }
}
