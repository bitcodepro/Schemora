# Phase 4 — schema changes и safe repairs

Acceptance:
- Сравнение двух immutable snapshots одной БД, добавления/удаления/изменения и отдельные rename candidates.
- После refresh только зависимый рецепт Broken; изменение типа NeedsReview; сохранён last-compatible snapshot.
- Preview и explicit approval ремонта; новая версия, отказ и rollback.
- Никаких regex/global string замен и автоматического угадывания бизнес-смысла.

Реально проверено: scripts/Test-Changes.ps1 —13/13 HTTP проверок: удалениеOldCode/добавлениеNewCode, renamecandidate, dependentBroken/unrelatedValid, AST preview с сохранением комментария, отсутствие изменения доapproval, новаяusableversion, persistedapproval, rollback/restore, subsequenttypechangedNeedsReview.

Conservative repair поддерживает однозначный SELECT со schema/table binding. CTE, реальные subqueries/set/DML и неоднозначные multipleDB scopes требуют ручной правки. Rename candidates — повод проверить, а не готовая автоматическая замена.

