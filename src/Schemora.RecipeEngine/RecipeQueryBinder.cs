using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Schemora.RecipeEngine;

public sealed partial class RecipeValidator
{
    private sealed partial class Binder
    {
        private IReadOnlyList<Value> Query(QueryExpression expression, Scope? parent)
        {
            if (++depth > 64)
            {
                Review("RECIPE_QUERY_DEPTH", "Query nesting exceeds the validation limit of 64.");
                depth--;
                return [];
            }
            try
            {
                switch (expression)
                {
                    case QueryParenthesisExpression parentheses:
                        return Query(parentheses.QueryExpression, parent);
                    case BinaryQueryExpression binary:
                        var first = Query(binary.FirstQueryExpression, parent);
                        var second = Query(binary.SecondQueryExpression, parent);
                        if (first.Count != second.Count)
                        {
                            Error("RECIPE_UNION_SHAPE", "Set-operation query branches have different column counts.");
                            return [];
                        }
                        var merged = first
                            .Select(
                                (value, index) =>
                                    value with
                                    {
                                        Origins = value.Origins.Concat(second[index].Origins).Distinct().ToArray(),
                                        Type = string.Equals(value.Type, second[index].Type, StringComparison.OrdinalIgnoreCase)
                                            ? value.Type
                                            : "unknown",
                                        Direct = false,
                                        Input = value.Input == second[index].Input ? value.Input : null,
                                    }
                            )
                            .ToArray();
                        if (binary.OrderByClause is not null)
                        {
                            var resultScope = new Scope(parent);
                            resultScope.Outputs.AddRange(merged);
                            Walk(binary.OrderByClause, resultScope, true);
                        }
                        if (binary.OffsetClause is not null)
                            Walk(binary.OffsetClause, new Scope(parent));
                        return merged;
                    case QuerySpecification query:
                        return Specification(query, parent);
                    default:
                        Review("RECIPE_QUERY_UNSUPPORTED", $"{expression.GetType().Name} needs manual query-lineage review.");
                        return [];
                }
            }
            finally
            {
                depth--;
            }
        }

        private IReadOnlyList<Value> Specification(QuerySpecification query, Scope? parent)
        {
            var scope = new Scope(parent);
            var predicates = new List<(BooleanExpression Expression, bool Join)>();
            if (query.FromClause is not null)
                foreach (var reference in query.FromClause.TableReferences)
                    Collect(reference);
            if (
                columnObserver is not null
                && scope.Sources.GroupBy(s => s.Alias, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)
            )
                Error("RECIPE_ALIAS_DUPLICATE", "Duplicate SQL aliases prevent exact repair binding.");
            foreach (var element in query.SelectElements)
            {
                switch (element)
                {
                    case SelectScalarExpression scalar:
                        var value = Expression(scalar.Expression, scope);
                        var alias = scalar.ColumnName?.Value ?? value.Name;
                        scope.Outputs.Add(value with { Name = alias });
                        break;
                    case SelectStarExpression star:
                        var selected = StarSources(star, scope);
                        foreach (var source in selected)
                        {
                            if (source.Unknown)
                                Review("RECIPE_WILDCARD_SHAPE", "Wildcard output includes an unresolved rowset.");
                            foreach (var column in source.Columns)
                            {
                                Add(column);
                                scope.Outputs.Add(column with { Instance = source.Instance });
                            }
                        }
                        break;
                    default:
                        Review("RECIPE_SELECT_UNSUPPORTED", $"{element.GetType().Name} requires review.");
                        Walk(element, scope);
                        break;
                }
            }
            if (query.WhereClause is not null)
                predicates.Add((query.WhereClause.SearchCondition, false));
            foreach (var predicate in predicates)
            {
                Walk(predicate.Expression, scope);
                ValidateRelationships(predicate.Expression, scope);
            }
            if (scope.Sources.Count > 1)
            {
                var ids = scope.Sources.Select(source => source.Instance).ToHashSet(StringComparer.Ordinal);
                var reached = new HashSet<string>(StringComparer.Ordinal) { scope.Sources[0].Instance };
                var changed = true;
                while (changed)
                {
                    changed = false;
                    foreach (var pair in scope.Connections)
                    {
                        if (reached.Contains(pair.Left) && ids.Contains(pair.Right))
                            changed |= reached.Add(pair.Right);
                        if (reached.Contains(pair.Right) && ids.Contains(pair.Left))
                            changed |= reached.Add(pair.Left);
                    }
                }
                if (!ids.IsSubsetOf(reached))
                    Review(
                        "RECIPE_JOIN_UNRESOLVED",
                        "FROM rowsets are not connected by explicit, unambiguous conjunctive equality joins; review the Cartesian/complex join semantics."
                    );
            }
            if (query.GroupByClause is not null)
                Walk(query.GroupByClause, scope);
            if (query.HavingClause is not null)
                Walk(query.HavingClause.SearchCondition, scope);
            if (query.OrderByClause is not null)
                Walk(query.OrderByClause, scope, true);
            if (query.OffsetClause is not null)
                Walk(query.OffsetClause, scope);
            if (query.TopRowFilter is not null)
                Walk(query.TopRowFilter.Expression, scope);

            return scope.Outputs;

            void Collect(TableReference reference)
            {
                switch (reference)
                {
                    case NamedTableReference named:
                        if (Named(named) is { } source)
                            scope.Sources.Add(source);
                        break;
                    case QualifiedJoin join:
                        Collect(join.FirstTableReference);
                        Collect(join.SecondTableReference);
                        predicates.Add((join.SearchCondition, true));
                        break;
                    case UnqualifiedJoin join:
                        Collect(join.FirstTableReference);
                        Collect(join.SecondTableReference);
                        if (join.UnqualifiedJoinType != UnqualifiedJoinType.CrossJoin)
                            Review("RECIPE_APPLY_UNSUPPORTED", "APPLY rowsets require review of correlated/functional lineage.");
                        break;
                    case JoinParenthesisTableReference parentheses:
                        Collect(parentheses.Join);
                        break;
                    case QueryDerivedTable derived:
                        var columns = Query(derived.QueryExpression, null);
                        if (derived.Columns.Count > 0)
                            columns = Rename(columns, derived.Columns.Select(column => column.Value).ToArray(), derived);
                        if (derived.Alias is null)
                        {
                            Error("RECIPE_DERIVED_ALIAS_MISSING", "A derived table needs an alias.");
                            break;
                        }
                        scope.Sources.Add(new(NewInstance(), derived.Alias.Value, true, columns, Unknown: columns.Count == 0));
                        break;
                    default:
                        Review("RECIPE_ROWSET_UNSUPPORTED", $"{reference.GetType().Name} rowset metadata is unavailable.");
                        scope.Sources.Add(
                            new(NewInstance(), (reference as TableReferenceWithAlias)?.Alias?.Value ?? "unknown", true, [], Unknown: true)
                        );
                        break;
                }
            }
        }

        private IReadOnlyList<Value> Rename(IReadOnlyList<Value> columns, IReadOnlyList<string> names, TSqlFragment node)
        {
            if (columns.Count == 0)
            {
                Review("RECIPE_DERIVED_SHAPE", "Derived/CTE column shape could not be established.");
                return [];
            }
            if (columns.Count != names.Count)
            {
                Error("RECIPE_DERIVED_COLUMN_COUNT", $"Derived/CTE column count mismatch at line {node.StartLine}.");
                return [];
            }
            return columns.Select((column, index) => column with { Name = names[index] }).ToArray();
        }

        private IReadOnlyList<Source> StarSources(SelectStarExpression star, Scope scope)
        {
            var parts = star.Qualifier?.Identifiers.Select(identifier => identifier.Value).ToArray() ?? [];
            if (parts.Length == 0)
                return scope.Sources;
            var matches = scope.Sources.Where(source => SourceMatches(source, parts)).ToArray();
            if (matches.Length == 0)
                Error("RECIPE_ALIAS_MISSING", "Wildcard qualifier does not name an available rowset.");
            else if (matches.Length > 1)
                Review("RECIPE_ALIAS_AMBIGUOUS", "Wildcard qualifier matches multiple rowsets.");
            return matches.Length == 1 ? matches : [];
        }

        private static bool SourceMatches(Source source, IReadOnlyList<string> qualifiers)
        {
            if (qualifiers.Count == 1)
                return string.Equals(source.Alias, qualifiers[0], StringComparison.OrdinalIgnoreCase);
            if (source.ExplicitAlias || source.Catalog is null)
                return false;
            return qualifiers.Count == 2
                    && string.Equals(source.Catalog.Table.Schema, qualifiers[0], StringComparison.OrdinalIgnoreCase)
                    && string.Equals(source.Catalog.Table.Name, qualifiers[1], StringComparison.OrdinalIgnoreCase)
                || qualifiers.Count == 3
                    && string.Equals(source.Catalog.DatabaseName, qualifiers[0], StringComparison.OrdinalIgnoreCase)
                    && string.Equals(source.Catalog.Table.Schema, qualifiers[1], StringComparison.OrdinalIgnoreCase)
                    && string.Equals(source.Catalog.Table.Name, qualifiers[2], StringComparison.OrdinalIgnoreCase);
        }

        private Value Column(ColumnReferenceExpression reference, Scope scope, bool allowOutputAliases)
        {
            var parts = reference.MultiPartIdentifier?.Identifiers.Select(identifier => identifier.Value).ToArray() ?? [];
            if (parts.Length == 0)
                return new(null, "unknown", []);
            var name = parts[^1];
            if (parts.Length == 1 && allowOutputAliases)
            {
                var aliases = scope.Outputs.Where(value => string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (aliases.Length == 1)
                {
                    Add(aliases[0]);
                    columnObserver?.Invoke(reference, aliases[0], null, true);
                    return aliases[0];
                }
                if (aliases.Length > 1)
                {
                    Review("RECIPE_COLUMN_AMBIGUOUS", $"Output alias {name} is ambiguous.");
                    return new(name, "unknown", []);
                }
            }
            var sources =
                parts.Length == 1 ? scope.Sources.ToArray() : scope.Sources.Where(source => SourceMatches(source, parts[..^1])).ToArray();
            var matches = sources
                .SelectMany(source =>
                    source
                        .Columns.Where(value => string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase))
                        .Select(value => (Source: source, Value: value with { Instance = source.Instance }))
                )
                .ToArray();
            if (matches.Length == 1)
            {
                Add(matches[0].Value);
                columnObserver?.Invoke(
                    reference,
                    matches[0].Value,
                    matches[0].Source.Catalog is not null ? matches[0].Source.Alias : null,
                    false
                );
                return matches[0].Value;
            }
            if (matches.Length > 1)
            {
                Review("RECIPE_COLUMN_AMBIGUOUS", $"Column {string.Join('.', parts)} matches multiple rowsets/columns.");
                return new(name, "unknown", []);
            }
            if (sources.Any(source => source.Unknown))
            {
                Review("RECIPE_COLUMN_UNRESOLVED", $"Column {name} belongs to an unresolved rowset.");
                return new(name, "unknown", []);
            }
            if ((parts.Length == 1 || sources.Length == 0) && scope.Parent is not null)
                return Column(reference, scope.Parent, false);
            if (parts.Length > 1 && sources.Length == 0)
                Error("RECIPE_ALIAS_MISSING", $"Qualifier {string.Join('.', parts[..^1])} is not an available SQL alias/table.");
            else if (sources.Length == 1 && sources[0].Catalog is { } table)
                Error(
                    "RECIPE_COLUMN_MISSING",
                    $"{table.Table.Schema}.{table.Table.Name}.{name} is missing from the current schema (removed or renamed)."
                );
            else
                Error(
                    "RECIPE_COLUMN_MISSING",
                    $"Column {name} is missing from the current rowset; recipe inputs expose only Value and Ordinal."
                );
            return new(name, "unknown", []);
        }
    }
}
