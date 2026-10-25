# Professional workspace tools — 1.6

The six agreed directions are available in Workspace quality. English remains the default; the new screens support Russian and Ukrainian. PostgreSQL, optional AI, GitHub and fresh Windows remain outside this scope.

## Quality review

Findings cover missing primary keys, unavailable/stale database models, unresolved object references, analyzer warnings/errors and untrusted relationship candidates. Missing primary keys are advisory, not an instruction to change the schema. Users can filter severity, page findings, inspect evidence and navigate to the available source/project location. Table metadata does not invent a precise source line when one is unavailable.

Marking a finding reviewed requires a 10–2000 character reason and its current fingerprint. Reviewed items remain inspectable and can be reopened. A changed snapshot invalidates the prior review. Reviews never change schema or promote candidate relationships to trusted JOINs.

## Analysis receipts

Every new scan atomically publishes a receipt alongside its sources, databases and snapshots. It records discovered file hashes, roots/excludes, application/runtime/DacFx and analysis versions, selected C# configuration/framework/trust, relationship/mapping settings hash and database snapshot IDs. Only explicitly rebuilt SQL projects have authoritative build manifests; the pre-existing build-manifest APIs preserve their build input closure. Receipts do not archive source contents, recreate an operating system or promise byte-identical builds. An SDK/MSBuild version that was not recorded is not fabricated.

Freshness explicitly rediscoveries and hashes supported files without executing builds. It reports additions, removals, modifications and configuration/snapshot changes. Comparisons explain changed paths between recorded runs. Pre-1.6 runs have no receipt and are unknown; receipts are not retroactively invented. A scan receipt can describe a partial attempt, so quality diagnostics and database availability remain relevant even when input hashes are current. Failed/unavailable/pending database models require refresh.

## Unified search

Parameterized SQLite queries search current tables, columns, views/routines, recipe names/descriptions, object-reference evidence and saved relationship evidence. Unicode matching does not depend on SQLite's ASCII-only default lower(). Kind filtering and stable pagination return at most 100 hits per page (UI 50); search text is limited to 256 characters. SQL identifiers and evidence remain user/source text. Object/column hits open the object browser or table details; recipe hits open their history. Available source locations can be copied/opened through the existing validated editor integration. Search is metadata search, not a semantic search of every SQL body.

## Collections and batch results

Collections belong to a workspace, have optimistic versions and contain up to 200 active recipes. Users can create, rename, edit membership and delete a collection without deleting its recipes. Recipe selection is searchable and paged. Up to 100 collections are retained per workspace.

Batch validation runs actual current-schema validation, never SQL execution. It retains recipe versions, statuses, diagnostic codes and snapshot IDs and compares them with the previous batch. Results can be reloaded after leaving the screen. A schema/context or recipe-version change during a batch refuses publication of a misleading combined result. Individual validation records already completed before cancellation/conflict may remain; the operation is not advertised as an atomic rollback of all validation side effects. Runtime ID lists are not requested or stored by this feature. Retained batch/receipt snapshot references participate in the existing history-cleanup protections.

## Incremental dependency projection

A disposable SQLite index stores SQL object names/kinds and references, keyed by workspace/database snapshot. An unchanged database reuses its projection. A changed database updates only changed node/reference rows and removes absent ones transactionally. Tests verify that changing a display name while retaining its identity updates one node and zero unchanged edges. A schema rename that changes identity removes the old node and inserts the new one. Recipe names/dependencies are projected from stored JSON without loading SQL templates. Source definitions/columns are not deserialized for a warm dependency query.

This is incremental projection and impact analysis, not a claim that SQL parsing, DacFx builds or EF inference were rewritten into a fully incremental compiler. A changed database still needs its source schema analyzed; lightweight node/reference metadata is currently read into transient memory for bounded traversal. Existing 10000-object/1000-response-node limits and one-shortest-path-per-recipe behavior remain explicit. Derived index tables can be recreated from authoritative records and do not change the base catalog version.

## Integrity, restore and rollback

Integrity checking uses SQLite integrity_check. Restore preview validates a local Schemora backup, checks its catalog version, stages a separate SQLite copy and shows workspace/recipe/snapshot counts plus its SHA256. V1→V2 migration is performed by the existing protected startup migration; V2 needs no version migration. Unknown future versions are refused.

Applying a restore requires explicit preview consent, a current catalog revision, an unchanged staged backup, no active scan jobs and a preview younger than 30 minutes. Schemora starts its own executable, waits for the old process to exit and release the instance lock, then uses the established startup restore path. The previous catalog is preserved. The UI lists local backups and before-restore copies so the latter can be selected for rollback. It never swaps the live SQLite database underneath active readers. Data directories whose permissions prevent publishing private runtime state may require reopening the app normally rather than relying on automatic browser discovery.

Automated acceptance uses only isolated synthetic data directories and exercises actual restore, process restart, a second restore of the preserved catalog and rollback verification. No user catalog is used for destructive testing.

## Reproduction

Test-Professional.ps1 covers quality, provenance, Unicode search, collections, batch comparison, index reuse, integrity and real restore/rollback. Test-ProfessionalUi.ps1 covers the new browser controls, navigation, consent, language switching and focus. Both are included in Test-All.ps1. Exact completed checks and artifact hashes are recorded in verification.json.

## Docker verification observation

The first final Docker schema run returned DOCKER_SCHEMA_PUBLISH_FAILED while deploying the synthetic Commerce model. Its underlying sqlcmd output was not retained by the existing failure path, so the cause was not established. An independent execution of the same generated script succeeded. The complete application schema/recovery suite subsequently passed 24 checks with a diagnostic CLI relay and again 24 checks without that relay, without changing Docker application code. This is recorded as an unreproduced initial failure, not as a proven root-cause fix. Exact generated-SQL execution passed 10 cases. Final owned-resource inventory was empty.
