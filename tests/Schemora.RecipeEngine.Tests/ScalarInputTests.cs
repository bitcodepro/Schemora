using Schemora.Relationships;
using Schemora.SqlServer;
using Xunit;

namespace Schemora.RecipeEngine.Tests;

public sealed class ScalarInputTests
{
    private const string PeriodTemplate =
        "SELECT o.OrderId FROM dbo.Orders AS o WHERE o.CreatedAt >= {{value:From}} AND o.CreatedAt < {{value:To}} AND o.IsArchived = {{value:Archived}} AND o.OrderId IN ({{ids:Ids}});";

    private static RecipeDefinition Period(string template = PeriodTemplate) =>
        new(
            "recipe",
            "Orders in a period",
            "",
            [],
            "sqlserver",
            ["db"],
            1,
            template,
            [new("From", "date"), new("To", "datetime2"), new("Archived", "bit"), new("Ids", "bigint")],
            CompatibilityLevel: 130
        );

    private static Dictionary<string, string> Values(
        string from = "2026-10-01",
        string to = "2026-11-01T08:30",
        string archived = "false"
    ) =>
        new()
        {
            ["From"] = from,
            ["To"] = to,
            ["Archived"] = archived,
            ["Ids"] = "7,8",
        };

    [Fact]
    public void DatesAndBitsParseStrictlyAndCompareByValue()
    {
        var processor = new IdProcessor();
        var dates = processor.Parse("2026-10-01,2026-13-01,01.10.2026", new("Day", "date"));
        Assert.Equal(1, dates.Statistics.Valid);
        Assert.All(dates.Errors, error => Assert.Equal("DATE_INVALID", error.Code));
        var times = processor.Parse("2026-10-01T08:30,\"2026-10-01 08:30:00\",2026-10-01T25:00", new("At", "datetime2", Deduplicate: true));
        Assert.Equal(2, times.Statistics.Valid);
        Assert.Equal(1, times.Statistics.Duplicates);
        Assert.Equal("DATETIME_INVALID", Assert.Single(times.Errors).Code);
        var bits = processor.Parse("true,1,0,yes", new("Flag", "bit"));
        Assert.Equal(3, bits.Statistics.Valid);
        Assert.Equal(1, bits.Statistics.Duplicates);
        Assert.Equal("BIT_INVALID", Assert.Single(bits.Errors).Code);
    }

    [Fact]
    public void StandaloneValuesBecomeTypedLiteralsWithExplicitDateStyles()
    {
        var result = new SqlRecipeRenderer().Render(Period(), Values());
        Assert.Contains("o.CreatedAt >= CONVERT(date, N'2026-10-01', 23)", result.Sql, StringComparison.Ordinal);
        Assert.Contains("o.CreatedAt < CONVERT(datetime2, N'2026-11-01T08:30:00.0000000', 126)", result.Sql, StringComparison.Ordinal);
        Assert.Contains("o.IsArchived = CONVERT(bit, 0)", result.Sql, StringComparison.Ordinal);
        Assert.Contains("FROM (VALUES", result.Sql, StringComparison.Ordinal);
        Assert.Equal("values", result.Strategy);
        Assert.DoesNotContain(result.Warnings, warning => warning.StartsWith("From:", StringComparison.Ordinal));
    }

    [Fact]
    public void ParameterizedValuesReadTheSingleJsonEntryWithTheSameStyles()
    {
        var result = new SqlRecipeRenderer().Render(Period(), Values(), "parameterized");
        Assert.Contains(
            "o.CreatedAt >= (SELECT [v].[Value] FROM (SELECT [j].[Ordinal], CONVERT(date, [j].[Value], 23) AS [Value] FROM OPENJSON(@SchemoraInputs, '$.From')",
            result.Sql,
            StringComparison.Ordinal
        );
        Assert.Contains("CONVERT(datetime2, [j].[Value], 126)", result.Sql, StringComparison.Ordinal);
        var payload = Assert.Single(result.Parameters).Value;
        Assert.Contains("\"value\":\"2026-11-01T08:30:00.0000000\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"value\":\"0\"", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueInputRefusesMoreThanOneValue()
    {
        var error = Assert.Throws<RecipeEngineException>(() =>
            new SqlRecipeRenderer().Render(Period(), Values(from: "2026-10-01,2026-10-02"))
        );
        Assert.Equal("VALUE_INPUT_SINGLE", error.Code);
    }

    [Fact]
    public void ARecipeWithOnlyValuesReportsTheScalarStrategy()
    {
        var recipe = new RecipeDefinition(
            "recipe",
            "Since",
            "",
            [],
            "sqlserver",
            ["db"],
            1,
            "SELECT o.OrderId FROM dbo.Orders AS o WHERE o.CreatedAt >= {{value:From}};",
            [new("From", "date")]
        );
        var result = new SqlRecipeRenderer().Render(recipe, new Dictionary<string, string> { ["From"] = "2026-10-01" });
        Assert.Equal("SELECT o.OrderId FROM dbo.Orders AS o WHERE o.CreatedAt >= CONVERT(date, N'2026-10-01', 23);\n", result.Sql);
        Assert.Equal("scalar", result.Strategy);
    }

    [Fact]
    public void AnInputCannotBeBothAValueAndAList()
    {
        var mixed = Period(PeriodTemplate.Replace("{{value:From}}", "{{value:Ids}}", StringComparison.Ordinal)) with
        {
            Inputs = [new("To", "datetime2"), new("Archived", "bit"), new("Ids", "bigint")],
        };
        Assert.Equal("PLACEHOLDER_KIND_MIXED", Assert.Throws<RecipeEngineException>(() => RecipeTemplate.ValidateDefinition(mixed)).Code);
    }

    [Fact]
    public void ValidationChecksValueTypesAgainstTheColumnsTheyMeet()
    {
        var orders = new RelationshipTable(
            "db",
            "Commerce",
            new(
                "db:orders",
                "dbo",
                "Orders",
                [new("OrderId", "bigint", false), new("CreatedAt", "datetime2(7)", false), new("IsArchived", "bit", false)],
                ["OrderId"]
            )
        );
        var graph = new RelationshipGraph([orders], [], []);
        var result = new RecipeValidator().Validate(Period(), graph);
        Assert.NotEqual("Broken", result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "RECIPE_INPUT_DATE_CONVERSION");
        Assert.Contains(new RecipeDependency("db", "db:orders", "CreatedAt"), result.Dependencies);
        var wrong = new RecipeValidator().Validate(
            Period(
                PeriodTemplate.Replace("o.IsArchived = {{value:Archived}}", "o.CreatedAt = {{value:Archived}}", StringComparison.Ordinal)
            ),
            graph
        );
        Assert.Equal("Broken", wrong.Status);
        Assert.Contains(wrong.Diagnostics, diagnostic => diagnostic.Code == "RECIPE_INPUT_TYPE_MISMATCH");
    }

    [Fact]
    public void DecimalsNeedPrecisionAndRespectIt()
    {
        Assert.Equal(
            "INPUT_PRECISION_REQUIRED",
            Assert.Throws<RecipeEngineException>(() => new IdProcessor().Parse("1", new("Amount", "decimal"))).Code
        );
        Assert.Equal(
            "INPUT_TYPE_OPTIONS_INVALID",
            Assert.Throws<RecipeEngineException>(() => new IdProcessor().Parse("1", new("Ids", "int", Precision: 10, Scale: 2))).Code
        );
        var amounts = new IdProcessor().Parse(
            "1500.50,1500.5,+0001500.500,-0.00,12345678.9,1.234,1e3,1,5",
            new("Amount", "decimal", Deduplicate: true, Precision: 10, Scale: 2)
        );
        // "1,5" is two list values, not a comma decimal; 1.234 exceeds the scale and 1e3 is not plain digits.
        Assert.Equal(7, amounts.Statistics.Valid);
        Assert.Equal(2, amounts.Statistics.Duplicates);
        Assert.All(amounts.Errors, error => Assert.Equal("DECIMAL_INVALID", error.Code));
        Assert.Equal(2, amounts.Errors.Count);
    }

    [Fact]
    public void DecimalValuesRenderAsExactNumericLiterals()
    {
        var recipe = new RecipeDefinition(
            "recipe",
            "Large orders",
            "",
            [],
            "sqlserver",
            ["db"],
            1,
            "SELECT o.OrderId FROM dbo.Orders AS o WHERE o.Total > {{value:Minimum}};",
            [new("Minimum", "decimal", Precision: 38, Scale: 10)],
            CompatibilityLevel: 130
        );
        var huge = "1234567890123456789012345678.1234567891";
        var standalone = new SqlRecipeRenderer().Render(recipe, new Dictionary<string, string> { ["Minimum"] = huge });
        Assert.Contains("o.Total > CONVERT(decimal(38,10), " + huge + ")", standalone.Sql, StringComparison.Ordinal);
        var parameterized = new SqlRecipeRenderer().Render(
            recipe,
            new Dictionary<string, string> { ["Minimum"] = "0010.50" },
            "parameterized"
        );
        Assert.Contains("CONVERT(decimal(38,10), [j].[Value])", parameterized.Sql, StringComparison.Ordinal);
        Assert.Contains("\"value\":\"10.5\"", Assert.Single(parameterized.Parameters).Value, StringComparison.Ordinal);
    }

    private static RecipeDefinition OptionalPeriod() =>
        Period(
            "SELECT o.OrderId FROM dbo.Orders AS o WHERE ({{value:From}} IS NULL OR o.CreatedAt >= {{value:From}}) AND o.CreatedAt < {{value:To}};"
        ) with
        {
            Inputs = [new("From", "date", Optional: true), new("To", "datetime2")],
        };

    [Fact]
    public void AnEmptyOptionalValueRendersAsATypedNull()
    {
        var renderer = new SqlRecipeRenderer();
        var empty = renderer.Render(OptionalPeriod(), new Dictionary<string, string> { ["From"] = "  ", ["To"] = "2026-11-01T00:00" });
        Assert.Contains("(CAST(NULL AS date) IS NULL OR o.CreatedAt >= CAST(NULL AS date))", empty.Sql, StringComparison.Ordinal);
        Assert.Contains(empty.Warnings, warning => warning.StartsWith("From: no value was entered", StringComparison.Ordinal));
        // A missing key counts as empty too, so command-line renders can leave optional inputs out.
        Assert.Equal(empty.Sql, renderer.Render(OptionalPeriod(), new Dictionary<string, string> { ["To"] = "2026-11-01T00:00" }).Sql);
        var filled = renderer.Render(
            OptionalPeriod(),
            new Dictionary<string, string> { ["From"] = "2026-10-01", ["To"] = "2026-11-01T00:00" }
        );
        Assert.Contains("CONVERT(date, N'2026-10-01', 23) IS NULL", filled.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CAST(NULL", filled.Sql, StringComparison.Ordinal);
        // Parameterized SQL reads an empty JSON array, which the scalar subquery turns into NULL.
        var parameterized = renderer.Render(
            OptionalPeriod(),
            new Dictionary<string, string> { ["From"] = "", ["To"] = "2026-11-01T00:00" },
            "parameterized"
        );
        Assert.Contains("\"From\":[]", Assert.Single(parameterized.Parameters).Value, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlySingleValueInputsCanBeOptional()
    {
        Assert.Equal(
            "ID_VALUES_REQUIRED",
            Assert.Throws<RecipeEngineException>(() => new SqlRecipeRenderer().Render(Period(), Values(from: ""))).Code
        );
        var list = Period() with
        {
            Inputs = [new("From", "date"), new("To", "datetime2"), new("Archived", "bit"), new("Ids", "bigint", Optional: true)],
        };
        Assert.Equal("INPUT_OPTIONAL_VALUE_ONLY", Assert.Throws<RecipeEngineException>(() => RecipeTemplate.ValidateDefinition(list)).Code);
        // An invalid value is still an error: optional only allows leaving the field empty.
        Assert.Equal(
            "ID_VALIDATION_FAILED",
            Assert
                .Throws<RecipeEngineException>(() =>
                    new SqlRecipeRenderer().Render(
                        OptionalPeriod(),
                        new Dictionary<string, string> { ["From"] = "soon", ["To"] = "2026-11-01T00:00" }
                    )
                )
                .Code
        );
    }
}
