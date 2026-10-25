using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using Xunit;

namespace Schemora.SqlServer.Tests;

public sealed class ExtendedDacpacMetadataTests : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "Artifacts", "metadata-" + Guid.NewGuid().ToString("N"));
    private readonly string package;

    public ExtendedDacpacMetadataTests()
    {
        Directory.CreateDirectory(directory);
        package = Path.Combine(directory, "Metadata.dacpac");
        using var model = new TSqlModel(SqlServerVersion.Sql160, new TSqlModelOptions());
        model.AddObjects(File.ReadAllText(FindFixture()));
        DacPackageExtensions.BuildPackage(package, model, new PackageMetadata { Name = "Metadata", Version = "1.0" });
    }

    [Fact]
    public void ReadsEveryObjectKindWithoutAddingViewsOrRoutinesToTables()
    {
        var graph = new DacpacReader().Read(package);
        Assert.Equal(2, graph.Tables.Count);
        Assert.NotNull(graph.Objects);
        Assert.Equal(8, graph.Objects.Count);
        Assert.Equal(3, graph.Objects.Count(value => value.Kind == "view"));
        Assert.Equal(2, graph.Objects.Count(value => value.Kind == "tableFunction"));
        Assert.Single(graph.Objects, value => value.Kind == "procedure");
        Assert.Single(graph.Objects, value => value.Kind == "scalarFunction");
        Assert.Equal("[dbo].[Items]", Assert.Single(graph.Objects, value => value.Kind == "synonym").Target);
        Assert.DoesNotContain(graph.Tables, table => table.Name == "ItemOverview");
        Assert.All(graph.Objects, value => Assert.NotNull(value.Definition));
    }

    [Fact]
    public void RetainsParameterShapesAndExactScalarReturnType()
    {
        var objects = new DacpacReader().Read(package).Objects!;
        var function = Assert.Single(objects, value => value.Name == "FormatCode");
        Assert.Equal("nvarchar(80)", function.ReturnType);
        Assert.Equal(new[] { "varchar(24)", "decimal(9,2)" }, function.Parameters!.Select(value => value.DataType));
        var procedure = Assert.Single(objects, value => value.Name == "GetItems");
        Assert.Equal(3, procedure.Parameters!.Count);
        Assert.True(Assert.Single(procedure.Parameters, value => value.Name == "@total").IsOutput);
        Assert.Empty(procedure.Columns);
        Assert.Equal(
            new[] { "Id", "Code", "CategoryId" },
            Assert.Single(objects, value => value.Name == "ItemsForCategory").Columns.Select(column => column.Name)
        );
        Assert.Equal("nvarchar(40)", Assert.Single(objects, value => value.Name == "ItemTokens").Columns.Single().DataType);
        Assert.False(Assert.Single(objects, value => value.Name == "ItemTokens").Columns.Single().IsNullable);
    }

    [Fact]
    public void IndexesPreserveOrderDirectionIncludeFilterAndViewOwnership()
    {
        var graph = new DacpacReader().Read(package);
        var items = graph.Tables.Single(value => value.Name == "Items");
        var unique = Assert.Single(items.UniqueKeys!);
        Assert.Equal("UQ_Items_Code", unique.Name);
        Assert.Equal(new[] { "Code" }, unique.Columns);
        var indexes = graph.Indexes!;
        Assert.Equal(6, indexes.Count);
        Assert.Equal(2, indexes.Count(value => value.IsPrimaryKey));
        var index = Assert.Single(indexes, value => value.Name == "IX_Items_Category");
        Assert.Equal(items.Id, index.ObjectId);
        Assert.Equal(new[] { new SchemaIndexColumn("CategoryId", true), new SchemaIndexColumn("Code", false) }, index.Columns);
        Assert.Equal(new[] { "Payload" }, index.IncludedColumns);
        Assert.Contains("IS NOT NULL", index.Filter, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            graph.Objects!.Single(value => value.Name == "ItemKeys").Id,
            indexes.Single(value => value.Name == "CIX_ItemKeys").ObjectId
        );
    }

    [Fact]
    public void DacfxReferencesResolveObjectsAndSynonymsWithStableEvidenceIds()
    {
        var reader = new DacpacReader();
        var graph = reader.Read(package);
        var overview = graph.Objects!.Single(value => value.Name == "ItemOverview");
        var dependencies = graph.ObjectDependencies!;
        Assert.Contains(dependencies, item => item.FromObjectId == overview.Id && item.ToObjectId == "[Metadata].[dbo].[Items]");
        Assert.Contains(dependencies, item => item.FromObjectId == overview.Id && item.ToObjectId == "[Metadata].[dbo].[Categories]");
        Assert.Contains(
            dependencies,
            item =>
                item.FromObjectId.EndsWith("[FormattedItems]", StringComparison.Ordinal)
                && item.ToObjectId == "[Metadata].[dbo].[FormatCode]"
        );
        Assert.Contains(dependencies, item => item.Kind == "synonym-target" && item.ToObjectId == "[Metadata].[dbo].[Items]");
        Assert.All(dependencies, item => Assert.False(string.IsNullOrWhiteSpace(item.SourcePath)));
        Assert.Equal(dependencies.Select(item => item.Id), reader.Read(package).ObjectDependencies!.Select(item => item.Id));
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

    public void Dispose() => Directory.Delete(directory, true);
}
