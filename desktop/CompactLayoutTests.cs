using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace MSGuide.Desktop;

internal static class CompactLayoutTests
{
    internal static void Run()
    {
        var work = new Native.RECT { Left = 0, Top = 0, Right = 1000, Bottom = 720 };
        var origin = new Native.POINT { X = 160, Y = 250 };
        var idle = CompanionPlacement.AnchoredPrompt(origin, work, 440, 180);
        var expanded = CompanionPlacement.AnchoredPrompt(origin, work, 440, 560);
        var reduced = CompanionPlacement.AnchoredPrompt(origin, work, 440, 200);
        IntegrationTests.Require(idle.Left == expanded.Left && idle.Top == expanded.Top
            && reduced.Left == idle.Left && reduced.Top == idle.Top
            && expanded.Bottom == work.Bottom && expanded.Width == idle.Width
            && reduced.Height == 200);
        var edge = CompanionPlacement.AnchoredPrompt(new() { X = 990, Y = 710 }, work, 440, 560);
        IntegrationTests.Require(edge.Width == 440 && edge.Height >= 160
            && edge.Right <= work.Right && edge.Bottom <= work.Bottom);

        int microphoneStarts = 0;
        using var speech = new SpeechService(() =>
        {
            microphoneStarts++;
            throw new InvalidOperationException("Layout tests must never open microphone input.");
        });
        var main = new MainWindow(speech, new CompanionPosition());
        try
        {
            main.CheckContentFitCompactLayout();
            IntegrationTests.Require(microphoneStarts == 0);
        }
        finally { main.Close(); }
    }

    internal static bool InsideDisclosure(DependencyObject element)
    {
        for (var parent = LogicalTreeHelper.GetParent(element); parent is not null;
            parent = LogicalTreeHelper.GetParent(parent))
            if (parent is Expander) return true;
        return false;
    }
}

public partial class MainWindow
{
    internal void CheckContentFitCompactLayout()
    {
        var compact = companion.Prompt;
        CameraRecoveryCard.Visibility = Visibility.Collapsed;
        compact.UpdateCamera(false);
        compact.SetSettingsVisible(false);
        compact.UpdateDraft("", true);
        double idleHeight = compact.MeasureContentHeight();
        IntegrationTests.Require(idleHeight >= CompanionPromptWindow.MinimumHeight && idleHeight <= 240
            && compact.SizeToContent == SizeToContent.Manual
            && compact.DraftControl.MinHeight == 44
            && compact.GuideModeOption.Visibility == Visibility.Visible
            && compact.FixModeOption.Visibility == Visibility.Visible
            && compact.Voice.RecordButton.Parent is StackPanel
            && compact.Voice.RecordButton.MinHeight >= 44
            && compact.Voice.StatusText.Visibility == Visibility.Collapsed
            && !compact.SettingsVisible);

        CameraRecoveryCard.Visibility = CameraStatePanel.Visibility = Visibility.Visible;
        CameraSelectionPanel.Visibility = CameraStartButton.Visibility = Visibility.Collapsed;
        CameraControlTargetButton.Visibility = CameraSessionActions.Visibility = Visibility.Visible;
        CameraControlTargetButton.IsEnabled = CameraStopButton.IsEnabled = true;
        CameraRestartTeamsButton.Visibility = Visibility.Collapsed;
        CameraDetailsExpander.IsExpanded = false;
        CameraMeetingText.Text = "Meeting: " + string.Join(" ", Enumerable.Repeat("Synthetic meeting title", 8));
        CameraSensingText.Text = "Fixture mode";
        CameraStateText.Text = "Camera control found";
        CameraStepText.Text = "Device-wide Camera access affects all already permitted apps. Review this scope before approving.";
        compact.UpdateCamera(true);
        double cameraHeight = compact.MeasureContentHeight();
        var content = (FrameworkElement)compact.Content;
        content.Measure(new Size(CompanionPromptWindow.PreferredWidth, cameraHeight));
        content.Arrange(new Rect(0, 0, CompanionPromptWindow.PreferredWidth, cameraHeight));
        content.UpdateLayout();
        CameraMeetingText.GetBindingExpression(AutomationProperties.NameProperty)?.UpdateTarget();
        IntegrationTests.Require(cameraHeight > idleHeight && cameraHeight < 480
            && !CameraDetailsExpander.IsExpanded
            && CompactLayoutTests.InsideDisclosure(CameraProgressPanel)
            && !CompactLayoutTests.InsideDisclosure(CameraStepText)
            && !CompactLayoutTests.InsideDisclosure(CameraStopButton)
            && !CompactLayoutTests.InsideDisclosure(CameraSensingText)
            && CameraStepText.Text.Contains("all already permitted apps", StringComparison.Ordinal)
            && CameraMeetingText.TextTrimming == TextTrimming.CharacterEllipsis
            && AutomationProperties.GetName(CameraMeetingText) == CameraMeetingText.Text);

        CameraRestartTeamsButton.Visibility = Visibility.Visible;
        CameraRestartWarning.GetBindingExpression(VisibilityProperty)?.UpdateTarget();
        content.UpdateLayout();
        IntegrationTests.Require(CameraRestartWarning.Visibility == Visibility.Visible
            && CameraRestartWarning.Text.Contains("end an active meeting", StringComparison.Ordinal)
            && !CompactLayoutTests.InsideDisclosure(CameraRestartWarning));
        CameraRestartTeamsButton.Visibility = Visibility.Collapsed;
        compact.Voice.Update(new("Unavailable", "Unavailable", false, false, false,
            "MIC STATUS UNKNOWN", false, "Stop audio", "Stop dictation and playback", false),
            [], null, "Microphone closure is not confirmed. Close MSGuide if it remains unresolved.", "", 0);
        IntegrationTests.Require(compact.Voice.StatusText.Visibility == Visibility.Visible
            && compact.Voice.StatusText.Text.Contains("MIC STATUS UNKNOWN", StringComparison.Ordinal)
            && !CompactLayoutTests.InsideDisclosure(compact.Voice.StopButton));

        CameraStepText.Text = string.Join(" ", Enumerable.Repeat(
            "Outcome unknown; the device-wide permission scope and recovery instructions remain visible.", 80));
        IntegrationTests.Require(compact.MeasureContentHeight() == CompanionPromptWindow.MaximumHeight
            && compact.WorkspaceScroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled
            && compact.WorkspaceScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto);
    }
}
