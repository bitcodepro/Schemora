# Phase 2

RelationshipAnalyzer merges declared FK, semantically bound EF mappings, observed SQL equality joins and deterministic name/type/key candidates. Every edge carries source/path/line/reason evidence, confidence and a separate trust decision. Candidate confidence never grants SQL eligibility. Unqualified/ambiguous SQL joins, OR predicates, missing endpoints and incompatible types are diagnosed rather than promoted.

Roslyn parses and binds C# against embedded official framework/EF metadata; no MSBuild, generators, Emit or user code executes. Explicit project-to-database mappings prevent applying a C# model to every database in a workspace. Missing dependencies, dynamic mappings, multiple contexts, spoofed attributes and unsupported configuration paths retain conservative diagnostics. Embedded metadata supports the single-file executable and avoids Assembly.Location assumptions.

Saved user confirmations/rejections live separately from generated metadata and survive rebuilds. Removed/changed endpoints become stale and cannot participate in path SQL. Physical evidence keeps priority; conflicts stay visible with reasons. Distinct database identities with the same SQL name cannot generate cross-database JOINs until the user supplies unambiguous SQL names.

The bounded graph API filters before limiting nodes and supports focused N-hop neighborhoods. React Flow provides visible-node virtualization, pan/zoom, minimap, controls and persisted manual positions. Server table search reaches tables outside the loaded fragment. The path finder uses only trusted compatible edges, returns alternative routes and escapes all SQL identifiers. Multiple routes require UI selection before preview/copy.

Acceptance: scripts/Test-Relationships.ps1 (real local API, synthetic work fixture). Core coverage includes physical priority, string IDs, composite/reversed joins, observed SQL scope, stale decisions, ambiguous database names and semantic EF mapping. Frontend graph-model tests check stable layouts, bounded/prototype-safe positions, foreign-key direction and server trust preservation. Visual browser E2E remains separately recorded in release validation.

