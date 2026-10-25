# Architecture

Schemora is a single local ASP.NET Core process with an embedded React/Vite UI. The self-contained single-file win-x64 publish includes the UI, the SQLite native runtime, DacFx, ScriptDom and embedded Roslyn metadata references. Running the EXE requires neither Node nor an installed .NET runtime.

Modules:
- Host: API, session security, staging/build tooling, jobs, workspace/relationship/recipe/change services.
- SqlServer: DacFx metadata, ScriptDom static DDL classification/reader, schema diff.
- Workspaces: read-only discovery, fingerprints, SQLite catalog/migrations/backup/recovery.
- Relationships: Roslyn EF mapping, embedded C# SQL/Dapper evidence, conservative candidates and trusted path finder.
- RecipeEngine: typed ID parser, DSL, deterministic SQL renderer, SQL dependency binder and conservative AST column repair.

SQLite runs in WAL mode. Catalog schema v2 adds expression indexes; a SQLite backup is created automatically before migrating from v1. Snapshots and recipe versions are immutable. Publishing sources/databases/snapshots is atomic; old snapshots are kept after a source is removed. A single writer host per data directory holds a file lock; after a crash the OS releases the handle.

The BackgroundService queue is bounded to 16, with one worker and one active job per workspace. Quick uses a timestamp/length+hash cache; Full/Rebuild rehash. Full is also needed after a content change that keeps the timestamp and length of ordinary discovered sources. For an authoritative SQL project model, the verified build closure is always rehashed, including external and ignored inputs. An unfinished discovery does not replace the working catalog. Publishing a stale job is rejected after roots/excludes change. After publishing, recipes are checked; errors in one database keep its previous snapshot with a failed/stale status.

DACPACs are read in-process without Docker. SQL project build: detect SDK/legacy → stage source/inherited configuration into a private service directory → dotnet/MSBuild → fresh DACPAC → DacFx. A bin/obj artifact is never assumed to be current. Static SQL is always marked partial. Building requires trust: arbitrary MSBuild targets are not sandboxed. The planner collects a bounded closure of local sibling projects/imports/ArtifactReference/SQLCMD, verifies source hashes and saves an immutable build manifest. Pinned NuGet DACPAC packages are restored into a private stage/cache and included in the hashed inputs. Unverifiable dynamic context explicitly requires an external build and a DACPAC.

The relationship model separates confidence from trust. Physical FKs and proven EF mappings are trusted; SQL/Dapper joins and naming candidates require confirmation with a stated reason. Separate DB identities prevent mixing tables with the same name. C#→DB mapping is set explicitly when there are several databases. Dapper splitOn does not establish an FK.

Recipes store definitions and derived lineage; runtime IDs/SQL/payload are transient. The ScriptDom binder checks available scopes and diagnoses ambiguity. After a schema change, a missing dependency is Broken; a type change relative to the last-compatible snapshot is NeedsReview. Repair preview patches only AST identifier spans in unambiguously bound SELECT/CTE/subquery/UNION contexts; apply is an explicit confirmation that creates a new immutable version.

The API is protected by the current fragment launch token, an exact Host/Origin and Fetch Metadata; Kestrel listens only on 127.0.0.1. CSP forbids external scripts/connect; Monaco workers are bundled locally. Private runtime launch info lets a repeated launch open the current session; if the ACL of the user's data dir cannot guarantee privacy, the token is not stored there.

PostgreSQL and AI are excluded from V1. Generated queries are never executed against business databases, and user assemblies/source generators are never loaded. Analyzer limitations are reported as diagnostics rather than as a fictitious authoritative schema.


AutoRefreshService watches roots via FileSystemWatcher, debounce and periodic reconciliation; it uses the shared queue for Quick/Full only. SchemaObjectService provides paged objects/detail/dependencies/transitive impact. ManualRelationService and ChainRecipeBuilder use the same relationship-context revision as recipe saving/generation. Details and extension boundaries: enhancements.md.


