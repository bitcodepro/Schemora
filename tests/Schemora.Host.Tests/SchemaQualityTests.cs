using Microsoft.Data.Sqlite;
using Schemora.Host;
using Schemora.Relationships;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class SchemaQualityTests
{
    [Fact]
    public void DistinctUnresolvedTargetsHaveIndependentReviewIdentities()
    {
        var directory = Path.Combine(Path.GetTempPath(), "quality-targets-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CatalogStore(Path.Combine(directory, "catalog.sqlite"));
            store.Put("Workspaces", "ws", "ws", new Workspace("ws", "demo", [], [], DateTimeOffset.UtcNow));
            var graph = new SchemaGraph(
                "demo",
                "source.sql",
                DateTimeOffset.UtcNow,
                [],
                [],
                [],
                [new("p", "dbo", "Routine", "procedure", [])],
                [],
                [
                    new("a", "p", null, "external.First", "static-reference", "source.sql", 1, .4),
                    new("b", "p", null, "external.Second", "static-reference", "source.sql", 2, .4),
                ]
            );
            store.Put("SchemaSnapshots", "s", "ws", new SnapshotRecord("s", "ws", "db", DateTimeOffset.UtcNow, "static", "h", graph));
            store.Put("Databases", "db", "ws", new DatabaseInfo("db", "ws", "demo", "source.sql", "sql", "ready", "s", null));
            var findings = SchemaQuality.Evaluate(store, "ws", new([], [], []));
            Assert.Equal(2, findings.Count);
            Assert.Equal(2, findings.Select(f => f.Id).Distinct().Count());
            SchemaQuality.Suppress(
                store,
                "ws",
                findings,
                new(findings[0].Id, findings[0].Fingerprint, "The first external reference is intentionally reviewed.")
            );
            Assert.Single(SchemaQuality.Evaluate(store, "ws", new([], [], [])).Where(f => f.SuppressionReason is not null));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void MissingKeyIsAdvisoryAndSuppressionNeedsReasonAndExpiresWithSnapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "quality-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CatalogStore(Path.Combine(directory, "catalog.sqlite"));
            store.Put("Workspaces", "ws", "ws", new Workspace("ws", "demo", [], [], DateTimeOffset.UtcNow));
            var graph = new SchemaGraph(
                "demo",
                "source.sql",
                DateTimeOffset.UtcNow,
                [new("t", "dbo", "Staging", [new("Value", "int", true)], [])],
                [],
                []
            );
            store.Put("SchemaSnapshots", "s", "ws", new SnapshotRecord("s", "ws", "db", DateTimeOffset.UtcNow, "static", "h", graph));
            store.Put("Databases", "db", "ws", new DatabaseInfo("db", "ws", "demo", "source.sql", "sql", "ready", "s", null));
            var relationships = new RelationshipGraph([], [], []);
            var search = System.Text.Json.JsonSerializer.SerializeToElement(
                WorkspaceSearch.Read(store, "ws", "Staging", null, 0, 50),
                CatalogStore.Json
            );
            Assert.Equal(2, search.GetProperty("total").GetInt32());
            var findings = SchemaQuality.Evaluate(store, "ws", relationships);
            var finding = Assert.Single(findings);
            Assert.Equal("warning", finding.Severity);
            Assert.Throws<AppException>(() => SchemaQuality.Suppress(store, "ws", findings, new(finding.Id, finding.Fingerprint, "short")));
            SchemaQuality.Suppress(
                store,
                "ws",
                findings,
                new(finding.Id, finding.Fingerprint, "Intentional staging table without a primary key.")
            );
            Assert.NotNull(Assert.Single(SchemaQuality.Evaluate(store, "ws", relationships)).SuppressionReason);
            store.Put("SchemaSnapshots", "s2", "ws", new SnapshotRecord("s2", "ws", "db", DateTimeOffset.UtcNow, "static", "h2", graph));
            store.Put("Databases", "db", "ws", new DatabaseInfo("db", "ws", "demo", "source.sql", "sql", "ready", "s2", null));
            var changed = SchemaQuality.Evaluate(store, "ws", relationships);
            Assert.Null(Assert.Single(changed).SuppressionReason);
            Assert.Throws<AppException>(() =>
                SchemaQuality.Suppress(store, "ws", changed, new(finding.Id, finding.Fingerprint, "Outdated review must not apply."))
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
