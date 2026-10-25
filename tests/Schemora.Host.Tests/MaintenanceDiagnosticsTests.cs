using System.Text.Json;
using Microsoft.Data.Sqlite;
using Schemora.Host;
using Schemora.RecipeEngine;
using Schemora.Relationships;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class MaintenanceDiagnosticsTests
{
    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "schemora-maintenance-" + Guid.NewGuid().ToString("N"));
        public CatalogStore Store { get; }

        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Store = new(Path.Combine(Directory, "catalog.sqlite"));
            Store.Put("Workspaces", "ws", null, new Workspace("ws", "Test", [Directory], [], DateTimeOffset.UtcNow));
        }

        public void Snapshot(string id) =>
            Store.Put(
                "SchemaSnapshots",
                id,
                "ws",
                new SnapshotRecord(
                    id,
                    "ws",
                    "db",
                    DateTimeOffset.UtcNow,
                    "static",
                    id,
                    new SchemaGraph(
                        "Db",
                        "schema.sql",
                        DateTimeOffset.UtcNow,
                        [new("a", "dbo", "A", [new("Id", "int", false)], ["Id"]), new("b", "dbo", "B", [new("Id", "int", false)], ["Id"])],
                        [],
                        []
                    )
                )
            );

        public void Age()
        {
            using var c = new SqliteConnection("Data Source=" + Store.Path);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                "UPDATE SchemaSnapshots SET UpdatedAt='2000-01-01T00:00:00Z'; UPDATE Jobs SET UpdatedAt='2000-01-01T00:00:00Z'";
            cmd.ExecuteNonQuery();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(Directory, true);
        }
    }

    [Fact]
    public async Task IndexedBrowsingPagesAndInvalidatesAfterDatabaseAliasChange()
    {
        using var f = new Fixture();
        f.Snapshot("s1");
        f.Store.Put("Databases", "db", "ws", new DatabaseInfo("db", "ws", "Db", "schema.sql", "sql", "ready", "s1", null));
        var relationships = new RelationshipService(f.Store, new RelationshipAnalyzer());
        var index = new GraphReadIndex(f.Store, relationships);
        await index.Ensure("ws", default);
        var graph = index.Graph("ws", null, null, null, null, 1, 0, null, 1);
        Assert.Single(graph.Tables);
        Assert.Equal(2, graph.TotalTables);
        Assert.True(graph.Truncated);
        var filter = index.Graph("ws", null, null, "dbo.B", null, 1, 0, null, 10);
        Assert.Equal("B", Assert.Single(filter.Tables).Table.Name);
        var before = JsonSerializer.Serialize(index.Tables("ws", null, null, null, 1, 1), CatalogStore.Json);
        Assert.Contains("\"offset\":1", before);
        var db = f.Store.Get<DatabaseInfo>("Databases", "db")!;
        f.Store.Put("Databases", "db", "ws", db with { Name = "Renamed" });
        await index.Ensure("ws", default);
        Assert.All(index.Graph("ws", null, null, null, null, 1, 0, null, 10).Tables, t => Assert.Equal("Renamed", t.DatabaseName));
        var cache = typeof(RelationshipService)
            .GetField("cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(relationships)!;
        Assert.Equal(0, (int)cache.GetType().GetProperty("Count")!.GetValue(cache)!);
    }

    [Fact]
    public async Task IndexedSearchKeepsRussianAndUkrainianCaseInsensitiveMatching()
    {
        using var f = new Fixture();
        var graph = new SchemaGraph(
            "База",
            "schema.sql",
            DateTimeOffset.UtcNow,
            [new("a", "дані", "Рахунки", [new("Ідентифікатор", "int", false)], ["Ідентифікатор"])],
            [],
            []
        );
        f.Store.Put(
            "SchemaSnapshots",
            "unicode",
            "ws",
            new SnapshotRecord("unicode", "ws", "db", DateTimeOffset.UtcNow, "static", "unicode", graph)
        );
        f.Store.Put("Databases", "db", "ws", new DatabaseInfo("db", "ws", "База", "schema.sql", "sql", "ready", "unicode", null));
        var index = new GraphReadIndex(f.Store, new RelationshipService(f.Store, new RelationshipAnalyzer()));
        await index.Ensure("ws", default);
        Assert.Single(index.Graph("ws", null, "ДАНІ", "РАХУНКИ", null, 1, 0, null, 10).Tables);
        Assert.Single(index.Graph("ws", null, null, "ІДЕНТИФІКАТОР", null, 1, 0, null, 10).Tables);
        var objects = JsonSerializer.SerializeToElement(
            new SchemaObjectService(f.Store).List("ws", null, null, "РАХУНКИ", 0, 10),
            CatalogStore.Json
        );
        Assert.Equal(1, objects.GetProperty("total").GetInt32());
    }

    [Fact]
    public void CleanupPreservesCurrentCompatibilityAndRecentMinimumSnapshotsAndCreatesBackup()
    {
        using var f = new Fixture();
        foreach (var id in new[] { "old", "compatible", "recent1", "recent2", "current" })
            f.Snapshot(id);
        f.Store.Put("Databases", "db", "ws", new DatabaseInfo("db", "ws", "Db", "schema.sql", "sql", "ready", "current", null));
        var definition = new RecipeDefinition("r", "Recipe", "", [], "sqlserver", ["db"], 1, "SELECT 1", []);
        var entry = new RecipeEntry(
            "ws",
            definition,
            new("Valid", [], []),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string> { { "db", "compatible" } }
        );
        f.Store.Put("Recipes", "r", "ws", entry);
        f.Age();
        var maintenance = new StorageMaintenance(f.Store);
        maintenance.Update(new(true, 2, 1));
        var preview = maintenance.Preview();
        Assert.Contains("current", preview.ProtectedSnapshotIds);
        Assert.Contains("compatible", preview.ProtectedSnapshotIds);
        Assert.True(preview.Snapshots > 0);
        Assert.Throws<AppException>(() => maintenance.Apply(new("stale"), new JobQueue(f.Store, null!, null!)));
        maintenance.Apply(new(preview.Revision), new JobQueue(f.Store, null!, null!));
        Assert.NotNull(f.Store.Get<SnapshotRecord>("SchemaSnapshots", "current"));
        Assert.NotNull(f.Store.Get<SnapshotRecord>("SchemaSnapshots", "compatible"));
        Assert.Single(System.IO.Directory.GetFiles(Path.Combine(f.Directory, "backups"), "*.sqlite"));
    }

    [Fact]
    public void DiagnosticLogAndDefaultReportCannotContainExceptionSecretsOrRootPaths()
    {
        using var f = new Fixture();
        var journal = new DiagnosticJournal(f.Directory);
        var id = journal.Record(
            "ANALYSIS_FAILED",
            422,
            error: new InvalidOperationException("Token=secret; SELECT 'customer-id'; " + f.Directory)
        );
        var json = JsonSerializer.Serialize(journal.Report(f.Store, false), CatalogStore.Json);
        Assert.Contains(id, json);
        Assert.DoesNotContain("customer-id", json);
        Assert.DoesNotContain("Token=secret", json);
        Assert.DoesNotContain(f.Directory, json);
        var persisted = File.ReadAllText(Path.Combine(f.Directory, "diagnostics", "events.jsonl"));
        Assert.DoesNotContain("secret", persisted);
        Assert.Contains(f.Directory.Replace("\\", "\\\\"), JsonSerializer.Serialize(journal.Report(f.Store, true), CatalogStore.Json));
    }

    [Fact]
    public void SourceOpeningRequiresExistingLocalFileInsideWorkspace()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Directory, "file.sql");
        File.WriteAllText(path, "SELECT 1");
        Assert.Equal(path, SourceLocation.Validate(f.Store, path));
        Assert.Throws<AppException>(() => SourceLocation.Validate(f.Store, Path.Combine(Path.GetTempPath(), "outside.sql")));
        Assert.Throws<AppException>(() => SourceLocation.Validate(f.Store, Path.Combine(f.Directory, "file.exe")));
    }
}
