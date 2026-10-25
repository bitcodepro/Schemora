using System.Text.Json;
using Schemora.Relationships;
using Schemora.SqlServer;
using Xunit;

namespace Schemora.Relationships.Tests;

public sealed class DapperSqlTests
{
    [Fact]
    public void SelectedProjectSymbolsChangeSqlEvidence()
    {
        using var source = new Source(
            "public class Repo { public void Read(dynamic connection) {\n#if SELECTED\nconnection.Query(\"SELECT o.Id,c.Id FROM dbo.Orders o JOIN dbo.Customers c ON o.CustomerId=c.Id\");\n#endif\n} }"
        );
        var selection = new CSharpProjectSelection("selection.csproj");
        var enabled = new CSharpProjectContext(selection, [new(source.Path, ["SELECTED"])], [], [], true, "enabled");
        var disabled = enabled with { Files = [new(source.Path, [])], Fingerprint = "disabled" };
        var analyzer = new DapperSqlAnalyzer();
        Assert.Single(analyzer.Analyze([Database()], [new("db", [], enabled)]).Relations);
        Assert.Empty(analyzer.Analyze([Database()], [new("db", [], disabled)]).Relations);
    }

    [Fact]
    public void ConfigurationReportsDynamicSqlWhilePreservingStaticEvidence()
    {
        using var source = new Source(
            Code(
                "\n#if DEBUG\nconnection.Query(\""
                    + Sql
                    + "\");\n#else\nconnection.Query(\"SELECT 1\");\n#endif\nconnection.Query(BuildSql());"
            )
        );
        var project = new CSharpProjectContext(
            new("Synthetic.csproj", "Debug", "net10.0"),
            [new(source.Path, ["DEBUG"])],
            [],
            [],
            true,
            "debug"
        );
        var debug = new DapperSqlAnalyzer().Analyze([Database()], [new("db", [], project)]);
        Assert.Single(debug.Relations);
        Assert.Contains(debug.Diagnostics, d => d.Code == "DAPPER_SQL_DYNAMIC");
        var release = new DapperSqlAnalyzer().Analyze(
            [Database()],
            [new("db", [], project with { Files = [new(source.Path, [])], Fingerprint = "release" })]
        );
        Assert.Empty(release.Relations);
        Assert.Contains(release.Diagnostics, d => d.Code == "DAPPER_SQL_DYNAMIC");
    }

    private const string Sql = "SELECT o.Id,c.Id FROM dbo.Orders o JOIN dbo.Customers c ON o.CustomerId=c.Id";

    private static string Code(string statements) =>
        "using Dapper;\npublic class Repo {\n public void Read(dynamic connection) {\n" + statements + ";\n }\n}";

    [Theory]
    [InlineData("Query")]
    [InlineData("QueryAsync")]
    [InlineData("QueryFirstOrDefaultAsync")]
    [InlineData("QuerySingle")]
    [InlineData("QueryMultipleAsync")]
    [InlineData("Execute")]
    [InlineData("ExecuteAsync")]
    public void ReadsOrdinaryAsyncAndExecuteCallsAsUntrustedEvidence(string method)
    {
        using var source = new Source(Code($"connection.{method}(\"{Sql}\", new {{ Id=987654321 }})"));
        var result = Analyze(source);
        var edge = Assert.Single(result.Relations);
        Assert.False(edge.IsTrusted);
        Assert.Equal("embedded-sql-candidate", edge.Relation.Kind);
        Assert.Equal("candidate", edge.Status);
        Assert.All(
            edge.Evidence,
            e =>
            {
                Assert.Equal("dapper-sql", e.Kind);
                Assert.Equal(source.Path, e.SourcePath);
                Assert.Equal(4, e.Line);
            }
        );
        Assert.DoesNotContain("987654321", JsonSerializer.Serialize(result));
        Assert.Contains(result.Diagnostics, d => d.Code == "DAPPER_SYMBOL_UNRESOLVED");
    }

    [Fact]
    public void ReadsVerbatimRawConstAndConstantConcatenationWithoutSavingValues()
    {
        var verbatim = "connection.Query(@\"SELECT o.Id,c.Id FROM dbo.Orders o\nJOIN dbo.Customers c ON o.CustomerId=c.Id\")";
        using var first = new Source(Code(verbatim));
        Assert.Equal(5, Assert.Single(Analyze(first).Relations).Evidence.Single().Line);
        var raw = "connection.Query(\"\"\"\nSELECT o.Id,c.Id FROM dbo.Orders o\nJOIN dbo.Customers c ON o.CustomerId=c.Id\n\"\"\")";
        using var second = new Source(Code(raw));
        Assert.Equal(6, Assert.Single(Analyze(second).Relations).Evidence.Single().Line);
        using var third = new Source(
            Code(
                "const string Prefix=\"SELECT o.Id,c.Id FROM dbo.Orders o \"; const string QueryText=Prefix+\"JOIN dbo.Customers c ON o.CustomerId=c.Id WHERE o.Id=123456789\"; connection.Query(QueryText)"
            )
        );
        var result = Analyze(third);
        Assert.Single(result.Relations);
        Assert.DoesNotContain("123456789", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void ReadsNamedStaticMapperAndInlineCommandDefinition()
    {
        using var source = new Source(
            Code(
                $"SqlMapper.Query<object>(connection, sql: \"{Sql}\", param: new {{ Id=888888 }}) ; connection.QueryAsync(new Dapper.CommandDefinition(commandText: \"{Sql}\", parameters: new {{ Id=999999 }}))"
            )
        );
        var result = Analyze(source);
        var edge = Assert.Single(result.Relations);
        Assert.Equal(2, edge.Evidence.Count);
        Assert.DoesNotContain("888888", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("999999", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void MultiMappingSplitOnIsNotRelationshipEvidence()
    {
        using var noJoin = new Source(
            Code(
                "connection.Query<Order,Customer,Order>(\"SELECT o.*,c.* FROM dbo.Orders o CROSS JOIN dbo.Customers c\", (o,c)=>o, splitOn: \"CustomerId\")"
            )
        );
        Assert.Empty(Analyze(noJoin).Relations);
        using var join = new Source(Code($"connection.Query<Order,Customer,Order>(\"{Sql}\", (o,c)=>o, splitOn: \"Id\")"));
        var edge = Assert.Single(Analyze(join).Relations);
        Assert.False(edge.IsTrusted);
        Assert.All(edge.Evidence, e => Assert.Contains("splitOn does not establish", e.Detail));
    }

    [Fact]
    public void RejectsInterpolationVariablesAndSqlBuildersWithoutGuessing()
    {
        using var source = new Source(
            Code(
                "var table=\"Orders\"; var sql=\"SELECT 1\"; connection.Query($\"SELECT * FROM {table}\"); connection.Query(sql); connection.Query(BuildSql());"
            )
        );
        var result = Analyze(source);
        Assert.Empty(result.Relations);
        Assert.Equal(3, result.Diagnostics.Count(d => d.Code == "DAPPER_SQL_DYNAMIC"));
    }

    [Fact]
    public void BindsOnlyExplicitDatabaseAndDoesNotSearchAnotherDatabase()
    {
        using var source = new Source(Code($"connection.Query(\"{Sql}\")"));
        var analyzer = new DapperSqlAnalyzer();
        var result = analyzer.Analyze([Database("a"), Database("b")], [new("b", [source.Path])]);
        var edge = Assert.Single(result.Relations);
        Assert.True(edge.Relation.FromTableId.StartsWith("b:") && edge.Relation.ToTableId.StartsWith("b:"));
        using var cross = new Source(
            Code("connection.Query(\"SELECT o.Id,c.Id FROM Other.dbo.Orders o JOIN Other.dbo.Customers c ON o.CustomerId=c.Id\")")
        );
        Assert.Empty(analyzer.Analyze([Database("a"), Database("b") with { Name = "Other" }], [new("a", [cross.Path])]).Relations);
        Assert.Contains(
            analyzer.Analyze([Database()], [new("missing", [source.Path])]).Diagnostics,
            d => d.Code == "DAPPER_DATABASE_UNASSIGNED"
        );
    }

    [Fact]
    public void AmbiguousColumnsAndMissingSchemasDoNotFabricateJoins()
    {
        using var ambiguous = new Source(Code("connection.Query(\"SELECT * FROM dbo.Orders o JOIN dbo.Customers c ON Id=c.Id\")"));
        var result = Analyze(ambiguous);
        Assert.Empty(result.Relations);
        Assert.Contains(result.Diagnostics, d => d.Code == "JOIN_COLUMN_AMBIGUOUS");
        using var missingSchema = new Source(Code("connection.Query(\"SELECT * FROM Orders o JOIN Customers c ON o.CustomerId=c.Id\")"));
        Assert.Empty(Analyze(missingSchema).Relations);
    }

    [Fact]
    public void SpoofedQueryMethodsStayUntrustedAndContribAttributesDoNotCreateForeignKeys()
    {
        using var source = new Source(
            "class Fake { void Query(string sql){} void Read() {Query(\""
                + Sql
                + "\");} } namespace Dapper.Contrib.Extensions {class TableAttribute:System.Attribute {public TableAttribute(string x){}} class KeyAttribute:System.Attribute {}} [Dapper.Contrib.Extensions.Table(\"Orders\")] class Order {[Dapper.Contrib.Extensions.Key] public int Id {get;set;} public int CustomerId {get;set;}}"
        );
        var result = Analyze(source);
        Assert.All(result.Relations, e => Assert.False(e.IsTrusted));
        Assert.Single(result.Relations);
        var graph = new RelationshipAnalyzer().Analyze([Database()], [new("db", [source.Path])]);
        Assert.DoesNotContain(graph.Relations, e => e.IsTrusted);
        Assert.DoesNotContain(graph.Relations, e => e.Relation.Kind == "orm-mapping");
    }

    [Fact]
    public void ParserDiagnosticsAndEvidenceNeverRetainSqlLiteralOrParameterValues()
    {
        const string secret = "PRIVATE_ID_9b72a06d";
        using var source = new Source(
            Code(
                $"connection.Query(\"{Sql} WHERE o.Id='{{secret}}'\",new {{ Value=\"{secret}\" }}); connection.Query(\"SELECT [ {secret}\")".Replace(
                    "{secret}",
                    secret
                )
            )
        );
        var result = Analyze(source);
        Assert.Single(result.Relations);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(secret, serialized);
        Assert.Contains(result.Diagnostics, d => d.Code == "JOIN_SQL_PARSE_ERROR");
    }

    [Fact]
    public void RejectsNetworkSourcesAndHonorsCancellation()
    {
        var remote = new DapperSqlAnalyzer().Analyze([Database()], [new("db", [@"\\not-contacted\share\source.cs"])]);
        Assert.Contains(remote.Diagnostics, d => d.Code == "DAPPER_LOCAL_SOURCE_REQUIRED");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            new DapperSqlAnalyzer().Analyze([Database()], [new("db", [])], cancellation.Token)
        );
    }

    private static DapperSqlAnalysisResult Analyze(Source source) =>
        new DapperSqlAnalyzer().Analyze([Database()], [new("db", [source.Path])]);

    private static RelationshipDatabase Database(string id = "db") =>
        new(
            id,
            "Commerce",
            new(
                "Commerce",
                "synthetic",
                DateTimeOffset.UtcNow,
                [
                    new(id + ":orders", "dbo", "Orders", [new("Id", "int", false), new("CustomerId", "int", false)], ["Id"]),
                    new(id + ":customers", "dbo", "Customers", [new("Id", "int", false)], ["Id"]),
                ],
                [],
                []
            )
        );

    private sealed class Source : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(AppContext.BaseDirectory, "dapper-test-" + Guid.NewGuid().ToString("N") + ".cs");

        public Source(string value) => File.WriteAllText(Path, value);

        public void Dispose() => File.Delete(Path);
    }
}
