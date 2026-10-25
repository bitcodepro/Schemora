# Build and acceptance scripts

All scripts target PowerShell 7 (`pwsh`). `Test-All.ps1` runs the Core and Ui group suites in parallel (4 at a time by default, `-Throttle`), writes a log for each suite and prints a summary; on failure it shows the tail of the log and keeps the logs in `work/test-all-<guid>`. Docker suites run one after another. Browser scenarios open the app via `ui/e2e/support.mjs`, which waits for the `data-app-ready` marker. Acceptance scripts accept `-Executable <path to EXE>` and `-SkipBuild`; each run writes reports and screenshots to `work/<kind>-<guid>`, and `Test-All.ps1` keeps the last three runs of each kind.

~~~powershell
pwsh -File scripts/Publish.ps1                     # UI + single-file EXE in artifacts/win-x64
pwsh -File scripts/Test-All.ps1                    # Core + Ui
pwsh -File scripts/Test-All.ps1 -Group Core        # HTTP regressions only
pwsh -File scripts/Test-All.ps1 -Group Ui          # browser suites only
pwsh -File scripts/Test-All.ps1 -Group Docker      # only with a real SQL Server in Docker
pwsh -File scripts/Test-All.ps1 -Docker            # everything together
pwsh -File scripts/Test-All.ps1 -Throttle 1         # one suite at a time, for a slow machine
~~~

## Core — HTTP regressions against the published EXE

| Script | What it checks |
|---|---|
| `Test-Smoke.ps1` | Copy of the single EXE: embedded resources, SDK and legacy SSDT build, sources left unchanged, API protection |
| `Test-Launch.ps1` | `schemora.json`, `--port`, `--config`, port in use, token, Origin and Host checks |
| `Test-Workspace.ps1` | Workspaces, Full Rescan and Quick Refresh, snapshots |
| `Test-Relationships.ps1` | FK, EF, observed and inferred relations, trusted paths |
| `Test-Dapper.ps1` | Extracting constant SQL from Dapper |
| `Test-Recipes.ps1` | Recipes, 10 000 IDs, no entered IDs in the data directory |
| `Test-Changes.ps1` | Snapshot diff, impact on recipes, repair with confirmation |
| `Test-Release.ps1` | Portable configuration and database mapping |
| `Test-Enhancements.ps1` | Manual relations, chain builder, object explorer, auto-refresh |
| `Test-ProductV13.ps1` | Portable configuration v2, recipe test values, C# compilation context |
| `Test-MaintenanceV13.ps1` | Graph index, history cleanup, anonymized diagnostics |
| `Test-Authoring.ps1` | Recipe drafts, version comparison and restore |
| `Test-Professional.ps1` | Quality, analysis freshness, search, recipe collections |
| `Test-Hardening.ps1` | Build receipts, managed files, quality rules, SQL search |
| `Test-Scale.ps1` | 5 000 synthetic tables |

## Ui — Chrome E2E (`ui/e2e/*.mjs`)

`Test-Ui.ps1` (smoke + enhancements), `Test-Localization.ps1` (EN/RU/UK and language persistence after restart), `Test-ProductV13Ui.ps1`, `Test-Experience.ps1` (sample project, navigator, documentation, Ctrl+K), `Test-ProfessionalUi.ps1`, `Test-HardeningUi.ps1` (keyboard, 200 % zoom, forced colors).

## Docker — a real SQL Server in temporary containers

`Test-GeneratedSql.ps1 -Run` executes the generated SQL of all strategies, `Test-DockerSchema.ps1 -Run` deploys a DACPAC and reads the catalog, `Test-DockerFailure.ps1 -Run` checks the publish failure log.

## Manual

- `Test-Docker.ps1 -Run` — the original DacFx and SqlPackage check on a synthetic project.
- `Test-Clean.ps1` — build and tests from a clean copy of the sources (`-Docker` adds the Docker group).
- `Test-CleanWindowsRuntime.ps1` — runs the EXE on a clean Windows machine without development tools; refuses to run on a developer machine.
