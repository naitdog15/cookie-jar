# macOS port: status after phase 1

Verified on the Windows PC with `powershell -File macos\verify.ps1`: **VERIFY OK**, no fixes needed.

## Per project

| Project | Built here | Tested here | Result |
|---|---|---|---|
| CookieJar.Platform (contracts) | yes | no tests | BUILD OK |
| CookieJar.Core | yes | yes | 512 passed, 0 failed |
| CookieJar.Platform.Windows | yes | yes | 143 passed, 0 failed |
| CookieJar.Platform.Mac | yes (compiles on Windows) | yes, logic and parsing only | 276 passed, 0 failed |
| CookieJar.AppModel | yes | yes | 125 passed, 0 failed |
| CookieJar.App (Avalonia) | **no** (needs .NET 8 SDK, only SDK 3.1 here) | no | not built |
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
| Secret store | supported (DPAPI) | unverified (`security` keychain; secret appears on argv briefly) |
| Window list/move/resize/tiling | supported | unverified (Accessibility grant needed) |
| Input (click, keys) | supported | unverified (CGEvent; Accessibility grant; ANSI key codes only) |
| Screen capture | supported | unverified (Screen Recording grant) |
| OCR | supported | unverified (Vision/shell route) |
| Start with login | supported | unverified (LaunchAgent) |
| Roblox locator/launcher | supported | unverified (/Applications or ~/Applications) |
| Permissions reporting | n/a | unverified (PermissionState Granted/Denied/Unknown) |
| Detectors (System.Drawing.Bitmap) | supported | unsupported until ported to RawImage |
| Tray / mini bar | supported | unsupported until menu bar item exists |

## Open issues and contract requests

Platform.Mac and Platform are frozen, so contract changes are listed, not done.

Contract requests:
- Mac-H2: MacSecretStore passes the secret on argv (`security add-generic-password -w`), visible in `ps` briefly. Fix: `security -i` with stdin, needs an `ICommandRunner` overload taking stdin.
- No composed `MacPlatform : IPlatform` exists; App/AppModel wiring builds it from parts. Nobody filed this; it should be added.

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

1. Port the detectors from `System.Drawing.Bitmap` to `RawImage` so they live in Core.
2. Install the .NET 8 SDK, build and run the Avalonia App, fix compile errors there.
3. Replace the tray and mini bar with a menu bar item.
4. Add the composed MacPlatform, resolve the contract requests above (stdin secret store), then run Platform.Mac against a real Mac and clear the MAC-UNVERIFIED markers.
5. Packaging, code signing and notarisation.
