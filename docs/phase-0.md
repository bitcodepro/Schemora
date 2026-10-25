# Phase 0 — tasks and acceptance

| ID | Task | Actually verified |
|---|---|---|
| P0-01 | Skeleton | Solution, React/TS/Vite, Host, SQL module; build with no warnings/errors |
| P0-02 | SDK build | Commerce/Billing .sqlproj → DACPAC; Commerce also via the API from the single EXE |
| P0-03 | Legacy route | Found MSBuild 18 + SSDT from the same VS18 installation; Legacy.sqlproj → DACPAC → dbo.LegacyItems via the API |
| P0-04 | Metadata | DacFx → tables/types/nullable/PK/ordered composite FK → JSON; 7 tests |
| P0-05 | Docker spike | SqlPackage → isolated SQL Server → sys catalog (4 tables/13 columns/4 PK/1 composite FK) → cleanup |
| P0-06 | Packaging | One self-contained win-x64 EXE, 65 949 569 bytes; verified a copy of the EXE alone, without neighbouring assets/DLLs |
| P0-07 | Local API | Token, Host/Origin, cross-site rejection, trust; sources unchanged; stale DACPAC not selected; 4 process lifecycle tests |
| P0-08 | Docs | Toolchain/architecture/model/security/recipe boundary, smoke commands, changelog and CI workflow |

All 11 unit/regression tests passed. npm audit and NuGet audit: no known vulnerabilities found in the current sources. Local end-to-end scenarios build → DACPAC → DacFx JSON and Docker live catalog passed. EXE SHA256: `3B314D714FBC455C5048BE68A39133F1DBF5BE18897168BF49751F4F26F49875`.

Not verified: visual browser interaction/layout and automatic browser opening on double-click (no browser automation connected); remote CI (no GitHub remote). The CI workflow is prepared; a successful remote run is not claimed.

The legacy fallback for a missing compatible toolchain is implemented with code SSDT_TOOLCHAIN_MISSING and DACPAC import; both components are present in the current environment. It was separately reproduced that the dotnet SDK alone does not find SSDT targets for legacy projects.

Spike boundaries: inherited props/targets with external imports and sibling project references produce a diagnostic/build error; such projects can be analysed via a prebuilt DACPAC. Arbitrary build targets remain trusted executable code.

Next phase: workspace discovery, a multi-database catalog, SQLite snapshots and incremental refresh. The large interactive graph and logical relations are Phase 2; recipes are Phase 3.
