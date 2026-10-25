using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Schemora.RecipeEngine;

public sealed partial class RecipeValidator
{
    private sealed partial class Binder
    {
        private sealed record Pair(Origin Left, Origin Right);

        private void ValidateRelationships(BooleanExpression predicate, Scope scope)
        {
            var pairs = new List<Pair>();
            foreach (var comparison in Equalities(predicate))
            {
                var first = Expression(comparison.FirstExpression, scope);
                var second = Expression(comparison.SecondExpression, scope);
                if (
                    !first.Direct
                    || !second.Direct
                    || first.Instance is null
                    || second.Instance is null
                    || first.Instance == second.Instance
                )
                    continue;
                scope.Connections.Add((first.Instance, second.Instance));
                if (first.Input is not null || second.Input is not null)
                    continue;
                if (first.Origins.Count != 1 || second.Origins.Count != 1)
                {
                    Review("RECIPE_JOIN_LINEAGE", "A join operand has multiple/computed column origins.");
                    continue;
                }
                var left = first.Origins[0];
                var right = second.Origins[0];
                if (string.CompareOrdinal(left.TableId, right.TableId) <= 0)
                    pairs.Add(new(left, right));
                else
                    pairs.Add(new(right, left));
            }
            foreach (var group in pairs.GroupBy(pair => (pair.Left.TableId, Target: pair.Right.TableId)))
            {
                var observed = group.DistinctBy(pair => (pair.Left.Column, pair.Right.Column)).ToArray();
                var first = observed[0];
                var matching = graph.Relations.Where(edge => Matches(edge.Relation, observed)).ToArray();
                foreach (var edge in matching)
                    dependencies.Add(new(first.Left.DatabaseId, first.Left.TableId, RelationId: edge.Relation.Id));
                if (matching.Any(edge => edge.IsTrusted))
                    continue;
                var detail = string.Join(
                    " AND ",
                    observed.Select(pair =>
                        $"{pair.Left.Schema}.{pair.Left.Table}.{pair.Left.Column} = {pair.Right.Schema}.{pair.Right.Table}.{pair.Right.Column}"
                    )
                );
                Review(
                    matching.Length == 0 ? "RECIPE_JOIN_UNMAPPED" : "RECIPE_JOIN_UNCONFIRMED",
                    $"Join {detail} has no exact trusted relation for its complete ordered column pairs. Review/confirm the relationship before relying on generated joins."
                );
            }
        }

        private void ValidateSubqueryRelationship(Value first, Value second, bool equality)
        {
            if (first.Input is not null || second.Input is not null || first.Origins.Count == 0 || second.Origins.Count == 0)
                return;
            if (
                !equality
                || !(first.Direct || first.ExactProjection)
                || !(second.Direct || second.ExactProjection)
                || first.Origins.Count != 1
                || second.Origins.Count != 1
            )
            {
                Review(
                    "RECIPE_SUBQUERY_JOIN_UNRESOLVED",
                    "A cross-rowset subquery comparison has computed/non-equality lineage; review its relationship semantics."
                );
                return;
            }
            var left = first.Origins[0];
            var right = second.Origins[0];
            var pair = string.CompareOrdinal(left.TableId, right.TableId) <= 0 ? new Pair(left, right) : new Pair(right, left);
            var matches = graph.Relations.Where(edge => Matches(edge.Relation, [pair])).ToArray();
            foreach (var edge in matches)
                dependencies.Add(new(left.DatabaseId, left.TableId, RelationId: edge.Relation.Id));
            if (matches.Any(edge => edge.IsTrusted))
                return;
            Review(
                matches.Length == 0 ? "RECIPE_SUBQUERY_JOIN_UNMAPPED" : "RECIPE_SUBQUERY_JOIN_UNCONFIRMED",
                $"Subquery comparison {left.Schema}.{left.Table}.{left.Column} = {right.Schema}.{right.Table}.{right.Column} has no exact trusted relation; review/confirm this relationship."
            );
        }

        private static bool Matches(Schemora.SqlServer.SchemaRelation relation, IReadOnlyList<Pair> pairs)
        {
            if (pairs.Count == 0 || relation.FromColumns.Count != relation.ToColumns.Count)
                return false;
            var first = pairs[0];
            var observed = pairs.Select(pair => pair.Left.Column + "\0" + pair.Right.Column).ToHashSet(StringComparer.Ordinal);
            if (
                relation.FromTableId == first.Left.TableId
                && relation.ToTableId == first.Right.TableId
                && observed.SetEquals(relation.FromColumns.Zip(relation.ToColumns, (left, right) => left + "\0" + right))
            )
                return true;
            return relation.ToTableId == first.Left.TableId
                && relation.FromTableId == first.Right.TableId
                && observed.SetEquals(relation.FromColumns.Zip(relation.ToColumns, (left, right) => right + "\0" + left));
        }

        private static IEnumerable<BooleanComparisonExpression> Equalities(BooleanExpression expression)
        {
            switch (expression)
            {
                case BooleanComparisonExpression comparison when comparison.ComparisonType == BooleanComparisonType.Equals:
                    yield return comparison;
                    break;
                case BooleanParenthesisExpression parentheses:
                    foreach (var item in Equalities(parentheses.Expression))
                        yield return item;
                    break;
                case BooleanBinaryExpression binary when binary.BinaryExpressionType == BooleanBinaryExpressionType.And:
                    foreach (var item in Equalities(binary.FirstExpression))
                        yield return item;
                    foreach (var item in Equalities(binary.SecondExpression))
                        yield return item;
                    break;
            }
        }
    }
}
