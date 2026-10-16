# Cookie Jar - handover (2026-10-16)

Cookie Jar launches and manages several Roblox clients side by side (Adopt Me). Windows app: WPF, .NET Framework, C# 5, runs as admin. A cross-platform (macOS) port is in progress under `macos/`.

## Where things are
| | |
|---|---|
| Repo | `C:\Users\Nathan\Documents\RobloxMultiLauncher`, GitHub `naitdog15/cookie-jar` |
| `main` | Windows app, **pushed** (`1cae312`). Includes the RAM slider and background CPU saving. |
| `macos-port` | Current branch. Last commit `d315d62` (handover); Windows tiling fixes and stage-two detector work are **uncommitted**. Previously documented as local only; no push was performed in this session. |
| `src/`, `tests/`, `build.ps1` | The shipping Windows app, its tests and build. `src/MainWindow.cs` now includes the tiling fixes described below. |
| `macos/` | Cross-platform port (see below). |
| `CookieJar/` | Old nested GitHub clone, git-ignored; can be deleted. |
| Root `AntiIdleClicker.cs`, `InputDiagnostics.cs`, `TeleportService.cs` | Stray copies, not part of any build. Tidy up before it matters. |
| `.claude/skills/route-lite` | `/route-lite`: Sonnet lead + up to 5 Haiku 5.5 workers, no Opus. |
| `README.md` | Feature list plus engineering reports for each round (audit, UI, design, slider). |
| `macos/docs/STATUS.md` | Port status, capability matrix, open issues, review log. |
| `macos/docs/detectors.md` | Stage-two detector APIs, OCR injection, pixel handling, tests, and integration limitations. |

## Build and test
- **Windows app:** `./build.ps1` (output `bin\CookieJar.exe`; close the tray app first). Tests: `.\tests\build_tests.ps1`, then `tests\out\TeleportTests.exe <detector|cashout|invite|clicker|order|background>`; these touch no mouse or keyboard. UI checks: `tests\build_scratch.ps1` then `ScratchHost.exe <outdir>`; it opens off-screen windows, so ask first.
- **Never run without asking** (they move the real mouse / send keys): `TeleportTests.exe all|input|service`. The user works on this PC while I run things.
- **macOS port:** `powershell -File macos\verify.ps1` (builds everything and runs all tests; `-Only <Platform|Core|Windows|Mac|AppModel|App>` takes **one** name). Last full run on Windows: **VERIFY OK**, **1,305 checks pass** (Core 669, Windows 143, Mac 340, AppModel 153); all six projects build, including the Avalonia App. Core includes **148 new detector/image checks**. No real Mac execution.
- **SDKs:** repo root is pinned to .NET Core SDK 3.1.404 (`global.json`); `macos/src/CookieJar.App` has its own `global.json` for SDK 8.0.426 (installed with winget). Libraries are netstandard2.0, C# 8, **no NuGet packages** (only the Avalonia app has any).

## What was done (in order)
1. **Audit and repair** of the Windows app. Per-instance memory reads were ~2.8 ms each (a full process snapshot); now a direct Windows call at 0.002 ms. An exited client could stay "Running" (fixed). Hidden-in-tray window no longer redraws. Log trimming batched.
2. **Timed warning**: RAM/CPU launch warnings answer themselves after 6 s so profile launches keep their stagger.
3. **Single launch and Relaunch**: Launch button on every saved account, Relaunch on stopped cards.
4. **UI and perf round**: Teleport target lists shared across cards, big memory leak fixed (Advanced rows kept every removed instance alive), shared dropdown templates, card shadows removed, instance cards redesigned (about a third shorter), stat strip (Instances/CPU/Memory/Graphics), consistent headings. Benchmark at 30 instances: refresh 68 ms to about 1 ms, memory 57 MB to 14 MB, hidden-to-tray 84 ms to under 1 ms.
5. **RAM slider** on cards and account rows: **Auto** (default, no cap) or 128 MB to 3 GB in 64 MB steps; amber below 800 MB. Old profiles still load.
6. **Background CPU saving** (Settings, on by default): clients not in front get lower priority + Windows efficiency mode; the client in front, clients not Running yet, and clients under 60 s old are never slowed; everything is restored on exit or when switched off.
7. **Git/GitHub**: repo initialised on top of the existing remote history; `main` pushed.
8. **macOS port, phase 1** (workflow: 4 teams, Sonnet lead + 4 Haiku each, every Haiku result reviewed and redone by a *different* Haiku when needed, 65 agents). Output: contracts (`macos/src/CookieJar.Platform`), Core logic, Windows and macOS adapters, app model, Avalonia shell, `macos/poc/` scripts.
9. **Outside review (Codex)** of the port; 16 findings checked, most fixed (secret on argv now stdin; window ids are CGWindowIDs; launch flow matches the Windows runner; foreground-process source; composed `MacPlatform.CreateDefault()`; and more). Full list in `macos/docs/STATUS.md` under "Second review".
10. **Windows tiling reliability fixes:** older placement jobs and their delayed repeats can no longer overwrite a newer job for the same instance. The delayed pass rediscovers window handles, stopped/relaunched instances reject old jobs, and failed moves receive bounded retries with log messages. Placement tracking is cleared when instances are removed/relaunched. Built successfully into both `bin_dev/CookieJar.exe` and, at the user's request, **`bin/CookieJar.exe`**. No live Roblox window test yet.
11. **macOS stage two — detector port:** implemented the roadmap's detector-porting step in `macos/src/CookieJar.Core/Imaging/`. Cash-out, invite, hospital, and teleport/Friends-panel analysis now consumes `RawImage`; added `ImageRect`, `ImagePoint`, managed crop/resize/contrast preprocessing, and scene-grid averaging. OCR is passed explicitly through `IOcrService`, with no global OCR engine, new production packages, or platform contract changes. A cancelled panel wait exits before capture. Hospital confirmation refuses affirmative targets when OCR is missing, fails, or reads unrelated text. Added `DetectorTests.cs`: existing screenshot fixtures, both themes, multiple scales, padded rows, OCR failures, ambiguous names, scene differences, and cancellation. Test-only PNG decoding links the existing managed decoder; Core does not depend on the Mac adapter. Full verification passed. This stage does **not** wire the Avalonia launch controls or automation actions.

## Decisions and gotchas worth knowing
- "Auto" RAM = no cap (same as the old "No limit"). The RAM cap is a working-set limit, not a hard memory limit.
- The user wants a **warning before anything takes over the mouse/keyboard**.
- **Codex**: setup says healthy, but its command runner fails to start (`setup refresh had errors`). Workaround that worked: build a prompt file with the code pasted in and run `node ...codex-companion.mjs task --prompt-file <file> --effort low`. The plugin has no concrete "Sol" model id, so reviews run on Codex's default model: **never describe them as confirmed Sol**.
- Shell quirks: bash heredocs with quotes broke repeatedly; write edit scripts to a file (or use the Edit tool). `powershell -File x.ps1 -Only A,B` passes one string, so a comma list silently runs nothing.
- Backups of earlier states live in the session scratch folder (temp, may disappear); git history is the real backup.
- Git identity on this PC is `naitdog15`.
- Detector results are in **capture pixels**, not desktop coordinates. Retina backing pixels must be mapped to Accessibility/window coordinates before sending input. Managed resampling replaces GDI+ and is not byte-identical. Invite classification preserves the Windows rule that unreadable dialog text counts as an invitation; gate features needing reliable classification on OCR availability. See `macos/docs/detectors.md` for integration details.

## Not verified yet
- **Everything on a real Mac.** About 49 `// MAC-UNVERIFIED:` comments mark each assumption (`grep -rn MAC-UNVERIFIED macos/src`).
- Windows app with **real Roblox clients**: tiling fixes (overlapping launches, loading-window replacement, failed moves), slider cap effect, background priority/efficiency mode (frame rate in background), relaunch, timed warning, performance with many instances.
- The Avalonia app has only been built, never started.
- The portable detectors pass Windows screenshot fixtures and fake OCR tests; Mac screenshots, Retina coordinate conversion, and real Vision OCR quality remain unverified.

## TODO
**Next up**
- [ ] Push `macos-port`: `git push -u origin macos-port` (decide whether to open a PR into `main`).
- [ ] Review and commit the current Windows tiling fixes and macOS detector changes before publishing; neither was committed in this session.
- [ ] Run `macos/poc/01..03` on a Mac: do two Roblox clients run side by side? **This is the go/no-go for the whole port.**
- [ ] Codex review of the Windows adapters and AppModel (the parts not yet reviewed).
- [ ] Wire the Avalonia app: drive `LaunchOrchestrator` and `BackgroundEfficiency` from buttons and a 1 s timer, call `PrepareForAnotherInstance` before multi-client launches.

**Port work**
- [ ] Port the ticket (HTTP) launcher so `launchOne` has a real implementation, including login-expired handling.
- [x] Port the detectors (Teleport, Cash out, Invite, Hospital) from `System.Drawing.Bitmap` to `RawImage`.
- [ ] Connect portable detectors to capture/automation services after permission checks and Retina coordinate conversion; the Avalonia shell does not yet run them.
- [ ] Menu bar item in place of tray and mini bar; start at login; packaging, signing and notarisation.
- [ ] Mac OCR helper (Vision) so the Ocr capability can turn on.
- [ ] After a Mac run: clear the MAC-UNVERIFIED markers and fix what the Mac shows.

**Windows app**
- [ ] Live test with real clients (list above); run the input tests with the user's OK.
- [ ] Optional: lower memory priority for background clients; account-row and dialog restyle; list virtualisation only if instance counts ever pass about 50.
- [ ] Delete the stray root `.cs` files and the nested `CookieJar/` clone.
