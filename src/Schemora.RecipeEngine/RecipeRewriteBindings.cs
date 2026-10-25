using Microsoft.SqlServer.TransactSql.ScriptDom;
using Schemora.Relationships;
using Schemora.SqlServer;

namespace Schemora.RecipeEngine;

internal sealed record RecipeRewriteOrigin(string DatabaseId, string TableId, string Column);

internal sealed record RecipeRewriteColumn(
    ColumnReferenceExpression Reference,
    string? PhysicalAlias,
    bool OutputAlias,
    bool Direct,
    IReadOnlyList<RecipeRewriteOrigin> Origins
);

internal sealed record RecipeRewriteBindingResult(IReadOnlyList<RecipeRewriteColumn> Columns, IReadOnlyList<SchemaDiagnostic> Diagnostics);

public sealed partial class RecipeValidator
{
    internal RecipeRewriteBindingResult BindForRewrite(
        RecipeDefinition recipe,
        RelationshipGraph graph,
        string text,
        IReadOnlyDictionary<string, RecipeInputDefinition> inputAliases
    )
    {
        var columns = new Dictionary<ColumnReferenceExpression, RecipeRewriteColumn>();
        var binder = new Binder(
            recipe,
            graph,
            (node, value, alias, outputAlias) =>
            {
                columns[node] = new(
                    node,
                    alias,
                    outputAlias,
                    value.Direct,
                    value.Origins.Select(origin => new RecipeRewriteOrigin(origin.DatabaseId, origin.TableId, origin.Column)).ToArray()
                );
            },
            inputAliases
        );
        var result = binder.Run(text);
        return new(columns.Values.ToArray(), result.Diagnostics);
    }
}
