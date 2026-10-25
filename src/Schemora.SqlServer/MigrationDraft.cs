using System.Text;

namespace Schemora.SqlServer;

public sealed record MigrationDraftResult(string Sql, int Statements, int CommentedOut);

/// <summary>
/// A review-only T-SQL draft that would make the base model look like the compared one. Schemora never runs it. The model
/// has no defaults, identity, computed columns, collations, check constraints or data, so those are left to the reader, and
/// every step that loses data (dropping tables and columns, changing primary keys) is written as a comment.
/// </summary>
public static class MigrationDraft
{
    private sealed class Section(string title)
    {
        public string Title { get; } = title;
        public List<string> Lines { get; } = [];
    }

    public static MigrationDraftResult Create(SchemaGraph from, SchemaGraph to)
    {
        var statements = 0;
        var commented = 0;
        var dropKeys = new Section("Drop foreign keys that change or disappear");
        var dropIndexes = new Section("Drop indexes and unique constraints that change or disappear");
        var newTables = new Section("New tables");
        var columns = new Section("Column changes");
        var primaryKeys = new Section("Primary key changes (review, then uncomment)");
        var addIndexes = new Section("Indexes and unique constraints");
        var addKeys = new Section("Foreign keys");
        var destructive = new Section("Destructive changes, commented out: they delete data");
        var manual = new Section("Script by hand");

        var oldTables = Tables(from);
        var newTablesByKey = Tables(to);
        var oldById = from.Tables.GroupBy(t => t.Id).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var newById = to.Tables.GroupBy(t => t.Id).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var removedTables = oldTables.Keys.Except(newTablesByKey.Keys).Order(StringComparer.Ordinal).ToArray();
        var addedTables = newTablesByKey.Keys.Except(oldTables.Keys).Order(StringComparer.Ordinal).ToArray();

        foreach (var key in addedTables)
        {
            var table = newTablesByKey[key];
            var lines = table.Columns.Select(column => "    " + Quote(column.Name) + " " + Column(column)).ToList();
            if (table.PrimaryKey.Count != 0)
            {
                var name = PrimaryKeyName(to, table);
                lines.Add(
                    "    "
                        + (name is null ? "" : "CONSTRAINT " + Quote(name) + " ")
                        + "PRIMARY KEY ("
                        + string.Join(", ", table.PrimaryKey.Select(Quote))
                        + ")"
                );
            }
            Statement(newTables, "CREATE TABLE " + Name(table) + " (\n" + string.Join(",\n", lines) + "\n);");
        }
        foreach (var key in removedTables)
            Comment(destructive, "DROP TABLE " + Name(oldTables[key]) + ";");

        foreach (var key in oldTables.Keys.Intersect(newTablesByKey.Keys).Order(StringComparer.Ordinal))
        {
            var before = oldTables[key];
            var after = newTablesByKey[key];
            var oldColumns = before.Columns.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var newColumns = after.Columns.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var column in after.Columns.Where(c => !oldColumns.ContainsKey(c.Name)))
            {
                if (!column.IsNullable)
                    Note(
                        columns,
                        $"{Quote(column.Name)} is NOT NULL: on a table with rows add a DEFAULT, or add it as NULL, backfill, then alter."
                    );
                Statement(columns, "ALTER TABLE " + Name(after) + " ADD " + Quote(column.Name) + " " + Column(column) + ";");
            }
            foreach (var column in after.Columns.Where(c => oldColumns.ContainsKey(c.Name)))
            {
                var old = oldColumns[column.Name];
                if (old.DataType == column.DataType && old.IsNullable == column.IsNullable)
                    continue;
                if (old.DataType != column.DataType)
                    Note(
                        columns,
                        $"{Quote(column.Name)}: {old.DataType} -> {column.DataType}; check that values convert without truncation, and drop and re-create indexes and keys on the column around this step."
                    );
                if (old.IsNullable && !column.IsNullable)
                    Note(columns, $"{Quote(column.Name)} becomes NOT NULL: fails while rows hold NULL; backfill first.");
                Statement(columns, "ALTER TABLE " + Name(after) + " ALTER COLUMN " + Quote(column.Name) + " " + Column(column) + ";");
            }
            foreach (var column in before.Columns.Where(c => !newColumns.ContainsKey(c.Name)))
                Comment(destructive, "ALTER TABLE " + Name(after) + " DROP COLUMN " + Quote(column.Name) + ";");
            if (!before.PrimaryKey.SequenceEqual(after.PrimaryKey, StringComparer.Ordinal))
            {
                var oldName = PrimaryKeyName(from, before);
                var newName = PrimaryKeyName(to, after);
                if (before.PrimaryKey.Count != 0)
                    Comment(
                        primaryKeys,
                        "ALTER TABLE "
                            + Name(after)
                            + " DROP CONSTRAINT "
                            + Quote(oldName ?? "<primary key name in the base database>")
                            + ";"
                    );
                if (after.PrimaryKey.Count != 0)
                    Comment(
                        primaryKeys,
                        "ALTER TABLE "
                            + Name(after)
                            + " ADD "
                            + (newName is null ? "" : "CONSTRAINT " + Quote(newName) + " ")
                            + "PRIMARY KEY ("
                            + string.Join(", ", after.PrimaryKey.Select(Quote))
                            + ");"
                    );
            }
        }

        // Foreign keys: matched like the comparison does, by source table and name, or by shape for system names.
        var oldKeys = Relations(from, oldById);
        var newKeys = Relations(to, newById);
        foreach (var (identity, (table, relation)) in oldKeys.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (newKeys.TryGetValue(identity, out var current) && SameRelation(relation, current.Relation, oldById, newById))
                continue;
            if (removedTables.Contains(Key(table)))
                continue;
            Statement(dropKeys, "ALTER TABLE " + Name(table) + " DROP CONSTRAINT " + Quote(relation.Name) + ";");
        }
        foreach (var (identity, (table, relation)) in newKeys.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (oldKeys.TryGetValue(identity, out var previous) && SameRelation(previous.Relation, relation, oldById, newById))
                continue;
            var target = newById[relation.ToTableId];
            Statement(
                addKeys,
                "ALTER TABLE "
                    + Name(table)
                    + " WITH CHECK ADD "
                    + (SchemaDiffer.IsSystemName(relation.Name) ? "" : "CONSTRAINT " + Quote(relation.Name) + " ")
                    + "FOREIGN KEY ("
                    + string.Join(", ", relation.FromColumns.Select(Quote))
                    + ") REFERENCES "
                    + Name(target)
                    + " ("
                    + string.Join(", ", relation.ToColumns.Select(Quote))
                    + ");"
            );
        }

        // Indexes and unique constraints; primary keys are handled with their tables.
        var oldIndexes = Indexes(from, oldById);
        var newIndexes = Indexes(to, newById);
        foreach (var (identity, (table, index)) in oldIndexes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (newIndexes.TryGetValue(identity, out var current) && SameIndex(index, current.Index))
                continue;
            if (removedTables.Contains(Key(table)))
                continue;
            Statement(
                dropIndexes,
                IsUniqueConstraint(table, index)
                    ? "ALTER TABLE " + Name(table) + " DROP CONSTRAINT " + Quote(index.Name) + ";"
                    : "DROP INDEX " + Quote(index.Name) + " ON " + Name(table) + ";"
            );
        }
        foreach (var (identity, (table, index)) in newIndexes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (oldIndexes.TryGetValue(identity, out var previous) && SameIndex(previous.Index, index))
                continue;
            if (index.Kind is not ("clustered" or "nonclustered"))
            {
                Note(manual, $"{index.Kind} index {Quote(index.Name)} on {Name(table)}.");
                continue;
            }
            var keyColumns = string.Join(", ", index.Columns.Select(c => Quote(c.Name) + (c.Descending ? " DESC" : "")));
            Statement(
                addIndexes,
                IsUniqueConstraint(table, index)
                    ? "ALTER TABLE "
                        + Name(table)
                        + " ADD "
                        + (SchemaDiffer.IsSystemName(index.Name) ? "" : "CONSTRAINT " + Quote(index.Name) + " ")
                        + "UNIQUE "
                        + index.Kind.ToUpperInvariant()
                        + " ("
                        + keyColumns
                        + ");"
                    : "CREATE "
                        + (index.IsUnique ? "UNIQUE " : "")
                        + index.Kind.ToUpperInvariant()
                        + " INDEX "
                        + Quote(index.Name)
                        + " ON "
                        + Name(table)
                        + " ("
                        + keyColumns
                        + ")"
                        + (
                            index.IncludedColumns.Count == 0
                                ? ""
                                : " INCLUDE (" + string.Join(", ", index.IncludedColumns.Select(Quote)) + ")"
                        )
                        + (string.IsNullOrWhiteSpace(index.Filter) ? "" : " WHERE " + index.Filter)
                        + ";"
            );
        }

        // Views, procedures and functions: their definitions belong to the source project, not to a generated draft.
        var oldObjects = (from.Objects ?? []).GroupBy(o => o.Schema + "\0" + o.Name).ToDictionary(g => g.Key, g => g.First());
        foreach (
            var item in (to.Objects ?? [])
                .GroupBy(o => o.Schema + "\0" + o.Name)
                .Select(g => g.First())
                .OrderBy(o => o.Schema + "." + o.Name)
        )
            if (!oldObjects.TryGetValue(item.Schema + "\0" + item.Name, out var old))
                Note(manual, $"{item.Kind} {Quote(item.Schema)}.{Quote(item.Name)} is new.");
            else if (old.Definition != item.Definition || old.Kind != item.Kind)
                Note(manual, $"{item.Kind} {Quote(item.Schema)}.{Quote(item.Name)} differs.");
        var newObjects = (to.Objects ?? []).Select(o => o.Schema + "\0" + o.Name).ToHashSet();
        foreach (
            var item in oldObjects.Values.Where(o => !newObjects.Contains(o.Schema + "\0" + o.Name)).OrderBy(o => o.Schema + "." + o.Name)
        )
            Note(
                manual,
                $"{item.Kind} {Quote(item.Schema)}.{Quote(item.Name)} exists only in the base model; drop it by hand if intended."
            );

        var sql = new StringBuilder()
            .Append("-- Schemora migration draft. Review only: Schemora never runs it.\n")
            .Append("-- Makes the base model ")
            .Append(OneLine(from.DatabaseName))
            .Append(" look like ")
            .Append(OneLine(to.DatabaseName))
            .Append(".\n")
            .Append("-- Not in the model, so not scripted: defaults, identity, computed columns, collations, check constraints,\n")
            .Append("-- permissions and data moves. Destructive steps are commented out.\n");
        var sections = new[] { dropKeys, dropIndexes, newTables, columns, primaryKeys, addIndexes, addKeys, destructive, manual }
            .Where(section => section.Lines.Count != 0)
            .ToArray();
        if (sections.Length == 0)
            sql.Append("\n-- The models match; there is nothing to change.\n");
        for (var index = 0; index < sections.Length; index++)
        {
            sql.Append("\n-- ").Append(index + 1).Append(". ").Append(sections[index].Title).Append('\n');
            foreach (var line in sections[index].Lines)
                sql.Append(line).Append('\n');
            sql.Append("GO\n");
        }
        return new(sql.ToString(), statements, commented);

        void Statement(Section section, string text)
        {
            section.Lines.Add(text);
            statements++;
        }
        void Comment(Section section, string text)
        {
            section.Lines.Add("-- " + text.Replace("\n", "\n-- ", StringComparison.Ordinal));
            commented++;
        }
        void Note(Section section, string text) => section.Lines.Add("-- " + OneLine(text));
    }

    private static Dictionary<string, SchemaTable> Tables(SchemaGraph graph) =>
        graph.Tables.GroupBy(Key).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);

    private static Dictionary<string, (SchemaTable Table, SchemaRelation Relation)> Relations(
        SchemaGraph graph,
        Dictionary<string, SchemaTable> tables
    ) =>
        graph
            .Relations.Where(r => r.Kind == "declared-fk" && tables.ContainsKey(r.FromTableId) && tables.ContainsKey(r.ToTableId))
            .Select(r =>
                (
                    Identity: Key(tables[r.FromTableId])
                        + "\0"
                        + (
                            SchemaDiffer.IsSystemName(r.Name)
                                ? "~"
                                    + Key(tables[r.ToTableId])
                                    + "("
                                    + string.Join(",", r.FromColumns)
                                    + ")->("
                                    + string.Join(",", r.ToColumns)
                                    + ")"
                                : r.Name
                        ),
                    Value: (tables[r.FromTableId], r)
                )
            )
            .GroupBy(item => item.Identity)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().Value, StringComparer.Ordinal);

    private static bool SameRelation(
        SchemaRelation before,
        SchemaRelation after,
        Dictionary<string, SchemaTable> oldTables,
        Dictionary<string, SchemaTable> newTables
    ) =>
        Key(oldTables[before.ToTableId]) == Key(newTables[after.ToTableId])
        && before.FromColumns.SequenceEqual(after.FromColumns, StringComparer.Ordinal)
        && before.ToColumns.SequenceEqual(after.ToColumns, StringComparer.Ordinal);

    private static Dictionary<string, (SchemaTable Table, SchemaIndex Index)> Indexes(
        SchemaGraph graph,
        Dictionary<string, SchemaTable> tables
    ) =>
        (graph.Indexes ?? [])
            .Where(i => !i.IsPrimaryKey && tables.ContainsKey(i.ObjectId))
            .Select(i =>
                (
                    Identity: Key(tables[i.ObjectId])
                        + "\0"
                        + (
                            SchemaDiffer.IsSystemName(i.Name)
                                ? "~" + i.Kind + ":" + string.Join(",", i.Columns.Select(c => c.Name))
                                : i.Name
                        ),
                    Value: (tables[i.ObjectId], i)
                )
            )
            .GroupBy(item => item.Identity)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().Value, StringComparer.Ordinal);

    private static bool SameIndex(SchemaIndex before, SchemaIndex after) =>
        before.Kind == after.Kind
        && before.IsUnique == after.IsUnique
        && before.Columns.SequenceEqual(after.Columns)
        && before.IncludedColumns.SequenceEqual(after.IncludedColumns, StringComparer.Ordinal)
        && before.Filter == after.Filter;

    private static bool IsUniqueConstraint(SchemaTable table, SchemaIndex index) =>
        index.IsUnique && (table.UniqueKeys ?? []).Any(key => key.Name == index.Name);

    private static string? PrimaryKeyName(SchemaGraph graph, SchemaTable table) =>
        (graph.Indexes ?? []).FirstOrDefault(i => i.IsPrimaryKey && i.ObjectId == table.Id) is { } key
        && !SchemaDiffer.IsSystemName(key.Name)
            ? key.Name
            : null;

    private static string Key(SchemaTable table) => table.Schema + "\0" + table.Name;

    private static string Name(SchemaTable table) => Quote(table.Schema) + "." + Quote(table.Name);

    private static string Column(SchemaColumn column) => column.DataType + (column.IsNullable ? " NULL" : " NOT NULL");

    private static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static string OneLine(string text) => text.ReplaceLineEndings(" ");
}
