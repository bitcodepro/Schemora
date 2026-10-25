# Phase 3 — recipes и ID processor

Acceptance:
- Typed int/bigint/GUID/nvarchar/varchar, explicit duplicate/order/encoding policies.
- Strict placeholders, bounded parser с позициями ошибок, invalid blocks render.
- VALUES/#temp/OPENJSON и один JSON parameter для больших списков.
- Immutable versions, clone/import/export/rollback, derived lineage, synthetic tests.
- Local Monaco editor, ID paste/file, statistics/errors/preview/clipboard без runtime persistence.

Реально проверено: core parser/renderer/golden/AST tests; HTTP scripts/Test-Recipes.ps1 —22/22, включая10000 ID (869ms в первом запуске), deterministic repeat, oneJSONparameter, escaping/Unicode, invalid positions, optimistic version conflict, clone/rollback, restart и отсутствие runtime sentinel в файлах SQLite/data-dir. Timing зависит от машины. Дополнительная проверка исполнения SQL в Docker выполняется отдельно scripts/Test-GeneratedSql.ps1.

