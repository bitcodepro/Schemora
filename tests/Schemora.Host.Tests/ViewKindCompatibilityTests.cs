using Microsoft.Data.Sqlite;
using Schemora.Host;
using Schemora.RecipeEngine;
using Schemora.Relationships;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class ViewKindCompatibilityTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SchemoraViewKinds-" + Guid.NewGuid().ToString("N"));
    private readonly CatalogStore store;
    private readonly RecipeService recipes;

    public ViewKindCompatibilityTests()
    {
        store = new(Path.Combine(directory, "catalog.sqlite"));
        store.Put("Workspaces", "ws", null, new Workspace("ws", "Object kind changes", [], [], DateTimeOffset.UtcNow));
        recipes = new(store, new RelationshipService(store, new RelationshipAnalyzer()), new RecipeValidator(), new SqlRecipeRenderer());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SameShapeViewTableReplacementStillRequiresReview(bool startsAsView)
    {
        Publish(startsAsView);
        var saved = await recipes.Save(
            "ws",
            new(
                "",
                "Consumer",
                "",
                [],
                "sqlserver",
                ["db"],
                0,
                "SELECT i.Id FROM dbo.Items i WHERE i.Id IN ({{ids:Ids}})",
                [new("Ids", "int")]
            ),
            null,
            "Synthetic",
            default
        );
        Assert.Equal("Valid", saved.Validation.Status);
        Publish(!startsAsView);
        var validation = await recipes.Revalidate(saved.Definition.Id, default);
        Assert.Equal("NeedsReview", validation.Status);
        Assert.Contains(validation.Diagnostics, d => d.Code == "DEPENDENCY_OBJECT_KIND_CHANGED");
        await Assert.ThrowsAsync<AppException>(() =>
            recipes.Render(saved.Definition.Id, new Dictionary<string, string> { ["Ids"] = "1" }, "standalone", "auto", false, default)
        );
    }

    private void Publish(bool isView)
    {
        SchemaColumn[] columns = [new("Id", "int", false)];
        SchemaGraph graph = new(
            "Commerce",
            "fixture.sql",
            DateTimeOffset.UtcNow,
            isView ? [] : [new("object", "dbo", "Items", columns, ["Id"])],
            [],
            [],
            isView ? [new("object", "dbo", "Items", "view", columns, Definition: "SELECT Id FROM dbo.SourceItems")] : []
        );
        var id = Guid.NewGuid().ToString("N");
        store.Batch([
            new("SchemaSnapshots", id, "ws", new SnapshotRecord(id, "ws", "db", DateTimeOffset.UtcNow, "full", id, graph)),
            new(
                "Databases",
                "db",
                "ws",
                new DatabaseInfo("db", "ws", "Commerce", "fixture.sql", "sql", "ready", id, DateTimeOffset.UtcNow)
            ),
        ]);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}
