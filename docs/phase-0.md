# Phase 0 — задачи и приёмка

| ID | Задача | Реально проверено |
|---|---|---|
| P0-01 | Каркас | Solution, React/TS/Vite, Host, SQL-модуль; build без warnings/errors |
| P0-02 | SDK build | Commerce/Billing .sqlproj → DACPAC; Commerce также через API из single EXE |
| P0-03 | Legacy route | Найдены MSBuild 18 + SSDT одной VS18 установки; Legacy.sqlproj → DACPAC → dbo.LegacyItems через API |
| P0-04 | Metadata | DacFx → таблицы/типы/nullable/PK/ordered composite FK → JSON; 7 tests |
| P0-05 | Docker spike | SqlPackage → isolated SQL Server → sys catalog (4 tables/13 columns/4 PK/1 composite FK) → cleanup |
| P0-06 | Packaging | Один self-contained win-x64 EXE, 65 949 569 bytes; проверена копия только EXE без соседних assets/DLL |
| P0-07 | Local API | Token, Host/Origin, cross-site rejection, trust; исходники не изменились; stale DACPAC не выбран; 4 process lifecycle tests |
| P0-08 | Docs | Toolchain/architecture/model/security/recipe boundary, smoke commands, changelog и CI workflow |

Все 11 unit/regression tests passed. npm audit и NuGet audit: известных уязвимостей по текущим источникам не обнаружено. Локальные end-to-end сценарии build → DACPAC → DacFx JSON и Docker live catalog прошли. EXE SHA256: `3B314D714FBC455C5048BE68A39133F1DBF5BE18897168BF49751F4F26F49875`.

Непроверенное: visual browser interaction/layout и автоматическое открытие браузера при двойном клике (нет подключённого browser automation); удалённый CI (нет GitHub remote). CI workflow подготовлен, его успешный удалённый запуск не заявляется.

Legacy fallback при отсутствии совместимого toolchain реализован с кодом SSDT_TOOLCHAIN_MISSING и импортом DACPAC; в текущем окружении оба компонента присутствуют. Отдельно воспроизведено, что dotnet SDK сам по себе не находит SSDT targets для legacy.

Границы spike: наследуемые props/targets с внешними imports и sibling project references дают диагностику/ошибку сборки; такие проекты можно анализировать через готовый DACPAC. Произвольные build targets остаются доверенным исполняемым кодом.

Следующая фаза: workspace discovery, каталог нескольких БД, SQLite snapshots и incremental refresh. Большой интерактивный graph и logical relations — Phase 2; recipes — Phase 3.
