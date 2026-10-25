using Schemora.Relationships;
using Schemora.SqlServer;
using Xunit;

namespace Schemora.Relationships.Tests;

public sealed class EfMappingTests
{
    private const string Header =
        "using System; using System.Collections.Generic; using System.ComponentModel.DataAnnotations.Schema; using Microsoft.EntityFrameworkCore; using Microsoft.EntityFrameworkCore.Metadata.Builders; ";
    private const string Entities = """
        [Table("Customers", Schema="dbo")]
        public class Customer { public int TenantId {get;set;} public Guid CustomerId {get;set;} public ICollection<Order> Orders {get;set;} = new List<Order>(); }
        [Table("Orders", Schema="sales")]
        public class Order { public int? TenantId {get;set;} public Guid? CustomerId {get;set;} public Customer? Customer {get;set;} }
        """;
    private const string ForeignChain =
        "m.Entity<Order>().HasOne(o=>o.Customer).WithMany(c=>c.Orders).HasForeignKey(o=>new {o.TenantId,o.CustomerId}).HasPrincipalKey(c=>new {c.TenantId,c.CustomerId});";

    [Fact]
    public void SelectedSymbolsEnableMappingButIncompleteProjectCannotConferTrust()
    {
        using var source = new Source(Header + Entities + Context("\n#if SELECTED\n" + ForeignChain + "\n#endif\n"));
        var complete = new CSharpProjectContext(
            new("selection.csproj", "Release", "net10.0"),
            [new(source.Path, ["SELECTED"])],
            [],
            [],
            true,
            "complete"
        );
        var analyzer = new EfMappingAnalyzer();
        Assert.True(Assert.Single(analyzer.Analyze([Database()], [new("db", [], complete)]).Relations).IsTrusted);
        var partial = complete with
        {
            IsComplete = false,
            Diagnostics = [new("warning", "CSHARP_PROJECT_PARTIAL", "SDK imports are unresolved.")],
        };
        var result = analyzer.Analyze([Database()], [new("db", [], partial)]);
        Assert.False(Assert.Single(result.Relations).IsTrusted);
        Assert.Contains(result.Diagnostics, d => d.Code == "CSHARP_PROJECT_PARTIAL");
        Assert.Empty(analyzer.Analyze([Database()], [new("db", [], complete with { Files = [new(source.Path, [])] })]).Relations);
    }

    private static string Context(string body) =>
        "public class ShopContext : DbContext { public DbSet<Order> Orders {get;set;} = null!; public DbSet<Customer> Customers {get;set;} = null!; protected override void OnModelCreating(ModelBuilder m) { "
        + body
        + " } }";

    [Fact]
    public void MapsNullableCompositeFluentKeysWithOrderedEvidence()
    {
        using var source = new Source(Header + Entities + Context(ForeignChain));
        var result = Analyze(source);
        var edge = Assert.Single(result.Relations);
        Assert.True(edge.IsTrusted, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal("mapped", edge.Status);
        Assert.Equal("orm-mapping", edge.Relation.Kind);
        Assert.Equal(new[] { "TenantId", "CustomerId" }, edge.Relation.FromColumns);
        Assert.Equal(new[] { "TenantId", "CustomerId" }, edge.Relation.ToColumns);
        Assert.Equal("db:orders", edge.Relation.FromTableId);
        Assert.Equal("db:customers", edge.Relation.ToTableId);
        Assert.All(
            edge.Evidence,
            e =>
            {
                Assert.Equal(source.Path, e.SourcePath);
                Assert.True(e.Line > 0);
            }
        );
    }

    [Fact]
    public void ReversesHasManyToDependentForeignKeyDirection()
    {
        using var source = new Source(
            Header
                + Entities
                + Context(
                    "m.Entity<Customer>().HasMany(c=>c.Orders).WithOne(o=>o.Customer).HasForeignKey(o=>new {o.TenantId,o.CustomerId});"
                )
        );
        var result = Analyze(source);
        var edge = Assert.Single(result.Relations);
        Assert.True(edge.IsTrusted, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal("db:orders", edge.Relation.FromTableId);
        Assert.Equal("db:customers", edge.Relation.ToTableId);
    }

    [Fact]
    public void ReadsTableColumnAndCompositeForeignKeyAttributesWithoutExecutingCode()
    {
        var entities = Entities.Replace(
            "public Guid? CustomerId {get;set;} public Customer? Customer {get;set;}",
            "[Column(\"CustomerId\")] public Guid? Reference {get;set;} [ForeignKey(\"TenantId,Reference\")] public Customer? Customer {get;set;}"
        );
        using var source = new Source(
            Header
                + entities
                + Context("")
                + "public class NeverExecute { static NeverExecute() { throw new Exception(\"User code must never run\"); } }"
        );
        var result = Analyze(source);
        var edge = Assert.Single(result.Relations);
        Assert.True(edge.IsTrusted, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(new[] { "TenantId", "CustomerId" }, edge.Relation.FromColumns);
    }

    [Fact]
    public void ReadsRegisteredEntityConfigurationAndColumnRename()
    {
        var entities = Entities.Replace("public Guid? CustomerId {get;set;}", "public Guid? Reference {get;set;}");
        var configuration = """
            public class OrderConfiguration : IEntityTypeConfiguration<Order> {
                public void Configure(EntityTypeBuilder<Order> b) {
                    b.ToTable("Orders", "sales");
                    b.Property(o=>o.Reference).HasColumnName("CustomerId");
                    b.HasOne(o=>o.Customer).WithMany(c=>c.Orders).HasForeignKey(o=>new {o.TenantId,o.Reference});
                }
            }
            """;
        using var source = new Source(Header + entities + Context("m.ApplyConfiguration(new OrderConfiguration());") + configuration);
        var result = Analyze(source);
        var edge = Assert.Single(result.Relations);
        Assert.True(edge.IsTrusted, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(new[] { "TenantId", "CustomerId" }, edge.Relation.FromColumns);
    }

    [Fact]
    public void UnregisteredConfigurationProducesReviewCandidate()
    {
        using var source = new Source(
            Header
                + Entities
                + Context("")
                + "public class OrderConfiguration : IEntityTypeConfiguration<Order> { public void Configure(EntityTypeBuilder<Order> b) { b.HasOne(o=>o.Customer).WithMany(c=>c.Orders).HasForeignKey(o=>new {o.TenantId,o.CustomerId}); } }"
        );
        var result = Analyze(source);
        Assert.All(result.Relations, e => Assert.False(e.IsTrusted));
        Assert.Contains(result.Diagnostics, d => d.Code == "EF_MAPPING_NEEDS_REVIEW");
    }

    [Fact]
    public void RejectsConflictingToTableMappings()
    {
        using var source = new Source(Header + Entities + Context("m.Entity<Order>().ToTable(\"OtherOrders\",\"sales\");" + ForeignChain));
        var result = Analyze(source);
        Assert.Empty(result.Relations);
        Assert.Contains(result.Diagnostics, d => d.Code == "EF_TABLE_AMBIGUOUS");
    }

    [Fact]
    public void DynamicTableAndConditionalMappingAreNeverTrusted()
    {
        using var source = new Source(
            Header
                + Entities
                + Context("m.Entity<Order>().ToTable(GetTable(),\"sales\"); if(DateTime.UtcNow.Day>0) {" + ForeignChain + "}")
                    .Replace("protected override", "private string GetTable()=>\"Orders\"; protected override")
        );
        var result = Analyze(source);
        Assert.All(result.Relations, e => Assert.False(e.IsTrusted));
        Assert.Contains(result.Diagnostics, d => d.Code == "EF_TABLE_DYNAMIC");
    }

    [Fact]
    public void RejectsShadowNamesAbsentFromCatalog()
    {
        using var source = new Source(
            Header
                + Entities
                + Context("m.Entity<Order>().HasOne(o=>o.Customer).WithMany(c=>c.Orders).HasForeignKey(\"TenantId\",\"MissingColumn\");")
        );
        var result = Analyze(source);
        Assert.Empty(result.Relations);
        Assert.Contains(result.Diagnostics, d => d.Code == "EF_COLUMN_UNRESOLVED");
    }

    [Fact]
    public void DoesNotGuessMissingPrincipalKeyOrIncompatibleTypes()
    {
        using var source = new Source(
            Header
                + Entities
                + Context("m.Entity<Order>().HasOne(o=>o.Customer).WithMany(c=>c.Orders).HasForeignKey(o=>new {o.TenantId,o.CustomerId});")
        );
        var db = Database();
        var noKey = db with
        {
            Graph = db.Graph with
            {
                Tables = db.Graph.Tables.Select(t => t.Name == "Customers" ? t with { PrimaryKey = [] } : t).ToArray(),
            },
        };
        var missing = Analyze(source, noKey);
        Assert.Empty(missing.Relations);
        Assert.Contains(missing.Diagnostics, d => d.Code == "EF_KEY_UNRESOLVED");
        var incompatible = db with
        {
            Graph = db.Graph with
            {
                Tables = db
                    .Graph.Tables.Select(t =>
                        t.Name == "Customers"
                            ? t with
                            {
                                Columns = t.Columns.Select(c => c.Name == "TenantId" ? c with { DataType = "bigint" } : c).ToArray(),
                            }
                            : t
                    )
                    .ToArray(),
            },
        };
        Assert.Empty(Analyze(source, incompatible).Relations);
    }

    [Fact]
    public void KeepsIdenticalEntityNamesIsolatedAcrossExplicitDatabaseScopes()
    {
        using var source = new Source(Header + Entities + Context(ForeignChain));
        var first = Database("first");
        var second = Database("second");
        var analyzer = new EfMappingAnalyzer();
        var one = analyzer.Analyze([first, second], [new("first", [source.Path])]);
        Assert.Equal("first:orders", Assert.Single(one.Relations).Relation.FromTableId);
        var both = analyzer.Analyze([first, second], [new("first", [source.Path]), new("second", [source.Path])]);
        Assert.Equal(2, both.Relations.Count);
        Assert.Equal(2, both.Relations.Select(e => e.Relation.Id).Distinct().Count());
        Assert.All(both.Relations, e => Assert.True(e.IsTrusted));
        var missing = analyzer.Analyze([first], [new("unassigned", [source.Path])]);
        Assert.Empty(missing.Relations);
        Assert.Contains(missing.Diagnostics, d => d.Code == "EF_DATABASE_UNASSIGNED");
    }

    [Fact]
    public void MultipleContextsAndSourceDefinedLookalikeAttributesAreUntrusted()
    {
        using var multi = new Source(
            Header
                + Entities
                + Context(ForeignChain)
                + "public class SecondContext : DbContext { public DbSet<Order> Orders {get;set;} = null!; }"
        );
        Assert.All(Analyze(multi).Relations, e => Assert.False(e.IsTrusted));
        var fakeEntities = Entities.Replace(
            "public Customer? Customer {get;set;}",
            "[ForeignKey(\"TenantId,CustomerId\")] public Customer? Customer {get;set;}"
        );
        using var fake = new Source(
            Header
                + fakeEntities
                + Context("")
                + "public class TableAttribute : Attribute { public TableAttribute(string name){} public string Schema {get;set;} = \"\"; } public class ForeignKeyAttribute : Attribute { public ForeignKeyAttribute(string name){} }"
        );
        var result = Analyze(fake);
        Assert.All(result.Relations, e => Assert.False(e.IsTrusted));
        Assert.Contains(result.Diagnostics, d => d.Code == "EF_MAPPING_NEEDS_REVIEW");
    }

    [Fact]
    public void HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            new EfMappingAnalyzer().Analyze([Database()], [new("db", [])], cancellation.Token)
        );
    }

    [Fact]
    public void MapsExplicitAlternatePrincipalKeyAndOneToOneDirection()
    {
        var entities = Entities
            .Replace("public Guid CustomerId {get;set;}", "public Guid CustomerId {get;set;} public string ExternalId {get;set;} = \"\";")
            .Replace(
                "public Guid? CustomerId {get;set;}",
                "public Guid? CustomerId {get;set;} public string? CustomerExternalId {get;set;}"
            );
        using var alternate = new Source(
            Header
                + entities
                + Context(
                    "m.Entity<Order>().HasOne(o=>o.Customer).WithMany(c=>c.Orders).HasForeignKey(o=>o.CustomerExternalId).HasPrincipalKey(c=>c.ExternalId);"
                )
        );
        var db = Database();
        db = db with
        {
            Graph = db.Graph with
            {
                Tables = db
                    .Graph.Tables.Select(t =>
                        t with
                        {
                            Columns = t
                                .Columns.Append(
                                    new SchemaColumn(
                                        t.Name == "Customers" ? "ExternalId" : "CustomerExternalId",
                                        "nvarchar(80)",
                                        t.Name != "Customers"
                                    )
                                )
                                .ToArray(),
                        }
                    )
                    .ToArray(),
            },
        };
        var result = Analyze(alternate, db);
        var edge = Assert.Single(result.Relations);
        Assert.True(edge.IsTrusted, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(new[] { "CustomerExternalId" }, edge.Relation.FromColumns);
        Assert.Equal(new[] { "ExternalId" }, edge.Relation.ToColumns);
        using var one = new Source(
            Header
                + Entities
                + Context(
                    "m.Entity<Customer>().HasOne<Order>().WithOne(o=>o.Customer).HasForeignKey<Order>(o=>new {o.TenantId,o.CustomerId}).HasPrincipalKey<Customer>(c=>new {c.TenantId,c.CustomerId});"
                )
        );
        var oneResult = Analyze(one);
        Assert.Equal("db:orders", Assert.Single(oneResult.Relations).Relation.FromTableId);
        Assert.All(oneResult.Relations, e => Assert.True(e.IsTrusted));
    }

    [Fact]
    public void RejectsNetworkPathsAndDiagnosesUnresolvedFluentReceiver()
    {
        var remote = new EfMappingAnalyzer().Analyze([Database()], [new("db", [@"\\not-contacted\share\mapping.cs"])]);
        Assert.Empty(remote.Relations);
        Assert.Contains(remote.Diagnostics, d => d.Code == "EF_LOCAL_SOURCE_REQUIRED");
        using var source = new Source(
            "public class Broken { void Configure() { unknown.Entity<Order>().ToTable(\"Orders\",\"sales\"); } }"
        );
        var result = Analyze(source);
        Assert.Empty(result.Relations);
        Assert.Contains(result.Diagnostics, d => d.Code == "EF_SEMANTIC_UNRESOLVED");
    }

    [Fact]
    public void ConditionalCompilationAndUnknownConfigurationHelpersRequireReview()
    {
        using var conditional = new Source(
            Header
                + Entities
                + Context(
                    "\n#if CUSTOM_CONVENTION\nm.Entity<Order>().ToTable(\"OtherOrders\",\"sales\");\n#else\nm.Entity<Order>().ToTable(\"Orders\",\"sales\");\n#endif\n"
                        + ForeignChain
                )
        );
        var result = Analyze(conditional);
        Assert.All(result.Relations, e => Assert.False(e.IsTrusted));
        Assert.Contains(result.Diagnostics, d => d.Code == "EF_PREPROCESSOR_PARTIAL");
        using var helper = new Source(
            Header
                + Entities
                + Context("ConfigureElsewhere(m);" + ForeignChain)
                    .Replace("protected override", "private void ConfigureElsewhere(ModelBuilder x) {} protected override")
        );
        var helperResult = Analyze(helper);
        Assert.All(helperResult.Relations, e => Assert.False(e.IsTrusted));
        Assert.Contains(helperResult.Diagnostics, d => d.Code == "EF_CONFIGURATION_DYNAMIC");
    }

    private static EfAnalysisResult Analyze(Source source, RelationshipDatabase? database = null) =>
        new EfMappingAnalyzer().Analyze([database ?? Database()], [new((database ?? Database()).Id, [source.Path])]);

    private static RelationshipDatabase Database(string id = "db") =>
        new(
            id,
            "Commerce",
            new(
                "Commerce",
                "synthetic",
                DateTimeOffset.UtcNow,
                [
                    new(
                        id + ":customers",
                        "dbo",
                        "Customers",
                        [new("TenantId", "int", false), new("CustomerId", "uniqueidentifier", false)],
                        ["TenantId", "CustomerId"]
                    ),
                    new(id + ":orders", "sales", "Orders", [new("TenantId", "int", true), new("CustomerId", "uniqueidentifier", true)], []),
                ],
                [],
                []
            )
        );

    private sealed class Source : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "ef-test-" + Guid.NewGuid().ToString("N") + ".cs");

        public Source(string contents) => File.WriteAllText(Path, contents);

        public void Dispose() => File.Delete(Path);
    }
}
