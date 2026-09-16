# Selected-window capture probe

The probe compares Windows.Graphics.Capture with `PrintWindow` flags `0` and
`2` for one explicit, visible, non-minimized HWND. It never falls back to
desktop capture and writes only JSON
diagnostics (dimensions, timings, success/blank classification, PID, class,
opaque window ID, and bounded aggregate UIA counts by control type). It does
not write screenshot pixels, UIA names/IDs/help text, or window titles.

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
