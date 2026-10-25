# Phase 5 — release hardening

Acceptance tasks:
- Local-only token/Host/Origin/CSP protection; ArgumentList/timeout/tree cancellation; invalid paths/packages/scripts; no runtime ID persistence.
- One writer per data-dir, interrupted-job recovery, SQLite v1→v2 backup migration, backup/download and validated CLI restore with previous-catalog preservation.
- Optimistic version/snapshot/relationship-context guards for save/render/approval; rejection and archive cannot be overwritten by stale work.
- Portable metadata/recipe configuration with explicit database mapping; dark/light theme, keyboard save/render and local Monaco workers.
- Large synthetic catalog with bounded UI graph; single-file release; reproducible unit/HTTP/browser/Docker tests and Windows CI workflow.

Implemented checks:
- Workspaces29/29 and Host27/27 in the final Release pass, including migration/corrupt recovery and controlled concurrency barriers.
- HTTP Release11/11: portable import/missing mapping, SQLite backup, no second host for same data-dir, graceful shutdown/tokenfile removal, CLI restore/previous backup.
- Chrome14/14: workspace→scan→FK graph/path→typedrecipe→10000ID paste/render/copy→schemaedit→Broken→ASTpreview→approval→history/theme/privacy/consent.
- Initial 5000table measurement: startup556ms, scan3644ms, graph1155ms/150nodes, unchangedQuick161ms,162.3MiB working set. PublishedEXE measurements supersede these in verification.json.
- Actual generatedSQL Docker10/10 with exact values, including cp1251/UTF8 and actual oneJSONparameter. Owned container removed.
- App Docker uses networknone/nohostports/mounts, offlineDacFx plus dockerexec/syscatalog; ownership markers and bounded recovery. Final integration12/12 passed on publishedEXE; recorded in verification.json.

Release sequence:
~~~powershell
dotnet test Schemora.slnx -c Release
pwsh -File scripts/Publish.ps1
pwsh -File scripts/Test-All.ps1 -Docker
~~~

All selected tests must pass before release is called ready. Final artifact SHA256/size/count and individual reports belong to docs/verification.json. Remote GitHub CI is configured, not claimed as executed. Manual default-browser double-click and arbitrary client repositories remain environment-dependent.


