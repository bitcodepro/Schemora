using System.Globalization;
using System.Text.Json;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;

namespace Schemora.RecipeEngine.Tests;

public sealed class IdProcessorTests
{
    [Fact]
    public void ParsesCsvTsvWhitespaceSqlListsAndCountsDuplicatesWithoutDroppingThem()
    {
        var result = new IdProcessor().Parse("IN (1,2;3\t4\r\n5 2)", new("Ids", "int"));
        Assert.Equal(new IdStatistics(6, 6, 0, 5, 1, 6), result.Statistics);
        Assert.Equal(new[] { "1", "2", "3", "4", "5", "2" }, result.Values.Select(value => value.Value));
        Assert.Equal(2, result.Values[4].Line);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void DeduplicationAndSortingAreExplicitAndRetainOriginalPositions()
    {
        var parser = new IdProcessor();
        var original = parser.Parse("3,1,3,2", new("Ids", "bigint"));
        Assert.Equal(4, original.Statistics.OutputCount);
        var unique = parser.Parse("3,1,3,2", new("Ids", "bigint", Deduplicate: true));
        Assert.Equal(new[] { "3", "1", "2" }, unique.Values.Select(value => value.Value));
        Assert.Equal(new[] { 1, 2, 4 }, unique.Values.Select(value => value.Index));
        var sorted = parser.Parse("3,1,3,2", new("Ids", "bigint", Deduplicate: true, PreserveOrder: false));
        Assert.Equal(new[] { "1", "2", "3" }, sorted.Values.Select(value => value.Value));
    }

    [Theory]
    [InlineData("int", "2147483647,-2147483648", 2)]
    [InlineData("int", "2147483648,-2147483649,1.5,1e2,+,١", 0)]
    [InlineData("bigint", "9223372036854775807,-9223372036854775808", 2)]
    [InlineData("bigint", "9223372036854775808,-9223372036854775809", 0)]
    public void EnforcesExactSignedSqlIntegerRanges(string type, string text, int valid)
    {
        var result = new IdProcessor().Parse(text, new("Ids", type));
        Assert.Equal(valid, result.Statistics.Valid);
        Assert.Equal(result.Statistics.Total - valid, result.Statistics.Invalid);
    }

    [Fact]
    public void GuidCasingAndQuotedUnicodeStringsAreNeverNormalized()
    {
        const string first = "abcdefab-1234-4567-89ab-abcdefabcdef";
        var guids = new IdProcessor().Parse(first + "," + first.ToUpperInvariant(), new("Ids", "uniqueidentifier", Deduplicate: true));
        Assert.Equal(2, guids.Statistics.Unique);
        Assert.Equal(2, guids.Statistics.OutputCount);
        Assert.Equal(first.ToUpperInvariant(), guids.Values[1].Value);
        var text = new IdProcessor().Parse("N'O''Brien',\"a,b\",‘Україна’,“a””b”,\"  padded  \",\"a\"\"b\"", new("Ids", "nvarchar", 100));
        Assert.Equal(new[] { "O'Brien", "a,b", "Україна", "a”b", "  padded  ", "a\"b" }, text.Values.Select(value => value.Value));
        Assert.Empty(text.Errors);
    }

    [Fact]
    public void EmptyCellsMalformedQuotesAndBadGuidReportItemAndLine()
    {
        var integer = new IdProcessor().Parse("1,,bad\n4,", new("Ids", "int"));
        Assert.Equal(new IdStatistics(5, 2, 3, 2, 0, 2), integer.Statistics);
        Assert.Equal(new[] { 2, 3, 5 }, integer.Errors.Select(error => error.Index));
        Assert.Equal(2, integer.Errors[^1].Line);
        var tsv = new IdProcessor().Parse("\t1\t", new("Ids", "int"));
        Assert.Equal(2, tsv.Statistics.Invalid);
        Assert.Equal(3, tsv.Statistics.Total);
        Assert.Equal("UNTERMINATED_QUOTE", Assert.Single(new IdProcessor().Parse("'unclosed", new("Ids", "nvarchar", 50)).Errors).Code);
        Assert.Equal(
            "GUID_INVALID",
            Assert.Single(new IdProcessor().Parse("'{abcdefab-1234-4567-89ab-abcdefabcdef}'", new("Ids", "uniqueidentifier")).Errors).Code
        );
    }

    [Fact]
    public void TextLengthUsesUtf16ForNvarcharAndDeclaredCodePageBytesForVarchar()
    {
        Assert.Equal(1, new IdProcessor().Parse("😀", new("Ids", "nvarchar", 2)).Statistics.Valid);
        Assert.Equal("TEXT_LENGTH_EXCEEDED", Assert.Single(new IdProcessor().Parse("😀", new("Ids", "nvarchar", 1)).Errors).Code);
        Assert.Equal("TEXT_ENCODING_INVALID", Assert.Single(new IdProcessor().Parse("Київ", new("Ids", "varchar", 20)).Errors).Code);
        Assert.Equal(
            "VARCHAR_COLLATION_REQUIRED",
            Assert.Single(new IdProcessor().Parse("Київ", new("Ids", "varchar", 20, "utf-8")).Errors).Code
        );
        var utf8 = new IdProcessor().Parse("Київ", new("Ids", "varchar", 8, "utf-8", Collation: "Latin1_General_100_BIN2_UTF8"));
        Assert.Equal(1, utf8.Statistics.Valid);
        Assert.Equal(
            "TEXT_LENGTH_EXCEEDED",
            Assert
                .Single(
                    new IdProcessor().Parse("Київ", new("Ids", "varchar", 7, "utf-8", Collation: "Latin1_General_100_BIN2_UTF8")).Errors
                )
                .Code
        );
        Assert.Equal(
            1,
            new IdProcessor()
                .Parse("Київ", new("Ids", "varchar", 4, "windows-1251", Collation: "Cyrillic_General_100_CI_AS"))
                .Statistics.Valid
        );
    }

    [Fact]
    public void UnsupportedDefinitionsAndEncodingCollationMismatchFailClearly()
    {
        Assert.Equal(
            "INPUT_LENGTH_REQUIRED",
            Assert.Throws<RecipeEngineException>(() => new IdProcessor().Parse("a", new("Ids", "nvarchar"))).Code
        );
        Assert.Equal(
            "INPUT_ENCODING_COLLATION_MISMATCH",
            Assert
                .Throws<RecipeEngineException>(() =>
                    new IdProcessor().Parse("a", new("Ids", "varchar", 20, "utf-8", Collation: "Cyrillic_General_100_CI_AS"))
                )
                .Code
        );
        Assert.Equal(
            "INPUT_NAME_INVALID",
            Assert.Throws<RecipeEngineException>(() => new IdProcessor().Parse("1", new("bad.name", "int"))).Code
        );
    }

    [Fact]
    public void BareTextInIsAnIdentifierUnlessItIntroducesAParenthesizedSqlList()
    {
        var parser = new IdProcessor();
        Assert.Equal("IN", Assert.Single(parser.Parse("IN", new("Ids", "nvarchar", 20)).Values).Value);
        Assert.Equal(new[] { "IN", "Text" }, parser.Parse("IN Text", new("Ids", "nvarchar", 20)).Values.Select(value => value.Value));
        Assert.Equal(
            new[] { "IN", "Text" },
            parser.Parse("IN (N'IN',N'Text')", new("Ids", "nvarchar", 20)).Values.Select(value => value.Value)
        );
    }

    [Fact]
    public void ErrorDetailsAreBoundedWhileInvalidCountsRemainExact()
    {
        var result = new IdProcessor().Parse(string.Join(',', Enumerable.Repeat("not-an-int", 2000)), new("Ids", "int"));
        Assert.Equal(2000, result.Statistics.Total);
        Assert.Equal(2000, result.Statistics.Invalid);
        Assert.Equal(1000, result.Errors.Count);
    }
}

public sealed class SqlRecipeRendererTests
{
    internal static RecipeDefinition Definition(RecipeInputDefinition? input = null, string? template = null, int compatibility = 130) =>
        new(
            "recipe",
            "Recipe",
            "Test recipe",
            [],
            "sqlserver",
            ["db"],
            1,
            template ?? "SELECT [t].[Id] FROM [dbo].[Items] AS [t] WHERE [t].[Id] IN ({{ids:Ids}});",
            [input ?? new("Ids", "int")],
            CompatibilityLevel: compatibility
        );

    [Fact]
    public void ListStrategyWritesPlainIntegerLiteralsAndDropsTheOrdinalWarning()
    {
        var sql = new SqlRecipeRenderer().Render(Definition(), new Dictionary<string, string> { ["Ids"] = "2,1,2" }, strategy: "list");
        Assert.Equal("SELECT [t].[Id] FROM [dbo].[Items] AS [t] WHERE [t].[Id] IN (2, 1, 2);\n", sql.Sql);
        Assert.Equal("list", sql.Strategy);
        Assert.DoesNotContain(sql.Warnings, warning => warning.Contains("[Ordinal]", StringComparison.Ordinal));
    }

    [Fact]
    public void ListStrategyKeepsTypedLiteralsForTextAndARowsetForTablePlaceholders()
    {
        var text = new SqlRecipeRenderer().Render(
            Definition(new("Ids", "nvarchar", 20, Strategy: "list")),
            new Dictionary<string, string> { ["Ids"] = "O'Brien" }
        );
        Assert.Contains("IN (CONVERT(nvarchar(20), N'O''Brien')", text.Sql, StringComparison.Ordinal);
        var table = new SqlRecipeRenderer().Render(
            Definition(
                template: "SELECT [t].[Id] FROM [dbo].[Items] AS [t] JOIN {{table:Ids}} AS [i] ON [i].[Value] = [t].[Id] WHERE [t].[Id] IN ({{ids:Ids}});"
            ),
            new Dictionary<string, string> { ["Ids"] = "7" },
            strategy: "list"
        );
        Assert.Contains("FROM (VALUES", table.Sql, StringComparison.Ordinal);
        Assert.Contains("IN (7)", table.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ListStrategyRefusesMoreThanAThousandValues()
    {
        var ids = string.Join(',', Enumerable.Range(1, SqlRecipeRenderer.MaximumListValues + 1));
        var error = Assert.Throws<RecipeEngineException>(() =>
            new SqlRecipeRenderer().Render(Definition(), new Dictionary<string, string> { ["Ids"] = ids }, strategy: "list")
        );
        Assert.Equal("LIST_STRATEGY_LIMIT", error.Code);
    }

    [Fact]
    public void SmallIntegerScriptMatchesGoldenAndRendersDeterministicallyAcrossCultures()
    {
        var recipe = Definition();
        var renderer = new SqlRecipeRenderer();
        var sql = renderer.Render(recipe, new Dictionary<string, string> { ["Ids"] = "2,1" });
        const string expected = """
            SELECT [t].[Id] FROM [dbo].[Items] AS [t] WHERE [t].[Id] IN (SELECT [i].[Value] FROM (SELECT [v].[Ordinal], [v].[Value] FROM (VALUES
                (1, CONVERT(int, 2)),
                (2, CONVERT(int, 1))
            ) AS [v]([Ordinal], [Value])) AS [i]);
            """;
        Assert.Equal(expected + "\n", sql.Sql);
        Assert.Equal("values", sql.Strategy);
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal(sql.Sql, renderer.Render(recipe, new Dictionary<string, string> { ["Ids"] = "2,1" }).Sql);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void TenThousandValuesProduceExactOrderedRowsInBatchesOfAtMostOneThousand()
    {
        var ids = string.Join('\n', Enumerable.Range(1, 10_000));
        var result = new SqlRecipeRenderer().Render(Definition(), new Dictionary<string, string> { ["Ids"] = ids });
        Assert.Equal("temp", result.Strategy);
        Assert.Equal(10_000, result.Statistics["Ids"].OutputCount);
        var visitor = new Inserts();
        Parse(result.Sql).Accept(visitor);
        Assert.Equal(10, visitor.Sizes.Count);
        Assert.All(visitor.Sizes, size => Assert.InRange(size, 1, 1000));
        Assert.Equal(Enumerable.Range(1, 10_000), visitor.Ordinals);
        Assert.Equal(Enumerable.Range(1, 10_000).Select(value => value.ToString(CultureInfo.InvariantCulture)), visitor.Values);
    }

    [Fact]
    public void InvalidMixedInputBlocksSqlCompletely()
    {
        var error = Assert.Throws<RecipeEngineException>(() =>
            new SqlRecipeRenderer().Render(Definition(), new Dictionary<string, string> { ["Ids"] = "1,bad,3" })
        );
        Assert.Equal("ID_VALIDATION_FAILED", error.Code);
        Assert.Single(error.Errors);
        Assert.Equal(2, error.Errors[0].Index);
    }

    [Fact]
    public void SqlQuotesAndUnicodeRemainDataRatherThanExecutableStatements()
    {
        const string hostile = "x'); DROP TABLE dbo.Victims;--";
        var input = "\"" + hostile + "\",N'Україна',N'O''Brien'";
        var result = new SqlRecipeRenderer().Render(
            Definition(new("Ids", "nvarchar", 100)),
            new Dictionary<string, string> { ["Ids"] = input }
        );
        var parsed = Parse(result.Sql);
        Assert.Single(parsed.Batches);
        Assert.Single(parsed.Batches[0].Statements);
        Assert.Contains("N'x''); DROP TABLE dbo.Victims;--'", result.Sql);
        Assert.Contains("N'Україна'", result.Sql);
        Assert.Equal(3, result.Statistics["Ids"].OutputCount);
    }

    [Fact]
    public void ParameterizedTenThousandValuesUseOneJsonParameterAndCopyableAdoExample()
    {
        var ids = string.Join(',', Enumerable.Range(1, 10_000));
        var result = new SqlRecipeRenderer().Render(Definition(), new Dictionary<string, string> { ["Ids"] = ids }, "parameterized");
        var parameter = Assert.Single(result.Parameters);
        Assert.Equal("@SchemoraInputs", parameter.Name);
        Assert.Equal("openjson", result.Strategy);
        using var payload = JsonDocument.Parse(parameter.Value);
        var array = payload.RootElement.GetProperty("Ids");
        Assert.Equal(10_000, array.GetArrayLength());
        Assert.Equal("1", array[0].GetProperty("value").GetString());
        Assert.Equal("10000", array[9999].GetProperty("value").GetString());
        Assert.Contains("OPENJSON(@SchemoraInputs", result.Sql);
        Assert.DoesNotContain("10000", result.Sql);
        Assert.Contains("SqlDbType.NVarChar, -1", result.ParameterizedExample);
        Parse(result.Sql);
    }

    [Fact]
    public void AllInputsShareOneParameterizedJsonObject()
    {
        var recipe = Definition() with
        {
            SqlTemplate = "SELECT a.[Value],b.[Value] FROM {{table:Ids}} AS a CROSS JOIN {{table:Names}} AS b;",
            Inputs = [new("Ids", "bigint"), new("Names", "nvarchar", 20)],
        };
        var result = new SqlRecipeRenderer().Render(
            recipe,
            new Dictionary<string, string> { ["Ids"] = "1,2", ["Names"] = "N'А',N'Б'" },
            "parameterized"
        );
        using var json = JsonDocument.Parse(Assert.Single(result.Parameters).Value);
        Assert.Equal(2, json.RootElement.EnumerateObject().Count());
        Assert.Equal(2, json.RootElement.GetProperty("Names").GetArrayLength());
        Parse(result.Sql);
    }

    [Fact]
    public void OpenJsonStandalonePreservesQuotedValuesAndChunkedLongPayload()
    {
        var values = string.Join(',', Enumerable.Range(0, 1500).Select(value => "N'value''" + value + "'"));
        var result = new SqlRecipeRenderer().Render(
            Definition(new("Ids", "nvarchar", 50)),
            new Dictionary<string, string> { ["Ids"] = values },
            strategy: "openjson"
        );
        Assert.Empty(result.Parameters);
        Assert.Contains("DECLARE @SchemoraInputs", result.Sql);
        Assert.Contains("SET @SchemoraInputs +=", result.Sql);
        Assert.Equal(1500, result.Statistics["Ids"].OutputCount);
        Parse(result.Sql);
    }

    [Fact]
    public void VarcharUsesDeclaredBytesAndCompatibilityGuards()
    {
        var input = new RecipeInputDefinition("Ids", "varchar", 4, "windows-1251", Collation: "Cyrillic_General_100_CI_AS");
        var result = new SqlRecipeRenderer().Render(Definition(input), new Dictionary<string, string> { ["Ids"] = "Київ" });
        Assert.Contains("CONVERT(varchar(4), N'Київ' COLLATE Cyrillic_General_100_CI_AS)", result.Sql);
        Assert.Contains("COLLATE Cyrillic_General_100_CI_AS", result.Sql);
        Parse(result.Sql);
        Assert.Equal(
            "OPENJSON_COMPATIBILITY_REQUIRED",
            Assert
                .Throws<RecipeEngineException>(() =>
                    new SqlRecipeRenderer().Render(
                        Definition(compatibility: 120),
                        new Dictionary<string, string> { ["Ids"] = "1" },
                        "parameterized"
                    )
                )
                .Code
        );
        var utf8 = new RecipeInputDefinition("Ids", "varchar", 20, "utf-8", Collation: "Latin1_General_100_BIN2_UTF8");
        Assert.Equal(
            "UTF8_COMPATIBILITY_REQUIRED",
            Assert
                .Throws<RecipeEngineException>(() =>
                    new SqlRecipeRenderer().Render(Definition(utf8), new Dictionary<string, string> { ["Ids"] = "Київ" })
                )
                .Code
        );
    }

    internal static TSqlScript Parse(string sql)
    {
        var script = new TSql170Parser(true).Parse(new StringReader(sql), out var errors);
        Assert.Empty(errors);
        return Assert.IsType<TSqlScript>(script);
    }

    private sealed class Inserts : TSqlFragmentVisitor
    {
        public List<int> Sizes { get; } = [];
        public List<int> Ordinals { get; } = [];
        public List<string> Values { get; } = [];

        public override void ExplicitVisit(InsertStatement statement)
        {
            var source = Assert.IsType<ValuesInsertSource>(statement.InsertSpecification.InsertSource);
            Sizes.Add(source.RowValues.Count);
            foreach (var row in source.RowValues)
            {
                Ordinals.Add(int.Parse(Assert.IsType<IntegerLiteral>(row.ColumnValues[0]).Value, CultureInfo.InvariantCulture));
                Values.Add(Assert.IsType<IntegerLiteral>(Assert.IsType<ConvertCall>(row.ColumnValues[1]).Parameter).Value);
            }
        }
    }
}

public sealed class RecipeTemplateTests
{
    [Theory]
    [InlineData("SELECT '{{ids:Ids}}';", "PLACEHOLDER_CONTEXT_INVALID")]
    [InlineData("SELECT 1; -- {{ids:Ids}}", "PLACEHOLDER_CONTEXT_INVALID")]
    [InlineData("SELECT {{eval:Ids}};", "PLACEHOLDER_UNKNOWN")]
    [InlineData("SELECT {{ids:Unknown}};", "PLACEHOLDER_INPUT_UNKNOWN")]
    [InlineData("SELECT {{ids:Ids};", "PLACEHOLDER_UNCLOSED")]
    public void RejectsUnknownExpressionsAndNonSqlPlaceholderContexts(string sql, string code)
    {
        Assert.Equal(
            code,
            Assert
                .Throws<RecipeEngineException>(() => RecipeTemplate.ValidateDefinition(SqlRecipeRendererTests.Definition(template: sql)))
                .Code
        );
    }

    [Fact]
    public void BindingExposesOnlyKnownInputTablesAndBrokenSqlCanStillBeSavedForRepair()
    {
        var recipe = SqlRecipeRendererTests.Definition();
        Assert.Contains("[#schemora_Ids]", RecipeTemplate.BindForValidation(recipe));
        Assert.Equal("#schemora_Ids", RecipeTemplate.InputTableName("Ids"));
        Assert.Single(RecipeTemplate.ValidateDefinition(recipe));
        var broken = recipe with { SqlTemplate = "SELECT broken FROM WHERE Id IN ({{ids:Ids}})" };
        Assert.Single(RecipeTemplate.ValidateDefinition(broken));
        Assert.Equal(
            "SQL_RENDER_PARSE_FAILED",
            Assert
                .Throws<RecipeEngineException>(() =>
                    new SqlRecipeRenderer().Render(broken, new Dictionary<string, string> { ["Ids"] = "1" })
                )
                .Code
        );
    }
}
