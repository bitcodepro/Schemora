# Changelog

## Unreleased

- SQL is generated only from the recipe version on screen: the request names it, a newer version is refused with RECIPE_VERSION_STALE, and the result reports the version it used.
- A save, copy or rollback that answers after a workspace or recipe switch is stored but no longer replaces the shown recipe or the address.
- A historical snapshot stays pinned in Objects (list and details), in the address (`snap=`) and after reload; choosing an object in Objects updates the address.
- The search cache drops the least recently searched workspace instead of all of them.
- The build carries its git commit (1.8.0+abc123def456, `.dirty` for local edits), shown as a tooltip on the version and returned by /api/health.
- Back/Forward restores the complete workspace, database, object, recipe and tab selection and honours the unsaved-draft confirmation. Cancelling a traversal preserves its history entry.
- Schema loads discard superseded responses and read a specific immutable snapshot. The open catalog checks a lightweight schema-state endpoint every five seconds, preserving the selected object; explicitly opened historical snapshots and imported DACPACs stay pinned.
- Large schema overviews always retain their selected table within the sixty-node limit. Ctrl+K filters support keyboard and assistive click activation with focus kept inside the dialog.
- Browser regressions cover multi-workspace history, cancelled navigation, delayed responses, background snapshots, keyboard filters and high-degree graph focus.
- Recipes accept single values: `{{value:Name}}` renders one typed literal (or reads the JSON parameter when parameterized); new input types date, datetime2 and bit with strict ISO parsing and explicit CONVERT styles, verified on SQL Server with a German session language.
- Single-value inputs get a one-line field in the UI; the tutorial adds `Customers.CreatedAt` and a "Period: new customers" recipe.
- CI fails on high-severity npm advisories and on vulnerable NuGet packages (none found today).
- decimal inputs with precision and scale (exact literals, no floating point); quick date presets under date fields.
- `Schemora.Host.exe render` renders SQL from an exported recipe file, beside a running app and in scripts.
- Changes page compares two databases of a workspace, or a database with a DACPAC file, read-only.
- Relationship graph exports as PNG or SVG.
- Database comparison: filter by change kind and export the shown rows as CSV (UTF-8 with BOM for Excel).
- Recipes with two date values offer one-click whole periods (last/this month, last 7/30 days, last/this year; end exclusive).
- Microsoft.Build.Sql 2.3.0 for the tutorial and SDK fixtures.
- The recipe run tab is called "Inputs and SQL"; the validate button names what the recipe takes.
- Optional single values: a `{{value:...}}` input marked optional renders as a typed NULL when left empty, so `({{value:From}} IS NULL OR ...)` skips the condition. Synthetic checks use typed samples for every input type.
- Saved values: runtime values can be kept under a name per recipe, encrypted with Windows DPAPI for the current user in `value-sets/` of the data directory; never in the catalog, exports, backups or diagnostics.
- Favourite and recently opened recipes in the recipe list.
- A saved collection renders into one SQL script (one batch per recipe, inputs entered once); `render` accepts several `--recipe` options for the same result.
- Comparison: system-named keys and indexes match by shape across separately deployed databases; HTML report export; a review-only migration draft (ALTER script) with data-losing steps commented out.
- Graph: colour tables by schema (with a legend) and compact cards. `?` lists keyboard shortcuts.
- Automatic catalog backups every 24 hours, keeping the newest 7 (configurable, with "Back up now").
- Server errors with a fixed message are translated by code (`npm run i18n:errors`), so rewording a message no longer untranslates it.
- Colour system: all stylesheet colours are 40 role tokens redefined by the light theme, which also fixes light-theme spots that stayed dark.
- Larger type: no interface text below 12px (7-8px became 12px, 9-10px 13px, 11-12px 14px); SQL editors use 14px; tight spots in the sidebar, recipe list and saved values were relaid.
- Graph cards mark primary keys with a key icon and highlight the columns that join tables; relations attach to those column rows, and the selected relation or JOIN path marks its columns. Select boxes draw their arrow with room from the edge.
- Schema catalog follows the selected workspace and its current snapshot: a workspace without a snapshot clears the old model, a rescan reloads it, and loading is shown.
- Ctrl+K opens another recipe even when the recipes page is already open, on its run tab; the Changes page still opens history.
- Large models: the catalog lists tables 200 at a time and the overview draws the selected table's neighbourhood beyond 60 tables.
- Search uses an index rebuilt only when snapshots, recipes or reviewed relations change; exact names rank first; Ctrl+K filters by kind and shows more; workspace search filters by database.
- The address keeps the page, workspace, recipe and tab, catalog object and graph database (`#/recipes?…`), so reload, Back and copied links return to the same place; runtime values never enter it.
- Unexpected server failures return 500 INTERNAL_ERROR with the operation number; unreadable files and tool failures stay 422.
- CI runs `npm test` for every UI test and a weekly job executes generated SQL on SQL Server.
- App.tsx and RecipesPanel.tsx are split into page, tab and hook modules; browser tests wait for an app-ready marker; Test-All runs suites in parallel (`-Throttle`).

## 1.8.0 — 2026-10-23

See docs/product-1.8.md for behaviour and limits.

- Opt-in "IN list" SQL strategy (plain literal list, up to 1000 values); Auto output is unchanged.
- Loading the tutorial from the UI builds its DACPAC when a .NET SDK is available.
- Ctrl+K keeps focus in the dialog; minimap and edge labels follow the theme; capitalised kind labels.
- Untranslated server messages fixed, with a test that fails on new ones; npm test runs every UI test.
- C# formatted with CSharpier (checked in CI); Test-All has -Group Core|Ui|Docker; scripts/README.md lists the suites.
- The host reports its version from the assembly; the sidebar shows it.
- A failed Rebuild keeps the existing model's status instead of marking it failed.
- Light theme: table headers, tags, recipe validation and lineage cards, the CSV/TXT button and capability icons no longer stay dark.
- One workspace selector in the sidebar (topbar on narrow screens) for every page; recipes keep their unsaved-draft confirmation.
- Schema catalog opens the selected workspace's model and lists its databases; paths are shown relative to the workspace roots.
- Ctrl+K search palette over pages, tables, columns, SQL code and recipes.
- Graph: theme-aware canvas, layered layout along the FK direction, direct edge routing; a chosen JOIN path dims other edges and is fitted into view.
- Saved recipes open on ID input; compact Quality findings; Settings jump bar; SQL editors size to their content.
- Page stylesheets load at startup, so shared classes no longer depend on navigation order; theme tokens for shared surfaces.
- Tutorial names its keys and indexes its FK; "Errors: N" wording; "Regression fixtures" translated.
- SDK builds no longer append a dotnet tools folder to the user PATH for every data directory.
- Prettier formatting, an i18n coverage test, and Test-All keeps only the last three runs per kind in work/.

## 1.7.0

- Bounded, redacted Docker failure journals with stage/timing and a diagnostic viewer.
- Managed file inventory, retention preview, protected backups/manifests and explicit cleanup.
- Per-workspace quality-rule severity and four additional metadata checks.
- SQL definition search with excerpts, source coordinates and match selection.
- Actual SDK/MSBuild/SSDT receipts in manifests and atomic scan provenance.
- Keyboard skip/main focus, named navigation, editor Tab escape, both-theme focus/contrast checks.
- Fixed tutorial SQL batch separators so its SDK project can be rebuilt.


## 1.6.0

- Workspace quality review with severity, independent finding identities, source navigation and reasoned suppression.
- Atomic scan receipts, source freshness and recorded input comparisons.
- Unicode metadata search with direct object/recipe navigation.
- Recipe collections and retained batch validation comparisons.
- Differential SQLite dependency projection updates and warm index reuse.
- Catalog integrity, verified restore preview, restart and rollback to the preserved copy.


## 1.5.0

- Local authoring recovery across browser origins, with navigation guards and runtime-value exclusion.
- Directed SQL-object-to-recipe paths with search, paging and explicit traversal bounds.
- EF/Dapper coverage diagnostics, source membership and next actions.
- Version comparison with current-schema validation before append-only restoration.
- EN/RU/UK HTML reports with selectable sections and readable recipe dependencies.


## 1.4.0 — 2026-10-23

- Guided first use and explicit isolated tutorial project creation, static scan and recipe.
- Repository/project/database/schema/object navigator with saved per-workspace filters and bounded object pages.
- Clickable recipe dependency diagram, transitive impact and keyboard-accessible recipe list.
- Separate typed Dapper and ADO.NET examples for parameterized rendering.
- Escaped standalone HTML schema documentation including keys and relationship evidence.
- New keyboard/zoom/localization Chrome acceptance and explicit clean-Windows runtime test.
- CI uses a separate hosted job consuming its own build artifact. Remote CI and actual fresh Windows verification remain pending environment/repository availability.

## 1.3.0 — 2026-10-23

- Portable v2 transfers manual composite relations, decisions, project/database mappings, safe C# selections, aliases and artificial recipe fixtures with explicit preview and pending relation review.
- Saved artificial recipe baselines rerun on recipe/schema checks and detect input policy, order, strategy and SQL contract changes.
- Revisioned SQLite graph browsing, server relation/object paging, default low-memory mode, bounded jobs cache and explicit backed-up history cleanup.
- Selected C# project membership, configuration, framework symbols and references; explicit trusted MSBuild evaluation with content-invalidated reuse and no automatic execution.
- Explicit table/schema rename mapping with conservative binding, guarded preview and separate version-creating approval.
- Structured API errors, rotating sanitized operation/job logs, optional-path support report and copy/open source locations.
- New EN/RU/UK UI workflows and .NET/HTTP/Chrome regressions. See docs/product-1.3.md for boundaries.

## 1.2.0 — 2026-10-23

- English is the fresh-install default; Russian and Ukrainian are available from the header and settings with persistent selection.
- Localized controls, statuses, diagnostics, accessibility labels and date/number formatting. Switching language preserves drafts, editor state and runtime ID memory; SQL and user-authored content stay unchanged.
- External schemora.json configures the loopback port (0 selects a free port); --port overrides JSON and --config selects an explicit file. Occupied fixed ports and invalid config have actionable diagnostics.
- Windows release includes one self-contained EXE and an optional JSON sidecar. Repeated publish preserves an existing customized JSON.
- Added locale catalog/state tests, real Chrome locale/restart/privacy checks and real launch/occupied-port security checks.

## 1.1.0 — 2026-10-22

- Verified build closure: sibling SQL projects, local DACPAC references, imports, SQLCMD inputs, immutable hash manifest and exact invalidation.
- Manual composite relationship editor with reasons, optimistic versions, schema drift checks and visible physical conflicts.
- Three-table/multi-table recipe wizard with chosen trusted route, typed ID input, ordered projections and output aliases.
- Views, procedures, scalar/table functions, synonyms, unique constraints, indexes, bounded SQL definitions and dependency/impact explorer.
- Extended snapshot diff and conservative CTE/subquery/UNION column repair with explicit preview/approval.
- Opt-in file watcher, debounce, periodic reconciliation, restart recovery and automatic dependent recipe checks.
- Versioned analysis cache upgrades existing snapshots once; readable views participate in recipe binding and compatibility checks.
- Created synthetic repositories, extended real Chrome/HTTP/Docker regressions and clean Windows source-build verification.

## 1.0.0 — 2026-10-22

- Phases1–5: multi-root discovery, SQLite v2 immutable snapshots, bounded jobs/SSE, incremental refresh.
- ScriptDom static schema fallback и явная диагностика SDK/legacy tooling; read-only source staging.
- Physical/EF/observed/inferred/user-confirmed relationships, Roslyn static mapping, C# SQL/Dapper candidates, trusted paths и React Flow canvas.
- Typed ID processor, deterministic VALUES/temp/JSON generation, one parameter payload, quoted/Unicode/encoding validation, no runtime persistence.
- Local Monaco, versioned recipe catalog, lineage, synthetic tests, export/import/clone/rollback.
- Schema diff, impact statuses, conservative AST repair preview/approval/newversion.
- SQLite migration/backup/recovery, oneinstance lock, optimistic concurrent edit guards, portable manifest with explicit DB mapping, themes/hotkeys.
- Optional owned SQL Server Docker schema preview, cancellation/recovery/cleanup, executable SQL generation tests.
- Self-contained singleEXE, HTTP/Chrome/security/performance regression scripts and Windows CI.

## 0.1.0 — 2026-10-22

- Phase0 toolchain spike: SDK-style+legacy SQL projects → real DACPAC → DacFx model.
- Embedded React UI, local session security, source-immutable staging and real isolated Docker/SqlPackage metadata proof.



