using System.Xml;
using System.Xml.Linq;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Schemora.Workspaces.Discovery;

namespace Schemora.Host;

public sealed class WorkspaceScanner(
    CatalogStore store,
    WorkspaceDiscovery discovery,
    DacpacReader reader,
    StaticSqlReader staticReader,
    SqlSourceClassifier classifier,
    SqlProjectBuilder builder
)
{
    public const int CurrentAnalysisVersion = 2;

    private sealed record SourceGroup(string Id, string Name, string SourcePath, string Kind, IReadOnlyList<DiscoveredSource> Members);

    public async Task<ScanOutcome> Scan(
        Workspace workspace,
        string mode,
        string? selectedDatabase,
        bool trustProjects,
        Action<string, double> progress,
        CancellationToken ct
    )
    {
        if (mode is not ("quick" or "full" or "rebuild"))
            throw new AppException("SCAN_MODE_INVALID", "Выберите quick, full или rebuild.");
        if (mode == "rebuild" && !trustProjects)
            throw new AppException("PROJECT_TRUST_REQUIRED", "Пересборка выполняет код проектов. Подтвердите доверие к ним.", 403);
        var started = DateTimeOffset.UtcNow;
        var previous = store.All<DiscoveredSource>("Sources", workspace.Id);
        var previousByPath = previous.ToDictionary(
            s => s.Path,
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
        );
        progress("Обнаружение проектов и fingerprint файлов", 5);
        var discoveryCache = previousByPath.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Kind.EndsWith("Sql", StringComparison.Ordinal) ? pair.Value with { Kind = "sql" } : pair.Value,
            previousByPath.Comparer
        );
        // Rebuild captures fresh content hashes for the model it is about to compile.
        var found = await discovery.Scan(workspace.Roots, workspace.Excludes, mode == "quick" ? discoveryCache : null, ct);
        if (!found.Complete)
            throw new AppException(
                "DISCOVERY_INCOMPLETE",
                "Обнаружение не завершено; предыдущий каталог сохранён без изменений. Устраните ошибки и повторите Full Rescan. "
                    + string.Join(" | ", found.Diagnostics.Take(50)),
                422
            );
        var diagnostics = found.Diagnostics.ToList();
        var sources = found
            .Sources.Select(s =>
            {
                if (s.Kind != "sql")
                    return s;
                if (previousByPath.TryGetValue(s.Path, out var cached) && cached.Hash == s.Hash)
                    return s with { Kind = cached.Kind, Diagnostics = cached.Diagnostics.Concat(s.Diagnostics).Distinct().ToArray() };
                var classification = classifier.Inspect(s.Path);
                return s with
                {
                    Kind = classification.Kind,
                    Diagnostics = s.Diagnostics.Concat(classification.Diagnostics.Select(d => d.Message)).ToArray(),
                };
            })
            .ToArray();
        diagnostics.AddRange(sources.SelectMany(s => s.Diagnostics).Take(1000));
        var old = store.All<DatabaseInfo>("Databases", workspace.Id).ToDictionary(d => d.Id);
        var groups = Group(workspace, sources, diagnostics);
        if (selectedDatabase is not null && groups.All(g => g.Id != selectedDatabase))
            throw new AppException("DATABASE_NOT_FOUND", "Выбранная БД больше не обнаружена. Выполните Full Rescan.", 404);
        var databases = new List<DatabaseInfo>();
        var snapshots = new List<SnapshotRecord>();
        var partial = diagnostics.Count > 0;
        for (var i = 0; i < groups.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var group = groups[i];
            progress($"Анализ {group.Name} ({i + 1}/{groups.Count})", 15 + 75.0 * i / Math.Max(1, groups.Count));
            old.TryGetValue(group.Id, out var prior);
            // Bin artifacts have no verified provenance for these source fingerprints.
            // Only explicit Rebuild establishes an authoritative project model.
            var fingerprint = Identity.Hash(
                string.Join("\n", group.Members.OrderBy(s => s.Path, StringComparer.Ordinal).Select(s => s.Path + ":" + s.Hash))
            );
            var last = prior?.CurrentSnapshotId is { } priorId ? store.Get<SnapshotRecord>("SchemaSnapshots", priorId) : null;
            var reusable = last?.AnalysisVersion == CurrentAnalysisVersion && last.Fingerprint == fingerprint;
            if (
                mode != "rebuild"
                && group.Kind == "sqlProject"
                && last is not null
                && (last.Mode == "rebuild" || last.BuildManifestId is not null)
            )
            {
                var manifest = last.BuildManifestId is { } manifestId ? builder.ReadManifest(manifestId) : null;
                var check = manifest is not null ? await builder.CheckManifest(manifest, ct) : new BuildManifestCheck(false, []);
                reusable = last.AnalysisVersion == CurrentAnalysisVersion && check.Current;
                if (!check.Current)
                    diagnostics.Add(
                        group.Name
                            + ": verified build input closure changed/unavailable; authoritative model reuse was refused. Rebuild is required."
                    );
            }
            if ((selectedDatabase is not null && group.Id != selectedDatabase) || (mode != "rebuild" && reusable))
            {
                databases.Add(prior ?? new(group.Id, workspace.Id, group.Name, group.SourcePath, group.Kind, "pending", null, null));
                partial |= prior?.Status is "partial" or "failed";
                continue;
            }
            try
            {
                SchemaGraph graph;
                string? buildManifestId = null;
                if (mode == "rebuild" && group.Kind == "sqlProject")
                {
                    var built = await builder.BuildWithManifest(group.SourcePath, trustProjects, ct);
                    graph = built.Graph;
                    buildManifestId = built.Manifest.Id;
                }
                else if (group.Kind == "dacpac")
                    graph = reader.Read(LocalFiles.Validate(group.SourcePath, ".dacpac"));
                else
                {
                    graph = staticReader.Read(
                        group.Name,
                        group.Members.Where(s => s.Kind is "schemaSql" or "migrationSql" or "unknownSql").Select(s => s.Path).ToArray()
                    );
                    if (group.Kind == "sqlProject")
                        graph = graph with
                        {
                            Diagnostics = graph
                                .Diagnostics.Append(
                                    new(
                                        "warning",
                                        "BUILD_REQUIRED_FOR_AUTHORITATIVE_MODEL",
                                        "Нет подтверждённого DACPAC для текущих fingerprints. Показана частичная статическая схема; условия SQL-проекта и внешние references не вычислялись. Для авторитетной модели выполните Rebuild с доверием к проекту."
                                    )
                                )
                                .ToArray(),
                        };
                    if (group.Kind == "staticSqlGroup")
                        graph = graph with
                        {
                            Diagnostics = graph
                                .Diagnostics.Append(
                                    new(
                                        "warning",
                                        "DATABASE_MAPPING_INFERRED",
                                        "SQL-файлы сгруппированы по каталогу. Проверьте соответствие отдельной БД; выберите более узкий workspace root при неоднозначности."
                                    )
                                )
                                .ToArray(),
                        };
                }
                ct.ThrowIfCancellationRequested();
                graph = Identity.Scope(graph with { DatabaseName = group.Name, SourcePath = group.SourcePath }, group.Id);
                var status =
                    graph.Diagnostics.Any(d => d.Severity == "error") ? "partial"
                    : graph.Diagnostics.Any(d => d.Severity == "warning") ? "partial"
                    : "ready";
                partial |= status == "partial";
                var snapshot = new SnapshotRecord(
                    Guid.NewGuid().ToString("N"),
                    workspace.Id,
                    group.Id,
                    DateTimeOffset.UtcNow,
                    mode,
                    fingerprint,
                    graph,
                    buildManifestId,
                    CurrentAnalysisVersion
                );
                snapshots.Add(snapshot);
                databases.Add(
                    new(group.Id, workspace.Id, group.Name, group.SourcePath, group.Kind, status, snapshot.Id, snapshot.CreatedAt)
                );
                diagnostics.AddRange(graph.Diagnostics.Where(d => d.Severity == "error").Select(d => group.Name + ": " + d.Message));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                partial = true;
                diagnostics.Add(group.Name + ": " + ex.Message);
                // A failed Rebuild leaves an existing model in place; it should not relabel that usable model as failed.
                // The job diagnostics carry the build error.
                databases.Add(
                    prior is not null
                        ? prior with
                        {
                            Status = mode == "rebuild" && prior.CurrentSnapshotId is not null ? prior.Status : "failed",
                        }
                        : new(group.Id, workspace.Id, group.Name, group.SourcePath, group.Kind, "failed", null, null)
                );
            }
        }
        var currentIds = sources.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var changed =
            sources.Count(s => !previousByPath.TryGetValue(s.Path, out var value) || value.Hash != s.Hash)
            + previous.Count(s => !currentIds.Contains(s.Id));
        ct.ThrowIfCancellationRequested();
        progress("Транзакционная публикация каталога и snapshots", 95);
        var outcome = new ScanOutcome(
            changed,
            sources.Length - sources.Count(s => !previousByPath.TryGetValue(s.Path, out var value) || value.Hash != s.Hash),
            snapshots.Count,
            diagnostics,
            partial
        );
        var run = new ScanRun(
            Guid.NewGuid().ToString("N"),
            workspace.Id,
            mode,
            started,
            DateTimeOffset.UtcNow,
            partial ? "partial" : "completed",
            outcome.ChangedSources,
            outcome.UnchangedSources,
            outcome.DatabasesUpdated,
            diagnostics
        );
        store.Publish(
            workspace.Id,
            sources,
            databases,
            snapshots,
            run,
            workspace,
            [
                new(
                    "AnalysisReceipts",
                    run.Id,
                    workspace.Id,
                    AnalysisProvenance.Capture(store, workspace, run, sources, databases, snapshots)
                ),
            ]
        );
        return outcome;
    }

    private List<SourceGroup> Group(Workspace workspace, IReadOnlyList<DiscoveredSource> sources, List<string> diagnostics)
    {
        var result = new List<SourceGroup>();
        var projects = sources.Where(s => s.Kind == "sqlProject").OrderByDescending(s => s.Path.Length).ToArray();
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var directory = Path.GetDirectoryName(project.Path)!;
            // Shared imports may live in sibling roots; conservatively invalidate every SQL
            // project when any discovered build configuration changes. Inherited known names
            // above roots are also supplied by discovery, matching the isolated builder.
            var members = sources
                .Where(s =>
                    s.Id == project.Id
                    || s.Kind.EndsWith("Sql", StringComparison.Ordinal) && Within(s.Path, directory) && !assigned.Contains(s.Id)
                )
                .Concat(sources.Where(s => s.Kind == "buildConfig"))
                .DistinctBy(s => s.Id)
                .ToArray();
            foreach (var member in members)
                assigned.Add(member.Id);
            var name = Path.GetFileNameWithoutExtension(project.Path);
            try
            {
                using var xml = XmlReader.Create(
                    project.Path,
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }
                );
                var declared = XDocument.Load(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
                if (!string.IsNullOrWhiteSpace(declared) && !declared.Contains("$("))
                    name = declared;
            }
            catch (Exception ex)
            {
                diagnostics.Add($"{project.Path}: некорректный XML проекта: {ex.Message}");
            }
            result.Add(
                new(Identity.Hash(workspace.Id + ":db:" + Identity.PathKey(project.Path)), name, project.Path, "sqlProject", members)
            );
        }
        foreach (
            var artifact in sources.Where(s => s.Kind == "dacpac" && !projects.Any(p => Within(s.Path, Path.GetDirectoryName(p.Path)!)))
        )
            result.Add(
                new(
                    Identity.Hash(workspace.Id + ":db:" + Identity.PathKey(artifact.Path)),
                    Path.GetFileNameWithoutExtension(artifact.Path),
                    artifact.Path,
                    "dacpac",
                    [artifact]
                )
            );
        foreach (
            var group in sources
                .Where(s => s.Kind is "schemaSql" or "migrationSql" or "unknownSql" && !assigned.Contains(s.Id))
                .GroupBy(
                    s => Path.GetDirectoryName(s.Path)!,
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
                )
        )
            result.Add(
                new(
                    Identity.Hash(workspace.Id + ":db:" + Identity.PathKey(group.Key)),
                    new DirectoryInfo(group.Key).Name,
                    group.Key,
                    "staticSqlGroup",
                    group.ToArray()
                )
            );
        result = result
            .Select(g => store.Get<DatabaseAlias>("UserSettings", "db-name:" + g.Id) is { } alias ? g with { Name = alias.Name } : g)
            .ToList();
        return result.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.SourcePath).ToList();
    }

    private static bool Within(string file, string directory)
    {
        var relative = Path.GetRelativePath(directory, file);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar);
    }
}
