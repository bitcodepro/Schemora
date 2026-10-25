# Скрипты сборки и приёмки

Все скрипты рассчитаны на PowerShell 7 (`pwsh`). `Test-All.ps1` запускает наборы групп Core и Ui параллельно (по умолчанию по 4, `-Throttle`), пишет лог каждого набора и печатает сводку; при ошибке показывает хвост лога и оставляет логи в `work/test-all-<guid>`. Docker-наборы идут по очереди. Браузерные сценарии открывают приложение через `ui/e2e/support.mjs`, который ждёт признак `data-app-ready`. Приёмочные принимают `-Executable <путь к EXE>` и `-SkipBuild`; каждый прогон пишет отчёты и скриншоты в `work/<вид>-<guid>`, `Test-All.ps1` оставляет три последних прогона каждого вида.

~~~powershell
pwsh -File scripts/Publish.ps1                     # UI + single-file EXE в artifacts/win-x64
pwsh -File scripts/Test-All.ps1                    # Core + Ui
pwsh -File scripts/Test-All.ps1 -Group Core        # только HTTP-регрессии
pwsh -File scripts/Test-All.ps1 -Group Ui          # только браузерные
pwsh -File scripts/Test-All.ps1 -Group Docker      # только с настоящим SQL Server в Docker
pwsh -File scripts/Test-All.ps1 -Docker            # всё вместе
pwsh -File scripts/Test-All.ps1 -Throttle 1         # по одному набору, если машина слабая
~~~

## Core — HTTP-регрессии против опубликованного EXE

| Скрипт | Что проверяет |
|---|---|
| `Test-Smoke.ps1` | Копия одного EXE: встроенные ресурсы, сборка SDK и legacy SSDT, неизменность исходников, защита API |
| `Test-Launch.ps1` | `schemora.json`, `--port`, `--config`, занятый порт, токен, проверка Origin и Host |
| `Test-Workspace.ps1` | Рабочие области, Full Rescan и Quick Refresh, снимки |
| `Test-Relationships.ps1` | FK, EF, наблюдаемые и предполагаемые связи, доверенные пути |
| `Test-Dapper.ps1` | Извлечение константного SQL из Dapper |
| `Test-Recipes.ps1` | Рецепты, 10 000 ID, отсутствие введённых ID в каталоге данных |
| `Test-Changes.ps1` | Diff снимков, влияние на рецепты, исправление с подтверждением |
| `Test-Release.ps1` | Переносимая конфигурация и сопоставление БД |
| `Test-Enhancements.ps1` | Ручные связи, конструктор цепочек, обозреватель объектов, автообновление |
| `Test-ProductV13.ps1` | Переносимая конфигурация v2, тестовые значения рецептов, контекст компиляции C# |
| `Test-MaintenanceV13.ps1` | Индекс графа, очистка истории, обезличенная диагностика |
| `Test-Authoring.ps1` | Черновики рецептов, сравнение и восстановление версий |
| `Test-Professional.ps1` | Качество, свежесть анализа, поиск, коллекции рецептов |
| `Test-Hardening.ps1` | Квитанции сборки, управляемые файлы, правила качества, поиск по SQL |
| `Test-Scale.ps1` | 5 000 синтетических таблиц |

## Ui — Chrome E2E (`ui/e2e/*.mjs`)

`Test-Ui.ps1` (smoke + enhancements), `Test-Localization.ps1` (EN/RU/UK и сохранение языка после перезапуска), `Test-ProductV13Ui.ps1`, `Test-Experience.ps1` (учебный проект, навигатор, документация, Ctrl+K), `Test-ProfessionalUi.ps1`, `Test-HardeningUi.ps1` (клавиатура, масштаб 200 %, forced colors).

## Docker — настоящий SQL Server во временных контейнерах

`Test-GeneratedSql.ps1 -Run` выполняет сгенерированный SQL всех стратегий, `Test-DockerSchema.ps1 -Run` разворачивает DACPAC и читает каталог, `Test-DockerFailure.ps1 -Run` проверяет журнал сбоев публикации.

## Вручную

- `Test-Docker.ps1 -Run` — исходная проверка DacFx и SqlPackage на синтетическом проекте.
- `Test-Clean.ps1` — сборка и тесты из чистой копии исходников (`-Docker` добавляет Docker-группу).
- `Test-CleanWindowsRuntime.ps1` — запуск EXE на чистой Windows без инструментов разработки; на машине разработчика отказывается работать.
