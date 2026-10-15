# Cookie Jar macOS port: architecture

## Layers and dependency direction
```
App (Avalonia) -> AppModel -> Core -> Platform (contracts)
                                        ^
                  Platform.Windows, Platform.Mac implement the contracts
```
Dependencies point down only. `Platform` has no dependencies and no OS calls (except `ProcessCommandRunner`, which uses `System.Diagnostics.Process`). `Core` and `AppModel` see only interfaces; the App composes one `IPlatform` at startup. `Platform.Windows` and `Platform.Mac` never reference each other.

## Capability model
`IPlatform.Capabilities` is a `PlatformCapabilities` flags value, constant for the process. A feature checks the flag, not the OS name:
```csharp
if (platform.Supports(PlatformCapabilities.MemoryCap)) platform.Processes.TryCapWorkingSet(pid, bytes);
```
Rules: an unsupported operation returns false/null/empty and its flag is absent; it never throws. UI hides or disables features whose flag is missing. Flags describe the OS ability; runtime permission (macOS Accessibility, Screen Recording) is separate and read through `GetPermissionState()` (`PermissionState`). Input and capture therefore need both the flag and `Granted`.

## Conventions
- Windows are opaque `long` ids; coordinates are screen pixels (`ScreenRect`); `MonitorInfo.ScaleFactor` converts to backing pixels.
- Images are `RawImage` (BGRA32, stride) instead of `System.Drawing.Bitmap`.
- Memory is bytes, delays are milliseconds, pids are `int`.
- External tools (`open`, `security`, `launchctl`, `osascript`) go through `ICommandRunner`, so tests inject canned output.
- Mac code not run on a Mac is marked `// MAC-UNVERIFIED:`.

## Windows source to contract map
| Windows file (`src/`) | Contract |
|---|---|
| `ProcessTree.cs` (snapshots, KillTree, IsAlive, IsElevated) | `IProcessService` |
| `MemoryGovernor.cs` (SetProcessWorkingSetSizeEx, QueryWorkingSet) | `IProcessService.TryCapWorkingSet / ClearWorkingSetCap / GetWorkingSetBytes` (policy stays in Core) |
| `BackgroundEfficiency.cs` (SetPriorityClass, power throttling, GetForegroundWindow) | `IProcessService.Set/ClearBackgroundPriority`, `IWindowService.GetForegroundWindow` |
| `RobloxLocator.cs`, `CacheCleaner.cs` (cache folders, RobloxCookies.dat, HKCU login values) | `IRobloxLocator` (`GetCacheDirectories`, `ClearLoginState`, `HasLoginState`) |
| `TicketLauncher.cs`, `InstanceManager.cs` (Process.Start) | `IRobloxLauncher.Launch / LaunchUrl` |
| `HandleCloser.cs` (ntdll handle table) | `IRobloxLauncher.PrepareForAnotherInstance / ReleaseSingleInstanceLock / CountSingleInstanceLocks`, `MultiInstance` |
| `Dpapi.cs`, `AccountStore.cs` | `ISecretStore` |
| `WindowManager.cs`, `MiniBar.cs` (user32 placement, monitors) | `IWindowService`, `MonitorInfo` |
| `InputSim.cs`, `AntiIdleClicker.cs`, `InputDiagnostics.cs` (SendInput, focus and restore) | `IInputService` (click method fallbacks stay inside the implementation) |
| `TeleportDetector.cs`, `HospitalDetector.cs`, `CashOutDetector.cs`, `InviteDetector.cs`, `ColourBlobs.cs` (System.Drawing capture) | `IScreenCapture`, `RawImage` |
| `TeleportOcr.cs` (Windows.Media.Ocr) | `IOcrService`, `OcrLine` |
| `Startup.cs` (schtasks) | `IStartupService` |
| `ProfileStore.cs`, `CacheCleaner.cs`, log writers (LocalApplicationData) | `IAppPaths` |
| `Program.cs` (app-wide Mutex), `MainWindow.Input.cs` (explorer.exe) | `IShellService` |
| `Tray.cs`, `Theme.cs`, `SmoothScroll.cs`, `Fx.cs`, clipboard (WinForms/WPF) | not contracts: UI toolkit concerns, Avalonia in the App layer |
| `HardwareProfile.cs` (WMI) | not a contract: Core takes a plain hardware snapshot supplied by each platform project |
