# Расширения после V1

PostgreSQL и AI отложены по просьбе пользователя. Пользовательских пилотных репозиториев нет: проверки используют созданные SQL/C# проекты и временные репозитории. Они проверяют перечисленные сценарии, но не представляют чужую реальную кодовую базу.

## Сборка SQL-проектов

Планировщик читает XML и локальные inputs без выполнения MSBuild. Поддерживает SDK-style Microsoft.Build.Sql и legacy SSDT, sibling SQL ProjectReference, локальные ArtifactReference, обычные imports/props/targets, известные inherited configuration, SQLCMD include/variables. Служебная копия сохраняет структуру и перенаправляет output/intermediate каждого проекта в короткие отдельные каталоги служебной копии. При слишком длинном custom data-dir служебная копия переносится в приватную временную папку Windows; manifest остаётся в выбранном data-dir. Путь, который всё равно превышает ограничение запуска tooling, явно диагностируется. Source closure проверяется по содержимому до и после сборки; появление, удаление или изменение зависимого файла делает старую модель непригодной для повторного использования.

Поддержаны закреплённые NuGet-пакеты с DACPAC: restore использует отдельный cache, проверяются состав пакета, identity/version и SHA512. Central package versions и локальные feeds входят в анализ; floating versions, package code/tasks и неоднозначный контекст явно диагностируются.

Immutable build manifest хранит пути, типы inputs, размеры и SHA256, configuration, список проектов и hashed environment inputs. Значения SQLCMD/environment в manifest не сохраняются. Snapshot содержит BuildManifestId. API: POST /api/schema/build/plan, GET /api/build-manifests/{id}, GET /api/databases/{id}/build-manifest. Это локальный технический manifest с абсолютными путями; он отличается от portable workspace export.

Property functions, динамические imports, неизвестные custom tasks и смешанные SDK/legacy dependencies не объявляются полным переносимым контекстом. Возвращается BUILD_INPUTS_INCOMPLETE с конкретными diagnostics; остаётся внешний build и импорт DACPAC. Сборка требует явного доверия: MSBuild выполняется с правами пользователя.

## Ручные связи и рецепты по маршруту

Редактор создаёт логическую связь между таблицами с упорядоченными парами колонок, названием и основанием. Изменение/удаление проверяет ожидаемую версию. Сохраняется точная форма колонок; изменение типа/nullability или исчезновение endpoint делает утверждение stale. Совместимость типов не доказывает уникальность бизнес-ключа. Физические FK сохраняют приоритет, а конфликтующая ручная связь требует отдельной проверки в графе.

Конструктор рецепта принимает исходную колонку ID, выбранный доверенный маршрут и явную проекцию колонок промежуточных/конечной таблиц. Псевдонимы результата и порядок полей редактируются. Сервер заново проверяет непрерывность, доверие и составные JOIN conditions, создаёт typed input и проверяет SQL binder. Preview не сохраняет рецепт; затем пользователь открывает черновик и сохраняет обычную версию. Поддержаны int, bigint, uniqueidentifier, nvarchar(n), varchar(n); для varchar действует существующая явная политика кодировки.

## Объекты БД

SchemaGraph дополнен Objects, Indexes и ObjectDependencies; SchemaTable — UniqueKeys. Старые snapshot JSON совместимы, отсутствующие списки считаются пустыми. Версия анализа снимка гарантирует однократный пересчёт старого каталога при Quick/Full Refresh, даже если исходные файлы не менялись. Таблицы остаются таблицами: view, procedure, scalar/table function и synonym представлены отдельными объектами.

Доступны колонки/параметры, SQL definition, synonym target, unique constraints, index keys/direction/include/filter и dependency evidence. Определение ограничено 256 КиБ UTF-8 на объект и 32 МиБ суммарно; превышение явно диагностируется. Static analysis остаётся partial: вычисленные типы, result sets процедур, dynamic SQL и неразрешённые внешние ссылки не выдумываются. Неоднозначная зависимость сохраняет TargetName и пустой ToObjectId.

Explorer использует серверную пагинацию; SQL definition загружается при открытии объекта. Detail показывает прямые зависимости, вызывающие объекты и транзитивное влияние на объекты/рецепты. Сравнение снимков учитывает definitions, параметры, unique keys, индексы и зависимости. Метаданные routines доступны для изучения; конструктор JOIN использует таблицы текущего relationship graph. Рецепты SELECT могут читать view с проверкой колонок; partial view требует review, изменение definition или замена view/table инвалидирует совместимость.

## Исправления SQL

Расширенный AST rewriter поддерживает доказуемые привязки колонок в SELECT с CTE, derived/scalar subqueries и UNION. Неоднозначные aliases/lineage, вычисленные выражения и неподдерживаемые контексты требуют ручной правки. Preview сохраняет форматирование и меняет только identifier spans; подтверждение по-прежнему создаёт новую версию с guards схемы, связей, рецепта и решения proposal.

## Автообновление и проверка выпуска

Auto-refresh по умолчанию выключен. FileSystemWatcher объединяет события с задержкой 2–60 секунд; периодическая сверка 1–60 минут покрывает пропущенные события и внешние inputs. После запуска проверяются изменения за время отсутствия приложения. Переполнение/ошибка watcher планирует Full Rescan и переподключение. Встроенные output/cache folders и собственный data-dir исключены из событий.

Автообновление использует общую ограниченную очередь и проверяет зависимые рецепты. Оно запускает Quick/Full без доверия и никогда не вызывает Rebuild. Изменившийся SQL-проект показывается как partial до явной сборки.

scripts/Test-Enhancements.ps1 проверяет ручные composite relations, версии, маршрут через три таблицы, объекты/impact, реальные файловые изменения, recipe invalidation, stale relation и restart. scripts/Test-Clean.ps1 создаёт копию исходников без bin/obj/node_modules/work/artifacts, сохраняет SHA256 inventory, устанавливает UI dependencies, собирает/test/publish и запускает все acceptance против созданного EXE. -Docker добавляет SQL Server execution и app schema extraction/recovery. Проверка выполняется на текущем Windows host; GitHub workflow запускается отдельно при наличии remote.




## Языки и порт запуска (1.2)

Default locale — en; поддержаны ru и uk. Выбор хранится в UserSettings language и browser cache schemora.language, без runtime ID. Поддержанные значения проверяются сервером. Изменение языка обновляет подписанные компоненты без remount; загрузка старой настройки и последовательные записи не перезаписывают более новый выбор. Даты/числа следуют выбранной locale. Собственные сообщения переводятся по полным шаблонам с сохранением параметров; пользовательские names/descriptions/SQL/reasons сохраняются. Неизвестные сообщения внешнего tooling остаются исходными.

schemora.json читается рядом с EXE, независимо от рабочего каталога: {"port":0}. Файл необязателен; explicit --config обязан существовать. Разрешено ровно одно integer поле port (0..65535), до16KiB. --port имеет приоритет. Настройка меняет только loopback port. Фиксированный занятый порт даёт LAUNCH_PORT_IN_USE и путь к JSON, без тихого fallback. Повторная сборка/публикация сохраняет существующий sidecar независимо от timestamps.

Test-Localization проверяет все три языка, сохранение после restart/fresh browser, rapid changes, live graph labels, unsaved recipe, runtime ID/error text и generated SQL. Test-Launch проверяет JSON/CLI precedence, отсутствие/некорректность файла, занятые порты и сохранение origin/token guards.
