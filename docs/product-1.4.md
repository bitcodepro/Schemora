# Product experience — 1.4

- The workspace navigator includes a first-use guide and an explicit Load tutorial project action. Each tutorial creates a separate synthetic SQL source directory in the application data directory, scans it without building code, and saves a runnable recipe. No production database is contacted. Refresh navigator discovers workspaces created in the main workspace editor.
- Navigation groups repository/project, database, schema and objects, including views and routines. Search filters are visibly saved per workspace in browser storage. Object lists page at 100 rows per schema; the selected workspace is remembered. A missing repository/project falls back to the workspace/source path.
- Selecting an object shows its dependent recipes as clickable graph nodes and a keyboard-accessible list with column references. Existing object-impact analysis also includes recipes reached through dependent objects. The diagram bounds visible recipes to 100; the list retains the complete result. Selecting a recipe opens its existing editor/history.
- Parameterized SQL rendering offers separate ADO.NET and Dapper examples. Both bind the generated JSON payload as a Unicode MAX parameter. Examples include real runtime values only in the current tab and explicit copy/download output; Schemora does not execute or persist them. Dapper/SqlClient are dependencies of the destination application, not new runtime dependencies for Schemora.
- HTML documentation is standalone and escapes user-authored text. It includes table columns, primary/unique keys, declared relationships, views/routines and relationship evidence/status/confidence. It requires no external fonts/scripts/assets. Schema metadata and user-authored evidence are exported only by an explicit download action.
- Visible focus indicators, native disclosure controls, labelled inputs and a keyboard alternative to graph interaction are provided. Chrome acceptance exercises navigation and focus at 200% zoom; this is not a claim of a complete accessibility certification.

## Windows and CI evidence

The existing GitHub workflow builds/tests/publishes the repository and now passes that same run's artifact to a separate hosted runtime job. It does not accept arbitrary binary download URLs or execute those binaries on a self-hosted machine. The hosted worker has development tools, so its evidence explicitly says cleanMachine=false.

Test-CleanWindowsRuntime.ps1 defaults to refusing machines where development tools are detected in PATH/common installation locations. On a dedicated clean Windows worker, provide a locally copied, verified Schemora executable and run the script with PowerShell 7. The optional AllowDevelopmentTools switch tests runtime functionality while explicitly recording that the machine is not clean.

This development host has SDK/Node/Visual Studio and Windows Sandbox is unavailable. The Hyper-V module is installed, but read-only VM inventory access was denied by Windows permissions. A fresh-Windows run is pending an actual clean environment. The local repository has no Git remote; remote CI execution is pending a user-provided destination repository. A workflow file or local clean-source build is not reported as remote CI or fresh-OS verification.

An initial arbitrary-URL/self-hosted workflow design was rejected by automatic approval review because it could execute unverified downloaded code. That design was discarded; only the safer same-workflow artifact job and local runtime test remain.

## Reproduction

Run Test-Experience.ps1 for new browser flows; Test-All.ps1 includes it. Run Test-CleanWindowsRuntime.ps1 with AllowDevelopmentTools for a clearly labelled local runtime check. The ordinary Test-Clean.ps1 -Docker verifies the complete source release on this host. Fresh Windows and remote Actions results must be added only after those external runs actually complete.
