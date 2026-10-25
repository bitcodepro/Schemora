using Microsoft.Data.Sqlite;
using Schemora.Host;
using Schemora.RecipeEngine;
using Schemora.Relationships;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class ViewRecipeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SchemoraViewRecipes-" + Guid.NewGuid().ToString("N"));
    private readonly CatalogStore store;
    private readonly RelationshipService relationships;
    private readonly RecipeService recipes;
    private readonly SchemaGraph graph = new(
        "Commerce",
        "schema.sql",
        DateTimeOffset.UtcNow,
        [new("t", "dbo", "Items", [new("Id", "int", false), new("Label", "nvarchar(80)", true)], ["Id"])],
        [],
        [],
        [
            new(
                "v",
                "dbo",
                "CurrentItems",
                "view",
                [new("Id", "int", false), new("Label", "nvarchar(80)", true)],
                Definition: "SELECT Id, Label FROM dbo.Items"
            ),
        ],
        [],
        [new("dependency", "v", "t", "dbo.Items", "dacfx-reference", "schema.sql", 1, 1)]
    );

    public ViewRecipeTests()
    {
        store = new(Path.Combine(directory, "catalog.sqlite"));
        store.Put("Workspaces", "ws", null, new Workspace("ws", "Views", [], [], DateTimeOffset.UtcNow));
        Publish(graph);
        relationships = new(store, new RelationshipAnalyzer());
        recipes = new(store, relationships, new RecipeValidator(), new SqlRecipeRenderer());
    }

    [Fact]
    public async Task ViewRecipeHasObjectLineageAndTransitiveImpactWithoutAddingGraphTables()
    {
        var context = await relationships.Get("ws", default);
        Assert.Single(context.Tables);
        Assert.Single(context.ReadableObjects!);
        var saved = await Save();
        Assert.Equal("Valid", saved.Validation.Status);
        Assert.Contains(saved.Validation.Dependencies, d => d.TableId == "v" && d.ColumnName == "Label");
        var rendered = await recipes.Render(
            saved.Definition.Id,
            new Dictionary<string, string> { ["Ids"] = "1,2" },
            "standalone",
            "auto",
            false,
            default
        );
        Assert.Contains("CurrentItems", rendered.Sql);
        var detail = System.Text.Json.JsonSerializer.SerializeToElement(
            new SchemaObjectService(store).Detail("ws", "t"),
            CatalogStore.Json
        );
        Assert.Contains(
            saved.Definition.Id,
            detail.GetProperty("impact").GetProperty("recipeIds").EnumerateArray().Select(v => v.GetString())
        );
    }

    [Fact]
    public async Task ViewColumnRemovalBreaksRecipeAndBodyChangeRequiresReview()
    {
        var saved = await Save();
        Publish(graph with { Objects = [graph.Objects![0] with { Definition = "SELECT Id, Label FROM dbo.Items WHERE Id > 0" }] });
        var validation = await recipes.Revalidate(saved.Definition.Id, default);
        Assert.Equal("NeedsReview", validation.Status);
        Assert.Contains(validation.Diagnostics, d => d.Code == "DEPENDENCY_OBJECT_CHANGED");
        Publish(graph with { Objects = [graph.Objects![0] with { Columns = [new("Id", "int", false)] }] });
        Assert.Equal("Broken", (await recipes.Revalidate(saved.Definition.Id, default)).Status);
    }

    [Fact]
    public async Task PartialViewShapeNeverSilentlyBecomesAValidRecipe()
    {
        Publish(graph with { Objects = [graph.Objects![0] with { IsPartial = true }] });
        var saved = await Save();
        Assert.Equal("NeedsReview", saved.Validation.Status);
        Assert.Contains(saved.Validation.Diagnostics, d => d.Code == "RECIPE_OBJECT_PARTIAL");
    }

    private Task<RecipeEntry> Save() =>
        recipes.Save(
            "ws",
            new(
                "",
                "View consumer",
                "",
                [],
                "sqlserver",
                ["db"],
                0,
                "SELECT v.Label FROM dbo.CurrentItems v WHERE v.Id IN ({{ids:Ids}})",
                [new("Ids", "int")]
            ),
            null,
            "Synthetic",
            default
        );

    private void Publish(SchemaGraph value)
    {
        var id = Guid.NewGuid().ToString("N");
        store.Batch([
            new("SchemaSnapshots", id, "ws", new SnapshotRecord(id, "ws", "db", DateTimeOffset.UtcNow, "full", id, value)),
            new("Databases", "db", "ws", new DatabaseInfo("db", "ws", "Commerce", "schema.sql", "sql", "ready", id, DateTimeOffset.UtcNow)),
        ]);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}
