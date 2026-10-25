using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Schemora.Host;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class NugetBoundaryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SchemoraNugetBoundary-" + Guid.NewGuid().ToString("N"));
    private string Project => Path.Combine(directory, "source", "Main.sqlproj");
    private string Data => Path.Combine(directory, "data");
    private string Cache => Path.Combine(Data, "nuget-packages", "synthetic.shared", "1.0.0");

    public NugetBoundaryTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Project)!);
        Directory.CreateDirectory(Cache);
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(Project)!, "Schema.sql"),
            "CREATE TABLE dbo.Own(Id int NOT NULL PRIMARY KEY);"
        );
        ProjectItems("<PackageReference Include=\"Synthetic.Shared\" Version=\"1.0.0\" />");
        File.WriteAllText(Path.Combine(Cache, "Synthetic.Shared.dacpac"), "synthetic fingerprint-only package input");
        File.WriteAllText(
            Path.Combine(Cache, "synthetic.shared.nuspec"),
            "<package><metadata><id>Synthetic.Shared</id><version>1.0.0</version></metadata></package>"
        );
        var archive = Path.Combine(Cache, "synthetic.shared.1.0.0.nupkg");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(Path.Combine(Cache, "Synthetic.Shared.dacpac"), "Synthetic.Shared.dacpac");
            zip.CreateEntryFromFile(Path.Combine(Cache, "synthetic.shared.nuspec"), "synthetic.shared.nuspec");
        }
        var hash = Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(archive)));
        File.WriteAllText(archive + ".sha512", hash);
        File.WriteAllText(Path.Combine(Cache, ".nupkg.metadata"), JsonSerializer.Serialize(new { version = 2, contentHash = hash }));
    }

    private void ProjectItems(string items) =>
        File.WriteAllText(Project, "<Project Sdk=\"Microsoft.Build.Sql/2.3.0\"><ItemGroup>" + items + "</ItemGroup></Project>");

    private Task<BuildInputManifest> Plan() =>
        new SqlProjectBuilder(new Toolchain(Data), new Schemora.SqlServer.DacpacReader()).Plan(Project);

    [Fact]
    public async Task FullyRestoredPinnedCacheIsCompleteAndIncludesArchiveProvenance()
    {
        var plan = await Plan();
        Assert.True(plan.Complete, JsonSerializer.Serialize(plan.Diagnostics));
        Assert.Contains(plan.Inputs, input => input.Path.EndsWith(".nupkg.metadata"));
        Assert.Contains(plan.Inputs, input => input.Path.EndsWith(".nupkg.sha512"));
    }

    [Theory]
    [InlineData(".nupkg.metadata")]
    [InlineData("synthetic.shared.nuspec")]
    [InlineData("synthetic.shared.1.0.0.nupkg")]
    [InlineData("synthetic.shared.1.0.0.nupkg.sha512")]
    public async Task PartialRestoreCacheNeverClaimsComplete(string missing)
    {
        File.Delete(Path.Combine(Cache, missing));
        var plan = await Plan();
        Assert.False(plan.Complete);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == "BUILD_PACKAGE_CACHE_UNVERIFIED");
    }

    [Fact]
    public async Task WrongArchiveHashAndWrongNuspecIdentityAreUnverified()
    {
        File.WriteAllText(Path.Combine(Cache, "synthetic.shared.1.0.0.nupkg.sha512"), "incorrect");
        File.WriteAllText(
            Path.Combine(Cache, "synthetic.shared.nuspec"),
            "<package><metadata><id>Different.Package</id><version>9.0.0</version></metadata></package>"
        );
        var plan = await Plan();
        Assert.False(plan.Complete);
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == "BUILD_PACKAGE_CACHE_UNVERIFIED" && diagnostic.Message.Contains("SHA512")
        );
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == "BUILD_PACKAGE_CACHE_UNVERIFIED" && diagnostic.Message.Contains("identity")
        );
    }

    [Fact]
    public async Task ConditionalVersionMetadataIsExplicitlyIncomplete()
    {
        ProjectItems(
            "<PackageReference Include=\"Synthetic.Shared\"><Version Condition=\"'$(Configuration)' == 'Debug'\">1.0.0</Version><Version Condition=\"'$(Configuration)' == 'Release'\">2.0.0</Version></PackageReference>"
        );
        var plan = await Plan();
        Assert.False(plan.Complete);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == "BUILD_PACKAGE_METADATA_UNVERIFIED");
    }

    [Fact]
    public async Task ConflictingReferencesDoNotChooseAnArbitraryVersion()
    {
        ProjectItems(
            "<PackageReference Include=\"Synthetic.Shared\" Version=\"1.0.0\" /><PackageReference Include=\"Synthetic.Shared\" Version=\"2.0.0\" />"
        );
        var plan = await Plan();
        Assert.False(plan.Complete);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == "BUILD_PACKAGE_VERSION_CONFLICT");
    }

    [Fact]
    public async Task CentralVersionsRequireCentralManagementToBeEnabled()
    {
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(Project)!, "Directory.Packages.props"),
            "<Project><ItemGroup><PackageVersion Include=\"Synthetic.Shared\" Version=\"1.0.0\" /></ItemGroup></Project>"
        );
        ProjectItems("<PackageReference Include=\"Synthetic.Shared\" />");
        var plan = await Plan();
        Assert.False(plan.Complete);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == "BUILD_PACKAGE_METADATA_UNVERIFIED");
    }

    [Fact]
    public async Task GlobalPackagesCannotBypassClosureVerification()
    {
        ProjectItems("<GlobalPackageReference Include=\"Synthetic.BuildCode\" Version=\"1.0.0\" />");
        var plan = await Plan();
        Assert.False(plan.Complete);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == "BUILD_PACKAGE_GLOBAL_UNVERIFIED");
    }

    [Fact]
    public async Task PackagePathPropertyNeedsExplicitGenerationOrToolsProof()
    {
        ProjectItems(
            "<PackageReference Include=\"Synthetic.Shared\" Version=\"1.0.0\" /><ArtifactReference Include=\"Shared\"><HintPath>$(PkgSynthetic_Shared)/Synthetic.Shared.dacpac</HintPath></ArtifactReference>"
        );
        Assert.False((await Plan()).Complete);
        ProjectItems(
            "<PackageReference Include=\"Synthetic.Shared\" Version=\"1.0.0\" GeneratePathProperty=\"true\" /><ArtifactReference Include=\"Shared\"><HintPath>$(PkgSynthetic_Shared)/Synthetic.Shared.dacpac</HintPath></ArtifactReference>"
        );
        Assert.True((await Plan()).Complete);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}
