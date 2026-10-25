using Schemora.SqlServer;
using Xunit;

namespace Schemora.Relationships.Tests;

public sealed class RelationshipTests : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "RelationshipArtifacts", Guid.NewGuid().ToString("N"));

    public RelationshipTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void DeclaredForeignKeyWinsAndAccumulatesCandidateEvidence()
    {
        var customers = Table("db", "Customers", [new("Id", "int", false)], ["Id"]);
        var orders = Table("db", "Orders", [new("Id", "int", false), new("CustomerId", "int", true)], ["Id"]);
        var relation = Foreign("sql-fk", orders, customers, ["CustomerId"], ["Id"]);
        var graph = new RelationshipAnalyzer().Analyze([Database("db", "Commerce", [customers, orders], [relation])]);
        var edge = Assert.Single(graph.Relations);
        Assert.Equal("physical", edge.Status);
        Assert.True(edge.IsTrusted);
        Assert.Contains(edge.Evidence, evidence => evidence.Kind == "declared-fk");
        Assert.Contains(edge.Evidence, evidence => evidence.Kind == "inferred-candidate");
    }

    [Fact]
    public void StringIdCandidateNeedsConfirmationBeforeAnyJoinPreview()
    {
        var transactions = Table("db", "Transactions", [new("Id", "nvarchar(80)", false)], ["Id"]);
        var invoices = Table("db", "Invoices", [new("Id", "int", false), new("TransactionId", "nvarchar(80)", true)], ["Id"]);
        var database = Database("db", "Billing", [transactions, invoices]);
        var analyzer = new RelationshipAnalyzer();
        var graph = analyzer.Analyze([database]);
        var edge = Assert.Single(graph.Relations);
        Assert.Equal("candidate", edge.Status);
        Assert.False(edge.IsTrusted);
        Assert.InRange(edge.Relation.Confidence, 0.1, 0.89);
        Assert.Empty(new RelationshipPathFinder().Find(graph, invoices.Id, transactions.Id));
        var confirmed = analyzer.Analyze(
            [database],
            decisions: [new(edge.Relation.Id, "confirm", "Shared transaction text identifier", DateTimeOffset.UtcNow)]
        );
        var trusted = Assert.Single(confirmed.Relations);
        Assert.Equal("confirmed", trusted.Status);
        Assert.Equal("user-confirmed", trusted.Relation.Kind);
        Assert.True(trusted.IsTrusted);
        var path = Assert.Single(new RelationshipPathFinder().Find(confirmed, invoices.Id, transactions.Id));
        Assert.Contains("[t0].[TransactionId] = [t1].[Id]", path.JoinSql);
        Assert.Contains("[Billing].[dbo].[Transactions]", path.JoinSql);
    }

    [Fact]
    public void SameBareColumnNameAndIncompatibleTypesAreInsufficientEvidence()
    {
        var first = Table("db", "Customers", [new("Id", "uniqueidentifier", false)], ["Id"]);
        var second = Table("db", "Other", [new("Id", "int", false), new("CustomerId", "nvarchar(80)", true)], ["Id"]);
        var graph = new RelationshipAnalyzer().Analyze([Database("db", "Commerce", [first, second])]);
        Assert.Empty(graph.Relations);
    }

    [Fact]
    public void RejectionConflictingWithPhysicalMetadataIsVisibleAndBlocked()
    {
        var parent = Table("db", "Parents", [new("Id", "int", false)], ["Id"]);
        var child = Table("db", "Children", [new("Id", "int", false), new("ParentId", "int", true)], ["Id"]);
        var database = Database("db", "Db", [parent, child], [Foreign("fk", child, parent, ["ParentId"], ["Id"])]);
        var analyzer = new RelationshipAnalyzer();
        var before = analyzer.Analyze([database]);
        var after = analyzer.Analyze(
            [database],
            decisions: [new(Assert.Single(before.Relations).Relation.Id, "reject", "Review required", DateTimeOffset.UtcNow)]
        );
        var edge = Assert.Single(after.Relations);
        Assert.Equal("conflict", edge.Status);
        Assert.False(edge.IsTrusted);
        Assert.Contains("conflicts", edge.ReviewReason);
        Assert.Empty(new RelationshipPathFinder().Find(after, child.Id, parent.Id));
    }

    [Fact]
    public void StableIdentitiesAndScoresDoNotDependOnInputTableOrder()
    {
        var parent = Table("db", "Customers", [new("Id", "int", false)], ["Id"]);
        var child = Table("db", "Orders", [new("Id", "int", false), new("CustomerId", "int", true)], ["Id"]);
        var analyzer = new RelationshipAnalyzer();
        var first = analyzer.Analyze([Database("db", "Db", [parent, child])]);
        var second = analyzer.Analyze([Database("db", "Db", [child, parent])]);
        Assert.Equal(first.Relations.Select(edge => edge.Relation.Id), second.Relations.Select(edge => edge.Relation.Id));
        Assert.Equal(first.Relations.Select(edge => edge.Relation.Confidence), second.Relations.Select(edge => edge.Relation.Confidence));
    }

    [Fact]
    public void ChangedTypesInvalidateSavedCandidateConfirmation()
    {
        var parent = Table("db", "Customers", [new("Id", "int", false)], ["Id"]);
        var child = Table("db", "Orders", [new("Id", "int", false), new("CustomerId", "int", true)], ["Id"]);
        var analyzer = new RelationshipAnalyzer();
        var original = analyzer.Analyze([Database("db", "Db", [parent, child])]);
        var decision = new RelationDecision(Assert.Single(original.Relations).Relation.Id, "confirm", "Reviewed", DateTimeOffset.UtcNow);
        var changed = child with { Columns = [new("Id", "int", false), new("CustomerId", "uniqueidentifier", true)] };
        var refreshed = analyzer.Analyze([Database("db", "Db", [parent, changed])], decisions: [decision]);
        Assert.Empty(refreshed.Relations);
        Assert.Contains(refreshed.Diagnostics, diagnostic => diagnostic.Code == "RELATION_DECISION_STALE");
    }

    [Fact]
    public void CompositeJoinPreviewPreservesPairsInBothDirections()
    {
        var parent = Table("db", "Parent]Name", [new("Tenant", "int", false), new("Id", "bigint", false)], ["Tenant", "Id"]);
        var child = Table(
            "db",
            "Children",
            [new("Id", "int", false), new("ParentTenant", "int", false), new("ParentId", "bigint", false)],
            ["Id"]
        );
        var graph = new RelationshipAnalyzer().Analyze([
            Database(
                "db",
                "Db]Name",
                [parent, child],
                [Foreign("FK_composite", child, parent, ["ParentId", "ParentTenant"], ["Id", "Tenant"])]
            ),
        ]);
        var finder = new RelationshipPathFinder();
        var forward = Assert.Single(finder.Find(graph, child.Id, parent.Id));
        Assert.Contains("[t0].[ParentId] = [t1].[Id] AND [t0].[ParentTenant] = [t1].[Tenant]", forward.JoinSql);
        Assert.Contains("[Db]]Name].[dbo].[Parent]]Name]", forward.JoinSql);
        var reverse = Assert.Single(finder.Find(graph, parent.Id, child.Id));
        Assert.Contains("[t0].[Id] = [t1].[ParentId] AND [t0].[Tenant] = [t1].[ParentTenant]", reverse.JoinSql);
    }

    [Fact]
    public void BreadthFirstPathsUseOnlyTrustedEdgesAndRemainBounded()
    {
        var a = Table("db", "A", [new("Id", "int", false)], ["Id"]);
        var b = Table("db", "B", [new("Id", "int", false), new("AId", "int", false)], ["Id"]);
        var c = Table("db", "C", [new("Id", "int", false), new("BId", "int", false)], ["Id"]);
        var graph = new RelationshipAnalyzer().Analyze([
            Database("db", "Db", [a, b, c], [Foreign("ba", b, a, ["AId"], ["Id"]), Foreign("cb", c, b, ["BId"], ["Id"])]),
        ]);
        Assert.Empty(new RelationshipPathFinder().Find(graph, a.Id, c.Id, maxDepth: 1));
        var path = Assert.Single(new RelationshipPathFinder().Find(graph, a.Id, c.Id, maxDepth: 2));
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, path.TableIds);
        Assert.Equal(2, path.RelationIds.Count);
    }

    [Fact]
    public void ObservedCompositeJoinMergesPhysicalEvidenceRegardlessOfSqlPredicateOrder()
    {
        var parent = Table("db", "Parent", [new("Tenant", "int", false), new("Id", "int", false)], ["Tenant", "Id"]);
        var child = Table("db", "Child", [new("Id", "int", false), new("Tenant", "int", false), new("ParentId", "int", false)], ["Id"]);
        var database = Database("db", "Db", [parent, child], [Foreign("fk", child, parent, ["Tenant", "ParentId"], ["Tenant", "Id"])]);
        var sql = Source(
            "db",
            "SELECT c.Id FROM dbo.Parent p INNER JOIN dbo.Child c ON p.Id = c.ParentId AND p.Tenant = c.Tenant WHERE c.Id = 12345;"
        );
        var graph = new RelationshipAnalyzer().Analyze([database], sqlPaths: [sql]);
        var observed = Assert.Single(graph.Relations, edge => edge.Evidence.Any(evidence => evidence.Kind == "observed-join"));
        Assert.Equal("physical", observed.Status);
        Assert.True(observed.IsTrusted);
        Assert.Contains(observed.Evidence, evidence => evidence.Kind == "observed-join" && evidence.Line == 1);
        Assert.DoesNotContain(
            graph.Relations.SelectMany(edge => edge.Evidence),
            evidence => evidence.Detail.Contains("12345", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void ObservedJoinIsUntrustedAndOrPredicatesAreNotAssertedAsRelations()
    {
        var left = Table("db", "One", [new("Id", "int", false), new("Link", "int", true)], ["Id"]);
        var right = Table("db", "Two", [new("Id", "int", false)], ["Id"]);
        var database = Database("db", "Db", [left, right]);
        var query = Source("db", "SELECT a.Id FROM dbo.One a JOIN dbo.Two b ON a.Link = b.Id;");
        var analyzer = new RelationshipAnalyzer();
        var graph = analyzer.Analyze([database], sqlPaths: [query]);
        var edge = Assert.Single(graph.Relations);
        Assert.Equal("observed", edge.Status);
        Assert.False(edge.IsTrusted);
        Assert.Empty(new RelationshipPathFinder().Find(graph, left.Id, right.Id));
        var conditional = Source("db", "SELECT a.Id FROM dbo.One a JOIN dbo.Two b ON a.Link = b.Id OR a.Id = b.Id;");
        Assert.Empty(analyzer.Analyze([database], sqlPaths: [conditional]).Relations);
    }

    [Fact]
    public void AmbiguousSqlDatabaseNamesDoNotBindObservedJoinsOrCrossDatabasePreview()
    {
        var first = Table("first", "Customers", [new("Id", "int", false)], ["Id"]);
        var second = Table("second", "Orders", [new("Id", "int", false), new("CustomerId", "int", true)], ["Id"]);
        var databases = new[] { Database("first", "Shared", [first]), Database("second", "Shared", [second]) };
        var analyzer = new RelationshipAnalyzer();
        var graph = analyzer.Analyze(databases);
        var candidate = Assert.Single(graph.Relations);
        var confirmed = analyzer.Analyze(
            databases,
            decisions: [new(candidate.Relation.Id, "confirm", "Reviewed cross database", DateTimeOffset.UtcNow)]
        );
        Assert.True(Assert.Single(confirmed.Relations).IsTrusted);
        Assert.Empty(new RelationshipPathFinder().Find(confirmed, first.Id, second.Id));
        Assert.Contains(graph.Diagnostics, diagnostic => diagnostic.Code == "DATABASE_NAME_AMBIGUOUS");
        var duplicate = Table("second", "Customers", [new("Id", "int", false)], ["Id"]);
        var query = Source("queries", "SELECT c.Id FROM Shared.dbo.Customers c JOIN Shared.dbo.Orders o ON c.Id = o.CustomerId;");
        var ambiguous = analyzer.Analyze([databases[0], Database("second", "Shared", [duplicate, second])], sqlPaths: [query]);
        Assert.DoesNotContain(ambiguous.Relations, edge => edge.Evidence.Any(evidence => evidence.Kind == "observed-join"));
        Assert.Contains(ambiguous.Diagnostics, diagnostic => diagnostic.Code == "JOIN_TABLE_AMBIGUOUS");
    }

    [Fact]
    public void ConfirmedCrossDatabaseRelationWithDistinctNamesCanRenderSafeQualifiedSql()
    {
        var one = Table("one", "Customers", [new("Id", "nvarchar(80)", false)], ["Id"]);
        var two = Table("two", "Orders", [new("Id", "int", false), new("CustomerId", "nvarchar(80)", true)], ["Id"]);
        var databases = new[] { Database("one", "CustomerDb", [one]), Database("two", "OrderDb", [two]) };
        var analyzer = new RelationshipAnalyzer();
        var before = analyzer.Analyze(databases);
        var decision = new RelationDecision(
            Assert.Single(before.Relations).Relation.Id,
            "confirm",
            "Documented logical key",
            DateTimeOffset.UtcNow
        );
        var graph = analyzer.Analyze(databases, decisions: [decision]);
        var path = Assert.Single(new RelationshipPathFinder().Find(graph, two.Id, one.Id));
        Assert.Contains("[OrderDb].[dbo].[Orders]", path.JoinSql);
        Assert.Contains("[CustomerDb].[dbo].[Customers]", path.JoinSql);
    }

    [Fact]
    public void UnresolvedDeclaredColumnsAreDiagnosedAndNeverUsedInPaths()
    {
        var one = Table("db", "One", [new("Id", "int", false)], ["Id"]);
        var two = Table("db", "Two", [new("Id", "int", false)], ["Id"]);
        var graph = new RelationshipAnalyzer().Analyze([
            Database("db", "Db", [one, two], [Foreign("broken", one, two, ["Removed"], ["Id"])]),
        ]);
        Assert.Empty(graph.Relations);
        Assert.Contains(graph.Diagnostics, diagnostic => diagnostic.Code == "RELATION_ENDPOINTS_UNRESOLVED");
        Assert.Empty(new RelationshipPathFinder().Find(graph, one.Id, two.Id));
    }

    [Fact]
    public void ObservedJoinAnalysisRejectsNetworkSourcesWithoutReadingThem()
    {
        var graph = new RelationshipAnalyzer().Analyze([], sqlPaths: [@"\\nonexistent-schemora-host\share\query.sql"]);
        Assert.Empty(graph.Relations);
        Assert.Contains(
            graph.Diagnostics,
            diagnostic =>
                diagnostic.Code == "JOIN_SQL_UNREADABLE" && diagnostic.Message.Contains("Network SQL paths", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void AlternativeTargetConflictIsVisibleBeforeAndAfterExplicitConfirmation()
    {
        var customers = Table("main", "Customers", [new("Id", "int", false)], ["Id"]);
        var orders = Table("main", "Orders", [new("Id", "int", false), new("CustomerId", "int", true)], ["Id"]);
        var archivedCustomers = Table("archive", "Customers", [new("Id", "int", false)], ["Id"]);
        var databases = new[]
        {
            Database("main", "Commerce", [customers, orders], [Foreign("FK_Orders_Customers", orders, customers, ["CustomerId"], ["Id"])]),
            Database("archive", "Archive", [archivedCustomers]),
        };
        var analyzer = new RelationshipAnalyzer();
        var before = analyzer.Analyze(databases);
        var alternative = Assert.Single(
            before.Relations,
            edge => edge.Relation.FromTableId == orders.Id && edge.Relation.ToTableId == archivedCustomers.Id
        );
        Assert.False(alternative.IsTrusted);
        Assert.Equal("candidate", alternative.Status);
        Assert.Contains("FK_Orders_Customers", alternative.ReviewReason);
        Assert.Contains(alternative.Evidence, evidence => evidence.Kind == "physical-conflict");
        Assert.Contains(before.Diagnostics, diagnostic => diagnostic.Code == "USER_RELATION_CONFLICT");
        var decision = new RelationDecision(
            alternative.Relation.Id,
            "confirm",
            "Archived customers share the documented immutable IDs",
            DateTimeOffset.UtcNow
        );
        var after = analyzer.Analyze(databases, decisions: [decision]);
        var confirmed = Assert.Single(after.Relations, edge => edge.Relation.Id == alternative.Relation.Id);
        Assert.True(confirmed.IsTrusted);
        Assert.Equal("confirmed", confirmed.Status);
        Assert.Contains(decision.Reason, confirmed.ReviewReason);
        Assert.Contains("FK_Orders_Customers", confirmed.ReviewReason);
        Assert.Contains(after.Diagnostics, diagnostic => diagnostic.Code == "USER_RELATION_CONFLICT");
        Assert.Contains(after.Relations, edge => edge.Status == "physical" && edge.IsTrusted && edge.Relation.ToTableId == customers.Id);
        Assert.Single(new RelationshipPathFinder().Find(after, orders.Id, archivedCustomers.Id));
    }

    private RelationshipDatabase Database(
        string id,
        string name,
        IReadOnlyList<SchemaTable> tables,
        IReadOnlyList<SchemaRelation>? relations = null
    ) => new(id, name, new(name, Path.Combine(directory, id, "Model.sqlproj"), DateTimeOffset.UtcNow, tables, relations ?? [], []));

    private static SchemaTable Table(string database, string name, IReadOnlyList<SchemaColumn> columns, IReadOnlyList<string> primaryKey) =>
        new(database + ":dbo." + name, "dbo", name, columns, primaryKey);

    private static SchemaRelation Foreign(
        string name,
        SchemaTable from,
        SchemaTable to,
        IReadOnlyList<string> left,
        IReadOnlyList<string> right
    ) => new(name, name, from.Id, to.Id, left, right, "declared-fk", 1);

    private string Source(string context, string sql)
    {
        var folder = Path.Combine(directory, context);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sql");
        File.WriteAllText(path, sql);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
