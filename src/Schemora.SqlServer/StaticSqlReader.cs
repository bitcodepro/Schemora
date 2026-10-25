using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Schemora.SqlServer;

/// <summary>Read-only, deliberately partial extraction of declarative T-SQL. It never executes scripts or evaluates their control flow.</summary>
public sealed class StaticSqlReader
{
    public SchemaGraph Read(string databaseName, IReadOnlyList<string> paths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count > 4096)
            throw new ArgumentException("Static analysis accepts at most 4096 SQL files at once.", nameof(paths));
        var diagnostics = new List<SchemaDiagnostic>
        {
            new(
                "warning",
                "STATIC_ANALYSIS_PARTIAL",
                "Static ScriptDom analysis extracts declarative table/object/index definitions and reference evidence. It does not execute migrations, dynamic SQL, session settings or procedural control flow; use DACPAC/catalog metadata for an authoritative schema."
            ),
        };
        var statements = new List<SourceStatement>();
        long totalBytes = 0;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                totalBytes += new FileInfo(path).Length;
                if (totalBytes > 64L * 1024 * 1024)
                {
                    diagnostics.Add(
                        new("error", "SQL_TOTAL_SIZE_LIMIT", "Static analysis stopped after reaching its total input limit of 64 MiB.")
                    );
                    break;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
            var source = SqlSourceClassifier.Parse(path);
            diagnostics.AddRange(source.Diagnostics);
            if (source.Script is null)
                continue;
            var currentDatabase = databaseName;
            foreach (var statement in SqlSourceClassifier.Statements(source.Script))
            {
                if (statement is UseStatement use)
                {
                    currentDatabase = use.DatabaseName.Value;
                    if (!string.Equals(currentDatabase, databaseName, StringComparison.OrdinalIgnoreCase))
                        Report(
                            "warning",
                            "DATABASE_CONTEXT_SKIPPED",
                            source.Path,
                            statement,
                            $"USE {currentDatabase} changes to a different database; its statements are not added to {databaseName}."
                        );
                    continue;
                }
                if (!string.Equals(currentDatabase, databaseName, StringComparison.OrdinalIgnoreCase))
                    continue;
                AddStatement(source.Path, statement, "dbo");
            }
        }

        var tables = new Dictionary<string, TableState>(StringComparer.Ordinal);
        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        var pendingForeignKeys = new List<PendingForeignKey>();
        foreach (var source in statements.Where(source => source.Statement is CreateTableStatement))
        {
            var create = (CreateTableStatement)source.Statement;
            var name = Resolve(create.SchemaObjectName, source, source.DefaultSchema, "table");
            if (name is null)
                continue;
            if (name.Value.Name.StartsWith('#'))
            {
                Report("info", "TEMP_TABLE_IGNORED", source.Path, create, "Temporary tables are not persistent schema objects.");
                continue;
            }
            var id = Id(name.Value.Schema, name.Value.Name);
            if (conflicts.Contains(id) || tables.ContainsKey(id))
            {
                conflicts.Add(id);
                tables.Remove(id);
                Report(
                    "error",
                    "TABLE_DEFINITION_CONFLICT",
                    source.Path,
                    create,
                    $"Multiple CREATE TABLE definitions for {id}; the ambiguous object is excluded."
                );
                continue;
            }
            if (create.Definition is null || create.SelectStatement is not null || create.CloneSource is not null)
            {
                Report(
                    "warning",
                    "TABLE_DEFINITION_UNSUPPORTED",
                    source.Path,
                    create,
                    $"The static reader cannot infer columns for {id} from SELECT, CLONE or an implicit table definition."
                );
                continue;
            }
            var table = new TableState(id, name.Value.Schema, name.Value.Name)
            {
                SchemaWasAssumed = create.SchemaObjectName.SchemaIdentifier is null,
            };
            tables.Add(id, table);
            ApplyDefinition(table, create.Definition, source);
        }
        foreach (var source in statements.Where(source => source.Statement is AlterTableAddTableElementStatement))
        {
            var alter = (AlterTableAddTableElementStatement)source.Statement;
            var name = Resolve(alter.SchemaObjectName, source, source.DefaultSchema, "ALTER TABLE target");
            if (name is null)
                continue;
            if (!tables.TryGetValue(Id(name.Value.Schema, name.Value.Name), out var table))
            {
                Report(
                    "warning",
                    "ALTER_TARGET_UNRESOLVED",
                    source.Path,
                    alter,
                    "ALTER TABLE ADD refers to a table without one unambiguous CREATE TABLE definition in these sources."
                );
                continue;
            }
            ApplyDefinition(table, alter.Definition, source);
        }

        foreach (var table in tables.Values)
        {
            if (table.PrimaryKey.Count != 0)
                foreach (var key in table.PrimaryKey)
                {
                    var columnIndex = table.Columns.FindIndex(column => column.Name == key);
                    if (columnIndex < 0)
                    {
                        diagnostics.Add(
                            new("error", "PRIMARY_KEY_COLUMN_UNRESOLVED", $"{table.Id}: primary key column {key} does not exist.")
                        );
                        table.Invalid = true;
                    }
                    else
                        table.Columns[columnIndex] = table.Columns[columnIndex] with { IsNullable = false };
                }
        }
        var validTables = tables.Values.Where(table => !table.Invalid).ToDictionary(table => table.Id, StringComparer.Ordinal);
        var relations = new List<SchemaRelation>();
        var relationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pending in pendingForeignKeys)
        {
            if (!validTables.TryGetValue(pending.FromTableId, out var from))
                continue;
            if (from.SchemaWasAssumed)
            {
                Report(
                    "warning",
                    "FOREIGN_KEY_SCHEMA_UNRESOLVED",
                    pending.Source.Path,
                    pending.Definition,
                    "The source table has an assumed default schema. Its foreign key is not bound until the schema is explicit."
                );
                continue;
            }
            var target = Resolve(pending.Definition.ReferenceTableName, pending.Source, "dbo", "foreign key target");
            if (target is null)
                continue;
            var targetId = Id(target.Value.Schema, target.Value.Name);
            if (!validTables.TryGetValue(targetId, out var to))
            {
                Report(
                    "warning",
                    "FOREIGN_KEY_TARGET_UNRESOLVED",
                    pending.Source.Path,
                    pending.Definition,
                    $"Foreign key target {targetId} is absent or ambiguous; no relation was fabricated."
                );
                continue;
            }
            if (to.SchemaWasAssumed)
            {
                Report(
                    "warning",
                    "FOREIGN_KEY_SCHEMA_UNRESOLVED",
                    pending.Source.Path,
                    pending.Definition,
                    "The target table has an assumed default schema. Its foreign key is not bound until the schema is explicit."
                );
                continue;
            }
            var fromColumns = pending.Definition.Columns.Select(column => column.Value).ToArray();
            if (fromColumns.Length == 0 && pending.InlineColumn is not null)
                fromColumns = [pending.InlineColumn];
            var toColumns = pending.Definition.ReferencedTableColumns.Select(column => column.Value).ToArray();
            if (toColumns.Length == 0)
                toColumns = to.PrimaryKey.ToArray();
            if (
                fromColumns.Length == 0
                || fromColumns.Length != toColumns.Length
                || fromColumns.Any(name => !from.Columns.Any(column => column.Name == name))
                || toColumns.Any(name => !to.Columns.Any(column => column.Name == name))
            )
            {
                Report(
                    "error",
                    "FOREIGN_KEY_COLUMNS_UNRESOLVED",
                    pending.Source.Path,
                    pending.Definition,
                    $"Foreign key on {from.Id} has unresolved or mismatched column pairs; no relation was added."
                );
                continue;
            }
            if (!to.UniqueKeys.Any(key => key.SequenceEqual(toColumns, StringComparer.Ordinal)))
            {
                Report(
                    "warning",
                    "FOREIGN_KEY_KEY_UNRESOLVED",
                    pending.Source.Path,
                    pending.Definition,
                    $"The referenced columns of {to.Id} do not match a parsed primary/unique constraint. A unique index or external metadata may be needed."
                );
                continue;
            }
            var keyName = pending.Definition.ConstraintIdentifier?.Value;
            if (string.IsNullOrWhiteSpace(keyName))
            {
                var identity = JsonSerializer.Serialize(
                    new
                    {
                        from.Id,
                        From = fromColumns,
                        Target = to.Id,
                        To = toColumns,
                    }
                );
                keyName = $"FK_{from.Name}_{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..12]}";
            }
            var relationId = Id(from.Schema, keyName);
            if (!relationIds.Add(relationId))
            {
                Report(
                    "error",
                    "FOREIGN_KEY_NAME_CONFLICT",
                    pending.Source.Path,
                    pending.Definition,
                    $"Duplicate foreign key identity {relationId}; inspect the source constraints."
                );
                continue;
            }
            relations.Add(new(relationId, keyName, from.Id, to.Id, fromColumns, toColumns, "declared-fk", 1.0));
        }
        var output = validTables
            .Values.OrderBy(table => table.Id, StringComparer.Ordinal)
            .Select(table => new SchemaTable(table.Id, table.Schema, table.Name, table.Columns.ToArray(), table.PrimaryKey.ToArray()))
            .ToArray();
        var metadata = StaticSqlMetadataReader.Read(
            databaseName,
            statements.Select(source => new SqlMetadataSource(source.Path, source.Statement, source.DefaultSchema)).ToArray(),
            output,
            diagnostics
        );
        output = output
            .Where(table => !metadata.ConflictingIds.Contains(table.Id, StringComparer.Ordinal))
            .Select(table => table with { UniqueKeys = metadata.UniqueKeys.GetValueOrDefault(table.Id, []) })
            .ToArray();
        var remainingTables = output.Select(table => table.Id).ToHashSet(StringComparer.Ordinal);
        return new SchemaGraph(
            databaseName,
            string.Join(";", paths),
            DateTimeOffset.UtcNow,
            output,
            relations
                .Where(relation => remainingTables.Contains(relation.FromTableId) && remainingTables.Contains(relation.ToTableId))
                .OrderBy(relation => relation.Id, StringComparer.Ordinal)
                .ToArray(),
            diagnostics,
            metadata.Objects,
            metadata.Indexes,
            metadata.Dependencies
        );

        void AddStatement(string path, TSqlStatement statement, string defaultSchema)
        {
            if (statement is CreateSchemaStatement schema)
            {
                if (schema.StatementList is not null)
                    foreach (var child in schema.StatementList.Statements)
                        AddStatement(path, child, schema.Name?.Value ?? schema.Owner?.Value ?? defaultSchema);
                return;
            }
            if (statement is CreateTableStatement or AlterTableAddTableElementStatement || StaticSqlMetadataReader.Supports(statement))
                statements.Add(new(path, statement, defaultSchema));
            else if (SqlSourceClassifier.IsDataStatement(statement) || statement is SelectStatement)
                Report(
                    "info",
                    "NON_SCHEMA_STATEMENT_IGNORED",
                    path,
                    statement,
                    "Query/data statements are not indexed as table definitions and were not executed."
                );
            else if (
                statement.GetType().Name.StartsWith("Create", StringComparison.Ordinal)
                || statement.GetType().Name.StartsWith("Alter", StringComparison.Ordinal)
                || statement.GetType().Name.StartsWith("Drop", StringComparison.Ordinal)
                || statement is IfStatement or BeginEndBlockStatement or ExecuteStatement
            )
                Report(
                    "warning",
                    "SQL_STATEMENT_UNSUPPORTED",
                    path,
                    statement,
                    $"{statement.GetType().Name} is not evaluated by the static table reader."
                );
        }

        void ApplyDefinition(TableState table, TableDefinition definition, SourceStatement source)
        {
            foreach (var column in definition.ColumnDefinitions)
            {
                var name = column.ColumnIdentifier.Value;
                if (table.Columns.Any(existing => existing.Name == name))
                {
                    Report(
                        "error",
                        "COLUMN_DEFINITION_CONFLICT",
                        source.Path,
                        column,
                        $"Duplicate column {table.Id}.{Quote(name)}; the table is excluded."
                    );
                    table.Invalid = true;
                    continue;
                }
                var nullable = column.Constraints.OfType<NullableConstraintDefinition>().FirstOrDefault();
                var inlinePrimaryKey = column.Constraints.OfType<UniqueConstraintDefinition>().Any(key => key.IsPrimaryKey);
                var isNullable = nullable?.Nullable ?? (column.IdentityOptions is null && !inlinePrimaryKey);
                if (nullable is null && column.IdentityOptions is null && !inlinePrimaryKey)
                    Report(
                        "warning",
                        "COLUMN_NULLABILITY_ASSUMED",
                        source.Path,
                        column,
                        $"{table.Id}.{Quote(name)} has no explicit NULL/NOT NULL. It is treated as nullable unless a parsed primary key/IDENTITY requires NOT NULL; session/database defaults are unavailable."
                    );
                var type = FormatType(column.DataType);
                if (column.ComputedColumnExpression is not null || column.DataType is null)
                {
                    type = "unresolved";
                    Report(
                        "warning",
                        "COLUMN_TYPE_UNRESOLVED",
                        source.Path,
                        column,
                        $"Computed column {table.Id}.{Quote(name)} needs DACPAC/catalog binding to resolve its data type."
                    );
                }
                table.Columns.Add(new(name, type, isNullable));
                foreach (var constraint in column.Constraints)
                    AddConstraint(table, constraint, source, name);
            }
            foreach (var constraint in definition.TableConstraints)
                AddConstraint(table, constraint, source, null);
        }

        void AddConstraint(TableState table, ConstraintDefinition constraint, SourceStatement source, string? inlineColumn)
        {
            if (constraint is ForeignKeyConstraintDefinition foreignKey)
                pendingForeignKeys.Add(new(table.Id, foreignKey, source, inlineColumn));
            else if (constraint is UniqueConstraintDefinition unique)
            {
                var columns = unique.Columns.Select(column => column.Column.MultiPartIdentifier.Identifiers.Last().Value).ToArray();
                if (columns.Length == 0 && inlineColumn is not null)
                    columns = [inlineColumn];
                table.UniqueKeys.Add(columns);
                if (!unique.IsPrimaryKey)
                    return;
                if (table.PrimaryKey.Count != 0)
                {
                    table.Invalid = true;
                    Report("error", "PRIMARY_KEY_CONFLICT", source.Path, constraint, $"{table.Id} has multiple primary key definitions.");
                }
                else
                    table.PrimaryKey.AddRange(columns);
            }
        }

        (string Schema, string Name)? Resolve(SchemaObjectName name, SourceStatement source, string defaultSchema, string role)
        {
            if (
                name.ServerIdentifier is not null
                || name.DatabaseIdentifier is not null
                    && !string.Equals(name.DatabaseIdentifier.Value, databaseName, StringComparison.OrdinalIgnoreCase)
            )
            {
                Report(
                    "warning",
                    "EXTERNAL_DATABASE_REFERENCE",
                    source.Path,
                    name,
                    $"The {role} references another database/server. It was not merged into {databaseName}."
                );
                return null;
            }
            if (name.BaseIdentifier is null || string.IsNullOrEmpty(name.BaseIdentifier.Value))
            {
                Report("error", "OBJECT_NAME_UNRESOLVED", source.Path, name, $"The {role} has an incomplete identifier.");
                return null;
            }
            if (name.SchemaIdentifier is null && role != "table")
            {
                Report(
                    "warning",
                    role == "foreign key target" ? "FOREIGN_KEY_SCHEMA_UNRESOLVED" : "OBJECT_SCHEMA_UNRESOLVED",
                    source.Path,
                    name,
                    $"The {role} has no explicit schema. Static analysis cannot safely bind the session default schema."
                );
                return null;
            }
            if (name.SchemaIdentifier is null)
                Report(
                    "warning",
                    "DEFAULT_SCHEMA_ASSUMED",
                    source.Path,
                    name,
                    $"Unqualified {role} {Quote(name.BaseIdentifier.Value)} is interpreted in schema {Quote(defaultSchema)}; the session default schema is unavailable."
                );
            return (name.SchemaIdentifier?.Value ?? defaultSchema, name.BaseIdentifier.Value);
        }

        string Id(string schema, string name) => $"{Quote(databaseName)}.{Quote(schema)}.{Quote(name)}";
        void Report(string severity, string code, string path, TSqlFragment fragment, string message) =>
            diagnostics.Add(new(severity, code, $"{path}({fragment.StartLine},{fragment.StartColumn}): {message}"));
    }

    private static string FormatType(DataTypeReference? type)
    {
        if (type is null)
            return "unresolved";
        var name = type is SqlDataTypeReference builtIn
            ? builtIn.SqlDataTypeOption.ToString().ToLowerInvariant() switch
            {
                "sqlvariant" => "sql_variant",
                var value => value,
            }
            : string.Join(".", type.Name.Identifiers.Select(identifier => Quote(identifier.Value)));
        if (type is ParameterizedDataTypeReference parameterized && parameterized.Parameters.Count != 0)
            name += "(" + string.Join(",", parameterized.Parameters.Select(parameter => parameter.Value.ToLowerInvariant())) + ")";
        return name;
    }

    private static string Quote(string value) => "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private sealed record SourceStatement(string Path, TSqlStatement Statement, string DefaultSchema);

    private sealed record PendingForeignKey(
        string FromTableId,
        ForeignKeyConstraintDefinition Definition,
        SourceStatement Source,
        string? InlineColumn
    );

    private sealed class TableState(string id, string schema, string name)
    {
        public string Id { get; } = id;
        public string Schema { get; } = schema;
        public string Name { get; } = name;
        public List<SchemaColumn> Columns { get; } = [];
        public List<string> PrimaryKey { get; } = [];
        public List<string[]> UniqueKeys { get; } = [];
        public bool Invalid { get; set; }
        public bool SchemaWasAssumed { get; init; }
    }
}
