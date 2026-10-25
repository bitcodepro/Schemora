using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Schemora.Host;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Schemora.Workspaces.Discovery;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class SnapshotAnalysisUpgradeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SchemoraAnalysisUpgrade-" + Guid.NewGuid().ToString("N"));
    private readonly CatalogStore store;
    private readonly Workspace workspace;
    private readonly WorkspaceScanner scanner;

    public SnapshotAnalysisUpgradeTests()
    {
        var root = Path.Combine(directory, "sources");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "Schema.sql"),
            """
            CREATE TABLE dbo.Items(Id int NOT NULL PRIMARY KEY,Code nvarchar(20) NULL);
            GO
            CREATE INDEX IX_Items_Code ON dbo.Items(Code);
            GO
            CREATE VIEW dbo.CurrentItems AS SELECT Id,Code FROM dbo.Items;
            """
        );
        var data = Path.Combine(directory, "data");
        store = new(Path.Combine(data, "catalog.sqlite"));
        workspace = new("ws", "Upgrade", [root], [], DateTimeOffset.UtcNow);
        store.Put("Workspaces", workspace.Id, null, workspace);
        var reader = new DacpacReader();
        scanner = new(
            store,
            new WorkspaceDiscovery(),
            reader,
            new StaticSqlReader(),
            new SqlSourceClassifier(),
            new SqlProjectBuilder(new Toolchain(data), reader)
        );
    }

    [Fact]
    public async Task UnchangedLegacySnapshotIsReanalyzedOnceThenQuickCacheResumes()
    {
        Assert.Equal(1, (await Scan("full")).DatabasesUpdated);
        var database = Assert.Single(store.All<DatabaseInfo>("Databases", "ws"));
        var current = store.Get<SnapshotRecord>("SchemaSnapshots", database.CurrentSnapshotId!)!;
        // Historical payload really lacks the new field, rather than opting into a version explicitly.
        var payload = JsonSerializer
            .SerializeToNode(
                current with
                {
                    Id = "legacy",
                    Graph = current.Graph with { Objects = null, Indexes = null, ObjectDependencies = null },
                },
                CatalogStore.Json
            )!
            .AsObject();
        payload.Remove("analysisVersion");
        var legacy = payload.Deserialize<SnapshotRecord>(CatalogStore.Json)!;
        Assert.Equal(1, legacy.AnalysisVersion);
        store.Batch([
            new("SchemaSnapshots", legacy.Id, "ws", legacy),
            new("Databases", database.Id, "ws", database with { CurrentSnapshotId = legacy.Id }),
        ]);

        var upgraded = await Scan("quick");
        Assert.Equal(0, upgraded.ChangedSources);
        Assert.True(upgraded.UnchangedSources > 0);
        Assert.Equal(1, upgraded.DatabasesUpdated);
        var upgradedId = Assert.Single(store.All<DatabaseInfo>("Databases", "ws")).CurrentSnapshotId!;
        Assert.NotEqual(legacy.Id, upgradedId);
        var snapshot = store.Get<SnapshotRecord>("SchemaSnapshots", upgradedId)!;
        Assert.Equal(WorkspaceScanner.CurrentAnalysisVersion, snapshot.AnalysisVersion);
        Assert.Equal("CurrentItems", Assert.Single(snapshot.Graph.Objects!).Name);
        Assert.Contains(snapshot.Graph.Indexes!, index => index.Name == "IX_Items_Code");
        Assert.Single(snapshot.Graph.ObjectDependencies!);
        Assert.Equal(1, store.Get<SnapshotRecord>("SchemaSnapshots", legacy.Id)!.AnalysisVersion);

        Assert.Equal(0, (await Scan("quick")).DatabasesUpdated);
        Assert.Equal(upgradedId, Assert.Single(store.All<DatabaseInfo>("Databases", "ws")).CurrentSnapshotId);
    }

    private Task<ScanOutcome> Scan(string mode) => scanner.Scan(workspace, mode, null, false, (_, _) => { }, default);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}
