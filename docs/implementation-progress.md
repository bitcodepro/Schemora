# Implementation progress — 1.7

All six agreed hardening directions are implemented: redacted Docker failure diagnostics, guarded managed-file cleanup, configurable quality checks, SQL-definition search/navigation, actual build toolchain receipts and accessibility improvements. See product-1.7.md for scope and limits. The tutorial SQL batch issue and scan queue completion/startup races discovered during verification were fixed.

Passed: 338 .NET tests (136 final host), 27 UI unit checks, 79 browser workflows, 203 HTTP checks, 27 launch/configuration/security checks, 24 ordinary Docker schema/recovery checks, 10 exact generated-SQL cases and 5 explicitly injected Docker failure checks. Final acceptance suites used the same delivered EXE. A strict ambiguous browser locator stopped one runner invocation; the locator was scoped to analysis coverage and the affected/remaining suites then passed on the same artifact. This is composed suite coverage, not a claim that the stopped runner returned success.

EXE SHA256: 2410DE7DD7F8CD1626CC8C101808ECD16F8B516C2D82D76EF0444514EAC20E77. Archive entries were hash verified. Manual Narrator/NVDA, clean Windows, remote CI and a fresh source-copy build were not performed. The unreproduced 1.6 Docker incident still has no established root cause; new diagnostics are verified prospectively. Temporary synthetic run directories were cleaned after preserving reports/evidence. PostgreSQL and AI remain deferred.

# Implementation progress — 1.6

All six agreed professional-workspace directions are implemented with explicit limits: quality review, atomic analysis receipts, unified search, recipe collections/batch comparison, differential dependency projections and guarded restore/restart/rollback. See product-1.6.md.

Passed: 332 .NET tests (final host 130), 27 UI unit checks, 66 browser workflows across staged builds (7 new workflows repeated on final artifact), 184 HTTP checks, 27 launch/configuration/security checks, 24 final Docker schema/recovery checks and 10 exact generated-SQL cases. Synthetic 5000-table dependency index: cold 439 ms, warm 83 ms, zero database refreshes on the warm query. Actual restore and rollback each restarted the executable in isolated test data directories.

EXE SHA256: 834D44D892C4B40E8F313D886B28A9EA45814C46843E6EB5B7334AEB29BC9868. Archive payload hashes verified. The initial Docker schema deployment failure was not reproduced in independent/diagnostic/ordinary repeats; its cause remains unknown and is recorded in verification.json. No fresh Windows, remote CI or clean-source-copy run is claimed. PostgreSQL and AI remain deferred.

# Implementation progress — 1.5

All six agreed application improvements are implemented. See product-1.5.md for behavior, privacy boundaries and limits.

Passed: 328 .NET tests (final host suite 126 repeated), 27 UI unit checks, 59 Chrome workflows, 160 HTTP checks and 27 launch/configuration/security checks. SDK-style and Legacy SSDT/toolchain smoke passed. Synthetic scale: 5000 tables and 1000 recipes; the 1000-recipe dependency page returned 28 nodes/27 edges in 94 ms on this run. Draft recovery passed an actual host restart with a changed origin.

Release EXE SHA256: E763AD611F55993825E41C751C09AFC5D94DF25793B6507099E2D01AB6DCC23E. Archive entries were hash-verified. This release uses an ordinary local source build; no new clean-source-copy, Docker, fresh-Windows or remote-CI result is claimed. PostgreSQL and optional AI remain deferred.

# Implementation progress — 1.4

The five requested product features are implemented: guided tutorial, hierarchical navigation/saved filters, clickable recipe dependencies, typed Dapper/ADO.NET examples and standalone HTML documentation. New controls support EN/RU/UK. Keyboard focus and CSS scaling at 200% passed Chrome acceptance. See product-1.4.md for exact behavior and limits.

Release verification: clean source build; 322 .NET Release tests; 24 UI unit checks; 49 Chrome workflows; 155 non-Docker HTTP checks; 27 launch/security checks; 24 Docker schema/recovery checks and 10 exact generated-SQL cases. The local runtime script passed 4 checks with cleanMachine=false and correctly rejected this development host when clean-machine mode was required. Archive payload hashes were verified.

External checks remain pending: no remote is configured and connected GitHub search found no Schemora repository; a target repository URL is needed to run Actions. Windows Sandbox is absent and Hyper-V inventory access is denied by Windows permissions, so no fresh-Windows success is claimed. A safe same-workflow artifact CI job and strict local runtime harness are prepared. The rejected arbitrary-URL/self-hosted design was discarded.

EXE SHA256: 9C61EF8C789479602AA1C1BAA692D135B52792A8CBBC18D9E2C1B82EF9AED368. Clean source: work/clean-97943529c4954ebcaefc2564629c9ef3/source. Complete evidence: verification.json. PostgreSQL and optional AI remain deferred.

# Previous 1.3 verification

All six improvements from the product audit are implemented and verified together. The supported behavior and conservative limits are documented in product-1.3.md. PostgreSQL and optional AI remain deferred. Pilot repositories are synthetic, as requested.

- Portable configuration v2 transfers recipes, explicitly saved artificial regression fixtures, manual composite relationships, confirmations/rejections, project/database mappings, safe compilation selections and aliases. Explicit mapping and a manifest/context-bound preview precede import. Imported relationship assertions require individual current-schema review; project execution trust is never transferred. V1 imports remain supported.
- Artificial recipe baselines store count, duplicate/order policies, values/ordinals, strategy and SQL/parameter contract expectations. Save/revalidation reruns them; malformed imported fixtures produce bounded failed runs. Runtime generation values are never copied into persisted fixtures.
- Revisioned SQLite projections serve bounded graph fragments, paged relations and projected SQL-object pages. Default low-memory mode avoids retaining a full relationship graph. Explicit history cleanup previews protect current/compatible/referenced snapshots and recipe history, block active jobs and create a backup before deleting eligible records. Full inference still needs transient graph memory.
- Selected C# configuration, framework, symbols, membership, language version and available references feed EF/Dapper. Optional trusted MSBuild evaluation is explicit; cached evaluation is content-guarded. Graph browsing and watcher refresh never start MSBuild. Unsupported SDK/generator/package/reference compilation remains partial and cannot confer automatic EF trust.
- Explicit old table/schema to selected current table mapping supports conservative aliased SELECT repair. Preview and approval preserve recipe/schema/context guards and create a new version. Ambiguous or unsupported queries require manual repair.
- API errors retain codes and operation IDs. Rotating sanitized application/job diagnostics and optional-path support reports exclude tokens, SQL, runtime IDs and exception messages. Source evidence supports copy/open with local workspace path validation and argument-list execution.
- All new screens support EN/RU/UK. English remains the fresh-install default; existing language preference and external schemora.json behavior are preserved.

Final clean verification on this Windows host: 320 .NET Release tests, 24 UI unit checks, 155 non-Docker HTTP checks, 27 launch configuration/security checks, 43 real Chrome checks (22 existing workflows + 11 locale/restart + 10 new product workflows), 24 Docker schema/recovery checks and 10 exact-value generated-SQL cases. SDK-style and Legacy SSDT actually built. npm audit reported zero vulnerabilities. Owned test Docker resources were removed. No fresh-OS or remote CI execution is claimed.

5000-table measurement: startup 1199 ms, scan 7509 ms, bounded graph/index 1642 ms (150 nodes), unchanged Quick 174 ms, working set 340.6 MiB. Timings vary by machine, cache and load. The renderer's 10000-ID cases also passed exact-row Docker verification.

Published 1.3.0 EXE: 78282231 bytes. SHA256: 116D4ACEB687511C271E9EE4DF6064DBB41CF82CC0D51991DE657E5F4EF5664D. Release archive contains exactly the executable and default schemora.json; each archive entry was hash-verified. Existing customized sidecars survive updates. Full evidence is in verification.json.

Reproduce: scripts/Test-Clean.ps1 -Docker. Test-All.ps1 includes ProductV13, MaintenanceV13 and ProductV13Ui acceptance suites. Clean verified source: work/clean-54cf796ef87942dd931b96551a550177/source. The source inventory matched the working sources before final report/documentation updates.
