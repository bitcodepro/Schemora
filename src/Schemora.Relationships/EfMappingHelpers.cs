using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Schemora.SqlServer;

namespace Schemora.Relationships;

public sealed partial class EfMappingAnalyzer
{
    private sealed partial class State
    {
        private SchemaTable? Bind(Entity entity, SyntaxNode node, out bool proven)
        {
            proven = false;
            var bindings = entity.Tables.Select(t => (t.Name, Schema: t.Schema ?? DefaultSchema(entity))).Distinct().ToArray();
            if (bindings.Length != 1)
            {
                Warn("EF_TABLE_AMBIGUOUS", node, $"Entity {entity.Name} has no single explicit Table/ToTable mapping.");
                return null;
            }
            var binding = bindings[0];
            var tables = db.Graph.Tables.Where(t => t.Name == binding.Name && t.Schema == binding.Schema).ToArray();
            if (tables.Length != 1)
            {
                Warn(
                    "EF_TABLE_UNRESOLVED",
                    node,
                    $"Mapped table {binding.Schema}.{binding.Name} is absent/ambiguous in the assigned database."
                );
                return null;
            }
            proven =
                entity.Tables.All(t => t.Proven)
                && entity.Contexts.Count == 1
                && entity.ProvenContexts.SetEquals(entity.Contexts)
                && !(
                    entity.Tables.Any(t => t.Schema is null)
                    && entity.Contexts.Any(c => defaultSchemas.TryGetValue(c, out var schema) && schema is null)
                );
            return tables[0];
        }

        private string DefaultSchema(Entity entity) =>
            entity.Contexts.Count == 1 && defaultSchemas.TryGetValue(entity.Contexts.Single(), out var schema) && schema is not null
                ? schema
                : "dbo";

        private IReadOnlyList<string>? Columns(
            Entity entity,
            SchemaTable table,
            IReadOnlyList<string> properties,
            bool shadow,
            SyntaxNode node,
            out bool proven
        )
        {
            proven = true;
            var columns = new List<string>();
            foreach (var property in properties)
            {
                var mappings = entity.Columns.TryGetValue(property, out var mapped) ? mapped : [new(property, true)];
                var names = mappings.Select(m => m.Name).Distinct(StringComparer.Ordinal).ToArray();
                if (names.Length != 1 || !table.Columns.Any(c => c.Name == names[0]))
                {
                    Warn("EF_COLUMN_UNRESOLVED", node, $"Property {entity.Name}.{property} has no single catalog column.");
                    return null;
                }
                if (!entity.Properties.TryGetValue(property, out var clr))
                {
                    if (!shadow)
                    {
                        Warn("EF_PROPERTY_UNRESOLVED", node, $"CLR property {entity.Name}.{property} is unresolved.");
                        return null;
                    }
                }
                else if (!ClrCompatible(clr.Symbol?.Type, table.Columns.Single(c => c.Name == names[0]).DataType))
                {
                    Warn("EF_CLR_TYPE_MISMATCH", node, $"CLR/catalog type for {entity.Name}.{property} is incompatible/unresolved.");
                    return null;
                }
                proven &= mappings.All(m => m.Proven);
                columns.Add(names[0]);
            }
            if (columns.Distinct(StringComparer.Ordinal).Count() != columns.Count)
            {
                Warn("EF_COLUMN_AMBIGUOUS", node, "Selected properties map to the same column.");
                return null;
            }
            return columns;
        }

        private bool ScopeProven(SyntaxNode node, SemanticModel model, Entity entity)
        {
            var owner = EnclosingClass(node, model);
            if (owner is null)
                return false;
            if (contexts.ContainsKey(owner))
                return contexts[owner] && InModelMethod(node, model);
            return configurations.TryGetValue(owner, out var configured)
                && configured == entity.Key
                && applied.TryGetValue(owner, out var registrations)
                && registrations.Count == 1
                && node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText == "Configure";
        }

        private bool InModelMethod(SyntaxNode node, SemanticModel model)
        {
            var method = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            return method is not null
                && (model.GetDeclaredSymbol(method, ct) as IMethodSymbol)?.OverriddenMethod is { } overridden
                && overridden.Name == "OnModelCreating"
                && Official(overridden.ContainingType);
        }

        private Entity? RootEntity(InvocationExpressionSyntax first, SemanticModel model)
        {
            if (MethodName(first) == "Entity")
                return GenericEntity(first, model);
            if (
                first.Expression is MemberAccessExpressionSyntax member
                && model.GetTypeInfo(member.Expression, ct).Type is INamedTypeSymbol type
                && type.Name == "EntityTypeBuilder"
                && type.TypeArguments.Length == 1
            )
                return Find(type.TypeArguments[0]);
            return null;
        }

        private Entity? Related(InvocationExpressionSyntax call, SemanticModel model, Entity entity)
        {
            if (
                model.GetTypeInfo(call, ct).Type is INamedTypeSymbol returned
                && returned.TypeArguments.Length == 2
                && Find(returned.TypeArguments[1]) is { } found
            )
                return found;
            if (GenericEntity(call, model) is { } generic)
                return generic;
            var selector = Selectors(call, model).Names;
            return selector is { Count: 1 } && entity.Properties.TryGetValue(selector[0], out var property)
                ? Navigation(property.Symbol?.Type)
                : null;
        }

        private Entity? GenericEntity(InvocationExpressionSyntax call, SemanticModel model) =>
            call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax generic }
            && generic.TypeArgumentList.Arguments.Count == 1
                ? Find(model.GetTypeInfo(generic.TypeArgumentList.Arguments[0], ct).Type)
                : null;

        private Entity? ReferenceNavigation(ITypeSymbol? type) => Find(type);

        private Entity? Navigation(ITypeSymbol? type)
        {
            if (Find(type) is { } direct)
                return direct;
            if (
                type is INamedTypeSymbol named
                && named.TypeArguments.Length == 1
                && named.ContainingNamespace.ToDisplayString() == "System.Collections.Generic"
            )
                return Find(named.TypeArguments[0]);
            return null;
        }

        private Entity? Find(ITypeSymbol? symbol) =>
            symbol is not null && entities.TryGetValue(Key(symbol).TrimEnd('?'), out var entity) ? entity : null;

        private static void AddContext(Entity entity, string context, bool proven)
        {
            entity.Contexts.Add(context);
            if (proven)
                entity.ProvenContexts.Add(context);
        }

        private static void AddColumn(Entity entity, string property, string column, bool proven)
        {
            if (!entity.Columns.TryGetValue(property, out var columns))
                entity.Columns[property] = columns = [];
            columns.Add(new(column, proven));
        }

        private string? EnclosingClass(SyntaxNode node, SemanticModel model) =>
            node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault() is { } owner
                ? Key(model.GetDeclaredSymbol(owner, ct))
                : null;

        private void Warn(string code, SyntaxNode node, string detail)
        {
            var location = node.GetLocation().GetLineSpan();
            report(code, $"{location.Path}({location.StartLinePosition.Line + 1}): {detail}");
        }

        private static bool IsOfficialCall(InvocationExpressionSyntax call, SemanticModel model) =>
            model.GetSymbolInfo(call).Symbol is IMethodSymbol symbol
            && Official(symbol.ReducedFrom?.ContainingType ?? symbol.ContainingType);

        private static bool IsOfficialAttribute(AttributeSyntax attr, SemanticModel model, string name) =>
            model.GetSymbolInfo(attr).Symbol is IMethodSymbol symbol
            && symbol.ContainingType.Name == name
            && symbol.ContainingType.ContainingNamespace.ToDisplayString() == "System.ComponentModel.DataAnnotations.Schema"
            && symbol.ContainingAssembly.Name == "System.ComponentModel.Annotations";

        private static string AttributeName(AttributeSyntax attr, SemanticModel model) =>
            model.GetSymbolInfo(attr).Symbol is IMethodSymbol symbol
                ? symbol.ContainingType.Name.Replace("Attribute", "", StringComparison.Ordinal)
                : Short(attr.Name.ToString()).Replace("Attribute", "", StringComparison.Ordinal);

        private static string? Constant(ExpressionSyntax? expression, SemanticModel model) =>
            expression is not null && model.GetConstantValue(expression) is { HasValue: true, Value: string value } ? value : null;

        private static bool Official(INamedTypeSymbol symbol) =>
            symbol.ContainingAssembly?.Name is { } name
            && (name == "Microsoft.EntityFrameworkCore" || name.StartsWith("Microsoft.EntityFrameworkCore.", StringComparison.Ordinal))
            && symbol.ContainingNamespace.ToDisplayString().StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal);

        private static bool Conditional(SyntaxNode node) =>
            node.Ancestors()
                .Any(n =>
                    n
                        is IfStatementSyntax
                            or ConditionalExpressionSyntax
                            or SwitchStatementSyntax
                            or ForStatementSyntax
                            or ForEachStatementSyntax
                            or WhileStatementSyntax
                            or DoStatementSyntax
                            or LocalFunctionStatementSyntax
                );

        private static IReadOnlyList<InvocationExpressionSyntax> Chain(InvocationExpressionSyntax outer)
        {
            var calls = new List<InvocationExpressionSyntax>();
            for (var call = outer; ; )
            {
                calls.Add(call);
                if (call.Expression is MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax inner })
                    call = inner;
                else
                    break;
            }
            calls.Reverse();
            return calls;
        }

        private static (IReadOnlyList<string>? Names, bool Shadow) Selectors(InvocationExpressionSyntax call, SemanticModel model)
        {
            var args = call.ArgumentList.Arguments;
            if (args.Count == 1 && args[0].Expression is LambdaExpressionSyntax lambda)
            {
                var parameter = lambda switch
                {
                    SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.ValueText,
                    ParenthesizedLambdaExpressionSyntax parenthesized when parenthesized.ParameterList.Parameters.Count == 1 =>
                        parenthesized.ParameterList.Parameters[0].Identifier.ValueText,
                    _ => null,
                };
                var expressions =
                    lambda.Body is AnonymousObjectCreationExpressionSyntax anonymous
                        ? anonymous.Initializers.Select(x => x.Expression).ToArray()
                    : lambda.Body is ExpressionSyntax expression ? [expression]
                    : Array.Empty<ExpressionSyntax>();
                if (
                    expressions.Length == 0
                    || expressions.Any(e =>
                        e is not MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax identifier }
                        || identifier.Identifier.ValueText != parameter
                    )
                )
                    return (null, false);
                return (expressions.Cast<MemberAccessExpressionSyntax>().Select(e => e.Name.Identifier.ValueText).ToArray(), false);
            }
            var names = args.Select(a => Constant(a.Expression, model)).ToArray();
            return names.Length != 0 && names.All(n => !string.IsNullOrWhiteSpace(n))
                ? (names.Select(n => n!).ToArray(), true)
                : (null, false);
        }

        private static bool Compatible(string first, string second) =>
            first.Replace(" ", "").Equals(second.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

        private static bool ClrCompatible(ITypeSymbol? type, string sql)
        {
            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
                type = nullable.TypeArguments[0];
            if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumeration)
                type = enumeration.EnumUnderlyingType;
            var name = sql.Split('(')[0].ToLowerInvariant();
            return type?.SpecialType switch
            {
                SpecialType.System_Int32 => name == "int",
                SpecialType.System_Int64 => name == "bigint",
                SpecialType.System_Int16 => name == "smallint",
                SpecialType.System_Byte => name == "tinyint",
                SpecialType.System_Boolean => name == "bit",
                SpecialType.System_String => name is "nvarchar" or "varchar" or "char" or "nchar" or "text" or "ntext",
                SpecialType.System_Decimal => name is "decimal" or "numeric" or "money" or "smallmoney",
                SpecialType.System_Double => name == "float",
                SpecialType.System_Single => name == "real",
                _ => type?.ToDisplayString() switch
                {
                    "System.Guid" => name == "uniqueidentifier",
                    "System.DateTime" => name is "datetime" or "datetime2" or "date" or "smalldatetime",
                    "System.DateTimeOffset" => name == "datetimeoffset",
                    "System.TimeSpan" => name == "time",
                    _ => type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte } && (name is "binary" or "varbinary"),
                },
            };
        }

        private static string Key(ISymbol? symbol) =>
            symbol?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "", StringComparison.Ordinal) ?? "";

        private static string Short(string text) => text.Split('.').Last().Split('<')[0];

        private static string MethodName(InvocationExpressionSyntax call) =>
            call.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                _ => "",
            };
    }
}
