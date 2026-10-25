using Schemora.Relationships;
using Schemora.SqlServer;
using Xunit;

namespace Schemora.RecipeEngine.Tests;

public sealed class RecipeObjectRewriterTests
{
    private static RelationshipGraph Graph() =>
        new([new("db", "Commerce", new("new-id", "sales", "Renamed", [new("Id", "int", false)], ["Id"]))], [], []);

    private static RecipeDefinition Recipe(string sql) => new("r", "test", "", [], "sqlserver", ["db"], 1, sql, []);

    [Fact]
    public void ExplicitRenamePatchesOnlySourceIdentifiersAndKeepsAliasLiteralComment()
    {
        var sql = "SELECT o.Id, 'dbo.Orders' AS label FROM dbo.Orders AS o -- dbo.Orders";
        var result = new RecipeColumnRewriter().PreviewObjectRename(Recipe(sql), Graph(), "db", "new-id", "dbo", "Orders");
        Assert.Equal(1, result.ReplacementCount);
        Assert.Equal("SELECT o.Id, 'dbo.Orders' AS label FROM [sales].[Renamed] AS o -- dbo.Orders", result.SqlTemplate);
    }

    [Theory]
    [InlineData("SELECT Id FROM dbo.Orders")]
    [InlineData("WITH c AS (SELECT o.Id FROM dbo.Orders o) SELECT * FROM c")]
    [InlineData("SELECT o.Missing FROM dbo.Orders o")]
    [InlineData("SELECT o.Id FROM OtherDb.dbo.Orders o")]
    public void AmbiguousOrUnsupportedRenameRetainsOriginal(string sql)
    {
        var result = new RecipeColumnRewriter().PreviewObjectRename(Recipe(sql), Graph(), "db", "new-id", "dbo", "Orders");
        Assert.Equal(0, result.ReplacementCount);
        Assert.Equal(sql, result.SqlTemplate);
        Assert.Contains(result.Diagnostics, d => d.Severity == "error");
    }
}
