using Microsoft.Data.Sqlite;
using Schemora.Host;
using Schemora.RecipeEngine;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class ProductExperienceTests
{
    [Fact]
    public void HtmlReportEncodesUserTextAndHasNoExternalAssets()
    {
        var directory = Path.Combine(Path.GetTempPath(), "schemora-html-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CatalogStore(Path.Combine(directory, "catalog.sqlite"));
            store.Put("Workspaces", "ws", "ws", new Workspace("ws", "<script>alert(1)</script>", [], [], DateTimeOffset.UtcNow));
            var graph = new SchemaGraph(
                "Demo",
                "source.sql",
                DateTimeOffset.UtcNow,
                [new("t", "dbo", "<img src=x>", [new("Id", "int", false)], ["Id"])],
                [],
                []
            );
            store.Put("SchemaSnapshots", "s", "ws", new SnapshotRecord("s", "ws", "db", DateTimeOffset.UtcNow, "static", "h", graph));
            store.Put("Databases", "db", "ws", new DatabaseInfo("db", "ws", "Demo", "source.sql", "sql", "ready", "s", null));
            var html = SchemaDocumentation.Export(store, "ws");
            Assert.DoesNotContain("<script>", html);
            Assert.Contains("&lt;script&gt;", html);
            Assert.Contains("Primary key", html);
            Assert.DoesNotContain("https://", html);
            Assert.Contains("&lt;img", html);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void DapperExampleUsesOneTypedJsonParameterAndEscapesCSharpContent()
    {
        var recipe = new RecipeDefinition(
            "r",
            "test",
            "",
            [],
            "sqlserver",
            ["db"],
            1,
            "SELECT 1 WHERE 1 IN ({{ids:Ids}});",
            [new("Ids", "int")]
        );
        var result = new SqlRecipeRenderer().Render(
            recipe,
            new Dictionary<string, string> { { "Ids", "1,2,1" } },
            "parameterized",
            "openjson"
        );
        Assert.Single(result.Parameters);
        Assert.Contains("DynamicParameters", result.DapperExample);
        Assert.Contains("DbType.String, size: -1", result.DapperExample);
        Assert.Contains("QueryAsync", result.DapperExample);
        Assert.Contains("SqlDbType.NVarChar", result.ParameterizedExample);
        Assert.Null(new SqlRecipeRenderer().Render(recipe, new Dictionary<string, string> { { "Ids", "1" } }).DapperExample);
    }
}
