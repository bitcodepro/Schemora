using Microsoft.Data.Sqlite;
using Schemora.Host;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class QualityRulesTests
{
    [Fact]
    public void LeadingKeyCoverageAndFilteredIndexesAreDistinguishedAndRulesAreVersioned()
    {
        var table = new SchemaTable("t", "dbo", "Child", [new("Id", "int", false), new("ParentId", "int", true)], ["Id"]);
        var index = new SchemaIndex("i", "t", "IX_Child", "nonclustered", false, false, [new("ParentId", false)], []);
        var graph = new SchemaGraph(
            "demo",
            "source.sql",
            DateTimeOffset.UtcNow,
            [table],
            [new("fk", "FK_Child", "t", "parent", ["ParentId"], ["Id"], "declared-fk", 1)],
            [],
            Indexes: [index]
        );
        var findings = new List<string>();
        void Collect(string rule, string severity, string objectId, string title, string detail) => findings.Add(rule);
        QualityRules.Evaluate(graph, Collect);
        Assert.Contains("NULLABLE_FOREIGN_KEY", findings);
        Assert.DoesNotContain("FK_WITHOUT_SUPPORTING_INDEX", findings);
        findings.Clear();
        QualityRules.Evaluate(graph with { Indexes = [index with { Filter = "ParentId > 0" }] }, Collect);
        Assert.Contains("FK_WITHOUT_SUPPORTING_INDEX", findings);
        findings.Clear();
        QualityRules.Evaluate(graph with { Indexes = [index, index with { Id = "i2", Name = "IX_Duplicate" }] }, Collect);
        Assert.Contains("DUPLICATE_INDEX", findings);
        var directory = Path.Combine(Path.GetTempPath(), "quality-rules-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CatalogStore(Path.Combine(directory, "catalog.sqlite"));
            store.Put("Workspaces", "ws", "ws", new Workspace("ws", "demo", [], [], DateTimeOffset.UtcNow));
            store.Put("SchemaSnapshots", "s", "ws", new SnapshotRecord("s", "ws", "db", DateTimeOffset.UtcNow, "static", "h", graph));
            store.Put("Databases", "db", "ws", new DatabaseInfo("db", "ws", "demo", "source.sql", "sql", "ready", "s", null));
            var initial = SchemaQuality.Evaluate(store, "ws", new([], [], []));
            Assert.Contains(initial, f => f.Rule == "NULLABLE_FOREIGN_KEY");
            var saved = QualityRules.Save(store, "ws", new(new Dictionary<string, string> { ["NULLABLE_FOREIGN_KEY"] = "off" }));
            Assert.Equal(1, saved.Version);
            Assert.DoesNotContain(SchemaQuality.Evaluate(store, "ws", new([], [], [])), f => f.Rule == "NULLABLE_FOREIGN_KEY");
            Assert.Throws<AppException>(() => QualityRules.Save(store, "ws", new(new Dictionary<string, string>())));
            Assert.Throws<AppException>(() =>
                QualityRules.Save(store, "ws", new(new Dictionary<string, string> { ["Unknown"] = "off" }, 1))
            );
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
