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
