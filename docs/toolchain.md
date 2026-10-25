# Toolchain V1

| Компонент | Проверенная версия | Назначение |
|---|---|---|
| .NET SDK / target | 10.0.402 / net10.0 LTS | Разработка и SDK-style SQL build; runtime в EXE |
| DacFx / ScriptDom | Microsoft.SqlServer.DacFx170.5.96 | DACPAC, SQL AST и offline schema DDL |
| Microsoft.Build.Sql | 2.3.0 | SDK SQL fixtures Commerce/Billing |
| Microsoft.Data.Sqlite | 10.0.13 | Embedded local storage |
| EFCore.Relational / Roslyn | 10.0.13 / 5.9.0 | Только static C# metadata analysis |
| Pkcs | 10.0.13 | Исправленная transitive dependency DacFx |
| Node / npm | 22.23.4 / 10.9.8 | Только frontend development |
| React / TypeScript / Vite | 19.3.1 / 7.0.2 / 8.3.5 | Locked frontend build |
| React Flow / Monaco | 12.12.0 / 0.57.0 | Graph и локальный SQL editor/workers |
| playwright-core | 1.64.1 | Тесты с установленным Chrome, fresh headless profile |
| MSBuild + SSDT | VS18 Community + v18.0 targets | Реальная legacySSDT сборка |
| Docker daemon | 29.7.2 | Optional temporary SQL Server2022 |
| SqlPackage | 170.5.96 | Внешний Phase0 publish/extract smoke; app Docker route использует bundledDacFx+sqlcmd |

Приложение поддерживает SDK-style Microsoft.Build.Sql и legacy SSDT import. .NET runtime не заменяет SDK; обычный dotnet build legacy недостаточен. Legacy готов только при MSBuild+SSDT одной установки; иначе diagnostics и existingDACPAC/staticfallback. Не обещается универсальная сборка arbitrary references/targets.

Базовые команды и фактические версии фиксируются global.json, csproj и package-lock.json. SDK-style: [Microsoft SQL projects CLI](https://learn.microsoft.com/en-us/sql/tools/visual-studio-code-extensions/sql-database-projects/build-database-project-from-command-line). Runtime: [Microsoft .NET support](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core). Упаковка: [Microsoft single-file docs](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview).

## Проверки

Phase0 исторически подтвердил: Commerce/Billing SDKbuild, Legacy на реальном MSBuild+SSDT,4tables13columns4PK/1compositeFK в Docker и DacFx,11тогдашнихunit tests, self-contained EXE. Тот ранний EXE заменён V1.

V1 acceptance scripts проверяют workspace incremental/read-only/restart, EFnullable mapping/manual relation trust/multiple paths, Dapper literalSQL evidence,10000ID parser/render/privacy, snapshotdiff/impact/approval/rollback/typechanges, catalog migration/backup/recovery/oneinstance,5000table boundedgraph и реальный Chrome workflow.

Generated SQL выполнен в SQLServer2022:10 сценариев с exact values, включая10000temp rows, oneADO.NETJSONparameter, hostilequotes/Unicode и cp1251/UTF8 VALUES/temp/JSON. Собственный контейнер удалён. Browser workflow проверяет локальные assets/workers, clipboard, отсутствие runtime values в storage и schemaedit→Broken→repairapproval.

UI/npm audit:0 vulnerabilities. .NET dependency audit: no vulnerable packages. Итоговые результаты и timings — docs/verification.json / локальные work/*report.json. Эти timings относятся к текущей машине, не универсальная гарантия.

CI Windows workflow включает build/tests/publish, publishedEXE HTTP/Chrome regressions и upload artifact. Docker проверки opt-in локально: Linux SQLcontainer недоступен как обычный сервис на Windows hostedrunner. Удалённый GitHub CI не запускался: remote не настроен.

Manual double-click default-browser launch и произвольные клиентские repositories не являются автоматическим доказательством поддержки всех окружений. CLI/real browser и standaloneEXE проверяются воспроизводимыми scripts.


Расширения 1.1 проверяются scripts/Test-Enhancements.ps1 и дополнительными22 Chrome workflows. scripts/Test-Clean.ps1 [-Docker] создаёт чистую копию source files с SHA256 inventory, устанавливает UI dependencies и выполняет build/test/singleEXE publish/Test-All. Для pinned NuGet DACPAC references используется служебный restore; executable code и transitive package graphs требуют внешней верификации. Поддержка PackageReference в SDK SQL projects сверена с Microsoft Learn: https://learn.microsoft.com/sql/tools/sql-database-projects/concepts/package-references . GitHub workflow дополнен новыми unit/HTTP/UI checks; фактический remote run отдельно отражается в verification.json.

