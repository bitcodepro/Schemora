# Product improvements — 1.5

All six agreed improvements are implemented locally. PostgreSQL and optional AI remain deferred. GitHub and clean Windows are outside this release verification.

## Authoring recovery

One active authoring draft per workspace is retained in browser storage immediately and in the local Schemora catalog after a 400 ms debounce. Catalog recovery supports a different browser origin after an automatic port change. Recovery is explicit; it does not silently overwrite the saved recipe. Saving or discarding deletes the retained copy. Leaving a dirty editor or closing the browser requests confirmation. A storage failure is visible.

Only typed recipe definition fields are persisted: SQL template, metadata, input definitions/policies and dependencies. Runtime ID text, parsed values, bound parameters and generated output remain excluded. SQL literals deliberately written inside a template are part of the persisted definition. The last fraction of a second before an abrupt termination may exist only in the browser copy. This is a single local draft, not collaborative editing or a multi-draft history.

## Dependencies and large workspaces

Object selection shows impact arrows from referenced object to caller, through views/routines to recipes. Each returned recipe has one shortest explanatory path; this does not enumerate every possible SQL execution path. Accessible text lists include endpoints, reference kind and confidence. Clicking a recipe opens its editor.

Search is server-side, debounced and cancellation-aware. Results page at 25 recipes (API maximum 100). The API reports total/offset/limit and partial results. Traversal is limited to 10000 objects and a response to 1000 nodes; unresolved references are explicitly partial. Cycles are visited once. References remain static metadata; no procedure or runtime SQL is executed. The navigator retains 100 objects per schema page and now omits the full recipe dependency payload. Full schema metadata and stored recipe records are still read transiently to compute a dependency page; this is not an index-only traversal.

A synthetic 1000-recipe test verifies the last page and bounded output: 28 nodes and 27 edges for 25 recipes sharing a three-object chain. Timing is recorded in test output and depends on machine load.

## EF / Dapper coverage

An explicit coverage action reports selected configuration/framework, included source files, discovered excluded files, partial project context, EF/Dapper evidence counts, diagnostic codes/locations and suggested next actions. Reading coverage never starts MSBuild. It uses static evaluation or a previously explicitly trusted, content-validated cache.

Counts describe observed evidence, not coverage percentages. Dynamic SQL is skipped with a reason; it is never executed. Dapper remains candidate evidence. Synthetic tests exercise Debug/Release conditional SQL, static joins alongside dynamic commands, and explicitly excluded files. Package/generated-source/build limitations remain visible rather than being presented as complete analysis.

## Version comparison

History opens comparison before restoration. Changed SQL, input policies/types, databases, dependency fields and metadata are shown side by side. A read-only comparison endpoint evaluates both definitions against current schema and shows dependencies, diagnostics and the selected version's last recorded validation when available. Historical validation is not a guarantee of current compatibility.

Restoration retains the existing optimistic version/schema guards, revalidates and appends a new version. It never rewrites history or executes the SQL.

## Documentation and localization

HTML export uses the current EN/RU/UK selection, with independent object, relationship, evidence and recipe dependency sections. Labels and known kinds/statuses are translated; user SQL identifiers, user-authored evidence and unknown external codes are preserved. Recipe dependencies use readable schema object names where available. All dynamic HTML text is escaped and the report has no external assets or scripts.

## Verification

See the 1.5 verification report for actual completed runs. Test-Experience.ps1 includes the new authoring/coverage/report workflows; Test-All.ps1 invokes it. Unit tests include draft privacy, definition comparison, report languages/sections, transitive paths, bounded recipe paging and configuration-sensitive Dapper analysis.
