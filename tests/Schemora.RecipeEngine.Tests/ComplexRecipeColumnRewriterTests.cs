using Microsoft.SqlServer.Dac.Model;
using Schemora.Relationships;
using Schemora.SqlServer;
using Xunit;

namespace Schemora.RecipeEngine.Tests;

public sealed class ComplexRecipeColumnRewriterTests
{
    private static readonly RelationshipGraph Graph = new(
        [
            new("db", "Commerce", new("items", "dbo", "Items", [new("Id", "int", false), new("New", "int", true)], ["Id"])),
            new(
                "db",
                "Commerce",
                new("other", "dbo", "Other", [new("Id", "int", false), new("Old", "int", true), new("New", "int", true)], ["Id"])
            ),
        ],
        [],
        []
    );

    private static RecipeDefinition Recipe(string sql) => new("r", "Complex", "", [], "sqlserver", ["db"], 1, sql, []);

    private static RecipeColumnRewriteResult Rewrite(string sql) =>
        new RecipeColumnRewriter().Preview(Recipe(sql), Graph, "db", "items", "Old", "New");

    [Theory]
    [InlineData("WITH c AS (SELECT Old FROM dbo.Items) SELECT Old FROM c;", 1)]
    [InlineData(
        "WITH a AS (SELECT i.Old FROM dbo.Items i), b(Selected) AS (SELECT a.Old FROM a) SELECT d.Selected FROM (SELECT b.Selected FROM b) d;",
        1
    )]
    [InlineData("SELECT d.Old FROM (SELECT i.Old FROM dbo.Items i) d WHERE d.Old>0;", 1)]
    [InlineData("SELECT i.Old FROM dbo.Items i WHERE EXISTS (SELECT 1 FROM dbo.Other o WHERE o.Id=i.Old);", 2)]
    [InlineData("SELECT (SELECT i.Old FROM dbo.Items i WHERE i.Id=o.Id) AS Selected FROM dbo.Other o;", 1)]
    [InlineData("SELECT i.Old FROM dbo.Items i UNION ALL SELECT i.Old FROM dbo.Items i;", 2)]
    [InlineData("WITH c AS (SELECT i.Old FROM dbo.Items i UNION ALL SELECT o.Old FROM dbo.Other o) SELECT c.Old FROM c;", 1)]
    [InlineData("SELECT i.Old FROM dbo.Items i UNION ALL SELECT o.Old FROM dbo.Other o ORDER BY Old;", 1)]
    public void RepairsExactPhysicalReferencesAcrossComplexScopes(string sql, int count)
    {
        var result = Rewrite(sql);
        Assert.Equal(count, result.ReplacementCount);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == "error");
        Assert.Contains("[New]", result.SqlTemplate);
        Assert.DoesNotContain(new RecipeValidator().Validate(Recipe(result.SqlTemplate), Graph).Diagnostics, d => d.Severity == "error");
        if (!sql.Contains("ORDER BY", StringComparison.Ordinal))
            AssertDacViewCompiles(result.SqlTemplate);
    }

    [Fact]
    public void PreservesCteDerivedAndUnionOutputNamesRatherThanRenamingLogicalAliases()
    {
        var result = Rewrite("WITH c AS (SELECT i.Old FROM dbo.Items i) SELECT d.Old FROM (SELECT c.Old FROM c) d;");
        Assert.Equal(1, result.ReplacementCount);
        Assert.Contains("i.[New] AS [Old]", result.SqlTemplate);
        Assert.Contains("SELECT d.Old", result.SqlTemplate);
        Assert.Contains("SELECT c.Old", result.SqlTemplate);
        AssertDacViewCompiles(result.SqlTemplate);
    }

    [Fact]
    public void CorrelatedAndShadowedAliasesDoNotPatchAnotherTablesSameNamedColumn()
    {
        var result = Rewrite("SELECT i.Old,(SELECT i.Old FROM dbo.Other i WHERE i.Id=1) AS OtherValue FROM dbo.Items i;");
        Assert.Equal(1, result.ReplacementCount);
        Assert.Contains("SELECT i.[New]", result.SqlTemplate);
        Assert.Contains("(SELECT i.Old FROM dbo.Other i", result.SqlTemplate);
        AssertDacViewCompiles(result.SqlTemplate, 2);
    }

    [Fact]
    public void QualifiesUnqualifiedReplacementToPreventCaptureByAnotherNewColumn()
    {
        var result = Rewrite("SELECT Old FROM dbo.Items i JOIN dbo.Other o ON i.Id=o.Id;", GraphWithoutOtherOld());
        Assert.Equal(1, result.ReplacementCount);
        Assert.Contains("[i].[New]", result.SqlTemplate);
        AssertDacViewCompiles(result.SqlTemplate);
    }

    [Fact]
    public void ComputedDerivedAliasesArePreservedAndNeverGuessedAsPhysicalOldColumns()
    {
        var result = Rewrite("SELECT d.Old FROM (SELECT i.Id+1 AS Old FROM dbo.Items i) d;");
        Assert.Equal(0, result.ReplacementCount);
        Assert.Equal("SELECT d.Old FROM (SELECT i.Id+1 AS Old FROM dbo.Items i) d;", result.SqlTemplate);
        result = Rewrite("SELECT d.Old FROM (SELECT i.Old+1 AS Old FROM dbo.Items i) d;");
        Assert.Equal(1, result.ReplacementCount);
        Assert.Contains("i.[New]+1 AS Old", result.SqlTemplate);
        Assert.Contains("SELECT d.Old", result.SqlTemplate);
        AssertDacViewCompiles(result.SqlTemplate);
    }

    [Theory]
    [InlineData("WITH c AS (SELECT * FROM dbo.Items) SELECT c.Old FROM c WHERE c.Id IN (SELECT i.Old FROM dbo.Items i);")]
    [InlineData("SELECT d.Old FROM (SELECT i.Id AS X,i.New AS X FROM dbo.Items i) d;")]
    [InlineData("WITH c AS (SELECT i.Old FROM dbo.Items i UNION ALL SELECT c.Old FROM c) SELECT c.Old FROM c;")]
    [InlineData("SELECT i.Old FROM dbo.Items i JOIN dbo.Other i ON i.Id=i.Id;")]
    public void UnprovableShapeRecursiveOrAmbiguousScopesKeepOriginalText(string sql)
    {
        var result = Rewrite(sql);
        Assert.Equal(0, result.ReplacementCount);
        Assert.Equal(sql, result.SqlTemplate);
        Assert.Contains(result.Diagnostics, d => d.Severity == "error");
    }

    [Fact]
    public void PreservesOriginalCommentLiteralPlaceholderAndExplicitAliasBytesInNestedQueries()
    {
        const string sql =
            "-- untouched Old\nWITH c AS (SELECT i.Old AS Stable,N'Old' AS Literal FROM dbo.Items i WHERE i.Id IN ({{ids:Ids}})) SELECT c.Stable FROM c;";
        var recipe = Recipe(sql) with { Inputs = [new("Ids", "int")] };
        var result = new RecipeColumnRewriter().Preview(recipe, Graph, "db", "items", "Old", "New");
        Assert.Equal(1, result.ReplacementCount);
        Assert.Contains("-- untouched Old\n", result.SqlTemplate);
        Assert.Contains("i.[New] AS Stable,N'Old' AS Literal", result.SqlTemplate);
        Assert.Contains("{{ids:Ids}}", result.SqlTemplate);
    }

    [Fact]
    public void UnionOutputOrderByBindsToPreservedFirstBranchName()
    {
        var result = Rewrite("SELECT i.Old FROM dbo.Items i UNION ALL SELECT i.Old FROM dbo.Items i ORDER BY Old;");
        Assert.Equal(2, result.ReplacementCount);
        Assert.Contains("i.[New] AS [Old]", result.SqlTemplate);
        Assert.EndsWith("ORDER BY Old;", result.SqlTemplate);
        Assert.Equal("Valid", new RecipeValidator().Validate(Recipe(result.SqlTemplate), Graph).Status);
        Assert.Equal(
            "Broken",
            new RecipeValidator()
                .Validate(Recipe("SELECT i.Id FROM dbo.Items i UNION ALL SELECT i.Id FROM dbo.Items i ORDER BY Missing;"), Graph)
                .Status
        );
    }

    private static RelationshipGraph GraphWithoutOtherOld() =>
        Graph with
        {
            Tables = Graph
                .Tables.Select(t =>
                    t.Table.Id == "other"
                        ? t with
                        {
                            Table = t.Table with { Columns = t.Table.Columns.Where(c => c.Name != "Old").ToArray() },
                        }
                        : t
                )
                .ToArray(),
        };

    private static RecipeColumnRewriteResult Rewrite(string sql, RelationshipGraph graph) =>
        new RecipeColumnRewriter().Preview(Recipe(sql), graph, "db", "items", "Old", "New");

    private static void AssertDacViewCompiles(string sql, int columns = 1)
    {
        using var model = new TSqlModel(SqlServerVersion.Sql160, new TSqlModelOptions());
        model.AddObjects("CREATE TABLE dbo.Items(Id int NOT NULL,New int NULL);");
        model.AddObjects("CREATE TABLE dbo.Other(Id int NOT NULL,Old int NULL,New int NULL);");
        var names = string.Join(",", Enumerable.Range(1, columns).Select(i => "[C" + i + "]"));
        model.AddObjects("CREATE VIEW dbo.RepairProof(" + names + ") AS " + sql);
        Assert.Empty(model.Validate());
    }
}
