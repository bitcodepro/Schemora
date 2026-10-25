# Synthetic fixtures

These projects contain invented schema definitions only. They have no connection strings, business data, custom MSBuild tasks, or deployment scripts. Fixture projects are trusted test input; general repository builds still require explicit trust.

| Folder | Purpose |
| --- | --- |
| `Commerce` | SDK-style `Microsoft.Build.Sql` 2.2.0, SQL Server 2022 target; four tables in `dbo` and `sales`, duplicate short table names, composite PK/FK, nullable text IDs without a guessed FK. |
| `Billing` | Separate SDK-style database with two tables and a physical FK; `dbo.Customers` must remain separate from Commerce. |
| `Legacy` | Original SSDT SQL project, unchanged project format; requires compatible MSBuild/SSDT. `SQLDBExtensionsRefPath` can select an installed SSDT directory. Missing targets must produce a useful diagnostic. |
| `LooseSql` | Separate DDL, query, and seed files. Discovery must not treat every `.sql` file as DDL, and must never execute the seed file. Static import is outside Phase 0. |
| `MappingSample` | Compilable C# project with standard table/column/key attributes for later discovery/mapping work. |

Run from the repository root in PowerShell:

```powershell
dotnet build tests/Fixtures/Commerce/Commerce.sqlproj -c Release
dotnet build tests/Fixtures/Billing/Billing.sqlproj -c Release
dotnet build tests/Fixtures/MappingSample/MappingSample.csproj -c Release
dotnet test tests/Schemora.SqlServer.Tests/Schemora.SqlServer.Tests.csproj -c Release
```

DACPAC output locations:

- `tests/Fixtures/Commerce/bin/Release/Commerce.dacpac`
- `tests/Fixtures/Billing/bin/Release/Billing.dacpac`

For legacy projects, use the compatible full MSBuild discovered by Schemora. Calling `dotnet build` without SSDT is expected to fail with MSB4019; this demonstrates the diagnostic case, not a successful legacy build.

The metadata tests create real DACPACs through DacFx from the same fixture SQL and delete only their own generated test artifacts. The SDK builds above separately prove the SQL project -> DACPAC toolchain path.
