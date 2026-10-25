# SQL Schema Studio — requirements specification and implementation plan

## 1. Purpose

Build a universal **local application** for analysing SQL schemas, navigating table relationships and safely producing repeatable SQL queries without connecting to production databases and without sending user IDs or data to an LLM.

The initial DBMS is **Microsoft SQL Server**; the architecture must allow adding PostgreSQL later as a separate provider. Sources are local SQL project repositories, plain `.sql` files, C# projects and Entity Framework Core mappings. One working folder may contain many repositories, solutions, databases and schemas.

**Main user scenario:** choose a root folder → discover SQL/C# projects → build an authoritative model of several databases → automatically establish physical and probable logical relations → browse an interactive graph → create and save a parameterized recipe → paste a list of IDs → deterministically get correct SQL → after the repositories change, refresh the model and re-check recipes.

## 2. Core principles and boundaries

- One executable file for the user application: the local ASP.NET Core server, API, UI and analyzer run in **a single process**. Docker and external SDKs/compilers are allowed as optional external tools for reproducing schemas; they are not permanently running application services.
- Open the UI in the browser on `localhost`. Do not expose the server to the local network. By default no registration, cloud, telemetry or AI.
- Do not connect to production/staging and do not read real business data. An isolated local SQL Server database in Docker is acceptable solely for deploying a schema and extracting metadata.
- No hidden changes to repositories. Scanning is read-only; service data lives in a separate application directory.
- When relations are ambiguous, the tool shows the confidence level and the source of evidence and **does not present a guess as an established FK**.
- SQL generation and ID processing are fully deterministic, verifiable and reproducible operations without an LLM.
- The MVP targets Windows x64, with platform-dependent processes isolated to allow supporting other OSes later.

## 3. Technologies and components

- **Backend:** .NET (current supported LTS), ASP.NET Core Minimal APIs or Controllers, DI, BackgroundService for the local job queue.
- **Frontend:** React + TypeScript + Vite, built into static assets and embedded in the published backend. Graph: React Flow (`@xyflow/react`) or an equivalent that supports large schemas and virtualization; editor: Monaco Editor.
- **Local storage:** SQLite for workspaces, object catalogs, relations, user confirmations, recipes, schema snapshots and validation results. Storage version migrations.
- **SQL model:** our own normalized metadata model, independent of a specific DBMS; provider abstraction (SQL Server in V1, PostgreSQL later).
- **C# analysis:** Roslyn/MSBuildWorkspace where possible; fallback file parsing and explicit diagnostics if loading the solution fails. Analysis of the EF Fluent API and attributes, without executing user C# code by default.
- **SQL analysis:** Microsoft.SqlServer.DacFx / ScriptDom and/or SQL project build; do not assume a text regex can reliably parse all of T-SQL.
- **Schema execution:** Docker CLI and a local SQL Server container — only when the deployment mode is selected, with the user's consent and strict isolation.
- **Logs:** structured local logs that do not store pasted IDs by default.

### 3.1 Proposed solution structure

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

The project split is indicative: do not create empty abstractions just for the sake of layers. Internally there is one host process and one exe; modules are libraries, not microservices.

## 4. Launch and installation

1. `dotnet publish` produces a self-contained single-file EXE for Windows x64 with the static UI assets inside the publish output; verify the actual publish structure and behaviour in CI.
2. Double-clicking the exe starts the backend on `127.0.0.1` on a free local port and opens the browser. The port and launch token must not allow unsolicited access from other websites: protect the local API (Origin/CSRF checks and a per-session token).
3. Store SQLite, settings, cache and logs in a separate profile directory (`%LOCALAPPDATA%/...`), not next to the exe or in the repositories.
4. A diagnostics page checks the availability of Docker, SQL tooling and .NET SDK/MSBuild and explains which feature is unavailable and how to enable it.
5. Closing the application cleanly stops background processes and, depending on settings, removes the temporary containers it created. Never stop other containers.

## 5. Workspace discovery

The user specifies one or more root folders (workspace roots). Discover recursively:

- Git repositories (including nested ones);
- `.sln`, `.slnx`, `.csproj`, `.sqlproj`, `.dacpac`, `.sql`, ORM configuration files;
- SQL definitions (`CREATE TABLE`, `ALTER TABLE`, PK, FK, indexes, views, synonyms — as support is added);
- EF Core `DbContext`, `DbSet`, `[Table]`, `[Column]`, `[ForeignKey]`, Fluent API (`ToTable`, `HasOne`, `HasMany`, `WithOne`, `WithMany`, `HasForeignKey`, `HasPrincipalKey`) and `IEntityTypeConfiguration<T>` configurations;
- project-to-database bindings from project metadata, configurations, connection-string *names* and EF mappings. Do not store passwords/secrets from configs.

Exclude `.git`, `bin`, `obj`, `node_modules`, `.vs`, `dist`, temporary directories and symlink cycles; support `.gitignore` and user exclude patterns. Do not index every SQL script as DDL: distinguish schema DDL, migrations, ad-hoc queries and seed/data scripts.

Every source has an identity, path, hash, timestamp, parse status and diagnostics. Support several databases with identical table names; full object names include workspace/database/schema/name and a stable internal ID. Mapping an unknown project→database is a suggestion to the user, and the decision is saved.

## 6. Schema building and DACPAC

**A DACPAC can be built automatically; a separate Visual Studio extension is not required as such.** But a compatible build tool/SDK is needed (depending on the SQL project type). The application must detect the `.sqlproj` type and show toolchain readiness rather than promise to build any project without its dependencies.

Priority of sources of truth:

1. **DACPAC from a SQL project:** detect a supported SDK-style SQL project (for example, Microsoft.Build.Sql) or a legacy SSDT SQL project; choose a compatible build command via `dotnet build`/MSBuild and the installed targets/tooling; reuse an existing artifact only if it is up to date.
2. **Parsing the DACPAC/DacFx model** without requiring Docker, if there is enough data for graph/schema extraction.
3. **Isolated deployment** to a local SQL Server in Docker, if the project or analysis mode needs a live SQL catalog: build the DACPAC → `SqlPackage /Action:Publish` into a local container → read `sys.schemas`, `sys.tables`, `sys.columns`, `sys.key_constraints`, `sys.foreign_keys`, `sys.foreign_key_columns`, `sys.indexes`, `sys.index_columns` and related metadata. Do not run arbitrary post-deployment scripts without separate explicit permission/review, since they can change data, reach outside or depend on the environment.
4. **Fallback:** static T-SQL analysis for sources without a buildable `.sqlproj`; explicitly report that the result is partial/incomplete.

Deployment is not required on every refresh: choose the fastest reproducible route, keep fingerprints and diagnose SQL Server edition/version incompatibilities. Do not mix metadata found for different databases. Containers/volumes must have unique prefixes for this application, isolated ports and credentials for the temporary environment; no accidental destructive action on other containers.

### 6.1 Analysis modes

- **Quick Refresh:** scan changed files by fingerprint, incremental parsing and re-checking of affected objects/recipes.
- **Rebuild Schema:** rebuild the selected database/DACPAC, fully update its snapshot and recompute dependencies.
- **Full Rescan:** rediscover root directories, all projects and databases, remove stale relations from the derived graph while keeping user decisions and history.

Show stages, errors, warnings, duration, cancellation, progress and the option to re-run. Operations must be idempotent.

## 7. Relationship graph and confidence

Nodes: workspace, repository, solution/project, database, schema, table/view, columns (details), recipe. Edges:

- **Declared FK**: FK from the SQL schema/catalog — confirmed.
- **ORM mapping**: EF configuration with an explicitly set table/column pair — mapped, with source and line/definition.
- **Observed joins**: joins from analysed SQL when they are syntactically unambiguous; evidence, not an automatic FK declaration.
- **Inferred candidate**: based on name patterns, data types, unique keys, entity context and additional signals; confidence score and reasons. Unverified relations must not be used automatically when building production SQL.
- **User-confirmed logical relation**: a logical relation confirmed by the user, with a reason and timestamp; stored separately from generated metadata.

For SQL Server `nvarchar`/`varchar` IDs that act as logical keys, candidates must be recognized even when no real FK exists. If there are several plausible paths, show the options and ask the user to choose. The graph highlights edge type and direction; a path between tables is built along the shortest/most relevant route with an explanation of the joins. Same-named fields are not proof of a relation.

**Evidence priority:** physical FK > exact ORM mapping > manually confirmed relation > observed SQL join > heuristic; a user confirmation may conflict with the physical schema — show the conflict instead of hiding it.

## 8. UI/UX

Main areas:

1. **Workspace Dashboard:** folders, repositories, databases, health, scan errors, Quick Refresh / Rebuild / Full Rescan actions.
2. **Schema Explorer:** a Database → Schema → Tables/Views tree, search by name and fields, filters, an object card: columns/types/nullability/PK/FK/indexes, source, usage.
3. **Relationship Graph:** an interactive canvas with pan/zoom, auto-layout, manual placement, focus on the selected table and its neighbours N steps away, filters by database/schema/type/confidence, and a separate mode for cross-database logical relations. The graph of a large system must load neighbourhoods/levels on demand rather than render the whole database at once.
4. **Path/Join Explorer:** choose the source and target tables, get routes, an explanation and a preview JOIN SQL; heuristic relations are not activated without confirmation.
5. **Recipes:** catalog, tags, search, versioning, SQL/settings editor and dependency view, parameter input and result preview.
6. **Schema Changes:** diff between snapshots (added/removed/renamed candidate/changed columns/FK/type), impacted recipes and remediation status.
7. **Settings/Diagnostics:** roots, excludes, build tools, Docker, security, data path, configuration export/import.

UI: a local desktop-like web interface with dark and light themes, keyboard shortcuts, saved graph position and clear empty/error states.

## 9. Recipes and SQL generation

A recipe is not a collection of scattered `.sql` files, one per WHERE clause, but a typed description of an operation:

- recipe ID, name, description, tags, target dialect/provider, database references, version;
- SQL template (preferably readable and verifiable), typed inputs, default policies and output mode;
- a set of dependencies on tables/columns/relations computed from SQL/metadata, not only entered by hand;
- test fixtures (without real user data) and expected result properties;
- validation status and the last-compatible schema snapshot.

Examples: "Invoices by Transaction IDs", "Documents for Invoices", "Transactions with related invoices".

### 9.1 Processing ID lists

- Accept pasted CSV, TSV, strings separated by spaces/newlines/commas/semicolons, SQL lists and CSV files; never send the data to a model or the network.
- Typed inputs: `int`, `bigint`, `uniqueidentifier`, `nvarchar`, `varchar` (with a specified length/encoding and validation rules).
- Show `total`, `valid`, `invalid`, `unique`, `duplicates`; an error report with the element/line number and a way to fix it.
- Preserve order when it matters; removing duplicates is an explicit setting, with no silent loss of values. Do not normalize GUID/string values without defined semantics.
- Generate SQL with correct escaping and no SQL injection. Two modes: **copyable standalone script** and **parameterized execution example**. Execute nothing by default.
- For large lists do not rely on `IN (...)` alone: support `VALUES`/`#temp` with batch INSERT, `OPENJSON` / `STRING_SPLIT` where types and the SQL Server version allow; for runtime ADO.NET, TVP when a compatible table type exists. Account for the SQL Server limit of 2100 parameters and the script size.
- Clearly show the exact number of values, the validation result and the SQL dialect/strategy used, and warn about a potentially heavy plan.
- Do not store entered IDs or the generated SQL containing them without explicit consent; do not log clipboard contents.

### 9.2 Recipe editor

Monaco SQL editor with T-SQL highlighting, a templating engine with a strictly limited set of expressions, preview, a lineage inspector, test sets and safe copying to the clipboard. Support manual creation, copying and importing a `.sql` file into a recipe, as well as a wizard for choosing tables/relations. Determinism and readable SQL matter more than a complex visual query builder at the first stage.

## 10. Schema refresh and impact analysis

On refresh/rebuild:

1. Get a new immutable schema snapshot and its diff against the previous one.
2. Recompute dependencies at the DB/schema/object/column/relation level.
3. Check every affected recipe: existence of objects, types, joins, parameters, SQL parsing/model binding, renderer compatibility.
4. Classify the recipe as `Valid`, `Warning`, `Broken`, `Auto-fix proposed`, `Needs review`. Give the exact reason, for example `dbo.Transactions.InvoiceId removed`.
5. Auto-fix only **unambiguous safe** transformations (for example, updating the path of an explicitly renamed object when there is sufficient evidence), create a versioned proposal and diff, and require confirmation before replacing. **Never silently change the meaning of SQL.**
6. Allow rolling back a fix and comparing versions. If there is no certainty, mark the recipe broken with a hint for manual repair.

For a heuristically inferred rename, use `possible rename`; do not assert it as fact without authoritative metadata.

## 11. Storage model

Minimal SQLite aggregates: `Workspaces`, `Roots`, `Repositories`, `Projects`, `Databases`, `SchemaSnapshots`, `SchemaObjects`, `Columns`, `Relations`, `RelationEvidence`, `RelationOverrides`, `Recipes`, `RecipeVersions`, `RecipeInputs`, `RecipeDependencies`, `ValidationRuns`, `ScanRuns`, `ToolchainCapabilities`, `UserSettings`. Large derived indexes and caches must be rebuildable. Support exporting/importing the workspace manifest and recipes in a human-readable format for Git, without local paths/secrets by default.

## 12. API and background jobs

Example endpoint groups: `/api/workspaces`, `/api/discovery`, `/api/databases`, `/api/schema`, `/api/graph`, `/api/paths`, `/api/relations`, `/api/recipes`, `/api/validation`, `/api/jobs`, `/api/diagnostics`.

Run long jobs through a bounded job queue with cancellation, retries only where correct, progress events via SignalR/SSE and a result log. Prevent concurrent rebuilds of the same object; publish a new snapshot transactionally after a successful build. A "partial success" state must be clearly visible.

## 13. Reliability and security requirements

- No execution of arbitrary commands taken from a repository without a trusted mode or an explicit warning. `dotnet build`, SQL post-deployment and third-party build targets can run code — mark the trust boundary and allow opting out.
- Analysis is read-only. Docker publish targets only the isolated local database created by the application; no arbitrary connection string for publish in V1.
- Defend against path traversal, symlink loops, SQL injection and shell injection; run CLIs with arguments via ProcessStartInfo.ArgumentList, with timeouts and resource limits.
- Do not store secrets from connection strings, keys, passwords or real IDs; generate container secrets locally and store/remove them securely.
- Reasonable responsiveness on large repositories, cancellable actions, a low-memory mode; incremental scan with hashes and bounded parallelism.
- Stable object identifiers, user confirmations preserved after rebuild; stale relations are re-checked and marked as conflicts.
- Port, CORS/Origin, CSRF and local-only binding are covered by tests.

## 14. Checks and testing

- Unit: ID inputs (including large lists, invalid lines, duplicates, quotes, Unicode, GUID), SQL escaping, deterministic rendering, schema diff, relation scoring, dependency graph.
- Integration: mini SQL projects; DacFx metadata; SQL Server in Docker with several databases and schemas; EF relationship mappings with nullable/composite keys and fluent mappings; missing FKs alongside text IDs.
- Snapshot/golden tests: expected T-SQL for different input types and strategies.
- Negative/security tests: malformed SQL, hostile file paths, invalid/missing tooling, cancellation, untrusted scripts.
- End-to-end UI: root picker → scan → graph → recipe → ID paste → SQL copy → schema edit → rebuild → broken recipe diagnostics → proposed safe fix.
- CI: .NET build/tests, `npm ci`, frontend typecheck/build, frontend E2E smoke where possible, Windows self-contained single-file publish and exe startup.

## 15. Phased implementation (the agent must follow this order)

### Phase 0 — toolchain research / technical spike

- Create the repository, solution and frontend shell.
- Verify building a test SDK-style `.sqlproj` into a DACPAC without the Visual Studio extension; verify a legacy SQL project and correct diagnostics when SSDT targets are missing.
- Extract the model via DacFx; verify the alternative with Docker SQL Server and SqlPackage.
- Test single-file publish, embedding UI assets, browser opening and local security.
- Record the matrix of actually supported SDK/tooling versions in the README.

**Done when:** a reproducible smoke demo works: build → DACPAC → metadata → JSON graph; a runnable EXE.

### Phase 1 — workspace discovery and schema catalog

Scanning folders and repositories; discovering projects and several databases; SQL Server provider; snapshots; object tree; errors/diagnostics; incremental refresh.

**Done when:** a folder with 2 SQL projects and a C# solution is correctly split into separate databases and tables, and updates are tracked.

### Phase 2 — relations and visual graph

Physical FK, Roslyn/EF mapping, rule-based logical candidate discovery, evidence/confidence, manual confirmation, graph UI, path finding and preview joins.

**Done when:** a table without FKs gets explainable candidates; the user confirms; the graph shows the source and the path; an unconfirmed join is not treated as authoritative without confirmation.

### Phase 3 — recipes and ID processor

Recipe DSL/SQL templates, typed parameters, deterministic parser, validation, generator for small/large IDs, Monaco UI, clipboard, recipe catalog.

**Done when:** 10 000 identifiers are processed without omissions, with a report and correct SQL; a recipe does not require a new file for every WHERE.

### Phase 4 — diff, impact analysis and safe auto-fix

Schema snapshot compare, dependency inference, recipe validation and status, repair proposals, preview+approval and rollback.

**Done when:** removing/renaming a field produces an exact warning for dependent recipes without breaking the others.

### Phase 5 — release hardening

Diagnostics, recovery, security tests, docs, packaging, fast startup, larger repos performance, CI release flow.

**Done when:** the application can be handed to another developer as a single EXE with a clear list of the external tools needed for specific features.

### Future (not in V1)

PostgreSQL provider (schema model, catalog adapter, SQL dialect, build/import pipeline), optional LLM/MCP integration only, an extended query builder, full automatic query generation from natural language, direct query execution against production databases.

## 16. How the coding agent executes tasks

1. For each phase, create small implementable issues/tasks with acceptance criteria; start with Phase 0, do not try to write everything at once.
2. Before every significant architectural decision, state the constraints and show the trade-off. Choose a practical minimal solution; avoid unnecessary services and libraries.
3. For every feature: code + unit/integration tests + documentation + reproducible commands.
4. Never declare a stage done without running the checks and demonstrating an end-to-end scenario. The report lists what was actually verified, what was not and why.
5. Maintain `docs/architecture.md`, `docs/schema-model.md`, `docs/recipe-format.md`, `docs/toolchain.md`, `docs/security.md` and a short changelog.
6. If it turns out that automatically building a particular kind of SQL project is impossible in the current environment, do not substitute fake data for the functionality; show a diagnostic and the alternative: import an existing DACPAC/static parse.

## 17. Acceptance criteria for the whole project

- The user runs **one exe** and gets a working UI on localhost; no separate frontend/backend launch is needed.
- They can choose a root folder with several SQL/C# repositories and see the different databases discovered, without schemas being mixed.
- The DACPAC build runs automatically when compatible tools are present; if the tools are missing, this is diagnosed, not hidden.
- When needed, the application starts an isolated SQL Server database in Docker for schema analysis, without connecting to a remote production database.
- It shows a graph of FKs, EF mappings and inferred logical relations with evidence and confidence.
- It lets the user save an SQL recipe, paste a large list of IDs and get exact, verifiable SQL without an LLM and without losing elements.
- After a schema update it distinguishes valid/broken/needs-review recipes and proposes only safe fixes, with a diff and confirmation.
- Quick Refresh correctly handles small changes without a full scan of the whole workspace.
- On build errors, unavailable Docker or a partially parseable project, the user gets clear diagnostics and working fallback features.

---

**Note on DACPAC:** a DACPAC is the build output of a SQL database project, not an EF Core feature. For an SDK-style `.sqlproj`, the build can run via a compatible `dotnet build` with the SQL build SDK. Older SSDT `.sqlproj` projects may require installed MSBuild/SSDT tooling; having the .NET runtime alone does not guarantee a build. `SqlPackage` is needed for publish/extract scenarios if that route is chosen, and may be shipped as a separately detected tool. The most important task of Phase 0 is to confirm the chosen options on real sample projects.
