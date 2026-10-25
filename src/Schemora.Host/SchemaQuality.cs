using System.Text.Json;
using Schemora.Relationships;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record QualityFinding(
    string Id,
    string Rule,
    string Severity,
    string DatabaseId,
    string? ObjectId,
    string Title,
    string Explanation,
    string? SourcePath,
    int? Line,
    string Fingerprint,
    string? SuppressionReason = null
);

public sealed record QualitySuppression(string FindingId, string Fingerprint, string Reason, DateTimeOffset ChangedAt);

public sealed record SuppressQualityRequest(string FindingId, string Fingerprint, string Reason, bool Suppress = true);

public static class SchemaQuality
{
    public static IReadOnlyList<QualityFinding> Evaluate(CatalogStore store, string workspaceId, RelationshipGraph relationships) =>
        store.WithWriteLock(() =>
        {
            var catalog = store.Catalog(workspaceId);
            var policy = QualityRules.Read(store, workspaceId);
            var findings = new List<QualityFinding>();
            void Add(
                string rule,
                string severity,
                DatabaseInfo db,
                string? objectId,
                string title,
                string detail,
                string? path = null,
                int? line = null,
                string? discriminator = null
            )
            {
                severity = policy.Rules.GetValueOrDefault(rule) ?? severity;
                if (severity == "off")
                    return;
                var id = Identity.Hash(
                    rule + "\0" + db.Id + "\0" + objectId + "\0" + title + "\0" + detail + "\0" + path + "\0" + line + "\0" + discriminator
                );
                var fingerprint = Identity.Hash(
                    JsonSerializer.Serialize(
                        new
                        {
                            rule,
                            severity,
                            db.CurrentSnapshotId,
                            objectId,
                            title,
                            detail,
                            path,
                            line,
                            discriminator,
                        },
                        CatalogStore.Json
                    )
                );
                var suppression = store.Get<QualitySuppression>("UserSettings", "quality:" + workspaceId + ":" + id);
                findings.Add(
                    new(
                        id,
                        rule,
                        severity,
                        db.Id,
                        objectId,
                        title,
                        detail,
                        path,
                        line,
                        fingerprint,
                        suppression?.Fingerprint == fingerprint ? suppression.Reason : null
                    )
                );
            }
            foreach (var db in catalog.Databases)
            {
                var graph = store.CurrentGraph(db.Id);
                if (db.Status is "failed" or "unavailable" or "pending" || graph is null)
                    Add(
                        "SCHEMA_UNAVAILABLE",
                        "error",
                        db,
                        null,
                        db.Name,
                        "The current schema is unavailable or uses a previous successful snapshot.",
                        db.SourcePath
                    );
                if (graph is null)
                    continue;
                foreach (var table in graph.Tables.Where(t => t.PrimaryKey.Count == 0))
                    Add(
                        "TABLE_WITHOUT_PRIMARY_KEY",
                        "warning",
                        db,
                        table.Id,
                        table.Schema + "." + table.Name,
                        "No primary key is declared. This can be intentional for staging or append-only tables.",
                        graph.SourcePath
                    );
                QualityRules.Evaluate(
                    graph,
                    (rule, severity, objectId, title, detail) => Add(rule, severity, db, objectId, title, detail, graph.SourcePath)
                );
                foreach (var dependency in graph.ObjectDependencies ?? [])
                    if (dependency.ToObjectId is null)
                        Add(
                            "UNRESOLVED_DEPENDENCY",
                            "warning",
                            db,
                            dependency.FromObjectId,
                            dependency.TargetName,
                            "The referenced object could not be resolved in this schema; external references require review.",
                            dependency.SourcePath,
                            dependency.Line
                        );
                foreach (var diagnostic in graph.Diagnostics.Where(d => d.Severity is "error" or "warning"))
                    Add(
                        "ANALYZER_" + diagnostic.Code,
                        diagnostic.Severity,
                        db,
                        null,
                        diagnostic.Code,
                        diagnostic.Message,
                        graph.SourcePath
                    );
            }
            foreach (var edge in relationships.Relations.Where(e => !e.IsTrusted && e.Status != "rejected"))
            {
                var table = relationships.Tables.FirstOrDefault(t => t.Table.Id == edge.Relation.FromTableId);
                var db = catalog.Databases.FirstOrDefault(d => d.Id == table?.DatabaseId);
                if (db is null)
                    continue;
                var evidence = edge.Evidence.FirstOrDefault();
                Add(
                    "UNCONFIRMED_RELATION",
                    "info",
                    db,
                    edge.Relation.FromTableId,
                    edge.Relation.Name,
                    "This relationship is candidate evidence and is not trusted for JOIN generation. " + (edge.ReviewReason ?? ""),
                    evidence?.SourcePath,
                    evidence?.Line,
                    Identity.Hash(JsonSerializer.Serialize(new { edge.Relation, edge.Evidence }, CatalogStore.Json))
                );
            }
            return (IReadOnlyList<QualityFinding>)
                findings
                    .DistinctBy(f => f.Id)
                    .OrderBy(f =>
                        f.Severity == "error" ? 0
                        : f.Severity == "warning" ? 1
                        : 2
                    )
                    .ThenBy(f => f.Title, StringComparer.Ordinal)
                    .ThenBy(f => f.Id)
                    .ToArray();
        });

    public static void Suppress(
        CatalogStore store,
        string workspaceId,
        IReadOnlyList<QualityFinding> current,
        SuppressQualityRequest request
    )
    {
        var finding =
            current.FirstOrDefault(f => f.Id == request.FindingId)
            ?? throw new AppException("QUALITY_FINDING_CHANGED", "Refresh quality findings before reviewing this item.", 409);
        if (finding.Fingerprint != request.Fingerprint)
            throw new AppException("QUALITY_FINDING_CHANGED", "The finding changed. Review it again.", 409);
        var key = "quality:" + workspaceId + ":" + finding.Id;
        if (!request.Suppress)
        {
            store.Delete("UserSettings", key);
            return;
        }
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length is < 10 or > 2000)
            throw new AppException("QUALITY_REASON_REQUIRED", "Provide a review reason of 10–2000 characters.");
        store.Put(
            "UserSettings",
            key,
            workspaceId,
            new QualitySuppression(finding.Id, finding.Fingerprint, request.Reason.Trim(), DateTimeOffset.UtcNow)
        );
    }
}
