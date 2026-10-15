# Cookie Jar for macOS (work in progress)

A cross-platform port of Cookie Jar. The Windows app in `../src` is untouched and keeps building with `../build.ps1`; this folder is the new codebase.

## Layout
| Path | What |
|---|---|
| `src/CookieJar.Platform` | **Contracts**: interfaces every OS implements (processes, secrets, windows, input, capture, OCR, startup, Roblox locator/launcher) plus the capability flags. Frozen after phase 1; changes need the lead's sign-off. |
| `src/CookieJar.Core` | Platform-independent logic: profiles, accounts, restrictions, RAM policy, tiling maths, instance model, launch planning. |
| `src/CookieJar.Platform.Windows` | Windows implementations of the contracts (wraps the existing Win32 code). |
| `src/CookieJar.Platform.Mac` | macOS implementations (libc, `open`, `security`, LaunchAgents, Accessibility/CGEvent). **Not run on a real Mac yet.** |
| `src/CookieJar.AppModel` | View-models and launch orchestration, UI-framework free. |
| `src/CookieJar.App` | Avalonia UI shell (needs the .NET 8 SDK; not built on this PC). |
| `tests/*` | One small console test project per library (shared `tests/Shared/MiniTest.cs`). |
| `poc/` | Shell scripts to run on a Mac to answer "can several Roblox clients run side by side?" |
| `docs/` | Architecture notes and status. |

## Rules
- Libraries target `netstandard2.0`, C# 8, **no NuGet packages** (only the App project has any). No `System.Drawing`, no WPF, no Windows-only calls outside `Platform.Windows`.
- Verify with `powershell -File macos\verify.ps1 -Only <Platform|Core|Windows|Mac|AppModel>`.
- Anything assuming macOS behaviour that has not been run on a Mac carries a `// MAC-UNVERIFIED:` comment so it can be found with grep.
