using Schemora.Relationships;
using Schemora.SqlServer;
using Xunit;

namespace Schemora.RecipeEngine.Tests;

public sealed class RecipeColumnRewriterTests
{
    private static RelationshipTable Table(
        string database = "db",
        string databaseName = "Commerce",
        string name = "Items",
        string[]? columns = null
    ) =>
        new(
            database,
            databaseName,
            new(
                database + ".dbo." + name,
                "dbo",
                name,
                (columns ?? ["Id", "New"]).Select(column => new SchemaColumn(column, "int", false)).ToArray(),
                ["Id"]
            )
        );

    private static RelationshipGraph Graph(params RelationshipTable[] tables) => new(tables, [], []);

    private static RecipeDefinition Recipe(string sql, string[]? databases = null) =>
        new(
            "id",
            "Repair",
            "",
            [],
            "sqlserver",
            databases ?? ["db"],
            1,
            sql,
            sql.Contains("{{", StringComparison.Ordinal) ? [new("Ids", "int")] : []
        );

    private static RecipeColumnRewriteResult Rewrite(
        string sql,
        RelationshipGraph? graph = null,
        string old = "Old",
        string replacement = "New",
        string[]? databases = null
    ) => new RecipeColumnRewriter().Preview(Recipe(sql, databases), graph ?? Graph(Table()), "db", "db.dbo.Items", old, replacement);

    [Fact]
    public void PatchesOnlyBoundIdentifierSpansPreservingAliasesCommentsAndLiterals()
    {
        const string sql =
            "-- i.Old remains a comment\nSELECT i.[Old] AS [Old], N'Old', i.Id FROM dbo.Items AS i WHERE i.Old IN ({{ids:Ids}}) ORDER BY i.Old;";
        var result = Rewrite(sql);
        Assert.Equal(3, result.ReplacementCount);
        Assert.Equal(
            "-- i.Old remains a comment\nSELECT i.[New] AS [Old], N'Old', i.Id FROM dbo.Items AS i WHERE i.[New] IN ({{ids:Ids}}) ORDER BY i.[New];",
            result.SqlTemplate
        );
        Assert.Equal("REWRITE_PREVIEW_ONLY", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void UsesExactDatabaseTableScopeAndPreservesOtherTablesOldColumn()
    {
        const string sql = "SELECT i.Old, o.Old FROM dbo.Items AS i INNER JOIN dbo.Other AS o ON i.Id=o.Id;";
        var result = Rewrite(sql, Graph(Table(), Table(name: "Other", columns: ["Id", "Old"])));
        Assert.Equal(1, result.ReplacementCount);
        Assert.Contains("i.[New], o.Old", result.SqlTemplate);
        var twoDatabases = Graph(Table(), Table("db2", "Billing"));
        AssertBlocked(Rewrite("SELECT i.Old FROM dbo.Items i;", twoDatabases, databases: ["db", "db2"]), "REWRITE_TABLE_AMBIGUOUS");
        var qualified = Rewrite("SELECT i.Old FROM Commerce.dbo.Items i;", twoDatabases, databases: ["db", "db2"]);
        Assert.Equal(1, qualified.ReplacementCount);
    }

    [Fact]
    public void EscapesReplacementIdentifiersAndPreservesDoubleQuoteStyle()
    {
        var graph = Graph(Table(columns: ["Id", "New] Name", "A\"B"]));
        Assert.Contains("i.[New]] Name]", Rewrite("SELECT i.Old FROM dbo.Items i;", graph, replacement: "New] Name").SqlTemplate);
        Assert.Contains("i.\"A\"\"B\"", Rewrite("SELECT i.\"Old\" FROM dbo.Items i;", graph, replacement: "A\"B").SqlTemplate);
    }

    [Theory]
    [InlineData("WITH c AS (SELECT Old FROM c) SELECT Old FROM c;", "REWRITE_BINDING_UNRESOLVED")]
    [InlineData("SELECT i.Old FROM dbo.Items i CROSS APPLY OPENJSON(N'[]') x;", "REWRITE_QUERY_UNSUPPORTED")]
    [InlineData("SELECT i.Old INTO #copy FROM dbo.Items i;", "REWRITE_QUERY_UNSUPPORTED")]
    [InlineData("UPDATE dbo.Items SET Old=1;", "REWRITE_QUERY_UNSUPPORTED")]
    [InlineData("SELECT i.Old FROM Items i;", "REWRITE_TABLE_QUALIFICATION_REQUIRED")]
    [InlineData("SELECT i.Old FROM dbo.Items i, dbo.Other i;", "REWRITE_ALIAS_AMBIGUOUS")]
    public void UnsupportedOrAmbiguousContextsPreserveOriginalSql(string sql, string code)
    {
        var result = Rewrite(sql, Graph(Table(), Table(name: "Other")));
        AssertBlocked(result, code);
        Assert.Equal(sql, result.SqlTemplate);
    }

    [Fact]
    public void UnqualifiedColumnsRequireOneCatalogSourceAndNoSelectAliasConflict()
    {
        Assert.Equal(1, Rewrite("SELECT Old FROM dbo.Items;").ReplacementCount);
        AssertBlocked(
            Rewrite("SELECT Old FROM dbo.Items i, dbo.Other o;", Graph(Table(), Table(name: "Other", columns: ["Old"]))),
            "REWRITE_COLUMN_AMBIGUOUS"
        );
        AssertBlocked(Rewrite("SELECT i.Id AS Old FROM dbo.Items i ORDER BY Old;"), "REWRITE_COLUMN_AMBIGUOUS");
        AssertBlocked(Rewrite("SELECT -i.New AS 'Old' FROM dbo.Items i ORDER BY Old;"), "REWRITE_COLUMN_AMBIGUOUS");
    }

    [Fact]
    public void TableInputAliasDoesNotInterfereAndMissingTargetsAreRejected()
    {
        var result = Rewrite("SELECT i.Old FROM dbo.Items i INNER JOIN {{table:Ids}} x ON i.Old=x.Value;");
        Assert.Equal(2, result.ReplacementCount);
        Assert.Contains("{{table:Ids}} x", result.SqlTemplate);
        AssertBlocked(Rewrite("SELECT i.Old FROM dbo.Items i;", replacement: "Missing"), "REWRITE_COLUMN_UNBOUND");
        AssertBlocked(Rewrite("SELECT i.Old FROM dbo.Items i;", databases: ["other"]), "REWRITE_DATABASE_SCOPE");
    }

    private static void AssertBlocked(RecipeColumnRewriteResult result, string code)
    {
        Assert.Equal(0, result.ReplacementCount);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == "error" && diagnostic.Code == code);
    }
}
