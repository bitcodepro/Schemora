# SQL Server Docker smoke (Phase 0)

This is an opt-in development test of the alternative live-catalog route. Normal DACPAC import does not require Docker or SqlPackage. The script builds **only** the checked-in synthetic Commerce fixture; it does not accept an arbitrary connection string or user database target.

```powershell
pwsh -NoProfile -File scripts/Test-Docker.ps1 -Run
# If Docker Desktop is not on PATH:
pwsh -NoProfile -File scripts/Test-Docker.ps1 -Run -DockerPath "$env:LOCALAPPDATA\Programs\DockerDesktop\resources\bin\docker.exe"
```

Prerequisites: PowerShell 7, compatible .NET SDK, a running Linux Docker engine, enough memory for a 3 GiB container, and first-run access to NuGet / Microsoft Container Registry when dependencies are not cached. The script installs Microsoft.SqlPackage **170.5.96** locally under `work/tools`, without changing global tools. The version is pinned from the [Microsoft-owned NuGet package](https://www.nuget.org/packages/Microsoft.SqlPackage/170.5.96).

The test:

1. Builds `tests/Fixtures/Commerce/Commerce.sqlproj` and rejects DACPAC archives containing pre/post-deployment script entries or data payloads.
2. Creates a unique `schemora-smoke-<random>` SQL Server 2022 Developer container with an invocation-specific ownership label, no host mounts, and `127.0.0.1:<ephemeral port>` publication.
3. Generates a random SA password in memory. Docker receives it through the process environment. SqlPackage receives it through a temporary publish profile in a directory restricted to the current Windows user (mode 0700 on Unix). The profile is deleted immediately after publish and its directory is removed in `finally`.
4. Publishes the schema with both `IgnorePreDeployScript` and `IgnorePostDeployScript` enabled, as documented by [Microsoft](https://learn.microsoft.com/en-us/sql/tools/sqlpackage/sqlpackage-publish).
5. Reads `sys.tables`, `sys.schemas`, `sys.columns`, `sys.key_constraints`, `sys.foreign_keys` and `sys.foreign_key_columns`; verifies 4 tables, 13 columns, 4 PKs and one composite FK with ordered `TenantId`, `CustomerId` columns. Schema-qualified names distinguish `dbo.Customers` and `sales.Customers`.
6. Checks that the exact container still has this invocation's ownership label before `docker rm --force --volumes`. Existing containers are neither stopped nor removed.
7. Writes a non-secret result to `work/docker-report.json`, including actual Docker, SqlPackage, SQL Server versions, image ID, DACPAC hash and cleanup status. A failure returns a nonzero exit status.

The image uses the standard Docker bridge; this fixture test is not a sandbox for untrusted SQL. Its only accepted source is the synthetic repository fixture. `2022-latest` can change; the report records the exact image ID used. No fixture rows are inserted, no remote databases are contacted, and no permanent SQL container is required by the app.

## Recorded run

Passed on 2026-10-22 (Windows x64): .NET SDK 10.0.402, Docker 29.7.2, SqlPackage 170.5.96.0, SQL Server 16.0.4265.3. Commerce built and published; the live catalog returned 4 tables, 13 columns, 4 PKs and 1 composite FK with the correct 2-column order. Both same-named Customers tables remained distinct by schema. Port binding was verified as loopback-only. The owned container and restricted credential directory were removed; a separate post-run check found no smoke containers or credential directories remaining.

Negative checks also passed: predeploy.sql, postdeploy.sql, Data/.../*.bcp and data.xml entries were rejected; a schema-only archive was accepted; invoking the script without -Run was rejected. The first real run exposed an incompatible sqlcmd output option combination; after correcting it, the complete smoke passed. The failure path also successfully removed its owned container and credential files.

The tested image ID was: `sha256:ba4c8329f48fb8f02e1416be6a930ebfd71268caee78aa985f3af4315e457c89`. Consult `work/docker-report.json` for the latest local result; it is deliberately ignored with the rest of `work/`.
