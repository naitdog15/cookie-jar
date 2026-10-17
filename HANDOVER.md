# Cookie Jar handover — 2026-10-17

## Current state

- Repository: `https://github.com/naitdog15/cookie-jar` (private).
- Worktree: `C:\Users\Nathan\Documents\RobloxMultiLauncher`.
- Windows branch: **main**. Latest application commit: **99e07d8**, version **1.1.1**, pushed to GitHub.
- Both `bin/CookieJar.exe` and `bin_dev/CookieJar.exe` were built from clean application commit `99e07d8475954f7b5edc84767ac34f53acb08cba`. Their generated `PATCH_NOTES.md` identifies that revision.
- `macos-port` remains at `2457f1c` (`Add auto-RAM and detector stage two`), matching its cached remote-tracking branch. It has its own older handover and portable code. This session did not change that branch.
- Read **AGENTS.md**, **CLAUDE.md** and **docs/RELEASE_POLICY.md** before changes. Windows work belongs on main; macOS work belongs on macos-port. Check the branch before editing.

## Windows changes delivered

1. **Hardware limits** (`9b7b507`): on this PC, i7-8700K (6 cores / 12 threads), about 24 GB RAM and RTX 3070 Ti, the targets are **9 recommended / 12 default hardware limit**, previously 6 / 8. The user explicitly withdrew the request to double those targets. CPU threads receive partial credit for recommendations; RAM math reserves 4 GB for Windows/apps. These are estimates, not measured game capacity.
2. **Auto RAM allocation**: Settings switch, on by default, persisted as `autoRam` in `minibar.settings`. Divides total RAM minus 4 GB across active and starting sessions, including those without a PID yet; caps each at 3 GB and rounds down to 64 MB steps. Current PC: 2,240 MB each at 9 sessions, 1,664 MB at 12. Rebalances in the tray too. While enabled, manual controls are disabled; turning it off restores saved manual limits. These are working-set caps, not reserved physical RAM or hard process-memory limits.
3. **Hardware-limit override** (`9d25703`): Settings > Override hardware instance limit (this app session only). Off by default, not persisted. Enabling requires confirmation; every launch beyond the limit, including each extra profile account, needs another explicit Yes/No prompt (default No). These prompts never auto-accept. Ordinary resource warnings retain the existing countdown. Free RAM below 1.5 GB still blocks launches. The main window and mini bar tooltip show override state.
4. **Versioning and patch notes** (`91e622b`, initial version 1.0.0): shared agent rules, release preparation, build/commit validation, versioned executable metadata/title, and generated distribution notes.
5. **Source updater** (`0cdea57`, 1.1.0; fixed by `99e07d8`, 1.1.1): Settings > Check for updates. Use **1.1.1 as the updater baseline**. Restart the local 1.1.1 executable once to load the feature; older running versions cannot acquire their first updater through a button they do not yet have.

## Updater implementation and behavior

- `src/MainWindow.Updates.cs`: check button/status, patch-notes dialog, download/build action and separate install/restart confirmation. Checking is user-triggered; there is no scheduled background polling.
- `src/UpdateService.cs`: version comparison (numeric triplets; alpha < beta < stable), GitHub access, pinned archive download, preparation and installer handoff.
- Checks GitHub **main**, pins the full commit SHA, then reads VERSION and the matching source notes from that revision. An equal or older version is not offered. A newer alpha/beta on main can be explicitly offered; there is no separate stable-release channel.
- **Private repository authentication:** uses installed GitHub CLI (`gh.exe`) and its existing sign-in, forced to github.com. Requires repository read access (`gh auth login` on another PC). Cookie Jar stores no GitHub token. Public repositories have an unauthenticated HTTPS fallback. Unauthenticated access to this private repository returns 404; do not mistake that for a missing repository.
- Source is downloaded to `%TEMP%\CookieJarUpdate-<GUID>`, checked for unsafe archive paths/size, extracted and built in isolation. The Git checkout, stored accounts and application settings are not updated or overwritten.
- `scripts/install-update.ps1` is embedded in the exe. Prepare mode builds and verifies the candidate version/hash. Install mode signals readiness, waits for the original process to exit, rechecks the binary, atomically replaces the exe, copies PATCH_NOTES.md and restarts. It keeps `<installed-exe>.previous` and restores the previous exe/notes on replacement or immediate startup failure.
- Roblox stays running through restart; launcher automation pauses and the session-only hardware override resets. Temporary source/build logs and backups are retained. Failure diagnostics are in the staging folder's `update.log`.
- **1.1.1 fix:** Windows PowerShell spawned from a PowerShell 7 environment could not load Get-FileHash, despite a successful source build. Hashing now uses .NET SHA-256 directly. Do not reintroduce that cmdlet dependency.
- Source update build contract: `build.ps1 -SourceArchive -SourceRevision <full-SHA> -OutDir <directory>`. Requires Windows PowerShell and the .NET Framework compiler; no Git checkout is needed. It still validates VERSION/notes and emits metadata/notes. Do not use archive mode to bypass ordinary development checks.

## Release workflow — mandatory for Codex and Claude

- `VERSION` is the source of truth. Major = +1.0.0 (reset minor/patch), minor = +0.1.0 (reset patch), patch = +0.0.1. Append `a` or `b` for alpha/beta on any numeric release. Promotion alpha -> beta -> stable keeps the numeric target. Do not reuse published version labels.
- Prepare once per application update: `./scripts/new-release.ps1 -Bump Major|Minor|Patch -Channel Stable|Alpha|Beta -Summary "Outcome"`. Use `-Bump None` for prerelease promotion. Fill the generated source notes before building.
- Commit application changes, VERSION and `docs/patch-notes/<version>.md` together. Rebuilding the same update does not increment the version. Documentation/test-only commits do not require a bump.
- Build embeds numeric assembly/file metadata and the exact display version, then generates `PATCH_NOTES.md` beside the exe with branch, revision and clean/dirty provenance. Rebuild after committing for distributable artifacts.
- `.githooks/pre-commit` validates staged contents rather than unstaged files. Enabled in this clone via `core.hooksPath=.githooks`; configure that in new clones. Do not bypass it.
- `docs/patch-notes/1.0.0.md`, `1.1.0.md` and `1.1.1.md` are the source release briefings. The macOS distribution still needs its own version-metadata/note-copy integration; do not claim the native Mac app has the Windows updater or Auto RAM UI.

## Build and verification

Windows source targets **WPF, .NET Framework, C# 5**, x64; app manifest requires administrator rights. Preserve that compatibility. Close the app through tray > Exit before rebuilding if its executable is locked.

| Command | Purpose / last result |
| --- | --- |
| `./build.ps1` | Builds bin/CookieJar.exe and PATCH_NOTES.md; passed at 1.1.1. |
| `./build.ps1 -OutDir bin_dev` | Development copy; passed at 1.1.1. |
| `./tests/build_hardware_tests.ps1` | Hardware limits, override boundaries and Auto RAM budget checks; passed. No input. |
| `./tests/release-tests.ps1` | Version/promotion, metadata/notes and isolated staged-Git checks; 29 passed. Tested in PowerShell 7 and Windows PowerShell 5. |
| `./tests/build_update_tests.ps1` | Updater version/revision validation; 12 passed. |
| `./tests/build_update_tests.ps1 -Live` | Adds real authenticated GitHub version/notes/archive checks; passed. Network needed. |
| `./tests/out/UpdateTests.exe --prepare-live` | Actual authenticated download and spawned source build, stopping before installation; passed against published 1.1.1 at 99e07d8. Run build_update_tests.ps1 first. |
| `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/update-install-tests.ps1` | Isolated archive build, install, backup, rollback, hash/tamper and traversal checks; 14 passed. Does not launch the real app or touch Roblox. |
| `./tests/build_scratch.ps1` | WPF scratch host compilation passed. It now references System.Web.Extensions for updater JSON. |
| `./tests/build_tests.ps1` then `./tests/out/TeleportTests.exe background` | Existing RAM-slider/background-efficiency suite: 21 passed earlier in this session. |

Warnings about unused test-hook fields are existing compiler warnings. New test fixtures live under ignored `tests/out/`; they are retained for inspection.

## Outstanding work and cautions

- **Not tested interactively:** clicking through updater dialogs and replacing/restarting the real Cookie Jar process. End-to-end authenticated preparation and isolated replacement/rollback passed; do not describe that as a live self-update test.
- **No live Roblox validation** of instance capacity, Auto RAM performance, override behavior at high counts, or preservation through updater restart. Working-set caps can cause paging/stutter.
- The updater's restart health check covers about the first 3 seconds. Later failures do not automatically roll back. Network errors, GitHub limits/access issues, unavailable compiler/PowerShell and delayed antivirus locks may prevent updating and should leave a recoverable installation.
- Any future change to updater/source-build tooling must preserve the archived-source build contract and test the real spawned helper environment.
- Native macOS feature parity remains separate. Preserve macos-port; do not merge its detector/tiling work wholesale into Windows main while making a Windows-only change.
- **Do not run real mouse/keyboard tests without permission.** `TeleportTests.exe all|input|service` and interactive automation may take control of the desktop. The user works on this PC during tasks.

This handover is documentation only; application VERSION remains 1.1.1.
