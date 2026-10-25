using Schemora.SqlServer;

namespace Schemora.RecipeEngine;

public sealed record RecipeInputDefinition(
    string Name,
    string Type,
    int? MaxLength = null,
    string? Encoding = null,
    bool Deduplicate = false,
    bool PreserveOrder = true,
    string Strategy = "auto",
    string? Collation = null,
    int? Precision = null,
    int? Scale = null,
    // Only for {{value:...}} inputs: an empty value renders as a typed NULL.
    bool Optional = false
);

public sealed record RecipeDependency(string DatabaseId, string TableId, string? ColumnName = null, string? RelationId = null);

public sealed record RecipeDefinition(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> Tags,
    string Dialect,
    IReadOnlyList<string> DatabaseIds,
    int Version,
    string SqlTemplate,
    IReadOnlyList<RecipeInputDefinition> Inputs,
    IReadOnlyList<RecipeDependency>? Dependencies = null,
    int CompatibilityLevel = 130
);

public sealed record IdStatistics(int Total, int Valid, int Invalid, int Unique, int Duplicates, int OutputCount);

public sealed record ProcessedId(int Index, int Line, int Column, string Value);

public sealed record IdIssue(int Index, int Line, int Column, string Code, string Message, string? RawValue = null);

public sealed record IdParseResult(
    RecipeInputDefinition Input,
    IdStatistics Statistics,
    IReadOnlyList<ProcessedId> Values,
    IReadOnlyList<IdIssue> Errors
);

public sealed record SqlBoundParameter(string Name, string SqlType, string Value);

public sealed record RecipeRenderResult(
    string Sql,
    string Mode,
    string Strategy,
    IReadOnlyDictionary<string, IdStatistics> Statistics,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<SqlBoundParameter> Parameters,
    string? ParameterizedExample = null,
    string? DapperExample = null,
    int? RecipeVersion = null
);

public sealed record RecipeToken(string Kind, string InputName, int Start, int Length, int Line, int Column);

public sealed record RecipeValidationResult(
    string Status,
    IReadOnlyList<RecipeDependency> Dependencies,
    IReadOnlyList<SchemaDiagnostic> Diagnostics
);

public sealed class RecipeEngineException(string code, string message, IReadOnlyList<IdIssue>? errors = null) : Exception(message)
{
    public string Code { get; } = code;
    public IReadOnlyList<IdIssue> Errors { get; } = errors ?? [];
}
