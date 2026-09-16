using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MSGuide.Desktop;

internal interface ISelectedWindowFrameCapture
{
    string Name { get; }
    BitmapSource Capture(WindowChoice window, Native.RECT rect, CancellationToken ct);
}

internal sealed class PrintWindowFrameCapture(uint flags) : ISelectedWindowFrameCapture
{
    private sealed record Result(bool NativeSucceeded, bool Blank, int SampleMinimum,
        int SampleMaximum, BitmapSource? Frame);

    public string Name => $"PrintWindow({flags})";

    public BitmapSource Capture(WindowChoice window, Native.RECT rect, CancellationToken ct)
    {
        var result = Execute(window.Handle, rect, ct, includeFrame: true);
        if (!result.NativeSucceeded)
            throw new InvalidOperationException("This window does not support PrintWindow capture. No desktop fallback is used.");
        if (result.Blank)
            throw new InvalidOperationException("Capture appears blank, protected, or unsupported. Nothing was sent. Try the built-in demo; there is no desktop fallback.");
        return result.Frame!;
    }

    internal CaptureAttemptDiagnostic Probe(WindowChoice window, Native.RECT rect, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var result = Execute(window.Handle, rect, ct, includeFrame: false);
            string outcome = !result.NativeSucceeded ? "native-failure" : result.Blank ? "blank-or-protected" : "accepted";
            return new(Name, flags, result.NativeSucceeded, result.NativeSucceeded && !result.Blank,
                outcome, result.SampleMinimum, result.SampleMaximum, clock.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or OutOfMemoryException or ExternalException)
        {
            return new(Name, flags, false, false, "capture-error", -1, -1, clock.ElapsedMilliseconds);
        }
    }

    private Result Execute(nint hwnd, Native.RECT rect, CancellationToken ct, bool includeFrame)
    {
        var dc = Native.CreateCompatibleDC(0);
        if (dc == 0) throw new InvalidOperationException("Cannot allocate a window capture context.");
        nint bitmap = 0, old = 0, bits = 0;
        byte[] pixels = new byte[checked(rect.Width * rect.Height * 4)];
        try
        {
            var info = new Native.BITMAPINFO
            {
                Size = 40, Width = rect.Width, Height = -rect.Height, Planes = 1, BitCount = 32
            };
            bitmap = Native.CreateDIBSection(dc, ref info, 0, out bits, 0, 0);
            if (bitmap == 0 || bits == 0) throw new InvalidOperationException("Cannot allocate a window bitmap.");
            old = Native.SelectObject(dc, bitmap);
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            ct.ThrowIfCancellationRequested();
            if (!Native.PrintWindow(hwnd, dc, flags)) return new(false, false, -1, -1, null);
            ct.ThrowIfCancellationRequested();
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            int min = 255, max = 0;
            for (int y = rect.Height / 10; y < rect.Height * 9 / 10; y += Math.Max(1, rect.Height / 80))
            for (int x = rect.Width / 10; x < rect.Width * 9 / 10; x += Math.Max(1, rect.Width / 80))
            {
                int i = (y * rect.Width + x) * 4;
                int light = (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3;
                min = Math.Min(min, light);
                max = Math.Max(max, light);
            }
            bool blank = max < 8 || max - min < 3;
            BitmapSource? frame = null;
            if (includeFrame && !blank)
            {
                frame = BitmapSource.Create(rect.Width, rect.Height, 96, 96, PixelFormats.Bgr32,
                    null, pixels, rect.Width * 4);
                frame.Freeze();
            }
            return new(true, blank, min, max, frame);
        }
        finally
        {
            Array.Clear(pixels);
            if (bits != 0) Marshal.Copy(pixels, 0, bits, pixels.Length);
            if (old != 0) Native.SelectObject(dc, old);
            if (bitmap != 0) Native.DeleteObject(bitmap);
            Native.DeleteDC(dc);
        }
    }
}

public sealed record CaptureAttemptDiagnostic(string Backend, uint Flags, bool NativeSucceeded,
    bool Accepted, string Outcome, int SampleMinimum, int SampleMaximum, long ElapsedMilliseconds);

public sealed record CaptureProbeReport(int Version, DateTimeOffset CapturedAt, string WindowId,
    uint ProcessId, string WindowClass, int Width, int Height, CaptureAttemptDiagnostic[] Attempts,
    AutomationProbeDiagnostic Automation);

public static class CaptureProbe
{
    public static CaptureProbeReport Run(nint hwnd, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        var window = new WindowChoice(hwnd, pid, Native.Title(hwnd), Native.WindowClass(hwnd));
        var previousDpi = Native.SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            if (!window.Matches() || Native.IsHungAppWindow(hwnd) || !Native.GetWindowRect(hwnd, out var rect))
                throw new InvalidOperationException("The requested HWND is not an available top-level window.");
            if (rect.Width < 40 || rect.Height < 40 || rect.Width > 12000 || rect.Height > 12000
                || (long)rect.Width * rect.Height > 32_000_000)
                throw new InvalidOperationException("The requested HWND has unsupported dimensions.");

            CaptureAttemptDiagnostic[] attempts =
            [
                new PrintWindowFrameCapture(0).Probe(window, rect, ct),
                new PrintWindowFrameCapture(2).Probe(window, rect, ct)
            ];
            var automation = AutomationEvidence.Probe(window, rect, ct);
            ct.ThrowIfCancellationRequested();
            if (!window.Matches() || !Native.GetWindowRect(hwnd, out var after) || !rect.Same(after))
                throw new InvalidOperationException("The requested HWND changed during the probe.");
            return new(1, DateTimeOffset.UtcNow, window.Id, pid, window.ClassName,
                rect.Width, rect.Height, attempts, automation);
        }
        finally { Native.SetThreadDpiAwarenessContext(previousDpi); }
    }

    public static void WriteJson(nint hwnd, string path, CancellationToken ct = default)
    {
        var report = Run(hwnd, ct);
        var json = JsonSerializer.Serialize(report,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        File.WriteAllText(path, json + Environment.NewLine);
    }
}
