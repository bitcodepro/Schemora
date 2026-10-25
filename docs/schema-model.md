# Schema and relationship model

SchemaGraph: DatabaseName, SourcePath, ExtractedAt, Tables, Relations, Diagnostics, Objects, Indexes, ObjectDependencies. SchemaTable: Id, Schema, Name, Columns, PrimaryKey, UniqueKeys. SchemaColumn: Name, DataType (length/precision/scale), IsNullable. SchemaRelation: Id, Name, FromTableId, ToTableId, FromColumns, ToColumns, Kind, Confidence. The order of From/ToColumns preserves the composite key mapping.

The catalog assigns each database a stable identity by workspace+source path; a table ID includes the database identity and hash(schema+name). Identical names in several schemas/databases are not merged. The current SQL database name can be set as an alias with a reason. A cross-database JOIN is blocked when two different database identities share the same SQL name.

DiscoveredSource stores identity/path/kind/hash/lastWrite/length/diagnostics. SQL is split into schemaSql/migrationSql/querySql/dataSql/unknownSql. Configs are indexed by metadata; their contents/connection-string secrets are not read. Nested repositories and projects are discovered separately; links/cycles and service directories are skipped. A working subset of .gitignore is supported; the Git index/global excludes are not used.

SnapshotRecord is immutable and contains the fingerprint, mode and the full scoped graph. DatabaseInfo stores currentSnapshotId/status. A refresh error keeps the last successful graph, but its relations are marked stale and excluded from trusted paths. History remains available when the source database disappears.

RelationshipGraph contains tables with DatabaseId/DatabaseName and edges with Status, IsTrusted, Evidence and ReviewReason. Evidence: Kind, SourcePath, Line, Detail. Kind: declared-fk, orm-mapping, user-confirmed, observed-join, inferred-candidate, embedded-sql-candidate. User confirm/reject decisions are stored separately from the derived model. A relation that disappears or changes becomes stale; a conflict with the physical schema is visible.

EF binding runs on the Roslyn semantic model with embedded metadata references, without executing C#. Shadow/dynamic/helper mappings, unresolved official symbols or unknown registration produce a diagnosed partial mapping. Dapper extracts only literal/const SQL in Query*/Execute*/inline CommandDefinition; runtime interpolation/builders are not evaluated, parameters are not read. Multi-mapping/splitOn does not create a relation.

SchemaDiffResult returns added/removed/changed table/column/PK/FK, before/after, and RenameCandidates separately. A candidate means structural similarity, not a proven rename. Results and candidate search are bounded with explicit diagnostics. A recipe dependency contains DatabaseId/TableId/ColumnName?/RelationId? and is derived from SQL, including wildcard columns.

Objects contain views, procedures, scalar/table functions, synonyms, parameters/return metadata, bounded definitions and source evidence. Indexes keep key direction, included columns and filter; ObjectDependencies hold a bound target or an unresolved TargetName. All object identities are scoped per database. The Explorer and diff use the extended model. Views are available to the recipe binder via a separate ReadableObjects catalog; the physical table graph is not mixed with routines. A change to the body of a used view requires review; a missing column gives Broken. Unknown computations/dynamic references and procedure results remain partial.

Manual relations keep reason/version and the exact column shape; stale metadata does not return to the trusted graph through an old confirmation. SnapshotRecord.BuildManifestId links the authoritative model to the immutable source closure manifest. Details: enhancements.md.


