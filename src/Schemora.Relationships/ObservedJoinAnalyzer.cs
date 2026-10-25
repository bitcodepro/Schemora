using Microsoft.SqlServer.TransactSql.ScriptDom;
using Schemora.SqlServer;

namespace Schemora.Relationships;

internal sealed record ObservedAnalysisResult(IReadOnlyList<RelationshipEdge> Relations, IReadOnlyList<SchemaDiagnostic> Diagnostics);

internal sealed class ObservedJoinAnalyzer
{
    internal ObservedAnalysisResult Analyze(
        IReadOnlyList<RelationshipDatabase> databases,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken
    )
    {
        var edges = new List<RelationshipEdge>();
        var diagnostics = new List<SchemaDiagnostic>();
        long totalBytes = 0;
        if (paths.Count > 4096)
            diagnostics.Add(new("warning", "JOIN_SOURCE_LIMIT", "Observed join analysis accepts at most 4096 source files."));
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase).Take(4096))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (edges.Count >= 10_000)
                break;
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) || new Uri(fullPath).IsUnc)
                    throw new IOException("Network SQL paths are not supported.");
                var info = new FileInfo(fullPath);
                for (
                    FileSystemInfo? entry = info;
                    entry is not null;
                    entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent
                )
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Linked SQL files/directories are not supported.");
                totalBytes += info.Length;
                if (totalBytes > 64L * 1024 * 1024)
                {
                    diagnostics.Add(new("warning", "JOIN_TOTAL_SIZE_LIMIT", "Observed join analysis stopped at 64 MiB total input."));
                    break;
                }
                if (info.Length > 16L * 1024 * 1024)
                {
                    diagnostics.Add(new("warning", "JOIN_SQL_TOO_LARGE", $"Observed join analysis skipped {path}: file exceeds 16 MiB."));
                    continue;
                }
                using var stream = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
                var parsed = new TSql170Parser(true).Parse(stream, out var errors);
                if (errors.Count != 0 || parsed is not TSqlScript script)
                {
                    foreach (var error in errors.Take(25))
                        diagnostics.Add(new("warning", "JOIN_SQL_PARSE_ERROR", $"{path}({error.Line},{error.Column}): {error.Message}"));
                    continue;
                }
                string? databaseContext = null;
                foreach (var statement in script.Batches.SelectMany(batch => batch.Statements))
                {
                    if (statement is UseStatement use)
                    {
                        databaseContext = use.DatabaseName.Value;
                        continue;
                    }
                    statement.Accept(new QueryVisitor(databases, path, databaseContext, edges, diagnostics, cancellationToken));
                    if (edges.Count >= 10_000)
                        break;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                diagnostics.Add(new("warning", "JOIN_SQL_UNREADABLE", $"{path}: {exception.Message}"));
            }
        }
        if (edges.Count >= 10_000)
            diagnostics.Add(new("warning", "JOIN_EDGE_LIMIT", "Observed join analysis stopped at 10000 evidence edges."));
        return new(edges, diagnostics.Take(1000).ToArray());
    }

    /// <summary>In-memory embedded SQL; never writes source text or literal values to evidence/diagnostics.</summary>
    internal ObservedAnalysisResult AnalyzeSqlText(
        IReadOnlyList<RelationshipDatabase> databases,
        string sql,
        string sourcePath,
        int sourceLine,
        CancellationToken cancellationToken,
        string evidenceKind = "dapper-sql"
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var edges = new List<RelationshipEdge>();
        var diagnostics = new List<SchemaDiagnostic>();
        if (sql.Length > 1024 * 1024)
            return new([], [new("warning", "JOIN_EMBEDDED_SQL_LIMIT", $"{sourcePath}({sourceLine}): embedded SQL exceeds 1 MiB.")]);
        using var text = new StringReader(sql);
        var parsed = new TSql170Parser(true).Parse(text, out var errors);
        if (errors.Count != 0 || parsed is not TSqlScript script)
        {
            // ScriptDom messages can quote a string/number from SQL. Persist only error codes/positions.
            foreach (var error in errors.Take(25))
                diagnostics.Add(
                    new(
                        "warning",
                        "JOIN_SQL_PARSE_ERROR",
                        $"{sourcePath}({sourceLine}): embedded SQL parse error {error.Number} at SQL line {error.Line}, column {error.Column}; SQL values were omitted."
                    )
                );
            return new([], diagnostics);
        }
        string? context = databases.Count == 1 ? databases[0].Name : null;
        foreach (var statement in script.Batches.SelectMany(batch => batch.Statements))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (statement is UseStatement use)
            {
                context = use.DatabaseName.Value;
                continue;
            }
            statement.Accept(new QueryVisitor(databases, sourcePath, context, edges, diagnostics, cancellationToken));
            if (edges.Count >= 10_000)
                break;
        }
        return new(
            edges
                .Select(edge =>
                    edge with
                    {
                        Relation = edge.Relation with { Kind = "embedded-sql-candidate" },
                        Status = "candidate",
                        IsTrusted = false,
                        Evidence = edge
                            .Evidence.Select(evidence =>
                                evidence with
                                {
                                    Kind = evidenceKind,
                                    SourcePath = sourcePath,
                                    Line = sourceLine + (evidence.Line ?? 1) - 1,
                                }
                            )
                            .ToArray(),
                        ReviewReason =
                            "Embedded SQL is observed source evidence only; explicit confirmation is required before trusted JOIN generation.",
                    }
                )
                .ToArray(),
            diagnostics.Take(1000).ToArray()
        );
    }

    private sealed class QueryVisitor(
        IReadOnlyList<RelationshipDatabase> databases,
        string path,
        string? databaseContext,
        List<RelationshipEdge> edges,
        List<SchemaDiagnostic> diagnostics,
        CancellationToken cancellationToken
    ) : TSqlFragmentVisitor
    {
        public override void ExplicitVisit(QuerySpecification node)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (edges.Count >= 10_000)
                return;
            var aliases = new Dictionary<string, List<RelationshipTable>>(StringComparer.OrdinalIgnoreCase);
            var scope = new List<RelationshipTable>();
            var predicates = new List<BooleanExpression>();
            if (node.FromClause is not null)
                foreach (var reference in node.FromClause.TableReferences)
                    Collect(reference);
            if (node.WhereClause is not null)
                predicates.Add(node.WhereClause.SearchCondition);
            foreach (var predicate in predicates)
            {
                var comparisons = ConjunctiveEqualities(predicate).ToArray();
                var pairs = new List<(RelationshipTable Left, string LeftColumn, RelationshipTable Right, string RightColumn, int Line)>();
                foreach (var comparison in comparisons)
                {
                    if (
                        comparison.FirstExpression is not ColumnReferenceExpression left
                        || comparison.SecondExpression is not ColumnReferenceExpression right
                    )
                        continue;
                    var first = BindColumn(left);
                    var second = BindColumn(right);
                    if (
                        first is null
                        || second is null
                        || first.Value.Table.Table.Id == second.Value.Table.Table.Id && first.Value.Column == second.Value.Column
                    )
                        continue;
                    if (string.CompareOrdinal(first.Value.Table.Table.Id, second.Value.Table.Table.Id) <= 0)
                        pairs.Add((first.Value.Table, first.Value.Column, second.Value.Table, second.Value.Column, comparison.StartLine));
                    else
                        pairs.Add((second.Value.Table, second.Value.Column, first.Value.Table, first.Value.Column, comparison.StartLine));
                }
                foreach (var group in pairs.GroupBy(pair => (pair.Left.Table.Id, Target: pair.Right.Table.Id)))
                {
                    if (edges.Count >= 10_000)
                        break;
                    var all = group.DistinctBy(pair => (pair.LeftColumn, pair.RightColumn)).ToArray();
                    var first = all[0];
                    var relation = new SchemaRelation(
                        "",
                        $"Observed {first.Left.Table.Name} ↔ {first.Right.Table.Name}",
                        first.Left.Table.Id,
                        first.Right.Table.Id,
                        all.Select(pair => pair.LeftColumn).ToArray(),
                        all.Select(pair => pair.RightColumn).ToArray(),
                        "observed-join",
                        0.6
                    );
                    edges.Add(
                        new(
                            relation,
                            "observed",
                            false,
                            [
                                new(
                                    "observed-join",
                                    path,
                                    first.Line,
                                    "SQL equality predicates: "
                                        + string.Join(
                                            " AND ",
                                            all.Select(pair =>
                                                $"{pair.Left.Table.Schema}.{pair.Left.Table.Name}.{pair.LeftColumn} = {pair.Right.Table.Schema}.{pair.Right.Table.Name}.{pair.RightColumn}"
                                            )
                                        )
                                        + ". This is observed usage, not a declared foreign key."
                                ),
                            ],
                            "Observed SQL joins require explicit confirmation before use in trusted paths."
                        )
                    );
                }
            }
            base.ExplicitVisit(node); // Derived/subquery SELECTs get their own alias scope.

            void Collect(TableReference reference)
            {
                switch (reference)
                {
                    case NamedTableReference named:
                        var table = BindTable(named.SchemaObject);
                        if (table is null)
                            return;
                        var alias = named.Alias?.Value ?? named.SchemaObject.BaseIdentifier.Value;
                        if (!aliases.TryGetValue(alias, out var list))
                            aliases.Add(alias, list = []);
                        list.Add(table);
                        scope.Add(table);
                        break;
                    case QualifiedJoin join:
                        Collect(join.FirstTableReference);
                        Collect(join.SecondTableReference);
                        predicates.Add(join.SearchCondition);
                        break;
                    case UnqualifiedJoin join:
                        Collect(join.FirstTableReference);
                        Collect(join.SecondTableReference);
                        break;
                    case JoinParenthesisTableReference parentheses:
                        Collect(parentheses.Join);
                        break;
                }
            }
            (RelationshipTable Table, string Column)? BindColumn(ColumnReferenceExpression column)
            {
                var parts = column.MultiPartIdentifier?.Identifiers.Select(identifier => identifier.Value).ToArray();
                if (parts is null || parts.Length == 0)
                    return null;
                IEnumerable<RelationshipTable> candidates;
                if (parts.Length == 1)
                    candidates = scope;
                else if (parts.Length == 2 && aliases.TryGetValue(parts[0], out var bound))
                    candidates = bound;
                else
                    candidates = scope.Where(table =>
                        parts.Length >= 3
                        && string.Equals(table.Table.Name, parts[^2], StringComparison.OrdinalIgnoreCase)
                        && string.Equals(table.Table.Schema, parts[^3], StringComparison.OrdinalIgnoreCase)
                        && (parts.Length < 4 || string.Equals(table.DatabaseName, parts[^4], StringComparison.OrdinalIgnoreCase))
                    );
                var matches = candidates.Where(table => table.Table.Columns.Any(candidate => candidate.Name == parts[^1])).ToArray();
                if (matches.Length > 1)
                    Warn(
                        "JOIN_COLUMN_AMBIGUOUS",
                        column,
                        "Column reference matches multiple tables in this SQL scope; no endpoint was guessed."
                    );
                return matches.Length == 1 ? (matches[0], parts[^1]) : null;
            }
        }

        private RelationshipTable? BindTable(SchemaObjectName name)
        {
            if (name.ServerIdentifier is not null || name.SchemaIdentifier is null || name.BaseIdentifier is null)
            {
                Warn(
                    "JOIN_TABLE_UNRESOLVED",
                    name,
                    "Observed join table has an external server or omitted schema; no database/schema was guessed."
                );
                return null;
            }
            var explicitDatabase = name.DatabaseIdentifier?.Value ?? databaseContext;
            IEnumerable<RelationshipDatabase> candidates = databases;
            if (explicitDatabase is not null)
                candidates = candidates.Where(database =>
                    string.Equals(database.Name, explicitDatabase, StringComparison.OrdinalIgnoreCase)
                );
            else
            {
                var contexts = databases
                    .Select(database => (Database: database, Directory: SourceDirectory(database.Graph.SourcePath)))
                    .Where(item => item.Directory is not null && Within(path, item.Directory))
                    .ToArray();
                if (contexts.Length != 0)
                {
                    var depth = contexts.Max(context => context.Directory!.Length);
                    candidates = contexts.Where(context => context.Directory!.Length == depth).Select(context => context.Database);
                }
            }
            var matches = candidates
                .SelectMany(database =>
                    database
                        .Graph.Tables.Where(table =>
                            string.Equals(table.Schema, name.SchemaIdentifier.Value, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(table.Name, name.BaseIdentifier.Value, StringComparison.OrdinalIgnoreCase)
                        )
                        .Select(table => new RelationshipTable(database.Id, database.Name, table))
                )
                .ToArray();
            if (matches.Length == 1)
                return matches[0];
            Warn(
                "JOIN_TABLE_AMBIGUOUS",
                name,
                "Observed join table is absent or matches more than one catalog database; no endpoint was fabricated."
            );
            return null;
        }

        private void Warn(string code, TSqlFragment fragment, string message)
        {
            if (diagnostics.Count < 1000)
                diagnostics.Add(new("warning", code, $"{path}({fragment.StartLine},{fragment.StartColumn}): {message}"));
        }

        private static IEnumerable<BooleanComparisonExpression> ConjunctiveEqualities(BooleanExpression expression)
        {
            switch (expression)
            {
                case BooleanComparisonExpression comparison when comparison.ComparisonType == BooleanComparisonType.Equals:
                    yield return comparison;
                    break;
                case BooleanParenthesisExpression parentheses:
                    foreach (var comparison in ConjunctiveEqualities(parentheses.Expression))
                        yield return comparison;
                    break;
                case BooleanBinaryExpression binary when binary.BinaryExpressionType == BooleanBinaryExpressionType.And:
                    foreach (var comparison in ConjunctiveEqualities(binary.FirstExpression))
                        yield return comparison;
                    foreach (var comparison in ConjunctiveEqualities(binary.SecondExpression))
                        yield return comparison;
                    break;
            }
        }

        private static string? SourceDirectory(string source)
        {
            try
            {
                return Directory.Exists(source) ? Path.GetFullPath(source) : Path.GetDirectoryName(Path.GetFullPath(source));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        private static bool Within(string file, string directory)
        {
            var relative = Path.GetRelativePath(directory, file);
            return !Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }
    }
}
