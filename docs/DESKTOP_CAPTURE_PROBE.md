# Selected-window capture probe

The probe compares Windows.Graphics.Capture with `PrintWindow` flags `0` and
`2` for one explicit, visible, non-minimized HWND. It never falls back to
desktop capture and writes only JSON
diagnostics (dimensions, timings, success/blank classification, PID, class,
opaque window ID, and bounded aggregate UIA counts by control type). It does
not write screenshot pixels, arbitrary UIA names/help text, or window titles.
Only allowlisted page-verification AutomationIds and their state are persisted.

```powershell
dotnet run --project desktop\CaptureProbe\MSGuide.CaptureProbe.csproj -- `
  --self-test `
  --output .\wgc-self-test.json

dotnet run --project desktop\CaptureProbe\MSGuide.CaptureProbe.csproj -- `
  --hwnd 0x01234567 `
  --output .\capture-probe.json
```

Run `--self-test` first. It creates one visible synthetic WPF window and succeeds
only when the WGC attempt is accepted and the exact HWND-rooted UIA probe
matches. It does not require the backend.

Obtain the HWND from a trusted window-inspection tool and run once for each New
Teams or Windows Camera Settings state being evaluated. Keep the target visible
and stationary until the command finishes.

Production capture uses Windows.Graphics.Capture with an exact-HWND
`IGraphicsCaptureItemInterop.CreateForWindow`, a BGRA-capable D3D11 device, a
free-threaded frame pool, and bounded `SoftwareBitmap` readback. It fails closed
without PrintWindow or desktop fallback. The PrintWindow attempts are diagnostic
only.

References:

- https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/tree/master/dotnet/WPF/ScreenCapture
- https://learn.microsoft.com/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow

## Live target findings (2026-09-16)

- On the pinned target machine, PrintWindow flag `0` returned black for both New
  Teams and Camera Settings. Flag `2` returned rendered content for both,
  including the live Teams preview. Flag `2` remains diagnostic-only because it
  is undocumented; results must be measured and blank/protected failures remain
  fail-closed. Delete probe artifacts after review and never persist preview
  pixels.
- New Teams exposed a rich WebView UIA subtree whose provider process differed
  from the selected top-level window process. Production traversal therefore
  anchors the Raw View walk at the exact selected HWND and permits bounded
  cross-process descendants. Evidence includes AutomationId, FrameworkId,
  enabled/targetable state, and TogglePattern state. Observed Teams IDs included
  `more-options-header`, `AudioSettings`, and `VideoSettings`.
- `ms-settings:privacy-webcam` opened Settings Home while an existing
  SystemSettings process was alive. After that specific process was closed and
  Settings was relaunched, the correct Camera page exposed rich cross-process
  UIA. Never infer page identity from the URI: require both
  `SystemSettings_CapabilityAccess_Camera_SystemGlobal_ToggleSwitch` and
  `SystemSettings_CapabilityAccess_Camera_UserGlobal_ToggleSwitch`.
- The preferred pinned-machine fixture is
  `MSTeams_8wekyb3d8bbwe_ToggleSwitch`, observed enabled and visible with an
  `off`/`on` TogglePattern state. MSGuide may identify and highlight this
  control, but the user performs the change. The desktop-wide
  `SystemSettings_CapabilityAccess_Camera_ClassicGlobal_ToggleSwitch` may be
  offscreen and is not the golden-path target.
- Probe JSON reports `verifiedPage` as `camera-privacy`, `teams-devices`, or
  `unknown`, plus allowlisted marker states. An unknown page must route to an
  explicit fixture/unsupported fallback rather than assuming deep-link success.
- A 20-run census found Teams `VideoSettings` and the Camera Settings packaged
  Teams toggle in every read; reads completed in 119-232 ms and provider PIDs
  consistently differed from top-level PIDs. `open_camera_settings` was not
  realized in those reads and is not a page-verification requirement. Use the
  fixed Camera Settings URI, then verify the Camera page markers before
  presenting the user-performed toggle step.
- The legacy owned-window CaptureTest produced a blank PrintWindow frame and
  IntegrationTest failed foreground activation earlier on this machine. Those
  results do not weaken WGC or page-verification requirements.
