# Schemora 1.3 — six product improvements

## Portable workspace

Settings exports configuration v2: recipe definitions, explicitly saved artificial regression fixtures, ordered manual relationships, confirmations/rejections, C# project/database mappings, safe configuration/framework selections and database aliases. Root paths, generated SQL, runtime ID text, run history and trust to execute project evaluation are excluded. Artificial fixture values are included deliberately and are visible for review before saving/exporting.

Create and scan the target workspace, map databases and projects explicitly, then preview. Conflicting mappings/aliases block import. The preview token binds the manifest, mappings and current model. Recipes import independently with validation results; imported relationships and decisions remain pending. Each pending assertion must be checked against the current schema and individually confirmed, or discarded. A missing or changed source cannot regain trust from the exported decision alone. Existing v1 imports remain supported. Existing roots/excludes remain local; themes are controlled by the theme selector.

## Recipe regressions

The Regression fixtures tab has its own empty input form. It never copies runtime generation inputs. Saving requires explicit confirmation that the data is artificial. A baseline stores typed value counts, duplicates, value/ordinal order, duplicate/order policies, chosen strategy and a hash of the generated SQL/parameter contract. Optional required/forbidden SQL fragments express additional expectations. These are renderer/schema regression checks, not assertions about business rows returned from a live database.

Saving a recipe or revalidating it after a schema scan reruns its saved fixtures. Runs identify the recipe version and show failures separately from schema compatibility. Altering SQL, parameters or input rules requires reviewing the failed baseline and explicitly replacing it. Malformed imported fixtures produce a failed run without breaking recipe saves or scans. Definitions are retained; old run history can be cleaned under the storage policy. No production connection or SQL execution is added.

## Memory and history

Low memory mode is enabled by default. Graph inference runs after a context change, then a revisioned SQLite projection serves paged table/relation searches and bounded neighborhoods. The full relationship graph is not retained in the service cache in this mode. SQL object listing also projects only selected catalog rows. Full inference/recipe validation still requires a transient full graph; this is not a fixed-RAM streaming analyzer.

Graph fragments contain at most 500 tables and 2000 edges. The UI requests 150 tables and 100 relations per page. Neighborhood expansion is bounded at 10000 identities with a truncation indicator. Legacy relation-list responses are bounded at 200 rows by default; use `/relations/page` with offset/limit for the complete catalog. Searches retain case-insensitive Cyrillic matching.

Settings shows catalog/WAL sizes and an explicit cleanup preview. The policy retains at least 2–100 recent snapshots per database and 1–3650 days of history. Current snapshots, last-compatible recipe snapshots, retained validation references, proposed repairs, recipe definitions/versions and fixture definitions are protected. Cleanup is blocked during active jobs, requires a current preview, backs up SQLite first, and removes snapshot child rows together. Backups and build packages are not automatically deleted. Freed SQLite pages are reusable and may not shrink the file immediately. Cleanup never runs automatically.

## C# compilation context

Relationships and paths includes configuration and target-framework selection per C# project. Safe static evaluation applies project file membership, conditional symbols, language version and available local reference metadata; unsupported imports, generators and dynamic expressions are diagnosed as partial. Project settings participate in relationship revision guards. Relevant content changes invalidate context without requiring a discovery scan first.

An explicit trusted preview/save may run MSBuild evaluation to obtain actual items, symbols and imported project inputs. Imports and property functions can execute project code; the checkbox is required. No restore/build/generator targets are requested. Process time, output and inputs are bounded. Ordinary graph reads and the watcher never start MSBuild. Trusted results are reused only while their recorded inputs match; changes require a new explicit evaluation. Trust/caches are not transferred through portable configuration.

SDK/package/generator and separate referenced-project compilation can remain partial even after evaluation. EF evidence from an incomplete context remains a candidate that can be reviewed explicitly. Dapper static SQL is candidate evidence; dynamic runtime SQL is not executed or inferred as a foreign key.

## Explicit table/schema rename repair

Changes supports mapping an explicitly named previous table/schema to a selected current table. Repair requires qualified sources with aliases and unambiguous binding. Preview preserves aliases, literals and comments and validates rewritten SQL. Approval creates a new version only if recipe version, schema snapshots and relationship context still match. Unsupported rowsets/CTEs or ambiguous names require manual repair. This maps a selected table; it is not a schema-wide batch rewrite.

## Diagnostics and source navigation

API errors retain their code, status and operation ID. Unexpected exception text is not returned to the browser. A bounded local journal stores operation codes, technical frame names and job progress, never request bodies, exception messages, SQL, ID text, headers or process output. Logs rotate at about 1 MiB with one retained previous file; the UI shows recent events or a selected job log.

The support JSON includes build/runtime, sanitized tool versions and current snapshot hashes. Root paths are excluded unless the user enables the option. SQL definitions, tokens, fixture values and source contents are excluded. Evidence/object sources support copying path:line and explicitly opening VS Code or Visual Studio. Opening validates an existing local source file under a workspace root, rejects links/network paths and passes arguments without shell interpolation. If an editor is unavailable, copying remains available.

## Verification

Run `scripts/Test-Clean.ps1 -Docker` for clean source build/publish plus existing and new .NET/HTTP/Chrome/Docker checks. New suites: `Test-ProductV13.ps1`, `Test-MaintenanceV13.ps1`, `Test-ProductV13Ui.ps1`. Verification evidence is recorded in `docs/verification.json`; a fresh Windows installation without development tools and remote CI are not implied by a clean source build on the development host. PostgreSQL and optional AI remain deferred.
