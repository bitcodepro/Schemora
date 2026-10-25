using System.Text.Json;
using Schemora.Relationships;
using Schemora.SqlServer;
using Xunit;

namespace Schemora.RecipeEngine.Tests;

public sealed class RecipeValidatorTests
{
    [Fact]
    public void DerivesDependenciesFromSqlAndTypedIdSubqueryNotCallerClaims()
    {
        var recipe = Recipe("SELECT t.TransactionId,t.InvoiceId FROM dbo.Transactions t WHERE t.TransactionId IN ({{ids:Ids}})") with
        {
            Dependencies = [new("forged", "fake", "Secret")],
        };
        var result = Validate(recipe);
        Assert.Equal("Valid", result.Status);
        Assert.Contains(new RecipeDependency("db", "db:transactions"), result.Dependencies);
        Assert.Contains(new RecipeDependency("db", "db:transactions", "TransactionId"), result.Dependencies);
        Assert.Contains(new RecipeDependency("db", "db:transactions", "InvoiceId"), result.Dependencies);
        Assert.DoesNotContain(result.Dependencies, d => d.DatabaseId == "forged" || d.TableId.Contains("schemora"));
    }

    [Fact]
    public void ValidatesTableInputValueOrdinalAndNumericWidth()
    {
        var result = Validate(
            Recipe("SELECT t.TransactionId,i.Ordinal FROM dbo.Transactions t JOIN {{table:Ids}} i ON i.Value=t.TransactionId")
        );
        Assert.Equal("Valid", result.Status);
        Assert.DoesNotContain(result.Dependencies, d => d.ColumnName == "Value" || d.ColumnName == "Ordinal");
        var mismatch = Validate(
            Recipe("SELECT t.TransactionId FROM dbo.Transactions t WHERE t.TransactionId IN ({{ids:Ids}})") with
            {
                Inputs = [new("Ids", "uniqueidentifier")],
            }
        );
        Assert.Equal("Broken", mismatch.Status);
        Assert.Contains(mismatch.Diagnostics, d => d.Code == "RECIPE_INPUT_TYPE_MISMATCH");
        var width = Validate(
            Recipe("SELECT t.TransactionId FROM dbo.Transactions t WHERE t.TransactionId IN ({{ids:Ids}})") with
            {
                Inputs = [new("Ids", "int")],
            }
        );
        Assert.Equal("Warning", width.Status);
        Assert.Contains(width.Diagnostics, d => d.Code == "RECIPE_INPUT_NUMERIC_CONVERSION");
        Assert.Equal("Broken", Validate(Recipe("SELECT i.Unknown FROM {{table:Ids}} i")).Status);
    }

    [Fact]
    public void MissingTableAndRemovedColumnProduceExactBrokenReasons()
    {
        var missing = Validate(Recipe("SELECT x.Id FROM dbo.Missing x", false));
        Assert.Equal("Broken", missing.Status);
        Assert.Contains(missing.Diagnostics, d => d.Code == "RECIPE_TABLE_MISSING" && d.Message.Contains("dbo.Missing"));
        var removed = Validate(Recipe("SELECT t.RemovedId FROM dbo.Transactions t", false));
        Assert.Equal("Broken", removed.Status);
        Assert.Contains(removed.Diagnostics, d => d.Code == "RECIPE_COLUMN_MISSING" && d.Message.Contains("dbo.Transactions.RemovedId"));
    }

    [Fact]
    public void RequiresReviewForAmbiguousDatabaseOrSchemaNames()
    {
        var graph = Graph();
        var second = Table("other", "Other", "dbo", "Transactions", [new("TransactionId", "bigint", false)], ["TransactionId"]);
        graph = graph with { Tables = graph.Tables.Append(second).ToArray() };
        var recipe = Recipe("SELECT t.TransactionId FROM dbo.Transactions t", false) with { DatabaseIds = ["db", "other"] };
        Assert.Equal("NeedsReview", Validate(recipe, graph).Status);
        Assert.Equal(
            "Valid",
            Validate(recipe with { SqlTemplate = "SELECT t.TransactionId FROM Commerce.dbo.Transactions t" }, graph).Status
        );
        graph = Graph() with
        {
            Tables = Graph()
                .Tables.Append(Table("db", "Commerce", "other", "Transactions", [new("TransactionId", "bigint", false)], ["TransactionId"]))
                .ToArray(),
        };
        Assert.Equal("NeedsReview", Validate(Recipe("SELECT TransactionId FROM Transactions", false), graph).Status);
        Assert.Equal("Valid", Validate(Recipe("SELECT TransactionId FROM dbo.Transactions", false), graph).Status);
    }

    [Fact]
    public void TracksTrustedJoinAndRequiresConfirmationForObservedOrPartialCompositeJoin()
    {
        var recipe = Recipe(
            "SELECT t.TransactionId,i.Status FROM dbo.Transactions t JOIN sales.Invoices i ON t.InvoiceId=i.InvoiceId",
            false
        );
        var result = Validate(recipe);
        Assert.Equal("Valid", result.Status);
        Assert.Contains(result.Dependencies, d => d.RelationId == "fk:invoice");
        var graph = Graph();
        graph = graph with { Relations = graph.Relations.Select(e => e with { IsTrusted = false, Status = "observed" }).ToArray() };
        var review = Validate(recipe, graph);
        Assert.Equal("NeedsReview", review.Status);
        Assert.Contains(review.Diagnostics, d => d.Code == "RECIPE_JOIN_UNCONFIRMED");
        Assert.Contains(review.Dependencies, d => d.RelationId == "fk:invoice");
        graph = Graph();
        graph = graph with
        {
            Relations =
            [
                graph.Relations[0] with
                {
                    Relation = graph.Relations[0].Relation with
                    {
                        FromColumns = ["TenantId", "InvoiceId"],
                        ToColumns = ["TenantId", "InvoiceId"],
                    },
                },
            ],
        };
        Assert.Equal("NeedsReview", Validate(recipe, graph).Status);
        Assert.Equal("Valid", Validate(recipe with { SqlTemplate = recipe.SqlTemplate + " AND t.TenantId=i.TenantId" }, graph).Status);
    }

    [Fact]
    public void BindsCtesDerivedTablesRenamesAndLineageThroughTrustedJoins()
    {
        var recipe = Recipe(
            "WITH tx AS (SELECT TransactionId,InvoiceId FROM dbo.Transactions), chosen(Id,Invoice) AS (SELECT x.TransactionId,x.InvoiceId FROM tx x) SELECT d.Id FROM (SELECT c.Id,c.Invoice FROM chosen c) d JOIN sales.Invoices i ON d.Invoice=i.InvoiceId WHERE d.Id IN ({{ids:Ids}})"
        );
        var result = Validate(recipe);
        Assert.Equal("Valid", result.Status);
        Assert.Contains(result.Dependencies, d => d.TableId == "db:transactions" && d.ColumnName == "InvoiceId");
        Assert.Contains(result.Dependencies, d => d.RelationId == "fk:invoice");
    }

    [Fact]
    public void BindsCorrelatedExistsAndScalarSubqueries()
    {
        var recipe = Recipe(
            "SELECT t.TransactionId,(SELECT i.Status FROM sales.Invoices i WHERE i.InvoiceId=t.InvoiceId) AS Status FROM dbo.Transactions t WHERE EXISTS (SELECT 1 FROM sales.Invoices i WHERE i.InvoiceId=t.InvoiceId) AND t.TransactionId IN ({{ids:Ids}})"
        );
        var result = Validate(recipe);
        Assert.Equal("Valid", result.Status);
        Assert.Contains(result.Dependencies, d => d.TableId == "db:invoices" && d.ColumnName == "Status");
        Assert.Contains(result.Dependencies, d => d.RelationId == "fk:invoice");
    }

    [Fact]
    public void WildcardsDependOnEveryColumnWhileCountStarDependsOnlyOnTable()
    {
        var wildcard = Validate(Recipe("SELECT t.* FROM dbo.Transactions t", false));
        Assert.Equal("Valid", wildcard.Status);
        Assert.Equal(3, wildcard.Dependencies.Count(d => d.ColumnName is not null));
        var count = Validate(Recipe("SELECT COUNT(*) AS Total FROM dbo.Transactions", false));
        Assert.Equal("Valid", count.Status);
        Assert.Single(count.Dependencies);
        Assert.Null(count.Dependencies[0].ColumnName);
    }

    [Fact]
    public void SupportsGroupingAndOrderByOutputAliasWithoutGuessingWhereAliases()
    {
        var result = Validate(
            Recipe(
                "SELECT t.InvoiceId AS K,COUNT(*) AS Total FROM dbo.Transactions t GROUP BY t.InvoiceId HAVING COUNT(*)>0 ORDER BY Total",
                false
            )
        );
        Assert.Equal("Valid", result.Status);
        Assert.Equal("Broken", Validate(Recipe("SELECT t.InvoiceId AS K FROM dbo.Transactions t WHERE K=1", false)).Status);
    }

    [Fact]
    public void ComplexOrJoinsCartesianAndOpaqueRowsetsNeedReview()
    {
        Assert.Equal(
            "NeedsReview",
            Validate(
                Recipe(
                    "SELECT t.TransactionId FROM dbo.Transactions t JOIN sales.Invoices i ON t.InvoiceId=i.InvoiceId OR t.TenantId=i.TenantId",
                    false
                )
            ).Status
        );
        Assert.Equal(
            "NeedsReview",
            Validate(Recipe("SELECT t.TransactionId FROM dbo.Transactions t CROSS JOIN sales.Invoices i", false)).Status
        );
        Assert.Equal("NeedsReview", Validate(Recipe("SELECT x.Value FROM OPENJSON(N'[]') x", false)).Status);
    }

    [Fact]
    public void HandlesCommaWhereJoinsAndRejectsUndeclaredRuntimeParameters()
    {
        Assert.Equal(
            "Valid",
            Validate(Recipe("SELECT t.TransactionId FROM dbo.Transactions t,sales.Invoices i WHERE t.InvoiceId=i.InvoiceId", false)).Status
        );
        var invalid = Validate(Recipe("SELECT t.TransactionId FROM dbo.Transactions t WHERE t.TransactionId=@Ids", false));
        Assert.Equal("Broken", invalid.Status);
        Assert.Contains(invalid.Diagnostics, d => d.Code == "RECIPE_PARAMETER_UNDECLARED");
    }

    [Fact]
    public void SetOperationsAndDerivedShapeHaveConservativeValidation()
    {
        Assert.Equal(
            "Valid",
            Validate(
                Recipe("SELECT t.TransactionId FROM dbo.Transactions t UNION ALL SELECT t.TransactionId FROM dbo.Transactions t", false)
            ).Status
        );
        Assert.Equal(
            "Broken",
            Validate(
                Recipe("SELECT t.TransactionId FROM dbo.Transactions t UNION SELECT i.InvoiceId,i.TenantId FROM sales.Invoices i", false)
            ).Status
        );
        Assert.Equal(
            "Broken",
            Validate(Recipe("WITH x(a,b) AS (SELECT TransactionId FROM dbo.Transactions) SELECT x.a FROM x", false)).Status
        );
    }

    [Fact]
    public void InvalidDefinitionsAndParserErrorsDoNotRetainLiteralValues()
    {
        const string secret = "PRIVATE_RUNTIME_ID_71fe9";
        var result = Validate(Recipe("SELECT FROM '" + secret + "'", false));
        Assert.Equal("Broken", result.Status);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result));
        Assert.Equal("Broken", Validate(Recipe("SELECT '{{ids:Ids}}'")).Status);
        Assert.Equal("Broken", Validate(Recipe(" ", false)).Status);
    }

    [Fact]
    public void TextLengthAndUnicodeConversionAreWarningsRatherThanSilentCoercion()
    {
        var recipe = Recipe("SELECT i.InvoiceId FROM sales.Invoices i WHERE i.Status IN ({{ids:Ids}})") with
        {
            Inputs = [new("Ids", "nvarchar", 100)],
        };
        Assert.Equal("Warning", Validate(recipe).Status);
        Assert.Contains(Validate(recipe).Diagnostics, d => d.Code == "RECIPE_INPUT_LENGTH");
        Assert.Contains(
            Validate(recipe with { Inputs = [new("Ids", "varchar", 20)] }).Diagnostics,
            d => d.Code == "RECIPE_INPUT_TEXT_CONVERSION"
        );
    }

    [Theory]
    [InlineData("SELECT t.TransactionId FROM dbo.Transactions t WHERE t.InvoiceId IN (SELECT i.InvoiceId FROM sales.Invoices i)")]
    [InlineData(
        "SELECT t.TransactionId FROM dbo.Transactions t WHERE t.InvoiceId=(SELECT i.InvoiceId FROM sales.Invoices i WHERE i.InvoiceId=1)"
    )]
    public void SubqueryRelationshipsRequireTheSameExactTrustAsExplicitJoins(string sql)
    {
        var recipe = Recipe(sql, false);
        var graph = Graph();
        Assert.Equal("Valid", Validate(recipe, graph).Status);
        graph = graph with { Relations = graph.Relations.Select(edge => edge with { IsTrusted = false, Status = "observed" }).ToArray() };
        var result = Validate(recipe, graph);
        Assert.Equal("NeedsReview", result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == "RECIPE_SUBQUERY_JOIN_UNCONFIRMED");
        Assert.Contains(result.Dependencies, d => d.RelationId == "fk:invoice");
    }

    [Fact]
    public void ComputedAndIncompleteCompositeSubqueryRelationshipsNeedReview()
    {
        Assert.Equal(
            "NeedsReview",
            Validate(
                Recipe(
                    "SELECT t.TransactionId FROM dbo.Transactions t WHERE t.InvoiceId=(SELECT MAX(i.InvoiceId) FROM sales.Invoices i)",
                    false
                )
            ).Status
        );
        var graph = Graph();
        graph = graph with
        {
            Relations =
            [
                graph.Relations[0] with
                {
                    Relation = graph.Relations[0].Relation with
                    {
                        FromColumns = ["TenantId", "InvoiceId"],
                        ToColumns = ["TenantId", "InvoiceId"],
                    },
                },
            ],
        };
        Assert.Equal(
            "NeedsReview",
            Validate(
                Recipe(
                    "SELECT t.TransactionId FROM dbo.Transactions t WHERE t.InvoiceId IN (SELECT i.InvoiceId FROM sales.Invoices i)",
                    false
                ),
                graph
            ).Status
        );
    }

    private static RecipeValidationResult Validate(RecipeDefinition recipe, RelationshipGraph? graph = null) =>
        new RecipeValidator().Validate(recipe, graph ?? Graph());

    private static RecipeDefinition Recipe(string sql, bool input = true) =>
        new("recipe", "Example", "", [], "sqlserver", ["db"], 1, sql, input ? [new("Ids", "bigint")] : []);

    private static RelationshipTable Table(
        string db,
        string name,
        string schema,
        string table,
        IReadOnlyList<SchemaColumn> columns,
        IReadOnlyList<string> keys
    ) => new(db, name, new(db + ":" + table.ToLowerInvariant(), schema, table, columns, keys));

    private static RelationshipGraph Graph()
    {
        var transactions = Table(
            "db",
            "Commerce",
            "dbo",
            "Transactions",
            [new("TransactionId", "bigint", false), new("InvoiceId", "int", true), new("TenantId", "int", false)],
            ["TransactionId"]
        );
        var invoices = Table(
            "db",
            "Commerce",
            "sales",
            "Invoices",
            [new("InvoiceId", "int", false), new("TenantId", "int", false), new("Status", "nvarchar(20)", false)],
            ["InvoiceId"]
        );
        return new(
            [transactions, invoices],
            [
                new(
                    new(
                        "fk:invoice",
                        "FK_Transactions_Invoices",
                        transactions.Table.Id,
                        invoices.Table.Id,
                        ["InvoiceId"],
                        ["InvoiceId"],
                        "declared-fk",
                        1
                    ),
                    "physical",
                    true,
                    []
                ),
            ],
            []
        );
    }
}
