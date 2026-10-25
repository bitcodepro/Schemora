# SQL Schema Studio — техническое задание и план реализации

## 1. Назначение

Создать универсальное **локальное приложение** для анализа SQL-схем, навигации по связям таблиц и безопасного формирования повторяемых SQL-запросов без подключения к рабочим базам данных и без отправки пользовательских ID или данных в LLM.

Первоначальная СУБД — **Microsoft SQL Server**; архитектура должна позволять позднее добавить PostgreSQL отдельным провайдером. Источники — локальные репозитории SQL-проектов, обычные `.sql`, C#-проекты и Entity Framework Core mapping. Одна рабочая папка может содержать много репозиториев, solutions, баз данных и схем.

**Главный пользовательский сценарий:** выбрать корневую папку → обнаружить SQL/C# проекты → собрать достоверную модель нескольких БД → автоматически установить физические и вероятные логические связи → просмотреть интерактивный граф → создать и сохранить параметризованный рецепт → вставить список ID → детерминированно получить корректный SQL → после изменения репозиториев обновить модель и проверить рецепты.

## 2. Базовые принципы и границы

- Один запускаемый файл для пользовательского приложения: локальный ASP.NET Core сервер, API, UI и анализатор работают в **одном процессе**. Docker и внешние SDK/компиляторы допускаются как необязательные внешние инструменты для воспроизведения схем; они не являются постоянно запущенными сервисами приложения.
- Открывать UI в браузере на `localhost`. Не экспонировать сервер в локальную сеть. По умолчанию без регистрации, облака, телеметрии и AI.
- Не подключаться к production/staging и не читать реальные данные бизнес-БД. Допустима изолированная локальная БД SQL Server в Docker исключительно для развёртывания схемы и извлечения метаданных.
- Никаких скрытых изменений репозиториев. Сканирование read-only; служебные данные — в отдельном каталоге приложения.
- При неоднозначности связей инструмент показывает степень уверенности и источник доказательства, **не выдает догадку за установленный FK**.
- Генерация SQL и обработка ID — полностью детерминированные, проверяемые и воспроизводимые операции без LLM.
- MVP ориентирован на Windows x64, с изоляцией платформозависимых процессов ради последующей поддержки других ОС.

## 3. Технологии и компоненты

- **Backend:** .NET (актуальная поддерживаемая LTS), ASP.NET Core Minimal APIs или Controllers, DI, BackgroundService для очереди локальных задач.
- **Frontend:** React + TypeScript + Vite, собранный в статические ресурсы и встроенный в публикуемый backend. Граф — React Flow (`@xyflow/react`) либо аналог, поддерживающий большие схемы и виртуализацию; редактор — Monaco Editor.
- **Local storage:** SQLite для рабочих пространств, каталогов объектов, связей, пользовательских подтверждений, рецептов, снимков схем и результатов проверки. Миграции версии хранилища.
- **SQL model:** собственная нормализованная модель метаданных, независимая от конкретной СУБД; provider abstraction (SQL Server в V1, PostgreSQL позднее).
- **C# analysis:** Roslyn/MSBuildWorkspace, где возможно; fallback-парсинг файлов и явная диагностика, если загрузка solution не удалась. Анализ EF Fluent API и атрибутов, без выполнения пользовательского C# кода по умолчанию.
- **SQL analysis:** Microsoft.SqlServer.DacFx / ScriptDom и/или SQL project build; не рассчитывать, что текстовый regex способен надёжно разобрать весь T-SQL.
- **Schema execution:** Docker CLI и локальный контейнер SQL Server — только если выбран режим развёртывания, с согласия пользователя и со строгой изоляцией.
- **Logs:** структурированные локальные логи без сохранения вставленных ID по умолчанию.

### 3.1 Предлагаемая структура solution

```text
src/
  SqlSchemaStudio.Host/            # ASP.NET Core, SPA hosting, API, startup
  SqlSchemaStudio.Application/     # use cases, validation, jobs
  SqlSchemaStudio.Domain/          # entities, schema graph, relations, recipes
  SqlSchemaStudio.Infrastructure/  # SQLite, filesystem, process runner, cache
  SqlSchemaStudio.Discovery/       # repositories, sqlproj, sln, csproj, artifacts
  SqlSchemaStudio.SqlServer/       # DacFx, ScriptDom, SQL Server metadata provider
  SqlSchemaStudio.EfCore/          # Roslyn analysis, EF relationship evidence
  SqlSchemaStudio.RecipeEngine/    # templates, typed inputs, renderer, validator
  SqlSchemaStudio.Contracts/       # versioned DTOs
ui/                                 # React/Vite/TypeScript
 tests/
  Unit/
  Integration/
  E2E/
```

Разделение по проектам ориентировочное: не создавать пустые абстракции ради слоёв. Внутри один host-процесс и один exe; модули — библиотеки, а не микросервисы.

## 4. Запуск и установка

1. `dotnet publish` создаёт self-contained single-file EXE для Windows x64, со статическими UI assets внутри результата публикации; проверить фактическую структуру и поведение publish на CI.
2. Двойной клик по exe запускает backend на `127.0.0.1` со свободным локальным портом и открывает браузер. Порт и токен запуска не должны допускать незапрошенный доступ от посторонних сайтов: предусмотреть защиту local API (проверка Origin/CSRF и per-session token).
3. Хранить SQLite, настройки, кэш и логи в отдельном каталоге профиля (`%LOCALAPPDATA%/...`), а не рядом с exe или в репозиториях.
4. Страница диагностики проверяет доступность Docker, SQL tooling, .NET SDK/MSBuild и объясняет, какая функция недоступна и как её включить.
5. Закрытие приложения корректно останавливает фоновые процессы и, по настройке, удаляет созданные временные контейнеры. Не останавливать чужие контейнеры.

## 5. Обнаружение рабочей области

Пользователь указывает одну или несколько корневых папок (workspace roots). Рекурсивно обнаружить:

- Git repositories (в том числе вложенные);
- `.sln`, `.slnx`, `.csproj`, `.sqlproj`, `.dacpac`, `.sql`, файлы конфигурации ORM;
- SQL definitions (`CREATE TABLE`, `ALTER TABLE`, PK, FK, индексы, views, synonyms — по мере поддержки);
- EF Core `DbContext`, `DbSet`, `[Table]`, `[Column]`, `[ForeignKey]`, Fluent API (`ToTable`, `HasOne`, `HasMany`, `WithOne`, `WithMany`, `HasForeignKey`, `HasPrincipalKey`) и конфигурации `IEntityTypeConfiguration<T>`;
- привязки проектов к базам по project metadata, конфигурациям, connection-string *именам* и EF mappings. Не сохранять пароли/секреты из конфигов.

Исключать `.git`, `bin`, `obj`, `node_modules`, `.vs`, `dist`, временные каталоги, символические циклы; поддерживать `.gitignore` и пользовательские exclude patterns. Не индексировать все SQL-скрипты как DDL: различать schema DDL, миграции, произвольные запросы и seed/data scripts.

Каждый источник имеет identity, path, hash, timestamp, статус парсинга и diagnostics. Поддержать несколько БД с одинаковыми именами таблиц; полные имена объектов включают workspace/database/schema/name и stable internal ID. Сопоставление неизвестных project→database — предложение пользователю с сохранением решения.

## 6. Построение схемы и DACPAC

**DACPAC можно собирать автоматически; отдельное Visual Studio расширение само по себе не обязательно.** Но необходим совместимый инструмент сборки/SDK (в зависимости от типа SQL проекта). Приложение обязано обнаружить тип `.sqlproj` и показать готовность toolchain, а не обещать собрать любой проект без зависимостей.

Приоритет источников истины:

1. **DACPAC из SQL-проекта:** обнаружить поддерживаемый SDK-style SQL project (например, Microsoft.Build.Sql) или legacy SSDT SQL project; выбрать совместимую команду сборки через `dotnet build`/MSBuild и установленные target/tooling; по возможности переиспользовать готовый артефакт лишь если он актуален.
2. **Разбор DACPAC/DacFx model** без обязательного Docker, если достаточно данных для graph/schema extraction.
3. **Изолированное развёртывание** в локальный SQL Server Docker, если проект или режим анализа требуют живого SQL catalog: собрать DACPAC → `SqlPackage /Action:Publish` в локальный контейнер → читать `sys.schemas`, `sys.tables`, `sys.columns`, `sys.key_constraints`, `sys.foreign_keys`, `sys.foreign_key_columns`, `sys.indexes`, `sys.index_columns` и связанные метаданные. Не запускать произвольные post-deployment scripts без отдельного явного разрешения/проверки, поскольку они могут менять данные, обращаться наружу или зависеть от окружения.
4. **Fallback:** статический T-SQL анализ для источников без собираемого `.sqlproj`; явно сообщать частичность/неполноту результата.

Развёртывание не является обязательным на каждом refresh: выбирать самый быстрый воспроизводимый маршрут, сохранять fingerprints и диагностировать несовместимости SQL Server edition/version. Не смешивать найденные метаданные разных баз. Контейнеры/тома должны иметь уникальные префиксы этого приложения, изолированные порты и учётные данные для временной среды; никакого случайного destructive action на чужих контейнерах.

### 6.1 Режимы анализа

- **Quick Refresh:** скан изменённых файлов по fingerprint, инкрементальный разбор и проверка затронутых объектов/рецептов.
- **Rebuild Schema:** пересборка выбранной БД/DACPAC, полное обновление её снимка и пересчёт зависимостей.
- **Full Rescan:** повторное обнаружение корневых каталогов, всех проектов и БД, удаление устаревших связей из производного графа с сохранением пользовательских решений и истории.

Показывать этапы, ошибки, предупреждения, длительность, отмену, прогресс и возможность повторного запуска. Операции должны быть идемпотентными.

## 7. Граф связей и confidence

Узлы: workspace, repository, solution/project, database, schema, table/view, columns (детали), recipe. Рёбра:

- **Declared FK**: FK из SQL schema/catalog — confirmed.
- **ORM mapping**: EF-конфигурация с явно установленной парой таблица/колонка — mapped, источник и line/definition.
- **Observed joins**: соединения из проанализированных SQL, когда они синтаксически однозначны; evidence, а не автоматическое объявление FK.
- **Inferred candidate**: по шаблонам имён, типам данных, уникальным ключам, контексту сущностей и дополнительным признакам; confidence score и причины. Нельзя автоматически использовать непроверенные связи при сборке production SQL.
- **User-confirmed logical relation**: логическая связь, подтверждённая пользователем, с основанием и timestamp; хранится отдельно от generated metadata.

Для SQL Server `nvarchar`/`varchar` ID, которые выступают как логические ключи, нужно распознавать кандидатов, даже если реального FK нет. Если несколько правдоподобных путей — показать варианты и запросить выбор. В графе выделять тип и направление ребра; построение пути между таблицами по кратчайшему/релевантному маршруту с объяснением joins. Не считать одноимённые поля доказательством связи.

**Приоритет evidence:** физический FK > точный ORM mapping > вручную подтверждённая relation > наблюдаемый SQL join > эвристика; пользовательское подтверждение может конфликтовать с физикой — конфликт показывать, а не замалчивать.

## 8. UI/UX

Главные области:

1. **Workspace Dashboard:** папки, репозитории, БД, health, ошибки сканирования, действия Quick Refresh / Rebuild / Full Rescan.
2. **Schema Explorer:** дерево Database → Schema → Tables/Views, поиск по названию и полям, фильтры, карточка объекта: столбцы/типы/nullability/PK/FK/indexes, источник, usage.
3. **Relationship Graph:** интерактивный canvas с pan/zoom, автолэйаутом, ручным размещением, фокусом на выбранной таблице и соседях на N шагов, фильтрами по БД/схеме/типу/уверенности, отдельным режимом межбазовых логических связей. Граф больших систем должен подгружать окрестности/уровни, а не рендерить всю БД одновременно.
4. **Path/Join Explorer:** выбрать исходную и целевую таблицы, получить маршруты, объяснение и предварительный JOIN SQL; эвристические связи не активировать без подтверждения.
5. **Recipes:** каталог, теги, поиск, версионирование, редактор SQL/настроек и просмотр зависимостей, ввод параметров и preview результата.
6. **Schema Changes:** diff между snapshots (added/removed/renamed candidate/changed columns/FK/type), impacted recipes и remediation status.
7. **Settings/Diagnostics:** корни, exclude, build tools, Docker, безопасность, путь данных, экспорт/импорт конфигурации.

UI: локальный desktop-like web интерфейс с тёмной и светлой темами, горячими клавишами, сохранением положения графа, понятными empty/error states.

## 9. Recipes и генерация SQL

Рецепт — не коллекция разрозненных `.sql` на каждый WHERE, а типизированное описание операции:

- recipe ID, имя, описание, теги, target dialect/provider, database references, version;
- SQL template (предпочтительно читаемый и проверяемый), typed inputs, default policies и output mode;
- набор зависимостей на таблицы/колонки/relations, вычисленных из SQL/metadata, а не только вписанных вручную;
- test fixtures (без реальных пользовательских данных), ожидаемые свойства результата;
- статус validation и last-compatible schema snapshot.

Примеры: «Invoices by Transaction IDs», «Documents for Invoices», «Transactions with related invoices».

### 9.1 Обработка списков ID

- Принимать вставку из CSV, TSV, строк, разделённых пробелами/переносами/запятыми/точкой с запятой, SQL-списков и CSV-файлов; не отправлять данные в модель или сеть.
- Typed inputs: `int`, `bigint`, `uniqueidentifier`, `nvarchar`, `varchar` (с указанной длиной/кодировкой и правилами валидации).
- Показывать `total`, `valid`, `invalid`, `unique`, `duplicates`; отчёт по ошибкам с номером элемента/строки и возможностью исправить.
- Сохранять порядок, если это существенно; удаление дубликатов — явная настройка, без молчаливой потери значений. Не нормализовать GUID/string значений без заданной семантики.
- Генерировать SQL с корректным экранированием, без SQL injection. Два режима: **copyable standalone script** и **parameterized execution example**. По умолчанию ничего не исполнять.
- Для больших списков не полагаться только на `IN (...)`: поддерживать `VALUES`/`#temp` c batch INSERT, `OPENJSON` / `STRING_SPLIT` там, где типы и версия SQL Server позволяют; для runtime ADO.NET — TVP при наличии совместимого table type. Учитывать лимит SQL Server 2100 параметров и размер скрипта.
- Ясно показывать точное количество значений, результат валидации, используемый SQL dialect/strategy и предупреждать о потенциально тяжёлом плане.
- Не сохранять введённые ID и исходный сгенерированный SQL с ними без явного согласия; не логировать содержимое clipboard.

### 9.2 Редактор рецептов

SQL Monaco Editor с подсветкой T-SQL, шаблонизатором строго ограниченного набора выражений, preview, lineage inspector, тестовыми наборами и безопасным копированием в clipboard. Поддержать ручное создание, копирование и импорт `.sql` в рецепт, а также wizard по выбору таблиц/связей. Детерминированность и читабельность SQL важнее сложного визуального query builder на первом этапе.

## 10. Обновление схемы и impact analysis

На refresh/rebuild:

1. Получить новый immutable schema snapshot и diff с прошлым.
2. Пересчитать зависимости на уровне DB/schema/object/column/relation.
3. Проверить каждую затронутую recipe: существование объектов, типов, joins, параметров, SQL parsing/model binding, совместимость рендерера.
4. Отнести рецепт к `Valid`, `Warning`, `Broken`, `Auto-fix proposed`, `Needs review`. Приводить точную причину: например, `dbo.Transactions.InvoiceId removed`.
5. Автофиксировать только **однозначные безопасные** преобразования (например, обновление пути явно переименованного объекта, если имеются достаточные доказательства), создавать версионированное предложение и diff, требовать подтверждения перед заменой. **Никогда молча не менять смысл SQL.**
6. Позволить откатить правку и сравнить версии. Если нет уверенности — пометка broken с подсказкой для ручного исправления.

Для rename, выводимого эвристически, использовать `possible rename`, не утверждать факт без авторитетной метаинформации.

## 11. Модель хранения

Минимальные агрегаты SQLite: `Workspaces`, `Roots`, `Repositories`, `Projects`, `Databases`, `SchemaSnapshots`, `SchemaObjects`, `Columns`, `Relations`, `RelationEvidence`, `RelationOverrides`, `Recipes`, `RecipeVersions`, `RecipeInputs`, `RecipeDependencies`, `ValidationRuns`, `ScanRuns`, `ToolchainCapabilities`, `UserSettings`. Большие производные индексы и cache должны быть пересоздаваемыми. Поддержать экспорт/импорт workspace manifest и recipes в человекочитаемый формат для Git, без локальных путей/секретов по умолчанию.

## 12. API и фоновые задачи

Примеры endpoint groups: `/api/workspaces`, `/api/discovery`, `/api/databases`, `/api/schema`, `/api/graph`, `/api/paths`, `/api/relations`, `/api/recipes`, `/api/validation`, `/api/jobs`, `/api/diagnostics`.

Долгие задачи выполнять через ограниченную job queue с cancellation, retries только когда корректно, progress events через SignalR/SSE, журналом результатов. Исключить конкурентные rebuild одного объекта; транзакционно публиковать новый snapshot после успешной сборки. Режим «частичный успех» должен быть явно виден.

## 13. Требования к надёжности и безопасности

- Никакого выполнения произвольных команд, извлечённых из репозитория, без доверенного режима или явного предупреждения. `dotnet build`, SQL post-deployment и сторонние build targets потенциально запускают код — обозначить trust boundary, позволить отказаться.
- Подразумевается read-only анализ. Docker publish выполняется только в созданную приложением изолированную локальную базу; никакого произвольного connection string для publish в V1.
- Противодействие path traversal, symlink loops, SQL injection, shell injection; CLI запускать с аргументами через ProcessStartInfo.ArgumentList, с timeouts и ограничением ресурсов.
- Не хранить секреты из connection strings, ключи, пароли, реальные ID; секреты контейнера генерировать локально и надёжно хранить/удалять.
- Разумная отзывчивость на больших репозиториях, отмена действий, low-memory mode; incremental scan с хешами и bounded parallelism.
- Стабильные идентификаторы объектов, сохранение пользовательских подтверждений после rebuild; stale relations перепроверять и помечать конфликтами.
- Порт, CORS/Origin, CSRF и local-only binding проверять тестами.

## 14. Проверки и тестирование

- Unit: входные ID (включая большие списки, ошибочные строки, дубликаты, кавычки, Unicode, GUID), SQL escaping, deterministic rendering, schema diff, relation scoring, dependency graph.
- Integration: mini SQL projects; DacFx metadata; SQL Server Docker с несколькими базами и схемами; EF relationship mappings с nullable/composite keys и fluent mappings; отсутствие FK при наличии текстовых ID.
- Snapshot/golden tests: expected T-SQL для разных input types и strategies.
- Negative/security tests: malformed SQL, hostile file paths, invalid/missing tooling, cancellation, untrusted scripts.
- End-to-end UI: root picker → scan → graph → recipe → ID paste → SQL copy → schema edit → rebuild → broken recipe diagnostics → proposed safe fix.
- CI: .NET build/tests, `npm ci`, frontend typecheck/build, frontend E2E smoke при возможности, Windows self-contained single-file publish и старт exe.

## 15. Поэтапная реализация (агент должен соблюдать порядок)

### Phase 0 — исследование toolchain / технический spike

- Создать репозиторий, solution и frontend shell.
- Проверить сборку тестового SDK-style `.sqlproj` в DACPAC без Visual Studio extension; проверить legacy SQL project и корректные diagnostics при отсутствии SSDT targets.
- Извлечь модель через DacFx; проверить альтернативу с Docker SQL Server и SqlPackage.
- Протестировать single-file publish, встраивание UI assets, открытие браузера и локальную безопасность.
- Зафиксировать матрицу реально поддерживаемых версий SDK/tooling в README.

**Готовность:** воспроизводимый smoke demo: build → DACPAC → metadata → JSON graph; запускаемый EXE.

### Phase 1 — workspace discovery и каталог схем

Сканирование папок и репозиториев; обнаружение проектов и нескольких БД; SQL Server provider; snapshots; дерево объектов; ошибки/diagnostics; incremental refresh.

**Готовность:** папка с 2 SQL проектами и C# solution правильно разбивается на отдельные БД и таблицы, обновления отслеживаются.

### Phase 2 — связи и визуальный граф

Physical FK, Roslyn/EF mapping, rule-based logical candidate discovery, evidence/confidence, manual confirmation, graph UI, path finding и preview joins.

**Готовность:** таблица без FK получает объяснимые кандидаты; пользователь подтверждает; граф показывает источник и путь; без подтверждения неподтверждённый join не считается достоверным.

### Phase 3 — рецепты и ID processor

Recipe DSL/SQL templates, typed parameters, deterministic parser, validation, generator for small/large IDs, Monaco UI, clipboard, recipe catalog.

**Готовность:** 10 000 идентификаторов обрабатываются без пропусков с отчётом и корректным SQL; рецепт не требует нового файла под каждый WHERE.

### Phase 4 — diff, impact analysis и safe auto-fix

Schema snapshot compare, dependency inference, recipe validation and status, repair proposals, preview+approval and rollback.

**Готовность:** удаление/переименование поля приводит к точному предупреждению для зависимых рецептов, не ломая остальные.

### Phase 5 — release hardening

Diagnostics, recovery, security tests, docs, packaging, fast startup, larger repos performance, CI release flow.

**Готовность:** приложение можно передать другому разработчику как один EXE с ясным списком внешних инструментов, необходимых для отдельных функций.

### Future (не включать в V1)

PostgreSQL provider (schema model, catalog adapter, SQL dialect, build/import pipeline), LLM/MCP integration только опционально, расширенный query builder, полноценная автоматическая генерация запросов из естественного языка, непосредственное выполнение запросов на рабочих БД.

## 16. Формат исполнения заданий coding-агентом

1. На каждую фазу создать маленькие реализуемые issues/tasks с acceptance criteria; начать с Phase 0, не пытаться писать всё сразу.
2. До каждого существенного архитектурного решения сформулировать ограничения и показать trade-off. Выбирать практичный minimal solution, избегать ненужных сервисов и библиотек.
3. Для каждой фичи: код + unit/integration tests + документация + reproducible commands.
4. Ни один этап не объявлять готовым без запуска проверок и демонстрации end-to-end scenario. В отчёте перечислять что реально проверено, что не проверено и почему.
5. Поддерживать `docs/architecture.md`, `docs/schema-model.md`, `docs/recipe-format.md`, `docs/toolchain.md`, `docs/security.md` и короткий changelog.
6. Если выясняется, что автоматическая сборка конкретного вида SQL проекта невозможна в текущем окружении, не подменять функциональность фиктивными данными; показать diagnostic и альтернативу import existing DACPAC/static parse.

## 17. Критерии приёмки всего проекта

- Пользователь запускает **один exe** и получает работающий UI на localhost; отдельный frontend/backend запуск не нужен.
- Может выбрать корневую папку с несколькими SQL/C# репозиториями, увидеть обнаруженные разные БД, не смешивая схемы.
- Сборка DACPAC выполняется автоматически при наличии совместимых инструментов; если инструменты отсутствуют, это диагностируется, а не скрывается.
- При необходимости приложение поднимает изолированную базу SQL Server Docker для разбора схемы, без подключения к удалённой рабочей БД.
- Показывает граф FK, EF mappings и предполагаемых логических связей с evidence и confidence.
- Позволяет сохранить SQL-рецепт, вставить большой список ID и получить точный проверяемый SQL без LLM и потери элементов.
- После обновления схемы различает valid/broken/needs-review recipes, предлагает только безопасные исправления с diff и подтверждением.
- Quick Refresh корректно обрабатывает небольшие изменения без полного сканирования всего workspace.
- При ошибках сборки, недоступном Docker или частично разбираемом проекте пользователь получает ясную диагностику и рабочие fallback-функции.

---

**Примечание по DACPAC:** DACPAC — это выходной артефакт сборки SQL database project, а не функция EF Core. Для SDK-style `.sqlproj` сборка может выполняться через совместимый `dotnet build` с SQL build SDK. Старые SSDT `.sqlproj` могут требовать установленного MSBuild/SSDT tooling; одно лишь наличие .NET runtime не гарантирует сборку. `SqlPackage` необходим для publish/extract сценариев, если выбран такой путь, и может поставляться как отдельный обнаруживаемый инструмент. Важнейшая задача Phase 0 — подтвердить выбранные варианты на настоящих образцах проектов.
