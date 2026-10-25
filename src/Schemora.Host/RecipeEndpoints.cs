using Schemora.RecipeEngine;
using Schemora.Workspaces;

namespace Schemora.Host;

public static class RecipeEndpoints
{
    public static void MapRecipeEndpoints(this WebApplication app)
    {
        app.MapPost("/api/inputs/parse", (ParseIdsRequest request, IdProcessor processor) => processor.Parse(request.Text, request.Input));
        app.MapGet("/api/workspaces/{id}/recipes", (string id, RecipeService service) => service.List(id));
        app.MapPost(
            "/api/workspaces/{id}/recipes",
            (string id, RecipeDefinition request, RecipeService service, CancellationToken ct) =>
                service.Save(id, request, null, "Recipe creation", ct)
        );
        app.MapPost(
            "/api/workspaces/{id}/recipes/import",
            (string id, ImportRecipeRequest request, RecipeService service, CancellationToken ct) =>
                service.Save(id, request.Recipe, null, "Recipe definition import", ct)
        );
        app.MapGet("/api/recipes/{id}", (string id, RecipeService service) => service.Require(id));
        app.MapPut(
            "/api/recipes/{id}",
            (string id, RecipeDefinition request, RecipeService service, CancellationToken ct) =>
                service.Save(service.Require(id).WorkspaceId, request, id, "Recipe edit", ct)
        );
        app.MapPost(
            "/api/recipes/{id}/clone",
            (string id, CloneRecipeRequest request, RecipeService service, CancellationToken ct) => service.Clone(id, request.Name, ct)
        );
        app.MapGet(
            "/api/recipes/{id}/versions/{version:int}/comparison",
            (string id, int version, RecipeService service, CancellationToken ct) => service.CompareVersion(id, version, ct)
        );
        app.MapGet("/api/recipes/{id}/versions", (string id, RecipeService service) => service.Versions(id));
        app.MapPost(
            "/api/recipes/{id}/rollback",
            (string id, RollbackRecipeRequest request, RecipeService service, CancellationToken ct) =>
                service.Rollback(id, request.Version, request.ExpectedVersion, ct)
        );
        app.MapPost("/api/recipes/{id}/validate", (string id, RecipeService service, CancellationToken ct) => service.Revalidate(id, ct));
        app.MapPost(
            "/api/recipes/{id}/render",
            (string id, RenderRecipeRequest request, RecipeService service, CancellationToken ct) =>
                service.Render(
                    id,
                    request.InputText,
                    request.Mode,
                    request.Strategy,
                    request.AcknowledgeReview,
                    ct,
                    request.ExpectedVersion
                )
        );
        app.MapPost("/api/recipes/{id}/tests", (string id, RecipeService service, CancellationToken ct) => service.SyntheticTest(id, ct));
        app.MapGet("/api/recipes/{id}/regression-fixtures", (string id, RecipeService service) => service.RegressionFixtures(id));
        app.MapPost(
            "/api/recipes/{id}/regression-fixtures",
            (string id, SaveRegressionRequest request, RecipeService service) => service.SaveRegression(id, request)
        );
        app.MapDelete(
            "/api/recipes/{id}/regression-fixtures/{fixtureId}",
            (string id, string fixtureId, int expectedVersion, RecipeService service) =>
            {
                service.DeleteRegression(id, fixtureId, expectedVersion);
                return Results.NoContent();
            }
        );
        app.MapGet("/api/recipes/{id}/regression-runs", (string id, RecipeService service) => service.RegressionRuns(id));
        app.MapPost(
            "/api/recipes/{id}/regression-runs",
            (string id, RecipeService service, CancellationToken ct) => service.RunRegressions(id, ct)
        );
        app.MapGet(
            "/api/recipes/{id}/export",
            (string id, RecipeService service, CatalogStore store) =>
            {
                var entry = service.Require(id);
                return new
                {
                    FormatVersion = 1,
                    Recipe = entry.Definition with
                    {
                        Dependencies = null,
                    },
                    DatabaseReferences = entry.Definition.DatabaseIds.Select(dbId => new
                    {
                        Id = dbId,
                        Name = store.Get<DatabaseInfo>("Databases", dbId)?.Name,
                    }),
                };
            }
        );
        app.MapGet(
            "/api/recipes/{id}/lineage",
            async (string id, RecipeService service, RelationshipService relationships, CancellationToken ct) =>
            {
                var entry = service.Require(id);
                var validation = await service.Revalidate(id, ct);
                var graph = await relationships.Get(entry.WorkspaceId, ct);
                return validation.Dependencies.Select(d =>
                {
                    var table = graph.Tables.FirstOrDefault(t => t.Table.Id == d.TableId);
                    var view = (graph.ReadableObjects ?? []).FirstOrDefault(o => o.Object.Id == d.TableId);
                    return new
                    {
                        d.DatabaseId,
                        DatabaseName = table?.DatabaseName ?? view?.DatabaseName,
                        d.TableId,
                        Schema = table?.Table.Schema ?? view?.Object.Schema,
                        TableName = table?.Table.Name ?? view?.Object.Name,
                        d.ColumnName,
                        d.RelationId,
                    };
                });
            }
        );
        app.MapPost(
            "/api/recipes/{id}/archive",
            (string id, ArchiveRecipeRequest request, RecipeService service) => service.Archive(id, request.Archived)
        );
    }
}

public sealed record ParseIdsRequest(string Text, RecipeInputDefinition Input);

public sealed record ImportRecipeRequest(RecipeDefinition Recipe);

public sealed record CloneRecipeRequest(string? Name);

public sealed record RollbackRecipeRequest(int Version, int ExpectedVersion);

public sealed record RenderRecipeRequest(
    IReadOnlyDictionary<string, string> InputText,
    string Mode = "standalone",
    string Strategy = "auto",
    bool AcknowledgeReview = false,
    int? ExpectedVersion = null
);

public sealed record ArchiveRecipeRequest(bool Archived);
