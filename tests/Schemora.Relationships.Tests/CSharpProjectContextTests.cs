using Schemora.Relationships;
using Xunit;

namespace Schemora.Relationships.Tests;

public sealed class CSharpProjectContextTests
{
    [Fact]
    public void SelectsConfigurationAndCompileMembershipAndFingerprintsContent()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var project = Path.Combine(dir, "Test.csproj");
            var source = Path.Combine(dir, "Selected.cs");
            File.WriteAllText(source, "class Selected {}");
            File.WriteAllText(Path.Combine(dir, "Excluded.cs"), "class Excluded {}");
            File.WriteAllText(
                project,
                "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><PropertyGroup Condition=\"'$(Configuration)' == 'Release'\"><DefineConstants>CHOSEN</DefineConstants></PropertyGroup><ItemGroup><Compile Include=\"Selected.cs\" /></ItemGroup></Project>"
            );
            var evaluator = new CSharpProjectContextEvaluator();
            var selected = evaluator.Evaluate(new(project, "Release", "net10.0"));
            Assert.Single(selected.Files);
            Assert.Equal(source, selected.Files[0].Path);
            Assert.Contains("CHOSEN", selected.Files[0].Defines);
            Assert.DoesNotContain("DEBUG", selected.Files[0].Defines);
            File.WriteAllText(source, "class Changed {}");
            Assert.NotEqual(selected.Fingerprint, evaluator.Evaluate(selected.Selection).Fingerprint);
            Assert.DoesNotContain("CHOSEN", evaluator.Evaluate(new(project, "Debug", "net10.0")).Files[0].Defines);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task TrustedEvaluationRequiresExplicitTrust() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CSharpTrustedProjectEvaluator().EvaluateAsync(new("untrusted.csproj"), false)
        );

    [Fact]
    public void ImportedPropertyContentInvalidatesFingerprintWithoutExecutingOrEvaluatingIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var project = Path.Combine(dir, "Repo.csproj");
            var imported = Path.Combine(dir, "common.props");
            File.WriteAllText(
                project,
                "<Project><Import Project=\"common.props\"/><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
            );
            File.WriteAllText(imported, "<Project><PropertyGroup><DefineConstants>IMPORTED</DefineConstants></PropertyGroup></Project>");
            var evaluator = new CSharpProjectContextEvaluator();
            var first = evaluator.Evaluate(new(project));
            Assert.False(first.IsComplete);
            Assert.Contains(imported, first.ProjectInputs!);
            File.WriteAllText(imported, "<Project><PropertyGroup><DefineConstants>CHANGED</DefineConstants></PropertyGroup></Project>");
            Assert.NotEqual(first.Fingerprint, evaluator.Evaluate(new(project)).Fingerprint);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void UnsupportedBuildLogicIsPartialAndNeverExecuted()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var project = Path.Combine(dir, "Test.csproj");
            File.WriteAllText(
                project,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><Target Name=\"Danger\"><Exec Command=\"invalid\" /></Target></Project>"
            );
            var result = new CSharpProjectContextEvaluator().Evaluate(new(project));
            Assert.False(result.IsComplete);
            Assert.Contains(result.Diagnostics, d => d.Code == "CSHARP_PROJECT_PARTIAL");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
