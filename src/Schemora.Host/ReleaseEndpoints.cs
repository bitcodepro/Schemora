using System.Text.Json;
using Schemora.RecipeEngine;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record PortableDatabase(string Id, string Name, string SourceKind);

public sealed record ConfigurationBundle(
    int FormatVersion,
    string Name,
    IReadOnlyList<string> RootNames,
    IReadOnlyList<string> Excludes,
    IReadOnlyList<PortableDatabase> Databases,
    IReadOnlyList<RecipeDefinition> Recipes,
    string? Theme,
    IReadOnlyList<PortableAssertion>? Assertions = null,
    IReadOnlyList<PortableProject>? Projects = null,
    IReadOnlyList<PortableAlias>? Aliases = null,
    IReadOnlyList<PortableRegressionFixture>? RegressionFixtures = null
);

public sealed record ImportConfigurationRequest(
    ConfigurationBundle Manifest,
    IReadOnlyDictionary<string, string> DatabaseMappings,
    IReadOnlyDictionary<string, string>? ProjectMappings = null,
    string? PreviewRevision = null
);

public static class ReleaseEndpoints
{
    public static void MapReleaseEndpoints(this WebApplication app, string dataDirectory)
    {
        app.MapGet("/api/workspaces/{id}/configuration/v2", (string id, PortableConfigurationService service) => service.Export(id));
        app.MapPost(
            "/api/workspaces/{id}/configuration/preview",
            (string id, ImportConfigurationRequest request, PortableConfigurationService service) => service.Preview(id, request)
        );
        app.MapPost(
            "/api/workspaces/{id}/configuration/import-v2",
            (string id, ImportConfigurationRequest request, PortableConfigurationService service, CancellationToken ct) =>
                service.Import(id, request, ct)
        );
        app.MapGet(
            "/api/workspaces/{id}/configuration/pending",
            (string id, PortableConfigurationService service, CatalogStore store) =>
            {
                WorkspaceEndpoints.RequireWorkspace(store, id);
                return service.Pending(id);
            }
        );
        app.MapPost(
            "/api/workspaces/{id}/configuration/pending/{pendingId}",
            async (
                string id,
                string pendingId,
                PortableReviewRequest request,
                PortableConfigurationService service,
                CancellationToken ct
            ) =>
            {
                await service.Review(id, pendingId, request.Apply, ct);
                return Results.Ok();
            }
        );
        app.MapGet(
            "/api/storage",
            (CatalogStore store, JobQueue jobs) =>
                new
                {
                    DataDirectory = dataDirectory,
                    CatalogPath = store.Path,
                    CatalogVersion = 2,
                    ActiveJobs = jobs.List().Count(j => j.Status is "queued" or "running"),
                    RecoveryCommand = "Schemora.Host.exe --data-dir <data-directory> --restore-backup <backup.sqlite>",
                }
        );
        app.MapPost(
            "/api/storage/backup",
            (CatalogStore store) =>
            {
                var directory = Path.Combine(dataDirectory, "backups");
                Directory.CreateDirectory(directory);
                var name = "schemora-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".sqlite";
                var path = Path.Combine(directory, name);
                store.Backup(path);
                return Results.File(path, "application/vnd.sqlite3", name);
            }
        );
        app.MapPost(
            "/api/application/shutdown",
            (HttpContext context, IHostApplicationLifetime lifetime) =>
            {
                context.Response.OnCompleted(() =>
                {
                    lifetime.StopApplication();
                    return Task.CompletedTask;
                });
                return Results.Ok(new { Status = "stopping" });
            }
        );
        app.MapGet(
            "/api/workspaces/{id}/configuration",
            (string id, CatalogStore store, RecipeService recipes) =>
            {
                var catalog = store.Catalog(id);
                var theme = store.Get<SettingValue>("UserSettings", "theme")?.Value;
                return new ConfigurationBundle(
                    1,
                    catalog.Workspace.Name,
                    catalog.Workspace.Roots.Select(p => Path.GetFileName(Path.TrimEndingDirectorySeparator(p))).ToArray(),
                    catalog.Workspace.Excludes,
                    catalog.Databases.Select(d => new PortableDatabase(d.Id, d.Name, d.SourceKind)).ToArray(),
                    recipes.List(id).Select(r => r.Definition with { Dependencies = null }).ToArray(),
                    theme is { ValueKind: JsonValueKind.String } ? theme.Value.GetString() : null
                );
            }
        );
        app.MapPost(
            "/api/workspaces/{id}/configuration/import",
            async (string id, ImportConfigurationRequest request, CatalogStore store, RecipeService recipes, CancellationToken ct) =>
            {
                WorkspaceEndpoints.RequireWorkspace(store, id);
                if (
                    request.Manifest.FormatVersion != 1
                    || request.Manifest.Recipes.Count > 100
                    || request.Manifest.Databases.Count > 100
                    || request.DatabaseMappings.Count > 100
                )
                    throw new AppException(
                        "CONFIGURATION_INVALID",
                        "Manifest v1 is supported: up to 100 databases and recipes per import."
                    );
                var target = store.All<DatabaseInfo>("Databases", id).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var databaseId in request.Manifest.Recipes.SelectMany(r => r.DatabaseIds).Distinct())
                    if (!request.DatabaseMappings.TryGetValue(databaseId, out var mapped) || !target.Contains(mapped))
                        throw new AppException(
                            "CONFIGURATION_MAPPING_REQUIRED",
                            "First scan the root folders and explicitly map each source database to a database in the current workspace."
                        );
                var results = new List<object>();
                foreach (var definition in request.Manifest.Recipes)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var imported = await recipes.Save(
                            id,
                            definition with
                            {
                                DatabaseIds = definition.DatabaseIds.Select(old => request.DatabaseMappings[old]).Distinct().ToArray(),
                            },
                            null,
                            "Portable configuration import",
                            ct
                        );
                        results.Add(
                            new
                            {
                                Name = definition.Name,
                                Id = imported.Definition.Id,
                                Status = imported.Validation.Status,
                            }
                        );
                    }
                    catch (AppException ex)
                    {
                        results.Add(
                            new
                            {
                                Name = definition.Name,
                                Id = (string?)null,
                                Status = "failed",
                                ex.Code,
                                ex.Message,
                            }
                        );
                    }
                    catch (RecipeEngineException ex)
                    {
                        results.Add(
                            new
                            {
                                Name = definition.Name,
                                Id = (string?)null,
                                Status = "failed",
                                ex.Code,
                                ex.Message,
                            }
                        );
                    }
                }
                return new
                {
                    Results = results,
                    RootSelectionRequired = false,
                    Note = "The current workspace's root folders were preserved; recipe definitions were imported with explicit database mappings.",
                };
            }
        );
    }
}

public sealed record PortableReviewRequest(bool Apply);
