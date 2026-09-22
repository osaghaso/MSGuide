using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;

namespace MSGuide.Desktop;

internal static class AutomationEvidence
{
    internal const int ScanNodeLimit = 2000;
    internal const int ScanDepthLimit = 64;
    internal const int ScanMilliseconds = 3000;

    private static readonly HashSet<string> KnownAutomationIds =
    [
        "SystemSettings_CapabilityAccess_Camera_SystemGlobal_ToggleSwitch",
        "SystemSettings_CapabilityAccess_Camera_UserGlobal_ToggleSwitch",
        "MSTeams_8wekyb3d8bbwe_ToggleSwitch",
        "SystemSettings_CapabilityAccess_Camera_ClassicGlobal_ToggleSwitch",
        "more-options-header",
        "AudioSettings",
        "VideoSettings",
        "open_camera_settings"
    ];

    internal static string Bounded(string? value, int maximum)
    {
        value = value?.Trim() ?? "";
        return value[..Math.Min(value.Length, maximum)];
    }

    internal static string? Optional(string? value, int maximum)
    {
        var bounded = Bounded(value, maximum);
        return bounded.Length == 0 ? null : bounded;
    }

    internal static bool IsKnownAutomationId(string automationId) =>
        KnownAutomationIds.Contains(automationId);

    internal static string VerifiedPage(IEnumerable<string> automationIds)
    {
        var ids = automationIds.ToHashSet(StringComparer.Ordinal);
        return ids.Contains("SystemSettings_CapabilityAccess_Camera_SystemGlobal_ToggleSwitch")
            && ids.Contains("SystemSettings_CapabilityAccess_Camera_UserGlobal_ToggleSwitch")
                ? "camera-privacy"
                : ids.Contains("VideoSettings")
                    ? "teams-devices"
                    : "unknown";
    }

    internal static string TargetId(WindowChoice window, string role, string label, double[] box,
        string automationId, string frameworkId, int providerProcessId, IReadOnlyList<int>? runtimeId)
    {
        var canonical = new StringBuilder(512)
            .Append("uia-v1|").Append(window.Id).Append('|').Append(window.ClassName)
            .Append('|').Append(role).Append('|').Append(label)
            .Append('|').Append(automationId).Append('|').Append(frameworkId)
            .Append('|').Append(providerProcessId).Append('|');
        foreach (double coordinate in box)
            canonical.Append(coordinate.ToString("R", CultureInfo.InvariantCulture)).Append(',');
        canonical.Append('|');
        if (runtimeId is not null)
            foreach (int part in runtimeId) canonical.Append(part).Append(',');
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return "uia-" + Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant();
    }

    internal static string? ControlId(WindowChoice window, int providerProcessId, IReadOnlyList<int>? runtimeId)
    {
        if (providerProcessId <= 0 || runtimeId is not { Count: > 0 and <= 64 }) return null;
        string identity = string.Join("|", "control-v1", window.Id, window.ClassName,
            providerProcessId.ToString(CultureInfo.InvariantCulture), string.Join(",", runtimeId));
        return "control-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    internal static string? ResourceId(WindowChoice window, string title, ElementInfo[] elements)
    {
        // Generic UIA document trees do not prove a file/site identity. Use an explicit resource handoff.
        if (string.IsNullOrWhiteSpace(title) || elements.Any(e => e.Role == "document")) return null;
        return "resource-" + ValueDigest(string.Join("|", window.Id, window.ClassName, title));
    }

    internal static CacheRequest CaptureCache(bool details = false)
    {
        var cache = new CacheRequest { TreeScope = TreeScope.Element };
        AutomationProperty[] properties =
        [
            AutomationElement.IsPasswordProperty, AutomationElement.IsOffscreenProperty,
            AutomationElement.IsEnabledProperty, AutomationElement.ProcessIdProperty,
            AutomationElement.ControlTypeProperty, AutomationElement.BoundingRectangleProperty
        ];
        foreach (var property in properties) cache.Add(property);
        if (details)
        {
            AutomationProperty[] metadata =
            [
                AutomationElement.NameProperty, AutomationElement.AutomationIdProperty,
                AutomationElement.FrameworkIdProperty, AutomationElement.RuntimeIdProperty,
                AutomationElement.HelpTextProperty, AutomationElement.ItemStatusProperty,
                AutomationElement.IsValuePatternAvailableProperty, AutomationElement.IsTogglePatternAvailableProperty,
                AutomationElement.IsInvokePatternAvailableProperty, AutomationElement.IsSelectionItemPatternAvailableProperty,
                AutomationElement.IsExpandCollapsePatternAvailableProperty, AutomationElement.IsScrollPatternAvailableProperty
            ];
            foreach (var property in metadata) cache.Add(property);
        }
        return cache;
    }

    internal static bool IsSupportedBrowser(WindowChoice window)
    {
        if (!window.ClassName.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal)) return false;
        try
        {
            using var process = Process.GetProcessById((int)window.ProcessId);
            return process.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            DiagnosticLog.Record("browser_identity_unavailable", new { errorType = ex.GetType().Name });
            return false;
        }
    }

    internal static string? BrowserResourceId(WindowChoice window, string address)
    {
        if (address.Length > 2048 || !Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0
            || string.IsNullOrWhiteSpace(uri.Host))
            return null;
        return "browser-" + ValueDigest(string.Join("|", window.Id, window.ClassName, uri.AbsoluteUri));
    }

    internal static bool IsBrowserAddressControl(string role, string name, bool insideDocument) =>
        !insideDocument && role == "edit" && name is "Address and search bar" or "Address bar";

    internal static string? ReadBrowserResourceId(WindowChoice window, CancellationToken token)
    {
        try { return ReadBrowserResourceCore(window, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
            or COMException or UnauthorizedAccessException or ArgumentException)
        {
            DiagnosticLog.Record("browser_resource_inspection_failed", new { errorType = ex.GetType().Name });
            return null;
        }
    }

    private static string? ReadBrowserResourceCore(WindowChoice window, CancellationToken token)
    {
        if (!IsSupportedBrowser(window) || !window.Matches()
            || !Native.GetWindowRect(window.Handle, out var rect)) return null;
        var privacy = CaptureCache();
        var details = CaptureCache(details: true);
        var root = AutomationElement.FromHandle(window.Handle).GetUpdatedCache(privacy);
        if (root.Cached.ProcessId != (int)window.ProcessId) return null;
        var walker = TreeWalker.RawViewWalker;
        var clock = Stopwatch.StartNew();
        int visited = 0, documents = 0, addresses = 0;
        bool incomplete = false;
        string? resource = null;
        AutomationElement? addressControl = null;
        void Walk(AutomationElement node, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (++visited > ScanNodeLimit || depth > ScanDepthLimit || clock.ElapsedMilliseconds >= ScanMilliseconds)
            { incomplete = true; return; }
            var value = node.Cached;
            if (value.IsPassword || value.IsOffscreen) return;
            if (value.ControlType == ControlType.Document)
            {
                if (Safety.AutomationBox(value.BoundingRectangle, rect) is not null) documents++;
                return; // Never accept an address-like field supplied by web content.
            }
            if (value.ControlType == ControlType.TabItem) return;
            if (value.ControlType == ControlType.Edit && value.IsEnabled
                && Safety.AutomationBox(value.BoundingRectangle, rect) is not null)
            {
                var field = node.GetUpdatedCache(details);
                if (!field.Cached.IsPassword && !field.Cached.IsOffscreen
                    && IsBrowserAddressControl("edit", field.Cached.Name, insideDocument: false))
                {
                    addresses++;
                    addressControl = field;
                    if (!field.Current.IsPassword
                        && field.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                        && pattern is ValuePattern address)
                        resource = BrowserResourceId(window, address.Current.Value);
                }
            }
            var child = walker.GetFirstChild(node, privacy);
            while (child is not null && !incomplete)
            {
                Walk(child, depth + 1);
                if (incomplete) break;
                child = walker.GetNextSibling(child, privacy);
            }
        }
        Walk(root, 0);
        token.ThrowIfCancellationRequested();
        if (incomplete || clock.ElapsedMilliseconds >= ScanMilliseconds
            || addresses != 1 || documents != 1 || addressControl is null || resource is null
            || !window.Matches() || !Native.GetWindowRect(window.Handle, out var after) || !rect.Same(after))
            return null;
        var current = addressControl.Current;
        return !current.IsPassword && !current.IsOffscreen && current.IsEnabled
            && addressControl.TryGetCurrentPattern(ValuePattern.Pattern, out var lastPattern)
            && lastPattern is ValuePattern last
            && BrowserResourceId(window, last.Current.Value) == resource ? resource : null;
    }

    internal static bool ResourceMatches(WindowChoice window, string expected, CancellationToken token) =>
        expected.StartsWith("browser-", StringComparison.Ordinal)
            ? ReadBrowserResourceId(window, token) == expected
            : ResourceId(window, Native.Title(window.Handle), []) == expected;

    internal static string? ToggleState(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern)
            || pattern is not TogglePattern toggle) return null;
        return toggle.Current.ToggleState switch
        {
            System.Windows.Automation.ToggleState.On => "on",
            System.Windows.Automation.ToggleState.Off => "off",
            System.Windows.Automation.ToggleState.Indeterminate => "indeterminate",
            _ => null
        };
    }

    internal static string ValueDigest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal sealed record ActionMetadata(string? Name, bool? IsReadOnly = null,
        string? ValueHash = null, int? ValueLength = null, bool? IsSelected = null,
        string[]? ScrollDirections = null, double? HorizontalScrollPercent = null,
        double? VerticalScrollPercent = null);

    internal static ActionMetadata ReadAction(AutomationElement element, bool cachedPatterns = false)
    {
        var current = cachedPatterns ? element.Cached : element.Current;
        if (current.IsPassword || current.IsOffscreen || !current.IsEnabled) return new(null);
        bool TryPattern(AutomationPattern pattern, AutomationProperty available, out object value)
        {
            value = null!;
            return (!cachedPatterns || element.GetCachedPropertyValue(available) is true)
                && element.TryGetCurrentPattern(pattern, out value);
        }
        if (TryPattern(ValuePattern.Pattern, AutomationElement.IsValuePatternAvailableProperty, out var valuePattern)
            && valuePattern is ValuePattern value && !value.Current.IsReadOnly)
        {
            var live = element.Current;
            if (live.IsPassword || live.IsOffscreen || !live.IsEnabled) return new(null);
            string text = value.Current.Value;
            // Full-field replacement only. Large/document editors need a dedicated adapter.
            return text.Length <= 1000
                ? new("set_value", false, ValueDigest(text), text.Length)
                : new(null);
        }
        bool toggle = TryPattern(TogglePattern.Pattern, AutomationElement.IsTogglePatternAvailableProperty, out _);
        bool invoke = TryPattern(InvokePattern.Pattern, AutomationElement.IsInvokePatternAvailableProperty, out _);
        bool select = TryPattern(SelectionItemPattern.Pattern, AutomationElement.IsSelectionItemPatternAvailableProperty, out var selection);
        bool expandable = TryPattern(ExpandCollapsePattern.Pattern, AutomationElement.IsExpandCollapsePatternAvailableProperty, out var pattern);
        string? state = pattern is ExpandCollapsePattern expand
            ? expand.Current.ExpandCollapseState.ToString().ToLowerInvariant()
            : null;
        string? action = ActionName(toggle, invoke, select, expandable, state);
        if (action is not null)
            return new(action, IsSelected: selection is SelectionItemPattern item ? item.Current.IsSelected : null);
        if (TryPattern(ScrollPattern.Pattern, AutomationElement.IsScrollPatternAvailableProperty, out var scrollPattern)
            && scrollPattern is ScrollPattern scroll)
        {
            double horizontal = scroll.Current.HorizontalScrollPercent;
            double vertical = scroll.Current.VerticalScrollPercent;
            var directions = ScrollDirections(horizontal, vertical);
            if (directions.Length > 0)
                return new("scroll", ScrollDirections: directions,
                    HorizontalScrollPercent: horizontal, VerticalScrollPercent: vertical);
        }
        return new(null);
    }

    internal static string? Action(AutomationElement element) => ReadAction(element).Name;

    internal static string[] ScrollDirections(double horizontal, double vertical)
    {
        var directions = new List<string>(4);
        if (horizontal is > 0 and <= 100) directions.Add("left");
        if (horizontal is >= 0 and < 100) directions.Add("right");
        if (vertical is > 0 and <= 100) directions.Add("up");
        if (vertical is >= 0 and < 100) directions.Add("down");
        return directions.ToArray();
    }

    internal static string? ActionName(
        bool toggle, bool invoke, bool select, bool expandable, string? expandState) =>
        toggle ? "toggle"
        : invoke ? "invoke"
        : select ? "select"
        : expandable && expandState == "collapsed" ? "expand"
        : expandable && expandState == "expanded" ? "collapse"
        : null;

    internal static bool MatchesTargetId(
        WindowChoice window, Native.RECT rect, AutomationElement element, string expected)
    {
        var value = element.Current;
        if (value.IsPassword || value.IsOffscreen) return false;
        var box = Safety.AutomationBox(value.BoundingRectangle, rect);
        if (box is null) return false;
        string name = Bounded(value.Name, 256);
        string automationId = Bounded(value.AutomationId, 128);
        if (name.Length == 0 && !IsKnownAutomationId(automationId)) return false;
        int[]? runtimeId = null;
        try { runtimeId = element.GetRuntimeId(); }
        catch (Exception ex) when (
            ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
        if (ControlId(window, value.ProcessId, runtimeId) is null) return false;
        string role = value.ControlType.ProgrammaticName
            .Replace("ControlType.", "").ToLowerInvariant();
        string label = name.Length > 0 ? name : automationId;
        return string.Equals(TargetId(
            window, role, label, box, automationId, Bounded(value.FrameworkId, 64),
            value.ProcessId, runtimeId), expected, StringComparison.Ordinal);
    }

    internal static AutomationElement? FindUniqueTarget(
        WindowChoice window, Native.RECT rect, string targetId, string label,
        string? automationId, CancellationToken cancellationToken, string? resourceId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var privacy = CaptureCache();
        var details = CaptureCache(details: true);
        var root = AutomationElement.FromHandle(window.Handle).GetUpdatedCache(privacy);
        if (root.Cached.ProcessId != (int)window.ProcessId) return null;
        if (resourceId is not null && !ResourceMatches(window, resourceId, cancellationToken)) return null;
        var walker = TreeWalker.RawViewWalker;
        var clock = Stopwatch.StartNew();
        int visited = 0;
        bool incomplete = false, duplicate = false;
        AutomationElement? match = null;
        void Walk(AutomationElement node, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++visited > ScanNodeLimit || depth > ScanDepthLimit || clock.ElapsedMilliseconds >= ScanMilliseconds)
            {
                incomplete = true;
                return;
            }
            var value = node.Cached;
            if (resourceId is not null && !resourceId.StartsWith("browser-", StringComparison.Ordinal)
                && value.ControlType == ControlType.Document)
            {
                incomplete = true;
                return;
            }
            if (value.IsPassword || value.IsOffscreen) return;
            var named = node.GetUpdatedCache(details).Cached;
            if (named.IsPassword || named.IsOffscreen) return;
            bool candidate = string.IsNullOrWhiteSpace(automationId)
                ? named.Name == label : named.AutomationId == automationId;
            if (candidate && MatchesTargetId(window, rect, node, targetId))
            {
                if (match is not null) { duplicate = true; return; }
                match = node;
            }
            var child = walker.GetFirstChild(node, privacy);
            while (child is not null && !incomplete && !duplicate)
            {
                Walk(child, depth + 1);
                if (incomplete || duplicate) break;
                child = walker.GetNextSibling(child, privacy);
            }
        }
        // Unlike FindAll, traversal has node/depth/time ceilings. Individual COM calls
        // can still hang; the action caller contains one late native worker.
        Walk(root, 0);
        return incomplete || duplicate || clock.ElapsedMilliseconds >= ScanMilliseconds
            || resourceId is not null && !ResourceMatches(window, resourceId, cancellationToken) ? null : match;
    }

    internal static AutomationProbeDiagnostic Probe(WindowChoice window, Native.RECT rect, CancellationToken ct)
    {
        int visited = 0, inBounds = 0, enabled = 0, disabled = 0, crossProcess = 0, toggles = 0;
        bool truncated = false;
        var roles = new Dictionary<string, int>(StringComparer.Ordinal);
        var knownControls = new Dictionary<string, KnownControlDiagnostic>(StringComparer.Ordinal);
        var clock = Stopwatch.StartNew();
        try
        {
            var root = AutomationElement.FromHandle(window.Handle);
            if (root.Current.ProcessId != (int)window.ProcessId)
                return Diagnostic(false, 0, 0, 0, 0, 0, 0, false,
                    "root-identity-changed", roles, knownControls);
            var walker = TreeWalker.RawViewWalker;
            void Walk(AutomationElement node, int depth)
            {
                ct.ThrowIfCancellationRequested();
                if (visited >= 800 || depth > 18 || clock.ElapsedMilliseconds >= 3000)
                {
                    truncated = true;
                    return;
                }
                visited++;
                var value = node.Current;
                if (value.IsPassword) return;
                string automationId = Bounded(value.AutomationId, 128);
                if (IsKnownAutomationId(automationId))
                {
                    string? toggleState = null;
                    try { toggleState = ToggleState(node); }
                    catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
                    knownControls[automationId] = new(automationId, value.IsEnabled,
                        value.IsOffscreen, toggleState);
                }
                if (value.IsOffscreen) return;
                if (Safety.AutomationBox(value.BoundingRectangle, rect) is not null)
                {
                    inBounds++;
                    if (value.IsEnabled) enabled++; else disabled++;
                    if (value.ProcessId != (int)window.ProcessId) crossProcess++;
                    string role = value.ControlType.ProgrammaticName.Replace("ControlType.", "").ToLowerInvariant();
                    roles[role] = roles.GetValueOrDefault(role) + 1;
                    try
                    {
                        if (node.TryGetCurrentPattern(TogglePattern.Pattern, out _)) toggles++;
                    }
                    catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
                }
                var child = walker.GetFirstChild(node);
                while (child is not null)
                {
                    Walk(child, depth + 1);
                    if (truncated) break;
                    child = walker.GetNextSibling(child);
                }
            }
            Walk(root, 0);
            return Diagnostic(true, visited, inBounds, enabled, disabled, crossProcess, toggles,
                truncated, "completed", roles, knownControls);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
            or COMException or UnauthorizedAccessException)
        {
            return Diagnostic(true, visited, inBounds, enabled, disabled, crossProcess, toggles,
                truncated, "provider-error", roles, knownControls);
        }
    }

    private static AutomationProbeDiagnostic Diagnostic(bool rootMatched, int visited, int inBounds,
        int enabled, int disabled, int crossProcess, int toggles, bool truncated, string outcome,
        IReadOnlyDictionary<string, int> roles,
        IReadOnlyDictionary<string, KnownControlDiagnostic> knownControls)
    {
        var controls = knownControls.Values.OrderBy(control => control.AutomationId).ToArray();
        string page = VerifiedPage(knownControls.Keys);
        return new(rootMatched, visited, inBounds, enabled, disabled, crossProcess, toggles,
            truncated, outcome, page, roles, controls);
    }
}

public sealed record AutomationProbeDiagnostic(bool RootMatched, int NodesVisited, int InBoundsElements,
    int EnabledElements, int DisabledElements, int CrossProcessElements, int TogglePatterns,
    bool Truncated, string Outcome, string VerifiedPage, IReadOnlyDictionary<string, int> Roles,
    KnownControlDiagnostic[] KnownControls);

public sealed record KnownControlDiagnostic(string AutomationId, bool IsEnabled, bool IsOffscreen,
    string? ToggleState);
