# Архитектура

Schemora — один локальный ASP.NET Core процесс с embedded React/Vite UI. Публикация win-x64 self-contained single-file включает UI, SQLite native runtime, DacFx, ScriptDom и embedded Roslyn metadata references. Для запуска EXE не нужны Node или установленный .NET runtime.

Модули:
- Host: API, session security, staging/build tooling, jobs, workspace/relationship/recipe/change services.
- SqlServer: DacFx metadata, ScriptDom static DDL classification/reader, schema diff.
- Workspaces: read-only discovery, fingerprints, SQLite catalog/migrations/backup/recovery.
- Relationships: Roslyn EF mapping, embedded C# SQL/Dapper evidence, conservative candidates и trusted path finder.
- RecipeEngine: typed ID parser, DSL, deterministic SQL renderer, SQL dependency binder и conservative AST column repair.

SQLite работает в WAL. Catalog schema v2 добавляет expression indexes; перед migration v1 автоматически создаётся SQLite backup. Snapshot и recipe version immutable. Публикация источников/БД/snapshots атомарна; старые snapshots сохраняются после удаления источника. Один writer-host на data-directory удерживает файловую блокировку; после crash handle освобождается ОС.

Очередь BackgroundService bounded16, один worker, одна активная задача на workspace. Quick использует timestamp/length+hash cache; Full/Rebuild rehash. Full нужен также после изменения содержимого с сохранением timestamp и length у обычных discovered sources. Для авторитетной SQL-project модели verified build closure всегда заново хешируется, включая внешние и ignored inputs. Незавершённый discovery не заменяет рабочий каталог. Публикация старой задачи отклоняется после изменения roots/excludes. После публикации проверяются рецепты; ошибки одной БД сохраняют прошлый snapshot с failed/stale статусом.

DACPAC читается внутри процесса без Docker. SQL project build: detect SDK/legacy → stage source/inherited configuration в приватный служебный каталог → dotnet/MSBuild → fresh DACPAC → DacFx. Bin/obj артефакт автоматически не считается актуальным. Static SQL всегда помечен partial. Сборка требует доверия: произвольные MSBuild targets не являются sandboxed. Планировщик собирает bounded closure локальных sibling projects/imports/ArtifactReference/SQLCMD, проверяет source hashes и сохраняет immutable build manifest. Pinned NuGet DACPAC packages восстанавливаются в private stage/cache и включаются в hashed inputs. Непроверяемый динамический контекст явно требует внешней сборки и DACPAC.

Relationship model отделяет confidence от trust. Physical FK и доказанный EF mapping trusted; SQL/Dapper joins и naming candidates требуют подтверждения с основанием. Отдельные DB identity предотвращают смешивание одноимённых таблиц. C#→БД задаётся явно при нескольких БД. Dapper splitOn не устанавливает FK.

Recipes сохраняют определения и derived lineage; runtime IDs/SQL/payload transient. ScriptDom binder проверяет доступные scopes и диагностирует неоднозначность. После schema change отсутствующий dependency Broken; изменение типа относительно last-compatible snapshot NeedsReview. Repair preview патчит только AST identifier spans в однозначно связанных SELECT/CTE/subquery/UNION contexts; apply — явное подтверждение с новой immutable версией.

API защищён текущим fragment launch token, точным Host/Origin и Fetch Metadata; Kestrel слушает только 127.0.0.1. CSP запрещает внешние scripts/connect, Monaco workers bundled locally. Private runtime launch info позволяет повторному запуску открыть текущую сессию; если ACL пользовательского data-dir не позволяет приватность, token там не сохраняется.

PostgreSQL и AI исключены из V1. Нет выполнения сгенерированных запросов на бизнес-БД и загрузки пользовательских assemblies/source generators. Ограничения анализаторов отражаются diagnostics, а не фиктивной достоверной схемой.


AutoRefreshService отслеживает корни через FileSystemWatcher, debounce и periodic reconciliation; использует общую очередь только Quick/Full. SchemaObjectService предоставляет paged objects/detail/dependencies/transitive impact. ManualRelationService и ChainRecipeBuilder используют ту же relationship-context revision, что сохранение/генерация рецептов. Детали и границы расширений: enhancements.md.


