using System.Net;
using System.Text.Json.Serialization;
using System.Windows;

namespace MSGuide.Desktop;

public sealed record ElementInfo(string Role, string Label, double[] Box, double Confidence = 0.95,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string TargetId = "",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string AutomationId = "",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string FrameworkId = "",
    bool IsEnabled = true, bool Targetable = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ToggleState = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HelpText = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ItemStatus = null);
public sealed record Observation(string Id, string WindowId, string Application, DateTimeOffset CapturedAt,
    int Width, int Height, string OcrText, ElementInfo[] Elements,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImageBase64);
public sealed record GuidanceRequest(string SessionId, string Prompt, bool Consent, Observation Observation);
public sealed record SessionInfo(string SessionId, DateTimeOffset ExpiresAt);
public sealed record HealthInfo(string Status, string Mode, string Version);
public sealed record TargetInfo(string Label, double[] Box, double Confidence);
public sealed record Citation(string Source, string Title);
public sealed record Guidance(string CorrelationId, string ObservationId, string WindowId, string Instruction,
    string Status, TargetInfo? Target, Citation[]? Citations, string Mode);

public static class Safety
{
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
        && elements.Any(e => e.Targetable && e.IsEnabled && e.Label == target.Label && ValidBox(e.Box)
            && e.Box.Zip(target.Box).All(pair => Math.Abs(pair.First - pair.Second) < 0.000001));

    public static bool VerifiedCameraSettingsPage(ElementInfo[] elements) =>
        AutomationEvidence.VerifiedPage(elements.Select(e => e.AutomationId)) == "camera-privacy";

    public static bool VerifiedTeamsDevicesPage(ElementInfo[] elements) =>
        AutomationEvidence.VerifiedPage(elements.Select(e => e.AutomationId)) == "teams-devices";

    public static ElementInfo? PackagedTeamsCameraPermission(ElementInfo[] elements) =>
        elements.FirstOrDefault(e => e.AutomationId == "MSTeams_8wekyb3d8bbwe_ToggleSwitch"
            && e.IsEnabled && e.Targetable && ValidBox(e.Box));

    public static bool Fresh(DateTimeOffset captured, DateTimeOffset now) =>
        now >= captured && now - captured < TimeSpan.FromSeconds(60);

    public static bool Matches(Guidance g, string observation, string window) =>
        g.ObservationId == observation && g.WindowId == window && !string.IsNullOrWhiteSpace(g.CorrelationId)
        && !string.IsNullOrWhiteSpace(g.Instruction) && g.Instruction.Length <= 16000
        && g.Mode is "demo" or "model" && g.Status is "next_step" or "clarification" or "completed"
        && (g.Status == "next_step" || g.Target is null);

    public static Uri? CitationUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.UserInfo.Length == 0 ? uri : null;
}