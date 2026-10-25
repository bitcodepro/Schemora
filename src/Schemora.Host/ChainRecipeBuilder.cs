using System.Text;
using System.Text.RegularExpressions;
using Schemora.RecipeEngine;
using Schemora.Relationships;

namespace Schemora.Host;

public sealed record ChainRecipeColumn(string TableId, string ColumnName, string? Alias = null);

public sealed record ChainRecipeRequest(
    string FromTableId,
    string ToTableId,
    IReadOnlyList<string> RelationIds,
    string InputColumn,
    string InputName,
    IReadOnlyList<ChainRecipeColumn> Columns,
    string? Name = null
);

public sealed record ChainRecipePreview(RecipeDefinition Definition, IReadOnlyList<string> Warnings);

public sealed class ChainRecipeBuilder(IRelationshipContext relationships, RecipeValidator validator)
{
    public async Task<ChainRecipePreview> Preview(string workspaceId, ChainRecipeRequest request, CancellationToken ct)
    {
        if (
            request is null
            || string.IsNullOrWhiteSpace(request.FromTableId)
            || string.IsNullOrWhiteSpace(request.ToTableId)
            || string.IsNullOrWhiteSpace(request.InputColumn)
            || request.RelationIds is null
            || request.RelationIds.Any(string.IsNullOrWhiteSpace)
            || request.RelationIds.Count > 12
            || request.RelationIds.Distinct().Count() != request.RelationIds.Count
            || request.Columns is null
            || request.Columns.Count is < 1 or > 200
            || request.Columns.Any(column =>
                column is null || string.IsNullOrWhiteSpace(column.TableId) || string.IsNullOrWhiteSpace(column.ColumnName)
            )
            || string.IsNullOrWhiteSpace(request.InputName)
            || !Regex.IsMatch(request.InputName, @"^[A-Za-z][A-Za-z0-9_]{0,63}\z", RegexOptions.CultureInvariant)
            || request.Name?.Length > 160
        )
            throw new AppException("CHAIN_RECIPE_INVALID", "Выберите маршрут до 12 связей, от 1 до 200 полей и имя входного списка.");
        var context = await relationships.GetSnapshot(workspaceId, ct);
        var graph = context.Graph;
        var tables = graph.Tables.ToDictionary(t => t.Table.Id, StringComparer.Ordinal);
        if (!tables.TryGetValue(request.FromTableId, out var first) || !tables.ContainsKey(request.ToTableId))
            throw new AppException("TABLE_NOT_FOUND", "Таблица отсутствует в текущей модели.", 404);
        var route = new List<RelationshipTable> { first };
        var edges = new List<RelationshipEdge>();
        foreach (var id in request.RelationIds)
        {
            var edge = graph.Relations.FirstOrDefault(e => e.Relation.Id == id);
            if (
                edge is null
                || !edge.IsTrusted
                || edge.Status is not ("physical" or "mapped" or "confirmed")
                || !RelationshipRules.Valid(edge.Relation, tables)
            )
                throw new AppException("CHAIN_RELATION_UNTRUSTED", "Маршрут содержит отсутствующую или недоверенную связь.", 409);
            var current = route[^1].Table.Id;
            var next =
                edge.Relation.FromTableId == current ? edge.Relation.ToTableId
                : edge.Relation.ToTableId == current ? edge.Relation.FromTableId
                : null;
            if (next is null || route.Any(t => t.Table.Id == next))
                throw new AppException("CHAIN_ROUTE_INVALID", "Связи должны задавать непрерывный маршрут без повторения таблиц.");
            route.Add(tables[next]);
            edges.Add(edge);
        }
        if (route[^1].Table.Id != request.ToTableId)
            throw new AppException("CHAIN_ROUTE_INVALID", "Выбранный маршрут не приводит к конечной таблице.");
        if (
            route
                .GroupBy(t => t.DatabaseName, StringComparer.OrdinalIgnoreCase)
                .Any(g => g.Select(t => t.DatabaseId).Distinct().Count() > 1)
        )
            throw new AppException("DATABASE_NAME_AMBIGUOUS", "Разным БД маршрута назначено одинаковое SQL-имя. Уточните имена БД.");
        var inputColumn =
            first.Table.Columns.FirstOrDefault(c => c.Name == request.InputColumn)
            ?? throw new AppException("COLUMN_NOT_FOUND", "Входная колонка отсутствует в начальной таблице.", 404);
        var input = Input(inputColumn.DataType, request.InputName);
        var aliases = route.Select((table, index) => (table.Table.Id, Alias: "t" + index)).ToDictionary(t => t.Id, t => t.Alias);
        var projection = new List<string>();
        var outputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in request.Columns)
        {
            if (
                !aliases.TryGetValue(column.TableId, out var alias)
                || !tables[column.TableId].Table.Columns.Any(c => c.Name == column.ColumnName)
            )
                throw new AppException("CHAIN_COLUMN_INVALID", "Поля результата должны принадлежать таблицам выбранного маршрута.");
            var output = string.IsNullOrWhiteSpace(column.Alias) ? column.ColumnName : column.Alias.Trim();
            if (output.Length > 128 || output.Any(char.IsControl) || !outputNames.Add(output))
                throw new AppException("CHAIN_OUTPUT_ALIAS_INVALID", "Задайте уникальные имена полей результата (до 128 символов).");
            projection.Add($"{Q(alias)}.{Q(column.ColumnName)} AS {Q(output)}");
        }
        var sql = new StringBuilder("SELECT ")
            .Append(string.Join(",\n       ", projection))
            .Append("\nFROM ")
            .Append(Name(first))
            .Append(" AS [t0]");
        for (var i = 0; i < edges.Count; i++)
        {
            var relation = edges[i].Relation;
            var forward = relation.FromTableId == route[i].Table.Id;
            var from = forward ? relation.FromColumns : relation.ToColumns;
            var to = forward ? relation.ToColumns : relation.FromColumns;
            sql.Append("\nINNER JOIN ")
                .Append(Name(route[i + 1]))
                .Append(" AS ")
                .Append(Q("t" + (i + 1)))
                .Append(" ON ")
                .Append(string.Join(" AND ", from.Zip(to).Select(p => $"{Q("t" + i)}.{Q(p.First)} = {Q("t" + (i + 1))}.{Q(p.Second)}")));
        }
        sql.Append("\nWHERE [t0].").Append(Q(inputColumn.Name)).Append(" IN ({{ids:").Append(input.Name).Append("}});");
        var definition = new RecipeDefinition(
            "",
            string.IsNullOrWhiteSpace(request.Name) ? first.Table.Name + " → " + route[^1].Table.Name : request.Name.Trim(),
            "Создан из выбранного доверенного маршрута.",
            ["chain"],
            "sqlserver",
            route.Select(t => t.DatabaseId).Distinct().ToArray(),
            1,
            sql.ToString(),
            [input]
        );
        var validation = validator.Validate(definition, graph);
        if (validation.Status is not ("Valid" or "Warning"))
            throw new AppException(
                "CHAIN_VALIDATION_FAILED",
                "Сгенерированный маршрут требует проверки: " + string.Join("; ", validation.Diagnostics.Select(d => d.Message))
            );
        definition = definition with { Dependencies = validation.Dependencies };
        if (relationships.ContextRevision(workspaceId) != context.Revision)
            throw new AppException(
                "RELATION_CONTEXT_CHANGED",
                "Модель изменилась во время построения рецепта. Выберите маршрут повторно.",
                409
            );
        return new(
            definition,
            validation
                .Diagnostics.Select(d => d.Message)
                .Concat(
                    input.Type == "varchar"
                        ?
                        [
                            "Для varchar по умолчанию принимается ASCII. Другую кодировку и collation можно явно задать в редакторе рецепта.",
                        ]
                        : []
                )
                .ToArray()
        );
    }

    private static RecipeInputDefinition Input(string sqlType, string name)
    {
        var type = sqlType
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace("[", "", StringComparison.Ordinal)
            .Replace("]", "", StringComparison.Ordinal)
            .ToLowerInvariant();
        // Preserve the default input policy: deduplication is an explicit authoring choice.
        if (type is "tinyint" or "smallint")
            return new(name, "int");
        if (type is "int" or "bigint" or "uniqueidentifier")
            return new(name, type);
        var match = Regex.Match(type, @"^(n?varchar)\((max|\d+)\)\z", RegexOptions.CultureInvariant);
        if (match.Success)
        {
            var length =
                match.Groups[2].Value == "max" ? -1
                : int.TryParse(match.Groups[2].Value, out var parsed) ? parsed
                : 0;
            if (length == -1 || length > 0 && length <= (match.Groups[1].Value == "nvarchar" ? 4000 : 8000))
                return new(name, match.Groups[1].Value, length);
        }
        throw new AppException(
            "CHAIN_INPUT_TYPE_UNSUPPORTED",
            "Входная колонка должна иметь целочисленный тип, uniqueidentifier, nvarchar(n/max) или varchar(n/max)."
        );
    }

    private static string Q(string value) => "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static string Name(RelationshipTable table) => Q(table.DatabaseName) + "." + Q(table.Table.Schema) + "." + Q(table.Table.Name);
}
