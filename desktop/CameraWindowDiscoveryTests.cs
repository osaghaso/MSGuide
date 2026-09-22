using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MSGuide.Desktop;

internal static class CameraWindowDiscoveryTests
{
    private sealed class Discovery(Func<WindowChoice, CameraSurfaceFinding> inspect) : ICameraWindowDiscovery
    {
        internal int Reads;
        public Task<CameraSurfaceFinding> InspectCameraSurfaceAsync(WindowChoice window, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult(inspect(window));
        }
    }

    internal static async Task RunAsync()
    {
        var home = new WindowChoice((nint)1, 1, "Chat | Microsoft Teams", "Synthetic");
        var meeting = new WindowChoice((nint)2, 1, "Meeting | Microsoft Teams", "Synthetic");
        var other = new WindowChoice((nint)3, 1, "Other meeting | Microsoft Teams", "Synthetic");
        var discovery = new Discovery(window => window == meeting
            ? CameraSurfaceFinding.MeetingCamera : CameraSurfaceFinding.NoCamera);
        foreach (var invoked in new WindowChoice?[] { null, home, meeting })
        {
            var found = await CameraWindowDiscovery.SelectAsync([home, meeting], invoked, discovery, CancellationToken.None);
            IntegrationTests.Require(found.Window == meeting);
        }
        discovery = new(window => window == home ? CameraSurfaceFinding.NoCamera : CameraSurfaceFinding.MeetingCamera);
        IntegrationTests.Require((await CameraWindowDiscovery.SelectAsync(
            [home, meeting, other], home, discovery, CancellationToken.None)).Window is null);
        IntegrationTests.Require((await CameraWindowDiscovery.SelectAsync(
            [home, meeting, other], meeting, discovery, CancellationToken.None)).Window == meeting);
        discovery = new(window => window == meeting ? CameraSurfaceFinding.MeetingCamera : CameraSurfaceFinding.Incomplete);
        IntegrationTests.Require((await CameraWindowDiscovery.SelectAsync(
            [home, meeting], null, discovery, CancellationToken.None)).Window is null);
        discovery = new(_ => CameraSurfaceFinding.NoCamera);
        IntegrationTests.Require((await CameraWindowDiscovery.SelectAsync(
            [home], home, discovery, CancellationToken.None)).Window is null);
        discovery = new(_ => CameraSurfaceFinding.MeetingCamera);
        var excessive = Enumerable.Range(1, 7).Select(index => home with { Handle = (nint)index }).ToArray();
        IntegrationTests.Require((await CameraWindowDiscovery.SelectAsync(
            excessive, null, discovery, CancellationToken.None)).Window is null && discovery.Reads == 0);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool rejected = false;
        try { await CameraWindowDiscovery.SelectAsync([meeting], null, discovery, cancelled.Token); }
        catch (OperationCanceledException) { rejected = true; }
        IntegrationTests.Require(rejected && discovery.Reads == 0);

        var camera = new ElementInfo("button", "Turn camera on (Ctrl+Shift+O)", [0.1, 0.2, 0.3, 0.1]);
        IntegrationTests.Require(CameraWindowDiscovery.Assess([camera]) == CameraSurfaceFinding.MeetingCamera);
        IntegrationTests.Require(CameraWindowDiscovery.Assess(
            [camera with { IsEnabled = false, Targetable = false }]) == CameraSurfaceFinding.MeetingCamera);
        foreach (var invalid in new[]
        {
            camera with { Role = "text" }, camera with { IsOffscreen = true }, camera with { IsPassword = true },
            camera with { Label = "Turn camera on (for everyone)" }
        })
            IntegrationTests.Require(CameraWindowDiscovery.Assess([invalid]) == CameraSurfaceFinding.NoCamera);
        IntegrationTests.Require(CameraWindowDiscovery.Assess([camera, camera]) == CameraSurfaceFinding.Incomplete);
    }

    internal static async Task RunNativeAsync(CancellationToken ct)
    {
        var button = new Button { Content = "Turn camera on (Ctrl+Shift+O)", IsEnabled = false };
        AutomationProperties.SetName(button, "Turn camera on (Ctrl+Shift+O)");
        var meeting = new Window
        {
            Title = "Owned camera discovery meeting | Microsoft Teams",
            Width = 480, Height = 320, ShowActivated = false, Content = button
        };
        var home = new Window
        {
            Title = "Owned camera discovery chat | Microsoft Teams",
            Width = 480, Height = 320, ShowActivated = false,
            Content = new TextBlock { Text = "Turn camera on (Ctrl+Shift+O)" }
        };
        var overlay = new OverlayWindow();
        int clicks = 0;
        button.Click += (_, _) => clicks++;
        try
        {
            home.Show();
            meeting.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var meetingChoice = new WindowChoice(new WindowInteropHelper(meeting).Handle,
                (uint)Environment.ProcessId, meeting.Title);
            var homeChoice = new WindowChoice(new WindowInteropHelper(home).Handle,
                (uint)Environment.ProcessId, home.Title);
            using (var controls = await CaptureService.InspectCameraControls(meetingChoice, ct))
                IntegrationTests.Require(controls.Valid() && controls.Png.Length == 0 && controls.Preview is null
                    && CameraWindowDiscovery.Assess(controls.Elements) == CameraSurfaceFinding.MeetingCamera);
            var sensing = new LiveCameraRecoverySensing(overlay);
            var found = await CameraWindowDiscovery.SelectAsync(
                [homeChoice, meetingChoice], homeChoice, sensing, ct);
            IntegrationTests.Require(found.Window == meetingChoice && clicks == 0 && !overlay.IsVisible);
        }
        finally { meeting.Close(); home.Close(); overlay.Close(); }
    }
}
