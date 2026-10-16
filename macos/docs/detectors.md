# Stage two: portable detectors

`CookieJar.Core.Imaging` contains the cash-out, invite, hospital, and teleport detectors ported from the shipping Windows app. They consume `RawImage` captures and return `ImageRect` / `ImagePoint` values in **capture pixels**. They perform no capture, process access, clicks, or keyboard input.

The colour tests, connected components, layout thresholds, Friends panel parsing, and conservative username matching follow the Windows implementation. Capture stays behind `IScreenCapture`; OCR stays behind `IOcrService`. There are no new production packages or platform contract changes.

## Use

```csharp
RawImage frame = platform.ScreenCapture.CaptureWindow(windowId);
ImageRect? cash = CashOutDetector.Find(frame);
ImageRect? dismiss = InviteDetector.Find(frame, declineTrades: true, declineInvites: true,
    ocr: platform.Supports(PlatformCapabilities.Ocr) ? platform.Ocr : null);

FriendsPanelInfo panel = TeleportDetector.FindPanel(frame);
TeleportDetector.ReadNames(frame, panel, targetUsername, platform.Ocr);
NameMatch match = TeleportDetector.MatchTarget(panel, targetUsername,
    platform.Supports(PlatformCapabilities.Ocr));
```

Pass the platform OCR service explicitly to `HospitalDetector.FindTeleportYes`, `TeleportDetector.ClassifyPanel`, and `TeleportDetector.WaitForList` when text recognition is available. These calls can be slow and belong off the UI thread. Consumers must check capture/input permission state separately before starting automation.

OCR failures produce empty text. Hospital confirmation returns no affirmative target unless OCR identifies the Gifts Display prompt. Invite classification retains the Windows rule: unreadable dialog text is classified as an invitation. A caller that requires reliable trade/invite separation should gate that feature on OCR availability. `MatchTarget` reports `NoOcr` when passed `ocrAvailable: false` and names are unreadable, and refuses ambiguous matches.

## Image handling

- Top-down BGRA buffers may contain padding after each row; all detectors remove that padding before analysis.
- Cropping, resizing, and OCR preprocessing allocate their own buffers and leave captures unchanged.
- OCR preprocessing uses managed bilinear resizing, contrast stretching, three threshold modes, and white margins. Scene comparison averages covered source pixels into a 64 × 36 grid so thin changes are not missed between sample points. These resamplers replace the Windows GDI+ implementation; they do not promise byte-identical interpolation.
- `WaitForList` retains its last capture as a managed buffer. No disposal is needed. A cancelled wait exits before the next capture; nonpositive polling intervals are rejected.
- Capture pixels must be mapped to window/desktop coordinates by the caller. On a Retina display a capture may have more backing pixels than the Accessibility window coordinates. Do not pass a detector point directly to desktop input without that conversion.

## Verification and remaining work

Run `powershell -File macos\verify.ps1 -Only Core`. The Core suite includes 148 additional checks covering existing PNG screenshots, both UI themes, multiple sizes, padded rows, OCR injection/failure, affirmative-target refusal, name ambiguity, scene changes, and cancellation. Tests link the existing managed PNG decoder solely to read fixtures; Core does not reference the macOS adapter.

This stage ports detection logic. Wiring automation actions into the Avalonia app, the ticket launcher, the Vision OCR helper executable, menu bar integration, and real Mac verification remain separate work. No detector has been validated against a real Mac capture yet. Real-device checks must cover Retina coordinate conversion, visual differences, and OCR quality before automated clicks are enabled.
