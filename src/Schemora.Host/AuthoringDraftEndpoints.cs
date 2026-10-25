using Schemora.RecipeEngine;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record AuthoringDraft(RecipeDefinition Definition, bool Imported, DateTimeOffset SavedAt);

public static class AuthoringDraftEndpoints
{
    public static void MapAuthoringDrafts(this WebApplication app)
    {
        app.MapGet(
            "/api/workspaces/{id}/authoring-draft",
            (string id, CatalogStore store) =>
            {
                WorkspaceEndpoints.RequireWorkspace(store, id);
                return new { Draft = store.Get<AuthoringDraft>("UserSettings", "authoring-draft:" + id) };
            }
        );
        app.MapPut(
            "/api/workspaces/{id}/authoring-draft",
            (string id, AuthoringDraft request, CatalogStore store) =>
            {
                WorkspaceEndpoints.RequireWorkspace(store, id);
                var definition = request.Definition;
                if (
                    definition is null
                    || definition.SqlTemplate is null
                    || definition.SqlTemplate.Length > 512 * 1024
                    || definition.Name is null
                    || definition.Name.Length > 160
                    || definition.Inputs is null
                    || definition.Inputs.Count > 100
                    || definition.DatabaseIds is null
                    || definition.Tags is null
                )
                    throw new AppException("DRAFT_INVALID", "Invalid authoring draft.");
                // Typed authoring fields only. Runtime input, rendered SQL and bound parameters have no storage path.
                var draft = request with
                {
                    SavedAt = DateTimeOffset.UtcNow,
                };
                store.Put("UserSettings", "authoring-draft:" + id, id, draft);
                return draft;
            }
        );
        app.MapDelete(
            "/api/workspaces/{id}/authoring-draft",
            (string id, CatalogStore store) =>
            {
                WorkspaceEndpoints.RequireWorkspace(store, id);
                store.Delete("UserSettings", "authoring-draft:" + id);
                return Results.NoContent();
            }
        );
    }
}
