using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using Schemora.Host;
using Schemora.SqlServer;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class NugetBuildTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SchemoraNuget-" + Guid.NewGuid().ToString("N"));
    private readonly string source;
    private readonly string project;
    private readonly string data;

    public NugetBuildTests()
    {
        source = Path.Combine(directory, "source");
        data = Path.Combine(directory, "data");
        Directory.CreateDirectory(Path.Combine(source, "feed"));
        project = Path.Combine(source, "Main.sqlproj");
        File.WriteAllText(
            project,
            """
            <Project Sdk="Microsoft.Build.Sql/2.3.0">
              <PropertyGroup><Name>Main</Name><DSP>Microsoft.Data.Tools.Schema.Sql.Sql160DatabaseSchemaProvider</DSP></PropertyGroup>
              <ItemGroup><PackageReference Include="Synthetic.Shared" Version="1.0.0" /></ItemGroup>
            </Project>
            """
        );
        File.WriteAllText(Path.Combine(source, "Schema.sql"), "CREATE VIEW dbo.Merged AS SELECT Id FROM dbo.Shared;");
        File.WriteAllText(
            Path.Combine(source, "NuGet.Config"),
            """
            <configuration><packageSources><clear /><add key="fixture" value="feed" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>
            """
        );
        using var model = new TSqlModel(SqlServerVersion.Sql160, new TSqlModelOptions());
        model.AddObjects("CREATE TABLE dbo.Shared(Id int NOT NULL PRIMARY KEY);");
        var dacpac = Path.Combine(directory, "Synthetic.Shared.dacpac");
        DacPackageExtensions.BuildPackage(dacpac, model, new PackageMetadata { Name = "Synthetic.Shared", Version = "1.0.0" });
        using var package = ZipFile.Open(Path.Combine(source, "feed", "Synthetic.Shared.1.0.0.nupkg"), ZipArchiveMode.Create);
        package.CreateEntryFromFile(dacpac, "tools/Synthetic.Shared.dacpac");
        using var writer = new StreamWriter(package.CreateEntry("Synthetic.Shared.nuspec").Open(), new UTF8Encoding(false));
        writer.Write(
            """
            <?xml version="1.0"?>
            <package><metadata><id>Synthetic.Shared</id><version>1.0.0</version><authors>Schemora</authors><description>Generated schema fixture</description><packageTypes><packageType name="DACPAC" /></packageTypes></metadata></package>
            """
        );
    }

    [Fact]
    public void PrivateCliHomeNeverRunsFirstTimeSetupThatEditsTheUserPath()
    {
        var environment = SqlProjectBuilder.DotnetEnvironment(data, Path.Combine(data, "packages"));
        Assert.Equal(Path.Combine(data, "dotnet-home"), environment["DOTNET_CLI_HOME"]);
        Assert.Equal("false", environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"]);
        Assert.Equal("1", environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"]);
    }

    [Fact]
    public async Task PinnedDacpacPackageRestoresPrivatelyBuildsAndEntersTheExactManifest()
    {
        var builder = new SqlProjectBuilder(new Toolchain(data), new DacpacReader());
        var before = State(source);
        var plan = await builder.Plan(project);
        Assert.False(plan.Complete);
        Assert.Contains(plan.Diagnostics, d => d.Code == "BUILD_PACKAGE_RESTORE_REQUIRED");
        var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
        var built = await builder.BuildWithManifest(project, true, default);
        Assert.Equal(userPath, Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
        Assert.True(built.Manifest.Complete, JsonSerializer.Serialize(built.Manifest.Diagnostics));
        Assert.Contains(built.Graph.Objects ?? [], o => o.Name == "Merged");
        Assert.Equal(before, State(source));
        Assert.Single(built.Manifest.Packages!);
        Assert.Contains(
            built.Manifest.Inputs,
            i => i.Kind == "dacpac-reference" && i.Path.StartsWith(data, StringComparison.OrdinalIgnoreCase)
        );
        Assert.True((await builder.CheckManifest(built.Manifest)).Current);
        var package = built.Manifest.Inputs.Single(i =>
            i.Kind == "dacpac-reference" && i.Path.StartsWith(data, StringComparison.OrdinalIgnoreCase)
        );
        File.AppendAllText(package.Path, "synthetic changed bytes");
        Assert.False((await builder.CheckManifest(built.Manifest)).Current);
        Assert.Empty(Directory.GetDirectories(Path.Combine(data, "builds")));
    }

    [Fact]
    public async Task FloatingVersionsDoNotClaimCompleteOrRunRestore()
    {
        File.WriteAllText(project, File.ReadAllText(project).Replace("Version=\"1.0.0\"", "Version=\"1.*\"", StringComparison.Ordinal));
        var builder = new SqlProjectBuilder(new Toolchain(data), new DacpacReader());
        var plan = await builder.Plan(project);
        Assert.False(plan.Complete);
        Assert.Contains(plan.Diagnostics, d => d.Code == "BUILD_PACKAGE_VERSION_UNPINNED");
        var error = await Assert.ThrowsAsync<AppException>(() => builder.Build(project, true, default));
        Assert.Equal("BUILD_INPUTS_INCOMPLETE", error.Code);
        Assert.False(Directory.Exists(Path.Combine(data, "nuget-packages")));
    }

    [Fact]
    public async Task PackageBuildCodeIsDiagnosedInsteadOfBeingDeclaredVerified()
    {
        var cache = Path.Combine(data, "nuget-packages", "synthetic.shared", "1.0.0");
        Directory.CreateDirectory(Path.Combine(cache, "build"));
        File.Copy(Path.Combine(directory, "Synthetic.Shared.dacpac"), Path.Combine(cache, "Synthetic.Shared.dacpac"));
        File.WriteAllText(
            Path.Combine(cache, "build", "unverified.targets"),
            "<Project><Target Name=\"Run\"><Exec Command=\"never run\" /></Target></Project>"
        );
        var plan = await new SqlProjectBuilder(new Toolchain(data), new DacpacReader()).Plan(project);
        Assert.False(plan.Complete);
        Assert.Contains(plan.Diagnostics, d => d.Code == "BUILD_CUSTOM_TASK_INPUTS_UNVERIFIED");
    }

    [Fact]
    public async Task PackageHintPathOutsideSourceBecomesAVerifiedStagedInput()
    {
        File.WriteAllText(
            project,
            File.ReadAllText(project)
                .Replace(
                    "<PackageReference Include=\"Synthetic.Shared\" Version=\"1.0.0\" />",
                    "<PackageReference Include=\"Synthetic.Shared\" Version=\"1.0.0\"><HintPath>../Synthetic.Shared.dacpac</HintPath></PackageReference>",
                    StringComparison.Ordinal
                )
        );
        var builder = new SqlProjectBuilder(new Toolchain(data), new DacpacReader());
        var before = State(source);
        var built = await builder.BuildWithManifest(project, true, default);
        Assert.True(built.Manifest.Complete, JsonSerializer.Serialize(built.Manifest.Diagnostics));
        Assert.Contains(built.Manifest.Inputs, input => input.Path == Path.Combine(directory, "Synthetic.Shared.dacpac"));
        Assert.Equal(before, State(source));
        File.AppendAllText(Path.Combine(directory, "Synthetic.Shared.dacpac"), "changed");
        Assert.False((await builder.CheckManifest(built.Manifest)).Current);
    }

    private static string State(string path) =>
        string.Join(
            "\n",
            Directory
                .GetFiles(path, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Select(f => f + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))))
        );

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}
