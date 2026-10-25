using Schemora.SqlServer;

namespace Schemora.Relationships;

public sealed record RelationshipDatabase(string Id, string Name, SchemaGraph Graph);

public sealed record RelationshipTable(string DatabaseId, string DatabaseName, SchemaTable Table);

public sealed record RelationshipObject(string DatabaseId, string DatabaseName, SchemaObject Object);

public sealed record RelationEvidence(string Kind, string SourcePath, int? Line, string Detail);

public sealed record RelationshipEdge(
    SchemaRelation Relation,
    string Status,
    bool IsTrusted,
    IReadOnlyList<RelationEvidence> Evidence,
    string? ReviewReason = null
);

public sealed record RelationDecision(string RelationId, string Decision, string Reason, DateTimeOffset ChangedAt);

public sealed record RelationshipGraph(
    IReadOnlyList<RelationshipTable> Tables,
    IReadOnlyList<RelationshipEdge> Relations,
    IReadOnlyList<SchemaDiagnostic> Diagnostics,
    IReadOnlyList<RelationshipObject>? ReadableObjects = null
);

public sealed record EfSourceSet(string DatabaseId, IReadOnlyList<string> Paths, CSharpProjectContext? Context = null);

public sealed record JoinPath(
    IReadOnlyList<string> TableIds,
    IReadOnlyList<string> RelationIds,
    string JoinSql,
    IReadOnlyList<string> Explanations
);
