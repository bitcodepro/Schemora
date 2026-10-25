using Microsoft.Data.Sqlite;
using Schemora.Host;
using Schemora.RecipeEngine;
using Schemora.Relationships;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class WorkflowBoundaryTests
{
    [Fact]
    public async Task ConfirmedManualMappingCannotStayTrustedAfterNullableDriftThroughANameCandidate()
    {
        using var f = new Fixture();
        var manual = await f.Manual.Save("ws", f.ManualRequest, null, default);
        await f.Relationships.Decide("ws", manual.RelationId, "confirmed", "Explicit review of current manual mapping", default);
        f.Publish(
            f.Graph with
            {
                Tables = f
                    .Graph.Tables.Select(t =>
                        t.Id != "a"
                            ? t
                            : t with
                            {
                                Columns = t.Columns.Select(c => c.Name == "BetaId" ? c with { IsNullable = true } : c).ToArray(),
                            }
                    )
                    .ToArray(),
            }
        );
        var edge = (await f.Relationships.Get("ws", default)).Relations.Single(e => e.Relation.Id == manual.RelationId);
        Assert.False(edge.IsTrusted);
        Assert.Equal("stale", edge.Status);
        Assert.Empty(new RelationshipPathFinder().Find(await f.Relationships.Get("ws", default), "a", "b"));
    }

    [Fact]
    public async Task DeletingAReviewedManualAssertionCannotLeaveItsDecisionTrustingANameCandidate()
    {
        using var f = new Fixture();
        var manual = await f.Manual.Save("ws", f.ManualRequest, null, default);
        await f.Relationships.Decide("ws", manual.RelationId, "confirmed", "Review applies to this manual mapping", default);
        f.Manual.Delete("ws", manual.Id, 1);
        var graph = await f.Relationships.Get("ws", default);
        Assert.DoesNotContain(graph.Relations, edge => edge.Relation.Id == manual.RelationId && edge.IsTrusted);
        Assert.Empty(new RelationshipPathFinder().Find(graph, "a", "b"));
    }

    [Fact]
    public async Task ChainInputKeepsDuplicatesAndInputOrderUnlessUserExplicitlyChangesPolicy()
    {
        using var f = new Fixture();
        var manual = await f.Manual.Save("ws", f.ManualRequest, null, default);
        var preview = await f.Chain.Preview("ws", f.ChainRequest(manual.RelationId), default);
        var input = Assert.Single(preview.Definition.Inputs);
        Assert.False(input.Deduplicate);
        Assert.True(input.PreserveOrder);
        var output = new SqlRecipeRenderer().Render(
            preview.Definition,
            new Dictionary<string, string> { ["Ids"] = "3,1,3" },
            "standalone",
            "auto"
        );
        Assert.Equal(3, output.Statistics["Ids"].OutputCount);
    }

    [Theory]
    [InlineData("tinyint", "int", null)]
    [InlineData("smallint", "int", null)]
    [InlineData("nvarchar(max)", "nvarchar", -1)]
    [InlineData("varchar(max)", "varchar", -1)]
    public async Task ChainSupportsTheSameIdentifierTypesOfferedByTheWizard(string sourceType, string inputType, int? maxLength)
    {
        using var f = new Fixture();
        f.Publish(
            f.Graph with
            {
                Tables = f
                    .Graph.Tables.Select(t =>
                        t.Id != "a"
                            ? t
                            : t with
                            {
                                Columns = t.Columns.Select(c => c.Name == "Id" ? c with { DataType = sourceType } : c).ToArray(),
                            }
                    )
                    .ToArray(),
            }
        );
        var manual = await f.Manual.Save("ws", f.ManualRequest, null, default);
        var preview = await f.Chain.Preview("ws", f.ChainRequest(manual.RelationId), default);
        Assert.Equal(inputType, Assert.Single(preview.Definition.Inputs).Type);
        Assert.Equal(maxLength, preview.Definition.Inputs[0].MaxLength);
    }

    [Theory]
    [InlineData(
        "{\"fromTableId\":null,\"toTableId\":\"b\",\"relationIds\":[],\"inputColumn\":\"Id\",\"inputName\":\"Ids\",\"columns\":[{\"tableId\":\"a\",\"columnName\":\"Id\"}]}"
    )]
    [InlineData(
        "{\"fromTableId\":\"a\",\"toTableId\":\"b\",\"relationIds\":[],\"inputColumn\":\"Id\",\"inputName\":\"Ids\",\"columns\":[null]}"
    )]
    public async Task MalformedNullableJsonFieldsReturnAStableValidationError(string json)
    {
        using var f = new Fixture();
        var request = System.Text.Json.JsonSerializer.Deserialize<ChainRecipeRequest>(json, CatalogStore.Json)!;
        var error = await Assert.ThrowsAsync<AppException>(() => f.Chain.Preview("ws", request, default));
        Assert.Equal("CHAIN_RECIPE_INVALID", error.Code);
    }

    [Fact]
    public async Task StaleManualEvidenceDoesNotDisableAnIndependentCurrentPhysicalForeignKey()
    {
        using var f = new Fixture();
        var manual = await f.Manual.Save("ws", f.ManualRequest, null, default);
        await f.Relationships.Decide("ws", manual.RelationId, "confirmed", "Review original manual shape", default);
        f.Publish(
            f.Graph with
            {
                Tables = f
                    .Graph.Tables.Select(t =>
                        t.Id != "a"
                            ? t
                            : t with
                            {
                                Columns = t.Columns.Select(c => c.Name == "BetaId" ? c with { IsNullable = true } : c).ToArray(),
                            }
                    )
                    .ToArray(),
                Relations = [new("fk", "FK_Alpha_Beta", "a", "b", ["BetaId"], ["Id"], "declared-fk", 1)],
            }
        );
        var edge = (await f.Relationships.Get("ws", default)).Relations.Single(e => e.Relation.Id == manual.RelationId);
        Assert.True(edge.IsTrusted);
        Assert.Equal("physical", edge.Status);
        Assert.False(Assert.Single(await f.Manual.List("ws", default)).CanUseForJoin);
    }

    [Fact]
    public async Task EditingReviewedManualEndpointsRemovesTheOldAssertionDecision()
    {
        using var f = new Fixture();
        var original = await f.Manual.Save("ws", f.ManualRequest, null, default);
        await f.Relationships.Decide("ws", original.RelationId, "confirmed", "Only old endpoints reviewed", default);
        var updated = await f.Manual.Save("ws", f.ManualRequest with { ToTableId = "c", ExpectedVersion = 1 }, original.Id, default);
        var graph = await f.Relationships.Get("ws", default);
        Assert.True(updated.CanUseForJoin);
        Assert.DoesNotContain(graph.Relations, edge => edge.Relation.Id == original.RelationId && edge.IsTrusted);
    }

    [Fact]
    public async Task DeletingManualMappingPreservesAnEarlierIndependentCandidateReview()
    {
        using var f = new Fixture();
        var original = Assert.Single((await f.Relationships.Get("ws", default)).Relations);
        await f.Relationships.Decide("ws", original.Relation.Id, "confirmed", "Independent review before manual mapping", default);
        var manual = await f.Manual.Save("ws", f.ManualRequest, null, default);
        f.Manual.Delete("ws", manual.Id, 1);
        Assert.Contains(
            (await f.Relationships.Get("ws", default)).Relations,
            edge => edge.Relation.Id == original.Relation.Id && edge.IsTrusted
        );
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "SchemoraWorkflowBoundary-" + Guid.NewGuid().ToString("N"));
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
                    new("a", "dbo", "Alpha", [new("Id", "int", false), new("BetaId", "int", false)], ["Id"]),
                    new("b", "dbo", "Beta", [new("Id", "int", false)], ["Id"]),
                    new("c", "dbo", "Gamma", [new("Id", "int", false)], ["Id"]),
                ],
                [],
                []
            );
        public ManualRelationRequest ManualRequest => new("Manual Alpha to Beta", "a", "b", ["BetaId"], ["Id"], "Synthetic known mapping");

        public Fixture()
        {
            Store = new(Path.Combine(directory, "catalog.sqlite"));
            Store.Put("Workspaces", "ws", null, new Workspace("ws", "Boundary", [], [], DateTimeOffset.UtcNow));
            Publish(Graph);
            Relationships = new(Store, new RelationshipAnalyzer());
            Manual = new(Store, Relationships);
            Chain = new(Relationships, new RecipeValidator());
        }

        public ChainRecipeRequest ChainRequest(string relationId) => new("a", "b", [relationId], "Id", "Ids", [new("b", "Id")]);

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
