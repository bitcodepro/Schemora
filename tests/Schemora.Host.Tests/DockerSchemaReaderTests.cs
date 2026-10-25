using System.IO.Compression;
using Schemora.SqlServer;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class DockerSchemaReaderTests
{
    [Fact]
    public async Task OptInIsRequiredBeforeAnyPathOrDockerAction()
    {
        var reader = new DockerSchemaReader(new Toolchain(AppContext.BaseDirectory), new DacpacReader());
        var error = await Assert.ThrowsAsync<AppException>(() => reader.Read(null, false, CancellationToken.None));
        Assert.Equal("DOCKER_OPT_IN_REQUIRED", error.Code);
        Assert.Equal(403, error.Status);
    }

    [Theory]
    [InlineData("predeploy.sql")]
    [InlineData("postdeploy.sql")]
    [InlineData("Data/dbo.Items.bcp")]
    [InlineData("scripts/innocent.sql")]
    [InlineData("../model.xml")]
    public void SchemaOnlyGuardRejectsAllScriptDataAndUnknownEntries(string payload)
    {
        WithPackage(
            ["model.xml", "DacMetadata.xml", payload],
            path =>
                Assert.Equal(
                    "DACPAC_SCHEMA_ONLY_REQUIRED",
                    Assert.Throws<AppException>(() => DockerSchemaReader.AssertSchemaOnly(path)).Code
                )
        );
    }

    [Fact]
    public void GuardRequiresSchemaMetadataAndRejectsDuplicateNames()
    {
        WithPackage(
            ["model.xml"],
            path => Assert.Equal("DACPAC_INVALID", Assert.Throws<AppException>(() => DockerSchemaReader.AssertSchemaOnly(path)).Code)
        );
        WithPackage(
            ["model.xml", "DacMetadata.xml", "MODEL.XML"],
            path =>
                Assert.Equal(
                    "DACPAC_SCHEMA_ONLY_REQUIRED",
                    Assert.Throws<AppException>(() => DockerSchemaReader.AssertSchemaOnly(path)).Code
                )
        );
        WithPackage(["model.xml", "DacMetadata.xml", "Origin.xml", "[Content_Types].xml"], DockerSchemaReader.AssertSchemaOnly);
    }

    private static void WithPackage(string[] names, Action<string> assert)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "work", "docker-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "guard.dacpac");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                foreach (var name in names)
                {
                    var entry = zip.CreateEntry(name);
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write("<synthetic />");
                }
            assert(path);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
