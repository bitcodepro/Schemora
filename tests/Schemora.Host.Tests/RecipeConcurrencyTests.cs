using System.Text.Json;
using Microsoft.Data.Sqlite;
using Schemora.Host;
using Schemora.RecipeEngine;
using Schemora.Relationships;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Schemora.Workspaces.Discovery;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class RecipeConcurrencyTests
{
    [Fact]
    public async Task FutureExpectedVersionIsRejectedBeforeItCanOverwriteHistory()
    {
        using var f = new Fixture();
        var saved = await f.Create();
        var error = await Assert.ThrowsAsync<AppException>(() =>
            f.Recipes.Save("ws", saved.Definition with { Version = 2, Name = "future" }, "recipe", "edit", default)
        );
        Assert.Equal("RECIPE_VERSION_CONFLICT", error.Code);
        Assert.Equal(1, f.Recipes.Require("recipe").Definition.Version);
        Assert.Single(f.Recipes.Versions("recipe"));
    }

    [Fact]
    public async Task ConcurrentSaveCannotOverwriteCommittedRecipeOrVersion()
    {
        using var f = new Fixture();
        var original = await f.Create();
        var barrier = f.Context.PauseNextGet();
        var first = f.Recipes.Save("ws", original.Definition with { Name = "first" }, "recipe", "first", default);
        await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await f.Recipes.Save("ws", original.Definition with { Name = "second" }, "recipe", "second", default);
        barrier.Release.TrySetResult();
        var error = await Assert.ThrowsAsync<AppException>(() => first);
        Assert.Equal("RECIPE_VERSION_CONFLICT", error.Code);
        Assert.Equal("second", f.Recipes.Require("recipe").Definition.Name);
        Assert.Equal(2, second.Definition.Version);
        Assert.Equal(new[] { 2, 1 }, f.Recipes.Versions("recipe").Select(v => v.Version));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArchiveDuringSaveOrValidationPreservesBothChanges(bool validating)
    {
        using var f = new Fixture();
        var original = await f.Create();
        var barrier = f.Context.PauseNextGet();
        Task pending = validating
            ? f.Recipes.Revalidate("recipe", default)
            : f.Recipes.Save("ws", original.Definition with { Name = "edited" }, "recipe", "edit", default);
        await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        f.Recipes.Archive("recipe", true);
        barrier.Release.TrySetResult();
        await pending;
        var current = f.Recipes.Require("recipe");
        Assert.True(current.Archived);
        Assert.Equal(validating ? "Initial" : "edited", current.Definition.Name);
        Assert.Equal(validating ? 1 : 2, current.Definition.Version);
        Assert.Empty(f.Recipes.List("ws"));
        f.Recipes.Archive("recipe", false);
        Assert.Equal(
            JsonSerializer.Serialize(current.Definition, CatalogStore.Json),
            JsonSerializer.Serialize(f.Recipes.Require("recipe").Definition, CatalogStore.Json)
        );
    }

    [Fact]
    public async Task RejectWhileApprovalIsValidatingWinsWithoutCreatingVersion()
    {
        using var f = new Fixture();
        await f.Create();
        var proposal = await f.Proposal();
        var barrier = f.Context.PauseNextGet();
        var approval = f.Changes.Approve(proposal.Id, 1, default);
        await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        f.Changes.Reject(proposal.Id);
        barrier.Release.TrySetResult();
        var error = await Assert.ThrowsAsync<AppException>(() => approval);
        Assert.Equal("REPAIR_ALREADY_DECIDED", error.Code);
        Assert.Equal("rejected", f.Store.Get<RepairProposal>("RepairProposals", proposal.Id)!.Status);
        Assert.Equal(1, f.Recipes.Require("recipe").Definition.Version);
        Assert.Single(f.Recipes.Versions("recipe"));
    }

    [Fact]
    public async Task SchemaChangeBetweenPreviewGuardAndSaveCaptureStillRejectsApproval()
    {
        using var f = new Fixture();
        await f.Create();
        var proposal = await f.Proposal();
        // The initial proposal guard reads the old revision; then a new immutable schema is
        // published before Save captures its own context. Only the final proposal guard catches this.
        f.Context.AfterNextRevisionCapture = () => f.NewSnapshot("snapshot2");
        var error = await Assert.ThrowsAsync<AppException>(() => f.Changes.Approve(proposal.Id, 1, default));
        Assert.Equal("REPAIR_CONTEXT_CHANGED", error.Code);
        Assert.Equal("proposed", f.Store.Get<RepairProposal>("RepairProposals", proposal.Id)!.Status);
        Assert.Equal(1, f.Recipes.Require("recipe").Definition.Version);
        Assert.Single(f.Recipes.Versions("recipe"));
    }

    [Fact]
    public async Task ApprovalCommitsNewVersionAndDecisionTogether()
    {
        using var f = new Fixture();
        await f.Create();
        var proposal = await f.Proposal();
        var approved = await f.Changes.Approve(proposal.Id, 1, default);
        Assert.Equal(2, approved.Definition.Version);
        Assert.Contains("NewId", approved.Definition.SqlTemplate);
        Assert.Equal("approved", f.Store.Get<RepairProposal>("RepairProposals", proposal.Id)!.Status);
        Assert.Equal(2, f.Store.Get<RepairProposal>("RepairProposals", proposal.Id)!.AppliedVersion);
        Assert.Equal(2, f.Recipes.Versions("recipe").Count);
        Assert.Equal(
            "REPAIR_ALREADY_DECIDED",
            (await Assert.ThrowsAsync<AppException>(() => f.Changes.Approve(proposal.Id, 1, default))).Code
        );
    }

    [Fact]
    public async Task SourceMappingOrRelationDecisionChangeInvalidatesInFlightSaveAndRender()
    {
        using var f = new Fixture();
        var original = await f.Create();
        var barrier = f.Context.PauseNextGet();
        var saving = f.Recipes.Save("ws", original.Definition with { Name = "stale graph" }, "recipe", "edit", default);
        await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        f.ChangeDecision();
        barrier.Release.TrySetResult();
        Assert.Equal("RELATION_CONTEXT_CHANGED", (await Assert.ThrowsAsync<AppException>(() => saving)).Code);
        Assert.Equal(1, f.Recipes.Require("recipe").Definition.Version);

        barrier = f.Context.PauseNextGet();
        var rendering = f.Recipes.Render(
            "recipe",
            new Dictionary<string, string> { { "Ids", "981726490031" } },
            "standalone",
            "auto",
            false,
            default
        );
        await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        f.AddSource("project", "csharpProject", "project.csproj", "hash1");
        f.Store.Put("UserSettings", "ef-map:ws:project", "ws", new EfProjectMapping("project", "db"));
        barrier.Release.TrySetResult();
        Assert.Equal("SCHEMA_CHANGED", (await Assert.ThrowsAsync<AppException>(() => rendering)).Code);
        Assert.DoesNotContain("981726490031", f.PersistedRecipeJson());
    }

    [Fact]
    public async Task RenderedRuntimeIdsNeverEnterRecipeHistoryOrValidationRecords()
    {
        using var f = new Fixture();
        await f.Create();
        const string runtimeId = "981726490032";
        var output = await f.Recipes.Render(
            "recipe",
            new Dictionary<string, string> { { "Ids", runtimeId } },
            "standalone",
            "auto",
            false,
            default
        );
        Assert.Contains(runtimeId, output.Sql);
        Assert.DoesNotContain(runtimeId, f.PersistedRecipeJson());
        Assert.Single(f.Recipes.Versions("recipe"));
        Assert.NotEmpty(f.Store.All<JsonElement>("ValidationRuns", "ws"));
    }

    [Fact]
    public async Task ValidationGraphRevisionCannotBeConfusedByAbaContextChanges()
    {
        using var f = new Fixture();
        var original = await f.Create();
        var originalDatabase = f.Database;
        var revision = f.Relationships.ContextRevision("ws");
        f.Context.AfterNextRevisionCapture = () => f.NewSnapshot("snapshot2");
        f.Context.AfterNextGraphCapture = () => f.Store.Put("Databases", "db", "ws", originalDatabase);
        var error = await Assert.ThrowsAsync<AppException>(() =>
            f.Recipes.Save("ws", original.Definition with { Name = "wrong context" }, "recipe", "edit", default)
        );
        Assert.Equal("RELATION_CONTEXT_CHANGED", error.Code);
        Assert.Equal(revision, f.Relationships.ContextRevision("ws"));
        Assert.Equal(1, f.Recipes.Require("recipe").Definition.Version);
    }

    [Fact]
    public void ContextRevisionCoversAllGraphInputsAndIgnoresUnrelatedUiSettings()
    {
        using var f = new Fixture();
        var first = f.Relationships.ContextRevision("ws");
        Assert.Equal(first, f.Relationships.ContextRevision("ws"));
        f.Store.Put("UserSettings", "theme", null, new { Value = "dark" });
        Assert.Equal(first, f.Relationships.ContextRevision("ws"));
        void Changed(Action change)
        {
            var previous = f.Relationships.ContextRevision("ws");
            change();
            Assert.NotEqual(previous, f.Relationships.ContextRevision("ws"));
        }
        Changed(() => f.Store.Put("Databases", "db", "ws", f.Database with { Name = "Renamed" }));
        Changed(() => f.Store.Put("Databases", "db", "ws", f.Database with { Status = "failed" }));
        Changed(() => f.NewSnapshot("snapshot2"));
        Changed(() => f.AddSource("cs", "csharp", "Mapping.cs", "hash1"));
        Changed(() => f.AddSource("cs", "csharp", "Mapping.cs", "hash2"));
        Changed(() => f.AddSource("project", "csharpProject", "project.csproj", "project1"));
        Changed(() => f.Store.Put("UserSettings", "ef-map:ws:project", "ws", new EfProjectMapping("project", "db")));
        Changed(f.ChangeDecision);
        var stable = f.Relationships.ContextRevision("ws");
        f.AddSource("cs", "csharp", "Mapping.cs", "hash2"); // UpdatedAt order changes, graph content does not.
        Assert.Equal(stable, f.Relationships.ContextRevision("ws"));
    }

    [Fact]
    public async Task SavedArtificialFixtureRerunsAndDetectsDuplicatePolicyChange()
    {
        using var f = new Fixture();
        var entry = await f.Create();
        var fixture = f.Recipes.SaveRegression(
            "recipe",
            new("Artificial duplicates", 1, true, new Dictionary<string, string> { { "Ids", "2,1,2" } }, SqlContains: ["SELECT o.OldId"])
        );
        Assert.Equal(new[] { "2", "1", "2" }, fixture.Expected["Ids"].Values);
        Assert.Equal(1, fixture.Expected["Ids"].Statistics.Duplicates);
        Assert.True(Assert.Single(f.Recipes.RegressionRuns("recipe")).Success);
        await f.Recipes.Save(
            "ws",
            entry.Definition with
            {
                Inputs = [entry.Definition.Inputs[0] with { Deduplicate = true }],
            },
            "recipe",
            "policy change",
            default
        );
        Assert.False(f.Recipes.RegressionRuns("recipe")[0].Success);
        Assert.Contains(f.Recipes.RegressionRuns("recipe")[0].Failures, m => m.Contains("duplicate"));
        var count = f.Recipes.RegressionRuns("recipe").Count;
        await f.Recipes.Revalidate("recipe", default);
        Assert.Equal(count + 1, f.Recipes.RegressionRuns("recipe").Count);
    }

    [Fact]
    public async Task FixtureRequiresArtificialConsentAndCurrentVersionAndRuntimeNeverSavesFixture()
    {
        using var f = new Fixture();
        await f.Create();
        Assert.Throws<AppException>(() =>
            f.Recipes.SaveRegression("recipe", new("test", 1, false, new Dictionary<string, string> { { "Ids", "1" } }))
        );
        Assert.Throws<AppException>(() =>
            f.Recipes.SaveRegression("recipe", new("test", 99, true, new Dictionary<string, string> { { "Ids", "1" } }))
        );
        await f.Recipes.Render("recipe", new Dictionary<string, string> { { "Ids", "987654321" } }, "standalone", "auto", false, default);
        Assert.Empty(f.Recipes.RegressionFixtures("recipe"));
        Assert.Empty(f.Recipes.RegressionRuns("recipe"));
        Assert.DoesNotContain("987654321", f.PersistedRecipeJson());
    }

    [Fact]
    public async Task ExplicitTableSchemaRenameUsesPreviewThenApprovalVersion()
    {
        using var f = new Fixture();
        await f.Create();
        var old = f.Store.Get<SnapshotRecord>("SchemaSnapshots", "snapshot1")!;
        var graph = old.Graph with { Tables = old.Graph.Tables.Select(t => t with { Schema = "sales", Name = "RenamedOrders" }).ToArray() };
        f.Store.Batch([
            new("SchemaSnapshots", "renamed", "ws", old with { Id = "renamed", Graph = graph }),
            new("Databases", "db", "ws", f.Database with { CurrentSnapshotId = "renamed" }),
        ]);
        var proposal = await f.Changes.Propose(
            "recipe",
            new("db", "db:orders", "", "", "Explicit synthetic rename", 1, "table", "dbo", "Orders"),
            default
        );
        Assert.Contains("[sales].[RenamedOrders]", proposal.AfterSql);
        Assert.Equal(1, f.Recipes.Require("recipe").Definition.Version);
        var approved = await f.Changes.Approve(proposal.Id, 1, default);
        Assert.Equal(2, approved.Definition.Version);
        Assert.Contains("[sales].[RenamedOrders]", approved.Definition.SqlTemplate);
        await Assert.ThrowsAsync<AppException>(() => f.Changes.Approve(proposal.Id, 1, default));
    }

    [Fact]
    public async Task MalformedPersistedFixtureFailsLocallyWithoutBreakingSchemaOrRecipeSave()
    {
        using var f = new Fixture();
        var entry = await f.Create();
        var fixture = f.Recipes.SaveRegression("recipe", new("Synthetic", 1, true, new Dictionary<string, string> { { "Ids", "1" } }));
        f.Store.Put(
            "RecipeRegressionFixtures",
            fixture.Id,
            "ws",
            fixture with
            {
                Expected = new Dictionary<string, RegressionExpectation> { { "Ids", null! } },
            }
        );
        var result = await f.Recipes.Revalidate("recipe", default);
        Assert.Equal("Valid", result.Status);
        Assert.False(f.Recipes.RegressionRuns("recipe")[0].Success);
        Assert.Single(f.Recipes.RegressionRuns("recipe")[0].Failures);
        var saved = await f.Recipes.Save("ws", entry.Definition with { Name = "Still saves" }, "recipe", "edit", default);
        Assert.Equal(2, saved.Definition.Version);
        Assert.False(f.Recipes.RegressionRuns("recipe")[0].Success);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "SchemoraHostTests-" + Guid.NewGuid().ToString("N")
        );
        public CatalogStore Store { get; }
        public RelationshipService Relationships { get; }
        public BlockingContext Context { get; }
        public RecipeService Recipes { get; }
        public ChangeService Changes { get; }
        public DatabaseInfo Database => Store.Get<DatabaseInfo>("Databases", "db")!;
        private readonly SchemaGraph graph = new(
            "Commerce",
            "synthetic.sql",
            DateTimeOffset.UtcNow,
            [new("db:orders", "dbo", "Orders", [new("Id", "bigint", false), new("OldId", "int", true), new("NewId", "int", true)], ["Id"])],
            [],
            []
        );

        public Fixture()
        {
            Store = new CatalogStore(System.IO.Path.Combine(directory, "catalog.sqlite"));
            Store.Put("Workspaces", "ws", null, new Workspace("ws", "Tests", [], [], DateTimeOffset.UtcNow));
            Store.Put("Databases", "db", "ws", new DatabaseInfo("db", "ws", "Commerce", "synthetic.sql", "sql", "ready", null, null));
            NewSnapshot("snapshot1");
            Relationships = new(Store, new RelationshipAnalyzer());
            Context = new(Relationships);
            Recipes = new(Store, Context, new RecipeValidator(), new SqlRecipeRenderer());
            Changes = new(
                Store,
                Recipes,
                Context,
                new RecipeValidator(),
                new SchemaDiffer(),
                new RecipeColumnRewriter(),
                new DacpacReader()
            );
        }

        public async Task<RecipeEntry> Create()
        {
            var input = new RecipeDefinition(
                "ignored",
                "Initial",
                "",
                [],
                "sqlserver",
                ["db"],
                1,
                "SELECT o.OldId FROM dbo.Orders o WHERE o.Id IN ({{ids:Ids}})",
                [new("Ids", "bigint")]
            );
            var created = await Recipes.Save("ws", input, null, "create", default);
            // Seed a deterministic ID through the normal immutable version records.
            var definition = created.Definition with
            {
                Id = "recipe",
            };
            var entry = created with { Definition = definition };
            Store.Batch([
                new("Recipes", "recipe", "ws", entry),
                new(
                    "RecipeVersions",
                    "recipe:v1",
                    "ws",
                    new RecipeVersionRecord("recipe:v1", "recipe", 1, definition, DateTimeOffset.UtcNow, "fixture")
                ),
            ]);
            Store.Delete("Recipes", created.Definition.Id);
            return entry;
        }

        public Task<RepairProposal> Proposal() =>
            Changes.Propose("recipe", new("db", "db:orders", "OldId", "NewId", "Synthetic rename", 1), default);

        public void NewSnapshot(string id)
        {
            Store.Batch([
                new("SchemaSnapshots", id, "ws", new SnapshotRecord(id, "ws", "db", DateTimeOffset.UtcNow, "static", id, graph)),
                new("Databases", "db", "ws", Database with { CurrentSnapshotId = id, Status = "ready" }),
            ]);
        }

        public void AddSource(string id, string kind, string path, string hash) =>
            Store.Put(
                "Sources",
                "ws:" + id,
                "ws",
                new DiscoveredSource(id, System.IO.Path.Combine(directory, path), kind, hash, DateTimeOffset.UtcNow, [])
            );

        public void ChangeDecision() =>
            Store.Put(
                "RelationOverrides",
                "r",
                "ws",
                new SavedRelationDecision(
                    "r",
                    "reject",
                    "Synthetic reason",
                    DateTimeOffset.UtcNow,
                    new(new("r", "candidate", "db:orders", "db:other", ["OldId"], ["Id"], "name-candidate", 0.5), "candidate", false, [])
                )
            );

        public string PersistedRecipeJson() =>
            string.Join(
                "\n",
                new[] { "Recipes", "RecipeVersions", "RecipeInputs", "RecipeDependencies", "ValidationRuns", "RepairProposals" }
                    .SelectMany(table => Store.All<JsonElement>(table, "ws"))
                    .Select(value => value.GetRawText())
            );

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    private sealed class Barrier
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BlockingContext(RelationshipService inner) : IRelationshipContext
    {
        private Barrier? pending;
        public Action? AfterNextRevisionCapture { get; set; }
        public Action? AfterNextGraphCapture { get; set; }

        public string ContextRevision(string workspaceId)
        {
            var revision = inner.ContextRevision(workspaceId);
            var action = AfterNextRevisionCapture;
            AfterNextRevisionCapture = null;
            action?.Invoke();
            return revision;
        }

        public Barrier PauseNextGet()
        {
            var barrier = new Barrier();
            if (Interlocked.CompareExchange(ref pending, barrier, null) is not null)
                throw new InvalidOperationException("A barrier is already armed.");
            return barrier;
        }

        public async Task<RelationshipContextSnapshot> GetSnapshot(string workspaceId, CancellationToken ct)
        {
            var graph = await inner.GetSnapshot(workspaceId, ct);
            var action = AfterNextGraphCapture;
            AfterNextGraphCapture = null;
            action?.Invoke();
            var barrier = Interlocked.Exchange(ref pending, null);
            if (barrier is not null)
            {
                barrier.Reached.TrySetResult();
                await barrier.Release.Task.WaitAsync(ct);
            }
            return graph;
        }
    }
}
