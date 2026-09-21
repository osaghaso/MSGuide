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
    bool automationComplete = true, string? resourceId = null, string? application = null) : IDisposable
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
    public string? ResourceId { get; } = resourceId;
    private string ApplicationName { get; } = application ?? window.Title;
    public bool Valid() => !disposed && Safety.Fresh(CapturedAt, DateTimeOffset.UtcNow) && Window.Matches()
        && Native.GetWindowRect(Window.Handle, out var now) && Rect.Same(now)
        && (ResourceId is null || (ResourceId.StartsWith("browser-", StringComparison.Ordinal)
            ? Native.Title(Window.Handle) == ApplicationName
            : ResourceId == AutomationEvidence.ResourceId(Window, Native.Title(Window.Handle), Elements)));
    public Observation Observation(bool image)
    {
        if (disposed) throw new ObjectDisposedException(nameof(Snapshot));
        return new(Id, Window.Id, ApplicationName[..Math.Min(ApplicationName.Length, 256)], CapturedAt,
            Preview?.PixelWidth ?? Rect.Width, Preview?.PixelHeight ?? Rect.Height, Text, Elements,
            image && Png.Length > 0 ? Convert.ToBase64String(Png) : null, AutomationComplete, ResourceId);
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
            string resourceTitle = Native.Title(window.Handle);
            bool browser = AutomationEvidence.IsSupportedBrowser(window);
            var browserScope = browser ? AutomationEvidence.ReadBrowserScope(window, ct) : null;
            string? browserResource = browserScope?.ResourceId;
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
            var (elements, text, note, complete) = ReadAutomation(window, rect, ct,
                maxDepth: AutomationEvidence.ScanDepthLimit,
                maxMilliseconds: AutomationEvidence.CaptureScanMilliseconds,
                scopedRoot: browserScope?.Document);
            ct.ThrowIfCancellationRequested();
            if (!window.Matches() || !Native.GetWindowRect(window.Handle, out var after) || !rect.Same(after))
                throw new InvalidOperationException("Window changed during capture. Capture and review again.");
            if (resourceTitle != Native.Title(window.Handle)
                || browser && browserResource != AutomationEvidence.ReadBrowserResourceId(window, ct))
                throw new CaptureResourceChangedException();
            return new(window, rect, captured, png, preview, elements, text, note, complete,
                browser ? browserResource : AutomationEvidence.ResourceId(window, resourceTitle, elements), resourceTitle);
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
        WindowChoice window, Native.RECT rect, CancellationToken ct, int maxDepth = 18,
        int maxMilliseconds = AutomationEvidence.ScanMilliseconds, AutomationElement? scopedRoot = null)
    {
        if (maxDepth is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maxDepth));
        if (maxMilliseconds is < 100 or > 10_000) throw new ArgumentOutOfRangeException(nameof(maxMilliseconds));
        var elements = new List<ElementInfo>();
        var text = new List<string>();
        var clock = Stopwatch.StartNew();
        int visited = 0, chars = 0;
        bool complete = true, textTruncated = false;
        string outcome = "complete";
        string? errorType = null;
        string note = "Bounded UI Automation evidence only (not pixel OCR). Password/offscreen subtrees excluded; cross-process descendants are included only beneath the selected HWND root; image is NOT redacted.";
        try
        {
            ct.ThrowIfCancellationRequested();
            var privacy = AutomationEvidence.CaptureCache();
            var details = AutomationEvidence.CaptureCache(details: true);
            var root = AutomationElement.FromHandle(window.Handle).GetUpdatedCache(privacy);
            if (root.Cached.ProcessId != (int)window.ProcessId)
                throw new InvalidOperationException("UI Automation root identity changed.");
            if (scopedRoot is not null) root = scopedRoot.GetUpdatedCache(privacy);
            // Control View excludes provider layout nodes that make large apps such as Visual Studio
            // exceed the bounded scan while retaining the semantic controls that can authorize actions.
            var walker = TreeWalker.ControlViewWalker;
            void Walk(AutomationElement node, int depth)
            {
                ct.ThrowIfCancellationRequested();
                if (++visited > AutomationEvidence.ScanNodeLimit || depth > maxDepth
                    || clock.ElapsedMilliseconds >= maxMilliseconds)
                {
                    complete = false;
                    outcome = depth > maxDepth ? "depth_limit"
                        : visited > AutomationEvidence.ScanNodeLimit ? "node_limit" : "time_limit";
                    return;
                }
                var value = node.Cached;
                // Do not read Name, Value, TextPattern or descendants of password controls.
                // Cross-process descendants are permitted only through this exact HWND-rooted Control View tree.
                if (value.IsPassword || value.IsOffscreen) return;
                var box = Safety.AutomationBox(value.BoundingRectangle, rect);
                if (box is not null)
                {
                    node = node.GetUpdatedCache(details);
                    value = node.Cached;
                    if (value.IsPassword || value.IsOffscreen) return;
                    box = Safety.AutomationBox(value.BoundingRectangle, rect);
                    var name = AutomationEvidence.Bounded(value.Name, 256);
                    string automationId = AutomationEvidence.Bounded(value.AutomationId, 128);
                    bool knownMarker = AutomationEvidence.IsKnownAutomationId(automationId);
                    if (box is not null && (name.Length > 0 || knownMarker
                        || value.ControlType == ControlType.Document))
                    {
                        string label = name.Length > 0 ? name : knownMarker ? automationId : "Document";
                        string role = value.ControlType.ProgrammaticName.Replace("ControlType.", "").ToLowerInvariant();
                        string frameworkId = AutomationEvidence.Bounded(value.FrameworkId, 64);
                        int[]? runtimeId = null;
                        string? toggleState = null;
                        try { runtimeId = node.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty) as int[]; }
                        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
                        try
                        {
                            if (node.GetCachedPropertyValue(AutomationElement.IsTogglePatternAvailableProperty) is true)
                                toggleState = AutomationEvidence.ToggleState(node);
                        }
                        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
                        var action = new AutomationEvidence.ActionMetadata(null);
                        try { action = AutomationEvidence.ReadAction(node, cachedPatterns: true); }
                        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
                        bool enabled = value.IsEnabled;
                        string? controlId = name.Length > 0 || knownMarker
                            ? AutomationEvidence.ControlId(window, value.ProcessId, runtimeId) : null;
                        elements.Add(new(role, label, box, TargetId: AutomationEvidence.TargetId(window,
                            role, label, box, automationId, frameworkId, value.ProcessId, runtimeId),
                            AutomationId: automationId, FrameworkId: frameworkId,
                            IsEnabled: enabled, Targetable: enabled && controlId is not null, ToggleState: toggleState,
                            HelpText: name.Length > 0 ? AutomationEvidence.Optional(value.HelpText, 256) : null,
                            ItemStatus: name.Length > 0 ? AutomationEvidence.Optional(value.ItemStatus, 128) : null,
                            Action: controlId is null ? null : action.Name, IsReadOnly: action.IsReadOnly,
                            ValueHash: action.ValueHash, ValueLength: action.ValueLength,
                            IsSelected: action.IsSelected, ScrollDirections: action.ScrollDirections,
                            HorizontalScrollPercent: action.HorizontalScrollPercent,
                            VerticalScrollPercent: action.VerticalScrollPercent, ControlId: controlId));
                        if (name.Length > 0)
                        {
                            if (chars + name.Length + 1 <= 12000 && text.Count < 200)
                            {
                                text.Add(name);
                                chars += name.Length + 1;
                            }
                            else textTruncated = true;
                        }
                    }
                }
                ct.ThrowIfCancellationRequested();
                if (clock.ElapsedMilliseconds >= maxMilliseconds)
                {
                    complete = false;
                    outcome = "time_limit";
                    return;
                }
                var child = walker.GetFirstChild(node, privacy);
                if (depth >= maxDepth)
                {
                    if (child is not null) { complete = false; outcome = "depth_limit"; }
                    return;
                }
                while (child is not null && visited < AutomationEvidence.ScanNodeLimit
                    && clock.ElapsedMilliseconds < maxMilliseconds)
                {
                    Walk(child, depth + 1);
                    ct.ThrowIfCancellationRequested();
                    if (visited >= AutomationEvidence.ScanNodeLimit
                        || clock.ElapsedMilliseconds >= maxMilliseconds)
                    {
                        complete = false;
                        outcome = visited >= AutomationEvidence.ScanNodeLimit ? "node_limit" : "time_limit";
                        break;
                    }
                    child = walker.GetNextSibling(child, privacy);
                }
                if (child is not null) complete = false;
            }
            Walk(root, 0);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException)
        {
            complete = false;
            outcome = "provider_error";
            errorType = ex.GetType().Name;
            note += " This application exposed incomplete/no accessible text; review carefully.";
        }
        var ambiguousIds = elements.Where(e => e.ControlId is not null)
            .GroupBy(e => e.ControlId).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        var duplicateTargets = elements.GroupBy(e => e.TargetId).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet();
        for (int index = 0; index < elements.Count; index++)
            if (ambiguousIds.Contains(elements[index].ControlId) || duplicateTargets.Contains(elements[index].TargetId))
                elements[index] = elements[index] with { ControlId = null, TargetId = null, Action = null, Targetable = false };
        var result = BoundEvidence(elements, string.Join('\n', text.Distinct()), note, complete, textTruncated);
        DiagnosticLog.Record("uia_inspection", new
        {
            visited, retained = result.Elements.Length, result.Complete, result.ContextTruncated,
            outcome = complete && !result.Complete ? "control_limit" : outcome,
            errorType,
            elapsedMs = clock.ElapsedMilliseconds
        });
        return result;
    }

    internal static AutomationReadResult BoundEvidence(IReadOnlyList<ElementInfo> elements, string text,
        string note, bool complete, bool textTruncated = false)
    {
        static bool Priority(ElementInfo element) => element.Action is not null
            || element.Role == "document" || AutomationEvidence.IsKnownAutomationId(element.AutomationId);
        var controls = elements.Where(Priority).ToArray();
        bool truncated = textTruncated || elements.Count > 200;
        var retained = controls.Concat(elements.Where(element => !Priority(element))).Take(200).ToArray();
        complete &= controls.Length <= 200;
        if (!complete) note += " The controls inspection was incomplete; no automation is authorized.";
        else if (truncated) note += " Non-action context was shortened; all inspected action controls were retained.";
        return new(retained, text, note, complete) { ContextTruncated = truncated };
    }
}

internal sealed class CaptureResourceChangedException()
    : InvalidOperationException("The selected resource changed during capture. Review it before continuing.");

internal sealed record AutomationReadResult(
    ElementInfo[] Elements, string Text, string Note, bool Complete)
{
    internal bool ContextTruncated { get; init; }

    public ElementInfo[] RequireComplete()
    {
        if (!Complete || ContextTruncated) throw new IncompleteAutomationReadException();
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