namespace Schemora.SqlServer;

public sealed record SchemaGraph(
    string DatabaseName,
    string SourcePath,
    DateTimeOffset ExtractedAt,
    IReadOnlyList<SchemaTable> Tables,
    IReadOnlyList<SchemaRelation> Relations,
    IReadOnlyList<SchemaDiagnostic> Diagnostics,
    IReadOnlyList<SchemaObject>? Objects = null,
    IReadOnlyList<SchemaIndex>? Indexes = null,
    IReadOnlyList<SchemaObjectDependency>? ObjectDependencies = null
);

public sealed record SchemaTable(
    string Id,
    string Schema,
    string Name,
    IReadOnlyList<SchemaColumn> Columns,
    IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<SchemaUniqueKey>? UniqueKeys = null
);

public sealed record SchemaColumn(string Name, string DataType, bool IsNullable);

public sealed record SchemaUniqueKey(string Id, string Name, IReadOnlyList<string> Columns);

public sealed record SchemaParameter(string Name, string DataType, bool IsOutput, bool IsNullable = true);

public sealed record SchemaObject(
    string Id,
    string Schema,
    string Name,
    string Kind,
    IReadOnlyList<SchemaColumn> Columns,
    IReadOnlyList<SchemaParameter>? Parameters = null,
    string? ReturnType = null,
    string? Target = null,
    string? Definition = null,
    string? SourcePath = null,
    int? Line = null,
    bool IsPartial = false
);

public sealed record SchemaIndexColumn(string Name, bool Descending);

public sealed record SchemaIndex(
    string Id,
    string ObjectId,
    string Name,
    string Kind,
    bool IsUnique,
    bool IsPrimaryKey,
    IReadOnlyList<SchemaIndexColumn> Columns,
    IReadOnlyList<string> IncludedColumns,
    string? Filter = null
);

public sealed record SchemaObjectDependency(
    string Id,
    string FromObjectId,
    string? ToObjectId,
    string TargetName,
    string Kind,
    string SourcePath,
    int? Line,
    double Confidence
);

public sealed record SchemaRelation(
    string Id,
    string Name,
    string FromTableId,
    string ToTableId,
    IReadOnlyList<string> FromColumns,
    IReadOnlyList<string> ToColumns,
    string Kind,
    double Confidence
);

public sealed record SchemaDiagnostic(string Severity, string Code, string Message);

public static class SchemaMetadataLimits
{
    public const int MaximumDefinitionBytes = 256 * 1024;
    public const int MaximumDefinitionsBytes = 32 * 1024 * 1024;
}
