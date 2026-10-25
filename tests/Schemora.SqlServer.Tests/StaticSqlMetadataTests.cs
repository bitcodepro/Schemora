using Xunit;

namespace Schemora.SqlServer.Tests;

public sealed class StaticSqlMetadataTests : IDisposable
{
    private readonly string directory = Path.Combine(
        AppContext.BaseDirectory,
        "Artifacts",
        "static-metadata-" + Guid.NewGuid().ToString("N")
    );

    private SchemaGraph Read(string sql)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".sql");
        File.WriteAllText(path, sql);
        return new StaticSqlReader().Read("Metadata", [path]);
    }

    [Fact]
    public void StaticObjectKindsAndIndexesAreSeparatedFromPersistentTables()
    {
        var sql = File.ReadAllText(FindFixture());
        var graph = Read(sql);
        Assert.Equal(2, graph.Tables.Count);
        Assert.Equal(8, graph.Objects!.Count);
        Assert.Equal(6, graph.Indexes!.Count);
        Assert.All(graph.Objects, value => Assert.True(value.IsPartial));
        Assert.All(graph.Objects, value => Assert.NotNull(value.Definition));
        var items = graph.Tables.Single(value => value.Name == "Items");
        Assert.Equal("UQ_Items_Code", Assert.Single(items.UniqueKeys!).Name);
        var index = graph.Indexes.Single(value => value.Name == "IX_Items_Category");
        Assert.Equal(new[] { new SchemaIndexColumn("CategoryId", true), new SchemaIndexColumn("Code", false) }, index.Columns);
        Assert.Equal(new[] { "Payload" }, index.IncludedColumns);
        Assert.Contains("IS NOT NULL", index.Filter, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(graph.Diagnostics, value => value.Code == "SQL_STATEMENT_UNSUPPORTED");
    }

    [Fact]
    public void StaticParametersExplicitReturnColumnsAndScalarTypeRemainExact()
    {
        var graph = Read(File.ReadAllText(FindFixture()));
        var scalar = graph.Objects!.Single(value => value.Name == "FormatCode");
        Assert.Equal("nvarchar(80)", scalar.ReturnType);
        Assert.Equal(new[] { "varchar(24)", "decimal(9,2)" }, scalar.Parameters!.Select(value => value.DataType));
        Assert.Equal("nvarchar(40)", graph.Objects!.Single(value => value.Name == "ItemTokens").Columns.Single().DataType);
        Assert.True(graph.Objects!.Single(value => value.Name == "GetItems").Parameters!.Single(value => value.Name == "@total").IsOutput);
        Assert.All(
            graph.Objects!.Single(value => value.Name == "ItemOverview").Columns,
            value => Assert.Equal("unresolved", value.DataType)
        );
    }

    [Fact]
    public void QualifiedDependenciesResolveButCtesAliasesAndDataLiteralsDoNotBecomeObjects()
    {
        var graph = Read(
            """
            CREATE TABLE dbo.Items(Id int NOT NULL PRIMARY KEY,Code nvarchar(20) NULL);
            GO
            CREATE VIEW dbo.vItems AS WITH picked AS (SELECT i.Id FROM dbo.Items i) SELECT p.Id FROM picked p;
            GO
            CREATE PROCEDURE dbo.ReadItems AS BEGIN SELECT i.Id FROM dbo.Items i; EXEC(N'SELECT * FROM dbo.NotARealDependency'); END;
            GO
            INSERT INTO dbo.Items VALUES(1,N'CREATE VIEW dbo.Fake AS SELECT 1');
            """
        );
        Assert.Equal(2, graph.Objects!.Count);
        Assert.Equal(2, graph.ObjectDependencies!.Count);
        Assert.All(graph.ObjectDependencies, value => Assert.Equal("[Metadata].[dbo].[Items]", value.ToObjectId));
        Assert.DoesNotContain(
            graph.ObjectDependencies,
            value =>
                value.TargetName.Contains("picked", StringComparison.OrdinalIgnoreCase)
                || value.TargetName.Contains("NotAReal", StringComparison.Ordinal)
        );
        Assert.Contains(graph.Diagnostics, value => value.Code == "DYNAMIC_OBJECT_DEPENDENCIES_UNAVAILABLE");
    }

    [Fact]
    public void ExternalAndUnqualifiedSynonymsKeepUnboundEvidence()
    {
        var graph = Read(
            """
            CREATE TABLE dbo.Items(Id int NOT NULL);
            GO
            CREATE SYNONYM dbo.RemoteItems FOR RemoteDb.dbo.Items;
            GO
            CREATE VIEW dbo.vItems AS SELECT Id FROM Items;
            """
        );
        Assert.Equal("[RemoteDb].[dbo].[Items]", graph.Objects!.Single(value => value.Name == "RemoteItems").Target);
        Assert.All(graph.ObjectDependencies!, value => Assert.Null(value.ToObjectId));
        Assert.Contains(graph.Diagnostics, value => value.Code == "OBJECT_DEPENDENCY_UNRESOLVED");
    }

    [Fact]
    public void ConflictingObjectKindsExcludeBothRatherThanInventingAValidTable()
    {
        var graph = Read("CREATE TABLE dbo.Clash(Id int NOT NULL); GO\nCREATE VIEW dbo.Clash AS SELECT 1 AS Id;".Replace("; GO", ";\nGO"));
        Assert.Empty(graph.Tables);
        Assert.Empty(graph.Objects!);
        Assert.Contains(graph.Diagnostics, value => value.Code == "OBJECT_DEFINITION_CONFLICT");
    }

    [Fact]
    public void OversizedDefinitionIsOmittedWithoutTruncatingSql()
    {
        var graph = Read(
            "CREATE PROCEDURE dbo.Big AS SELECT N'" + new string('Ж', SchemaMetadataLimits.MaximumDefinitionBytes / 2 + 1) + "';"
        );
        var value = Assert.Single(graph.Objects!);
        Assert.Null(value.Definition);
        Assert.True(value.IsPartial);
        Assert.Contains(graph.Diagnostics, value => value.Code == "OBJECT_DEFINITION_LIMIT");
    }

    [Fact]
    public void ThreeConflictingIndexAndUniqueDefinitionsNeverReintroduceTheLastState()
    {
        var graph = Read(
            """
            CREATE TABLE dbo.Items(Id int NOT NULL,Code int NULL,CONSTRAINT UQ_Items UNIQUE(Code));
            GO
            ALTER TABLE dbo.Items ADD CONSTRAINT UQ_Items UNIQUE(Id);
            GO
            ALTER TABLE dbo.Items ADD CONSTRAINT UQ_Items UNIQUE(Code);
            GO
            CREATE INDEX IX_Items ON dbo.Items(Id);
            GO
            CREATE INDEX IX_Items ON dbo.Items(Code);
            GO
            CREATE INDEX IX_Items ON dbo.Items(Id);
            """
        );
        Assert.Empty(Assert.Single(graph.Tables).UniqueKeys!);
        Assert.Empty(graph.Indexes!);
        Assert.Contains(graph.Diagnostics, value => value.Code == "UNIQUE_KEY_DEFINITION_CONFLICT");
        Assert.Contains(graph.Diagnostics, value => value.Code == "INDEX_DEFINITION_CONFLICT");
    }

    private static string FindFixture()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "tests", "Fixtures", "Metadata", "Schema.sql");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException("Synthetic Metadata fixture not found.");
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}
