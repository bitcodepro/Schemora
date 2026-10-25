using Microsoft.Data.Sqlite;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Schemora.Workspaces.Discovery;
using Xunit;

namespace Schemora.Workspaces.Tests;

public sealed class CatalogStoreTests : IDisposable
{
    private readonly string directory = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "Schemora-catalog-" + Guid.NewGuid().ToString("N")
    );

    private CatalogStore Create() => new(System.IO.Path.Combine(directory, "catalog.sqlite"));

    [Fact]
    public void PersistsWorkspacesSnapshotsAndScopedObjectsAcrossReopen()
    {
        var store = Create();
        var workspace = new Workspace("w", "Test", [directory], [], DateTimeOffset.UtcNow);
        store.Put("Workspaces", "w", "w", workspace);
        var graph = Graph("dbo", "Orders");
        var snapshot = new SnapshotRecord("s1", "w", "db", DateTimeOffset.UtcNow, "full", "fp", Identity.Scope(graph, "db"));
        store.Publish(
            "w",
            [],
            [new("db", "w", "Commerce", "file", "sqlProject", "ready", "s1", snapshot.CreatedAt)],
            [snapshot],
            new("scan", "w", "full", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "completed", 1, 0, 1, [])
        );
        var reopened = Create();
        Assert.Equal("Test", reopened.Get<Workspace>("Workspaces", "w")!.Name);
        Assert.Single(reopened.CurrentGraph("db")!.Tables);
        Assert.Single(reopened.All<object>("Columns", "w"));
        Assert.Single(reopened.Snapshots("db"));
        Assert.Empty(reopened.Snapshots("unrelated"));
    }

    [Fact]
    public void FailedBatchRollsBackEarlierWritesAndCatalogReplacement()
    {
        var store = Create();
        store.Put("Workspaces", "w", "w", new { name = "original" });
        Assert.Throws<ArgumentException>(() =>
            store.Batch([new("Workspaces", "new", "w", new { name = "new" }), new("invalid", "x", "w", new { })], [("Workspaces", "w")])
        );
        Assert.NotNull(store.Get<object>("Workspaces", "w"));
        Assert.Null(store.Get<object>("Workspaces", "new"));
    }

    [Fact]
    public void RejectsUnknownTableAndFutureCatalogVersion()
    {
        var store = Create();
        Assert.Throws<ArgumentException>(() => store.All<object>("Workspaces; DROP TABLE Workspaces"));
        using (var connection = new SqliteConnection("Data Source=" + store.Path))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=999";
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => Create());
    }

    [Fact]
    public void NamesContainingSeparatorsAndDuplicateDatabaseNamesKeepDistinctIds()
    {
        var first = Identity.Scope(Graph("a:b", "c"), "db1");
        var second = Identity.Scope(Graph("a", "b:c"), "db1");
        var third = Identity.Scope(Graph("a:b", "c"), "db2");
        Assert.NotEqual(first.Tables[0].Id, second.Tables[0].Id);
        Assert.NotEqual(first.Tables[0].Id, third.Tables[0].Id);
    }

    [Fact]
    public void RemovingDatabaseFromCurrentCatalogPreservesSnapshotHistory()
    {
        var store = Create();
        var snapshot = new SnapshotRecord("s", "w", "db", DateTimeOffset.UtcNow, "full", "f", Graph("dbo", "A"));
        var run = new ScanRun("r", "w", "full", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "completed", 0, 0, 1, []);
        store.Publish("w", [], [new("db", "w", "A", "path", "dacpac", "ready", "s", snapshot.CreatedAt)], [snapshot], run);
        store.Publish("w", [], [], [], run with { Id = "r2" });
        Assert.Null(store.CurrentGraph("db"));
        Assert.NotNull(store.Get<SnapshotRecord>("SchemaSnapshots", "s"));
    }

    [Fact]
    public void PublishedSnapshotCannotBeRewritten()
    {
        var store = Create();
        var first = new SnapshotRecord("immutable", "w", "db", DateTimeOffset.UtcNow, "full", "one", Graph("dbo", "A"));
        store.Put("SchemaSnapshots", first.Id, "w", first);
        Assert.Throws<SqliteException>(() => store.Put("SchemaSnapshots", first.Id, "w", first with { Fingerprint = "two" }));
        Assert.Equal("one", store.Get<SnapshotRecord>("SchemaSnapshots", first.Id)!.Fingerprint);
    }

    [Fact]
    public void StaleWorkspaceJobCannotPublishAfterRootsChange()
    {
        var store = Create();
        var captured = new Workspace("w", "test", [directory], [], DateTimeOffset.UtcNow);
        store.Put("Workspaces", "w", "w", captured with { Roots = [directory + "-new"] });
        var run = new ScanRun("stale", "w", "full", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "completed", 1, 0, 0, []);
        Assert.Throws<InvalidOperationException>(() => store.Publish("w", [], [], [], run, captured));
        Assert.Empty(store.All<ScanRun>("ScanRuns", "w"));
    }

    private static SchemaGraph Graph(string schema, string name) =>
        new("Db", "source", DateTimeOffset.UtcNow, [new("t", schema, name, [new("Id", "int", false)], ["Id"])], [], []);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        var target = System.IO.Path.GetFullPath(directory);
        if (
            target.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            && System.IO.Directory.Exists(target)
        )
            System.IO.Directory.Delete(target, true);
    }
}
