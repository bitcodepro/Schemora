namespace Schemora.SqlServer;

public sealed record SchemaChange(string Kind, string TableId, string Schema, string Table, string? Column, string? Before, string? After);

public sealed record RenameCandidate(string Kind, string OldTableId, string NewTableId, string OldName, string NewName, string Reason);

public sealed record SchemaDiffResult(
    IReadOnlyList<SchemaChange> Changes,
    IReadOnlyList<RenameCandidate> RenameCandidates,
    IReadOnlyList<SchemaDiagnostic> Diagnostics
);

/// <summary>Deterministic bounded comparison; structural similarity is a review candidate, never a rename fact.</summary>
public sealed partial class SchemaDiffer
{
    public const int MaximumChanges = 10_000;
    public const int MaximumRenameCandidates = 1_000;

    public SchemaDiffResult Compare(SchemaGraph before, SchemaGraph after)
    {
        var changes = new List<SchemaChange>();
        var candidates = new List<RenameCandidate>();
        var diagnostics = new List<SchemaDiagnostic>();
        var changeLimit = false;
        var renameLimit = false;
        var renameScans = 0;
        var scanLimit = false;
        var oldTables = Index(before.Tables, "before");
        var newTables = Index(after.Tables, "after");
        if (oldTables is null || newTables is null)
            return new(changes, candidates, diagnostics);
        var removed = oldTables
            .Where(pair => !newTables.ContainsKey(pair.Key))
            .Select(pair => pair.Value)
            .OrderBy(Key, StringComparer.Ordinal)
            .ToArray();
        var added = newTables
            .Where(pair => !oldTables.ContainsKey(pair.Key))
            .Select(pair => pair.Value)
            .OrderBy(Key, StringComparer.Ordinal)
            .ToArray();
        foreach (var table in removed)
            Add("tableRemoved", table, null, Describe(table), null);
        foreach (var table in added)
            Add("tableAdded", table, null, null, Describe(table));
        foreach (var key in oldTables.Keys.Intersect(newTables.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var oldTable = oldTables[key];
            var newTable = newTables[key];
            if (!Columns(oldTable, out var oldColumns) || !Columns(newTable, out var newColumns))
                continue;
            var removedColumns = oldColumns
                .Values.Where(column => !newColumns.ContainsKey(column.Name))
                .OrderBy(column => column.Name, StringComparer.Ordinal)
                .ToArray();
            var addedColumns = newColumns
                .Values.Where(column => !oldColumns.ContainsKey(column.Name))
                .OrderBy(column => column.Name, StringComparer.Ordinal)
                .ToArray();
            foreach (var column in removedColumns)
                Add("columnRemoved", newTable, column.Name, Describe(column), null);
            foreach (var column in addedColumns)
                Add("columnAdded", newTable, column.Name, null, Describe(column));
            foreach (var name in oldColumns.Keys.Intersect(newColumns.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var oldColumn = oldColumns[name];
                var newColumn = newColumns[name];
                if (!string.Equals(oldColumn.DataType, newColumn.DataType, StringComparison.Ordinal))
                    Add("columnTypeChanged", newTable, name, oldColumn.DataType, newColumn.DataType);
                if (oldColumn.IsNullable != newColumn.IsNullable)
                    Add(
                        "columnNullabilityChanged",
                        newTable,
                        name,
                        oldColumn.IsNullable ? "nullable" : "not null",
                        newColumn.IsNullable ? "nullable" : "not null"
                    );
            }
            if (!oldTable.PrimaryKey.SequenceEqual(newTable.PrimaryKey, StringComparer.Ordinal))
                Add("primaryKeyChanged", newTable, null, string.Join(", ", oldTable.PrimaryKey), string.Join(", ", newTable.PrimaryKey));
            foreach (var oldColumn in removedColumns)
            {
                if (renameLimit || scanLimit)
                    break;
                foreach (var newColumn in addedColumns)
                {
                    if (!CanScan())
                        break;
                    if (SameShape(oldColumn, newColumn))
                        Candidate(
                            new(
                                "column",
                                oldTable.Id,
                                newTable.Id,
                                oldColumn.Name,
                                newColumn.Name,
                                "Removed and added columns have the same exact SQL type and nullability in the same table; names alone do not prove a rename."
                            )
                        );
                    if (renameLimit)
                        break;
                }
            }
        }
        foreach (var oldTable in removed)
        {
            if (renameLimit || scanLimit)
                break;
            foreach (var newTable in added)
            {
                if (!CanScan())
                    break;
                if (!SameTableShape(oldTable, newTable))
                    continue;
                Candidate(
                    new(
                        "table",
                        oldTable.Id,
                        newTable.Id,
                        oldTable.Schema + "." + oldTable.Name,
                        newTable.Schema + "." + newTable.Name,
                        "Removed and added tables have the same ordered column names, exact types, nullability and primary key. This is a structural candidate requiring review."
                    )
                );
                if (renameLimit)
                    break;
            }
        }
        CompareRelations();
        CompareExtended(before, after, changes, diagnostics);
        return new(changes, candidates, diagnostics);

        bool CanScan()
        {
            if (++renameScans <= 100_000)
                return true;
            if (!scanLimit)
            {
                scanLimit = true;
                diagnostics.Add(
                    new(
                        "warning",
                        "DIFF_RENAME_SCAN_LIMIT",
                        "Structural rename analysis exceeded 100000 comparisons; review candidates are partial."
                    )
                );
            }
            return false;
        }
        Dictionary<string, SchemaTable>? Index(IReadOnlyList<SchemaTable> tables, string side)
        {
            var result = new Dictionary<string, SchemaTable>(StringComparer.Ordinal);
            foreach (var table in tables)
                if (!result.TryAdd(Key(table), table))
                {
                    diagnostics.Add(
                        new(
                            "error",
                            "DIFF_TABLE_AMBIGUOUS",
                            $"The {side} snapshot contains duplicate schema/table names; comparison was blocked."
                        )
                    );
                    return null;
                }
            return result;
        }
        bool Columns(SchemaTable table, out Dictionary<string, SchemaColumn> result)
        {
            result = new(StringComparer.Ordinal);
            foreach (var column in table.Columns)
                if (!result.TryAdd(column.Name, column))
                {
                    diagnostics.Add(
                        new(
                            "error",
                            "DIFF_COLUMN_AMBIGUOUS",
                            "A snapshot contains duplicate column names; this table was excluded from column comparison."
                        )
                    );
                    return false;
                }
            return true;
        }
        void Add(string kind, SchemaTable table, string? column, string? oldValue, string? newValue)
        {
            if (changes.Count < MaximumChanges)
                changes.Add(new(kind, table.Id, table.Schema, table.Name, column, oldValue, newValue));
            else if (!changeLimit)
            {
                changeLimit = true;
                diagnostics.Add(new("warning", "DIFF_CHANGE_LIMIT", "Schema changes exceeded 10000; the bounded result is partial."));
            }
        }
        void Candidate(RenameCandidate candidate)
        {
            if (candidates.Count < MaximumRenameCandidates)
                candidates.Add(candidate);
            else if (!renameLimit)
            {
                renameLimit = true;
                diagnostics.Add(new("warning", "DIFF_RENAME_LIMIT", "Rename candidates exceeded 1000; the bounded result is partial."));
            }
        }
        void CompareRelations()
        {
            var oldById = before
                .Tables.GroupBy(table => table.Id, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            var newById = after
                .Tables.GroupBy(table => table.Id, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            var oldRelations = before
                .Relations.Select(relation => Normalize(relation, oldById))
                .Where(item => item is not null)
                .Select(item => item!)
                .OrderBy(item => item.Identity, StringComparer.Ordinal)
                .ToArray();
            var newRelations = after
                .Relations.Select(relation => Normalize(relation, newById))
                .Where(item => item is not null)
                .Select(item => item!)
                .OrderBy(item => item.Identity, StringComparer.Ordinal)
                .ToArray();
            var oldGroups = oldRelations
                .GroupBy(item => item.Identity, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var newGroups = newRelations
                .GroupBy(item => item.Identity, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            foreach (var key in oldGroups.Keys.Union(newGroups.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                oldGroups.TryGetValue(key, out var oldItems);
                newGroups.TryGetValue(key, out var newItems);
                if (oldItems?.Length > 1 || newItems?.Length > 1)
                {
                    diagnostics.Add(
                        new(
                            "warning",
                            "DIFF_RELATION_AMBIGUOUS",
                            "Duplicate source-table/constraint identities were excluded from FK comparison."
                        )
                    );
                    continue;
                }
                var oldItem = oldItems?.SingleOrDefault();
                var newItem = newItems?.SingleOrDefault();
                if (oldItem is null)
                    Add("foreignKeyAdded", newItem!.Table, null, null, newItem.Shape);
                else if (newItem is null)
                    Add("foreignKeyRemoved", oldItem.Table, null, oldItem.Shape, null);
                else if (!string.Equals(oldItem.Shape, newItem.Shape, StringComparison.Ordinal))
                    Add("foreignKeyChanged", newItem.Table, null, oldItem.Shape, newItem.Shape);
            }
        }
        RelationShape? Normalize(SchemaRelation relation, IReadOnlyDictionary<string, SchemaTable> tables)
        {
            if (!tables.TryGetValue(relation.FromTableId, out var source) || !tables.TryGetValue(relation.ToTableId, out var target))
            {
                diagnostics.Add(
                    new(
                        "warning",
                        "DIFF_RELATION_UNBOUND",
                        "A snapshot FK references a missing or ambiguous table and was excluded from comparison."
                    )
                );
                return null;
            }
            // A system-named FK has a different random suffix in every deployment, so it is identified by what it links.
            var system = IsSystemName(relation.Name);
            var link = Key(target) + "(" + string.Join(",", relation.FromColumns) + ")->(" + string.Join(",", relation.ToColumns) + ")";
            return new(
                Key(source) + "\0" + (system ? "~" + link : relation.Name),
                source,
                (system ? "(system-named)" : relation.Name)
                    + ": "
                    + source.Schema
                    + "."
                    + source.Name
                    + "("
                    + string.Join(",", relation.FromColumns)
                    + ") -> "
                    + target.Schema
                    + "."
                    + target.Name
                    + "("
                    + string.Join(",", relation.ToColumns)
                    + ") ["
                    + relation.Kind
                    + "]"
            );
        }
    }

    private sealed record RelationShape(string Identity, SchemaTable Table, string Shape);

    private static string Key(SchemaTable table) => table.Schema + "\0" + table.Name;

    // SQL Server names unnamed constraints like PK__Orders__3214EC07A1B2C3D4: prefix, table and column fragments, hex suffix.
    internal static bool IsSystemName(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            name,
            "^(PK|FK|UQ|DF|CK)__.+__[0-9A-F]{8,16}$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant
        );

    private static string Describe(SchemaColumn column) => column.DataType + (column.IsNullable ? " NULL" : " NOT NULL");

    private static string Describe(SchemaTable table) =>
        string.Join("; ", table.Columns.Select(column => column.Name + " " + Describe(column)))
        + "; PK("
        + string.Join(",", table.PrimaryKey)
        + ")";

    private static bool SameShape(SchemaColumn first, SchemaColumn second) =>
        first.DataType == second.DataType && first.IsNullable == second.IsNullable;

    private static bool SameTableShape(SchemaTable first, SchemaTable second) =>
        first.Columns.Count > 0
        && first.Columns.Count == second.Columns.Count
        && first.Columns.Zip(second.Columns).All(pair => pair.First.Name == pair.Second.Name && SameShape(pair.First, pair.Second))
        && first.PrimaryKey.SequenceEqual(second.PrimaryKey, StringComparer.Ordinal);
}
