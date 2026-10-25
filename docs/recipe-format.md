# Recipes and runtime inputs

A recipe stores Id, Name, Description, Tags, Dialect=sqlserver, DatabaseIds, Version, SqlTemplate, Inputs, CompatibilityLevel and computed Dependencies. HTTP JSON uses camelCase. RecipeVersions history is immutable: editing, import, a confirmed repair and rollback all create a new version. Rollback does not delete later versions.

Template example:

~~~sql
SELECT d.DocumentId
FROM dbo.Documents AS d
WHERE d.DocumentId IN ({{ids:Ids}})
  AND d.CreatedAt >= {{value:From}};
~~~

Inputs: [{ "name": "Ids", "type": "int", "deduplicate": false, "preserveOrder": true, "strategy": "auto" }, { "name": "From", "type": "date", "deduplicate": false, "preserveOrder": true, "strategy": "auto" }].

Supported types: int, bigint, uniqueidentifier, nvarchar, varchar, date, datetime2, bit and decimal. decimal requires precision 1..38 and scale 0..precision; the value is digits with a dot, within precision and scale, with no exponent; an exact numeric literal goes into the SQL, for example `CONVERT(decimal(10,2), 1500.5)`. date accepts only yyyy-MM-dd, datetime2 accepts yyyy-MM-ddTHH:mm[:ss[.fffffff]] (a value with a space instead of T must be quoted), bit accepts 1, 0, true or false. Text requires maxLength (1..4000 / 8000, or -1 for MAX). For varchar, ASCII is allowed by default; non-ASCII requires an explicitly specified encoding windows-1251, windows-1252 or utf-8 and a matching supported Collation. A UTF8 collation requires SQL Server 2019 / compatibility 150+. Prefer nvarchar for Unicode.

A value input can be marked `optional: true` (only for {{value:...}}; for a list — `INPUT_OPTIONAL_VALUE_ONLY`). An empty or missing value is substituted as `CAST(NULL AS type)`; in parameterized mode it is an empty JSON array, which the scalar subquery turns into NULL; the generator adds a warning. To skip the condition, write: `({{value:From}} IS NULL OR o.CreatedAt >= {{value:From}})`. An invalid value is still an error: optional only allows an empty field.

The DSL supports only {{ids:Name}} (SELECT Value for IN), {{table:Name}} (rowset Ordinal,Value) and {{value:Name}} (a single value). An input used as {{value:...}} accepts exactly one value (`VALUE_INPUT_SINGLE`) and cannot also be a list (`PLACEHOLDER_KIND_MIXED`). In standalone mode it is substituted as a typed literal, for example `CONVERT(date, N'2026-10-01', 23)`; in parameterized mode it is read from the same JSON parameter. Dates are always converted with an explicit style (23 for date, 126 for datetime2), so they do not depend on the session language or SET DATEFORMAT. Comparing date/datetime2 with another date type in the schema produces a conversion warning; comparing with a non-date type is an error. Expressions, code execution and placeholders inside strings/comments/quoted identifiers are forbidden. Input names are ASCII and case-sensitive; names that conflict case-insensitively in SQL are forbidden. @SchemoraInputs and #schemora_* are reserved by the generator.

The parser reads comma/semicolon/tab/newline separators and quoted values with a doubled quote. It does not convert string IDs to numbers, does not change GUID case and does not remove duplicates unless a policy is chosen. It keeps the index and line/column; empty/invalid elements are diagnosed. Any error blocks rendering entirely. Total/Valid/Invalid/Unique/Duplicates/OutputCount are shown separately. PreserveOrder means the original Ordinal; IN does not guarantee result order — use a rowset and ORDER BY Ordinal.

Standalone auto chooses VALUES for <=100 elements, otherwise a typed #temp with INSERT batches of <=1000 rows. You can explicitly choose VALUES/temp/OPENJSON or list — a plain list of literals in IN, no more than 1000 values. Parameterized mode always uses a single nvarchar(max) JSON parameter @SchemoraInputs with a separate payload and an ADO.NET example, without interpolating IDs into CommandText. OPENJSON requires compatibility 130+. SQL values are escaped; varchar encodings are checked without replacement characters.

Limits: 32 inputs, 8 Mi characters / 100000 values in total, SQL output 64 MiB. The 64 MiB API request limit accounts for UTF-8/JSON escaping; the UI additionally limits file input. These are local memory limits, not data truncation.

The ScriptDom validator computes dependencies on tables, columns and trusted relations from the SQL. Caller-supplied Dependencies are ignored. Unambiguous aliases, CTEs, derived/correlated subqueries and composite joins are bound to the catalog. A missing object/column is Broken; an ambiguous binding, an unverified join and an unsupported context are NeedsReview. Valid/Warning allow generation; NeedsReview requires explicit confirmation of a manual review; Broken blocks it.

Runtime text, parsing results, SQL with IDs and the JSON parameter payload live only in HTTP/UI memory. They never reach SQLite, localStorage, definition exports or logs. The synthetic check uses only generated test values. An SQL template that the author deliberately saves is part of the recipe definition.

For date and datetime2 fields the interface offers quick values (today, tomorrow, start of month and year, start of last month, 30 days ago) in the local calendar.

Command line: `Schemora.Host.exe render --recipe <export.json> --input Name=values|@file [--mode] [--strategy] [--format sql|json] [--out]` renders an exported recipe with the same engine, without opening the catalog.

API: /api/inputs/parse; /api/workspaces/{id}/recipes; /api/recipes/{id}/{validate,render,versions,lineage,tests,export,clone,rollback}. A recipe PUT requires the current version. Export contains formatVersion1, recipe and databaseReferences. When moving databases, they must be mapped explicitly to the current catalog.

