# Phase 4 — schema changes and safe repairs

Acceptance:
- Comparison of two immutable snapshots of one database: additions/removals/changes and separate rename candidates.
- After refresh only the dependent recipe is Broken; a type change is NeedsReview; the last-compatible snapshot is kept.
- Repair preview and explicit approval; new version, rejection and rollback.
- No regex/global string replacements and no automatic guessing of business meaning.

Actually verified: scripts/Test-Changes.ps1 — 13/13 HTTP checks: removal of OldCode/addition of NewCode, rename candidate, dependent Broken/unrelated Valid, AST preview preserving a comment, no change before approval, new usable version, persisted approval, rollback/restore, subsequent type change NeedsReview.

Conservative repair supports an unambiguous SELECT with schema/table binding. CTEs, real subqueries/set/DML and ambiguous multiple-DB scopes require manual editing. Rename candidates are a reason to review, not a ready-made automatic replacement.

