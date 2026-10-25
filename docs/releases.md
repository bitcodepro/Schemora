# CI, versions and Windows releases

The workflow and release-tool structure is adapted from `bitcodepro/Typlo` at
`bfbe00202863c98724a10077a86a830def6fed46`. Schemora currently supports **Windows x64**.
Linux/macOS packages are not advertised or produced. The optional generated-SQL
regression still runs on Ubuntu with SQL Server on the weekly schedule and on manual runs.

Every PR and push to `main` checks the frontend, C# formatting, NuGet advisories,
solution tests and the published HTTP/Chrome acceptance suites. Python tests check
the release boundaries. A failed manually requested SQL regression blocks release
version selection; its skipped state is allowed on an ordinary PR or push.

## Version policy

- With no stable `v*` tags, automatic publication starts at `0.1.0`.
- Otherwise the highest stable tag advances by one patch version.
- `[minor]` / `#minor` or `[major]` / `#major` in the commit subject select a larger bump.
- `[skip release]` in the subject runs validation without producing a release.
- A manual run can select a bump or an explicit SemVer, such as `0.2.0-rc.1`.
- Existing tags/releases are never replaced. An explicit release, including a
  prerelease, must exceed the highest stable version.
- PR packages use `0.0.0-pr.<number>` and never publish a tag or release.

The version is supplied to the release build without rewriting the source project
or the historical feature changelog. `build-info.json` records the exact source SHA,
version and runtime identifier, and the application's informational version includes
the source SHA. Local development currently uses the project version `1.8.0`.

## Package and validation

Each release contains exactly `Schemora-<version>-win-x64.zip` and its matching
`.sha256` file. The ZIP contains only:

- `Schemora.Host.exe` (self-contained single file; no .NET/Node installation needed);
- `schemora.json` with only `{"port": 0}`;
- `README.md`, `THIRD-PARTY-NOTICES.txt` and `build-info.json`;
- the existing root `LICENSE`, if the project owner supplies one.

No project license is invented by packaging. Third-party license/notice texts are
collected from the restored runtime NuGet dependencies and production frontend
dependencies. Native Windows packaging uses isolated staging, disables debug
symbols, maps source paths, checks x64 PE architecture, exact inventory, checksums,
source/version identity and safe archive paths. Symlinks and local data are rejected.
The downloaded archive is verified again, then its executable serves the embedded UI,
loads the tutorial and renders SQL on a separate Windows runner. That runner has
development tools installed; its report does not claim a tool-free Windows test.

```powershell
python scripts/release.py package --rid win-x64 --version 0.1.0
python scripts/release.py verify --directory artifacts/packages --version 0.1.0 --commit <40-character-SHA>
python -m pip install PyYAML==6.0.3
python -B -m unittest discover -s tests/release -v
```

Normal packaging requires a clean checkout. For local verification of uncommitted
tooling changes, `package --validation-only` marks the archive and build identity;
`verify --validation-only` can inspect it, but the publication verifier rejects it.

Only the `publish` subcommand writes to GitHub. It requires this repository's CI
workflow on `main`, rechecks remote `main`, rejects tag/release collisions and version
regression, reserves a new tag, uploads a draft, verifies all asset names and sizes,
rechecks `main`, then publishes. Failures retain the draft/tag for inspection. It
does not delete, force-update or silently retry existing tags/releases.

All checkouts disable persisted credentials. The workflow defaults to `contents: read`;
only the release job has `contents: write`. Artifacts are kept for seven days. No
custom publishing token, certificate or signing identity is required; the Windows
executable is unsigned unless a separately authorized signing process is added.

`work/`, build outputs, package staging, browser profiles, local catalogs, session
files and credentials must remain excluded from source publication. Review an explicit
source inventory before any new root commit; do not stage the whole directory blindly.
