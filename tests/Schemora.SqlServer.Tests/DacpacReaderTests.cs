using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using Xunit;

namespace Schemora.SqlServer.Tests;

public sealed class DacpacReaderTests : IClassFixture<DacpacFixture>
{
    private readonly DacpacFixture _fixture;

    public DacpacReaderTests(DacpacFixture fixture) => _fixture = fixture;

    [Fact]
    public void ReadsTablesColumnsAndCompositeKeysWithoutMixingSchemas()
    {
        var graph = new DacpacReader().Read(_fixture.CommercePath);
        Assert.Equal("Commerce", graph.DatabaseName);
        Assert.Equal(4, graph.Tables.Count);
        var customers = Assert.Single(graph.Tables, table => table.Schema == "dbo" && table.Name == "Customers");
        Assert.Equal(new[] { "TenantId", "CustomerId" }, customers.PrimaryKey);
        var externalId = Assert.Single(customers.Columns, column => column.Name == "ExternalId");
        Assert.Equal("nvarchar(80)", externalId.DataType);
        Assert.True(externalId.IsNullable);
        Assert.False(customers.Columns.Single(column => column.Name == "TenantId").IsNullable);
        var salesCustomers = Assert.Single(graph.Tables, table => table.Schema == "sales" && table.Name == "Customers");
        Assert.NotEqual(customers.Id, salesCustomers.Id);
        var total = graph.Tables.Single(table => table.Name == "Orders").Columns.Single(column => column.Name == "Total");
        Assert.Equal("decimal(18,2)", total.DataType);
        Assert.Empty(graph.Diagnostics);
    }

    [Fact]
    public void PreservesForeignKeyColumnPairsInDeclaredOrder()
    {
        var graph = new DacpacReader().Read(_fixture.CommercePath);
        var relation = Assert.Single(graph.Relations);
        Assert.Equal("FK_Orders_Customers", relation.Name);
        Assert.Equal("[Commerce].[sales].[Orders]", relation.FromTableId);
        Assert.Equal("[Commerce].[dbo].[Customers]", relation.ToTableId);
        Assert.Equal(new[] { "TenantId", "CustomerId" }, relation.FromColumns);
        Assert.Equal(new[] { "TenantId", "CustomerId" }, relation.ToColumns);
        Assert.Equal("declared-fk", relation.Kind);
        Assert.Equal(1.0, relation.Confidence);
        Assert.DoesNotContain(graph.Relations, item => item.FromTableId.EndsWith("[Documents]", StringComparison.Ordinal));
    }

    [Fact]
    public void DatabaseNameSeparatesOtherwiseIdenticalTableNames()
    {
        var commerce = new DacpacReader().Read(_fixture.CommercePath);
        var billing = new DacpacReader().Read(_fixture.BillingPath);
        var commerceId = commerce.Tables.Single(table => table.Schema == "dbo" && table.Name == "Customers").Id;
        var billingId = billing.Tables.Single(table => table.Schema == "dbo" && table.Name == "Customers").Id;
        Assert.NotEqual(commerceId, billingId);
        Assert.Equal("[Billing].[dbo].[Customers]", billingId);
    }

    [Fact]
    public void ExtractedObjectIdentitiesAreStableAcrossReads()
    {
        var reader = new DacpacReader();
        var first = reader.Read(_fixture.CommercePath);
        var second = reader.Read(_fixture.CommercePath);
        Assert.Equal(first.Tables.Select(table => table.Id), second.Tables.Select(table => table.Id));
        Assert.Equal(first.Relations.Select(relation => relation.Id), second.Relations.Select(relation => relation.Id));
        Assert.Equal(first.Tables.SelectMany(table => table.Columns), second.Tables.SelectMany(table => table.Columns));
    }

    [Fact]
    public void CorruptDacpacHasActionableFailureInsteadOfSuccessfulEmptyGraph()
    {
        var error = Assert.Throws<DacpacReadException>(() => new DacpacReader().Read(_fixture.CorruptPath));
        Assert.Contains("valid SQL schema package", error.Message);
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public void MissingAndWrongExtensionFilesAreRejected()
    {
        Assert.Throws<FileNotFoundException>(() => new DacpacReader().Read(Path.Combine(_fixture.OutputDirectory, "missing.dacpac")));
        Assert.Throws<ArgumentException>(() => new DacpacReader().Read(_fixture.PlainSqlPath));
    }

    [Fact]
    public void PreservesLengthMaxPrecisionAndAliasTypeMetadata()
    {
        var graph = new DacpacReader().Read(_fixture.TypePath);
        var table = Assert.Single(graph.Tables);
        Assert.Equal("nvarchar(max)", table.Columns.Single(column => column.Name == "Body").DataType);
        Assert.Equal("varbinary(16)", table.Columns.Single(column => column.Name == "Token").DataType);
        Assert.Equal("datetime2(4)", table.Columns.Single(column => column.Name == "Created").DataType);
        Assert.Equal("[dbo].[ExternalKey]", table.Columns.Single(column => column.Name == "ExternalId").DataType);
    }
}

public sealed class DacpacFixture : IDisposable
{
    public string OutputDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Artifacts", Guid.NewGuid().ToString("N"));
    public string CommercePath => Path.Combine(OutputDirectory, "Commerce.dacpac");
    public string BillingPath => Path.Combine(OutputDirectory, "Billing.dacpac");
    public string CorruptPath => Path.Combine(OutputDirectory, "Corrupt.dacpac");
    public string PlainSqlPath => Path.Combine(OutputDirectory, "schema.sql");
    public string TypePath => Path.Combine(OutputDirectory, "Types.dacpac");

    public DacpacFixture()
    {
        Directory.CreateDirectory(OutputDirectory);
        var fixtures = FindFixtureDirectory();
        CreatePackage(CommercePath, "Commerce", File.ReadAllText(Path.Combine(fixtures, "Commerce", "Schema.sql")));
        CreatePackage(BillingPath, "Billing", File.ReadAllText(Path.Combine(fixtures, "Billing", "Schema.sql")));
        CreatePackage(
            TypePath,
            "Types",
            """
            CREATE TYPE [dbo].[ExternalKey] FROM nvarchar(64) NULL;
            GO
            CREATE TABLE [dbo].[TypedItems] (
                [Id] int NOT NULL PRIMARY KEY,
                [Body] nvarchar(max) NULL,
                [Token] varbinary(16) NULL,
                [Created] datetime2(4) NOT NULL,
                [ExternalId] [dbo].[ExternalKey] NULL
            );
            GO
            """
        );
        File.WriteAllText(CorruptPath, "This is not a DACPAC archive.");
        File.WriteAllText(PlainSqlPath, "CREATE TABLE dbo.Test (Id int);");
    }

    private static void CreatePackage(string path, string name, string sql)
    {
        using var model = new TSqlModel(SqlServerVersion.Sql160, new TSqlModelOptions());
        model.AddObjects(sql);
        DacPackageExtensions.BuildPackage(path, model, new PackageMetadata { Name = name, Version = "1.0.0" });
    }

    private static string FindFixtureDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "tests", "Fixtures");
            if (Directory.Exists(path))
                return path;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Run tests from the Schemora source checkout; tests/Fixtures was not found.");
    }

    public void Dispose()
    {
        if (Directory.Exists(OutputDirectory))
            Directory.Delete(OutputDirectory, recursive: true);
    }
}
