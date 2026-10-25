using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Schemora.SqlServer;

internal sealed record SqlMetadataSource(string Path, TSqlStatement Statement, string DefaultSchema);

internal sealed record StaticExtendedMetadata(
    IReadOnlyList<SchemaObject> Objects,
    IReadOnlyList<SchemaIndex> Indexes,
    IReadOnlyList<SchemaObjectDependency> Dependencies,
    IReadOnlyDictionary<string, IReadOnlyList<SchemaUniqueKey>> UniqueKeys,
    IReadOnlyList<string> ConflictingIds
);

internal static class StaticSqlMetadataReader
{
    internal static bool Supports(TSqlStatement statement) =>
        statement
            is ViewStatementBody
                or ProcedureStatementBody
                or FunctionStatementBody
                or CreateSynonymStatement
                or CreateIndexStatement
                or CreateColumnStoreIndexStatement;

    internal static StaticExtendedMetadata Read(
        string database,
        IReadOnlyList<SqlMetadataSource> sources,
        IReadOnlyList<SchemaTable> tables,
        List<SchemaDiagnostic> diagnostics
    )
    {
        var objects = new Dictionary<string, SchemaObject>(StringComparer.Ordinal);
        var objectSources = new Dictionary<string, SqlMetadataSource>(StringComparer.Ordinal);
        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        var definitionBytes = 0;
        foreach (
            var source in sources.Where(source =>
                Supports(source.Statement) && source.Statement is not (CreateIndexStatement or CreateColumnStoreIndexStatement)
            )
        )
        {
            SchemaObjectName? name = source.Statement switch
            {
                ViewStatementBody view => view.SchemaObjectName,
                ProcedureStatementBody procedure => procedure.ProcedureReference.Name,
                FunctionStatementBody function => function.Name,
                CreateSynonymStatement synonym => synonym.Name,
                _ => null,
            };
            if (name is null)
                continue;
            var resolved = ResolveName(name, source, allowAssumedSchema: true);
            if (resolved is null)
                continue;
            var id = Id(resolved.Value.Schema, resolved.Value.Name);
            if (conflicts.Contains(id) || objects.ContainsKey(id) || tables.Any(table => table.Id == id))
            {
                conflicts.Add(id);
                objects.Remove(id);
                objectSources.Remove(id);
                Report(
                    "OBJECT_DEFINITION_CONFLICT",
                    source,
                    name,
                    "Conflicting table/view/routine/synonym definitions were excluded; static analysis cannot choose one migration state."
                );
                continue;
            }
            var kind = source.Statement switch
            {
                ViewStatementBody => "view",
                ProcedureStatementBody => "procedure",
                CreateSynonymStatement => "synonym",
                FunctionStatementBody function when function.ReturnType is ScalarFunctionReturnType => "scalarFunction",
                _ => "tableFunction",
            };
            var columns = source.Statement switch
            {
                ViewStatementBody view when view.Columns.Count > 0 => view
                    .Columns.Select(column => new SchemaColumn(column.Value, "unresolved", true))
                    .ToArray(),
                ViewStatementBody view => QueryColumns(view.SelectStatement, source),
                FunctionStatementBody { ReturnType: SelectFunctionReturnType function } => QueryColumns(function.SelectStatement, source),
                FunctionStatementBody { ReturnType: TableValuedFunctionReturnType function } => function
                    .DeclareTableVariableBody.Definition.ColumnDefinitions.Select(column => new SchemaColumn(
                        column.ColumnIdentifier.Value,
                        SqlMetadataNames.FormatType(column.DataType),
                        column.Constraints.OfType<NullableConstraintDefinition>().FirstOrDefault()?.Nullable ?? true
                    ))
                    .ToArray(),
                _ => [],
            };
            var parameters = source.Statement switch
            {
                ProcedureStatementBody procedure => procedure.Parameters,
                FunctionStatementBody function => function.Parameters,
                _ => null,
            };
            var parameterValues =
                parameters
                    ?.Select(parameter => new SchemaParameter(
                        parameter.VariableName.Value,
                        SqlMetadataNames.FormatType(parameter.DataType),
                        parameter.Modifier == ParameterModifier.Output,
                        parameter.Nullable?.Nullable ?? true
                    ))
                    .ToArray()
                ?? [];
            var returnType = source.Statement is FunctionStatementBody { ReturnType: ScalarFunctionReturnType scalar }
                ? SqlMetadataNames.FormatType(scalar.DataType)
                : null;
            var target = source.Statement is CreateSynonymStatement synonymTarget ? Name(synonymTarget.ForName) : null;
            string? definition = SourceText(source.Statement);
            var bytes = Encoding.UTF8.GetByteCount(definition);
            if (
                bytes > SchemaMetadataLimits.MaximumDefinitionBytes
                || (long)definitionBytes + bytes > SchemaMetadataLimits.MaximumDefinitionsBytes
            )
            {
                definition = null;
                Report(
                    "OBJECT_DEFINITION_LIMIT",
                    source,
                    source.Statement,
                    "Object source SQL was omitted because the 256 KiB object or 32 MiB snapshot definition limit was reached."
                );
            }
            else
                definitionBytes += bytes;
            objects.Add(
                id,
                new(
                    id,
                    resolved.Value.Schema,
                    resolved.Value.Name,
                    kind,
                    columns,
                    parameterValues,
                    returnType,
                    target,
                    definition,
                    source.Path,
                    source.Statement.StartLine,
                    true
                )
            );
            objectSources[id] = source;
            if (columns.Any(column => column.DataType == "unresolved"))
                Report(
                    "OBJECT_COLUMN_METADATA_PARTIAL",
                    source,
                    source.Statement,
                    "View/inline-function output names are parsed, but data types/nullability require DacFx/catalog binding; unknown columns are not fabricated."
                );
            if (
                source.Statement.GetType().Name.StartsWith("Alter", StringComparison.Ordinal)
                || source.Statement.GetType().Name.StartsWith("CreateOrAlter", StringComparison.Ordinal)
            )
                Report(
                    "OBJECT_MIGRATION_STATE_PARTIAL",
                    source,
                    source.Statement,
                    "ALTER/CREATE OR ALTER source describes one possible object state; migration order was not executed."
                );
        }
        var known = tables
            .Where(table => !conflicts.Contains(table.Id))
            .ToDictionary(table => table.Id, table => (table.Schema, table.Name, table.Columns), StringComparer.Ordinal);
        foreach (var value in objects.Values)
            known[value.Id] = (value.Schema, value.Name, value.Columns);
        var indexes = new Dictionary<string, SchemaIndex>(StringComparer.Ordinal);
        var indexConflicts = new HashSet<string>(StringComparer.Ordinal);
        var keyOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var keyConflicts = new HashSet<string>(StringComparer.Ordinal);
        var uniqueKeys = new Dictionary<string, List<SchemaUniqueKey>>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            switch (source.Statement)
            {
                case CreateTableStatement table when table.Definition is not null:
                    AddTableElements(table.SchemaObjectName, table.Definition, source);
                    break;
                case AlterTableAddTableElementStatement table:
                    AddTableElements(table.SchemaObjectName, table.Definition, source);
                    break;
                case CreateIndexStatement index:
                    AddIndex(
                        index.OnName,
                        index.Name.Value,
                        index.Clustered == true ? "clustered" : "nonclustered",
                        index.Unique,
                        false,
                        SortColumns(index.Columns),
                        index.IncludeColumns.Select(ColumnName).ToArray(),
                        Filter(index.FilterPredicate),
                        source
                    );
                    break;
                case CreateColumnStoreIndexStatement index:
                    AddIndex(
                        index.OnName,
                        index.Name.Value,
                        "columnstore",
                        false,
                        false,
                        index.Columns.Select(column => new SchemaIndexColumn(ColumnName(column), false)).ToArray(),
                        [],
                        Filter(index.FilterPredicate),
                        source
                    );
                    break;
            }
        }
        var dependencies = new Dictionary<string, SchemaObjectDependency>(StringComparer.Ordinal);
        foreach (
            var (fromId, source) in objectSources
                .Concat(
                    sources
                        .Where(source => source.Statement is CreateTableStatement)
                        .Select(source =>
                        {
                            var table = (CreateTableStatement)source.Statement;
                            var name = ResolveName(table.SchemaObjectName, source, true);
                            return new KeyValuePair<string, SqlMetadataSource>(
                                name is null ? "" : Id(name.Value.Schema, name.Value.Name),
                                source
                            );
                        })
                )
                .Where(pair => known.ContainsKey(pair.Key))
        )
        {
            var collector = new DependencyReferences();
            source.Statement.Accept(collector);
            foreach (var reference in collector.Values)
            {
                var name = reference.Name;
                if (name.BaseIdentifier is null)
                    continue;
                if (name.BaseIdentifier?.Value.StartsWith('#') == true || name.BaseIdentifier?.Value.StartsWith('@') == true)
                    continue;
                if (name.SchemaIdentifier is null && collector.Ctes.Contains(name.BaseIdentifier?.Value ?? ""))
                    continue;
                if (
                    name.SchemaIdentifier?.Value is "sys" or "INFORMATION_SCHEMA"
                    && name.DatabaseIdentifier is null
                    && name.ServerIdentifier is null
                )
                    continue;
                var targetName = Name(name);
                string? targetId = null;
                if (
                    name.SchemaIdentifier is not null
                    && name.ServerIdentifier is null
                    && (
                        name.DatabaseIdentifier is null
                        || string.Equals(name.DatabaseIdentifier.Value, database, StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    var possible = Id(name.SchemaIdentifier.Value, name.BaseIdentifier!.Value);
                    if (known.ContainsKey(possible))
                    {
                        targetId = possible;
                        targetName =
                            SqlMetadataNames.Quote(name.SchemaIdentifier.Value) + "." + SqlMetadataNames.Quote(name.BaseIdentifier!.Value);
                    }
                }
                if (targetId == fromId)
                    continue;
                var kind = source.Statement is CreateSynonymStatement ? "synonym-target" : "static-reference";
                var identity = DacpacMetadataReader.DependencyId(fromId, targetName, kind);
                if (dependencies.Count >= 200_000)
                {
                    Report(
                        "OBJECT_DEPENDENCY_LIMIT",
                        source,
                        reference.Fragment,
                        "Object dependency evidence reached 200000 entries; the result is partial."
                    );
                    break;
                }
                dependencies.TryAdd(
                    identity,
                    new(
                        identity,
                        fromId,
                        targetId,
                        targetName,
                        kind,
                        source.Path,
                        reference.Fragment.StartLine,
                        targetId is null ? 0.4 : 0.8
                    )
                );
                if (targetId is null)
                    Report(
                        "OBJECT_DEPENDENCY_UNRESOLVED",
                        source,
                        reference.Fragment,
                        "An external, unqualified or unknown object reference was retained without inventing a local target."
                    );
            }
            if (collector.DynamicSql)
                Report(
                    "DYNAMIC_OBJECT_DEPENDENCIES_UNAVAILABLE",
                    source,
                    source.Statement,
                    "Dynamic EXEC SQL is not executed or parsed as schema dependency facts."
                );
        }
        return new(
            objects.Values.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray(),
            indexes.Values.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray(),
            dependencies.Values.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray(),
            uniqueKeys.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<SchemaUniqueKey>)pair.Value.ToArray(), StringComparer.Ordinal),
            conflicts.ToArray()
        );

        void AddTableElements(SchemaObjectName name, TableDefinition definition, SqlMetadataSource source)
        {
            var resolved = ResolveName(name, source, true);
            if (resolved is null || !known.ContainsKey(Id(resolved.Value.Schema, resolved.Value.Name)))
                return;
            foreach (var column in definition.ColumnDefinitions)
            foreach (var key in column.Constraints.OfType<UniqueConstraintDefinition>())
                AddKey(key, column.ColumnIdentifier.Value);
            foreach (var key in definition.TableConstraints.OfType<UniqueConstraintDefinition>())
                AddKey(key, null);
            foreach (var index in definition.Indexes)
            {
                var kind = index.IndexType?.IndexTypeKind.ToString() ?? "NonClustered";
                AddIndex(
                    name,
                    index.Name.Value,
                    kind.Contains("ColumnStore", StringComparison.OrdinalIgnoreCase) ? "columnstore"
                        : kind == "Clustered" ? "clustered"
                        : kind == "NonClustered" ? "nonclustered"
                        : "other",
                    index.Unique,
                    false,
                    SortColumns(index.Columns),
                    index.IncludeColumns.Select(ColumnName).ToArray(),
                    Filter(index.FilterPredicate),
                    source
                );
            }
            void AddKey(UniqueConstraintDefinition key, string? inline)
            {
                var columns = SortColumns(key.Columns);
                if (columns.Length == 0 && inline is not null)
                    columns = [new(inline, false)];
                var keyName =
                    key.ConstraintIdentifier?.Value
                    ?? (key.IsPrimaryKey ? "(unnamed primary key)" : "(unnamed unique key)")
                        + " "
                        + string.Join(',', columns.Select(column => column.Name));
                if (key.ConstraintIdentifier is null)
                    Report(
                        "CONSTRAINT_NAME_UNRESOLVED",
                        source,
                        key,
                        "An unnamed key has a deterministic display identity; its eventual server-generated name is unknown."
                    );
                var id = Id(resolved.Value.Schema, resolved.Value.Name);
                if (!key.IsPrimaryKey)
                {
                    if (!uniqueKeys.TryGetValue(id, out var values))
                        uniqueKeys[id] = values = [];
                    var keyId = Id(resolved.Value.Schema, keyName);
                    if (keyConflicts.Contains(keyId) || keyOwners.ContainsKey(keyId))
                    {
                        keyConflicts.Add(keyId);
                        if (keyOwners.Remove(keyId, out var previousOwner))
                            uniqueKeys[previousOwner].RemoveAll(value => value.Id == keyId);
                        Report(
                            "UNIQUE_KEY_DEFINITION_CONFLICT",
                            source,
                            key,
                            "Multiple unique key definitions share one identity; no migration state was guessed."
                        );
                    }
                    else
                    {
                        keyOwners.Add(keyId, id);
                        values.Add(new(keyId, keyName, columns.Select(column => column.Name).ToArray()));
                    }
                }
                AddIndex(
                    name,
                    keyName,
                    key.Clustered is null ? "other"
                        : key.Clustered == true ? "clustered"
                        : "nonclustered",
                    true,
                    key.IsPrimaryKey,
                    columns,
                    [],
                    null,
                    source
                );
            }
        }
        void AddIndex(
            SchemaObjectName target,
            string name,
            string kind,
            bool unique,
            bool primary,
            SchemaIndexColumn[] columns,
            string[] included,
            string? filter,
            SqlMetadataSource source
        )
        {
            var resolved = ResolveName(target, source, true);
            if (resolved is null)
                return;
            var targetId = Id(resolved.Value.Schema, resolved.Value.Name);
            if (
                !known.TryGetValue(targetId, out var owner)
                || columns.Any(column => !owner.Columns.Any(value => value.Name == column.Name))
                || included.Any(column => !owner.Columns.Any(value => value.Name == column))
            )
            {
                Report("INDEX_TARGET_UNRESOLVED", source, target, "An index owner/column was unresolved and the index was excluded.");
                return;
            }
            var id = DacpacMetadataReader.IndexId(targetId, name);
            if (
                indexConflicts.Contains(id)
                || !indexes.TryAdd(id, new(id, targetId, name, kind, unique, primary, columns, included, filter))
            )
            {
                indexConflicts.Add(id);
                indexes.Remove(id);
                Report(
                    "INDEX_DEFINITION_CONFLICT",
                    source,
                    target,
                    "Multiple index definitions share one identity; no migration state was guessed."
                );
            }
        }
        (string Schema, string Name)? ResolveName(SchemaObjectName name, SqlMetadataSource source, bool allowAssumedSchema)
        {
            if (
                name.ServerIdentifier is not null
                || name.DatabaseIdentifier is not null
                    && !string.Equals(name.DatabaseIdentifier.Value, database, StringComparison.OrdinalIgnoreCase)
            )
            {
                Report("EXTERNAL_DATABASE_REFERENCE", source, name, "An external definition was not merged into this database.");
                return null;
            }
            if (name.BaseIdentifier is null || string.IsNullOrEmpty(name.BaseIdentifier.Value))
                return null;
            if (name.SchemaIdentifier is null && !allowAssumedSchema)
                return null;
            if (name.SchemaIdentifier is null)
                Report(
                    "DEFAULT_SCHEMA_ASSUMED",
                    source,
                    name,
                    "An unqualified definition uses an assumed default schema in this partial static snapshot."
                );
            return (name.SchemaIdentifier?.Value ?? source.DefaultSchema, name.BaseIdentifier!.Value);
        }
        string Id(string schema, string name) => string.Join('.', new[] { database, schema, name }.Select(SqlMetadataNames.Quote));
        void Report(string code, SqlMetadataSource source, TSqlFragment fragment, string message)
        {
            if (diagnostics.Count < 5000)
                diagnostics.Add(new("warning", code, $"{source.Path}({fragment.StartLine},{fragment.StartColumn}): {message}"));
        }
    }

    private static SchemaColumn[] QueryColumns(SelectStatement select, SqlMetadataSource source)
    {
        var query = select.QueryExpression;
        while (query is BinaryQueryExpression binary)
            query = binary.FirstQueryExpression;
        if (query is not QuerySpecification specification)
            return [];
        return specification
            .SelectElements.OfType<SelectScalarExpression>()
            .Select(element =>
                element.ColumnName?.Value
                ?? (
                    element.Expression is ColumnReferenceExpression column
                        ? column.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value
                        : null
                )
            )
            .Where(name => name is not null)
            .Select(name => new SchemaColumn(name!, "unresolved", true))
            .ToArray();
    }

    private static string Name(SchemaObjectName name) =>
        string.Join('.', name.Identifiers.Select(identifier => SqlMetadataNames.Quote(identifier.Value)));

    private static SchemaIndexColumn[] SortColumns(IEnumerable<ColumnWithSortOrder> columns) =>
        columns.Select(column => new SchemaIndexColumn(ColumnName(column.Column), column.SortOrder == SortOrder.Descending)).ToArray();

    private static string ColumnName(ColumnReferenceExpression column) => column.MultiPartIdentifier.Identifiers.Last().Value;

    private static string? Filter(TSqlFragment? fragment)
    {
        if (fragment is null)
            return null;
        new Sql170ScriptGenerator().GenerateScript(fragment, out var result);
        return result;
    }

    private static string SourceText(TSqlFragment fragment) =>
        string.Concat(
            fragment
                .ScriptTokenStream.Skip(fragment.FirstTokenIndex)
                .Take(fragment.LastTokenIndex - fragment.FirstTokenIndex + 1)
                .Select(token => token.Text)
        );

    private sealed class DependencyReferences : TSqlFragmentVisitor
    {
        public List<(SchemaObjectName Name, TSqlFragment Fragment)> Values { get; } = [];
        public HashSet<string> Ctes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool DynamicSql { get; private set; }

        public override void ExplicitVisit(CommonTableExpression value)
        {
            Ctes.Add(value.ExpressionName.Value);
            base.ExplicitVisit(value);
        }

        public override void ExplicitVisit(NamedTableReference value)
        {
            Values.Add((value.SchemaObject, value));
            base.ExplicitVisit(value);
        }

        public override void ExplicitVisit(SchemaObjectFunctionTableReference value)
        {
            Values.Add((value.SchemaObject, value));
            base.ExplicitVisit(value);
        }

        public override void ExplicitVisit(CreateSynonymStatement value)
        {
            Values.Add((value.ForName, value));
        }

        public override void ExplicitVisit(FunctionCall value)
        {
            if (value.CallTarget is MultiPartIdentifierCallTarget target)
            {
                var name = new SchemaObjectName();
                foreach (var identifier in target.MultiPartIdentifier.Identifiers)
                    name.Identifiers.Add(identifier);
                name.Identifiers.Add(value.FunctionName);
                Values.Add((name, value));
            }
            base.ExplicitVisit(value);
        }

        public override void ExplicitVisit(ExecuteStatement value)
        {
            if (
                value.ExecuteSpecification.ExecutableEntity is ExecutableProcedureReference procedure
                && procedure.ProcedureReference.ProcedureReference?.Name is { } name
            )
                Values.Add((name, value));
            else
                DynamicSql = true;
            base.ExplicitVisit(value);
        }
    }
}
