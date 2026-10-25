# Phase 1

Workspace roots and exclusion patterns are persisted in a versioned SQLite catalog. Discovery identifies nested repositories/solutions/SQL and C# projects, skips links/build output, honors per-directory .gitignore and user patterns, and caches file fingerprints. Known ORM configuration paths are indexed without reading their contents. A full rescan re-hashes files; quick refresh reuses matching length/write stamps and schema snapshots.

Each SQL project/DACPAC has a separate stable database identity scoped to its workspace and source path. Same-named objects do not merge across databases. Loose DDL is grouped conservatively by source directory and marked for mapping review. Source classification uses ScriptDom, with parse diagnostics retained; queries/seed scripts do not create tables. Read-only fallback is explicitly partial. Rebuild uses Phase0 trusted toolchain routes.

SQLite stores immutable-history schema snapshots plus source/project/repository/object/column/relation catalog entries. A successful scan publishes catalog replacement and new snapshots in one transaction. Old snapshots survive source deletion; current derived catalog entries do not. Per-workspace jobs are serialized through a bounded queue, expose progress/SSE/cancellation, and interrupted jobs are marked after restart.

Acceptance command: `pwsh -File scripts/Test-Workspace.ps1`. Unit coverage includes ScriptDom table/key extraction, malformed/ambiguous/external definitions, discovery/gitignore/link handling, incremental edits and SQLite rollback/persistence/isolation. This phase does not execute repository code during quick/full discovery.

Git index/global ignore/.git/info/exclude and POSIX character classes are outside the discovery matcher; unsupported patterns are diagnosed. Static fallback does not replay migrations or evaluate project conditions, so an authoritative model requires a compatible build/DACPAC. Parent build configuration with unsupported imports remains explicitly diagnosed.
