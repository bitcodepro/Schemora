using Xunit;

namespace Schemora.SqlServer.Tests;

public sealed class SchemaDifferTests
{
    private static SchemaColumn Col(string name, string type = "int", bool nullable = false) => new(name, type, nullable);

    private static SchemaTable Table(
        string name = "Items",
        SchemaColumn[]? columns = null,
        string[]? pk = null,
        string schema = "dbo",
        string? id = null
    ) => new(id ?? "db." + schema + "." + name, schema, name, columns ?? [Col("Id")], pk ?? ["Id"]);

    private static SchemaGraph Graph(SchemaTable[] tables, SchemaRelation[]? relations = null) =>
        new("Db", "fixture", DateTimeOffset.UnixEpoch, tables, relations ?? [], []);

    [Fact]
    public void ComparesTypesNullabilityOrderedPrimaryKeysAndAddedRemovedColumns()
    {
        var before = Graph([
            Table(columns: [Col("Id"), Col("OldText", "nvarchar(20)", true), Col("Amount", "decimal(10,2)")], pk: ["Id", "Amount"]),
        ]);
        var after = Graph([
            Table(
                columns: [Col("Id", "bigint"), Col("NewText", "nvarchar(20)", true), Col("Amount", "decimal(10,2)", true)],
                pk: ["Amount", "Id"]
            ),
        ]);
        var result = new SchemaDiffer().Compare(before, after);
        Assert.Equal(
            new[] { "columnRemoved", "columnAdded", "columnNullabilityChanged", "columnTypeChanged", "primaryKeyChanged" },
            result.Changes.Select(change => change.Kind)
        );
        var candidate = Assert.Single(result.RenameCandidates);
        Assert.Equal("column", candidate.Kind);
        Assert.Equal("OldText", candidate.OldName);
        Assert.Equal("NewText", candidate.NewName);
        Assert.Contains("do not prove", candidate.Reason);
        Assert.DoesNotContain(result.Changes, change => change.Kind.Contains("rename", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void MatchesTablesByQualifiedNameAcrossSourceIdsAndSuggestsStructuralRenameOnly()
    {
        var before = Graph([Table(id: "old.Items"), Table("Removed")]);
        var after = Graph([Table(id: "new.Items"), Table("Added")]);
        var result = new SchemaDiffer().Compare(before, after);
        Assert.Equal(new[] { "tableRemoved", "tableAdded" }, result.Changes.Select(change => change.Kind));
        Assert.Equal("dbo.Removed", Assert.Single(result.RenameCandidates).OldName);
        Assert.Equal("dbo.Added", result.RenameCandidates[0].NewName);
        Assert.Equal(result, result with { });
        Assert.Equal(result.Changes, new SchemaDiffer().Compare(before, after).Changes);
    }

    [Fact]
    public void ForeignKeyEndpointColumnOrderAndConstraintRemovalAreChanges()
    {
        var source = Table(columns: [Col("Id"), Col("Code")], pk: ["Id", "Code"]);
        var target = Table("Target", [Col("Id"), Col("Code")], ["Id", "Code"]);
        SchemaRelation Fk(string name, string[] to) => new(name, name, source.Id, target.Id, ["Id", "Code"], to, "physicalFK", 1);
        var result = new SchemaDiffer().Compare(
            Graph([source, target], [Fk("FK_Items_Target", ["Id", "Code"]), Fk("FK_Removed", ["Id", "Code"])]),
            Graph([source, target], [Fk("FK_Items_Target", ["Code", "Id"]), Fk("FK_Added", ["Id", "Code"])])
        );
        Assert.Equal(new[] { "foreignKeyAdded", "foreignKeyChanged", "foreignKeyRemoved" }, result.Changes.Select(change => change.Kind));
        Assert.Contains("Target(Code,Id)", result.Changes[1].After);
    }

    [Fact]
    public void SchemaNamesRemainDistinctAndAmbiguousSnapshotsAreDiagnosed()
    {
        Assert.Empty(
            new SchemaDiffer()
                .Compare(Graph([Table(schema: "dbo"), Table(schema: "sales")]), Graph([Table(schema: "dbo"), Table(schema: "sales")]))
                .Changes
        );
        var ambiguous = new SchemaDiffer().Compare(Graph([Table(), Table()]), Graph([Table()]));
        Assert.Empty(ambiguous.Changes);
        Assert.Equal("DIFF_TABLE_AMBIGUOUS", Assert.Single(ambiguous.Diagnostics).Code);
    }

    [Fact]
    public void LargeDiffAndRenameCandidateSetsHaveExplicitBounds()
    {
        var before = Graph([Table(columns: Enumerable.Range(0, 10_001).Select(index => Col("Old" + index)).ToArray())]);
        var after = Graph([Table(columns: [Col("New")])]);
        var result = new SchemaDiffer().Compare(before, after);
        Assert.Equal(SchemaDiffer.MaximumChanges, result.Changes.Count);
        Assert.Equal(SchemaDiffer.MaximumRenameCandidates, result.RenameCandidates.Count);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "DIFF_CHANGE_LIMIT");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "DIFF_RENAME_LIMIT");
    }

    [Fact]
    public void DatabasesDeployedSeparatelyMatchSystemNamedKeysAndIndexesByShape()
    {
        // The same model deployed twice: table ids carry each database's id, and unnamed constraints get random system names.
        SchemaGraph Deployed(string database, string fkName, string pkName, bool extraIndex = false)
        {
            var orders = Table("Orders", [Col("Id"), Col("CustomerId")], ["Id"], id: database + ".dbo.Orders");
            var customers = Table("Customers", id: database + ".dbo.Customers");
            SchemaIndex Index(string id, string name, bool primary, string column) =>
                new(id, orders.Id, name, "clustered", primary, primary, [new(column, false)], []);
            return Graph([orders, customers], [new(fkName, fkName, orders.Id, customers.Id, ["CustomerId"], ["Id"], "physicalFK", 1)]) with
            {
                Indexes =
                [
                    Index(database + ".pk", pkName, true, "Id"),
                    Index(database + ".ix", "IX_Orders_CustomerId", false, "CustomerId"),
                    .. extraIndex ? [Index(database + ".ix2", "IX_Orders_Id", false, "Id")] : Array.Empty<SchemaIndex>(),
                ],
            };
        }
        var production = Deployed("prod", "FK__Orders__Custome__3B75D760", "PK__Orders__3214EC07A1B2C3D4");
        var staging = Deployed("stage", "FK__Orders__Custome__4C6B5938", "PK__Orders__3214EC0712345678");
        Assert.Empty(new SchemaDiffer().Compare(production, staging).Changes);
        var changed = new SchemaDiffer().Compare(
            production,
            Deployed("stage", "FK_Orders_Customers", "PK__Orders__3214EC0712345678", true)
        );
        // An explicitly named FK is a different constraint from a system-named one; real index changes still show.
        Assert.Equal(new[] { "foreignKeyAdded", "foreignKeyRemoved", "indexAdded" }, changed.Changes.Select(change => change.Kind).Order());
    }
}
