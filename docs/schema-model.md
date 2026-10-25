# Модель схемы и связей

SchemaGraph: DatabaseName, SourcePath, ExtractedAt, Tables, Relations, Diagnostics, Objects, Indexes, ObjectDependencies. SchemaTable: Id, Schema, Name, Columns, PrimaryKey, UniqueKeys. SchemaColumn: Name, DataType (length/precision/scale), IsNullable. SchemaRelation: Id, Name, FromTableId, ToTableId, FromColumns, ToColumns, Kind, Confidence. Порядок From/ToColumns сохраняет соответствие составного ключа.

Catalog присваивает БД устойчивую identity по workspace+source path; ID таблицы включает database identity и hash(schema+name). Одинаковые имена в нескольких схемах/БД не объединяются. Текущее SQL database name можно задать alias с основанием. Межбазовый JOIN блокируется при двух разных database identities с одинаковым SQL name.

DiscoveredSource хранит identity/path/kind/hash/lastWrite/length/diagnostics. SQL разделяется на schemaSql/migrationSql/querySql/dataSql/unknownSql. Configs индексируются метаданными, их содержимое/connection-string secrets не читаются. Nested repositories и проекты обнаруживаются отдельно; ссылки/циклы и служебные каталоги пропускаются. Поддерживается рабочее подмножество .gitignore; Git index/global excludes не используются.

SnapshotRecord неизменяем, содержит fingerprint, mode и полный scoped graph. DatabaseInfo хранит currentSnapshotId/status. Ошибка обновления оставляет последний успешный graph, но его relations помечаются stale и исключаются из trusted paths. История остаётся доступной при исчезновении исходной БД.

RelationshipGraph содержит таблицы с DatabaseId/DatabaseName и edges с Status, IsTrusted, Evidence и ReviewReason. Evidence: Kind, SourcePath, Line, Detail. Kind: declared-fk, orm-mapping, user-confirmed, observed-join, inferred-candidate, embedded-sql-candidate. Пользовательские confirm/reject хранятся отдельно от производной модели. Исчезнувшая или изменившаяся связь становится stale, конфликт с физической схемой видим.

EF binding выполняется Roslyn semantic model с embedded metadata references, без выполнения C#. При shadow/dynamic/helper mapping, unresolved official symbols или неизвестной регистрации выводится диагностированный partial mapping. Dapper извлекает только literal/const SQL в Query*/Execute*/inline CommandDefinition; runtime interpolation/builders не вычисляются, параметры не читаются. Multi-mapping/splitOn не создаёт relation.

SchemaDiffResult возвращает added/removed/changed table/column/PK/FK, before/after, и RenameCandidates отдельно. Candidate означает сходство структуры, не доказанное переименование. Результаты и поиск кандидатов bounded с явной диагностикой. Recipe dependency содержит DatabaseId/TableId/ColumnName?/RelationId?, выводится из SQL, включая wildcard columns.

Objects содержат views, procedures, scalar/table functions, synonyms, parameters/return metadata, bounded definitions и source evidence. Indexes сохраняют key direction, included columns и filter; ObjectDependencies — bound target либо unresolved TargetName. Все object identities scoped по БД. Explorer и diff используют расширенную модель. Views доступны recipe binder через отдельный ReadableObjects catalog; физический граф таблиц не смешивается с routines. Изменение тела используемой view требует проверки, пропавшая колонка даёт Broken. Неизвестные вычисления/динамические ссылки и результат procedures остаются partial.

Ручные связи сохраняют reason/version и точную форму колонок; stale metadata не возвращается в trusted graph через старое подтверждение. SnapshotRecord.BuildManifestId связывает авторитетную модель с immutable source closure manifest. Детали: enhancements.md.


