# Phase 3 — recipes and ID processor

Acceptance:
- Typed int/bigint/GUID/nvarchar/varchar, explicit duplicate/order/encoding policies.
- Strict placeholders, bounded parser with error positions, invalid blocks render.
- VALUES/#temp/OPENJSON and a single JSON parameter for large lists.
- Immutable versions, clone/import/export/rollback, derived lineage, synthetic tests.
- Local Monaco editor, ID paste/file, statistics/errors/preview/clipboard without runtime persistence.

Actually verified: core parser/renderer/golden/AST tests; HTTP scripts/Test-Recipes.ps1 — 22/22, including 10000 IDs (869 ms on the first run), deterministic repeat, one JSON parameter, escaping/Unicode, invalid positions, optimistic version conflict, clone/rollback, restart and no runtime sentinel in the SQLite/data-dir files. Timing depends on the machine. An additional SQL execution check in Docker is run separately by scripts/Test-GeneratedSql.ps1.

