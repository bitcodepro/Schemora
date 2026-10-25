using System.Net;
using System.Text;
using Schemora.Relationships;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record DocumentationOptions(
    string Language = "en",
    bool Objects = true,
    bool Relations = true,
    bool Evidence = true,
    bool Recipes = false
);

public static class SchemaDocumentation
{
    public static string Export(
        CatalogStore store,
        string workspaceId,
        RelationshipGraph? relationships = null,
        DocumentationOptions? options = null
    )
    {
        options ??= new();
        var language = options.Language is "ru" or "uk" ? options.Language : "en";
        string L(string en, string ru, string uk) =>
            language == "ru" ? ru
            : language == "uk" ? uk
            : en;
        string Label(string value) =>
            value switch
            {
                "ready" => L("Ready", "Готово", "Готово"),
                "warning" or "Warning" => L("Warning", "Есть замечания", "Є зауваження"),
                "failed" or "Broken" => L("Failed", "Ошибка", "Помилка"),
                "pending" => L("Pending", "Ожидание", "Очікування"),
                "unavailable" => L("Unavailable", "Недоступно", "Недоступно"),
                "Valid" => L("Compatible", "Совместим", "Сумісний"),
                "NeedsReview" => L("Needs review", "Нужна проверка", "Потрібна перевірка"),
                "confirmed" => L("Confirmed", "Подтверждено", "Підтверджено"),
                "candidate" => L("Candidate", "Кандидат", "Кандидат"),
                "rejected" => L("Rejected", "Отклонено", "Відхилено"),
                "view" => L("View", "Представление", "Подання"),
                "procedure" => L("Procedure", "Процедура", "Процедура"),
                "function" => L("Function", "Функция", "Функція"),
                "synonym" => L("Synonym", "Синоним", "Синонім"),
                "physical" => L("Physical", "Физическая", "Фізичний"),
                "mapped" => L("ORM mapping", "ORM-привязка", "ORM-прив’язка"),
                "ef-mapping" => L("EF mapping", "Привязка EF", "Прив’язка EF"),
                "declared-fk" => L("Declared foreign key", "Объявленный внешний ключ", "Оголошений зовнішній ключ"),
                "scalarFunction" => L("Scalar function", "Скалярная функция", "Скалярна функція"),
                "tableFunction" => L("Table function", "Табличная функция", "Таблична функція"),
                "foreign-key" => L("Foreign key", "Внешний ключ", "Зовнішній ключ"),
                "dapper-sql" => L("Dapper SQL", "SQL Dapper", "SQL Dapper"),
                "manual-relation" => L("Manual relationship", "Ручная связь", "Ручний зв’язок"),
                _ => value,
            };
        var catalog = store.Catalog(workspaceId);
        var graphs = catalog.Databases.ToDictionary(db => db.Id, db => store.CurrentGraph(db.Id));
        var names = graphs
            .SelectMany(pair =>
                (pair.Value?.Tables ?? [])
                    .Select(t => new { t.Id, Name = t.Schema + "." + t.Name })
                    .Concat((pair.Value?.Objects ?? []).Select(o => new { o.Id, Name = o.Schema + "." + o.Name }))
            )
            .ToDictionary(o => o.Id, o => o.Name);
        var relationshipTables = relationships?.Tables.ToDictionary(t => t.Table.Id);
        static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
        var html = new StringBuilder(
            "<!doctype html><html lang=\""
                + language
                + "\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>"
                + H(L("Schema documentation", "Документация схемы", "Документація схеми"))
                + "</title><style>body{font:16px system-ui;max-width:1100px;margin:40px auto;padding:20px}table{border-collapse:collapse;width:100%;margin:20px 0}td,th{border:1px solid #aaa;padding:9px;text-align:left}code{overflow-wrap:anywhere}section{break-inside:avoid}a{color:#126b68}</style><h1>"
        )
            .Append(H(catalog.Workspace.Name))
            .Append(
                "</h1><p>"
                    + H(
                        L(
                            "Schema metadata only. Generated locally by Schemora.",
                            "Только метаданные схемы. Создано локально в Schemora.",
                            "Лише метадані схеми. Створено локально в Schemora."
                        )
                    )
                    + "</p><nav>"
            );
        foreach (var db in catalog.Databases)
            html.Append("<a href=\"#db-").Append(H(db.Id)).Append("\">").Append(H(db.Name)).Append("</a> · ");
        html.Append("</nav>");
        foreach (var db in catalog.Databases)
        {
            var graph = graphs[db.Id];
            if (graph is null)
                continue;
            html.Append("<h2 id=\"db-")
                .Append(H(db.Id))
                .Append("\">")
                .Append(H(db.Name))
                .Append("</h2><p>" + H(L("Status:", "Статус:", "Стан:")) + " ")
                .Append(H(Label(db.Status)))
                .Append("</p>");
            if (options.Objects)
                foreach (var table in graph.Tables)
                {
                    html.Append("<section><h3>")
                        .Append(H(table.Schema + "." + table.Name))
                        .Append("</h3><p>" + H(L("Primary key:", "Первичный ключ:", "Первинний ключ:")) + " ")
                        .Append(H(string.Join(", ", table.PrimaryKey)))
                        .Append(
                            "</p><table><tr><th>"
                                + H(L("Column", "Колонка", "Стовпець"))
                                + "</th><th>"
                                + H(L("Type", "Тип", "Тип"))
                                + "</th><th>"
                                + H(L("Nullable", "Допускает NULL", "Допускає NULL"))
                                + "</th></tr>"
                        );
                    foreach (var c in table.Columns)
                        html.Append("<tr><td>")
                            .Append(H(c.Name))
                            .Append("</td><td>")
                            .Append(H(c.DataType))
                            .Append("</td><td>")
                            .Append(c.IsNullable ? L("Yes", "Да", "Так") : L("No", "Нет", "Ні"))
                            .Append("</td></tr>");
                    html.Append("</table><ul>");
                    foreach (var key in table.UniqueKeys ?? [])
                        html.Append("<li>UNIQUE ")
                            .Append(H(key.Name))
                            .Append(": ")
                            .Append(H(string.Join(", ", key.Columns)))
                            .Append("</li>");
                    html.Append("</ul></section>");
                }
            if (options.Relations)
            {
                html.Append("<h3>" + H(L("Declared relationships", "Объявленные связи", "Оголошені зв’язки")) + "</h3><ul>");
                foreach (var relation in graph.Relations)
                    html.Append("<li>")
                        .Append(H(relation.Name))
                        .Append(" · ")
                        .Append(H(string.Join(", ", relation.FromColumns)))
                        .Append(" → ")
                        .Append(H(string.Join(", ", relation.ToColumns)))
                        .Append("</li>");
                html.Append("</ul>");
            }
            if (options.Objects)
            {
                html.Append("<h3>" + H(L("Views and routines", "Представления и процедуры", "Подання та процедури")) + "</h3><ul>");
                foreach (var obj in graph.Objects ?? [])
                    html.Append("<li>").Append(H(obj.Schema + "." + obj.Name)).Append(" · ").Append(H(Label(obj.Kind))).Append("</li>");
                html.Append("</ul>");
            }
        }
        if (options.Evidence && relationships is not null)
        {
            html.Append("<h2>" + H(L("Relationship evidence", "Доказательства связей", "Докази зв’язків")) + "</h2><ul>");
            foreach (var edge in relationships.Relations)
            {
                var from = relationshipTables!.GetValueOrDefault(edge.Relation.FromTableId);
                var to = relationshipTables!.GetValueOrDefault(edge.Relation.ToTableId);
                html.Append("<li><strong>")
                    .Append(H(edge.Relation.Name))
                    .Append("</strong>: ")
                    .Append(H(from?.DatabaseName + "." + from?.Table.Schema + "." + from?.Table.Name))
                    .Append(" → ")
                    .Append(H(to?.DatabaseName + "." + to?.Table.Schema + "." + to?.Table.Name))
                    .Append(" · ")
                    .Append(H(Label(edge.Status)))
                    .Append(" · ")
                    .Append(edge.Relation.Confidence.ToString("P0", System.Globalization.CultureInfo.InvariantCulture))
                    .Append("<ul>");
                foreach (var evidence in edge.Evidence)
                    html.Append("<li>").Append(H(Label(evidence.Kind))).Append(": ").Append(H(evidence.Detail)).Append("</li>");
                html.Append("</ul></li>");
            }
            html.Append("</ul>");
        }
        if (options.Recipes)
        {
            html.Append("<h2>" + H(L("Recipe dependencies", "Зависимости рецептов", "Залежності рецептів")) + "</h2><ul>");
            foreach (var recipe in store.All<RecipeEntry>("Recipes", workspaceId).Where(r => !r.Archived))
            {
                html.Append("<li>")
                    .Append(H(recipe.Definition.Name))
                    .Append(" · ")
                    .Append(H(Label(recipe.Validation.Status)))
                    .Append("<ul>");
                foreach (var dependency in recipe.Validation.Dependencies)
                    html.Append("<li>")
                        .Append(H(names.GetValueOrDefault(dependency.TableId, dependency.TableId)))
                        .Append(" · ")
                        .Append(H(dependency.ColumnName))
                        .Append("</li>");
                html.Append("</ul></li>");
            }
            html.Append("</ul>");
        }
        return html.Append("</html>").ToString();
    }
}
