using Xunit;

namespace Schemora.SqlServer.Tests;

public sealed class StaticSqlReaderTests : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "StaticSqlArtifacts", Guid.NewGuid().ToString("N"));

    public StaticSqlReaderTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData("-- CREATE TABLE fake(Id int)\nSELECT N'CREATE TABLE fake(Id int)';", "querySql")]
    [InlineData("-- schema words are just comments\nINSERT INTO dbo.Items(Id) VALUES(1);", "dataSql")]
    [InlineData("CREATE TABLE dbo.Items(Id int NOT NULL PRIMARY KEY);", "schemaSql")]
    [InlineData("ALTER TABLE dbo.Items ADD ExternalId nvarchar(80) NULL;", "migrationSql")]
    [InlineData("CREATE TABLE dbo.Items(Id int); INSERT INTO dbo.Items(Id) VALUES(1);", "migrationSql")]
    [InlineData("/* only a comment with CREATE TABLE */", "unknownSql")]
    [InlineData("CREATE TABLE dbo.Broken(Id int NOT A REAL TYPE);", "unknownSql")]
    public void ClassifiesRealStatementsRatherThanCommentsOrLiteralContents(string sql, string expected)
    {
        var path = Source(sql);
        Assert.Equal(expected, new SqlSourceClassifier().Classify(path));
    }

    [Fact]
    public void ReadsGoQuotedNamesTypesAndPrimaryKeyNullability()
    {
        var graph = Read(
            """
            -- Delimiters, comments and GO must be parsed as T-SQL.
            CREATE SCHEMA [sales area];
            GO
            CREATE TABLE [sales area].[Order]]Lines] (
                [Tenant] int NOT NULL,
                [Line Id] bigint,
                [Name] nvarchar(120) NULL,
                [Body] nvarchar(max) NULL,
                [Amount] decimal(19,4) NOT NULL,
                [Created] datetime2(3) NOT NULL,
                [Key] [dbo].[ExternalKey] NULL,
                CONSTRAINT [PK_Order Lines] PRIMARY KEY ([Line Id], [Tenant])
            );
            GO
            CREATE TABLE "dbo"."Quoted" ("Id" int PRIMARY KEY, "Payload" varbinary(32) NULL);
            """
        );
        Assert.Equal(2, graph.Tables.Count);
        var table = graph.Tables.Single(table => table.Schema == "sales area");
        Assert.Equal("Order]Lines", table.Name);
        Assert.Equal("[StaticDb].[sales area].[Order]]Lines]", table.Id);
        Assert.Equal(new[] { "Line Id", "Tenant" }, table.PrimaryKey);
        Assert.False(table.Columns.Single(column => column.Name == "Line Id").IsNullable);
        Assert.Equal("nvarchar(120)", table.Columns.Single(column => column.Name == "Name").DataType);
        Assert.Equal("nvarchar(max)", table.Columns.Single(column => column.Name == "Body").DataType);
        Assert.Equal("decimal(19,4)", table.Columns.Single(column => column.Name == "Amount").DataType);
        Assert.Equal("datetime2(3)", table.Columns.Single(column => column.Name == "Created").DataType);
        Assert.Equal("[dbo].[ExternalKey]", table.Columns.Single(column => column.Name == "Key").DataType);
        Assert.Equal(
            "varbinary(32)",
            graph.Tables.Single(table => table.Name == "Quoted").Columns.Single(column => column.Name == "Payload").DataType
        );
        Assert.Contains(graph.Diagnostics, diagnostic => diagnostic.Code == "STATIC_ANALYSIS_PARTIAL");
    }

    [Fact]
    public void ResolvesInlineAndAlterForeignKeysAcrossFilesWithCompositeOrder()
    {
        var alter = Source(
            """
            ALTER TABLE sales.Child ADD CONSTRAINT FK_Child_Parent
                FOREIGN KEY (ParentB, ParentA) REFERENCES dbo.Parent (B, A);
            ALTER TABLE sales.Child ADD ExternalId varchar(40) NULL;
            """
        );
        var definitions = Source(
            """
            CREATE TABLE dbo.Parent (A int NOT NULL, B int NOT NULL, PRIMARY KEY(B,A));
            CREATE TABLE dbo.Tag (Id int PRIMARY KEY);
            CREATE TABLE sales.Child (
                Id int NOT NULL PRIMARY KEY,
                ParentA int NULL, ParentB int NULL,
                TagId int NULL REFERENCES dbo.Tag(Id)
            );
            """
        );
        var graph = new StaticSqlReader().Read("StaticDb", [alter, definitions]);
        Assert.Equal(3, graph.Tables.Count);
        Assert.Equal(2, graph.Relations.Count);
        var composite = graph.Relations.Single(relation => relation.Name == "FK_Child_Parent");
        Assert.Equal(new[] { "ParentB", "ParentA" }, composite.FromColumns);
        Assert.Equal(new[] { "B", "A" }, composite.ToColumns);
        Assert.Equal("[StaticDb].[sales].[Child]", composite.FromTableId);
        Assert.Equal("[StaticDb].[dbo].[Parent]", composite.ToTableId);
        Assert.All(graph.Relations, relation => Assert.Equal("declared-fk", relation.Kind));
        Assert.Equal(
            "varchar(40)",
            graph.Tables.Single(table => table.Name == "Child").Columns.Single(column => column.Name == "ExternalId").DataType
        );
        Assert.DoesNotContain(graph.Diagnostics, diagnostic => diagnostic.Severity == "error");
    }

    [Fact]
    public void ReferenceWithoutExplicitTargetColumnsUsesDeclaredPrimaryKey()
    {
        var graph = Read(
            """
            CREATE TABLE dbo.Parent(A int NOT NULL, B int NOT NULL, PRIMARY KEY (B,A));
            CREATE TABLE dbo.Child(Id int PRIMARY KEY, X int NULL, Y int NULL,
                FOREIGN KEY (X,Y) REFERENCES dbo.Parent);
            """
        );
        Assert.Equal(new[] { "B", "A" }, Assert.Single(graph.Relations).ToColumns);
    }

    [Fact]
    public void DoesNotIndexDataQueriesConditionalTablesOrProcedureBodies()
    {
        var graph = Read(
            """
            CREATE TABLE dbo.Real(Id int PRIMARY KEY);
            GO
            INSERT INTO dbo.Real(Id) VALUES(1);
            SELECT 'CREATE TABLE dbo.Fake(Id int)' AS TextOnly;
            IF 1 = 0 BEGIN CREATE TABLE dbo.Conditional(Id int); END;
            GO
            CREATE PROCEDURE dbo.MakeTable AS BEGIN CREATE TABLE dbo.RuntimeOnly(Id int); END;
            """
        );
        Assert.Equal("Real", Assert.Single(graph.Tables).Name);
        Assert.Empty(graph.Relations);
        Assert.Contains(graph.Diagnostics, diagnostic => diagnostic.Code == "NON_SCHEMA_STATEMENT_IGNORED");
        Assert.Contains(graph.Diagnostics, diagnostic => diagnostic.Code == "SQL_STATEMENT_UNSUPPORTED");
    }

    [Fact]
    public void ParseFailureSkipsOnlyMalformedFileAndReportsLineLocation()
    {
        var malformed = Source("CREATE TABLE dbo.Broken(Id int NOT A REAL TYPE);");
        var valid = Source("CREATE TABLE dbo.Valid(Id int NOT NULL PRIMARY KEY);");
        var graph = new StaticSqlReader().Read("StaticDb", [malformed, valid]);
        Assert.Equal("Valid", Assert.Single(graph.Tables).Name);
        Assert.Contains(
            graph.Diagnostics,
            diagnostic =>
                diagnostic.Code == "SQL_PARSE_ERROR"
                && diagnostic.Message.Contains(malformed, StringComparison.Ordinal)
                && diagnostic.Message.Contains("(1,", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void DoesNotMergeSchemasOrDatabasesAndDiagnosesExternalDeclarations()
    {
        var graph = Read(
            """
            CREATE TABLE [StaticDb].[dbo].[Same] (Id int PRIMARY KEY);
            CREATE TABLE [sales].[Same] (Id int PRIMARY KEY);
            CREATE TABLE [OtherDb].[dbo].[Same] (Id int PRIMARY KEY);
            GO
            USE [OtherDb];
            GO
            CREATE TABLE dbo.AlsoExternal(Id int PRIMARY KEY);
            GO
            USE [StaticDb];
            GO
            CREATE TABLE dbo.BackHere(Id int PRIMARY KEY);
            """
        );
        Assert.Equal(3, graph.Tables.Count);
        Assert.Equal(2, graph.Tables.Count(table => table.Name == "Same"));
        Assert.DoesNotContain(graph.Tables, table => table.Name == "AlsoExternal");
        Assert.Contains(graph.Diagnostics, diagnostic => diagnostic.Code == "EXTERNAL_DATABASE_REFERENCE");
        Assert.Contains(graph.Diagnostics, diagnostic => diagnostic.Code == "DATABASE_CONTEXT_SKIPPED");
    }

    [Fact]
    public void UnresolvedForeignKeysNeverProduceInventedEndpoints()
    {
        var graph = Read(
            """
            CREATE TABLE dbo.Child(Id int PRIMARY KEY, ParentId int NULL,
                FOREIGN KEY(ParentId) REFERENCES dbo.Missing(Id));
            """
        );
        Assert.Single(graph.Tables);
        Assert.Empty(graph.Relations);
        Assert.Contains(graph.Diagnostics, diagnostic => diagnostic.Code == "FOREIGN_KEY_TARGET_UNRESOLVED");
    }

    [Fact]
    public void DuplicateDefinitionsAreExcludedRatherThanSilentlyOverwritten()
    {
        var first = Source("CREATE TABLE dbo.Same(Id int PRIMARY KEY);");
        var second = Source("CREATE TABLE dbo.Same(OtherId bigint PRIMARY KEY);");
        var graph = new StaticSqlReader().Read("StaticDb", [first, second]);
        Assert.Empty(graph.Tables);
        Assert.Contains(graph.Diagnostics, diagnostic => diagnostic.Code == "TABLE_DEFINITION_CONFLICT");
    }

    [Fact]
    public void UnknownComputedTypesAndNullabilityHaveExplicitDiagnostics()
    {
        var graph = Read("CREATE TABLE dbo.Items(Id int IDENTITY PRIMARY KEY, Maybe varchar(20), Computed AS (Id + 1));");
        var table = Assert.Single(graph.Tables);
        Assert.False(table.Columns.Single(column => column.Name == "Id").IsNullable);
        Assert.True(table.Columns.Single(column => column.Name == "Maybe").IsNullable);
        Assert.Equal("unresolved", table.Columns.Single(column => column.Name == "Computed").DataType);
        Assert.Contains(graph.Diagnostics, diagnostic => diagnostic.Code == "COLUMN_TYPE_UNRESOLVED");
        Assert.Contains(graph.Diagnostics, diagnostic => diagnostic.Code == "COLUMN_NULLABILITY_ASSUMED");
    }

    [Fact]
    public void UnnamedForeignKeyIdentitiesStayStableAcrossInputOrder()
    {
        var parent = Source("CREATE TABLE dbo.Parent(Id int PRIMARY KEY);");
        var child = Source("CREATE TABLE dbo.Child(Id int PRIMARY KEY, ParentId int NULL REFERENCES dbo.Parent(Id));");
        var reader = new StaticSqlReader();
        var first = reader.Read("StaticDb", [parent, child]);
        var second = reader.Read("StaticDb", [child, parent]);
        Assert.Equal(Assert.Single(first.Relations).Id, Assert.Single(second.Relations).Id);
    }

    [Fact]
    public void MissingAndOversizedFilesReturnDiagnosticsWithoutLosingOtherFiles()
    {
        var missing = Path.Combine(directory, "missing.sql");
        var oversized = Source("-- oversized file");
        using (var stream = new FileStream(oversized, FileMode.Open, FileAccess.Write))
            stream.SetLength(16L * 1024 * 1024 + 1);
        var valid = Source("CREATE TABLE dbo.Valid(Id int PRIMARY KEY);");
        var graph = new StaticSqlReader().Read("StaticDb", [missing, oversized, valid]);
        Assert.Single(graph.Tables);
        Assert.Equal(2, graph.Diagnostics.Count(diagnostic => diagnostic.Code == "SQL_FILE_UNREADABLE"));
        Assert.Equal("unknownSql", new SqlSourceClassifier().Classify(missing));
    }

    [Fact]
    public void OmittedSchemasAreNeverGuessedIntoConfirmedForeignKeyEndpoints()
    {
        var graph = Read(
            """
            CREATE TABLE dbo.Parent(Id int PRIMARY KEY);
            CREATE TABLE sales.Parent(Id int PRIMARY KEY);
            CREATE TABLE dbo.Child(Id int PRIMARY KEY, ParentId int NULL REFERENCES Parent(Id));
            CREATE TABLE UnqualifiedChild(Id int PRIMARY KEY, ParentId int NULL REFERENCES dbo.Parent(Id));
            """
        );
        Assert.Equal(4, graph.Tables.Count);
        Assert.Empty(graph.Relations);
        Assert.Contains(graph.Diagnostics, diagnostic => diagnostic.Code == "DEFAULT_SCHEMA_ASSUMED");
        Assert.Equal(2, graph.Diagnostics.Count(diagnostic => diagnostic.Code == "FOREIGN_KEY_SCHEMA_UNRESOLVED"));
    }

    [Fact]
    public void SchemaAuthorizationFormCanContainTableDefinitions()
    {
        var graph = Read("CREATE SCHEMA AUTHORIZATION dbo CREATE TABLE dbo.Contained(Id int NOT NULL PRIMARY KEY);");
        Assert.Equal("Contained", Assert.Single(graph.Tables).Name);
        Assert.DoesNotContain(graph.Diagnostics, diagnostic => diagnostic.Severity == "error");
    }

    private SchemaGraph Read(string sql) => new StaticSqlReader().Read("StaticDb", [Source(sql)]);

    private string Source(string sql)
    {
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".sql");
        File.WriteAllText(path, sql);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
