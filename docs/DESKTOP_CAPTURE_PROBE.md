# Selected-window capture probe

The probe compares `PrintWindow` flags `0` and `2` for one explicit, visible,
non-minimized HWND. It never falls back to desktop capture and writes only JSON
diagnostics (dimensions, timings, success/blank classification, PID, class,
opaque window ID, and bounded aggregate UIA counts by control type). It does
not write screenshot pixels, UIA names/IDs/help text, or window titles.

```powershell
dotnet run --project desktop\CaptureProbe\MSGuide.CaptureProbe.csproj -- `
  --hwnd 0x01234567 `
  --output .\capture-probe.json
```

Obtain the HWND from a trusted window-inspection tool and run once for each New
Teams or Windows Camera Settings state being evaluated. Keep the target visible
and stationary until the command finishes.

Production capture still uses `PrintWindow(..., 2)`. Windows.Graphics.Capture is
not claimed as implemented: Microsoft's WPF sample requires a D3D11 device,
WinRT `IDirect3DDevice`, `IGraphicsCaptureItemInterop.CreateForWindow`, frame
pool/session lifetime, and GPU-surface readback. `ISelectedWindowFrameCapture`
now isolates that future backend without weakening selected-HWND or fail-closed
invariants.

References:

- https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/tree/master/dotnet/WPF/ScreenCapture
- https://learn.microsoft.com/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow
