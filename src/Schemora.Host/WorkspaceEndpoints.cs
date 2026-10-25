using System.Text.Json;
using Schemora.SqlServer;
using Schemora.Workspaces;

namespace Schemora.Host;

public static class WorkspaceEndpoints
{
    public static void MapWorkspaceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/workspaces", (CatalogStore store) => store.All<Workspace>("Workspaces"));
        app.MapPost(
            "/api/workspaces",
            (WorkspaceRequest request, CatalogStore store) =>
            {
                var workspace = Make(request, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
                Save(workspace, store);
                return Results.Created("/api/workspaces/" + workspace.Id, workspace);
            }
        );
        app.MapPut(
            "/api/workspaces/{id}",
            (string id, WorkspaceRequest request, CatalogStore store, JobQueue queue) =>
            {
                var prior = RequireWorkspace(store, id);
                if (queue.IsActive(id))
                    throw new AppException("WORKSPACE_BUSY", "Wait for the job to finish or cancel it before changing root folders.", 409);
                var workspace = Make(request, id, prior.CreatedAt);
                Save(workspace, store);
                return workspace;
            }
        );
        app.MapGet(
            "/api/workspaces/{id}/catalog",
            (string id, CatalogStore store) =>
            {
                RequireWorkspace(store, id);
                return store.Catalog(id);
            }
        );
        app.MapPost(
            "/api/workspaces/{id}/scan",
            (string id, ScanRequest request, CatalogStore store, JobQueue queue) =>
                queue.Enqueue(RequireWorkspace(store, id), request.Mode, request.DatabaseId, request.TrustProjects)
        );
        app.MapGet(
            "/api/workspaces/{id}/schema-state",
            (string id, CatalogStore store) =>
            {
                RequireWorkspace(store, id);
                return new { Databases = store.All<DatabaseInfo>("Databases", id) };
            }
        );
        app.MapGet(
            "/api/databases/{id}/schema",
            (string id, CatalogStore store) =>
                store.CurrentGraph(id)
                ?? throw new AppException("SCHEMA_NOT_FOUND", "The database has no published schema yet. Check the scan diagnostics.", 404)
        );
        app.MapGet("/api/databases/{id}/snapshots", (string id, CatalogStore store) => store.Snapshots(id));
        app.MapGet(
            "/api/schema/snapshots/{id}",
            (string id, CatalogStore store) =>
                store.Get<SnapshotRecord>("SchemaSnapshots", id)?.Graph
                ?? throw new AppException("SNAPSHOT_NOT_FOUND", "Snapshot not found.", 404)
        );
        app.MapGet("/api/jobs", (JobQueue queue) => queue.List());
        app.MapGet(
            "/api/jobs/{id}",
            (string id, JobQueue queue) => queue.Get(id) ?? throw new AppException("JOB_NOT_FOUND", "Job not found.", 404)
        );
        app.MapDelete(
            "/api/jobs/{id}",
            (string id, JobQueue queue) =>
            {
                if (queue.Get(id) is null)
                    throw new AppException("JOB_NOT_FOUND", "Job not found.", 404);
                queue.Cancel(id);
                return Results.Accepted();
            }
        );
        app.MapGet(
            "/api/jobs/{id}/events",
            async (string id, HttpContext context, JobQueue queue) =>
            {
                if (queue.Get(id) is null)
                    throw new AppException("JOB_NOT_FOUND", "Job not found.", 404);
                context.Response.ContentType = "text/event-stream";
                string? previous = null;
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    var job = queue.Get(id)!;
                    var json = JsonSerializer.Serialize(job, CatalogStore.Json);
                    if (json != previous)
                    {
                        await context.Response.WriteAsync("event: progress\ndata: " + json + "\n\n", context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                        previous = json;
                    }
                    if (job.Status is not ("queued" or "running"))
                        break;
                    await Task.Delay(300, context.RequestAborted);
                }
            }
        );
        app.MapGet(
            "/api/workspaces/{id}/manifest",
            (string id, CatalogStore store) =>
            {
                var workspace = RequireWorkspace(store, id);
                // Machine paths require deliberate re-selection on another machine.
                return new
                {
                    FormatVersion = 1,
                    workspace.Name,
                    RootNames = workspace.Roots.Select(p => new DirectoryInfo(p).Name),
                    workspace.Excludes,
                };
            }
        );
    }

    public static Workspace RequireWorkspace(CatalogStore store, string id) =>
        store.Get<Workspace>("Workspaces", id) ?? throw new AppException("WORKSPACE_NOT_FOUND", "Workspace not found.", 404);

    private static Workspace Make(WorkspaceRequest request, string id, DateTimeOffset createdAt)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 160)
            throw new AppException("WORKSPACE_NAME_INVALID", "Enter a name of up to 160 characters.");
        if (request.Roots is null || request.Roots.Count is < 1 or > 20)
            throw new AppException("WORKSPACE_ROOTS_INVALID", "Specify 1 to 20 local root folders.");
        var roots = request
            .Roots.Select(input =>
            {
                if (!Path.IsPathFullyQualified(input))
                    throw new AppException("LOCAL_ROOT_REQUIRED", "An absolute path to a local folder is required.");
                var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
                if (path.StartsWith(@"\\") || new Uri(path).IsUnc || !Directory.Exists(path))
                    throw new AppException("LOCAL_ROOT_REQUIRED", "The folder was not found or the path is a network path.");
                LocalFiles.CheckNoLinks(path);
                return path;
            })
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
        var excludes = (request.Excludes ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (excludes.Length > 100 || excludes.Any(s => s.Length > 500))
            throw new AppException("EXCLUDES_TOO_LARGE", "At most 100 exclusion patterns of up to 500 characters each.");
        return new(id, name, roots, excludes, createdAt);
    }

    private static void Save(Workspace workspace, CatalogStore store) =>
        store.Batch(
            new[] { new StoredRecord("Workspaces", workspace.Id, workspace.Id, workspace) }.Concat(
                workspace.Roots.Select(
                    (path, i) =>
                        new StoredRecord("Roots", workspace.Id + ":" + i, workspace.Id, new { WorkspaceId = workspace.Id, Path = path })
                )
            ),
            [("Roots", workspace.Id)]
        );
}

public sealed record WorkspaceRequest(string? Name, IReadOnlyList<string>? Roots, IReadOnlyList<string>? Excludes);

public sealed record ScanRequest(string Mode, string? DatabaseId = null, bool TrustProjects = false);
