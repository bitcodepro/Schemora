using Microsoft.Data.Sqlite;
using Schemora.Host;
using Schemora.RecipeEngine;
using Schemora.Relationships;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class PortableConfigurationTests
{
    [Fact]
    public async Task CompositeAssertionsRemainUntrustedUntilIndividualReview()
    {
        var path = Path.Combine(Path.GetTempPath(), "portable-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var store = new CatalogStore(path);
        store.Put("Workspaces", "ws", null, new Workspace("ws", "Test", [], [], DateTimeOffset.UtcNow));
        var graph = new SchemaGraph(
            "Db",
            "test.sql",
            DateTimeOffset.UtcNow,
            [
                new("a", "dbo", "A", [new("Tenant", "int", false), new("Id", "int", false)], ["Tenant", "Id"]),
                new("b", "dbo", "B", [new("Tenant", "int", false), new("Id", "int", false)], ["Tenant", "Id"]),
            ],
            [],
            []
        );
        store.Put(
            "SchemaSnapshots",
            "snapshot",
            "ws",
            new SnapshotRecord("snapshot", "ws", "target", DateTimeOffset.UtcNow, "static", "hash", graph)
        );
        store.Put("Databases", "target", "ws", new DatabaseInfo("target", "ws", "Db", "test.sql", "sql", "ready", "snapshot", null));
        var relationships = new RelationshipService(store, new RelationshipAnalyzer());
        var manual = new ManualRelationService(store, relationships);
        var service = new PortableConfigurationService(
            store,
            new RecipeService(store, relationships, new RecipeValidator(), new SqlRecipeRenderer()),
            relationships,
            manual
        );
        var assertion = new PortableAssertion(
            "source-manual",
            "manual",
            "Composite",
            new("source", "dbo", "A"),
            new("source", "dbo", "B"),
            ["Tenant", "Id"],
            ["Tenant", "Id"],
            "Reviewed contract"
        );
        var definition = new RecipeDefinition(
            "source-recipe",
            "Fixture recipe",
            "",
            [],
            "sqlserver",
            ["source"],
            1,
            "SELECT a.Id FROM dbo.A a WHERE a.Id IN ({{ids:Ids}})",
            [new("Ids", "int")]
        );
        var expected = new RegressionExpectation(new IdStatistics(3, 3, 0, 2, 1, 3), ["2", "1", "2"], [1, 2, 3], false, true);
        var fixture = new PortableRegressionFixture(
            "source-recipe",
            "Artificial duplicates",
            new Dictionary<string, string> { { "Ids", "2,1,2" } },
            "standalone",
            "auto",
            new Dictionary<string, RegressionExpectation> { { "Ids", expected } },
            "inline",
            ["SELECT a.Id"],
            [],
            new string('a', 64)
        );
        var bundle = new ConfigurationBundle(
            2,
            "Portable",
            [],
            [],
            [new("source", "Db", "sql")],
            [definition],
            null,
            [assertion],
            RegressionFixtures: [fixture]
        );
        var missing = service.Preview("ws", new(bundle, new Dictionary<string, string>()));
        Assert.False(missing.CanImport);
        var request = new ImportConfigurationRequest(bundle, new Dictionary<string, string> { { "source", "target" } });
        var preview = service.Preview("ws", request);
        Assert.True(preview.CanImport);
        await Assert.ThrowsAsync<AppException>(() => service.Import("ws", request, default));
        await service.Import("ws", request with { PreviewRevision = preview.Revision }, default);
        var importedRecipe = Assert.Single(store.All<RecipeEntry>("Recipes", "ws"));
        Assert.NotEqual("source-recipe", importedRecipe.Definition.Id);
        var importedFixture = Assert.Single(store.All<RecipeRegressionFixture>("RecipeRegressionFixtures", "ws"));
        Assert.Equal(importedRecipe.Definition.Id, importedFixture.RecipeId);
        Assert.Equal(new[] { "2", "1", "2" }, importedFixture.Expected["Ids"].Values);
        Assert.Empty(store.All<RecipeRegressionRun>("RecipeRegressionRuns", "ws"));
        Assert.Single(service.Export("ws").RegressionFixtures!);
        Assert.Empty(ManualRelationCatalog.List(store, "ws"));
        Assert.Empty(store.All<SavedRelationDecision>("RelationOverrides", "ws"));
        var pending = Assert.Single(service.Pending("ws"));
        Assert.Equal(2, Assert.Single(service.Export("ws").Assertions!).FromColumns.Count);
        await service.Review("ws", pending.Id, true, default);
        Assert.Empty(service.Pending("ws"));
        Assert.Equal(2, Assert.Single(ManualRelationCatalog.List(store, "ws")).FromColumns.Count);
        Assert.True(Assert.Single((await relationships.Get("ws", default)).Relations).IsTrusted);
        // Only the referenced identity is recovered from history after a current table disappears.
        var next = graph with
        {
            Tables = graph.Tables.Where(t => t.Id != "b").ToArray(),
        };
        store.Put(
            "SchemaSnapshots",
            "new-snapshot",
            "ws",
            new SnapshotRecord("new-snapshot", "ws", "target", DateTimeOffset.UtcNow, "static", "new", next)
        );
        store.Put("Databases", "target", "ws", new DatabaseInfo("target", "ws", "Db", "test.sql", "sql", "ready", "new-snapshot", null));
        Assert.Equal("B", Assert.Single(service.Export("ws").Assertions!).To.Name);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void VersionOneJsonStillDeserializesWithOptionalV2Fields()
    {
        var bundle = System.Text.Json.JsonSerializer.Deserialize<ConfigurationBundle>(
            "{\"formatVersion\":1,\"name\":\"v1\",\"rootNames\":[],\"excludes\":[],\"databases\":[],\"recipes\":[]}",
            CatalogStore.Json
        )!;
        Assert.Equal(1, bundle.FormatVersion);
        Assert.Null(bundle.Assertions);
        Assert.Null(bundle.Projects);
        Assert.Null(bundle.Aliases);
    }

    [Fact]
    public async Task ChangedManifestAndDatabaseMappingsInvalidatePreview()
    {
        var (store, service) = EmptyWorkspace();
        var bundle = new ConfigurationBundle(2, "Original", [], [], [new("source", "DB", "sql")], [], null);
        var request = new ImportConfigurationRequest(bundle, new Dictionary<string, string> { { "source", "target" } });
        var preview = service.Preview("ws", request);
        Assert.True(preview.CanImport);
        var changed = request with { Manifest = bundle with { Name = "Changed" }, PreviewRevision = preview.Revision };
        var error = await Assert.ThrowsAsync<AppException>(() => service.Import("ws", changed, default));
        Assert.Equal("CONFIGURATION_PREVIEW_REQUIRED", error.Code);
        store.Put("Databases", "second", "ws", new DatabaseInfo("second", "ws", "Second", "second.sql", "sql", "ready", null, null));
        error = await Assert.ThrowsAsync<AppException>(() =>
            service.Import("ws", request with { PreviewRevision = preview.Revision }, default)
        );
        Assert.Equal("CONFIGURATION_PREVIEW_REQUIRED", error.Code);
        preview = service.Preview("ws", request);
        changed = request with
        {
            DatabaseMappings = new Dictionary<string, string> { { "source", "second" } },
            PreviewRevision = preview.Revision,
        };
        error = await Assert.ThrowsAsync<AppException>(() => service.Import("ws", changed, default));
        Assert.Equal("CONFIGURATION_PREVIEW_REQUIRED", error.Code);
    }

    [Fact]
    public void MalformedRegressionExpectationsConflictWithoutCrashing()
    {
        var (_, service) = EmptyWorkspace();
        var recipe = new RecipeDefinition(
            "recipe",
            "Fixture",
            "",
            [],
            "sqlserver",
            ["source"],
            1,
            "SELECT 1 WHERE 1 IN ({{ids:Ids}})",
            [new("Ids", "int")]
        );
        var expected = new RegressionExpectation(new IdStatistics(1, 1, 0, 1, 0, 1), ["1"], [1], false, true);
        var fixture = new PortableRegressionFixture(
            "recipe",
            "Test",
            new Dictionary<string, string> { { "Ids", "1" } },
            "standalone",
            "auto",
            new Dictionary<string, RegressionExpectation> { { "Ids", expected } },
            "values",
            [],
            [],
            new string('a', 64)
        );
        var malformed = new[]
        {
            fixture with
            {
                Expected = null!,
            },
            fixture with
            {
                Expected = new Dictionary<string, RegressionExpectation>(),
            },
            fixture with
            {
                Expected = new Dictionary<string, RegressionExpectation> { { "Ids", null! } },
            },
            fixture with
            {
                Expected = new Dictionary<string, RegressionExpectation> { { "Ids", expected with { Statistics = null! } } },
            },
            fixture with
            {
                Expected = new Dictionary<string, RegressionExpectation> { { "Ids", expected with { Values = null! } } },
            },
            fixture with
            {
                Expected = new Dictionary<string, RegressionExpectation> { { "Ids", expected with { Ordinals = null! } } },
            },
            fixture with
            {
                Expected = new Dictionary<string, RegressionExpectation> { { "Ids", expected with { Values = [null!] } } },
            },
            fixture with
            {
                ArtificialInputs = new Dictionary<string, string>(),
                Expected = new Dictionary<string, RegressionExpectation>(),
            },
        };
        foreach (var item in malformed)
        {
            var bundle = new ConfigurationBundle(
                2,
                "Bad fixture",
                [],
                [],
                [new("source", "DB", "sql")],
                [recipe],
                null,
                RegressionFixtures: [item]
            );
            var preview = service.Preview("ws", new(bundle, new Dictionary<string, string> { { "source", "target" } }));
            Assert.False(preview.CanImport);
            Assert.Contains(preview.Items, i => i.Kind == "regression-fixture" && i.Status == "conflict");
        }
    }

    [Fact]
    public async Task ProjectSelectionsTransferWithoutTrustOrEvaluatedCache()
    {
        var (store, service) = EmptyWorkspace();
        var path = Path.Combine(Path.GetTempPath(), "project-" + Guid.NewGuid().ToString("N") + ".csproj");
        File.WriteAllText(
            path,
            "<Project><PropertyGroup><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup></Project>"
        );
        foreach (var name in new[] { "source-project", "target-project" })
            store.Put(
                "Sources",
                "ws:" + name,
                "ws",
                new Schemora.Workspaces.Discovery.DiscoveredSource(name, path, "csharpProject", "hash", DateTimeOffset.UtcNow, [])
            );
        store.Put("UserSettings", "ef-map:ws:source-project", "ws", new EfProjectMapping("source-project", "target"));
        store.Put(
            "UserSettings",
            CSharpProjectEndpoints.Key("ws", "source-project"),
            "ws",
            new CSharpProjectSettings("source-project", "Release", "net10.0", true)
        );
        store.Put(
            "UserSettings",
            CSharpProjectEndpoints.Key("ws", "source-project") + ":trusted",
            "ws",
            new CSharpTrustedContextCache("old", "old", new(new(path, "Release", "net10.0"), [], [], [], false, "old"))
        );
        var bundle = service.Export("ws");
        var project = Assert.Single(bundle.Projects!, p => p.Id == "source-project");
        bundle = bundle with { Projects = [project] };
        Assert.Equal("Release", project.Configuration);
        Assert.Equal("net10.0", project.TargetFramework);
        var json = System.Text.Json.JsonSerializer.Serialize(bundle, CatalogStore.Json);
        Assert.DoesNotContain("trustedEvaluation", json);
        Assert.DoesNotContain("staticFingerprint", json);
        var request = new ImportConfigurationRequest(
            bundle,
            new Dictionary<string, string> { { "target", "target" } },
            new Dictionary<string, string> { { "source-project", "target-project" } }
        );
        var invalid = request with { Manifest = bundle with { Projects = [project with { Configuration = "$(Danger)" }] } };
        Assert.False(service.Preview("ws", invalid).CanImport);
        var preview = service.Preview("ws", request);
        Assert.True(preview.CanImport);
        var altered = request with
        {
            ProjectMappings = new Dictionary<string, string> { { "source-project", "source-project" } },
            PreviewRevision = preview.Revision,
        };
        var mappingError = await Assert.ThrowsAsync<AppException>(() => service.Import("ws", altered, default));
        Assert.Equal("CONFIGURATION_PREVIEW_REQUIRED", mappingError.Code);
        await service.Import("ws", request with { PreviewRevision = preview.Revision }, default);
        var settings = store.Get<CSharpProjectSettings>("UserSettings", CSharpProjectEndpoints.Key("ws", "target-project"))!;
        Assert.Equal("Release", settings.Configuration);
        Assert.Equal("net10.0", settings.TargetFramework);
        Assert.False(settings.TrustedEvaluation);
        Assert.Null(store.Get<CSharpTrustedContextCache>("UserSettings", CSharpProjectEndpoints.Key("ws", "target-project") + ":trusted"));
    }

    private static (CatalogStore Store, PortableConfigurationService Service) EmptyWorkspace()
    {
        var store = new CatalogStore(Path.Combine(Path.GetTempPath(), "portable-focused-" + Guid.NewGuid().ToString("N") + ".sqlite"));
        store.Put("Workspaces", "ws", null, new Workspace("ws", "Test", [], [], DateTimeOffset.UtcNow));
        store.Put("Databases", "target", "ws", new DatabaseInfo("target", "ws", "DB", "test.sql", "sql", "ready", null, null));
        var relationships = new RelationshipService(store, new RelationshipAnalyzer());
        return (
            store,
            new PortableConfigurationService(
                store,
                new RecipeService(store, relationships, new RecipeValidator(), new SqlRecipeRenderer()),
                relationships,
                new ManualRelationService(store, relationships)
            )
        );
    }

    [Fact]
    public void SingleDatabaseImplicitProjectBindingPreservesSafeCompilationSelection()
    {
        var (store, service) = EmptyWorkspace();
        store.Put(
            "Sources",
            "ws:implicit-project",
            "ws",
            new Schemora.Workspaces.Discovery.DiscoveredSource(
                "implicit-project",
                "implicit.csproj",
                "csharpProject",
                "hash",
                DateTimeOffset.UtcNow,
                []
            )
        );
        store.Put(
            "UserSettings",
            CSharpProjectEndpoints.Key("ws", "implicit-project"),
            "ws",
            new CSharpProjectSettings("implicit-project", "Release", "net10.0", true)
        );
        Assert.Null(store.Get<EfProjectMapping>("UserSettings", "ef-map:ws:implicit-project"));
        var project = Assert.Single(service.Export("ws").Projects!);
        Assert.Equal("implicit-project", project.Id);
        Assert.Equal("target", project.DatabaseId);
        Assert.Equal("Release", project.Configuration);
        Assert.Equal("net10.0", project.TargetFramework);
        store.Put("Databases", "second", "ws", new DatabaseInfo("second", "ws", "Second", "second.sql", "sql", "ready", null, null));
        Assert.Empty(service.Export("ws").Projects!);
    }
}
