# macOS port status

## Stage two update (2026-10-16)

The detector port listed first under "Next phases" is implemented in `CookieJar.Core.Imaging`. Cash-out, invite, hospital, and teleport analysis now consumes `RawImage`, with explicit `IOcrService` injection and managed image preprocessing. Core has **669 passing checks**, including **148 new checks** for screenshot fixtures, scaling, padded buffers, OCR failures, conservative confirmation, matching, scene differences, and cancellation. See [integration notes](detectors.md).

The phase-one report below is historical. The Avalonia launch controls and background-efficiency timer are still unwired; the authenticated ticket launcher and Vision helper remain unported. Real Mac execution remains unverified. The detector port does not enable automated input in the app.

Final validation: `macos/verify.ps1` reports **VERIFY OK**. All six projects build, including the Avalonia app; Core, Windows, Mac, and AppModel tests pass. Validation ran on Windows, not macOS.

Verified on the Windows PC with `powershell -File macos\verify.ps1`: **VERIFY OK**, no fixes needed.

## Per project

| Project | Built here | Tested here | Result |
|---|---|---|---|
| CookieJar.Platform (contracts) | yes | no tests | BUILD OK |
| CookieJar.Core | yes | yes | 512 passed, 0 failed |
| CookieJar.Platform.Windows | yes | yes | 143 passed, 0 failed |
| CookieJar.Platform.Mac | yes (compiles on Windows) | yes, logic and parsing only | 276 passed, 0 failed |
| CookieJar.AppModel | yes | yes | 125 passed, 0 failed |
| CookieJar.App (Avalonia) | yes (.NET 8 SDK, from its own folder) | no | built, never started |
| poc/*.sh | syntax only (`bash -n`) | **not run** (needs a Mac) | unverified |

Nothing in Platform.Mac has run on a real Mac. Tests there cover pure logic (key maps, window-list parsing, command building) with fakes.

Windows app: `git status --short -- src` prints nothing, and `build.ps1 -OutDir %TEMP%/cj_integrate_build` prints BUILD OK (only existing CS0649 warnings). The exe was not run.

## MAC-UNVERIFIED markers

Count of `// MAC-UNVERIFIED:` lines: 42 in 20 files (40 in source, 2 in docs; poc/README.md also has a differently formatted one).

| File | Count |
|---|---|
| Platform.Mac/MacProcessService.cs | 7 |
| Platform.Mac/MacInputService.cs | 5 |
| Platform.Mac/MacRobloxLocator.cs | 3 |
| Platform.Mac/MacRobloxLauncher.cs | 2 |
| Platform.Mac/MacScreenCapture.cs | 2 |
| Platform.Mac/MacSecretStore.cs | 2 |
| Platform.Mac/MacShellService.cs | 2 |
| Platform.Mac/MacWindowParsing.cs | 2 |
| Platform.Mac/MacWindowService.cs | 2 |
| Platform.Mac/MacKeyMap.cs | 1 |
| Platform.Mac/MacOcrService.cs | 1 |
| Platform.Mac/MacStartupService.cs | 1 |
| AppModel/LaunchOrchestrator.cs | 2 |
| App/CompositionRoot.cs | 2 |
| App/App.axaml.cs | 1 |
| App/Program.cs | 1 |
| Core/BackgroundEfficiency.cs | 1 |
| Core/Games.cs | 1 |
| docs/architecture.md | 1 |
| README.md | 1 |

## Capability matrix

Windows column: implemented and tested here (Windows project). macOS column: code exists; "unverified" means never run on a Mac.

| Capability | Windows | macOS |
|---|---|---|
| Multi-instance launch | supported | unverified (`open -n`; this is what the PoC answers) |
| Single-instance lock handling | supported | unsupported (lock calls are no-ops returning 0) |
| Background priority (lower) | supported | unverified (nice) |
| Background priority (restore) | supported | unverified (raising priority needs root, may fail) |
| Working-set RAM cap | supported | unsupported (macOS has none; UI must hide RAM cap) |
| Secret store | supported (DPAPI) | unverified (`security -i` keychain; the secret is sent on stdin, never on argv) |
| Window list/move/resize/tiling | supported | unverified (Accessibility grant needed) |
| Input (click, keys) | supported | unverified (CGEvent; Accessibility grant; ANSI key codes only) |
| Screen capture | supported | unverified (Screen Recording grant) |
| OCR | supported | unverified (Vision/shell route) |
| Start with login | supported | unverified (LaunchAgent) |
| Roblox locator/launcher | supported | unverified (/Applications or ~/Applications) |
| Permissions reporting | n/a | unverified (PermissionState Granted/Denied/Unknown) |
| Detectors | supported | RawImage analysis implemented and fixture-tested in Core; real Mac capture / OCR unverified |
| Tray / mini bar | supported | unsupported until menu bar item exists |

## Open issues and contract requests

Platform.Mac and Platform are frozen, so contract changes are listed, not done.

Contract requests:
- (Fixed, see "Second review" below) The secret-on-argv issue and the missing composed `MacPlatform`.

Core:
- MiniBarSettings still has Windows ClickMethod values (SendInput/Legacy); the Mac input layer must map or ignore them.
- ProfileStore.LegacyFileCount is carried from Windows; decide if wanted on Mac.

macOS:
- ClearBackgroundPriority needs root, may fail for normal users.
- No working-set cap: UI and AppModel must gate RAM features on Capabilities.
- Key mapping assumes ANSI layout.
- Release/CountSingleInstanceLocks are no-ops; launch orchestration must not rely on them.
- UI must surface PermissionState Denied/Unknown for Accessibility, Automation, Screen Recording.

App / AppModel:
- PrepareForAnotherInstance is not called by the launch paths; the UI must call it once before a multi-client launch.
- Relaunch is just LaunchSingle; the caller must stop the old instance.
- MainViewModel.StatLine and StatusLine return the same text (duplicate).
- RamScale: index 0 is Auto (0 MB), index 1 is 128 MB; tests follow the code, not the original task text.

Process notes: Win-H2 and App-H4 each needed two review rounds and a lead escalation (resolved). The team reports did not pass on worker-level details.

## What you must run on a Mac

Follow `macos/poc/README.md`. Install Roblox and sign in, open Terminal, `chmod +x` the three scripts, then in order:
1. `./01-check-roblox.sh`
2. `./03-permissions.sh` (grant missing permissions in System Settings > Privacy & Security, reopen Terminal, rerun)
3. `./02-two-clients.sh "<URL 1>" "<URL 2>"` with a fresh pair of launch URLs (tickets expire fast), about 70 s.

Send back:
1. Full output of `01-check-roblox.sh`.
2. Full output of `03-permissions.sh`.
3. Full output of `02-two-clients.sh`, including the `both clients alive for 60s: yes/no` line.
4. A screenshot of Activity Monitor showing two `RobloxPlayer` rows while both clients are open.

Quit the clients afterward yourself (Cmd-Q); the scripts never close them.

## Next phases

1. **Done in stage two:** port the detectors from `System.Drawing.Bitmap` to `RawImage` so they live in Core.
2. Install the .NET 8 SDK, build and run the Avalonia App, fix compile errors there.
3. Replace the tray and mini bar with a menu bar item.
4. Run Platform.Mac against a real Mac and clear the MAC-UNVERIFIED markers (the composed `MacPlatform.CreateDefault()` and the stdin secret store are done).
5. Packaging, code signing and notarisation.

## Second review (Codex) and what changed

An outside review (Codex, default model at low effort; not confirmed to be Sol 5.6) read the macOS adapters and, with the
Windows originals pasted in, the Core / AppModel / contracts. Each finding was checked against the code before acting.

Fixed:
- **Secret on argv**: `MacSecretStore` now sends `add-generic-password ...` to `security -i` on stdin through the new
  `IStdinCommandRunner` (additive; `ProcessCommandRunner` implements it). Without a stdin runner `Set` refuses; it never falls back to argv. Values with CR/LF/NUL are refused. Stdin is written on its own thread under the call's timeout.
- **Window identity**: the window id is now the real `CGWindowID` (from `CGWindowListCopyWindowInfo`), stable while the window exists and accepted by `screencapture -l`. Raise / minimise / move find the window by process and current bounds when they run (no list index, no titles pasted into scripts).
- **Content bounds**: a window covering a whole display (full screen / borderless) keeps its full frame; others lose the 28 pt title bar. `CaptureWindow` uses `-o` and crops the title bar when given the window service.
- **Zombies** are no longer "running" (`ps -o stat=` state Z). **Lock file** is no longer deleted on release (delete race).
- **Launch PID races**: launches are serialised and a pid already handed out is never handed out again (test fails without the lock).
- **Launch flow (AppModel)**: `launchOne` now takes a `LaunchRequest` (profile name, place id, 60 s timeout, cancellation token, first-launch cache flag) and returns an `AccountLaunchResult` (Ok / TicketFailed (+TokenExpired event) / TimedOut / Cancelled / Failed), matching the Windows runner. Cancel is re-checked after the warning question. A missing confirmation handler now declines instead of accepting.
- **Foreground process**: new optional `IForegroundProcessSource` (Windows and macOS window services implement it) and `BackgroundEfficiency.ForegroundFrom(platform)`. Unknown foreground now means "slow nothing"; the old default of 0 would have slowed the client in front after 60 s.
- **MemoryPolicy.ValidateAllocation** is exception-safe again, like the original.
- **Composed `MacPlatform.CreateDefault()`** wires every Mac service and the capability flags; the App uses it (it previously wired six services by hand).
- **RawImage** rejects row sizes that overflow 32 bits; ownership documented. Platform lifetime/threading documented.

Checked and not changed:
- `ApplyTiling` is not missing; it is `ProfileTiling.ApplyTiling` (a separate file).
- Tiling across monitors: the port breaks ties between equal-sized monitors by list order (the original `Sort` was unstable); an empty monitor list returns nothing because pure logic cannot enumerate monitors (callers pass `IWindowService.GetMonitors()`).
- Lock-count members stay in the contract (documented as Windows-only, 0 elsewhere); the exited/unknown distinction for working-set reads is recoverable with `IsRunning` (documented).

Still open: `BackgroundEfficiency` and `LaunchOrchestrator` are not yet driven by the app's timer / buttons (the Avalonia shell does not call them); the ticket (HTTP) launcher is not ported, so `launchOne` has no real implementation yet; `CGWindowListCopyWindowInfo` through JXA, the `-o` capture size maths and `security -i` quoting are all unverified until run on a Mac.
