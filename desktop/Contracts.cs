using System.Net;
using System.Text.Json.Serialization;
using System.Windows;

namespace MSGuide.Desktop;

public sealed record ElementInfo(string Role, string Label, double[] Box, double Confidence = 0.95,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TargetId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string AutomationId = "",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string FrameworkId = "",
    bool IsEnabled = true, bool Targetable = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ToggleState = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HelpText = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ItemStatus = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Action = null,
    bool IsOffscreen = false, bool IsPassword = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsReadOnly = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ValueHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ValueLength = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsSelected = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? ScrollDirections = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? HorizontalScrollPercent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? VerticalScrollPercent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ControlId = null);
public sealed record Observation(string Id, string WindowId, string Application, DateTimeOffset CapturedAt,
    int Width, int Height, string OcrText, ElementInfo[] Elements,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImageBase64,
    bool AutomationComplete = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResourceId = null);
public sealed record TaskStep(int Step, string ObservationId, string? AfterObservationId,
    string TargetId, string Label, string Action, string Outcome);
public sealed record TaskProgress(string TaskId, int Step, string Status, TaskStep[] History,
    string RemainingWork, string UserInput = "",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PlanSegment? Plan = null,
    int PlanCursor = 0, string ReplanReason = "");
public sealed record GuidanceRequest(string SessionId, string Prompt, bool Consent, Observation Observation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TaskProgress? Task = null,
    bool PlanSegments = true);
public sealed record SessionInfo(string SessionId, DateTimeOffset ExpiresAt);
public sealed record HealthInfo(string Status, string Mode, string Version);
public sealed record TargetInfo(string Label, double[] Box, double Confidence,
    string? TargetId = null, string? AutomationId = null, string? FrameworkId = null,
    bool? IsEnabled = null, bool? IsOffscreen = null, string? ToggleState = null,
    string? Action = null, string? Value = null, string? ScrollDirection = null, string? ValueHash = null,
    string? ControlId = null, bool? IsSelected = null);
public sealed record Citation(string Source, string Title);
public sealed record Guidance(string CorrelationId, string ObservationId, string WindowId, string Instruction,
    string Status, TargetInfo? Target, Citation[]? Citations, string Mode,
    string? RemainingWork = null, string? TaskId = null, int? Step = null, PlanSegment? Plan = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlanIntent(string Role, string Label, string Action,
    string? AutomationId = null, string? FrameworkId = null, string? ToggleState = null,
    bool? IsSelected = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlanAction(string Kind, string Instruction, PlanIntent? Intent = null,
    string? ControlId = null, string? Value = null, string? ScrollDirection = null, string? ValueHash = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlanBoundary(string Kind, string Reason, string Needed = "");
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlanSegment(string PlanId, string WindowId, PlanAction[] Steps,
    PlanBoundary Boundary, string? ResourceId = null);

public static class Safety
{
    internal const int MaxPlanSteps = 32;

    public static Uri ApiUri(string? value)
    {
        if (!Uri.TryCreate(value ?? "http://127.0.0.1:8000", UriKind.Absolute, out var uri)
            || uri.Scheme != "http" || uri.UserInfo.Length != 0 || uri.Query.Length != 0
            || uri.Fragment.Length != 0 || uri.AbsolutePath != "/"
            || !(uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                 || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip))))
            throw new InvalidOperationException("MSGUIDE_API_URL must be a loopback HTTP origin, for example http://127.0.0.1:8000. Remote endpoints are blocked.");
        // Pin localhost to a literal loopback address; do not rely on DNS or a proxy.
        return uri.Host == "localhost" ? new UriBuilder(uri) { Host = "127.0.0.1" }.Uri : uri;
    }

    public static bool ValidBox(double[]? b) => b is { Length: 4 }
        && b.All(double.IsFinite) && b[0] >= 0 && b[1] >= 0 && b[2] > 0 && b[3] > 0
        && b[0] + b[2] <= 1 && b[1] + b[3] <= 1;

    public static Rect PhysicalTarget(Native.RECT r, double[] b) =>
        new(r.Left + b[0] * r.Width, r.Top + b[1] * r.Height, b[2] * r.Width, b[3] * r.Height);

    internal static double[]? AutomationBox(Rect bounds, Native.RECT window)
    {
        if (bounds.IsEmpty || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y)
            || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)
            || window.Width <= 0 || window.Height <= 0) return null;
        bounds.Intersect(new Rect(window.Left, window.Top, window.Width, window.Height));
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return null;
        double x = (bounds.Left - window.Left) / window.Width, y = (bounds.Top - window.Top) / window.Height;
        double[] box = [x, y, Math.Min(bounds.Width / window.Width, 1 - x), Math.Min(bounds.Height / window.Height, 1 - y)];
        return ValidBox(box) ? box : null;
    }

    internal static bool ObservedTarget(TargetInfo target, ElementInfo[] elements) =>
        !string.IsNullOrWhiteSpace(target.Label) && double.IsFinite(target.Confidence)
        && target.Confidence is >= 0.8 and <= 1 && ValidBox(target.Box)
        && (target.ControlId is null || elements.Count(e => e.ControlId == target.ControlId) == 1)
        && elements.Count(e => e.Targetable && e.IsEnabled && !e.IsOffscreen && !e.IsPassword
            && e.Confidence >= target.Confidence && e.Label == target.Label
            && (target.TargetId is null || target.TargetId == e.TargetId)
            && (target.AutomationId is null || target.AutomationId == e.AutomationId)
            && (target.FrameworkId is null || target.FrameworkId == e.FrameworkId)
            && (target.ControlId is null || target.ControlId == e.ControlId)
            && (target.IsSelected is null || target.IsSelected == e.IsSelected)
            && target.IsEnabled is not false && target.IsOffscreen is not true
            && target.ToggleState == e.ToggleState && target.ValueHash == e.ValueHash
            && target.Action == e.Action && ValidBox(e.Box) && ValidActionInput(target)
            && (target.Action is null || !string.IsNullOrEmpty(target.TargetId))
            && (target.Action != "set_value" || e.IsReadOnly is false && e.ValueLength is >= 0 and <= 1000)
            && (target.Action != "scroll" || e.ScrollDirections?.Contains(target.ScrollDirection!) == true)
            && e.Box.Zip(target.Box).All(pair => Math.Abs(pair.First - pair.Second) < 0.000001)) == 1;

    internal static bool ValidActionInput(TargetInfo target) =>
        target.Action is null or "invoke" or "toggle" or "select" or "expand" or "collapse" or "set_value" or "scroll"
        && (target.Action == "set_value"
            ? target.Value is { Length: <= 1000 } && target.ValueHash is { Length: 64 }
                && target.ValueHash.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9')
                && target.Value.All(c => !char.IsControl(c) || c is '\r' or '\n' or '\t')
            : target.Value is null)
        && (target.Action == "scroll"
            ? target.ScrollDirection is "up" or "down" or "left" or "right"
            : target.ScrollDirection is null)
        && (target.Action != "toggle" || target.ToggleState is "off" or "on");

    internal static bool EvidenceId(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '-');

    private static bool IntentMatches(PlanIntent intent, ElementInfo element) =>
        intent.Role == element.Role && intent.Label == element.Label && intent.Action == element.Action
        && (intent.AutomationId is null || intent.AutomationId == element.AutomationId)
        && (intent.FrameworkId is null || intent.FrameworkId == element.FrameworkId)
        && (intent.Action != "toggle" || intent.ToggleState == element.ToggleState)
        && (intent.Action != "select" || element.IsSelected == false);

    internal static TargetInfo? BindPlanAction(PlanAction step, Observation observation)
    {
        if (!observation.AutomationComplete || step.Kind != "action" || step.Intent is not { } intent)
            return null;
        var matches = observation.Elements.Where(e => IntentMatches(intent, e)
            && (step.ControlId is null || e.ControlId == step.ControlId)).ToArray();
        if (matches.Length != 1) return null;
        var element = matches[0];
        if (!EvidenceId(element.ControlId)
            || observation.Elements.Count(e => e.ControlId == element.ControlId) != 1
            || intent.Action == "set_value" && (element.ValueHash != step.ValueHash
                || step.ControlId is null && element.ValueLength != 0))
            return null;
        var target = new TargetInfo(element.Label, element.Box, element.Confidence,
            element.TargetId, element.AutomationId, element.FrameworkId, element.IsEnabled, element.IsOffscreen,
            element.ToggleState, element.Action, step.Value, step.ScrollDirection, element.ValueHash,
            element.ControlId, intent.Action == "select" ? false : null);
        return ObservedTarget(target, observation.Elements) ? target : null;
    }

    internal static bool ValidPlan(PlanSegment plan, Observation observation)
    {
        if (!Guid.TryParseExact(plan.PlanId, "D", out _) || plan.WindowId != observation.WindowId
            || plan.ResourceId != observation.ResourceId
            || plan.ResourceId is not null && !EvidenceId(plan.ResourceId)
            || plan.Steps is not { Length: <= MaxPlanSteps } || plan.Boundary is not { } boundary
            || boundary.Kind is not ("completion_candidate" or "resource" or "needs_input" or "permission"
                or "observation" or "unsupported" or "plan_limit")
            || string.IsNullOrWhiteSpace(boundary.Reason) || boundary.Reason.Length > 500
            || boundary.Needed is null || boundary.Needed.Length > 1000
            || boundary.Kind != "completion_candidate" && string.IsNullOrWhiteSpace(boundary.Needed)
            || plan.Steps.Length == MaxPlanSteps && boundary.Kind == "completion_candidate")
            return false;
        for (int index = 0; index < plan.Steps.Length; index++)
        {
            var step = plan.Steps[index];
            if (step is null || string.IsNullOrWhiteSpace(step.Instruction) || step.Instruction.Length > 500)
                return false;
            if (step.Kind == "manual")
            {
                if (index != plan.Steps.Length - 1 || boundary.Kind == "completion_candidate"
                    || step.Intent is not null || step.ControlId is not null || step.Value is not null
                    || step.ScrollDirection is not null || step.ValueHash is not null) return false;
                continue;
            }
            if (step.Kind != "action" || step.Intent is not { } intent
                || string.IsNullOrWhiteSpace(intent.Role) || intent.Role.Length > 64
                || string.IsNullOrWhiteSpace(intent.Label) || intent.Label.Length > 256
                || intent.AutomationId is { Length: > 256 } || intent.FrameworkId is { Length: > 256 }
                || intent.Action is not ("invoke" or "toggle" or "select" or "expand" or "collapse" or "set_value" or "scroll")
                || (intent.Action == "toggle" ? intent.ToggleState is not ("on" or "off") : intent.ToggleState is not null)
                || (intent.Action == "select" ? intent.IsSelected != false : intent.IsSelected is not null)
                || step.ControlId is not null && !EvidenceId(step.ControlId)
                || !ValidActionInput(new(intent.Label, [0, 0, 1, 1], 1,
                    ToggleState: intent.ToggleState, Action: intent.Action,
                    Value: step.Value, ScrollDirection: step.ScrollDirection, ValueHash: step.ValueHash))
                || intent.Action != "set_value" && step.ValueHash is not null
                || intent.Action == "set_value" && step.ControlId is null
                    && step.ValueHash != AutomationEvidence.ValueDigest("")
                || step.ControlId is not null && BindPlanAction(step, observation) is null)
                return false;
        }
        return true;
    }

    public static bool VerifiedCameraSettingsPage(ElementInfo[] elements) =>
        AutomationEvidence.VerifiedPage(elements.Select(e => e.AutomationId)) == "camera-privacy";

    public static bool VerifiedTeamsDevicesPage(ElementInfo[] elements) =>
        AutomationEvidence.VerifiedPage(elements.Select(e => e.AutomationId)) == "teams-devices";

    public static ElementInfo? PackagedTeamsCameraPermission(ElementInfo[] elements) =>
        elements.FirstOrDefault(e => e.AutomationId == "MSTeams_8wekyb3d8bbwe_ToggleSwitch"
            && e.IsEnabled && e.Targetable && ValidBox(e.Box));

    public static bool Fresh(DateTimeOffset captured, DateTimeOffset now) =>
        now >= captured && now - captured < TimeSpan.FromSeconds(60);

    internal static TimeSpan GuidanceBudget(DateTimeOffset captured, DateTimeOffset now) =>
        TimeSpan.FromSeconds(Math.Max(0, Math.Min(54, 59 - Math.Max(0, (now - captured).TotalSeconds))));

    public static bool Matches(Guidance g, string observation, string window) =>
        g.ObservationId == observation && g.WindowId == window && !string.IsNullOrWhiteSpace(g.CorrelationId)
        && !string.IsNullOrWhiteSpace(g.Instruction) && g.Instruction.Length <= 16000
        && g.Mode is "demo" or "model"
        && g.Status is "next_step" or "clarification" or "completed" or "blocked" or "needs_input" or "completion_candidate"
        && (g.Status == "next_step" || g.Target is null)
        && (g.Plan is null || g.Status == "next_step" && g.Target is null)
        && (g.RemainingWork is null || g.RemainingWork.Length <= 1000);

    public static Uri? CitationUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.UserInfo.Length == 0 ? uri : null;
}