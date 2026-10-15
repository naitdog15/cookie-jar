# App notes

## What CookieJar.AppModel exposes

Pure logic (netstandard2.0, no UI types). Verified here with `verify.ps1 -Only AppModel`.

- `ObservableBase`: PropertyChanged base; `Set` raises only when the value really changes.
- `FeatureState` (Visible, Enabled, Reason): built by `FeatureState.From(platform, flag, label)`. Unsupported features stay visible but disabled, with the reason "<label> is not supported on <platform>.".
- `MainViewModel`: `Instances`, `Accounts`, gates (`MemoryCap`, `BackgroundPriority`, `WindowPlacement`, `InputInjection`, `StartAtLogin`), `ActiveCount`, `StatusLine`/`StatLine` (from `Restrictor.StatusLine`), and the RAM slider: `RamIndex` (0 = Auto, 1..`RamScale.Steps`, clamped), `RamMb`, `RamLabel`.
- `InstanceCardViewModel`: forwards the wrapped `RobloxInstance` changes; `CanCapMemory`, `CanThrottle`; `Dispose` detaches.
- `AccountRowViewModel`: `IsSelected`, `IsRunning`, `Placement` gate.
- `TimedWarning`: countdown state machine (below).
- `LaunchOrchestrator`: restriction check (`CheckRestriction`), `LaunchSingle`, `Relaunch`, `LaunchProfile` with stagger and cancellation, `PrepareForAnotherInstance`. Process start is delegated to a `launchOne` callback; waiting goes through an injectable delay; messages come from `LaunchPlanner`. Reports are `LaunchReport` / `AccountReport`.

## How the UI drives TimedWarning

The UI owns the timer. It creates `new TimedWarning(TimedWarning.DefaultSeconds)` (6), shows `RemainingSeconds`, calls `Tick()` once per second, and `Accept()` / `Decline()` from the buttons. It closes the dialog when `State` is no longer `Pending`. `Result` is false only for `Declined`; running out of time (`TimedOut`) counts as Yes, like `TimedWarnDialog`. Calls after the state has settled are ignored. A zero or negative countdown is clamped to 1 second. The orchestrator asks the UI through `Events.ConfirmWarning`, which is expected to run this dialog and return `Result`.

## What the Avalonia App contains (UNVERIFIED)

`macos/src/CookieJar.App`: `Program`, `App` (axaml + code), `MainWindow` (axaml + code), `CompositionRoot` (wires the Mac platform, AppModel and Core), and `Themes/CookieJarTheme.axaml`. It references Avalonia 11.0.10 from NuGet.

**The App project is UNVERIFIED.** Only .NET SDK 3.1 is available on this machine, so the App project (net6+/Avalonia) has never been built or run, and `verify.ps1` does not cover it. Only AppModel, Core, Platform and the Windows platform are built and tested here. Build it on a Mac (or a machine with a current SDK) before trusting it.

## MAC-UNVERIFIED assumptions

Find them all with `grep -rn "MAC-UNVERIFIED" macos`. In the App and AppModel:

- Classic desktop lifetime gives a normal macOS app menu and Dock behaviour (`App.axaml.cs`).
- The user profile folder is the macOS home used by the Mac services (`CompositionRoot.cs`).
- The capability flags set in `CompositionRoot` match what `MacProcessService` really provides.
- Avalonia 11.0.10 runs on macOS 13+ through Avalonia.Desktop (`CookieJar.App.csproj`).
- `UsePlatformDetect` selects the macOS backend (`Program.cs`).
- The macOS launcher can allow a second Roblox client (`LaunchOrchestrator.PrepareForAnotherInstance`); the proof of concept in `poc/` has not run on a Mac.
- `launchOne` starts the client through `IRobloxLauncher` on macOS.

## Test expectations

Expected numbers come from Core/Windows code: `RamScale` (`MbFromIndex(0)` is 0 = Auto, index 1 is 128, step 64, max 3072), `TimedWarnDialog.DefaultSeconds` = 6, `Restrictor` block under 1.5 GB free and warn under 3.0 GB free. The task text said `MbFromIndex(0)` = 128; the Core code returns 0 for index 0 and 128 for index 1, so the tests follow the code.

## contractRequests

None.
