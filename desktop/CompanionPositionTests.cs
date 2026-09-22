using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace MSGuide.Desktop;

internal static class CompanionPositionTests
{
    internal static void Run()
    {
        var origin = new Native.POINT { X = -850, Y = 120 };
        var state = new CompanionPosition();
        IntegrationTests.Require(state.FollowPointer && state.Anchor is null && state.Error is null);
        state.SetFollowing(false, origin);
        IntegrationTests.Require(!state.FollowPointer && state.Anchor is { X: -850, Y: 120 });
        var moved = CompanionMoveHandle.DragOrigin(origin,
            new() { X = -830, Y = 130 }, new() { X = -620, Y = 290 });
        IntegrationTests.Require(moved.X == -640 && moved.Y == 280);
        IntegrationTests.Require(!CompanionMoveHandle.ExceedsDragThreshold(origin, origin, 1)
            && !CompanionMoveHandle.ExceedsDragThreshold(origin, new() { X = origin.X + 1, Y = origin.Y }, 2)
            && CompanionMoveHandle.ExceedsDragThreshold(origin, moved, 1));
        var logo = CompanionMoveHandle.ForLogo(state, WindowsLogoVisual.CreateBadge());
        IntegrationTests.Require(logo.Template.LoadContent() is ContentPresenter
            && logo.BorderThickness == new Thickness(0)
            && logo.ToolTip is string { Length: < 50 }
            && AutomationProperties.GetHelpText(logo).Contains("keyboard"));
        CheckTooltipStyle();
        state.MoveTo(moved);
        IntegrationTests.Require(state.Anchor is { X: -640, Y: 280 });
        state.SetFollowing(true, default);
        IntegrationTests.Require(state.FollowPointer && state.Anchor is { X: -640, Y: 280 });
        bool rejected = false;
        try { state.MoveTo(origin); } catch (InvalidOperationException) { rejected = true; }
        IntegrationTests.Require(rejected);
        state.SetFollowing(false, origin);
        IntegrationTests.Require(state.Anchor is { X: -850, Y: 120 });

        var work = new Native.RECT { Left = -1920, Top = -1080, Right = 0, Bottom = 0 };
        foreach (double scale in new[] { 1d, 1.25d, 1.5d, 2d })
        {
            int width = (int)(48 * scale);
            var rect = CompanionPlacement.Pinned(new() { X = -900, Y = -400 }, work, width, width);
            IntegrationTests.Require(rect.Left == -900 && rect.Top == -400 && rect.Width == width);
            rect = CompanionPlacement.Pinned(new() { X = 999999, Y = 999999 }, work, width, width);
            IntegrationTests.Require(rect.Right == 0 && rect.Bottom == 0);
            rect = CompanionPlacement.Pinned(new() { X = -999999, Y = -999999 }, work, width, width);
            IntegrationTests.Require(rect.Left == work.Left && rect.Top == work.Top);
        }
        IntegrationTests.Require(CompanionPlacement.Pinned(default, work, 4000, 3000).Same(work));
        foreach (uint dpi in new uint[] { 96, 120, 144, 168, 192, 216, 240, 288 })
        foreach (var size in new[] { new Size(48, 48), new Size(390, 76), new Size(390, 154) })
        foreach (var point in new[]
        {
            new Native.POINT { X = -900, Y = -500 },
            new Native.POINT { X = -10, Y = -10 },
            new Native.POINT { X = -1910, Y = -1070 }
        })
        {
            var floating = CompanionPlacement.FloatingAtPoint(point, work, size, dpi, dpi, 35, 25);
            IntegrationTests.Require(floating.Width == (int)Math.Ceiling(size.Width * dpi / 96)
                && floating.Height == (int)Math.Ceiling(size.Height * dpi / 96)
                && floating.Left >= work.Left && floating.Top >= work.Top
                && floating.Right <= work.Right && floating.Bottom <= work.Bottom);
        }
        var centeredPoint = new Native.POINT { X = -900, Y = -500 };
        var centered = CompanionPlacement.FloatingAtPoint(centeredPoint, work, new Size(48, 48),
            192, 192, 0, 0, centered: true);
        IntegrationTests.Require(centered.Width == 96 && centered.Height == 96
            && centered.Left + centered.Width / 2 == centeredPoint.X
            && centered.Top + centered.Height / 2 == centeredPoint.Y);

        var prompt = new CompanionPromptWindow(_ => Task.CompletedTask, () => { },
            position: state, setFollowing: follow => state.SetFollowing(follow, origin));
        try
        {
            IntegrationTests.Require(prompt.FollowPointerControl.IsChecked == false
                && prompt.MoveControl.IsEnabled && prompt.MoveControl.Focusable
                && new ButtonAutomationPeer(prompt.MoveControl).GetName() == "Move pinned companion"
                && AutomationProperties.GetName(prompt.FollowPointerControl).Contains("stay in place"));
            CheckMoveAccessibility(prompt.MoveControl);
            prompt.FollowPointerControl.IsChecked = true;
            IntegrationTests.Require(state.FollowPointer && !prompt.MoveControl.IsEnabled);
            prompt.FollowPointerControl.IsChecked = false;
            IntegrationTests.Require(!state.FollowPointer && prompt.MoveControl.IsEnabled);
            prompt.UpdateTask(CameraRecoveryInteractionMode.Control, null, false, true);
            IntegrationTests.Require(!state.FollowPointer && prompt.ModeDescription.Contains("FIX"));
            prompt.UpdateTask(CameraRecoveryInteractionMode.Guide, null, false, true);
            IntegrationTests.Require(!state.FollowPointer && prompt.ModeDescription.Contains("GUIDE"));
        }
        finally { prompt.Close(); }

        string root = Path.Combine(Path.GetTempPath(), "MSGuide-position-tests-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "position.json");
        try
        {
            var saved = new CompanionPosition(path);
            saved.Load();
            IntegrationTests.Require(saved.FollowPointer && saved.Error is null && !File.Exists(path));
            saved.SetFollowing(false, origin);
            saved.MoveTo(moved);
            saved.Save();
            var restored = new CompanionPosition(path);
            restored.Load();
            IntegrationTests.Require(restored.Preference == saved.Preference && restored.Error is null
                && Directory.GetFiles(root, "*.tmp").Length == 0);
            using (var json = JsonDocument.Parse(File.ReadAllText(path)))
                IntegrationTests.Require(json.RootElement.EnumerateObject().Select(p => p.Name)
                    .OrderBy(n => n).SequenceEqual(new[] { "FollowPointer", "Version", "X", "Y" }));
            saved.SetFollowing(true, default);
            restored.Load();
            IntegrationTests.Require(restored.FollowPointer && restored.Anchor is { X: -640, Y: 280 });
            foreach (string invalid in new[]
            {
                "not json", "null", """{"Version":2}""", """{"FollowPointer":false}""",
                """{"X":1}""", """{"X":2147483647,"Y":0}""", new string('x', 4097)
            })
            {
                File.WriteAllText(path, invalid);
                restored.Load();
                IntegrationTests.Require(restored.FollowPointer && restored.Error is not null
                    && File.ReadAllText(path) == invalid);
            }
            restored.SetFollowing(false, origin);
            IntegrationTests.Require(restored.Error is null);
            saved.Load();
            IntegrationTests.Require(saved.Preference == restored.Preference);
            string blocked = Path.Combine(root, "not-a-directory");
            File.WriteAllText(blocked, "synthetic");
            var cannotSave = new CompanionPosition(Path.Combine(blocked, "position.json"));
            cannotSave.SetFollowing(false, origin);
            IntegrationTests.Require(!cannotSave.FollowPointer && cannotSave.Error is not null
                && cannotSave.Description.Contains("could not be saved"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    internal static void CheckMoveAccessibility(CompanionMoveHandle move)
    {
        move.ApplyTemplate();
        move.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        IntegrationTests.Require(ReferenceEquals(move.Style, Application.Current.FindResource(typeof(Button)))
            && move.IsTabStop && move.Focusable && move.DesiredSize.Width >= 72 && move.DesiredSize.Height >= 36
            && move.Template.FindName("Chrome", move) is Border { Background: SolidColorBrush }
            && move.Foreground is SolidColorBrush && move.Background is SolidColorBrush);
        var foreground = ((SolidColorBrush)move.Foreground).Color;
        var background = ((SolidColorBrush)move.Background).Color;
        double light = Luminance(foreground), dark = Luminance(background);
        IntegrationTests.Require((Math.Max(light, dark) + 0.05) / (Math.Min(light, dark) + 0.05) >= 4.5);
        IntegrationTests.Require(move.Template.Triggers.OfType<Trigger>().Any(trigger =>
            trigger.Property == UIElement.IsKeyboardFocusedProperty && Equals(trigger.Value, true)
            && trigger.Setters.OfType<Setter>().Any(setter =>
                setter.Property == Border.BorderThicknessProperty && Equals(setter.Value, new Thickness(2)))));
        IntegrationTests.Require(AutomationProperties.GetHelpText(move).Contains("arrow keys"));
    }

    private static void CheckTooltipStyle()
    {
        var tooltip = new ToolTip
        {
            Content = "When pinned, drag to move. Keyboard: arrow keys move 10 pixels; Shift+arrow moves 1 pixel."
        };
        tooltip.ApplyTemplate();
        tooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        tooltip.Arrange(new Rect(tooltip.DesiredSize));
        tooltip.UpdateLayout();
        IntegrationTests.Require(ReferenceEquals(tooltip.Background, Application.Current.FindResource("SurfaceRaisedBrush"))
            && ReferenceEquals(tooltip.Foreground, Application.Current.FindResource("TextBrush"))
            && tooltip.DesiredSize.Width <= 300 && tooltip.DesiredSize.Height > 32
            && tooltip.Template.FindName("TooltipContent", tooltip) is ContentPresenter);
        var presenter = (ContentPresenter)tooltip.Template.FindName("TooltipContent", tooltip);
        presenter.ApplyTemplate();
        IntegrationTests.Require(tooltip.ContentTemplate.FindName("TooltipText", presenter) is TextBlock
            { TextWrapping: TextWrapping.Wrap } text && text.Text == (string)tooltip.Content);
        double foreground = Luminance(((SolidColorBrush)tooltip.Foreground).Color);
        double background = Luminance(((SolidColorBrush)tooltip.Background).Color);
        IntegrationTests.Require((Math.Max(foreground, background) + 0.05) /
            (Math.Min(foreground, background) + 0.05) >= 4.5);
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte component)
        {
            double value = component / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }
}
