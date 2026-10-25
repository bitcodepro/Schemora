using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Schemora.Relationships;

namespace Schemora.RecipeEngine;

public sealed partial class RecipeColumnRewriter
{
    // Names are supplied explicitly by the user; no rename heuristic is applied.
    public RecipeColumnRewriteResult PreviewObjectRename(
        RecipeDefinition recipe,
        RelationshipGraph graph,
        string databaseId,
        string tableId,
        string oldSchema,
        string oldTable
    )
    {
        RecipeColumnRewriteResult Block(string code, string message) => new(recipe.SqlTemplate, 0, [new("error", code, message)]);
        var target = graph.Tables.Where(t => t.DatabaseId == databaseId && t.Table.Id == tableId).ToArray();
        if (
            !recipe.DatabaseIds.Contains(databaseId)
            || target.Length != 1
            || string.IsNullOrWhiteSpace(oldSchema)
            || string.IsNullOrWhiteSpace(oldTable)
        )
            return Block("REWRITE_TABLE_UNBOUND", "Select one current target table and explicit previous schema and table names.");
        var current = target[0];
        if (
            graph.Tables.Any(t =>
                t.DatabaseId == databaseId
                && t.Table.Id != tableId
                && t.Table.Schema.Equals(oldSchema, StringComparison.OrdinalIgnoreCase)
                && t.Table.Name.Equals(oldTable, StringComparison.OrdinalIgnoreCase)
            )
        )
            return Block("REWRITE_TABLE_AMBIGUOUS", "The previous name still identifies another current table.");
        IReadOnlyList<RecipeToken> tokens;
        try
        {
            tokens = RecipeTemplate.ValidateDefinition(recipe);
        }
        catch (RecipeEngineException ex)
        {
            return Block(ex.Code, ex.Message);
        }
        var parsing = new StringBuilder(recipe.SqlTemplate);
        var inputs = new Dictionary<string, RecipeInputDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens.Reverse())
        {
            var name = "#i" + token.Start;
            var text = token.Kind switch
            {
                "ids" => "SELECT 1",
                "value" => "1",
                _ => "[" + name + "]",
            };
            if (text.Length > token.Length)
                return Block("REWRITE_PLACEHOLDER_OFFSETS", "Cannot preserve placeholder offsets.");
            if (token.Kind == "table")
                inputs[name] = recipe.Inputs.Single(i => i.Name == token.InputName);
            parsing.Remove(token.Start, token.Length).Insert(token.Start, text.PadRight(token.Length));
        }
        var parsed = new TSql170Parser(true).Parse(new StringReader(parsing.ToString()), out var errors) as TSqlScript;
        if (
            errors.Count > 0
            || parsed?.Batches.Count != 1
            || parsed.Batches[0].Statements.Count != 1
            || parsed.Batches[0].Statements[0] is not SelectStatement statement
            || statement.Into is not null
            || statement.WithCtesAndXmlNamespaces is not null
        )
            return Block(
                "REWRITE_QUERY_UNSUPPORTED",
                "Object rename repair requires a SELECT without CTEs or SELECT INTO; use explicit source aliases."
            );
        var refs = new References();
        statement.Accept(refs);
        if (refs.Unsupported)
            return Block("REWRITE_QUERY_UNSUPPORTED", "Unsupported rowset; repair manually.");
        bool InToken(TSqlFragment f) => tokens.Any(t => f.StartOffset >= t.Start && f.StartOffset < t.Start + t.Length);
        var sources = refs
            .Tables.Where(t =>
                !InToken(t)
                && t.SchemaObject.SchemaIdentifier?.Value.Equals(oldSchema, StringComparison.OrdinalIgnoreCase) == true
                && t.SchemaObject.BaseIdentifier.Value.Equals(oldTable, StringComparison.OrdinalIgnoreCase)
            )
            .ToArray();
        if (sources.Length == 0)
            return Block("REWRITE_NO_CHANGE", "No explicitly qualified previous source name was found.");
        if (sources.Any(t => t.Alias is null))
            return Block(
                "REWRITE_SOURCE_ALIAS_REQUIRED",
                "Give each renamed table reference an explicit alias before using safe object rename repair."
            );
        foreach (var source in sources)
        {
            var name = source.SchemaObject;
            if (
                name.Identifiers.Count is < 2 or > 3
                || (
                    name.DatabaseIdentifier is not null
                    && !name.DatabaseIdentifier.Value.Equals(current.DatabaseName, StringComparison.OrdinalIgnoreCase)
                )
            )
                return Block("REWRITE_TABLE_AMBIGUOUS", "A source resolves outside the selected database.");
            if (
                name.DatabaseIdentifier is null
                && graph.Tables.Count(t =>
                    recipe.DatabaseIds.Contains(t.DatabaseId)
                    && t.Table.Schema.Equals(oldSchema, StringComparison.OrdinalIgnoreCase)
                    && t.Table.Name.Equals(oldTable, StringComparison.OrdinalIgnoreCase)
                ) > 0
            )
                return Block(
                    "REWRITE_TABLE_AMBIGUOUS",
                    "The previous two-part name also identifies a current source; qualify the database explicitly."
                );
        }
        var virtualTable = current with { Table = current.Table with { Schema = oldSchema, Name = oldTable } };
        var virtualGraph = graph with
        {
            Tables = graph.Tables.Select(t => t.DatabaseId == databaseId && t.Table.Id == tableId ? virtualTable : t).ToArray(),
        };
        var bound = new RecipeValidator().BindForRewrite(recipe, virtualGraph, parsing.ToString(), inputs);
        if (bound.Diagnostics.Any(BindingFailure))
            return Block("REWRITE_BINDING_UNRESOLVED", "Previous SQL cannot be bound unambiguously; repair manually.");
        var patches = sources
            .SelectMany(s =>
                new[]
                {
                    (Node: s.SchemaObject.SchemaIdentifier, Text: Quote(current.Table.Schema)),
                    (Node: s.SchemaObject.BaseIdentifier, Text: Quote(current.Table.Name)),
                }
            )
            .ToArray();
        var sql = new StringBuilder(recipe.SqlTemplate);
        foreach (var patch in patches.OrderByDescending(p => p.Node.StartOffset))
            sql.Remove(patch.Node.StartOffset, patch.Node.FragmentLength).Insert(patch.Node.StartOffset, patch.Text);
        var result = sql.ToString();
        if (new RecipeValidator().Validate(recipe with { SqlTemplate = result }, graph).Diagnostics.Any(BindingFailure))
            return Block("REWRITE_RESULT_BINDING_FAILED", "Rewritten SQL cannot be bound safely.");
        return new(
            result,
            sources.Length,
            [
                new(
                    "info",
                    "REWRITE_PREVIEW_ONLY",
                    "Explicit table/schema rename preview; aliases, literals and comments are preserved. Review and approve separately."
                ),
            ]
        );
    }
}
