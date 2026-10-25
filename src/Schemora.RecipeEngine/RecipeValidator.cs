using Microsoft.SqlServer.TransactSql.ScriptDom;
using Schemora.Relationships;
using Schemora.SqlServer;

namespace Schemora.RecipeEngine;

/// <summary>Validates recipe definitions against local schema metadata; never reads runtime ID values or executes SQL.</summary>
public sealed partial class RecipeValidator
{
    public RecipeValidationResult Validate(RecipeDefinition recipe, RelationshipGraph graph)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(graph);
        try
        {
            RecipeTemplate.ValidateDefinition(recipe);
            return new Binder(recipe, graph).Run(RecipeTemplate.BindForValidation(recipe));
        }
        catch (RecipeEngineException exception)
        {
            return new("Broken", [], [new("error", exception.Code, exception.Message)]);
        }
    }

    private sealed record Origin(
        string DatabaseId,
        string DatabaseName,
        string TableId,
        string Schema,
        string Table,
        string Column,
        string Type
    );

    private sealed record Value(
        string? Name,
        string Type,
        IReadOnlyList<Origin> Origins,
        string? Input = null,
        bool Direct = false,
        string? Instance = null,
        bool Subquery = false,
        bool ExactProjection = false
    );

    private sealed record Source(
        string Instance,
        string Alias,
        bool ExplicitAlias,
        IReadOnlyList<Value> Columns,
        RelationshipTable? Catalog = null,
        bool Unknown = false
    );

    private sealed class Scope(Scope? parent)
    {
        public Scope? Parent { get; } = parent;
        public List<Source> Sources { get; } = [];
        public List<Value> Outputs { get; } = [];
        public HashSet<(string Left, string Right)> Connections { get; } = [];
    }

    private sealed partial class Binder(
        RecipeDefinition recipe,
        RelationshipGraph graph,
        Action<ColumnReferenceExpression, Value, string?, bool>? columnObserver = null,
        IReadOnlyDictionary<string, RecipeInputDefinition>? inputAliases = null
    )
    {
        private readonly List<SchemaDiagnostic> diagnostics = [];
        private readonly HashSet<RecipeDependency> dependencies = [];
        private readonly IReadOnlyList<RelationshipTable> catalog = graph
            .Tables.Concat(
                (graph.ReadableObjects ?? [])
                    .Where(o => o.Object.Kind == "view")
                    .Select(o => new RelationshipTable(
                        o.DatabaseId,
                        o.DatabaseName,
                        new SchemaTable(o.Object.Id, o.Object.Schema, o.Object.Name, o.Object.Columns, [])
                    ))
            )
            .Where(t => recipe.DatabaseIds.Contains(t.DatabaseId, StringComparer.Ordinal))
            .ToArray();
        private readonly Dictionary<string, RecipeInputDefinition> inputs = inputAliases is null
            ? recipe.Inputs.ToDictionary(input => RecipeTemplate.InputTableName(input.Name), StringComparer.OrdinalIgnoreCase)
            : new(inputAliases, StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> declaredVariables = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ScalarSubquery, IReadOnlyList<Value>> subqueries = [];
        private bool broken;
        private bool review;
        private int instance;
        private int depth;
        private IReadOnlyDictionary<string, Source> currentCtes = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase);

        public RecipeValidationResult Run(string sql)
        {
            using var text = new StringReader(sql);
            var parsed = new TSql170Parser(true).Parse(text, out var errors);
            if (errors.Count != 0 || parsed is not TSqlScript script)
            {
                foreach (var error in errors.Take(25))
                    Error(
                        "RECIPE_SQL_PARSE_ERROR",
                        $"SQL parse error {error.Number} at line {error.Line}, column {error.Column}; literal values were omitted."
                    );
                if (errors.Count == 0)
                    Error("RECIPE_SQL_EMPTY", "The template does not contain a SQL script.");
                return Result();
            }
            var variables = new VariableDeclarations();
            parsed.Accept(variables);
            foreach (var name in variables.Names)
                declaredVariables.Add(name);
            var statements = script.Batches.SelectMany(batch => batch.Statements).ToArray();
            if (statements.Length == 0)
                Error("RECIPE_SQL_EMPTY", "The template contains no SQL statements.");
            foreach (var statement in statements)
            {
                if (statement is SelectStatement select)
                {
                    if (select.Into is not null)
                        Review("RECIPE_SELECT_INTO", "SELECT INTO creates another rowset and requires review.");
                    var ctes = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase);
                    if (select.WithCtesAndXmlNamespaces is not null)
                        foreach (var cte in select.WithCtesAndXmlNamespaces.CommonTableExpressions)
                        {
                            var name = cte.ExpressionName.Value;
                            ctes[name] = new(NewInstance(), name, false, [], Unknown: true);
                            currentCtes = ctes;
                            var columns = Query(cte.QueryExpression, null);
                            if (cte.Columns.Count > 0)
                                columns = Rename(columns, cte.Columns.Select(column => column.Value).ToArray(), cte);
                            ctes[name] = new(NewInstance(), name, false, columns);
                        }
                    currentCtes = ctes;
                    Query(select.QueryExpression, null);
                }
                else
                {
                    Review(
                        "RECIPE_CONSTRUCT_UNSUPPORTED",
                        $"{statement.GetType().Name} requires manual review; this validator does not execute procedural or mutation statements."
                    );
                    var references = new TableReferences();
                    statement.Accept(references);
                    foreach (var reference in references.Tables)
                    {
                        var source = Named(reference);
                        if (source is not null)
                            foreach (var column in source.Columns)
                                Add(column);
                    }
                }
            }
            return Result();
        }

        private RecipeValidationResult Result() =>
            new(
                broken ? "Broken"
                    : review ? "NeedsReview"
                    : diagnostics.Count != 0 ? "Warning"
                    : "Valid",
                dependencies
                    .OrderBy(d => d.DatabaseId, StringComparer.Ordinal)
                    .ThenBy(d => d.TableId, StringComparer.Ordinal)
                    .ThenBy(d => d.ColumnName, StringComparer.Ordinal)
                    .ThenBy(d => d.RelationId, StringComparer.Ordinal)
                    .ToArray(),
                diagnostics
            );

        private void Error(string code, string message)
        {
            broken = true;
            Diagnostic("error", code, message);
        }

        private void Review(string code, string message)
        {
            review = true;
            Diagnostic("warning", code, message);
        }

        private void Warning(string code, string message) => Diagnostic("warning", code, message);

        private void Diagnostic(string severity, string code, string message)
        {
            if (diagnostics.Count < 1000 && !diagnostics.Any(d => d.Code == code && d.Message == message))
                diagnostics.Add(new(severity, code, message));
        }

        private string NewInstance() => "source:" + ++instance;

        private void Add(Value value)
        {
            foreach (var origin in value.Origins)
                dependencies.Add(new(origin.DatabaseId, origin.TableId, origin.Column));
        }

        private static string Name(SchemaObjectName name) => string.Join(".", name.Identifiers.Select(identifier => identifier.Value));

        private Source? Named(NamedTableReference reference)
        {
            var name = reference.SchemaObject;
            var alias = reference.Alias?.Value ?? name.BaseIdentifier?.Value ?? "unknown";
            if (name.BaseIdentifier is null)
            {
                Review("RECIPE_TABLE_UNRESOLVED", "Table name is incomplete.");
                return new(NewInstance(), alias, true, [], Unknown: true);
            }
            if (name.Identifiers.Count == 1 && inputs.TryGetValue(name.BaseIdentifier.Value, out var input))
                return new(
                    NewInstance(),
                    alias,
                    reference.Alias is not null,
                    [new("Value", InputRules.SqlType(input), [], input.Name, true), new("Ordinal", "int", [], Direct: true)]
                );
            if (name.Identifiers.Count == 1 && currentCtes.TryGetValue(name.BaseIdentifier.Value, out var cte))
            {
                if (cte.Unknown)
                    Review("RECIPE_CTE_RECURSIVE", "Recursive/forward CTE shape cannot be proven statically.");
                return cte with { Instance = NewInstance(), Alias = alias, ExplicitAlias = reference.Alias is not null };
            }
            if (name.ServerIdentifier is not null || name.Identifiers.Count is < 1 or > 3)
            {
                Review("RECIPE_SERVER_REFERENCE_UNSUPPORTED", "External server/four-part table references require review.");
                return new(NewInstance(), alias, true, [], Unknown: true);
            }
            if (name.BaseIdentifier.Value.StartsWith('#') || name.BaseIdentifier.Value.StartsWith('@'))
            {
                Review("RECIPE_TEMP_TABLE_UNRESOLVED", $"Temporary rowset {name.BaseIdentifier.Value} is not a defined recipe input.");
                return new(NewInstance(), alias, true, [], Unknown: true);
            }
            var matches = catalog
                .Where(table =>
                    string.Equals(table.Table.Name, name.BaseIdentifier.Value, StringComparison.OrdinalIgnoreCase)
                    && (
                        name.SchemaIdentifier is null
                        || string.Equals(table.Table.Schema, name.SchemaIdentifier.Value, StringComparison.OrdinalIgnoreCase)
                    )
                    && (
                        name.DatabaseIdentifier is null
                        || string.Equals(table.DatabaseName, name.DatabaseIdentifier.Value, StringComparison.OrdinalIgnoreCase)
                    )
                )
                .ToArray();
            if (matches.Length == 0)
            {
                Error("RECIPE_TABLE_MISSING", $"{Name(name)} is missing from the explicitly selected database schema.");
                return new(NewInstance(), alias, true, [], Unknown: true);
            }
            if (matches.Length != 1)
            {
                Review(
                    "RECIPE_TABLE_AMBIGUOUS",
                    $"{Name(name)} matches multiple selected database/schema objects; choose an explicit database and schema."
                );
                return new(NewInstance(), alias, true, [], Unknown: true);
            }
            var table = matches[0];
            if ((graph.ReadableObjects ?? []).Any(o => o.Object.Id == table.Table.Id && o.Object.IsPartial))
                Review("RECIPE_OBJECT_PARTIAL", "View shape is only partially proven; verify its definition before using this recipe.");
            dependencies.Add(new(table.DatabaseId, table.Table.Id));
            return new(
                NewInstance(),
                alias,
                reference.Alias is not null,
                table
                    .Table.Columns.Select(column => new Value(
                        column.Name,
                        column.DataType,
                        [
                            new(
                                table.DatabaseId,
                                table.DatabaseName,
                                table.Table.Id,
                                table.Table.Schema,
                                table.Table.Name,
                                column.Name,
                                column.DataType
                            ),
                        ],
                        Direct: true
                    ))
                    .ToArray(),
                table
            );
        }

        private sealed class TableReferences : TSqlFragmentVisitor
        {
            public List<NamedTableReference> Tables { get; } = [];

            public override void ExplicitVisit(NamedTableReference node) => Tables.Add(node);
        }

        private sealed class VariableDeclarations : TSqlFragmentVisitor
        {
            public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

            public override void ExplicitVisit(DeclareVariableElement node) => Names.Add(node.VariableName.Value);
        }
    }
}
