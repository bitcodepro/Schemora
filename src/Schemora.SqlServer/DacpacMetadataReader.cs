using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.SqlServer.Dac.Model;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlIndex = Microsoft.SqlServer.Dac.Model.Index;

namespace Schemora.SqlServer;

internal sealed record DacpacExtendedMetadata(
    IReadOnlyList<SchemaObject> Objects,
    IReadOnlyList<SchemaIndex> Indexes,
    IReadOnlyList<SchemaObjectDependency> Dependencies,
    IReadOnlyDictionary<string, IReadOnlyList<SchemaUniqueKey>> UniqueKeys
);

internal static class DacpacMetadataReader
{
    internal static DacpacExtendedMetadata Read(TSqlModel model, string database, string path, List<SchemaDiagnostic> diagnostics)
    {
        var sourceObjects =
            new List<(
                TSqlObject Object,
                string Kind,
                ModelRelationshipClass? Columns,
                ModelRelationshipClass? Parameters,
                ModelRelationshipClass? Dependencies
            )>();
        Add(View.TypeClass, "view", View.Columns, null, View.BodyDependencies);
        Add(Procedure.TypeClass, "procedure", null, Procedure.Parameters, Procedure.BodyDependencies);
        Add(ScalarFunction.TypeClass, "scalarFunction", null, ScalarFunction.Parameters, ScalarFunction.BodyDependencies);
        Add(
            TableValuedFunction.TypeClass,
            "tableFunction",
            TableValuedFunction.Columns,
            TableValuedFunction.Parameters,
            TableValuedFunction.BodyDependencies
        );
        Add(Synonym.TypeClass, "synonym", null, null, Synonym.ForObject);
        var definitionCharacters = 0;
        var objects = new List<SchemaObject>();
        foreach (var entry in sourceObjects.OrderBy(item => Id(database, item.Object), StringComparer.Ordinal))
        {
            var source = entry.Object.GetSourceInformation();
            var sourcePath = string.IsNullOrWhiteSpace(source?.SourceName) ? path : source.SourceName;
            var partial = false;
            string? definition = null;
            if (entry.Object.TryGetScript(out var script))
            {
                var bytes = Encoding.UTF8.GetByteCount(script);
                if (
                    bytes <= SchemaMetadataLimits.MaximumDefinitionBytes
                    && (long)definitionCharacters + bytes <= SchemaMetadataLimits.MaximumDefinitionsBytes
                )
                {
                    definition = script;
                    definitionCharacters += bytes;
                }
                else
                {
                    partial = true;
                    Report(
                        "OBJECT_DEFINITION_LIMIT",
                        "An object definition was omitted because it exceeds 256 KiB UTF-8 or the snapshot's 32 MiB UTF-8 definition budget."
                    );
                }
            }
            else
            {
                partial = true;
                Report("OBJECT_DEFINITION_UNAVAILABLE", "DacFx could not produce source SQL for a schema object.");
            }
            var columns = entry.Columns is null
                ? []
                : entry
                    .Object.GetReferenced(entry.Columns)
                    .Select(column =>
                    {
                        var type = ColumnType(column);
                        if (type == "unresolved")
                            partial = true;
                        // Derived columns do not always have a nullability/type contract in a DACPAC model.
                        var nullable = column.ObjectType == Column.TypeClass ? column.GetProperty<bool>(Column.Nullable) : true;
                        return new SchemaColumn(Last(column), type, nullable);
                    })
                    .ToArray();
            var parameters = entry.Parameters is null
                ? []
                : entry
                    .Object.GetReferenced(entry.Parameters)
                    .Select(parameter => new SchemaParameter(
                        Last(parameter),
                        Format(parameter, Parameter.DataType, Parameter.Length, Parameter.Precision, Parameter.Scale, Parameter.IsMax),
                        parameter.GetProperty<bool>(Parameter.IsOutput),
                        parameter.GetProperty<bool>(Parameter.IsNullable)
                    ))
                    .ToArray();
            string? returnType = null;
            if (entry.Kind == "scalarFunction")
            {
                if (definition is not null)
                {
                    var parsed = new TSql170Parser(true).Parse(new StringReader(definition), out var errors) as TSqlScript;
                    if (
                        errors.Count == 0
                        && parsed
                            ?.Batches.SelectMany(batch => batch.Statements)
                            .OfType<CreateFunctionStatement>()
                            .SingleOrDefault()
                            ?.ReturnType
                            is ScalarFunctionReturnType scalar
                    )
                        returnType = SqlMetadataNames.FormatType(scalar.DataType);
                }
                if (returnType is null)
                {
                    returnType = "unresolved";
                    partial = true;
                }
            }
            var target =
                entry.Kind == "synonym"
                    ? entry
                        .Object.GetReferencedRelationshipInstances(Synonym.ForObject, DacExternalQueryScopes.All)
                        .Select(reference => string.Join('.', reference.ObjectName.Parts.Select(SqlMetadataNames.Quote)))
                        .FirstOrDefault()
                    : null;
            objects.Add(
                new(
                    Id(database, entry.Object),
                    Schema(entry.Object),
                    Last(entry.Object),
                    entry.Kind,
                    columns,
                    parameters,
                    returnType,
                    target,
                    definition,
                    sourcePath,
                    source?.StartLine > 0 ? source.StartLine : null,
                    partial
                )
            );
            if (columns.Any(column => column.DataType == "unresolved"))
                Report(
                    "OBJECT_COLUMN_METADATA_PARTIAL",
                    "Some derived columns have no resolved DacFx data type; use live SQL catalog metadata for their authoritative type/nullability."
                );
        }
        var indexes = new List<SchemaIndex>();
        var keys = new Dictionary<string, IReadOnlyList<SchemaUniqueKey>>(StringComparer.Ordinal);
        var uniqueGroups = new Dictionary<string, List<SchemaUniqueKey>>(StringComparer.Ordinal);
        foreach (
            var key in model
                .GetObjects(DacQueryScopes.UserDefined, UniqueConstraint.TypeClass)
                .OrderBy(item => Id(database, item), StringComparer.Ordinal)
        )
        {
            var host = key.GetReferenced(UniqueConstraint.Host).SingleOrDefault();
            if (host is null)
                continue;
            var hostId = Id(database, host);
            if (!uniqueGroups.TryGetValue(hostId, out var values))
                uniqueGroups[hostId] = values = [];
            values.Add(new(Id(database, key), Last(key), key.GetReferenced(UniqueConstraint.Columns).Select(Last).ToArray()));
            indexes.Add(
                new(
                    IndexId(hostId, Last(key)),
                    hostId,
                    Last(key),
                    key.GetProperty<bool>(UniqueConstraint.Clustered) ? "clustered" : "nonclustered",
                    true,
                    false,
                    IndexColumns(key, UniqueConstraint.Columns, UniqueConstraint.ColumnsRelationship.Ascending),
                    []
                )
            );
        }
        foreach (var group in uniqueGroups)
            keys[group.Key] = group.Value;
        foreach (var key in model.GetObjects(DacQueryScopes.UserDefined, PrimaryKeyConstraint.TypeClass))
        {
            var host = key.GetReferenced(PrimaryKeyConstraint.Host).SingleOrDefault();
            if (host is null)
                continue;
            var hostId = Id(database, host);
            indexes.Add(
                new(
                    IndexId(hostId, Last(key)),
                    hostId,
                    Last(key),
                    key.GetProperty<bool>(PrimaryKeyConstraint.Clustered) ? "clustered" : "nonclustered",
                    true,
                    true,
                    IndexColumns(key, PrimaryKeyConstraint.Columns, PrimaryKeyConstraint.ColumnsRelationship.Ascending),
                    []
                )
            );
        }
        foreach (var index in model.GetObjects(DacQueryScopes.UserDefined, SqlIndex.TypeClass))
        {
            var host = index.GetReferenced(SqlIndex.IndexedObject).SingleOrDefault();
            if (host is null)
                continue;
            var hostId = Id(database, host);
            indexes.Add(
                new(
                    IndexId(hostId, Last(index)),
                    hostId,
                    Last(index),
                    index.GetProperty<bool>(SqlIndex.Clustered) ? "clustered" : "nonclustered",
                    index.GetProperty<bool>(SqlIndex.Unique),
                    false,
                    IndexColumns(index, SqlIndex.Columns, SqlIndex.ColumnsRelationship.Ascending),
                    index.GetReferenced(SqlIndex.IncludedColumns).Select(Last).ToArray(),
                    ReadFilter(index)
                )
            );
        }
        foreach (var index in model.GetObjects(DacQueryScopes.UserDefined, ColumnStoreIndex.TypeClass))
        {
            var host = index.GetReferenced(ColumnStoreIndex.IndexedObject).SingleOrDefault();
            if (host is null)
                continue;
            var hostId = Id(database, host);
            indexes.Add(
                new(
                    IndexId(hostId, Last(index)),
                    hostId,
                    Last(index),
                    "columnstore",
                    false,
                    false,
                    IndexColumns(index, ColumnStoreIndex.Columns, ColumnStoreIndex.ColumnsRelationship.Ascending),
                    [],
                    ReadFilter(index)
                )
            );
        }
        var known = model
            .GetObjects(DacQueryScopes.UserDefined, Table.TypeClass)
            .Concat(sourceObjects.Select(item => item.Object))
            .ToDictionary(item => string.Join("\0", item.Name.Parts), item => Id(database, item), StringComparer.Ordinal);
        var knownColumnParents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in model.GetObjects(DacQueryScopes.UserDefined, Table.TypeClass))
        foreach (var column in table.GetReferenced(Table.Columns))
            knownColumnParents[string.Join("\0", column.Name.Parts)] = Id(database, table);
        foreach (var entry in sourceObjects.Where(entry => entry.Columns is not null))
        foreach (var column in entry.Object.GetReferenced(entry.Columns!))
            knownColumnParents[string.Join("\0", column.Name.Parts)] = Id(database, entry.Object);
        var knownNamesById = known.ToDictionary(pair => pair.Value, pair => pair.Key.Split('\0'), StringComparer.Ordinal);
        if (model.GetObjects(DacQueryScopes.UserDefined, XmlIndex.TypeClass, SpatialIndex.TypeClass).Any())
            Report(
                "INDEX_KIND_METADATA_PARTIAL",
                "XML/spatial indexes are not yet represented by the DacFx index model; use catalog metadata for their inventory."
            );
        var dependencies = new Dictionary<string, SchemaObjectDependency>(StringComparer.Ordinal);
        foreach (var entry in sourceObjects)
        {
            if (entry.Dependencies is null)
                continue;
            var fromId = Id(database, entry.Object);
            var source = entry.Object.GetSourceInformation();
            foreach (var reference in entry.Object.GetReferencedRelationshipInstances(entry.Dependencies, DacExternalQueryScopes.All))
            {
                var parts = reference.ObjectName?.Parts;
                if (parts is null || parts.Count == 0)
                    continue;
                if (
                    reference.Object is { } value
                    && (value.ObjectType == Parameter.TypeClass || value.ObjectType == DataType.TypeClass || value.Name.Parts.Count == 1)
                )
                    continue;
                var targetParts = parts.ToArray();
                string? targetId = null;
                // Only actual same-model objects/columns bind locally. A three-part unresolved name may
                // be database.schema.object, not a local schema.table.column prefix.
                if (reference.Object is { } referenced)
                {
                    var identity = string.Join("\0", referenced.Name.Parts);
                    if (!known.TryGetValue(identity, out targetId))
                        knownColumnParents.TryGetValue(identity, out targetId);
                    if (targetId is not null)
                        targetParts = knownNamesById[targetId];
                }
                if (
                    targetId == fromId
                    || targetId is null && (parts.Count < 2 || parts.Any(part => part.StartsWith('@') || part.StartsWith('#')))
                )
                    continue;
                var targetName = string.Join('.', targetParts.Select(SqlMetadataNames.Quote));
                var kind = entry.Kind == "synonym" ? "synonym-target" : "dacfx-reference";
                var id = DependencyId(fromId, targetName, kind);
                if (dependencies.Count >= 200_000)
                {
                    Report("OBJECT_DEPENDENCY_LIMIT", "Object dependency evidence reached 200000 entries; the result is partial.");
                    break;
                }
                dependencies.TryAdd(
                    id,
                    new(
                        id,
                        fromId,
                        targetId,
                        targetName,
                        kind,
                        string.IsNullOrWhiteSpace(source?.SourceName) ? path : source.SourceName,
                        source?.StartLine > 0 ? source.StartLine : null,
                        targetId is null ? 0.5 : 1
                    )
                );
                if (targetId is null)
                    Report(
                        "OBJECT_DEPENDENCY_UNRESOLVED",
                        "An external or unresolved object reference is retained as evidence without a fabricated local target."
                    );
            }
        }
        return new(
            objects,
            indexes.OrderBy(index => index.Id, StringComparer.Ordinal).ToArray(),
            dependencies.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(),
            keys
        );

        void Add(
            ModelTypeClass type,
            string kind,
            ModelRelationshipClass? columns,
            ModelRelationshipClass? parameters,
            ModelRelationshipClass? deps
        ) =>
            sourceObjects.AddRange(
                model.GetObjects(DacQueryScopes.UserDefined, type).Select(value => (value, kind, columns, parameters, deps))
            );
        void Report(string code, string message)
        {
            if (!diagnostics.Any(item => item.Code == code))
                diagnostics.Add(new("warning", code, message));
        }
    }

    private static SchemaIndexColumn[] IndexColumns(TSqlObject value, ModelRelationshipClass relationship, ModelPropertyClass ascending) =>
        value
            .GetReferencedRelationshipInstances(relationship)
            .Select(reference => new SchemaIndexColumn(
                reference.Object is { } column ? Last(column) : reference.ObjectName.Parts.Last(),
                !reference.GetProperty<bool>(ascending)
            ))
            .ToArray();

    private static string ColumnType(TSqlObject column)
    {
        try
        {
            return Format(column, Column.DataType, Column.Length, Column.Precision, Column.Scale, Column.IsMax);
        }
        catch (ArgumentException)
        {
            return "unresolved";
        }
    }

    private static string Format(
        TSqlObject value,
        ModelRelationshipClass typeReference,
        ModelPropertyClass length,
        ModelPropertyClass precision,
        ModelPropertyClass scale,
        ModelPropertyClass isMax
    )
    {
        var type = value.GetReferenced(typeReference, DacQueryScopes.All).SingleOrDefault();
        if (type is null)
            return "unresolved";
        if (type.Name.Parts.Count > 1)
            return string.Join('.', type.Name.Parts.Select(SqlMetadataNames.Quote));
        var name = Last(type).ToLowerInvariant();
        return name switch
        {
            "varchar" or "nvarchar" or "char" or "nchar" or "binary" or "varbinary" => name
                + "("
                + (value.GetProperty<bool>(isMax) ? "max" : value.GetProperty<int>(length).ToString(CultureInfo.InvariantCulture))
                + ")",
            "decimal" or "numeric" => name + "(" + value.GetProperty<int>(precision) + "," + value.GetProperty<int>(scale) + ")",
            "time" or "datetime2" or "datetimeoffset" => name + "(" + value.GetProperty<int>(scale) + ")",
            "float" => name + "(" + value.GetProperty<int>(precision) + ")",
            _ => name,
        };
    }

    internal static string IndexId(string objectId, string name) => objectId + ":index:" + SqlMetadataNames.Quote(name);

    internal static string DependencyId(string source, string target, string kind) =>
        "dependency:"
        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + "\0" + target + "\0" + kind))).ToLowerInvariant();

    private static string Id(string database, TSqlObject value) =>
        string.Join('.', new[] { database }.Concat(value.Name.Parts).Select(SqlMetadataNames.Quote));

    private static string Schema(TSqlObject value) => value.Name.Parts.Count > 1 ? value.Name.Parts[^2] : "dbo";

    private static string Last(TSqlObject value) => value.Name.Parts.LastOrDefault() ?? "(unnamed)";

    private static string? ReadFilter(TSqlObject value)
    {
        if (!value.TryGetAst(out var script))
            return null;
        var statement = script.Batches.SelectMany(batch => batch.Statements).FirstOrDefault();
        var filter = statement switch
        {
            CreateIndexStatement index => index.FilterPredicate,
            CreateColumnStoreIndexStatement columnstore => columnstore.FilterPredicate,
            _ => null,
        };
        if (filter is null)
            return null;
        new Sql170ScriptGenerator().GenerateScript(filter, out var text);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}

internal static class SqlMetadataNames
{
    internal static string Quote(string value) => "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]";

    internal static string FormatType(DataTypeReference? type)
    {
        if (type is null)
            return "unresolved";
        var name = type is SqlDataTypeReference builtIn
            ? builtIn.SqlDataTypeOption.ToString().ToLowerInvariant() switch
            {
                "sqlvariant" => "sql_variant",
                var value => value,
            }
            : string.Join('.', type.Name.Identifiers.Select(identifier => Quote(identifier.Value)));
        if (type is ParameterizedDataTypeReference parameterized && parameterized.Parameters.Count > 0)
            name += "(" + string.Join(',', parameterized.Parameters.Select(parameter => parameter.Value.ToLowerInvariant())) + ")";
        return name;
    }
}
