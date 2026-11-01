using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

internal sealed partial class BuildScript
{
    void ExecuteVrrIntegrationTests()
    {
        if (string.IsNullOrWhiteSpace(VrrSourceDir))
            throw new ArgumentException("--vrr-source-dir is required; select the source revision explicitly.");
        string source = Path.GetFullPath(VrrSourceDir, RootDirectory);
        string[] vrrFiles = ["FixedSimulationClock.cs", "VrrModule.CooperativeTicks.cs", "RenderPassContext.cs", "PresentationAdmission.cs"];
        string[] inputs = vrrFiles.Select(name => Path.Combine(source, "src", name))
            .Concat(new[] { "ParryModule.VrrSimulation.cs", "ParryModule.WindowTiming.cs", "combat/ParryInputState.cs", "combat/ParryInputStateTransitions.cs" }
                .Select(name => Path.Combine(RootDirectory, "src", name)))
            .Concat(new[] { "VrrSimulationFixture.cs", "ParryWindowTimingFixture.cs" }
                .Select(name => Path.Combine(RootDirectory, "build", "tests", name)))
            .Concat(Directory.GetFiles(Path.Combine(RootDirectory, "build", "integration-tests"), "*", SearchOption.TopDirectoryOnly))
            .Concat(new[] { "build/Build.VrrIntegration.cs", "build/Build.cs", "build/Build.csproj", "global.json", "NuGet.config" }
                .Select(name => Path.Combine(RootDirectory, name)).Where(File.Exists))
            .ToArray();
        var hashes = inputs.ToDictionary(path => path,
            path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        string results = Path.Combine(RootDirectory, ".workspace", "vrr-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(results);
        string sdk = RunProcess("dotnet", "--version", "Read integration SDK version", silent: true).StdOut.Trim();
        RunChecked("dotnet", $"test {Quote(Path.Combine(RootDirectory, "build", "integration-tests", "VrrIntegration.Tests.csproj"))} -c Release -p:VrrSourceDir={Quote(source)} --verbosity minimal --logger {Quote("trx;LogFileName=results.trx")} --results-directory {Quote(results)}",
            "Parry/VRR source integration");
        if (hashes.Any(input => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(input.Key))) != input.Value))
            throw new InvalidOperationException("Integration sources changed during verification; rerun against a stable snapshot.");
        string resultPath = Path.Combine(results, "results.trx");
        var counters = XDocument.Load(resultPath).Descendants().Single(node => node.Name.LocalName == "Counters");
        if ((int?)counters.Attribute("executed") is not > 0)
            throw new InvalidOperationException("The integration runner executed no tests.");
        File.WriteAllText(Path.Combine(results, "provenance.json"), JsonSerializer.Serialize(new
        {
            workflow = "verify-vrr", run_at = DateTimeOffset.UtcNow, vrr_source = source, sdk, configuration = "Release",
            inputs = hashes.Select(input => new { path = input.Key, sha256 = input.Value }),
            outputs = new[] { new { path = resultPath, sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(resultPath))) } },
            tests = counters.Attributes().ToDictionary(attribute => attribute.Name.LocalName, attribute => attribute.Value),
            native_gameplay_validated = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
