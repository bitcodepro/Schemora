using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Schemora.Relationships;
using Schemora.SqlServer;

namespace Schemora.RecipeEngine;

public sealed record RecipeColumnRewriteResult(string SqlTemplate, int ReplacementCount, IReadOnlyList<SchemaDiagnostic> Diagnostics);

/// <summary>Preview-only, scope-bound identifier repair. Catalog columns are patched; projected aliases retain their names.</summary>
public sealed partial class RecipeColumnRewriter
{
    public RecipeColumnRewriteResult Preview(
        RecipeDefinition recipe,
        RelationshipGraph graph,
        string databaseId,
        string tableId,
        string oldColumn,
        string newColumn
    )
    {
        RecipeColumnRewriteResult Block(string code, string message) => new(recipe.SqlTemplate, 0, [new("error", code, message)]);
        if (!recipe.DatabaseIds.Contains(databaseId, StringComparer.Ordinal))
            return Block("REWRITE_DATABASE_SCOPE", "The selected database is outside the recipe scope.");
        var targets = graph.Tables.Where(t => t.DatabaseId == databaseId && t.Table.Id == tableId).ToArray();
        if (targets.Length != 1)
            return Block("REWRITE_TABLE_UNBOUND", "The selected database/table is missing or ambiguous.");
        var target = targets[0];
        var replacements = target.Table.Columns.Where(c => c.Name.Equals(newColumn, StringComparison.Ordinal)).ToArray();
        if (string.IsNullOrWhiteSpace(oldColumn) || replacements.Length != 1)
            return Block("REWRITE_COLUMN_UNBOUND", "The replacement column must exist exactly once in the selected table.");
        if (oldColumn == newColumn)
            return new(recipe.SqlTemplate, 0, [new("warning", "REWRITE_NO_CHANGE", "The old and replacement names are identical.")]);
        IReadOnlyList<RecipeToken> tokens;
        try
        {
            tokens = RecipeTemplate.ValidateDefinition(recipe);
        }
        catch (RecipeEngineException error)
        {
            return Block(error.Code, error.Message);
        }
        var inputAliases = new Dictionary<string, RecipeInputDefinition>(StringComparer.OrdinalIgnoreCase);
        var parsing = new StringBuilder(recipe.SqlTemplate);
        foreach (var token in tokens.Reverse())
        {
            var name = "#i" + token.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var text = token.Kind switch
            {
                "ids" => "SELECT 1",
                "value" => "1",
                _ => "[" + name + "]",
            };
            if (text.Length > token.Length)
                return Block("REWRITE_PLACEHOLDER_OFFSETS", "The placeholder cannot be represented without changing source offsets.");
            if (token.Kind == "table")
                inputAliases[name] = recipe.Inputs.Single(input => input.Name == token.InputName);
            parsing.Remove(token.Start, token.Length).Insert(token.Start, text.PadRight(token.Length));
        }
        var parsed = new TSql170Parser(true).Parse(new StringReader(parsing.ToString()), out var parseErrors) as TSqlScript;
        if (parseErrors.Count != 0 || parsed is null)
            return Block("REWRITE_SQL_PARSE_FAILED", "The template has SQL syntax errors; no preview was generated.");
        if (
            parsed.Batches.Count != 1
            || parsed.Batches[0].Statements.Count != 1
            || parsed.Batches[0].Statements[0] is not SelectStatement statement
            || statement.Into is not null
        )
            return Block("REWRITE_QUERY_UNSUPPORTED", "Repair requires one SELECT statement without SELECT INTO or mutation statements.");
        var references = new References();
        statement.Accept(references);
        if (references.Unsupported)
            return Block("REWRITE_QUERY_UNSUPPORTED", "The query contains a functional, procedural or otherwise unsupported rowset.");
        var ctes = statement.WithCtesAndXmlNamespaces?.CommonTableExpressions.ToArray() ?? [];
        if (ctes.GroupBy(c => c.ExpressionName.Value, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            return Block("REWRITE_ALIAS_AMBIGUOUS", "Duplicate CTE names prevent exact binding.");
        var cteNames = ctes.Select(c => c.ExpressionName.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool InsideToken(TSqlFragment fragment) =>
            tokens.Any(t => fragment.StartOffset >= t.Start && fragment.StartOffset < t.Start + t.Length);
        foreach (var table in references.Tables.Where(t => !InsideToken(t)))
        {
            var name = table.SchemaObject;
            if (
                name.Identifiers.Count == 1
                && (cteNames.Contains(name.BaseIdentifier.Value) || inputAliases.ContainsKey(name.BaseIdentifier.Value))
            )
                continue;
            if (name.Identifiers.Count is < 2 or > 3 || name.Identifiers.Any(i => i.Value.Length == 0))
                return Block(
                    "REWRITE_TABLE_QUALIFICATION_REQUIRED",
                    "Catalog tables require explicit schema.table or database.schema.table names."
                );
        }
        // A removed old column exists only in this temporary binding graph. It is never
        // persisted or exposed as current metadata; the caller explicitly selected its table.
        var virtualTarget = target.Table.Columns.Any(c => c.Name.Equals(oldColumn, StringComparison.OrdinalIgnoreCase))
            ? target.Table
            : target.Table with
            {
                Columns = target.Table.Columns.Append(replacements[0] with { Name = oldColumn }).ToArray(),
            };
        var bindingGraph = graph with
        {
            Tables = graph
                .Tables.Select(t => t.DatabaseId == databaseId && t.Table.Id == tableId ? t with { Table = virtualTarget } : t)
                .ToArray(),
        };
        var binder = new RecipeValidator();
        var bound = binder.BindForRewrite(recipe, bindingGraph, parsing.ToString(), inputAliases);
        var failure = bound.Diagnostics.FirstOrDefault(BindingFailure);
        if (failure is not null)
        {
            var code =
                failure.Code == "RECIPE_TABLE_AMBIGUOUS" ? "REWRITE_TABLE_AMBIGUOUS"
                : failure.Code == "RECIPE_ALIAS_DUPLICATE" ? "REWRITE_ALIAS_AMBIGUOUS"
                : failure.Code == "RECIPE_COLUMN_AMBIGUOUS" ? "REWRITE_COLUMN_AMBIGUOUS"
                : "REWRITE_BINDING_UNRESOLVED";
            return Block(code, "The original SQL could not be bound unambiguously: " + failure.Message);
        }
        var columns = bound.Columns.GroupBy(c => c.Reference.StartOffset).ToDictionary(g => g.Key, g => g.First());
        var binaryOrders = references.BinaryQueries.Where(q => q.OrderByClause is not null).Select(q => q.OrderByClause!).ToArray();
        bool Selected(RecipeRewriteColumn column) =>
            column.Origins.Count == 1
            && column.Origins[0].DatabaseId == databaseId
            && column.Origins[0].TableId == tableId
            && column.Origins[0].Column.Equals(oldColumn, StringComparison.OrdinalIgnoreCase);
        bool Old(ColumnReferenceExpression column) =>
            column.ColumnType == ColumnType.Regular
            && column.MultiPartIdentifier?.Identifiers is { Count: > 0 } parts
            && parts[^1].Value.Equals(oldColumn, StringComparison.OrdinalIgnoreCase);
        foreach (var reference in references.Columns.Where(c => !InsideToken(c) && Old(c)))
            if (!columns.ContainsKey(reference.StartOffset))
                return Block("REWRITE_BINDING_UNRESOLVED", "An old-column reference occurs in an unsupported or unresolved SQL context.");
        var patches = new List<(int Start, int Length, string Text)>();
        var physical = new HashSet<int>();
        foreach (var column in bound.Columns.Where(c => !InsideToken(c.Reference) && Old(c.Reference)))
        {
            if (column.OutputAlias)
            {
                var inSetOrder = binaryOrders.Any(order =>
                    column.Reference.StartOffset >= order.StartOffset
                    && column.Reference.StartOffset < order.StartOffset + order.FragmentLength
                );
                if (!inSetOrder && (!column.Direct || !Selected(column)))
                    return Block(
                        "REWRITE_COLUMN_AMBIGUOUS",
                        "An old name denotes a computed/unrelated SELECT alias; manual review is required."
                    );
                continue;
            }
            if (column.PhysicalAlias is null || !Selected(column))
                continue; // CTE/derived names are stable projected aliases.
            var parts = column.Reference.MultiPartIdentifier.Identifiers;
            var identifier = parts[^1];
            var replacement =
                identifier.QuoteType == QuoteType.DoubleQuote
                    ? "\"" + newColumn.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                    : Quote(newColumn);
            // Qualify previously unqualified source references so an identically named new
            // column on another joined rowset cannot silently capture the repaired expression.
            if (parts.Count == 1)
                replacement = Quote(column.PhysicalAlias) + "." + replacement;
            patches.Add((identifier.StartOffset, identifier.FragmentLength, replacement));
            physical.Add(column.Reference.StartOffset);
        }
        if (physical.Count == 0)
            return new(
                recipe.SqlTemplate,
                0,
                [new("warning", "REWRITE_NO_REFERENCES", "No bound physical reference to the selected old column was found.")]
            );
        var protectedQueries = new HashSet<QuerySpecification>();
        void Protect(QueryExpression query)
        {
            switch (query)
            {
                case QuerySpecification specification:
                    protectedQueries.Add(specification);
                    break;
                case QueryParenthesisExpression parentheses:
                    Protect(parentheses.QueryExpression);
                    break;
                case BinaryQueryExpression binary:
                    Protect(binary.FirstQueryExpression);
                    Protect(binary.SecondQueryExpression);
                    break;
            }
        }
        foreach (var cte in ctes)
            Protect(cte.QueryExpression);
        foreach (var derived in references.Derived)
            Protect(derived.QueryExpression);
        foreach (var binary in references.BinaryQueries)
            Protect(binary.FirstQueryExpression);
        foreach (var query in protectedQueries)
        foreach (var scalar in query.SelectElements.OfType<SelectScalarExpression>().Where(s => s.ColumnName is null))
        {
            ScalarExpression expression = scalar.Expression;
            while (expression is ParenthesisExpression parentheses)
                expression = parentheses.Expression;
            if (expression is ColumnReferenceExpression reference && physical.Contains(reference.StartOffset))
                patches.Add(
                    (
                        scalar.Expression.StartOffset + scalar.Expression.FragmentLength,
                        0,
                        " AS " + Quote(reference.MultiPartIdentifier.Identifiers[^1].Value)
                    )
                );
        }
        var rewritten = new StringBuilder(recipe.SqlTemplate);
        foreach (var patch in patches.Distinct().OrderByDescending(p => p.Start))
            rewritten.Remove(patch.Start, patch.Length).Insert(patch.Start, patch.Text);
        var result = rewritten.ToString();
        RecipeValidationResult validation;
        try
        {
            validation = binder.Validate(recipe with { SqlTemplate = result }, graph);
        }
        catch (RecipeEngineException error)
        {
            return Block(error.Code, error.Message);
        }
        if (validation.Diagnostics.Any(BindingFailure))
            return Block(
                "REWRITE_RESULT_BINDING_FAILED",
                "The preview does not preserve unambiguous catalog/rowset binding; original SQL was retained."
            );
        return new(
            result,
            physical.Count,
            [
                new(
                    "info",
                    "REWRITE_PREVIEW_ONLY",
                    "Bound source identifiers were patched in this preview; CTE/derived/set output names and explicit aliases were preserved. Review and explicitly approve before saving."
                ),
            ]
        );
    }

    private static string Quote(string value) => "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static bool BindingFailure(SchemaDiagnostic diagnostic) =>
        diagnostic.Severity == "error"
        || diagnostic.Code
            is "RECIPE_COLUMN_AMBIGUOUS"
                or "RECIPE_ALIAS_AMBIGUOUS"
                or "RECIPE_TABLE_AMBIGUOUS"
                or "RECIPE_CTE_RECURSIVE"
                or "RECIPE_COLUMN_UNRESOLVED"
                or "RECIPE_ROWSET_UNSUPPORTED"
                or "RECIPE_APPLY_UNSUPPORTED"
                or "RECIPE_QUERY_UNSUPPORTED"
                or "RECIPE_DERIVED_SHAPE"
                or "RECIPE_QUERY_DEPTH"
                or "RECIPE_SERVER_REFERENCE_UNSUPPORTED"
                or "RECIPE_OBJECT_PARTIAL";

    private sealed class References : TSqlFragmentVisitor
    {
        public List<NamedTableReference> Tables { get; } = [];
        public List<ColumnReferenceExpression> Columns { get; } = [];
        public List<QueryDerivedTable> Derived { get; } = [];
        public List<BinaryQueryExpression> BinaryQueries { get; } = [];
        public bool Unsupported { get; private set; }

        public override void ExplicitVisit(NamedTableReference node)
        {
            Tables.Add(node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            Columns.Add(node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(QueryDerivedTable node)
        {
            Derived.Add(node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(BinaryQueryExpression node)
        {
            BinaryQueries.Add(node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(InlineDerivedTable node) => Unsupported = true;

        public override void ExplicitVisit(SchemaObjectFunctionTableReference node) => Unsupported = true;

        public override void ExplicitVisit(VariableTableReference node) => Unsupported = true;

        public override void ExplicitVisit(PivotedTableReference node) => Unsupported = true;

        public override void ExplicitVisit(UnpivotedTableReference node) => Unsupported = true;

        public override void ExplicitVisit(OpenJsonTableReference node) => Unsupported = true;

        public override void ExplicitVisit(OpenXmlTableReference node) => Unsupported = true;

        public override void ExplicitVisit(BuiltInFunctionTableReference node) => Unsupported = true;

        public override void ExplicitVisit(DataModificationTableReference node) => Unsupported = true;

        public override void ExplicitVisit(FullTextTableReference node) => Unsupported = true;

        public override void ExplicitVisit(OpenRowsetTableReference node) => Unsupported = true;
    }
}
