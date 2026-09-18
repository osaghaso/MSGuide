using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MSGuide.Desktop;

public sealed class Snapshot(WindowChoice window, Native.RECT rect, DateTimeOffset captured,
    byte[] png, BitmapSource? preview, ElementInfo[] elements, string text, string note,
    bool automationComplete = true) : IDisposable
{
    private bool disposed;
    public string Id { get; } = Guid.NewGuid().ToString();
    public WindowChoice Window { get; } = window;
    public Native.RECT Rect { get; } = rect;
    public DateTimeOffset CapturedAt { get; } = captured;
    public byte[] Png { get; private set; } = png;
    public BitmapSource? Preview { get; private set; } = preview;
    public ElementInfo[] Elements { get; private set; } = elements;
    public string Text { get; private set; } = text;
    public string Note { get; } = note;
    public bool AutomationComplete { get; } = automationComplete;
    public bool Valid() => !disposed && Safety.Fresh(CapturedAt, DateTimeOffset.UtcNow) && Window.Matches()
        && Native.GetWindowRect(Window.Handle, out var now) && Rect.Same(now);
    public Observation Observation(bool image)
    {
        if (disposed) throw new ObjectDisposedException(nameof(Snapshot));
        return new(Id, Window.Id, Window.Title[..Math.Min(Window.Title.Length, 256)], CapturedAt,
            Preview?.PixelWidth ?? Rect.Width, Preview?.PixelHeight ?? Rect.Height, Text, Elements,
            image && Png.Length > 0 ? Convert.ToBase64String(Png) : null, AutomationComplete);
    }
    public void Dispose()
    {
        disposed = true;
        Array.Clear(Png); Png = []; Preview = null; Elements = []; Text = "";
    }
}

public static class CaptureService
{
    private static readonly ISelectedWindowFrameCapture FrameCapture = new WindowsGraphicsCaptureFrameCapture();

    // ponytail: one outstanding native capture. A stuck provider cannot queue more workers;
    // process-isolate UIA if support for unresponsive applications becomes required.
    private static int busy;
    internal static Task WhenIdle { get; private set; } = Task.CompletedTask;

    public static async Task<Snapshot> Capture(WindowChoice window, CancellationToken ct, bool includeImage = true)
    {
        ct.ThrowIfCancellationRequested();
        if (DesktopAction.IsBusy)
            throw new InvalidOperationException("A native action is still returning. No new capture or action can start until it finishes.");
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
                result = CaptureCore(window, captureToken, includeImage);
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
            if (!ct.IsCancellationRequested) throw new InvalidOperationException("Window capture timed out. No snapshot was sent. This application may not support Windows Graphics Capture/UI Automation.");
            throw;
        }
    }

    private static Snapshot CaptureCore(WindowChoice window, CancellationToken ct, bool includeImage)
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
            BitmapSource? preview = null;
            png = [];
            if (includeImage)
            {
                preview = ReadWindow(window, rect, ct);
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
            }
            var (elements, text, note, complete) = ReadAutomation(window, rect, ct);
            ct.ThrowIfCancellationRequested();
            if (!window.Matches() || !Native.GetWindowRect(window.Handle, out var after) || !rect.Same(after))
                throw new InvalidOperationException("Window changed during capture. Capture and review again.");
            return new(window, rect, captured, png, preview, elements, text, note, complete);
        }
        catch { if (png is not null) Array.Clear(png); throw; }
        finally { Native.SetThreadDpiAwarenessContext(previousDpi); }
    }

    private static BitmapSource ReadWindow(WindowChoice window, Native.RECT rect, CancellationToken ct)
    {
        // The backend receives only the approved HWND. It must never copy the desktop.
        var source = FrameCapture.Capture(window, rect, ct);
        double scale = Math.Min(1, 1280d / Math.Max(rect.Width, rect.Height));
        return scale < 1 ? Resize(source, scale) : source;
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

    internal static AutomationReadResult ReadAutomation(
        WindowChoice window, Native.RECT rect, CancellationToken ct, int maxDepth = 18)
    {
        if (maxDepth is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maxDepth));
        var elements = new List<ElementInfo>();
        var text = new List<string>();
        var clock = Stopwatch.StartNew();
        int visited = 0, chars = 0;
        bool complete = true;
        string note = "Bounded UI Automation evidence only (not pixel OCR). Password/offscreen subtrees excluded; cross-process descendants are included only beneath the selected HWND root; image is NOT redacted.";
        try
        {
            ct.ThrowIfCancellationRequested();
            var root = AutomationElement.FromHandle(window.Handle);
            if (root.Current.ProcessId != (int)window.ProcessId)
                throw new InvalidOperationException("UI Automation root identity changed.");
            var walker = TreeWalker.RawViewWalker;
            void Walk(AutomationElement node, int depth)
            {
                ct.ThrowIfCancellationRequested();
                if (++visited > 800 || depth > maxDepth || elements.Count >= 200
                    || text.Count >= 200 || chars >= 12000 || clock.ElapsedMilliseconds > 3000)
                {
                    complete = false;
                    return;
                }
                var value = node.Current;
                // Do not read Name, Value, TextPattern or descendants of password controls.
                // Cross-process descendants are permitted only through this exact HWND-rooted Raw View tree.
                if (value.IsPassword || value.IsOffscreen) return;
                var box = Safety.AutomationBox(value.BoundingRectangle, rect);
                if (box is not null)
                {
                    var name = AutomationEvidence.Bounded(value.Name, 256);
                    string automationId = AutomationEvidence.Bounded(value.AutomationId, 128);
                    bool knownMarker = AutomationEvidence.IsKnownAutomationId(automationId);
                    if ((name.Length > 0 || knownMarker)
                        && (name.Length == 0 || chars + name.Length + 1 <= 12000))
                    {
                        string label = name.Length > 0 ? name : automationId;
                        string role = value.ControlType.ProgrammaticName.Replace("ControlType.", "").ToLowerInvariant();
                        string frameworkId = AutomationEvidence.Bounded(value.FrameworkId, 64);
                        int[]? runtimeId = null;
                        string? toggleState = null;
                        try { runtimeId = node.GetRuntimeId(); }
                        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
                        try { toggleState = AutomationEvidence.ToggleState(node); }
                        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
                        var action = new AutomationEvidence.ActionMetadata(null);
                        try { action = AutomationEvidence.ReadAction(node); }
                        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
                        bool enabled = value.IsEnabled;
                        elements.Add(new(role, label, box, TargetId: AutomationEvidence.TargetId(window,
                            role, label, box, automationId, frameworkId, value.ProcessId, runtimeId),
                            AutomationId: automationId, FrameworkId: frameworkId,
                            IsEnabled: enabled, Targetable: enabled, ToggleState: toggleState,
                            HelpText: name.Length > 0 ? AutomationEvidence.Optional(value.HelpText, 256) : null,
                            ItemStatus: name.Length > 0 ? AutomationEvidence.Optional(value.ItemStatus, 128) : null,
                            Action: action.Name, IsReadOnly: action.IsReadOnly,
                            ValueHash: action.ValueHash, ValueLength: action.ValueLength,
                            IsSelected: action.IsSelected, ScrollDirections: action.ScrollDirections,
                            HorizontalScrollPercent: action.HorizontalScrollPercent,
                            VerticalScrollPercent: action.VerticalScrollPercent));
                        if (name.Length > 0)
                        {
                            text.Add(name);
                            chars += name.Length + 1;
                        }
                    }
                    else if (name.Length > 0 && chars + name.Length + 1 > 12000)
                        complete = false;
                }
                ct.ThrowIfCancellationRequested();
                if (elements.Count >= 200 || text.Count >= 200
                    || chars >= 12000 || clock.ElapsedMilliseconds > 3000)
                {
                    complete = false;
                    return;
                }
                var child = walker.GetFirstChild(node);
                if (depth >= maxDepth)
                {
                    if (child is not null) complete = false;
                    return;
                }
                while (child is not null && visited < 800 && elements.Count < 200
                    && text.Count < 200 && chars < 12000 && clock.ElapsedMilliseconds < 3000)
                {
                    Walk(child, depth + 1);
                    ct.ThrowIfCancellationRequested();
                    if (visited >= 800 || elements.Count >= 200 || text.Count >= 200
                        || chars >= 12000 || clock.ElapsedMilliseconds >= 3000)
                    {
                        complete = false;
                        break;
                    }
                    child = walker.GetNextSibling(child);
                }
                if (child is not null) complete = false;
            }
            Walk(root, 0);
            if (visited >= 800 || elements.Count >= 200 || text.Count >= 200
                || chars >= 12000 || clock.ElapsedMilliseconds >= 3000) complete = false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException)
        {
            complete = false;
            note += " This application exposed incomplete/no accessible text; review carefully.";
        }
        if (!complete) note += " Metadata was bounded/truncated.";
        return new(elements.ToArray(), string.Join('\n', text.Distinct()), note, complete);
    }
}

internal sealed record AutomationReadResult(
    ElementInfo[] Elements, string Text, string Note, bool Complete)
{
    public ElementInfo[] RequireComplete()
    {
        if (!Complete) throw new IncompleteAutomationReadException();
        return Elements;
    }
}

internal sealed class IncompleteAutomationReadException : InvalidOperationException
{
    public IncompleteAutomationReadException()
        : base("The controls inspection was incomplete. No camera state or action was accepted. Inspect the selected camera screen again.")
    {
    }
}