using System.Text.RegularExpressions;
using Schemora.Relationships;
using Schemora.Workspaces;
using Schemora.Workspaces.Discovery;

namespace Schemora.Host;

public sealed record CSharpProjectSettings(
    string ProjectId,
    string Configuration = "Debug",
    string? TargetFramework = null,
    bool TrustedEvaluation = false
);

public sealed record CSharpTrustedContextCache(string StaticFingerprint, string InputFingerprint, CSharpProjectContext Context);

public static class CSharpProjectEndpoints
{
    public static string Key(string workspaceId, string projectId) => "csharp-context:" + workspaceId + ":" + projectId;

    public static void MapCSharpProjectEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/api/workspaces/{id}/csharp-context",
            (string id, CatalogStore store) =>
            {
                WorkspaceEndpoints.RequireWorkspace(store, id);
                return store
                    .All<DiscoveredSource>("Sources", id)
                    .Where(p => p.Kind == "csharpProject")
                    .Select(p => new
                    {
                        ProjectId = p.Id,
                        p.Path,
                        Settings = store.Get<CSharpProjectSettings>("UserSettings", Key(id, p.Id)) ?? new(p.Id),
                    });
            }
        );
        app.MapPut(
            "/api/workspaces/{id}/csharp-context",
            async (string id, CSharpProjectSettings settings, CatalogStore store, RelationshipService service, CancellationToken ct) =>
            {
                var project = Validate(id, settings, store);
                if (settings.TrustedEvaluation)
                {
                    var context = await Evaluate(project.Path, settings, ct);
                    var baseline = new CSharpProjectContextEvaluator().Evaluate(context.Selection, ct);
                    store.Put(
                        "UserSettings",
                        Key(id, settings.ProjectId) + ":trusted",
                        id,
                        new CSharpTrustedContextCache(baseline.Fingerprint, InputFingerprint(context), context)
                    );
                }
                store.Put("UserSettings", Key(id, settings.ProjectId), id, settings);
                service.Invalidate(id);
                return settings;
            }
        );
        app.MapPost(
            "/api/workspaces/{id}/csharp-context/preview",
            async (string id, CSharpProjectSettings settings, CatalogStore store, CancellationToken ct) =>
            {
                var project = Validate(id, settings, store);
                return await Evaluate(project.Path, settings, ct);
            }
        );
    }

    public static CSharpProjectContext ReadContext(
        CatalogStore store,
        string workspaceId,
        string path,
        CSharpProjectSettings settings,
        CancellationToken ct = default
    )
    {
        var baseline = new CSharpProjectContextEvaluator().Evaluate(new(path, settings.Configuration, settings.TargetFramework), ct);
        if (!settings.TrustedEvaluation)
            return baseline;
        var cached = store.Get<CSharpTrustedContextCache>("UserSettings", Key(workspaceId, settings.ProjectId) + ":trusted");
        if (
            cached is not null
            && cached.InputFingerprint != "unavailable"
            && cached.StaticFingerprint == baseline.Fingerprint
            && cached.InputFingerprint == InputFingerprint(cached.Context)
        )
            return cached.Context;
        return baseline with
        {
            IsComplete = false,
            Diagnostics = baseline
                .Diagnostics.Append(
                    new Schemora.SqlServer.SchemaDiagnostic(
                        "warning",
                        "CSHARP_TRUST_REFRESH_REQUIRED",
                        "Project content changed or trusted context is unavailable. Explicitly save the trusted context again to evaluate MSBuild."
                    )
                )
                .ToArray(),
        };
    }

    internal static string InputFingerprint(CSharpProjectContext context)
    {
        try
        {
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            long size = 0;
            foreach (
                var path in context
                    .Files.Select(f => f.Path)
                    .Concat(context.ReferencePaths)
                    .Concat(context.ProjectInputs ?? [])
                    .Order(StringComparer.Ordinal)
            )
            {
                if (new Uri(Path.GetFullPath(path)).IsUnc)
                    return "unavailable";
                var file = new FileInfo(path);
                for (
                    FileSystemInfo? entry = file;
                    entry is not null;
                    entry = entry is FileInfo f ? f.Directory : ((DirectoryInfo)entry).Parent
                )
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        return "unavailable";
                if (!file.Exists)
                {
                    hash.AppendData(System.Text.Encoding.UTF8.GetBytes(path + "missing"));
                    continue;
                }
                if (file.Length > 32 * 1024 * 1024 || (size += file.Length) > 128 * 1024 * 1024)
                    return "unavailable";
                hash.AppendData(System.Text.Encoding.UTF8.GetBytes(path));
                using var stream = File.OpenRead(path);
                hash.AppendData(System.Security.Cryptography.SHA256.HashData(stream));
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "unavailable";
        }
    }

    public static Task<CSharpProjectContext> Evaluate(string path, CSharpProjectSettings settings, CancellationToken ct = default)
    {
        var selection = new CSharpProjectSelection(path, settings.Configuration, settings.TargetFramework);
        return settings.TrustedEvaluation
            ? new CSharpTrustedProjectEvaluator().EvaluateAsync(selection, true, ct)
            : Task.FromResult(new CSharpProjectContextEvaluator().Evaluate(selection, ct));
    }

    private static DiscoveredSource Validate(string id, CSharpProjectSettings settings, CatalogStore store)
    {
        WorkspaceEndpoints.RequireWorkspace(store, id);
        if (
            !Regex.IsMatch(settings.Configuration, @"^[A-Za-z0-9_. -]{1,64}$")
            || settings.TargetFramework is { } tfm && !Regex.IsMatch(tfm, @"^[A-Za-z0-9_.-]{1,80}$")
        )
            throw new AppException("CSHARP_SELECTION_INVALID", "Select a valid configuration and target framework.");
        return store.All<DiscoveredSource>("Sources", id).FirstOrDefault(p => p.Id == settings.ProjectId && p.Kind == "csharpProject")
            ?? throw new AppException("CSHARP_PROJECT_NOT_FOUND", "Select a discovered C# project.", 404);
    }
}
