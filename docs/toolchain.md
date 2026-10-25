# Toolchain V1

| Component | Verified version | Purpose |
|---|---|---|
| .NET SDK / target | 10.0.402 / net10.0 LTS | Development and SDK-style SQL build; runtime in the EXE |
| DacFx / ScriptDom | Microsoft.SqlServer.DacFx170.5.96 | DACPAC, SQL AST and offline schema DDL |
| Microsoft.Build.Sql | 2.3.0 | SDK SQL fixtures Commerce/Billing |
| Microsoft.Data.Sqlite | 10.0.13 | Embedded local storage |
| EFCore.Relational / Roslyn | 10.0.13 / 5.9.0 | Static C# metadata analysis only |
| Pkcs | 10.0.13 | Patched transitive dependency of DacFx |
| Node / npm | 22.23.4 / 10.9.8 | Frontend development only |
| React / TypeScript / Vite | 19.3.1 / 7.0.2 / 8.3.5 | Locked frontend build |
| React Flow / Monaco | 12.12.0 / 0.57.0 | Graph and local SQL editor/workers |
| playwright-core | 1.64.1 | Tests with installed Chrome, fresh headless profile |
| MSBuild + SSDT | VS18 Community + v18.0 targets | Real legacy SSDT build |
| Docker daemon | 29.7.2 | Optional temporary SQL Server2022 |
| SqlPackage | 170.5.96 | External Phase 0 publish/extract smoke; the app Docker route uses bundled DacFx + sqlcmd |

The application supports SDK-style Microsoft.Build.Sql and legacy SSDT import. The .NET runtime does not replace the SDK; a plain dotnet build is not enough for legacy projects. Legacy is ready only with MSBuild+SSDT from the same installation; otherwise diagnostics and an existing DACPAC/static fallback. A universal build of arbitrary references/targets is not promised.

Base commands and actual versions are pinned by global.json, csproj and package-lock.json. SDK-style: [Microsoft SQL projects CLI](https://learn.microsoft.com/en-us/sql/tools/visual-studio-code-extensions/sql-database-projects/build-database-project-from-command-line). Runtime: [Microsoft .NET support](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core). Packaging: [Microsoft single-file docs](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview).

## Checks

Phase 0 historically confirmed: Commerce/Billing SDK build, Legacy on real MSBuild+SSDT, 4 tables/13 columns/4 PK/1 composite FK in Docker and DacFx, 11 unit tests at the time, a self-contained EXE. That early EXE has been superseded by V1.

V1 acceptance scripts check workspace incremental/read-only/restart, EF nullable mapping/manual relation trust/multiple paths, Dapper literal SQL evidence, 10000-ID parser/render/privacy, snapshot diff/impact/approval/rollback/type changes, catalog migration/backup/recovery/one instance, a 5000-table bounded graph and a real Chrome workflow.

Generated SQL was executed in SQL Server 2022: 10 scenarios with exact values, including 10000 temp rows, one ADO.NET JSON parameter, hostile quotes/Unicode and cp1251/UTF8 VALUES/temp/JSON. The app's own container was removed. The browser workflow checks local assets/workers, clipboard, absence of runtime values in storage and schema edit → Broken → repair approval.

UI/npm audit: 0 vulnerabilities. .NET dependency audit: no vulnerable packages. Final results and timings: docs/verification.json / local work/*report.json. These timings apply to the current machine and are not a universal guarantee.

The CI Windows workflow includes build/tests/publish, published-EXE HTTP/Chrome regressions and artifact upload. Docker checks are opt-in locally: a Linux SQL container is not available as a regular service on a Windows hosted runner. Remote GitHub CI has not been run: no remote is configured.

Manual double-click default-browser launch and arbitrary client repositories are not automatic proof of support for every environment. CLI/real browser and the standalone EXE are checked by reproducible scripts.


The 1.1 enhancements are checked by scripts/Test-Enhancements.ps1 and an additional 22 Chrome workflows. scripts/Test-Clean.ps1 [-Docker] creates a clean copy of the source files with a SHA256 inventory, installs UI dependencies and runs build/test/single-EXE publish/Test-All. Pinned NuGet DACPAC references use a service restore; executable code and transitive package graphs require external verification. PackageReference support in SDK SQL projects was checked against Microsoft Learn: https://learn.microsoft.com/sql/tools/sql-database-projects/concepts/package-references . The GitHub workflow was extended with new unit/HTTP/UI checks; the actual remote run is recorded separately in verification.json.

