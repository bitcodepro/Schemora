# Application hardening — 1.7

The six agreed improvements extend the existing SQL Server application. PostgreSQL, optional AI, GitHub and clean Windows verification remain deferred.

## Docker failures

Failed isolated Docker operations return a bounded JSON diagnostic with operation stage, elapsed milliseconds, error code, publication exit code when available, and up to 4000 characters of output. The generated SQL password is removed before truncation; common password/token assignments are also redacted. No environment dump, Docker inspect payload, SQL script or source DACPAC is retained in this journal. Up to 20 failure reports are kept in the private data-directory `docker-diagnostics` folder. The workspace tools can display them through the authenticated local API. A journal write failure does not hide the original API diagnostic.

Publication output is now retained on failure instead of being discarded. This improves investigation of a future failure; it does not establish the cause of the unreproduced 1.6 incident. Timeout/cancellation and cleanup retain the existing process/container ownership and isolation controls. Explicit fault-injection acceptance forwards the real Docker lifecycle and substitutes only the schema-publication command; a separate ordinary Docker suite tests successful publication without this substitution.

## Managed files and disk space

Workspace tools show local backup, build-manifest, recovery-preview, diagnostic, build-stage and NuGet cache file sizes. A policy keeps files for 1–3650 days (default 30) and at least 2–100 recent owned backups (default 3). Cleanup requires an explicit UI consent and current preview revision; policy/file metadata/content changes invalidate the preview. Each eligible file is hash checked again before deletion. Active scan jobs block cleanup. Current SQLite/WAL files, sources, tutorial projects and paths outside the application data directory are never cleanup candidates.

Only allow-listed generated names are eligible. Manifests referenced by any retained snapshot are protected. Recent owned backups remain protected even when an unknown backup has a newer timestamp. Unknown files remain protected. NuGet packages are reported but protected because they may be required for offline builds. Old GUID build stages can have expired files removed only after the stage itself also ages beyond retention; normal builds have bounded lifetimes and clean their own stage on completion. Short temporary build roots outside the data directory are not broadly swept by this UI.

The inventory refuses links and stops at 50000 files/directories. File cleanup is an explicit operation, not an automatic app background deletion service. Deletion is per file, not a transactional filesystem rollback: an I/O error or concurrent change can stop a partly completed operation; reloading the preview shows the remaining files. SQLite history cleanup remains a separate backed-up action. Empty build directories can remain after eligible file cleanup.

## Configurable quality rules

Each workspace can set eight named rules to off, info, warning or error, using an optimistic policy version. The original missing-key, unavailable-schema, unresolved-dependency and untrusted-relation findings remain. New advisory checks cover nullable FK columns, missing recorded FK-leading unfiltered indexes, duplicate recorded index definitions and partially analyzed SQL objects. Supporting index checks consider leading key order, primary/unique keys and filters; an included column does not substitute for a leading key. Duplicate detection compares key order/direction, includes, uniqueness, kind and filter; it is conservative rather than an index tuning engine.

Rules use the metadata actually available. They do not infer disabled/untrusted FK flags absent from the schema model, recommend unconditional index creation, drop indexes, modify schema, or change relationship trust. Analyzer diagnostics retain their original severity. Changing a finding's effective severity invalidates its prior fingerprint-based review. Rule codes remain stable technical identifiers; explanations and controls support English, Russian and Ukrainian.

## Search inside SQL definitions

Unified search adds SQL-code hits in stored definitions of views, procedures/functions and other available SQL objects. It searches current workspace snapshots with parameterized literal Unicode matching; `%`, `_` and quotes are not wildcard/SQL syntax. A result contains an excerpt of at most 300 characters around the first match and its line in the stored definition. An exact source line is added only when the model already has the definition's source starting line. Clicking a code result opens its object definition and selects/reveals the match in Monaco.

The search does not execute SQL, read live database data or claim to search missing definitions, every repository file, or runtime-generated Dapper SQL. Existing definition size limits apply. Pagination limits the response; searching stored definitions is not advertised as an FTS index. Definitions without reliable source coordinates still show their stored-definition line without inventing an editor source position.

## Build toolchain receipts

Successful SDK-style and Legacy SSDT builds record the actual executable/path/hash and MSBuild version. SDK builds also record the selected .NET SDK version, SQL project SDK declaration and selected MSBuild assembly hash when found. Legacy builds record the paired SSDT targets path/hash and SQL tasks product version/hash when available. Version probes run in the staged project's working directory, preserving inherited SDK selection. Source closure checks and a fresh tool-receipt comparison refuse reuse when the selected tooling changes.

The build manifest and atomically published scan receipt expose these facts in the provenance panel. Pending snapshots participate before publication, so a newly built model does not lose its receipt. Old manifests and imported DACPACs remain explicitly unknown; installed tooling is not presented as proof of an old build. Version probes do not evaluate project targets. Explicit build trust is still required for project execution. Receipts improve traceability, not a guarantee of byte-identical builds across operating systems. The generated tutorial now separates SQL batches with GO and is itself buildable.

Regression testing also exposed queue races: terminal scan status was visible before its workspace reservation was released, and asynchronous startup recovery could mistake a newly queued job for an interrupted old job. Recovery now completes in StartAsync before accepting work; terminal status is published after reservation/cancellation cleanup. A repeated immediate-start test covers successful and failed jobs without adding a timing grace delay.

## Accessibility

The app has a keyboard-visible skip link to its main landmark, named navigation buttons, current-page state and heading focus after page changes. New controls use labels and status/error announcements. Focus outlines support dark/light themes and Windows forced colors. Monaco uses its screen-reader mode and Tab-focus mode so Tab can leave the editor. SQL search reveals/selects the match without forcing editor focus.

Browser acceptance checks actual Tab/Shift+Tab navigation, editor escape, accessible landmark names, both-theme contrast for the tested new panels/controls, 200% zoom and forced colors/reduced motion. These automated checks are not a manual NVDA/Narrator audit, a full-app WCAG certification, or clean Windows verification. The exact completed checks and artifact coverage are in verification.json.

## Reproduction

`Test-Hardening.ps1` tests the new HTTP flows, including actual SDK rebuild/provenance and managed file deletion in synthetic data. `Test-HardeningUi.ps1` tests the new browser scenarios. `Test-DockerFailure.ps1 -Run` uses only synthetic models and owned containers to verify deterministic publication failure diagnostics/redaction/cleanup; it requires the real Docker daemon. All three are included in the applicable `Test-All.ps1` branches. Targeted .NET tests exercise journal limits/redaction, cleanup protections/revision guards, FK index semantics, rule policies, SQL search coordinates/injection literals and pre-publication build receipts. Existing real SDK and Legacy tests verify recorded tool versions and changed-tool reuse refusal.
