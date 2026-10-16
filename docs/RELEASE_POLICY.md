# Shared release policy for Codex and Claude

`VERSION` is the source of truth. Every application update must include a version change and a brief `docs/patch-notes/<VERSION>.md` in the same commit as its source. This applies on Windows/main and macOS/macos-port. An unchanged rebuild uses the existing version and notes; never bump once per build or output directory. Pure documentation/test-only commits do not require an application version bump. Changes to build/release tooling do.

## Choose the version before building or committing

- Major: incompatible changes or a substantial redesign; `X.Y.Z` -> `(X+1).0.0`.
- Minor: new compatible features; `X.Y.Z` -> `X.(Y+1).0`.
- Patch: compatible bug fixes or maintenance; `X.Y.Z` -> `X.Y.(Z+1)`.
- Alpha/beta: append exactly `a`/`b` to the intended numeric release, e.g. `2.0.0a`, `1.3.0b`, `1.2.4a`. This notation also applies to minor and patch releases.
- Promote alpha -> beta -> stable without increasing the numeric triplet. Use a new numeric version for another update in the same channel; do not silently reuse published version labels. Stable -> alpha/beta requires a new numeric target.
- If several changes share one update, use the highest applicable bump once. If the user specifies the version or release type, use their instruction.

Run `./scripts/new-release.ps1 -Bump Major|Minor|Patch -Channel Stable|Alpha|Beta -Summary "Short outcome"`. `-Channel` defaults to Stable. For promotion use `-Bump None -Channel Beta|Stable`. Review and fill the generated notes before building. Initial version `1.0.0` establishes the previously unversioned Windows baseline.

## Patch-notes briefing

Keep notes brief and factual: version, date, platform, summary, user-facing changes, validation performed and known limitations. Include source paths so readers can find the implementation. Record checks actually run; do not claim live/UI testing based on compilation. Remove all template placeholders. Never include secrets or account data. Keep historical release notes.

## Build and commit

1. Check the intended branch and working-tree changes. Prepare the version and source notes for this update.
2. Build (`./build.ps1`, optionally `-OutDir bin_dev`) and run appropriate checks. The Windows build validates the version/notes and generates numeric assembly/file versions plus the exact display version (including `a`/`b`). It puts generated `PATCH_NOTES.md` beside the exe with the source revision, branch and dirty/clean state. A dirty build refers to that revision plus local changes, not to a published release commit.
3. Review the source notes and version together with the source changes. Stage all three. The pre-commit check requires a version bump and matching notes for application/build-tool changes. Configure once per clone: `git config core.hooksPath .githooks`. Do not skip it with `--no-verify`.
4. Commit with an outcome-focused message. Rebuild from the clean commit for distributable artifacts so generated provenance identifies that commit. Push only within the user's authorized scope. Include the version and relevant validation in the final report.

The commit check also covers `macos/src`; use the same version and notes convention for port updates. The existing macOS build does not yet consume Windows generated assembly metadata or copy its distribution notes: Mac release work must wire those into its packaging before claiming a versioned Mac distribution.
