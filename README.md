# Cookie Jar

_A tidy jar for running several Roblox clients side by side._

_(formerly Roblox Multi Launcher)_

WPF launcher (.NET Framework, C# 5) for running several Roblox clients. Runs as administrator.

Windows instance limits scale with hardware: recommended capacity uses physical cores plus half the extra logical threads, a 2 GB RAM budget per client, and a 4 GB OS/app reserve. The hard limit uses logical threads and 1.5 GB per client with the same reserve. GPUs reporting under 4 GB VRAM retain limits of 3 recommended / 5 maximum. Live memory blocks and CPU warnings still apply. These are planning estimates, not measured game capacity. An i7-8700K (6C/12T), about 24 GB usable RAM and RTX 3070 Ti gets **9 recommended / 12 maximum**, previously 6 / 8. The macOS port still uses its earlier policy.

Run `./tests/build_hardware_tests.ps1` for hardware policy and launch-boundary checks; these do not launch games or send input.

Settings > **Auto RAM allocation across sessions** is on by default and remembers your choice. It splits the RAM budget (total RAM minus 4 GB for Windows/apps) equally across active and starting sessions, up to 3 GB each, rounded down to 64 MB steps. Caps rebalance as sessions start, stop or relaunch, including while the launcher is in the tray. On this PC that is 2,240 MB each at 9 sessions and 1,664 MB each at 12. Switching it off restores the existing per-session/profile limits. These working-set caps do not reserve physical memory or limit all game memory; live low-memory protection still applies. The instance targets remain **9 recommended / 12 maximum**.

## Build and run
To exceed the hardware instance-count limit, enable **Settings > Override hardware instance limit (this app session only)** and confirm the warning. Each additional launch beyond the limit requires an explicit Yes/No confirmation (default No, no automatic countdown), including every extra account in a profile run. Warnings show the next session number, free RAM and CPU load and explain lag, paging, crashes and an unresponsive Windows desktop. The override resets on app exit; the block below 1.5 GB free RAM remains active. The main window and mini bar tooltip show when the override is on. The 9 recommended / 12 hardware limit values remain the default targets on this PC.

- `./build.ps1` builds `bin\CookieJar.exe`; `./build.ps1 -OutDir bin_dev` builds a dev copy.
- Close the launcher (tray icon -> Exit) before rebuilding; it locks the exe. Run the exe and accept the UAC prompt.
- `CookieJar.exe --minibar` starts with only the mini bar and the tray icon (used by "Start with Windows").

## Features
- Accounts and profiles stored with DPAPI (AccountStore / ProfileStore). Launch a whole profile in one click.
- Game selection (Games.cs): dropdown offers Adopt Me! only (place 920587237), with the line "Launches: Adopt Me! (place 920587237)". Profile launches and website launches both use the selected game.
- Per-instance RAM: No limit / Low / Medium / High / Custom MB. Low 1216, Medium 2048, High 3584 MB (scaled down with many instances), applied on change, launch and adoption, saved to the profile. Custom MB and allocation warnings are under Advanced.
- Window tiling: Auto-tile and "Tile now" place windows evenly using each monitor's work area (physical pixels, per-monitor DPI), retrying until the Roblox window exists and again a few seconds later. Per-instance layout override (Advanced) is saved to the profile per account. Unmapped and adopted clients are placed too.
- "Switch to" per instance and "Bring all to front" under profiles.
- Hospital button (card, next to Cash out): backpack -> Gifts (selected automatically if needed) -> + -> Yes on "Teleport to The Gifts Display?" (only when the OCR title reads Gifts Display) -> waits for arrival -> turns with a right-button camera drag until the hospital marker is centred -> holds W for 12 s (one Space jump per stall, max 3; WalkSeconds is a placeholder until the real walk time is measured). Cancel on the card, or moving the mouse, stops it and releases the keys and mouse button. Option "Pause auto-click while the hospital route runs" in Advanced (default off). Failed runs leave diaghospital_<time>_<instance> (report.txt, last.png). One at a time with Teleport and CASH OUT. Not tested live.
- Anti-idle clicker (master switch in Advanced, default OFF, plus a per-instance switch) clicks the green CASH OUT button. When a trade request or party invite is on screen instead, it clicks the red Decline / Close button (never Accept / View). Checked on the same interval, re-checked after 3 s when an invite was dismissed.
- Stop: one per-instance action (card, mini bar chip, mini bar menu) that kills the Roblox client and all child processes immediately (ProcessTree.KillTree, like Process Explorer > Kill Process Tree), so Roblox shows no in-game Leave prompt. A themed confirmation (Go back default; "Don't ask again this session" for single stops) warns that unsaved progress may be lost and trades/actions cut off. Stop all (main window, tray, mini bar) asks once with a summary list. The old graceful close and separate Kill process tree button are gone. A stopped instance stays as a Stopped card with Remove; its clicker entry is unregistered.
- Mini bar: game select, Play, Stop all, small RAM and CPU meters (green, amber at warn, red at critical, tooltip with exact numbers and free RAM), "running / recommended" badge (e.g. 5 / 6) and one chip per instance. Settings: Show mini bar, Start with Windows (scheduled task, runs `--minibar`).
- Tray: the X button and minimise hide the window into the notification area (no taskbar button). Left-click or double-click restores. Menu: Open, Show/Hide mini bar, Bring all to front, Stop all instances, Exit. A one-time balloon explains this. Windows may place the icon in the hidden-icons chevron.
- Exit: only the tray Exit, the Exit button (top right), the mini bar menu Exit, or ending the process quit the app. If instances run, a profile launch is in progress or auto-click is on, a dialog offers Go back (default), Exit and leave Roblox running, or Exit and stop all instances (tree kill, clients end immediately). Logoff/shutdown closes without prompting.
- Teleport (TeleportDetector/TeleportService/TeleportUi): each instance card has Teleport to + a dropdown of the other Running instances (by username); the mini bar chip menu has Teleport to <user>. It drives the in-game FRIENDS button and the friend's green Teleport button through window capture, Windows OCR (late-bound WinRT, no extra install) and SendInput, then judges success from screen changes: green Teleported, amber Clicked - unconfirmed, red message. One at a time, Cancel supported, one automatic retry. Both accounts must be friends in Adopt Me; the game window briefly takes focus and the mouse moves; it can fail if the game UI changes or the window is covered or too small.
- Light/dark themes (ComboBoxes, checkboxes, tooltips, tray menu follow the theme live).

## Known limits
- RAM cap is a working-set trim, not a hard memory limit; Windows can page it back in.
- The clicker must briefly take window focus and moves the cursor, then restores both.
- The mini bar is a topmost overlay beside the tray, not a real deskband.
- Account tokens are stored on disk protected only by DPAPI (current Windows user).
- Startup task needs admin rights; "Access is denied" detection is English-only.
- Two instances of the same account: only the first uses the account's saved layout; the second is auto-tiled.

## Teleport tests
- 	ests\build_tests.ps1 then 	ests\out\TeleportTests.exe [detector|service] (fixtures tests\tp_*.png); 	ests\build_scratch.ps1 then 	ests\out\ScratchHost.exe (UI states, screenshots in tests\shots_teleport, 20 theme toggles).
- Live Adopt Me only: real sidebar position/scale, OCR on real names, success heuristics, real SendInput clicks.

## Verification status
Verified with a scratch test host using the real MainWindow and MiniBar with fake data: 20 light/dark toggles without exceptions, tiling maths (1-8 and 12 windows at 1920x1080, 2560x1440@150%, two monitors: count, inside work area, no overlaps), v2 and v3 profile file round trip, account removal cleaning layout/RAM entries, mini bar threshold maths, hide/restore cycles and the exit dialog. Screenshots in light and dark were reviewed.
Untested against live Roblox: real window tiling, clicker, RAM trim effect, mini bar docking on your taskbar, Start with Windows task, tray menu appearance and balloon, minimise-to-tray by mouse, SessionEnding.

## Manual test checklist
1. Start the exe; toggle dark mode a few times; open the profile, game and RAM dropdowns in dark mode.
2. Launch a profile of 2+ accounts: windows should tile without stacking; change a card's RAM dropdown and check the log; Switch to; Bring all to front; Tile now.
3. Website launch (Launch new instance): browser opens the Adopt Me page.
4. Advanced: enable auto-click master, set an interval; save a layout for one instance and relaunch.
5. Settings: Show mini bar (check meters, badge, tooltip); Start with Windows on then off (banner message).
6. Click X and minimise: window vanishes to the tray with a one-time balloon; left-click the icon restores; tray menu items work.
7. Exit with instances running: dialog appears; Go back keeps the app; Exit and leave Roblox running exits and the tray icon disappears.
8. Reboot or run `--minibar`: only the mini bar and tray icon appear.
9. Teleport: with two Running instances (friends in Adopt Me), pick the other user on a card and press Teleport; watch the chip (green/amber/red), try Cancel, try an offline friend, try the mini bar chip menu.


## Input layer, keep-alive and diagnostics
- All clicks (CASH OUT, Teleport, keep-alive) go through `src\InputSim.cs`: restore if minimised, verified foreground (SetForegroundWindow, then ALT trick, then minimise/restore), real mouse-move events in small steps, hover pause (~120 ms), button held ~80 ms, cursor readback and WindowFromPoint check at every stage (a covered window means no click). Timings are in `InputSim.Timing`.
- Advanced > Click method: Auto (SendInput, then mouse_event, then PostMessage when the screen shows no change; remembers what worked), SendInput, Legacy (mouse_event then PostMessage). Log lines and the card show which method worked.
- Advanced > Keep sessions from timing out (default on, active while the auto-click master switch is on): every 1-15 minutes (default 4) each instance is focused briefly, gets a small real mouse move over the window and back (optional Right Ctrl tap, off by default; Shift is avoided because Roblox treats it as Shift Lock), then cursor and focus are restored. Sequential, skipped while you are active. Cards show "Last keep-alive: 2m ago" and a warning chip after 12 minutes without confirmed input.
- Advanced > Diagnostics > Diagnose input: pick a running instance and press the button (do not touch the mouse for ~1 minute). It records window/DPI/monitor/elevation/foreground/cursor/WindowFromPoint/SendInput results, clicks the FRIENDS button once per method (panel is closed again) and saves report.txt plus annotated PNGs to `%LocalAppData%\RobloxMultiLauncher\diag\<timestamp>\`. A dialog has "Open diagnostics folder". Every teleport / CASH OUT / keep-alive click failure writes similar evidence to `diag\fail_<timestamp>_<kind>\` (rate limited, old ones pruned).
- Tests: `tests\build_tests.ps1` then `TeleportTests.exe input` (fake input backend, no real input except one optional click on its own throw-away window).

## Teleport / Diagnose update (tall windows)
- FRIENDS click now targets the pink icon of the sticker (not the tilted label). A 3-point ladder (icon, centre, upper-right) is tried before switching click method; the point that worked is remembered and logged.
- Panel/row detection is relative to the detected panel (wide aspect gates; tested on 1716x1358, 1200x1400, 2000x700, 800x600, 1000x1000, 3440x1440, 640x720 incl. non-uniform stretch). OCR crops follow the row height and try several scales/thresholds. Name mismatches give: "Panel opened, read: ...; target X is not among them".
- Every teleport failure (teleport-friends, -norows, -name, -ambiguous, -nobutton, -button, -noeffect) writes diag\fail_<ts>_<kind>\ with report.txt, capture.png (raw) and failure.png (annotated).
- Diagnose input always creates diag\<ts>\report.txt (even when refused), reads the opened panel (no Teleport click), closes it via the red X, and always shows a dialog with the folder.
- Tests: tests\build_tests.ps1 then tests\out\TeleportTests.exe all (253 checks).

## Friends list loading, other UI themes, manual Cash out
- Roblox loads the FRIENDS list asynchronously (slow with many clients). After the panel opens, Teleport polls every ~700 ms until at least one row shows (state LOADING = 'Loading...' text in the body via OCR, EMPTY = blank body, LOADED = rows). Progress shows on the card ('Waiting for the friends list to load... 8 s'). After 10 s of LOADING it clicks the Nearby tab then the Friends tab once to force a refresh. At the limit it closes the panel and fails with 'Friends list never finished loading (Roblox is busy - try with fewer instances running or again in a moment)' and saves evidence kind `loading` (diag\fail_*_loading, incl. the last capture). Limit: Advanced > 'Friends list wait (s)' 5-60, default 25, saved as `listwait=` in minibar.settings. Cancel aborts the wait promptly; the ClickLock is always released.
- If the list is loaded but the target is not among the visible rows, the list is scrolled with the mouse wheel (up to 6 steps, stops when nothing new appears) before 'name not found'.
- Panel detection is theme independent: Adopt Me recolours its UI per player (pink, blue ...). The body is any pale tint, confirmed by the red X and the green Friends / dark Nearby tab pair. The FRIENDS sticker (pink) and CASH OUT (green) are not theme dependent.
- FRIENDS toggles the panel, so the service re-captures before every FRIENDS click (and before retries) and never clicks while the panel is open. A click that failed because the cursor did not follow (user moving the mouse) is retried twice.
- Diagnose input waits (max 25 s) for the list, records 'list loaded after N s' / 'still loading after N s' and per-phase timings in report.txt.
- Cash out button on every card: one manual CASH OUT attempt through the auto-clicker path (same detector, click at the button centre, InputSim method ladder, ClickLock, focus/cursor restore), one at a time with Teleport. Chips: green 'Cashed out', amber 'Clicked - unconfirmed', red specific failure. Every attempt is logged, counted in the card's Clicks, kept in an in-memory 'Click history' (last 50, auto and manual) and, on success, saved to diag\cashout_<time>_<user>\ (report.txt, before.png, after.png; newest 20 kept).
- Tests: tests\build_tests.ps1 then tests\out\TeleportTests.exe all (also `theme` and `cashout`); fixtures tp_pink_open.png, tp_loading_panel.png.

## Performance audit (2026-10-15)
- Per-instance memory reads (InstanceManager.Tick every 1 s, MemoryGovernor re-apply every 5 s) used `Process.WorkingSet64`, which on .NET Framework snapshots every process and thread on the PC: measured 2.84 ms CPU per instance per read (361 processes). Replaced with `MemoryGovernor.QueryWorkingSet` (OpenProcess + GetExitCodeProcess + K32GetProcessMemoryInfo): 0.002 ms. At 10 instances this removes ~28 ms of UI-thread work per second.
- Fix: an exited Roblox client whose pid was still held open stayed "Running"; the exit code is now checked.
- While the main window is hidden in the tray, the 1 s timer no longer redraws the usage text, cards and Advanced rows (state used by the mini bar and mapping still updates; the window catches up when shown). RAM warning lines are rebuilt only when their text or the theme changes.
- RAM/CPU launch warnings (single launch and profile launch) are a themed, topmost, non-focus-stealing dialog that answers "Launch anyway / Keep launching" by itself after 6 s, so a profile launch is not held up waiting for a click.
- Not verified live (needs admin + real Roblox clients): tick timings with many instances, the timed warning during a profile launch.
- One-off launches: every saved account row has **Launch** (starts only that account; disabled while it runs), and a Stopped card linked to a saved account has **Relaunch** (replaces the stopped card with a fresh launch of that account). Both use the profile runner (restriction check, timed warning, progress, Cancel); layout/RAM come from the active profile when it contains the account. Launching the profile again also only starts members that are not running.

## UI and performance overhaul (2026-10-15, round 2)
Measured with an off-screen benchmark (real MainWindow, N fake instances, the 1 s refresh plus layout, 40 runs). v0 = before the audit, v2 = now.

| Instances | Refresh+layout v0 | v2 | Managed memory v0 | v2 | Card height v0 | v2 |
|---|---|---|---|---|---|---|
| 1 | 3.0 ms | 0.3 ms | 4.4 MB | 4.3 MB | 357 px | 239 px |
| 10 | 26.6 ms | 0.3 ms | 12.1 MB | 7.0 MB | 357 px | 230 px |
| 30 | 68.3 ms | 1.0 ms | 56.6 MB | 13.9 MB | 357 px | 227 px |

Hidden in the tray with 30 instances: 83.6 ms -> 0.35 ms per tick. Removing and re-adding 30 instances three times: memory used to climb by ~61 MB each round; now it returns to ~5 MB.

- Leak fixed: the Advanced per-instance rows (rebuilt on every instance change) registered their labels in window-lifetime theme lists and were never released, keeping every old row and every removed instance alive (~2 MB per instance change). Regression check in ScratchHost ("leak: theme label lists do not grow").
- Teleport target lists: built once per refresh pass and shared by all cards; a stamp tells cards when the target set changed, so dropdowns are rebuilt only then (was a list + signature string per card per second). Tooltips are only reassigned when their text changes.
- Combo boxes share one parsed template and item style (was two XAML parses per combo, again on every theme change).
- Cards no longer use a DropShadowEffect (a blur shader re-run whenever card content changed, i.e. every second); a 2 px bottom border gives the lift.
- Activity log trims in batches (it used to rewrite the whole log box for every line past 500).
- Instance cards: header with name, status badge and one detail line (account, RAM, uptime, PID); one action row; Teleport, RAM limit and Auto-click on one row; one muted footer (clicks, keep-alive, warning chip). The repeated explanations moved into tooltips. Stopped cards show only Relaunch / Remove / Layout. About a third shorter.
- Layout: the website-launch / Stop all / Adopt toolbar now heads the instance list, directly under Profiles; the Accounts section moved below the instances.
- Tests: ScratchHost 54 checks (new: theme label colours, leak regression; keep-alive card checks now trigger the refresh directly instead of waiting on a 1 s timer, which was flaky under load). TeleportTests detector/cashout/invite/clicker/order pass. Not run: TeleportTests input/service (they move the real mouse).
- Not verified live: real Roblox clients, real GPU/render cost of the removed shadow (not measured), DPI scaling other than the dev PC.

## Design pass (2026-10-15, round 3)
- Design tokens in Theme.cs: type scale (TextDisplay 22, TextTitle 18, TextSection 15, TextBody 13, TextMeta 12, TextCaption 11), spacing (Gap 8, GapSection 12) and Theme.CardPadding. New UI should use these instead of literal sizes.
- Hardware card replaced by a stat strip: Instances (running / recommended, limit meter), CPU (live %, meter, CPU name, cores), Memory (used %, meter, free of total), Graphics. Meters turn amber/red at 75/90 % CPU and 80/90 % RAM. Four columns, two by two below 720 px. Meters are a template-free MeterBar (no animation; re-layout only on a 0.5 % change) instead of ProgressBar.
- Section headings share one style: Profiles, Instances (with a live "2 running · 1 stopped" count), Accounts, Advanced, Settings, Activity log. Profiles separates the window tools (front / tile / auto-tile) from launching with a divider.
- Instance cards: memory and uptime changes (every second) update only the detail line instead of the whole card; the duplicate MemoryText notification is ignored.
- Benchmark unchanged within noise (0.7-0.9 ms per 1 s refresh at 30 instances); no instances or cards retained after removal. ScratchHost 54/54, TeleportTests detector/cashout/invite/clicker/order pass.
- Not done on purpose: list virtualisation (only pays off at 50+ cards; the hard limit on the dev PC is 8). Accounts rows, dialogs and the Advanced/Settings bodies keep their previous layout.

## RAM slider and background CPU saving (2026-10-15, round 4)
- **RAM limit is now a slider** on every instance card and on every account row of the active profile: **Auto** (far left, the default: no cap, Windows decides, same as the old "No limit") or **128 MB to 3 GB in 64 MB steps** (`src/RamScale.cs`). The value shows beside the track, in amber below 800 MB (likely to stutter). A change is applied and saved about 350 ms after you stop moving the slider, not on every step. Caps are stored as `Custom` + MB, so existing profiles keep working: old Low / Medium / High show their real MB on the slider, and Advanced > Custom MB still accepts values above 3 GB. The floor for custom caps is now 128 MB (was 400 / 256).
- **Save CPU on background clients** (Settings, on by default, saved as `bgefficiency=` in minibar.settings): clients that are not the foreground window run at BELOW_NORMAL priority with Windows efficiency mode (EcoQoS). The client in front, any client that is not Running yet, and any client started less than 60 s ago always run at full speed. Cards show "Background (lower CPU priority)" while a client is held back. Turning the option off, or exiting the launcher (including "Exit and leave Roblox running"), puts every client back to Normal / system managed. One `GetForegroundWindow` per existing 1 s tick; Windows is only called when a client's state changes (`src/BackgroundEfficiency.cs`). If the launcher is killed rather than exited, clients stay at the lower priority until their window comes to the front and the launcher next runs.
- Not controlled: GPU, disk, network, and CPU of the client you are looking at. Priority only matters when the CPU is busy; with free CPU it changes nothing you would notice. Efficiency mode can lower a background client's frame rate and clock speed, which is the point, but it has not been tried against live Roblox clients.
- Tests: `TeleportTests.exe background` (21 checks, no mouse or keyboard: scale maths, then priority and efficiency mode read back from Windows for a throw-away child process, foreground, grace period, pid reuse, off switch, exit restore). ScratchHost 68 checks, including slider commit/debounce/Auto/amber/theme and the card's background note. The fixed 1.3 s waits that made two older ScratchHost checks flaky were replaced by direct refreshes.
