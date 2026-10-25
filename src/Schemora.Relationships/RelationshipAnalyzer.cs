using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Schemora.SqlServer;

namespace Schemora.Relationships;

public sealed class RelationshipAnalyzer
{
    public RelationshipGraph Analyze(
        IReadOnlyList<RelationshipDatabase> databases,
        IReadOnlyList<EfSourceSet>? efSources = null,
        IReadOnlyList<string>? sqlPaths = null,
        IReadOnlyList<RelationDecision>? decisions = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<RelationshipEdge>? manualRelations = null
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = databases.SelectMany(database => database.Graph.Diagnostics).ToList();
        var tables = databases
            .SelectMany(database => database.Graph.Tables.Select(table => new RelationshipTable(database.Id, database.Name, table)))
            .OrderBy(table => table.Table.Id, StringComparer.Ordinal)
            .ToArray();
        var duplicateIds = tables
            .GroupBy(table => table.Table.Id, StringComparer.Ordinal)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (duplicateIds.Count != 0)
            diagnostics.Add(
                new(
                    "error",
                    "TABLE_ID_AMBIGUOUS",
                    "Duplicate table identities were excluded from relationship analysis; scope schemas by database identity first."
                )
            );
        tables = tables.Where(table => !duplicateIds.Contains(table.Table.Id)).ToArray();
        var byId = tables.ToDictionary(table => table.Table.Id, StringComparer.Ordinal);
        foreach (
            var group in databases
                .GroupBy(database => database.Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Select(database => database.Id).Distinct().Count() > 1)
        )
            diagnostics.Add(
                new(
                    "warning",
                    "DATABASE_NAME_AMBIGUOUS",
                    $"Distinct catalog databases share SQL name {group.Key}. Cross-database JOIN previews between them are blocked until database names are mapped unambiguously."
                )
            );
        var edges = new Dictionary<string, RelationshipEdge>(StringComparer.Ordinal);
        foreach (var database in databases)
        foreach (var relation in database.Graph.Relations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(
                new(
                    relation with
                    {
                        Kind = "declared-fk",
                        Confidence = 1,
                    },
                    "physical",
                    true,
                    [
                        new(
                            "declared-fk",
                            database.Graph.SourcePath,
                            null,
                            $"Declared constraint {relation.Name}; ordered foreign/principal column pairs come from schema metadata."
                        ),
                    ]
                )
            );
        }
        if (efSources is { Count: > 0 })
        {
            var ef = new EfMappingAnalyzer().Analyze(databases, efSources, cancellationToken);
            diagnostics.AddRange(ef.Diagnostics);
            foreach (var edge in ef.Relations)
                Add(edge);
            var embedded = new DapperSqlAnalyzer().Analyze(databases, efSources, cancellationToken);
            diagnostics.AddRange(embedded.Diagnostics);
            foreach (var edge in embedded.Relations)
                Add(edge);
        }
        if (sqlPaths is { Count: > 0 })
        {
            var observations = new ObservedJoinAnalyzer().Analyze(databases, sqlPaths, cancellationToken);
            diagnostics.AddRange(observations.Diagnostics);
            foreach (var edge in observations.Relations)
                Add(edge);
        }
        foreach (var manual in manualRelations ?? [])
            Add(manual);
        foreach (var candidate in InferCandidates(tables, diagnostics, cancellationToken))
            Add(candidate);
        var physicalBySource = edges
            .Values.Where(edge => edge.Relation.Kind == "declared-fk")
            .GroupBy(edge => SourceKey(edge.Relation.FromTableId, edge.Relation.FromColumns), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var item in edges.Where(item => item.Value.Relation.Kind != "declared-fk").ToArray())
        {
            var edge = item.Value;
            var alternatives = new[]
            {
                SourceKey(edge.Relation.FromTableId, edge.Relation.FromColumns),
                SourceKey(edge.Relation.ToTableId, edge.Relation.ToColumns),
            }
                .Distinct(StringComparer.Ordinal)
                .Where(physicalBySource.ContainsKey)
                .SelectMany(key => physicalBySource[key])
                .Where(physical => physical.Relation.Id != edge.Relation.Id)
                .DistinctBy(physical => physical.Relation.Id)
                .ToArray();
            if (alternatives.Length == 0)
                continue;
            var conflictEvidence = alternatives
                .Select(physical => new RelationEvidence(
                    "physical-conflict",
                    physical.Evidence.FirstOrDefault(evidence => evidence.Kind == "declared-fk")?.SourcePath ?? "",
                    null,
                    $"Physical constraint {physical.Relation.Name} maps {Describe(physical.Relation.FromTableId)} ({string.Join(", ", physical.Relation.FromColumns)}) to {Describe(physical.Relation.ToTableId)} ({string.Join(", ", physical.Relation.ToColumns)}). This alternative uses the same source columns with a different target/mapping."
                ))
                .ToArray();
            var reason = string.Join(" ", conflictEvidence.Select(evidence => evidence.Detail));
            edges[item.Key] = edge with
            {
                Status = "candidate",
                IsTrusted = false,
                Evidence = edge.Evidence.Concat(conflictEvidence).ToArray(),
                ReviewReason = reason + " Explicit confirmation is required after reviewing the physical metadata.",
            };
            diagnostics.Add(new("warning", "USER_RELATION_CONFLICT", $"{edge.Relation.Name}: {reason}"));
        }
        foreach (
            var decision in (decisions ?? [])
                .OrderBy(decision => decision.ChangedAt)
                .ThenBy(decision => decision.RelationId, StringComparer.Ordinal)
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!edges.TryGetValue(decision.RelationId, out var edge))
            {
                diagnostics.Add(
                    new(
                        "warning",
                        "RELATION_DECISION_STALE",
                        $"Saved decision for {decision.RelationId} no longer matches current endpoints/evidence. It was preserved but is not trusted."
                    )
                );
                continue;
            }
            var evidence = edge
                .Evidence.Append(
                    new RelationEvidence("user-decision", "", null, $"{decision.Decision} at {decision.ChangedAt:O}: {decision.Reason}")
                )
                .ToArray();
            if (decision.Decision == "confirm")
                edges[decision.RelationId] =
                    edge.Status == "physical"
                        ? edge with
                        {
                            Evidence = evidence,
                        }
                        : edge with
                        {
                            Relation = edge.Relation with { Kind = "user-confirmed" },
                            Status = "confirmed",
                            IsTrusted = true,
                            Evidence = evidence,
                            ReviewReason =
                                decision.Reason
                                + (
                                    edge.Evidence.Any(item => item.Kind == "physical-conflict")
                                        ? " Physical conflict remains visible: "
                                            + string.Join(
                                                " ",
                                                edge.Evidence.Where(item => item.Kind == "physical-conflict").Select(item => item.Detail)
                                            )
                                        : ""
                                ),
                        };
            else if (decision.Decision == "reject")
                edges[decision.RelationId] = edge.Status is "physical" or "mapped"
                    ? edge with
                    {
                        Status = "conflict",
                        IsTrusted = false,
                        Evidence = evidence,
                        ReviewReason =
                            "A saved rejection conflicts with declared schema/ORM metadata. Resolve the conflict before using this edge in SQL.",
                    }
                    : edge with
                    {
                        Status = "rejected",
                        IsTrusted = false,
                        Evidence = evidence,
                        ReviewReason =
                            decision.Reason
                            + (
                                edge.Evidence.Any(item => item.Kind == "physical-conflict")
                                    ? " Physical conflict remains visible: "
                                        + string.Join(
                                            " ",
                                            edge.Evidence.Where(item => item.Kind == "physical-conflict").Select(item => item.Detail)
                                        )
                                    : ""
                            ),
                    };
            else
                diagnostics.Add(
                    new(
                        "warning",
                        "RELATION_DECISION_INVALID",
                        $"Unknown relation decision {decision.Decision}; expected confirm or reject."
                    )
                );
        }
        return new(
            tables,
            edges.Values.OrderBy(edge => edge.Relation.Id, StringComparer.Ordinal).ToArray(),
            diagnostics.Take(2000).ToArray(),
            databases
                .SelectMany(d => (d.Graph.Objects ?? []).Where(o => o.Kind == "view").Select(o => new RelationshipObject(d.Id, d.Name, o)))
                .ToArray()
        );

        string SourceKey(string id, IReadOnlyList<string> columns) =>
            JsonSerializer.Serialize(new { id, columns = columns.Order(StringComparer.Ordinal).ToArray() });
        string Describe(string id) =>
            byId.TryGetValue(id, out var table) ? $"{table.DatabaseName}.{table.Table.Schema}.{table.Table.Name}" : id;
        void Add(RelationshipEdge edge)
        {
            if (!RelationshipRules.Valid(edge.Relation, byId))
            {
                diagnostics.Add(
                    new(
                        "warning",
                        "RELATION_ENDPOINTS_UNRESOLVED",
                        $"Relation {edge.Relation.Name} has missing columns/endpoints or incompatible types and was excluded."
                    )
                );
                return;
            }
            var id = RelationshipRules.Identity(edge.Relation);
            edge = edge with { Relation = edge.Relation with { Id = id } };
            if (!edges.TryGetValue(id, out var prior))
            {
                edges.Add(id, edge);
                return;
            }
            var selected = Priority(edge) > Priority(prior) ? edge : prior;
            var evidence = prior
                .Evidence.Concat(edge.Evidence)
                .Distinct()
                .OrderBy(item => item.Kind, StringComparer.Ordinal)
                .ThenBy(item => item.SourcePath, StringComparer.Ordinal)
                .ThenBy(item => item.Line)
                .ToArray();
            edges[id] = selected with { Evidence = evidence };
        }
    }

    private static int Priority(RelationshipEdge edge) =>
        edge.Relation.Kind switch
        {
            "declared-fk" => 5,
            "orm-mapping" when edge.IsTrusted => 4,
            "user-confirmed" => 3,
            "observed-join" or "embedded-sql-candidate" => 2,
            _ => 1,
        };

    private static IEnumerable<RelationshipEdge> InferCandidates(
        IReadOnlyList<RelationshipTable> tables,
        List<SchemaDiagnostic> diagnostics,
        CancellationToken cancellationToken
    )
    {
        var targets = new Dictionary<string, List<(RelationshipTable Table, SchemaColumn Column, bool Primary)>>(StringComparer.Ordinal);
        foreach (var target in tables)
        foreach (var column in target.Table.Columns)
        {
            var primary = target.Table.PrimaryKey.Count == 1 && target.Table.PrimaryKey[0] == column.Name;
            if (
                !primary
                && !(
                    RelationshipRules.IsText(column.DataType)
                    && (
                        column.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
                        || column.Name.EndsWith("Key", StringComparison.OrdinalIgnoreCase)
                        || column.Name.EndsWith("Reference", StringComparison.OrdinalIgnoreCase)
                    )
                )
            )
                continue;
            var entity = Singular(Normalize(target.Table.Name));
            var columnName = Normalize(column.Name);
            var names = new HashSet<string>(StringComparer.Ordinal) { entity + columnName };
            if (primary)
                names.Add(entity + "id");
            if (columnName.StartsWith(entity, StringComparison.Ordinal) && columnName.Length > entity.Length)
                names.Add(columnName);
            foreach (var name in names)
            {
                if (!targets.TryGetValue(name, out var list))
                    targets.Add(name, list = []);
                list.Add((target, column, primary));
            }
        }
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var from in tables)
        foreach (var column in from.Table.Columns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = Normalize(column.Name);
            if (normalized is "id" or "key" or "externalid" or "reference")
                continue;
            var lookup = new[] { normalized }.Concat(
                new[] { "parent", "related", "source", "target" }
                    .Where(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(prefix => normalized[prefix.Length..])
            );
            foreach (var key in lookup)
            {
                if (!targets.TryGetValue(key, out var candidates))
                    continue;
                foreach (var target in candidates)
                {
                    if (
                        from.Table.Id == target.Table.Table.Id && column.Name == target.Column.Name
                        || !RelationshipRules.TypesCompatible(column.DataType, target.Column.DataType)
                    )
                        continue;
                    var score =
                        0.45
                        + (target.Primary ? 0.25 : 0.05)
                        + (from.DatabaseId == target.Table.DatabaseId ? 0.1 : 0)
                        + (from.Table.Schema == target.Table.Table.Schema ? 0.05 : 0)
                        - (key == normalized ? 0 : 0.05);
                    var relation = new SchemaRelation(
                        "",
                        $"{from.Table.Name}.{column.Name} → {target.Table.Table.Name}.{target.Column.Name}",
                        from.Table.Id,
                        target.Table.Table.Id,
                        [column.Name],
                        [target.Column.Name],
                        "inferred-candidate",
                        Math.Round(Math.Min(score, 0.89), 2)
                    );
                    var identity = RelationshipRules.Identity(relation);
                    if (!emitted.Add(identity))
                        continue;
                    if (emitted.Count > 2000)
                    {
                        diagnostics.Add(
                            new(
                                "warning",
                                "RELATION_CANDIDATE_LIMIT",
                                "Candidate discovery stopped at 2000 edges. Narrow the workspace/database selection for large catalogs."
                            )
                        );
                        yield break;
                    }
                    var reasons =
                        $"Entity-qualified column name matches {target.Table.Table.Name}; compatible type {column.DataType}; "
                        + (target.Primary ? "target is the single-column primary key" : "target text key uniqueness is not established")
                        + (
                            from.DatabaseId == target.Table.DatabaseId
                                ? "; same database"
                                : "; cross-database candidate requires explicit confirmation"
                        );
                    yield return new(
                        relation with
                        {
                            Id = identity,
                        },
                        "candidate",
                        false,
                        [new("inferred-candidate", "", null, reasons)],
                        "Heuristic evidence is never used by the trusted path finder until explicitly confirmed."
                    );
                }
            }
        }
    }

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string Singular(string value) =>
        value.EndsWith("ies", StringComparison.Ordinal) && value.Length > 3 ? value[..^3] + "y"
        : value.EndsWith('s') && !value.EndsWith("ss", StringComparison.Ordinal) && value.Length > 3 ? value[..^1]
        : value;
}

public static class RelationshipRules
{
    public static bool Valid(SchemaRelation relation, IReadOnlyDictionary<string, RelationshipTable> tables)
    {
        if (
            !tables.TryGetValue(relation.FromTableId, out var from)
            || !tables.TryGetValue(relation.ToTableId, out var to)
            || relation.FromColumns.Count == 0
            || relation.FromColumns.Count != relation.ToColumns.Count
        )
            return false;
        for (var index = 0; index < relation.FromColumns.Count; index++)
        {
            var left = from.Table.Columns.SingleOrDefault(column => column.Name == relation.FromColumns[index]);
            var right = to.Table.Columns.SingleOrDefault(column => column.Name == relation.ToColumns[index]);
            if (left is null || right is null || !TypesCompatible(left.DataType, right.DataType))
                return false;
        }
        return true;
    }

    public static string Identity(SchemaRelation relation)
    {
        var pairs = relation.FromColumns.Zip(relation.ToColumns).Select(pair => (Left: pair.First, Right: pair.Second)).ToArray();
        var forward =
            string.CompareOrdinal(relation.FromTableId, relation.ToTableId) < 0
            || relation.FromTableId == relation.ToTableId
                && string.CompareOrdinal(JsonSerializer.Serialize(relation.FromColumns), JsonSerializer.Serialize(relation.ToColumns)) <= 0;
        var source = forward ? relation.FromTableId : relation.ToTableId;
        var target = forward ? relation.ToTableId : relation.FromTableId;
        var canonical = pairs
            .Select(pair => forward ? new[] { pair.Left, pair.Right } : new[] { pair.Right, pair.Left })
            .OrderBy(pair => pair[0], StringComparer.Ordinal)
            .ThenBy(pair => pair[1], StringComparer.Ordinal)
            .ToArray();
        return "rel:"
            + Convert
                .ToHexString(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes(
                            JsonSerializer.Serialize(
                                new
                                {
                                    source,
                                    target,
                                    canonical,
                                }
                            )
                        )
                    )
                )
                .ToLowerInvariant();
    }

    public static bool IsText(string type) => BaseType(type) is "varchar" or "nvarchar" or "char" or "nchar";

    public static bool TypesCompatible(string left, string right)
    {
        var first = BaseType(left);
        var second = BaseType(right);
        if (first is "" or "unresolved" || second is "" or "unresolved")
            return false;
        return first == second;
    }

    private static string BaseType(string type) => type.Split('(')[0].Trim().ToLowerInvariant();
}
