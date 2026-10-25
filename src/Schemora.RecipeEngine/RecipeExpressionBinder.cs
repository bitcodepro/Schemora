using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Schemora.RecipeEngine;

public sealed partial class RecipeValidator
{
    private sealed partial class Binder
    {
        private Value Expression(ScalarExpression expression, Scope scope, bool aliases = false)
        {
            switch (expression)
            {
                case ColumnReferenceExpression column:
                    return Column(column, scope, aliases);
                case ParenthesisExpression parentheses:
                    return Expression(parentheses.Expression, scope, aliases);
                case ScalarSubquery subquery:
                    var output = Subquery(subquery, scope);
                    if (output.Count != 1)
                    {
                        Error("RECIPE_SUBQUERY_COLUMN_COUNT", "A scalar/IN subquery must expose one column.");
                        return new(null, "unknown", []);
                    }
                    return output[0] with { Direct = false, Instance = null, Subquery = true, ExactProjection = output[0].Direct };
                case CastCall cast:
                    var castValue = Expression(cast.Parameter, scope, aliases);
                    new Sql170ScriptGenerator().GenerateScript(cast.DataType, out var castType);
                    return castValue with { Name = null, Type = castType.Trim(), Direct = false, ExactProjection = false };
                case ConvertCall convert:
                    var converted = Expression(convert.Parameter, scope, aliases);
                    new Sql170ScriptGenerator().GenerateScript(convert.DataType, out var convertedType);
                    return converted with { Name = null, Type = convertedType.Trim(), Direct = false, ExactProjection = false };
                case IntegerLiteral:
                    return new(null, "int", []);
                case StringLiteral literal:
                    return new(null, literal.IsNational ? "nvarchar" : "varchar", []);
                case NullLiteral:
                    return new(null, "unknown", []);
                default:
                    var values = new List<Value>();
                    expression.Accept(new Expressions(this, scope, aliases, values));
                    return new(
                        null,
                        "unknown",
                        values.SelectMany(value => value.Origins).Distinct().ToArray(),
                        values.Select(value => value.Input).Distinct().Count() == 1 ? values.FirstOrDefault()?.Input : null,
                        Subquery: values.Any(value => value.Subquery)
                    );
            }
        }

        private void Walk(TSqlFragment fragment, Scope scope, bool aliases = false) =>
            fragment.Accept(new Expressions(this, scope, aliases, []));

        private IReadOnlyList<Value> Subquery(ScalarSubquery query, Scope scope)
        {
            if (!subqueries.TryGetValue(query, out var values))
                subqueries[query] = values = Query(query.QueryExpression, scope);
            return values;
        }

        private void Compare(Value first, Value second, bool equality = true)
        {
            if (first.Input is not null && (second.Origins.Count != 0 || second.Input is not null))
                InputType(first, second);
            if (second.Input is not null && first.Origins.Count != 0)
                InputType(second, first);
            if (first.Subquery || second.Subquery)
                ValidateSubqueryRelationship(first, second, equality);
        }

        private void InputType(Value input, Value target)
        {
            var from = BaseType(input.Type);
            var to = BaseType(target.Type);
            if (from is "" or "unknown" || to is "" or "unknown" || to.StartsWith('['))
            {
                Review("RECIPE_INPUT_TYPE_UNRESOLVED", $"Input {input.Input} is compared with an unresolved/computed SQL type.");
                return;
            }
            if (from == to)
            {
                if (
                    from is "nvarchar" or "varchar"
                    && Length(input.Type) is { } inputLength
                    && Length(target.Type) is { } targetLength
                    && inputLength > targetLength
                )
                    Warning(
                        "RECIPE_INPUT_LENGTH",
                        $"Input {input.Input} permits values longer than the target column {target.Name}; review the declared maximum length."
                    );
                return;
            }
            if (
                from is "int" or "bigint" && to is "tinyint" or "smallint" or "int" or "bigint"
                || from is "int" or "bigint" or "decimal" && to is "decimal" or "numeric" or "money" or "smallmoney"
                || from is "decimal" && to is "tinyint" or "smallint" or "int" or "bigint" or "float" or "real"
            )
            {
                Warning(
                    "RECIPE_INPUT_NUMERIC_CONVERSION",
                    $"Input {input.Input} uses {from} while target {target.Name} uses {to}; SQL numeric conversion is required."
                );
                return;
            }
            if (from is "date" or "datetime2" && to is "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset")
            {
                Warning(
                    "RECIPE_INPUT_DATE_CONVERSION",
                    $"Input {input.Input} uses {from} while target {target.Name} uses {to}; SQL date conversion is required."
                );
                return;
            }
            if (from is "varchar" or "nvarchar" && to is "varchar" or "nvarchar" or "char" or "nchar")
            {
                Warning(
                    "RECIPE_INPUT_TEXT_CONVERSION",
                    $"Input {input.Input} uses {from} while target {target.Name} uses {to}; review Unicode/code-page and length semantics."
                );
                return;
            }
            Error(
                "RECIPE_INPUT_TYPE_MISMATCH",
                $"Input {input.Input} type {input.Type} is incompatible with target {target.Name} type {target.Type}."
            );
        }

        private static string BaseType(string type) => type.Split('(')[0].Trim().ToLowerInvariant();

        private static int? Length(string type)
        {
            var start = type.IndexOf('(');
            var end = type.IndexOf(')');
            if (start < 0 || end <= start)
                return null;
            var text = type[(start + 1)..end].Trim();
            return text.Equals("max", StringComparison.OrdinalIgnoreCase) ? int.MaxValue
                : int.TryParse(text, out var length) ? length
                : null;
        }

        private sealed class Expressions(Binder binder, Scope scope, bool aliases, List<Value> values) : TSqlFragmentVisitor
        {
            public override void ExplicitVisit(ColumnReferenceExpression node) => values.Add(binder.Column(node, scope, aliases));

            public override void ExplicitVisit(VariableReference node)
            {
                if (!binder.declaredVariables.Contains(node.Name))
                    binder.Error(
                        "RECIPE_PARAMETER_UNDECLARED",
                        $"SQL parameter {node.Name} has no recipe input/local declaration; only typed ID rowset placeholders are supported."
                    );
            }

            public override void ExplicitVisit(ScalarSubquery node)
            {
                foreach (var value in binder.Subquery(node, scope))
                    values.Add(value with { Direct = false, Instance = null, Subquery = true, ExactProjection = value.Direct });
            }

            public override void ExplicitVisit(ExistsPredicate node) => binder.Subquery(node.Subquery, scope);

            public override void ExplicitVisit(BooleanComparisonExpression node)
            {
                var first = binder.Expression(node.FirstExpression, scope, aliases);
                var second = binder.Expression(node.SecondExpression, scope, aliases);
                binder.Compare(first, second, node.ComparisonType == BooleanComparisonType.Equals);
            }

            public override void ExplicitVisit(InPredicate node)
            {
                var value = binder.Expression(node.Expression, scope, aliases);
                if (node.Subquery is not null)
                {
                    var columns = binder.Subquery(node.Subquery, scope);
                    if (columns.Count != 1)
                        binder.Error("RECIPE_SUBQUERY_COLUMN_COUNT", "IN subquery must expose one column.");
                    else
                        binder.Compare(value, columns[0] with { Subquery = true, ExactProjection = columns[0].Direct });
                }
                foreach (var item in node.Values)
                    binder.Expression(item, scope, aliases);
            }
        }
    }
}
