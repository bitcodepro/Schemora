# Post-V1 enhancements

PostgreSQL and AI are deferred at the user's request. There are no user pilot repositories: checks use generated SQL/C# projects and temporary repositories. They cover the listed scenarios but do not represent someone else's real codebase.

## Building SQL projects

The planner reads XML and local inputs without running MSBuild. It supports SDK-style Microsoft.Build.Sql and legacy SSDT, sibling SQL ProjectReference, local ArtifactReference, ordinary imports/props/targets, known inherited configuration, SQLCMD include/variables. The service copy keeps the structure and redirects each project's output/intermediate into short separate directories inside the service copy. If a custom data dir is too long, the service copy moves to a private Windows temp folder; the manifest stays in the chosen data dir. A path that still exceeds the tooling launch limit is diagnosed explicitly. The source closure is verified by content before and after the build; a dependent file appearing, disappearing or changing makes the old model unusable for reuse.

Pinned NuGet packages with DACPACs are supported: restore uses a separate cache, and package contents, identity/version and SHA512 are verified. Central package versions and local feeds are included in the analysis; floating versions, package code/tasks and ambiguous context are diagnosed explicitly.

The immutable build manifest stores paths, input types, sizes and SHA256, configuration, the project list and hashed environment inputs. SQLCMD/environment values are not stored in the manifest. A snapshot contains BuildManifestId. API: POST /api/schema/build/plan, GET /api/build-manifests/{id}, GET /api/databases/{id}/build-manifest. This is a local technical manifest with absolute paths; it differs from the portable workspace export.

Property functions, dynamic imports, unknown custom tasks and mixed SDK/legacy dependencies are not declared a complete portable context. BUILD_INPUTS_INCOMPLETE is returned with specific diagnostics; the remaining option is an external build and DACPAC import. Building requires explicit trust: MSBuild runs with the user's rights.

## Manual relations and route-based recipes

The editor creates a logical relation between tables with ordered column pairs, a name and a reason. Updating/deleting checks the expected version. The exact column shape is stored; a type/nullability change or a missing endpoint makes the assertion stale. Type compatibility does not prove business-key uniqueness. Physical FKs keep priority, and a conflicting manual relation requires a separate review in the graph.

The recipe builder takes a source ID column, a chosen trusted route and an explicit projection of intermediate/final table columns. Result aliases and field order are editable. The server re-checks continuity, trust and composite JOIN conditions, creates a typed input and runs the SQL binder. Preview does not save the recipe; the user then opens the draft and saves a regular version. Supported types: int, bigint, uniqueidentifier, nvarchar(n), varchar(n); for varchar the existing explicit encoding policy applies.

## Database objects

SchemaGraph gains Objects, Indexes and ObjectDependencies; SchemaTable gains UniqueKeys. Old snapshot JSON stays compatible; missing lists are treated as empty. The snapshot analysis version guarantees a one-time recomputation of an old catalog on Quick/Full Refresh even if source files did not change. Tables stay tables: views, procedures, scalar/table functions and synonyms are represented as separate objects.

Available: columns/parameters, SQL definition, synonym target, unique constraints, index keys/direction/include/filter and dependency evidence. Definitions are limited to 256 KiB of UTF-8 per object and 32 MiB in total; exceeding the limit is diagnosed explicitly. Static analysis stays partial: computed types, procedure result sets, dynamic SQL and unresolved external references are never invented. An ambiguous dependency keeps TargetName and an empty ToObjectId.

The Explorer uses server-side pagination; the SQL definition loads when an object is opened. Detail shows direct dependencies, calling objects and transitive impact on objects/recipes. Snapshot comparison covers definitions, parameters, unique keys, indexes and dependencies. Routine metadata is available for exploration; the JOIN builder uses tables from the current relationship graph. SELECT recipes can read views with column checks; a partial view requires review, and a definition change or a view/table replacement invalidates compatibility.

## SQL repairs

The extended AST rewriter supports provable column bindings in SELECT with CTEs, derived/scalar subqueries and UNION. Ambiguous aliases/lineage, computed expressions and unsupported contexts require manual editing. Preview keeps formatting and changes only identifier spans; confirmation still creates a new version with schema, relation, recipe and proposal-decision guards.

## Auto-refresh and release verification

Auto-refresh is off by default. FileSystemWatcher coalesces events with a 2–60 second delay; periodic reconciliation every 1–60 minutes covers missed events and external inputs. On startup, changes made while the app was not running are checked. A watcher overflow/error schedules a Full Rescan and a reconnect. Built-in output/cache folders and the app's own data dir are excluded from events.

Auto-refresh uses the shared bounded queue and checks dependent recipes. It runs Quick/Full without trust and never triggers Rebuild. A changed SQL project is shown as partial until an explicit build.

scripts/Test-Enhancements.ps1 checks manual composite relations, versions, a route through three tables, objects/impact, real file changes, recipe invalidation, stale relations and restart. scripts/Test-Clean.ps1 creates a copy of the sources without bin/obj/node_modules/work/artifacts, saves a SHA256 inventory, installs UI dependencies, builds/tests/publishes and runs all acceptance checks against the produced EXE. -Docker adds SQL Server execution and app schema extraction/recovery. Verification runs on the current Windows host; the GitHub workflow runs separately when a remote exists.




## Languages and launch port (1.2)

The default locale is en; ru and uk are supported. The choice is stored in the UserSettings language and the browser cache key schemora.language, without runtime IDs. Supported values are validated by the server. Changing the language updates subscribed components without a remount; loading an old setting and successive writes do not overwrite a newer choice. Dates/numbers follow the selected locale. The app's own messages are translated from full templates with parameters preserved; user names/descriptions/SQL/reasons are kept as is. Unknown messages from external tooling stay in their original form.

schemora.json is read next to the EXE, regardless of the working directory: {"port":0}. The file is optional; an explicit --config must exist. Exactly one integer field, port (0..65535), is allowed, up to 16 KiB. --port takes precedence. The setting changes only the loopback port. A fixed port that is in use yields LAUNCH_PORT_IN_USE and the JSON path, with no silent fallback. Rebuilding/republishing keeps an existing sidecar regardless of timestamps.

Test-Localization checks all three languages, persistence after restart/fresh browser, rapid changes, live graph labels, an unsaved recipe, runtime ID/error text and generated SQL. Test-Launch checks JSON/CLI precedence, a missing/invalid file, ports in use and that origin/token guards are preserved.
