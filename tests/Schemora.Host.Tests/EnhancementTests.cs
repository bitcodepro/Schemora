using System.Text.Json;
using Microsoft.Data.Sqlite;
using Schemora.Host;
using Schemora.RecipeEngine;
using Schemora.Relationships;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class EnhancementTests
{
    [Fact]
    public async Task ManualCompositeRelationSurvivesReloadAndBecomesUntrustedOnTypeDrift()
    {
        using var f = new Fixture();
        var before = f.Relationships.ContextRevision("ws");
        var record = await f.Manual.Save("ws", f.Request(), null, default);
        Assert.True(record.CanUseForJoin);
        Assert.Equal("confirmed", record.Status);
        Assert.NotEqual(before, f.Relationships.ContextRevision("ws"));
        var reloaded = new RelationshipService(f.Store, new RelationshipAnalyzer());
        var edge = Assert.Single((await reloaded.Get("ws", default)).Relations, e => e.Relation.Id == record.RelationId);
        Assert.Equal(2, edge.Relation.FromColumns.Count);
        Assert.True(edge.IsTrusted);
        f.Publish(
            f.Graph with
            {
                Tables = f
                    .Graph.Tables.Select(t =>
                        t.Id != "a"
                            ? t
                            : t with
                            {
                                Columns = t.Columns.Select(c => c.Name == "Tenant" ? c with { DataType = "bigint" } : c).ToArray(),
                            }
                    )
                    .ToArray(),
            }
        );
        var stale = Assert.Single(await f.Manual.List("ws", default));
        Assert.Equal("stale", stale.Status);
        Assert.False(stale.CanUseForJoin);
        Assert.Empty(new RelationshipPathFinder().Find(await f.Relationships.Get("ws", default), "a", "b"));
    }

    [Fact]
    public async Task ManualEditsUseVersionsAndDeleteRemovesTheAssertion()
    {
        using var f = new Fixture();
        var original = await f.Manual.Save("ws", f.Request(), null, default);
        Assert.Equal(
            "MANUAL_RELATION_VERSION_CONFLICT",
            (
                await Assert.ThrowsAsync<AppException>(() =>
                    f.Manual.Save("ws", f.Request() with { ExpectedVersion = 99 }, original.Id, default)
                )
            ).Code
        );
        var changed = await f.Manual.Save(
            "ws",
            f.Request() with
            {
                Reason = "Reviewed contract",
                ExpectedVersion = 1,
            },
            original.Id,
            default
        );
        Assert.Equal(2, changed.Version);
        Assert.Equal("MANUAL_RELATION_VERSION_CONFLICT", Assert.Throws<AppException>(() => f.Manual.Delete("ws", original.Id, 1)).Code);
        f.Manual.Delete("ws", original.Id, 2);
        Assert.Empty(await f.Manual.List("ws", default));
        Assert.DoesNotContain(
            (await f.Relationships.Get("ws", default)).Relations,
            e => e.Relation.Id == original.RelationId && e.IsTrusted
        );
    }

    [Fact]
    public async Task ManualMappingRejectsMissingColumnsIncompatibleTypesAndDuplicatePairs()
    {
        using var f = new Fixture();
        Assert.Equal(
            "MANUAL_RELATION_TYPES_INVALID",
            (
                await Assert.ThrowsAsync<AppException>(() =>
                    f.Manual.Save("ws", f.Request() with { FromColumns = ["Missing", "Code"] }, null, default)
                )
            ).Code
        );
        Assert.Equal(
            "MANUAL_RELATION_COLUMNS_INVALID",
            (
                await Assert.ThrowsAsync<AppException>(() =>
                    f.Manual.Save("ws", f.Request() with { FromColumns = ["Tenant", "Tenant"] }, null, default)
                )
            ).Code
        );
        await f.Manual.Save("ws", f.Request(), null, default);
        Assert.Equal(
            "MANUAL_RELATION_EXISTS",
            (await Assert.ThrowsAsync<AppException>(() => f.Manual.Save("ws", f.Request(), null, default))).Code
        );
    }

    [Fact]
    public async Task PhysicalConflictRemainsVisibleUntilExplicitDecision()
    {
        using var f = new Fixture();
        f.Publish(f.Graph with { Relations = [new("fk", "Declared", "a", "c", ["Tenant", "Code"], ["Tenant", "Code"], "declared-fk", 1)] });
        var created = await f.Manual.Save("ws", f.Request(), null, default);
        Assert.False(created.CanUseForJoin);
        var edge = (await f.Relationships.Get("ws", default)).Relations.Single(e => e.Relation.Id == created.RelationId);
        Assert.Contains(edge.Evidence, e => e.Kind == "physical-conflict");
        await f.Relationships.Decide("ws", created.RelationId, "confirmed", "Reviewed different business meaning", default);
        Assert.True((await f.Manual.List("ws", default)).Single().CanUseForJoin);
    }

    [Fact]
    public async Task ChainBuilderProducesCompositeJoinTypedInputAndSelectedProjection()
    {
        using var f = new Fixture();
        var manual = await f.Manual.Save("ws", f.Request(), null, default);
        var request = new ChainRecipeRequest(
            "a",
            "b",
            [manual.RelationId],
            "Tenant",
            "Ids",
            [new("a", "Code", "SourceCode"), new("b", "Label", "Destination]Label")]
        );
        var preview = await f.Chain.Preview("ws", request, default);
        Assert.Contains("[t0].[Tenant] = [t1].[Tenant] AND [t0].[Code] = [t1].[Code]", preview.Definition.SqlTemplate);
        Assert.Contains("[Destination]]Label]", preview.Definition.SqlTemplate);
        Assert.Contains("IN ({{ids:Ids}})", preview.Definition.SqlTemplate);
        Assert.Equal("int", Assert.Single(preview.Definition.Inputs).Type);
        var validated = new RecipeValidator().Validate(preview.Definition, await f.Relationships.Get("ws", default));
        Assert.Contains(validated.Status, new[] { "Valid", "Warning" });
        Assert.Contains(validated.Dependencies, d => d.RelationId == manual.RelationId);
        Assert.Empty(f.Store.All<RecipeEntry>("Recipes", "ws")); // Preview cannot create history.
        var rendered = new SqlRecipeRenderer().Render(
            preview.Definition,
            new Dictionary<string, string> { ["Ids"] = "1,2,1" },
            "standalone",
            "auto"
        );
        Assert.Contains("SELECT", rendered.Sql);
        Assert.Equal(3, rendered.Statistics["Ids"].OutputCount);
    }

    [Fact]
    public async Task ChainBuilderRejectsRejectedRoutesAndProjectionOutsideRoute()
    {
        using var f = new Fixture();
        var manual = await f.Manual.Save("ws", f.Request(), null, default);
        var request = new ChainRecipeRequest("a", "b", [manual.RelationId], "Tenant", "Ids", [new("c", "Label")]);
        Assert.Equal("CHAIN_COLUMN_INVALID", (await Assert.ThrowsAsync<AppException>(() => f.Chain.Preview("ws", request, default))).Code);
        await f.Relationships.Decide("ws", manual.RelationId, "rejected", "Do not use", default);
        Assert.Equal(
            "CHAIN_RELATION_UNTRUSTED",
            (
                await Assert.ThrowsAsync<AppException>(() => f.Chain.Preview("ws", request with { Columns = [new("b", "Label")] }, default))
            ).Code
        );
    }

    [Fact]
    public void ExtendedObjectsAreScopedAndImpactTraversesDependenciesWithoutCycles()
    {
        using var f = new Fixture();
        var raw = f.Graph with
        {
            Objects =
            [
                new("v", "dbo", "View", "view", [new("Tenant", "int", false)], Definition: "SELECT Tenant FROM dbo.Alpha"),
                new("p", "dbo", "Proc", "procedure", [], Definition: "SELECT * FROM dbo.View"),
            ],
            Indexes = [new("ix", "a", "IX_Code", "nonclustered", true, false, [new("Code", false)], ["Tenant"])],
            ObjectDependencies =
            [
                new("d1", "v", "a", "dbo.Alpha", "static-reference", "schema.sql", 1, 1),
                new("d2", "p", "v", "dbo.View", "static-reference", "schema.sql", 2, 1),
                new("d3", "v", "p", "dbo.Proc", "static-reference", "schema.sql", 3, 1),
            ],
        };
        var scoped = Identity.Scope(raw, "db");
        var table = scoped.Tables.Single(t => t.Name == "Alpha");
        Assert.StartsWith("db:", table.Id);
        Assert.All(scoped.ObjectDependencies!, d => Assert.StartsWith("db:", d.FromObjectId));
        Assert.Equal(table.Id, Assert.Single(scoped.Indexes!).ObjectId);
        f.Publish(scoped);
        var service = new SchemaObjectService(f.Store);
        var detail = JsonSerializer.SerializeToElement(service.Detail("ws", table.Id), CatalogStore.Json);
        Assert.Equal(2, detail.GetProperty("impact").GetProperty("objectIds").GetArrayLength());
        Assert.Equal(1, detail.GetProperty("indexes").GetArrayLength());
        var list = JsonSerializer.SerializeToElement(service.List("ws", null, "view", null, 0, 100), CatalogStore.Json);
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Null, list.GetProperty("items")[0].GetProperty("definition").ValueKind);
        var view = service.Detail("ws", scoped.Objects!.Single(o => o.Kind == "view").Id);
        Assert.Contains("SELECT Tenant", JsonSerializer.Serialize(view, CatalogStore.Json));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "SchemoraEnhancements-" + Guid.NewGuid().ToString("N"));
        public CatalogStore Store { get; }
        public RelationshipService Relationships { get; }
        public ManualRelationService Manual { get; }
        public ChainRecipeBuilder Chain { get; }
        public SchemaGraph Graph { get; } =
            new(
                "Commerce",
                "synthetic.sql",
                DateTimeOffset.UtcNow,
                [
                    new(
                        "a",
                        "dbo",
                        "Alpha",
                        [new("Tenant", "int", false), new("Code", "int", false), new("Label", "nvarchar(50)", true)],
                        ["Tenant", "Code"]
                    ),
                    new(
                        "b",
                        "dbo",
                        "Beta",
                        [new("Tenant", "int", false), new("Code", "int", false), new("Label", "nvarchar(50)", true)],
                        ["Tenant", "Code"]
                    ),
                    new(
                        "c",
                        "dbo",
                        "Gamma",
                        [new("Tenant", "int", false), new("Code", "int", false), new("Label", "nvarchar(50)", true)],
                        ["Tenant", "Code"]
                    ),
                ],
                [],
                []
            );

        public Fixture()
        {
            Store = new(Path.Combine(directory, "catalog.sqlite"));
            Store.Put("Workspaces", "ws", null, new Workspace("ws", "Synthetic", [], [], DateTimeOffset.UtcNow));
            Publish(Graph);
            Relationships = new(Store, new RelationshipAnalyzer());
            Manual = new(Store, Relationships);
            Chain = new(Relationships, new RecipeValidator());
        }

        public ManualRelationRequest Request() =>
            new("Business mapping", "a", "b", ["Tenant", "Code"], ["Tenant", "Code"], "Documented application contract");

        public void Publish(SchemaGraph graph)
        {
            var id = Guid.NewGuid().ToString("N");
            Store.Batch([
                new("SchemaSnapshots", id, "ws", new SnapshotRecord(id, "ws", "db", DateTimeOffset.UtcNow, "static", id, graph)),
                new(
                    "Databases",
                    "db",
                    "ws",
                    new DatabaseInfo("db", "ws", "Commerce", "synthetic.sql", "sql", "ready", id, DateTimeOffset.UtcNow)
                ),
            ]);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
