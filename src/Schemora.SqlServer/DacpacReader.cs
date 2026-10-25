using System.Globalization;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;

namespace Schemora.SqlServer;

/// <summary>Reads schema metadata without deploying a package or running its deployment scripts.</summary>
public sealed class DacpacReader
{
    public SchemaGraph Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("DACPAC file was not found.", fullPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".dacpac", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only .dacpac schema packages are accepted.", nameof(path));

        try
        {
            using var package = DacPackage.Load(fullPath);
            using var model = new TSqlModel(fullPath);
            var database = string.IsNullOrWhiteSpace(package.Name) ? Path.GetFileNameWithoutExtension(fullPath) : package.Name;
            var diagnostics = new List<SchemaDiagnostic>();
            var primaryKeys = model
                .GetObjects(DacQueryScopes.UserDefined, PrimaryKeyConstraint.TypeClass)
                .Select(key => new
                {
                    Host = key.GetReferenced(PrimaryKeyConstraint.Host).SingleOrDefault(),
                    Columns = key.GetReferenced(PrimaryKeyConstraint.Columns).Select(LastName).ToArray(),
                })
                .Where(key => key.Host is not null)
                .ToDictionary(key => ObjectId(database, key.Host!), key => (IReadOnlyList<string>)key.Columns, StringComparer.Ordinal);

            var tables = model
                .GetObjects(DacQueryScopes.UserDefined, Table.TypeClass)
                .OrderBy(table => ObjectId(database, table), StringComparer.Ordinal)
                .Select(table =>
                {
                    var id = ObjectId(database, table);
                    return new SchemaTable(
                        id,
                        SchemaName(table),
                        LastName(table),
                        table
                            .GetReferenced(Table.Columns)
                            .Select(column => new SchemaColumn(
                                LastName(column),
                                FormatDataType(column, diagnostics),
                                column.GetProperty<bool>(Column.Nullable)
                            ))
                            .ToArray(),
                        primaryKeys.GetValueOrDefault(id, Array.Empty<string>())
                    );
                })
                .ToArray();
            var tableIds = tables.Select(table => table.Id).ToHashSet(StringComparer.Ordinal);
            var relations = new List<SchemaRelation>();
            foreach (
                var foreignKey in model
                    .GetObjects(DacQueryScopes.UserDefined, ForeignKeyConstraint.TypeClass)
                    .OrderBy(key => ObjectId(database, key), StringComparer.Ordinal)
            )
            {
                var fromTable = foreignKey.GetReferenced(ForeignKeyConstraint.Host).SingleOrDefault();
                var toTable = foreignKey.GetReferenced(ForeignKeyConstraint.ForeignTable).SingleOrDefault();
                var fromColumns = foreignKey.GetReferenced(ForeignKeyConstraint.Columns).Select(LastName).ToArray();
                var toColumns = foreignKey.GetReferenced(ForeignKeyConstraint.ForeignColumns).Select(LastName).ToArray();
                if (
                    fromTable is null
                    || toTable is null
                    || fromColumns.Length == 0
                    || fromColumns.Length != toColumns.Length
                    || !tableIds.Contains(ObjectId(database, fromTable))
                    || !tableIds.Contains(ObjectId(database, toTable))
                )
                {
                    diagnostics.Add(
                        new SchemaDiagnostic(
                            "warning",
                            "FK_UNRESOLVED",
                            $"Foreign key {foreignKey.Name} could not be resolved completely; it was omitted from the graph."
                        )
                    );
                    continue;
                }
                relations.Add(
                    new SchemaRelation(
                        ObjectId(database, foreignKey),
                        LastName(foreignKey),
                        ObjectId(database, fromTable),
                        ObjectId(database, toTable),
                        fromColumns,
                        toColumns,
                        "declared-fk",
                        1.0
                    )
                );
            }
            var metadata = DacpacMetadataReader.Read(model, database, fullPath, diagnostics);
            tables = tables.Select(table => table with { UniqueKeys = metadata.UniqueKeys.GetValueOrDefault(table.Id, []) }).ToArray();
            return new SchemaGraph(
                database,
                fullPath,
                DateTimeOffset.UtcNow,
                tables,
                relations,
                diagnostics,
                metadata.Objects,
                metadata.Indexes,
                metadata.Dependencies
            );
        }
        catch (Exception exception)
            when (exception is not DacpacReadException
                && exception is not OutOfMemoryException
                && exception is not OperationCanceledException
            )
        {
            throw new DacpacReadException(
                "The DACPAC could not be read. Verify that it is a valid SQL schema package compatible with the installed DacFx.",
                exception
            );
        }
    }

    private static string FormatDataType(TSqlObject column, List<SchemaDiagnostic> diagnostics)
    {
        var dataType = column.GetReferenced(Column.DataType, DacQueryScopes.All).SingleOrDefault();
        if (dataType is null)
        {
            diagnostics.Add(
                new SchemaDiagnostic(
                    "warning",
                    "COLUMN_TYPE_UNRESOLVED",
                    $"Column {column.Name} has no resolved data type (for example, a computed expression)."
                )
            );
            return "unresolved";
        }
        var name = LastName(dataType);
        // Alias/user-defined types retain their schema instead of being presented as a built-in type.
        if (dataType.Name.Parts.Count > 1)
            return string.Join(".", dataType.Name.Parts.Select(Quote));
        return name.ToLowerInvariant() switch
        {
            "varchar" or "nvarchar" or "char" or "nchar" or "varbinary" or "binary" =>
                $"{name}({(column.GetProperty<bool>(Column.IsMax) ? "max" : column.GetProperty<int>(Column.Length).ToString(CultureInfo.InvariantCulture))})",
            "decimal" or "numeric" => $"{name}({column.GetProperty<int>(Column.Precision)},{column.GetProperty<int>(Column.Scale)})",
            "datetime2" or "datetimeoffset" or "time" => $"{name}({column.GetProperty<int>(Column.Scale)})",
            "float" => $"{name}({column.GetProperty<int>(Column.Precision)})",
            _ => name,
        };
    }

    private static string ObjectId(string database, TSqlObject value) =>
        string.Join(".", new[] { database }.Concat(value.Name.Parts).Select(Quote));

    private static string Quote(string value) => "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static string LastName(TSqlObject value) => value.Name.Parts[^1];

    private static string SchemaName(TSqlObject value) => value.Name.Parts.Count > 1 ? value.Name.Parts[^2] : "dbo";
}

public sealed class DacpacReadException(string message, Exception innerException) : Exception(message, innerException);
