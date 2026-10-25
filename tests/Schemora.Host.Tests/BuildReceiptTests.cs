using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Schemora.Host;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class BuildReceiptTests
{
    [Fact]
    public async Task LegacyReceiptReadsEngineFileVersionWithoutLaunchingTheExecutable()
    {
        await WithLegacyFixture(async executable =>
        {
            var engine = Path.Combine(Path.GetDirectoryName(executable)!, "Microsoft.Build.dll");
            var version = FileVersionInfo.GetVersionInfo(engine);
            var expected = new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart, version.FilePrivatePart);
            // This is a copied DLL, not a runnable MSBuild.exe, and the working directory does not exist.
            // A successful capture therefore proves that receipt collection does not launch a process.
            var receipt = await BuildToolCapture.Capture(executable, false, null, Path.Combine(engine, "missing"), default);
            Assert.Equal("Legacy SSDT", receipt.Kind);
            Assert.Equal(expected.ToString(), receipt.MsBuildVersion);
            Assert.Equal(BuildToolCapture.Hash(executable), receipt.ExecutableSha256);
            Assert.Equal(receipt.ExecutableSha256, receipt.MsBuildSha256);
            Assert.Null(receipt.SdkVersion);
            Assert.Equal(receipt, await BuildToolCapture.Capture(executable, false, null, "", default));

            // Hashes remain fresh; reading version metadata must not introduce a process-wide receipt cache.
            await File.AppendAllTextAsync(executable, "changed executable bytes");
            var changed = await BuildToolCapture.Capture(executable, false, null, "", default);
            Assert.NotEqual(receipt.ExecutableSha256, changed.ExecutableSha256);
            Assert.Equal(changed.ExecutableSha256, changed.MsBuildSha256);
            Assert.Equal(receipt.MsBuildVersion, changed.MsBuildVersion);
        });
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("")]
    [InlineData("not a versioned PE file")]
    public async Task LegacyReceiptRejectsMissingOrUnversionedEngineMetadata(string contents)
    {
        await WithLegacyFixture(async executable =>
        {
            var engine = Path.Combine(Path.GetDirectoryName(executable)!, "Microsoft.Build.dll");
            File.Delete(engine);
            if (contents != "missing")
                await File.WriteAllTextAsync(engine, contents);
            var error = await Assert.ThrowsAsync<AppException>(() => BuildToolCapture.Capture(executable, false, null, "", default));
            Assert.Equal("BUILD_TOOL_VERSION_UNAVAILABLE", error.Code);
        });
    }

    [Fact]
    public async Task LegacyReceiptHonorsCancellationBeforeReadingMetadata()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BuildToolCapture.Capture("missing.exe", false, null, "", cancellation.Token)
        );
    }

    private static async Task WithLegacyFixture(Func<string, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SchemoraLegacyReceipt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "MSBuild.exe");
            File.Copy(typeof(BuildToolCapture).Assembly.Location, executable);
            File.Copy(typeof(BuildToolCapture).Assembly.Location, Path.Combine(directory, "Microsoft.Build.dll"));
            await action(executable);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void PendingSnapshotsContributeToolReceiptsBeforeAtomicPublicationAndOldManifestsStayUnknown()
    {
        var directory = Path.Combine(Path.GetTempPath(), "build-receipt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CatalogStore(Path.Combine(directory, "catalog.sqlite"));
            var id = Guid.NewGuid().ToString("N");
            var tools = new BuildToolReceipt(
                "SDK-style",
                "dotnet",
                "exe-hash",
                "10.0.402",
                "18.0.0",
                "msbuild-hash",
                null,
                null,
                null,
                "Microsoft.Build.Sql/2.3.0"
            );
            var manifest = new BuildInputManifest(
                id,
                "project.sqlproj",
                DateTimeOffset.UtcNow,
                "Release",
                [],
                true,
                [],
                "h",
                [],
                new Dictionary<string, string>(),
                Toolchain: tools
            );
            var folder = Path.Combine(directory, "build-manifests");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, id + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, CatalogStore.Json));
            var pending = new SnapshotRecord(
                "snapshot",
                "ws",
                "db",
                DateTimeOffset.UtcNow,
                "rebuild",
                "h",
                new SchemaGraph("demo", "project.sqlproj", DateTimeOffset.UtcNow, [], [], []),
                id
            );
            var db = new DatabaseInfo("db", "ws", "demo", "project.sqlproj", "sqlProject", "ready", pending.Id, null);
            Assert.Null(store.Get<SnapshotRecord>("SchemaSnapshots", pending.Id));
            var result = Assert.Single(BuildToolCapture.FromSnapshots(store, [db], [pending]));
            Assert.Equal(tools, result.Toolchain);
            File.WriteAllText(path, JsonSerializer.Serialize(manifest with { Toolchain = null }, CatalogStore.Json));
            Assert.Null(Assert.Single(BuildToolCapture.FromSnapshots(store, [db], [pending])).Toolchain);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
