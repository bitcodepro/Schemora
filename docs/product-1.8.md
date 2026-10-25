# Workspace experience — 1.8

1.8 reorganises the interface around one selected workspace and adds a few opt-in conveniences. Schema analysis, relationship trust, recipe rendering contracts and privacy rules from 1.7 are unchanged unless stated here.

## One selected workspace

The sidebar selector (topbar below 800 px) is the only workspace control; every page follows it and the choice is remembered in browser storage. A recipe with unsaved edits asks before the workspace changes, as switching inside the recipe page did before. Paths under the selected workspace's roots are shown relative to them; tooltips and copied source locations keep the absolute path. The schema catalog opens the selected workspace's current model and lists its databases that have a snapshot; a DACPAC opened by path stays open until the workspace changes.

## Search

Ctrl+K (Cmd+K) or the topbar Search button opens a palette over the existing workspace search: pages, tables, columns, SQL definitions and recipes of the selected workspace, 20 results at a time. It runs only after two characters and reads the same stored metadata as the workspace tools search; it does not search runtime IDs, live databases or files outside the catalog. Inside the SQL editor Ctrl+K stays Monaco's chord prefix.

## Graph layout

Without a focus table, each connected component is laid out in layers along the FK direction: referencing tables left of the tables they reference. Cycles are broken by a deterministic depth-first order, and two barycentre passes order each layer to reduce crossings. With a focus table the 1.7 neighbourhood layout is kept. Saved positions still take precedence; the layout is deterministic for the same tables and relations. Choosing a JOIN path dims other edges and fits the path's tables into view.

## IN list strategy

`list` is an explicit SQL strategy, chosen per render or per input. `{{ids:...}}` becomes a literal list: `int`/`bigint` as plain numbers, other types with the same typed `CONVERT` and collation as the rowset strategies. A `{{table:...}}` use of the same input still receives a `VALUES` rowset. The strategy refuses more than 1000 values (`LIST_STRATEGY_LIMIT`), because long literal lists slow plan compilation and can reach SQL Server expression limits. It does not expose `[Ordinal]`, so the ordering warning is omitted. `auto` still selects `values` or `temp`, so existing regression fixtures keep their SQL hashes.

## Tutorial

Loading the tutorial from the interface starts a Rebuild of the generated tutorial SQL project when the diagnostics report an available .NET SDK. The project is written by Schemora into its own data directory, so no project-trust confirmation is requested for it. The build restores `Microsoft.Build.Sql` from NuGet and can need network access; if it fails, the partial static model stays and the workspace page shows the job error. Creating the tutorial through `POST /api/tutorial` alone does not build it.

## Translations

A UI test fails when a literal `t()` key or a server `AppException`/`RecipeEngineException` message has no catalog entry; launch-configuration errors (`LAUNCH_*`) print to the console and are excluded. Server prose is still translated by matching catalog text, not by message codes.

## Maintenance

UI code is formatted with Prettier and C# with CSharpier (pinned in `dotnet-tools.json`, checked in CI; `tests/Fixtures` is excluded because analyzer tests assert line numbers there). Shared surfaces use `--bg-*`, `--border-*`, `--text-*` and `--accent*` tokens that the light theme redefines; most component colours are still literal. `Test-All.ps1 -Group Core|Ui|Docker` selects suites and keeps the newest three `work/` runs per kind. SDK builds pass `DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false`, so a private `DOTNET_CLI_HOME` no longer appends to the user's PATH.
