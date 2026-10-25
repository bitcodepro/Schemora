# Schemora

A local desktop tool for SQL Server. It builds a model of several databases from your repositories, shows how tables relate and where each relationship comes from, builds verifiable JOINs, and turns a list of IDs into ready-to-run SQL from a saved recipe. It never connects to production databases and sends nothing over the network.

One EXE for Windows x64: the server, the UI and the analyzers run in a single process on `127.0.0.1`. PostgreSQL and AI features are not supported yet.

## What it does

- **Workspaces.** Several root folders with SQL projects (SDK-style and legacy SSDT), loose `.sql` files, DACPACs and C# projects. Full Rescan discovers projects, Quick Refresh updates changed files, and auto-refresh watches the folders.
- **Schema catalog.** Tables, views, procedures, functions, synonyms, indexes and unique keys of the selected workspace; search in SQL definitions; snapshot history.
- **Relationships and paths.** Declared FKs, EF Core mappings, JOINs found in C#/Dapper, observed and inferred relationships — each with its evidence and confidence. An unverified relationship is never used for an automatic JOIN until you confirm it. Path Explorer builds JOINs from trusted relationships only.
- **SQL recipes.** Versioned templates with lists (`{{ids:Ids}}` / `{{table:Ids}}`) and single values (`{{value:From}}`, e.g. a from/to period); typed inputs (integers and decimal, GUID, strings, dates with "today / start of month…" shortcuts, bit); explicit duplicate and order policies. Paste IDs or load a file and get a standalone SQL script or a query with one JSON parameter. Any input error blocks generation. A single value can be optional: an empty field renders `NULL`. Frequently used values can be saved under a name — they are encrypted for your Windows account. Favourite and recent recipes, and one script for a whole recipe collection.
- **Schema changes.** Snapshot diffs, impact on recipes, rename candidates, and AST-based repairs with a preview and a separate confirmation. Compare two databases of a workspace, or a database with a DACPAC from another environment, with an HTML report and a review-only migration draft (data-losing steps are commented out; Schemora never runs it).
- **Workspace quality.** Configurable rules (tables without a PK, FKs without an index, duplicate indexes and more) with justified suppression of findings.
- **Search.** `Ctrl+K` searches pages, tables, columns, SQL code and recipes of the current workspace; `?` lists all keyboard shortcuts.
- **Relationship graph** with colouring by schema, compact cards, and PNG/SVG export.
- UI in English, Russian and Ukrainian; dark and light themes.

## Running

Start `artifacts/win-x64/Schemora.Host.exe` and a browser opens. No DLLs, Node or .NET runtime are needed next to the EXE. Data lives in `%LOCALAPPDATA%\Schemora`; one instance works with one data directory. Shutdown and backups are under Settings and tools.

To get started, press **Load tutorial project** on the Workspace page: it creates a sample workspace with a database, a relationship and a recipe.

### Port

The port is set in `schemora.json` **next to the EXE**:

~~~json
{
  "port": 0
}
~~~

`0` picks a free port automatically; set a fixed one, e.g. `8088`, if you prefer. If the port is taken, startup stops with a message naming the port and the JSON path. Without the file `0` is used; invalid JSON, unknown properties and values outside `0..65535` are rejected. `--port 8088` overrides the file and `--config <path>` selects another file. The server always listens on `127.0.0.1` only. Publishing again does not overwrite your `schemora.json`.

### Without a browser

~~~powershell
Schemora.Host.exe --no-browser --session-file <new-file> --data-dir <directory>
~~~

The session file contains a temporary token — do not commit it. A normal start never prints the token to stdout. If private permissions cannot be set on the chosen data directory, the token for reopening is not stored; the single-instance lock still works.

### SQL from the command line

A recipe exported with **Export JSON** can be rendered without the UI, even while the app is running:

~~~powershell
Schemora.Host.exe render --recipe orders.json --input From=2026-10-01 --input Ids=@ids.txt > orders.sql
Schemora.Host.exe render --recipe orders.json --input Ids=@ids.txt --mode parameterized --out orders.json
~~~

`--input Name=values` accepts the same text as the ID box; `@file` reads values from a file. Several `--recipe` options produce one script: each recipe is its own batch (`GO`), and an input reaches every recipe that uses its name. Parameterized mode prints JSON with the SQL and the parameter. Warnings go to stderr; exit code 0 means success, 1 a command-line error, 2 a recipe or input error. `render --help` shows the full syntax.

### Backups

A button in the settings downloads a consistent SQLite copy. In addition, the app copies the catalog to `backups\auto-*.sqlite` once a day and keeps the newest 7 copies; the interval and the number of copies are set in Workspace quality → Analysis, search and recovery → Catalog health and recovery. Saved recipe values are never part of the catalog or its copies. To restore while the app is stopped:

~~~powershell
Schemora.Host.exe --data-dir E:\SchemoraData --restore-backup E:\Backups\schemora.sqlite
~~~

The previous catalog is preserved before it is replaced, and a damaged copy is rejected. The v1→v2 migration also makes a backup.

## Typical workflow

1. Create a workspace with absolute paths to your repository roots; the selected workspace applies to every page.
2. **Full Rescan** discovers SQL/C# projects and databases. Static SQL parsing is always partial; for an exact model choose **Rebuild** and confirm that you trust the MSBuild targets (SDK-style projects build with `dotnet build`, legacy SSDT with a compatible Visual Studio MSBuild + SSDT pair). Without these tools you can still import a ready DACPAC or use partial static parsing.
3. On the relationships page review the evidence. With several databases, map C# projects to databases. A JOIN found in Dapper is a candidate; `splitOn` is not an FK.
4. Save a recipe, paste IDs and get SQL. SQL with your IDs is generated locally, never executed by the app, and never stored in the catalog, the logs or browser storage.
5. After a schema change, refresh the workspace: recipe statuses are recalculated, and the Changes page shows the diff and proposes repairs.

## Portable configuration

Export v2 carries recipes, manual relationships, decisions, project mappings and database aliases — without absolute paths or entered IDs (explicitly saved synthetic test values are included). To import, first choose the roots and scan, then map databases and projects and review the preview. Imported relationships need a separate confirmation; trust in MSBuild is never transferred.

## External tools

| Action | Requirements |
|---|---|
| Run, catalog, graph, recipes, reading DACPAC/SQL/C# | Windows x64 and a browser only |
| Building an SDK-style SQL project | .NET 10 SDK and the project's SQL SDK; restore may need network access |
| Building legacy SSDT | Visual Studio / Build Tools with a compatible MSBuild + SSDT |
| Schema preview in Docker (optional) | Docker Desktop with Linux containers and a SQL Server 2022 image |
| Development | .NET 10 SDK, Node 22.23.4 / npm 10.9.8, PowerShell 7 (`pwsh`) for the acceptance scripts |

The Docker preview accepts a schema-only DACPAC and runs only after explicit consent: it creates a temporary owned container without network or host ports, deploys the schema without pre/post scripts or data, reads the system catalog through `docker exec`, and removes its resources. The app never accepts arbitrary connection strings. After a crash, ownership labels let the next start clean up the container.

## Limitations

- Static SQL parsing does not run migrations or dynamic SQL and does not evaluate SQL project conditions; an exact model needs Rebuild or a DACPAC. Quick Refresh compares modification time and size — Full Rescan recomputes hashes.
- A working subset of `.gitignore` is supported; global Git excludes and the index are not used.
- EF mappings from dynamic helpers, shadow properties and unknown project symbols need review. Dapper analysis extracts constant SQL only and never runs C#.
- Neighbouring SQL ProjectReferences, local ArtifactReferences and plain imports are built from a verified staging copy with an immutable input manifest. Dynamic imports, property functions and unknown tasks are diagnosed and need an external build plus a DACPAC import.
- AST repairs support provable bindings in SELECT/CTE/subqueries/UNION; ambiguous expressions and DML are fixed by hand. A rename candidate does not prove business meaning.

## Development

~~~powershell
cd ui
npm ci --cache ../work/npm-cache
npm run format:check
npm test
npm run build
cd ..
dotnet build Schemora.slnx -c Release
dotnet test Schemora.slnx -c Release

pwsh -File scripts/Publish.ps1
pwsh -File scripts/Test-All.ps1                 # HTTP + Chrome acceptance against the published EXE
pwsh -File scripts/Test-GeneratedSql.ps1 -Run   # optional: a real SQL Server in a temporary container
pwsh -File scripts/Test-DockerSchema.ps1 -Run
~~~

- UI code is formatted with Prettier (`npm run format`). The i18n test fails when a `t('…')` string in the UI has no translation.
- Colours come from role tokens (`--bg-*`, `--border-*`, `--text-*`, `--accent*`, status and tint tokens) in `ui/src/styles.css`; the light theme redefines them in `ui/src/light.css`.
- Acceptance scripts accept `-Executable <path>` and `-SkipBuild`. `Test-All.ps1` keeps only the three latest runs of each kind in `work/`.
- SQL project builds run `dotnet` with `DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false`, so each data directory's own `DOTNET_CLI_HOME` does not append entries to the user PATH.

Documentation: [architecture](docs/architecture.md), [schema model](docs/schema-model.md), [recipe format](docs/recipe-format.md), [build tools](docs/toolchain.md), [security](docs/security.md), [implementation progress](docs/implementation-progress.md). Per-version details and limits are in the [CHANGELOG](CHANGELOG.md) and `docs/product-*.md`.
