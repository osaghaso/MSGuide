using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;

namespace MSGuide.Desktop;

internal static class AutomationEvidence
{
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
        string role = value.ControlType.ProgrammaticName
            .Replace("ControlType.", "").ToLowerInvariant();
        string label = name.Length > 0 ? name : automationId;
        return string.Equals(TargetId(
            window, role, label, box, automationId, Bounded(value.FrameworkId, 64),
            value.ProcessId, runtimeId), expected, StringComparison.Ordinal);
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
