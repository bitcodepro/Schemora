using System.Text;
using Schemora.RecipeEngine;
using Schemora.Workspaces;

namespace Schemora.Host;

public static class ProductExperienceEndpoints
{
    private static object[] NavigationObjects(CatalogStore store, string databaseId)
    {
        var graph = store.CurrentGraph(databaseId);
        return (graph?.Tables ?? [])
            .Select(t =>
                (object)
                    new
                    {
                        t.Id,
                        t.Schema,
                        t.Name,
                        Kind = "table",
                    }
            )
            .Concat(
                (graph?.Objects ?? []).Select(o =>
                    (object)
                        new
                        {
                            o.Id,
                            o.Schema,
                            o.Name,
                            o.Kind,
                        }
                )
            )
            .ToArray();
    }

    public static void MapProductExperience(this WebApplication app, string dataDirectory)
    {
        app.MapAuthoringDrafts();
        app.MapCatalogCare(dataDirectory);
        app.MapGet(
            "/api/workspaces/{id}/search",
            (string id, string? query, string? kind, int? offset, int? limit, string? databaseId, CatalogStore store) =>
                WorkspaceSearch.Read(store, id, query ?? "", kind, offset ?? 0, limit ?? 50, databaseId)
        );
        app.MapDelete(
            "/api/workspaces/{id}/recipe-collections/{collectionId}",
            (string id, string collectionId, int expectedVersion, CatalogStore store) =>
                store.WithWriteLock(() =>
                {
                    var settings = RecipeCollections.Read(store, id);
                    var collection =
                        settings.Collections.FirstOrDefault(c => c.Id == collectionId)
                        ?? throw new AppException("COLLECTION_NOT_FOUND", "Collection not found.", 404);
                    if (collection.Version != expectedVersion)
                        throw new AppException("COLLECTION_CHANGED", "Reload the collection before deleting.", 409);
                    store.Put(
                        "UserSettings",
                        "recipe-collections:" + id,
                        id,
                        new CollectionSettings(settings.Collections.Where(c => c.Id != collectionId).ToArray())
                    );
                    return Results.NoContent();
                })
        );
        app.MapGet("/api/workspaces/{id}/recipe-collections", (string id, CatalogStore store) => RecipeCollections.Read(store, id));
        app.MapPut(
            "/api/workspaces/{id}/recipe-collections",
            (string id, RecipeCollection request, CatalogStore store) => RecipeCollections.Save(store, id, request)
        );
        app.MapPost(
            "/api/workspaces/{id}/recipe-collections/{collectionId}/render",
            (
                string id,
                string collectionId,
                CollectionRenderRequest request,
                CatalogStore store,
                RecipeService recipes,
                CancellationToken ct
            ) => CollectionRender.Render(store, recipes, id, collectionId, request, ct)
        );
        app.MapGet("/api/workspaces/{id}/recipe-batch", (string id, CatalogStore store) => RecipeCollections.History(store, id));
        app.MapPost(
            "/api/workspaces/{id}/recipe-batch",
            (
                string id,
                BatchRecipeRequest request,
                CatalogStore store,
                RecipeService recipes,
                RelationshipService relationships,
                CancellationToken ct
            ) => RecipeCollections.Check(store, recipes, relationships, id, request, ct)
        );
        app.MapGet(
            "/api/workspaces/{id}/provenance",
            (string id, CatalogStore store, Schemora.Workspaces.Discovery.WorkspaceDiscovery discovery, CancellationToken ct) =>
                AnalysisProvenance.Report(store, id, discovery, ct)
        );
        app.MapGet("/api/diagnostics/docker", () => DockerFailureDiagnostics.Read(dataDirectory));
        app.MapGet(
            "/api/workspaces/{id}/quality/rules",
            (string id, CatalogStore store) => new { Policy = QualityRules.Read(store, id), Definitions = QualityRules.Definitions }
        );
        app.MapPut(
            "/api/workspaces/{id}/quality/rules",
            (string id, QualityPolicy request, CatalogStore store) => QualityRules.Save(store, id, request)
        );
        app.MapGet(
            "/api/workspaces/{id}/quality",
            async (string id, CatalogStore store, RelationshipService relationships, CancellationToken ct) =>
                SchemaQuality.Evaluate(store, id, await relationships.Get(id, ct))
        );
        app.MapPost(
            "/api/workspaces/{id}/quality/review",
            async (
                string id,
                SuppressQualityRequest request,
                CatalogStore store,
                RelationshipService relationships,
                CancellationToken ct
            ) =>
            {
                SchemaQuality.Suppress(store, id, SchemaQuality.Evaluate(store, id, await relationships.Get(id, ct)), request);
                return Results.NoContent();
            }
        );
        app.MapGet(
            "/api/workspaces/{id}/analysis-coverage",
            async (string id, CatalogStore store, RelationshipService relationships, CancellationToken ct) =>
            {
                var catalog = store.Catalog(id);
                var graph = await relationships.Get(id, ct);
                var projects = catalog
                    .Sources.Where(p => p.Kind == "csharpProject")
                    .Select(p =>
                    {
                        var settings = store.Get<CSharpProjectSettings>("UserSettings", CSharpProjectEndpoints.Key(id, p.Id)) ?? new(p.Id);
                        var context = CSharpProjectEndpoints.ReadContext(store, id, p.Path, settings, ct);
                        var included = context.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var prefix = Path.GetDirectoryName(p.Path)! + Path.DirectorySeparatorChar;
                        var excluded = catalog
                            .Sources.Where(f =>
                                f.Kind == "csharp"
                                && f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                && !included.Contains(f.Path)
                            )
                            .Select(f => f.Path)
                            .ToArray();
                        return new
                        {
                            p.Id,
                            p.Path,
                            settings.Configuration,
                            settings.TargetFramework,
                            context.IsComplete,
                            IncludedFiles = included.Order().ToArray(),
                            ExcludedFiles = excluded,
                            context.Diagnostics,
                        };
                    })
                    .ToArray();
                return new
                {
                    Projects = projects,
                    Diagnostics = graph
                        .Diagnostics.Where(d => d.Code.StartsWith("DAPPER_") || d.Code.StartsWith("EF_") || d.Code.StartsWith("CSHARP_"))
                        .Distinct(),
                    DapperEvidence = graph.Relations.SelectMany(r => r.Evidence).Where(e => e.Kind == "dapper-sql").Distinct().Count(),
                    EfEvidence = graph.Relations.SelectMany(r => r.Evidence).Where(e => e.Kind.StartsWith("ef")).Distinct().Count(),
                };
            }
        );
        app.MapGet(
            "/api/workspaces/{id}/dependency-paths",
            (string id, string objectId, string? query, int? offset, int? limit, CatalogStore store) =>
                DependencyExplorer.Read(store, id, objectId, query, offset ?? 0, limit ?? 25)
        );
        app.MapGet(
            "/api/workspaces/{id}/documentation",
            async (
                string id,
                string? language,
                bool? objects,
                bool? relations,
                bool? evidence,
                bool? recipes,
                CatalogStore store,
                RelationshipService relationships,
                CancellationToken ct
            ) =>
                Results.File(
                    Encoding.UTF8.GetBytes(
                        SchemaDocumentation.Export(
                            store,
                            id,
                            evidence == false ? null : await relationships.Get(id, ct),
                            new(language ?? "en", objects ?? true, relations ?? true, evidence ?? true, recipes ?? false)
                        )
                    ),
                    "text/html; charset=utf-8",
                    "schemora-schema.html"
                )
        );
        app.MapGet(
            "/api/workspaces/{id}/recipe-dependencies",
            (string id, bool? includeRecipes, CatalogStore store, RecipeService recipes) =>
            {
                var catalog = store.Catalog(id);
                return new
                {
                    Databases = catalog.Databases.Select(db => new
                    {
                        db.Id,
                        db.Name,
                        db.SourcePath,
                        Objects = NavigationObjects(store, db.Id),
                    }),
                    Recipes = includeRecipes == false
                        ? Array.Empty<object>()
                        : recipes
                            .List(id)
                            .Select(r =>
                                (object)
                                    new
                                    {
                                        Id = r.Definition.Id,
                                        r.Definition.Name,
                                        r.Validation.Status,
                                        Dependencies = r.Validation.Dependencies,
                                    }
                            )
                            .ToArray(),
                };
            }
        );
        app.MapPost(
            "/api/tutorial",
            async (CatalogStore store, WorkspaceScanner scanner, RecipeService recipes, CancellationToken ct) =>
            {
                var id = Guid.NewGuid().ToString("N");
                var directory = Path.Combine(dataDirectory, "tutorials", id);
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(
                    Path.Combine(directory, "Schema.sql"),
                    "CREATE TABLE dbo.Customers(CustomerId int NOT NULL,Name nvarchar(100) NOT NULL,CreatedAt date NOT NULL,CONSTRAINT PK_Customers PRIMARY KEY(CustomerId));\nGO\nCREATE TABLE dbo.Orders(OrderId int NOT NULL,CustomerId int NOT NULL,CONSTRAINT PK_Orders PRIMARY KEY(OrderId),CONSTRAINT FK_Orders_Customers FOREIGN KEY(CustomerId) REFERENCES dbo.Customers(CustomerId));\nGO\nCREATE INDEX IX_Orders_CustomerId ON dbo.Orders(CustomerId);",
                    ct
                );
                await File.WriteAllTextAsync(
                    Path.Combine(directory, "Tutorial.sqlproj"),
                    "<Project><Sdk Name=\"Microsoft.Build.Sql\" Version=\"2.3.0\"/><PropertyGroup><Name>SchemoraTutorial</Name><DSP>Microsoft.Data.Tools.Schema.Sql.Sql160DatabaseSchemaProvider</DSP></PropertyGroup></Project>",
                    ct
                );
                var workspace = new Workspace(id, "Schemora tutorial", [directory], [], DateTimeOffset.UtcNow);
                store.Put("Workspaces", id, id, workspace);
                await scanner.Scan(workspace, "full", null, false, (_, _) => { }, ct);
                var database = store.Catalog(id).Databases.Single();
                var recipe = await recipes.Save(
                    id,
                    new(
                        "",
                        "Orders for customers",
                        "Artificial tutorial",
                        [],
                        "sqlserver",
                        [database.Id],
                        0,
                        "SELECT o.OrderId, o.CustomerId\nFROM dbo.Orders AS o\nWHERE o.CustomerId IN ({{ids:Ids}});",
                        [new("Ids", "int")]
                    ),
                    null,
                    "Tutorial",
                    ct
                );
                // A second recipe shows single-value inputs: a date range instead of an ID list.
                await recipes.Save(
                    id,
                    new(
                        "",
                        "Period: new customers",
                        "Artificial tutorial",
                        [],
                        "sqlserver",
                        [database.Id],
                        0,
                        "SELECT c.CustomerId, c.Name, c.CreatedAt\nFROM dbo.Customers AS c\nWHERE c.CreatedAt >= {{value:From}}\n  AND c.CreatedAt < {{value:To}};",
                        [new("From", "date"), new("To", "date")]
                    ),
                    null,
                    "Tutorial",
                    ct
                );
                return new
                {
                    Workspace = workspace,
                    Database = database,
                    RecipeId = recipe.Definition.Id,
                };
            }
        );
    }
}
