using Microsoft.Data.Sqlite;
using Schemora.Host;
using Schemora.RecipeEngine;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class DependencyExplorerTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "schemora-path-" + Guid.NewGuid().ToString("N"));
    readonly CatalogStore store;

    public DependencyExplorerTests()
    {
        store = new(Path.Combine(directory, "catalog.sqlite"));
        store.Put("Workspaces", "ws", "ws", new Workspace("ws", "demo", [], [], DateTimeOffset.UtcNow));
        var graph = new SchemaGraph(
            "demo",
            "source.sql",
            DateTimeOffset.UtcNow,
            [new("t", "dbo", "Table", [new("Id", "int", false)], ["Id"])],
            [],
            [],
            [new("v", "dbo", "View", "view", []), new("p", "dbo", "Procedure", "procedure", [])],
            [],
            [new("vt", "v", "t", "dbo.Table", "select", "source.sql", 1, 1), new("pv", "p", "v", "dbo.View", "execute", "source.sql", 2, 1)]
        );
        store.Put("SchemaSnapshots", "s", "ws", new SnapshotRecord("s", "ws", "db", DateTimeOffset.UtcNow, "static", "h", graph));
        store.Put("Databases", "db", "ws", new DatabaseInfo("db", "ws", "demo", "source.sql", "sql", "ready", "s", null));
    }

    void Recipe(int index)
    {
        var id = "r" + index;
        var dependencies = new RecipeDependency[] { new("db", "p") };
        var definition = new RecipeDefinition(id, "Recipe " + index.ToString("D4"), "", [], "sqlserver", ["db"], 1, "SELECT 1", []);
        store.Put(
            "Recipes",
            id,
            "ws",
            new RecipeEntry(
                "ws",
                definition,
                new("Valid", dependencies, []),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                new Dictionary<string, string>()
            )
        );
    }

    [Fact]
    public void ExplainsTableViewProcedureRecipeAndPaginatesBeyondFirstHundred()
    {
        for (var i = 0; i < 130; i++)
            Recipe(i);
        var first = DependencyExplorer.Read(store, "ws", "t", null, 0, 25);
        var last = DependencyExplorer.Read(store, "ws", "t", null, 125, 25);
        Assert.Equal(130, first.Total);
        Assert.Equal(5, last.Nodes.Count(n => n.Kind == "recipe"));
        Assert.Contains(first.Edges, e => e.Source == "t" && e.Target == "v" && e.Explanation.Contains("select"));
        Assert.Contains(first.Edges, e => e.Source == "v" && e.Target == "p");
        Assert.All(
            first.Edges,
            e =>
            {
                Assert.Contains(first.Nodes, n => n.Id == e.Source);
                Assert.Contains(first.Nodes, n => n.Id == e.Target);
            }
        );
        Assert.False(first.Partial);
        var found = DependencyExplorer.Read(store, "ws", "t", "0129", 0, 25);
        Assert.Equal(1, found.Total);
        Assert.Contains(found.Nodes, n => n.Id == "recipe:r129");
    }

    [Fact]
    public void ThousandRecipeMeasurementRemainsBoundedAndDeterministic()
    {
        for (var i = 0; i < 1000; i++)
            Recipe(i);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var page = DependencyExplorer.Read(store, "ws", "t", null, 975, 25);
        timer.Stop();
        Assert.Equal(1000, page.Total);
        Assert.Equal(28, page.Nodes.Count);
        Assert.Equal(27, page.Edges.Count);
        Assert.Contains(page.Nodes, n => n.Id == "recipe:r999");
        // Generous budget detects accidental unbounded traversal without relying on desktop benchmark timings.
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30));
        Console.WriteLine($"1000 recipes: {timer.ElapsedMilliseconds} ms, {page.Nodes.Count} nodes, {page.Edges.Count} edges");
    }

    [Fact]
    public void UnchangedSnapshotsReuseTheDependencyProjection()
    {
        Recipe(1);
        var cold = DependencyExplorer.Read(store, "ws", "t", null, 0, 25);
        var warm = DependencyExplorer.Read(store, "ws", "t", null, 0, 25);
        Assert.Equal(1, cold.RefreshedDatabases);
        Assert.Equal(0, warm.RefreshedDatabases);
        Assert.Equal(cold.Total, warm.Total);
        Assert.Equal(cold.Edges, warm.Edges);
        Assert.Equal(0, warm.UpdatedNodes);
        Assert.Equal(0, warm.UpdatedEdges);
        var old = store.CurrentGraph("db")!;
        var changed = old with { Tables = old.Tables.Select(t => t with { Name = "Renamed" }).ToArray() };
        store.Put(
            "SchemaSnapshots",
            "changed",
            "ws",
            new SnapshotRecord("changed", "ws", "db", DateTimeOffset.UtcNow, "static", "changed", changed)
        );
        store.Put("Databases", "db", "ws", new DatabaseInfo("db", "ws", "demo", "source.sql", "sql", "ready", "changed", null));
        var delta = DependencyExplorer.Read(store, "ws", "t", null, 0, 25);
        Assert.Equal(1, delta.UpdatedNodes);
        Assert.Equal(0, delta.UpdatedEdges);
        Assert.Contains(delta.Nodes, n => n.Name == "dbo.Renamed");
    }

    [Theory]
    [InlineData("en", "Primary key")]
    [InlineData("ru", "Первичный ключ")]
    [InlineData("uk", "Первинний ключ")]
    public void ReportLanguageAndSectionSelectionAreRespected(string language, string heading)
    {
        Recipe(1);
        var all = SchemaDocumentation.Export(store, "ws", null, new(language, Recipes: true));
        Assert.Contains("lang=\"" + language + "\"", all);
        Assert.Contains(heading, all);
        Assert.Contains("Recipe 0001", all);
        var selected = SchemaDocumentation.Export(
            store,
            "ws",
            null,
            new(language, Objects: false, Relations: false, Evidence: false, Recipes: false)
        );
        Assert.DoesNotContain("<table>", selected);
        Assert.DoesNotContain("Recipe 0001", selected);
        Assert.DoesNotContain("dbo.View", selected);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }
}
