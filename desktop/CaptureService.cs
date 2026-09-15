using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MSGuide.Desktop;

public sealed class Snapshot(WindowChoice window, Native.RECT rect, DateTimeOffset captured,
    byte[] png, BitmapSource preview, ElementInfo[] elements, string text, string note) : IDisposable
{
    public string Id { get; } = Guid.NewGuid().ToString();
    public WindowChoice Window { get; } = window;
    public Native.RECT Rect { get; } = rect;
    public DateTimeOffset CapturedAt { get; } = captured;
    public byte[] Png { get; private set; } = png;
    public BitmapSource? Preview { get; private set; } = preview;
    public ElementInfo[] Elements { get; private set; } = elements;
    public string Text { get; private set; } = text;
    public string Note { get; } = note;
    public bool Valid() => Preview is not null && Safety.Fresh(CapturedAt, DateTimeOffset.UtcNow) && Window.Matches()
        && Native.GetWindowRect(Window.Handle, out var now) && Rect.Same(now);
    public Observation Observation(bool image)
    {
        if (Preview is null) throw new ObjectDisposedException(nameof(Snapshot));
        return new(Id, Window.Id, Window.Title[..Math.Min(Window.Title.Length, 256)], CapturedAt,
            Preview.PixelWidth, Preview.PixelHeight, Text, Elements, image ? Convert.ToBase64String(Png) : null);
    }
    public void Dispose()
    {
        Array.Clear(Png); Png = []; Preview = null; Elements = []; Text = "";
    }
}

public static class CaptureService
{
    // ponytail: one outstanding native capture. A stuck provider cannot queue more workers;
    // process-isolate PrintWindow/UIA if support for unresponsive applications becomes required.
    private static int busy;
    internal static Task WhenIdle { get; private set; } = Task.CompletedTask;

    public static async Task<Snapshot> Capture(WindowChoice window, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            throw new InvalidOperationException("A previous window capture is still returning. Use another application after it finishes, or restart MSGuide.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var captureToken = timeout.Token;
        var task = Task.Run(() =>
        {
            Snapshot? result = null;
            try
            {
                result = CaptureCore(window, captureToken);
                captureToken.ThrowIfCancellationRequested();
                return result;
            }
            catch { result?.Dispose(); throw; }
            finally { Interlocked.Exchange(ref busy, 0); }
        });
        // Includes cleanup of a late result; callers can await native completion without polling.
        WhenIdle = task.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && captureToken.IsCancellationRequested) t.Result.Dispose();
            else if (t.IsFaulted) _ = t.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            var result = await task.WaitAsync(captureToken);
            if (captureToken.IsCancellationRequested) { result.Dispose(); captureToken.ThrowIfCancellationRequested(); }
            return result;
        }
        catch (OperationCanceledException) when (captureToken.IsCancellationRequested)
        {
            // A native call cannot be aborted safely. Drop and clear anything it returns later.
            _ = task.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); else _ = t.Exception; }, TaskScheduler.Default);
            if (!ct.IsCancellationRequested) throw new InvalidOperationException("Window capture timed out. No snapshot was sent. This application may not support PrintWindow/UI Automation.");
            throw;
        }
    }

    private static Snapshot CaptureCore(WindowChoice window, CancellationToken ct)
    {
        var previousDpi = Native.SetThreadDpiAwarenessContext(new nint(-4));
        byte[]? png = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!window.Matches() || Native.IsHungAppWindow(window.Handle)
                || !Native.GetWindowRect(window.Handle, out var rect))
                throw new InvalidOperationException("Selected window disappeared, changed, is minimized, or is not responding. Refresh the chooser.");
            if (rect.Width < 40 || rect.Height < 40 || rect.Width > 12000 || rect.Height > 12000
                || (long)rect.Width * rect.Height > 32_000_000)
                throw new InvalidOperationException("Unsupported window dimensions. Resize the selected window and retry.");
            var captured = DateTimeOffset.UtcNow;
            var preview = ReadWindow(window.Handle, rect, ct);
            ct.ThrowIfCancellationRequested();
            png = Encode(preview);
            while (png.Length > 2_000_000 && preview.PixelWidth > 320 && preview.PixelHeight > 200)
            {
                ct.ThrowIfCancellationRequested();
                Array.Clear(png);
                preview = Resize(preview, 0.75);
                png = Encode(preview);
            }
            if (png.Length > 2_000_000) throw new InvalidOperationException("PNG exceeds the 2 MB limit; select a smaller window.");
            var (elements, text, note) = ReadAutomation(window, rect, ct);
            ct.ThrowIfCancellationRequested();
            if (!window.Matches() || !Native.GetWindowRect(window.Handle, out var after) || !rect.Same(after))
                throw new InvalidOperationException("Window changed during capture. Capture and review again.");
            return new(window, rect, captured, png, preview, elements, text, note);
        }
        catch { if (png is not null) Array.Clear(png); throw; }
        finally { Native.SetThreadDpiAwarenessContext(previousDpi); }
    }

    private static BitmapSource ReadWindow(nint hwnd, Native.RECT rect, CancellationToken ct)
    {
        var dc = Native.CreateCompatibleDC(0);
        if (dc == 0) throw new InvalidOperationException("Cannot allocate a window capture context.");
        nint bitmap = 0, old = 0, bits = 0;
        byte[] pixels = new byte[checked(rect.Width * rect.Height * 4)];
        try
        {
            var info = new Native.BITMAPINFO { Size = 40, Width = rect.Width, Height = -rect.Height, Planes = 1, BitCount = 32 };
            bitmap = Native.CreateDIBSection(dc, ref info, 0, out bits, 0, 0);
            if (bitmap == 0 || bits == 0) throw new InvalidOperationException("Cannot allocate a window bitmap.");
            old = Native.SelectObject(dc, bitmap);
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            // Only the approved HWND renders into this private bitmap. NEVER copy the desktop.
            ct.ThrowIfCancellationRequested();
            if (!Native.PrintWindow(hwnd, dc, 2))
                throw new InvalidOperationException("This window does not support PrintWindow capture. No desktop fallback is used.");
            ct.ThrowIfCancellationRequested();
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            int min = 255, max = 0;
            for (int y = rect.Height / 10; y < rect.Height * 9 / 10; y += Math.Max(1, rect.Height / 80))
            for (int x = rect.Width / 10; x < rect.Width * 9 / 10; x += Math.Max(1, rect.Width / 80))
            {
                int i = (y * rect.Width + x) * 4;
                int light = (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3;
                min = Math.Min(min, light); max = Math.Max(max, light);
            }
            if (max < 8 || max - min < 3)
                throw new InvalidOperationException("Capture appears blank, protected, or unsupported. Nothing was sent. Try the built-in demo; there is no desktop fallback.");
            BitmapSource source = BitmapSource.Create(rect.Width, rect.Height, 96, 96, PixelFormats.Bgr32, null, pixels, rect.Width * 4);
            source.Freeze();
            double scale = Math.Min(1, 1600d / Math.Max(rect.Width, rect.Height));
            return scale < 1 ? Resize(source, scale) : source;
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

    private static BitmapSource Resize(BitmapSource source, double scale)
    {
        var resized = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        resized.Freeze();
        return resized;
    }

    private static byte[] Encode(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        var bytes = buffer.ToArray();
        if (buffer.TryGetBuffer(out var segment)) Array.Clear(segment.Array!, 0, (int)buffer.Length);
        return bytes;
    }

    private static (ElementInfo[], string, string) ReadAutomation(WindowChoice window, Native.RECT rect, CancellationToken ct)
    {
        var elements = new List<ElementInfo>();
        var text = new List<string>();
        var clock = Stopwatch.StartNew();
        int visited = 0, chars = 0;
        string note = "UI Automation names only (not pixel OCR). Password/offscreen subtrees excluded; image is NOT redacted.";
        try
        {
            ct.ThrowIfCancellationRequested();
            var root = AutomationElement.FromHandle(window.Handle);
            var walker = TreeWalker.RawViewWalker;
            void Walk(AutomationElement node, int depth)
            {
                ct.ThrowIfCancellationRequested();
                if (++visited > 800 || depth > 18 || text.Count >= 200 || chars >= 12000 || clock.ElapsedMilliseconds > 3000) return;
                var value = node.Current;
                // Do not read Name, Value, TextPattern or descendants of password controls.
                if (value.IsPassword || value.IsOffscreen || value.ProcessId != (int)window.ProcessId) return;
                var box = Safety.AutomationBox(value.BoundingRectangle, rect);
                if (box is not null)
                {
                    var name = value.Name?.Trim() ?? "";
                    name = name[..Math.Min(name.Length, 256)];
                    if (name.Length > 0 && chars + name.Length + 1 <= 12000)
                    {
                        string role = value.ControlType.ProgrammaticName.Replace("ControlType.", "").ToLowerInvariant();
                        if (value.IsEnabled) elements.Add(new(role, name, box));
                        text.Add(name); chars += name.Length + 1;
                    }
                }
                ct.ThrowIfCancellationRequested();
                if (depth >= 18 || text.Count >= 200 || chars >= 12000 || clock.ElapsedMilliseconds > 3000) return;
                var child = walker.GetFirstChild(node);
                while (child is not null && visited < 800 && text.Count < 200 && chars < 12000 && clock.ElapsedMilliseconds < 3000)
                {
                    Walk(child, depth + 1);
                    ct.ThrowIfCancellationRequested();
                    if (visited >= 800 || text.Count >= 200 || chars >= 12000 || clock.ElapsedMilliseconds >= 3000) break;
                    child = walker.GetNextSibling(child);
                }
            }
            Walk(root, 0);
            if (visited >= 800 || text.Count >= 200 || chars >= 12000 || clock.ElapsedMilliseconds >= 3000) note += " Metadata was bounded/truncated.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException)
        { note += " This application exposed incomplete/no accessible text; review carefully."; }
        return (elements.ToArray(), string.Join('\n', text.Distinct()), note);
    }
}