using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Schemora.SqlServer;

public sealed record SqlSourceClassification(string Kind, IReadOnlyList<SchemaDiagnostic> Diagnostics);

/// <summary>Classifies parsed top-level statements. Comments, strings and procedure bodies are never treated as schema definitions.</summary>
public sealed class SqlSourceClassifier
{
    internal const long MaximumFileBytes = 16L * 1024 * 1024;

    public string Classify(string path) => Inspect(path).Kind;

    public SqlSourceClassification Inspect(string path)
    {
        var parsed = Parse(path);
        return new SqlSourceClassification(parsed.Kind, parsed.Diagnostics);
    }

    internal static ParsedSqlSource Parse(string path)
    {
        var diagnostics = new List<SchemaDiagnostic>();
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) || new Uri(fullPath).IsUnc)
                throw new IOException("Network SQL paths are not supported.");
            var information = new FileInfo(fullPath);
            if (information.Length > MaximumFileBytes)
                throw new IOException("The SQL file exceeds the static analysis limit of 16 MiB.");
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var input = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var fragment = new TSql170Parser(initialQuotedIdentifiers: true).Parse(input, out var errors);
            foreach (var error in errors.Take(100))
                diagnostics.Add(
                    new SchemaDiagnostic(
                        "error",
                        "SQL_PARSE_ERROR",
                        $"{fullPath}({error.Line},{error.Column}): SQL{error.Number}: {error.Message}"
                    )
                );
            if (errors.Count != 0 || fragment is not TSqlScript script)
                return new ParsedSqlSource(fullPath, null, "unknownSql", diagnostics);
            var statements = Statements(script).ToArray();
            var hasCreate = statements.Any(statement => statement.GetType().Name.StartsWith("Create", StringComparison.Ordinal));
            var hasMigration = statements.Any(statement =>
                statement.GetType().Name.StartsWith("Alter", StringComparison.Ordinal)
                || statement.GetType().Name.StartsWith("Drop", StringComparison.Ordinal)
                || statement is TruncateTableStatement
            );
            var hasData = statements.Any(IsDataStatement);
            var kind =
                hasMigration || hasCreate && hasData ? "migrationSql"
                : hasCreate ? "schemaSql"
                : hasData ? "dataSql"
                : statements.Any(statement => statement is SelectStatement) ? "querySql"
                : "unknownSql";
            return new ParsedSqlSource(fullPath, script, kind, diagnostics);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            diagnostics.Add(new SchemaDiagnostic("error", "SQL_FILE_UNREADABLE", $"{path}: {exception.Message}"));
            return new ParsedSqlSource(path, null, "unknownSql", diagnostics);
        }
    }

    internal static IEnumerable<TSqlStatement> Statements(TSqlScript script) => script.Batches.SelectMany(batch => batch.Statements);

    internal static bool IsDataStatement(TSqlStatement statement) =>
        statement is InsertStatement or UpdateStatement or DeleteStatement or MergeStatement or TruncateTableStatement
        || statement.GetType().Name == "BulkInsertStatement";
}

internal sealed record ParsedSqlSource(string Path, TSqlScript? Script, string Kind, IReadOnlyList<SchemaDiagnostic> Diagnostics);
