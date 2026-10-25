# Рецепты и runtime inputs

Рецепт сохраняет Id, Name, Description, Tags, Dialect=sqlserver, DatabaseIds, Version, SqlTemplate, Inputs, CompatibilityLevel и вычисленные Dependencies. HTTP JSON использует camelCase. История RecipeVersions неизменяема: редактирование, импорт, подтверждённый repair и rollback создают новую версию. Откат не удаляет последующие версии.

Пример шаблона:

~~~sql
SELECT d.DocumentId
FROM dbo.Documents AS d
WHERE d.DocumentId IN ({{ids:Ids}})
  AND d.CreatedAt >= {{value:From}};
~~~

Inputs: [{ "name": "Ids", "type": "int", "deduplicate": false, "preserveOrder": true, "strategy": "auto" }, { "name": "From", "type": "date", "deduplicate": false, "preserveOrder": true, "strategy": "auto" }].

Поддерживаются int, bigint, uniqueidentifier, nvarchar, varchar, date, datetime2, bit и decimal. decimal требует precision 1..38 и scale 0..precision; значение — цифры с точкой, в пределах точности и масштаба, без экспоненты; в SQL попадает точный числовой литерал, например `CONVERT(decimal(10,2), 1500.5)`. date принимает только yyyy-MM-dd, datetime2 — yyyy-MM-ddTHH:mm[:ss[.fffffff]] (значение с пробелом вместо T нужно взять в кавычки), bit — 1, 0, true или false. Текст требует maxLength (1..4000 / 8000 либо -1 для MAX). Для varchar по умолчанию разрешён ASCII; non-ASCII требует явно указанной кодировки windows-1251, windows-1252 или utf-8 и соответствующей поддерживаемой Collation. UTF8 collation требует SQL Server 2019 / compatibility150+. Предпочитайте nvarchar для Unicode.

Вход-значение можно пометить `optional: true` (только для {{value:...}}; для списка — `INPUT_OPTIONAL_VALUE_ONLY`). Пустое или отсутствующее значение подставляется как `CAST(NULL AS тип)`, в parameterized — пустой массив JSON, который скалярный подзапрос превращает в NULL; генератор добавляет предупреждение. Условие пропускается так: `({{value:From}} IS NULL OR o.CreatedAt >= {{value:From}})`. Неверное значение по-прежнему ошибка: необязательность разрешает только пустое поле.

DSL поддерживает только {{ids:Name}} (SELECT Value для IN), {{table:Name}} (rowset Ordinal,Value) и {{value:Name}} (одно значение). Вход, использованный как {{value:...}}, принимает ровно одно значение (`VALUE_INPUT_SINGLE`) и не может одновременно быть списком (`PLACEHOLDER_KIND_MIXED`). В standalone он подставляется типизированным литералом, например `CONVERT(date, N'2026-10-01', 23)`; в parameterized читается из того же JSON-параметра. Даты всегда конвертируются с явным стилем (23 для date, 126 для datetime2), поэтому не зависят от языка сессии и SET DATEFORMAT. Сравнение date/datetime2 с другим типом даты в схеме — предупреждение о преобразовании, с недатовым типом — ошибка. Выражения, выполнение кода и placeholders внутри строк/комментариев/quoted identifiers запрещены. Имена inputs — ASCII, case-sensitive; SQL-конфликт имён без учёта регистра запрещён. @SchemoraInputs и #schemora_* зарезервированы генератором.

Парсер читает разделители comma/semicolon/tab/newline и quoted значения с doubled quote. Не преобразует строковые ID в числа, не меняет регистр GUID и не удаляет дубликаты без выбранной политики. Сохраняет индекс и line/column; пустые/ошибочные элементы диагностируются. Любая ошибка блокирует render целиком. Total/Valid/Invalid/Unique/Duplicates/OutputCount показываются отдельно. PreserveOrder означает исходный Ordinal; IN не гарантирует порядок результатов — используйте rowset и ORDER BY Ordinal.

Standalone auto выбирает VALUES для <=100 элементов, иначе typed #temp с INSERT партиями <=1000 строк. Можно явно выбрать VALUES/temp/OPENJSON или list — простой список литералов в IN, не больше 1000 значений. Parameterized всегда использует один nvarchar(max) JSON-параметр @SchemoraInputs с отдельным payload и примером ADO.NET, без интерполяции ID в CommandText. OPENJSON требует compatibility130+. Значения SQL экранируются; кодировки varchar проверяются без replacement characters.

Лимиты: 32 inputs, суммарно 8 Mi characters / 100000 значений, SQL output64 MiB. API request64 MiB учитывает UTF-8/JSON escaping; UI дополнительно ограничивает файловый ввод. Это ограничения локальной памяти, а не усечение данных.

ScriptDom validator вычисляет зависимости на таблицы, колонки и trusted relations из SQL. Caller-supplied Dependencies игнорируются. Однозначные aliases, CTE, derived/correlated subqueries и составные joins связываются с каталогом. Missing object/column — Broken; неоднозначный binding, непроверенный join и неподдерживаемый контекст — NeedsReview. Valid/Warning допускают генерацию; NeedsReview требует явного подтверждения ручной проверки; Broken блокирует её.

Runtime text, результаты parsing, SQL с ID и JSON parameter payload живут только в памяти HTTP/UI. Они не попадают в SQLite, localStorage, exports определения или логи. Синтетическая проверка использует только генерируемые тестовые значения. SQL-шаблон, который автор сознательно сохраняет, является частью определения рецепта.

Для полей date и datetime2 интерфейс предлагает быстрые значения (сегодня, завтра, начало месяца и года, начало прошлого месяца, 30 дней назад) в локальном календаре.

Командная строка: `Schemora.Host.exe render --recipe <export.json> --input Имя=значения|@файл [--mode] [--strategy] [--format sql|json] [--out]` рендерит экспортированный рецепт тем же движком, не открывая каталог.

API: /api/inputs/parse; /api/workspaces/{id}/recipes; /api/recipes/{id}/{validate,render,versions,lineage,tests,export,clone,rollback}. PUT рецепта требует актуальный version. Export содержит formatVersion1, recipe и databaseReferences. При переносе БД необходимо явно сопоставить с текущим каталогом.

