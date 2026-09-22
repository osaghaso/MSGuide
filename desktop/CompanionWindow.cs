using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MSGuide.Desktop;

internal sealed class CompactScrollViewer : ScrollViewer
{
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_VerticalScrollBar") is ScrollBar bar)
            bar.SetResourceReference(StyleProperty, "CompactScrollBarStyle");
    }
}

internal static class WindowsLogoVisual
{
    public static Grid Create(double size = 18)
    {
        var panes = new Grid { Width = size, Height = size };
        panes.RowDefinitions.Add(new RowDefinition());
        panes.RowDefinitions.Add(new RowDefinition());
        panes.ColumnDefinitions.Add(new ColumnDefinition());
        panes.ColumnDefinitions.Add(new ColumnDefinition());
        for (int index = 0; index < 4; index++)
        {
            var pane = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
                Margin = new Thickness(Math.Max(0.75, size / 18))
            };
            Grid.SetRow(pane, index / 2);
            Grid.SetColumn(pane, index % 2);
            panes.Children.Add(pane);
        }
        return panes;
    }

    public static Border CreateBadge(double size = 34)
    {
        return new Border
        {
            Width = size,
            Height = size,
            Padding = new Thickness(size * 0.2),
            CornerRadius = new CornerRadius(size * 0.24),
            Background = new SolidColorBrush(Color.FromArgb(245, 15, 23, 42)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(104, 224, 255)),
            BorderThickness = new Thickness(1),
            Child = Create(size * 0.58),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(0, 120, 212),
                BlurRadius = 12,
                ShadowDepth = 0,
                Opacity = 0.7
            }
        };
    }
}

internal static class CompanionPlacement
{
    internal static Native.RECT NearCursor(
        Native.POINT cursor, Native.RECT work, int width, int height,
        int offsetX = 22, int offsetY = 12)
    {
        int x = cursor.X + offsetX;
        int y = cursor.Y + offsetY;
        if (x + width > work.Right) x = cursor.X - offsetX - width;
        if (y + height > work.Bottom) y = cursor.Y - offsetY - height;
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));
        return new Native.RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
    }

    internal static bool TryCurrent(
        int width, int height, out Native.RECT rect,
        int offsetX = 22, int offsetY = 12)
    {
        rect = default;
        if (!Native.GetCursorPos(out var cursor)) return false;
        return TryAtPoint(cursor, width, height, out rect, offsetX, offsetY);
    }

    internal static bool TryAtPoint(Native.POINT point, int width, int height, out Native.RECT rect,
        int offsetX, int offsetY)
    {
        rect = default;
        var monitor = Native.MonitorFromPoint(point, 0);
        if (monitor == 0) return false;
        var info = new Native.MONITORINFO { Size = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(monitor, ref info)) return false;
        rect = NearCursor(point, info.Work, width, height, offsetX, offsetY);
        return true;
    }

    internal static Native.RECT FloatingAtPoint(
        Native.POINT point, Native.RECT work, Size logicalSize, uint dpiX, uint dpiY,
        int offsetX, int offsetY, bool centered = false)
    {
        int width = Math.Min(work.Width, (int)Math.Ceiling(logicalSize.Width * dpiX / 96));
        int height = Math.Min(work.Height, (int)Math.Ceiling(logicalSize.Height * dpiY / 96));
        return NearCursor(point, work, width, height,
            centered ? -width / 2 : offsetX, centered ? -height / 2 : offsetY);
    }

    internal static bool TryFloatingAtPoint(Native.POINT point, Size logicalSize,
        int offsetX, int offsetY, out Native.RECT rect, out Native.RECT work, out nint monitor,
        bool centered = false)
    {
        rect = default;
        if (!TryMonitor(point, out monitor, out work, out var dpiX, out var dpiY)) return false;
        rect = FloatingAtPoint(point, work, logicalSize, dpiX, dpiY, offsetX, offsetY, centered);
        return true;
    }

    private static bool TryMonitor(Native.POINT point, out nint monitor, out Native.RECT work,
        out uint dpiX, out uint dpiY)
    {
        work = default;
        dpiX = dpiY = 0;
        monitor = Native.MonitorFromPoint(point, 2);
        var info = new Native.MONITORINFO { Size = Marshal.SizeOf<Native.MONITORINFO>() };
        if (monitor == 0 || !Native.GetMonitorInfo(monitor, ref info)
            || Native.GetDpiForMonitor(monitor, 0, out dpiX, out dpiY) != 0) return false;
        work = info.Work;
        return true;
    }

    internal static Native.RECT Pinned(Native.POINT origin, Native.RECT work, int width, int height)
    {
        width = Math.Min(width, work.Width);
        height = Math.Min(height, work.Height);
        int x = Math.Clamp(origin.X, work.Left, Math.Max(work.Left, work.Right - width));
        int y = Math.Clamp(origin.Y, work.Top, Math.Max(work.Top, work.Bottom - height));
        return new() { Left = x, Top = y, Right = x + width, Bottom = y + height };
    }

    internal static bool TryPinned(Native.POINT origin, double width, double height, out Native.RECT rect)
        => TrySized(origin, width, height, true, out rect);

    internal static Native.RECT AnchoredPrompt(Native.POINT origin, Native.RECT work, int width, int height,
        int minimumHeight = 160)
    {
        width = Math.Min(width, work.Width);
        int minimumVisibleHeight = Math.Min(Math.Min(height, minimumHeight), work.Height);
        int x = Math.Clamp(origin.X, work.Left, Math.Max(work.Left, work.Right - width));
        int y = Math.Clamp(origin.Y, work.Top, Math.Max(work.Top, work.Bottom - minimumVisibleHeight));
        return new()
        {
            Left = x, Top = y, Right = x + Math.Min(width, work.Right - x),
            Bottom = y + Math.Min(height, work.Bottom - y)
        };
    }

    internal static bool TryAnchoredPrompt(Native.POINT origin, double width, double height, out Native.RECT rect)
        => TrySized(origin, width, height, true, out rect, preserveOrigin: true);

    internal static bool TryPrompt(double width, double height, out Native.RECT rect)
    {
        rect = default;
        return Native.GetCursorPos(out var pointer) && TrySized(pointer, width, height, false, out rect);
    }

    private static bool TrySized(Native.POINT origin, double width, double height, bool pinned, out Native.RECT rect,
        bool preserveOrigin = false)
    {
        rect = default;
        if (!TryMonitor(origin, out _, out var work, out var dpiX, out var dpiY)) return false;
        int physicalWidth = Math.Min(work.Width, (int)Math.Ceiling(width * dpiX / 96));
        int physicalHeight = Math.Min(work.Height, (int)Math.Ceiling(height * dpiY / 96));
        rect = preserveOrigin ? AnchoredPrompt(origin, work, physicalWidth, physicalHeight,
                (int)Math.Ceiling(CompanionPromptWindow.MinimumHeight * dpiY / 96))
            : pinned ? Pinned(origin, work, physicalWidth, physicalHeight)
            : NearCursor(origin, work, physicalWidth, physicalHeight);
        return true;
    }
}

internal sealed class CursorCompanionWindow : Window
{
    private readonly Grid root;
    private readonly Border badge;
    private readonly Border bubble;
    private readonly TextBlock message;
    private readonly Ellipse activityRing;
    private readonly CompanionPosition position;
    private readonly CompanionMoveHandle moveHandle;
    private readonly DispatcherTimer tracking = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer autoHide = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly List<string> taskActions = [];
    private string taskStatus = "";
    private bool expanded;
    private Size contentSize = new(48, 48);
    private nint followMonitor;
    private Native.POINT? actionPoint;
    private double currentX = double.NaN;
    private double currentY = double.NaN;
    private nint Handle => new WindowInteropHelper(this).Handle;

    internal bool CanMove => !position.FollowPointer && actionPoint is null;
    internal Button OpenControl => moveHandle;
    internal Size LogicalContentSize => contentSize;
    internal Rect LogoBounds => badge.TransformToAncestor(root).TransformBounds(new Rect(badge.RenderSize));

    public CursorCompanionWindow(CompanionPosition? position = null, Action? activate = null)
    {
        this.position = position ?? new();
        Title = "MSGuide cursor companion";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        IsHitTestVisible = false;
        Focusable = false;
        SizeToContent = SizeToContent.Manual;

        root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        badge = WindowsLogoVisual.CreateBadge();
        activityRing = new Ellipse
        {
            Width = 42,
            Height = 42,
            Stroke = new SolidColorBrush(Color.FromRgb(96, 205, 255)),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection([2, 2]),
            Visibility = Visibility.Collapsed,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        var logoLayer = new Grid { Width = 44, Height = 44 };
        logoLayer.Children.Add(activityRing);
        logoLayer.Children.Add(badge);
        badge.HorizontalAlignment = HorizontalAlignment.Center;
        badge.VerticalAlignment = VerticalAlignment.Center;
        moveHandle = CompanionMoveHandle.ForLogo(this.position, logoLayer, activate);
        root.Children.Add(moveHandle);

        message = new TextBlock
        {
            MaxWidth = 320,
            MaxHeight = 126,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13,
            LineHeight = 18
        };
        bubble = new Border
        {
            Margin = new Thickness(8, 2, 0, 2),
            Padding = new Thickness(13, 10, 13, 10),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(245, 32, 32, 32)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(71, 71, 71)),
            BorderThickness = new Thickness(1),
            Child = message,
            Visibility = Visibility.Collapsed,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 18,
                Direction = 270,
                ShadowDepth = 7,
                Opacity = 0.5
            }
        };
        Grid.SetColumn(bubble, 1);
        root.Children.Add(bubble);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            UpdateInteraction();
            Native.SetWindowDisplayAffinity(Handle, Native.MSGuideDisplayAffinity);
            HwndSource.FromHwnd(Handle)?.AddHook(Hook);
        };
        tracking.Tick += (_, _) => { if (IsVisible) Position(); };
        autoHide.Tick += (_, _) => ShowIdle();
        this.position.Changed += PlacementChanged;
        Closed += (_, _) => this.position.Changed -= PlacementChanged;
    }

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x84 && !CanMove) { handled = true; return new nint(-1); }
        if (message == 0x21) { handled = true; return new nint(3); }
        return 0;
    }

    public void ShowIdle()
    {
        actionPoint = null;
        autoHide.Stop();
        expanded = false;
        bubble.Visibility = Visibility.Collapsed;
        activityRing.Visibility = Visibility.Collapsed;
        activityRing.BeginAnimation(RenderTransformProperty, null);
        SetContentSize(48, 48);
        ShowCompanion();
    }

    public void ShowProcessing()
    {
        actionPoint = null;
        autoHide.Stop();
        expanded = true;
        message.Text = "Thinking about the app you invoked MSGuide from...";
        bubble.Visibility = Visibility.Visible;
        activityRing.Visibility = Visibility.Visible;
        var rotation = new RotateTransform();
        activityRing.RenderTransform = rotation;
        rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360,
            new Duration(TimeSpan.FromSeconds(1))) { RepeatBehavior = RepeatBehavior.Forever });
        SetContentSize(390, 76);
        ShowCompanion();
    }

    public void BeginTask()
    {
        taskActions.Clear();
        taskStatus = "Thinking about action 1…";
        ShowTask();
    }

    public void ShowTaskAction(int number, string action)
    {
        taskActions.Add($"{number}. {action}");
        if (taskActions.Count > 6) taskActions.RemoveAt(0);
        taskStatus = $"Thinking about action {number + 1}…";
        ShowTask();
    }

    public void FinishTask(string status)
    {
        taskStatus = status;
        ShowTask();
        activityRing.Visibility = Visibility.Collapsed;
        autoHide.Stop();
    }

    public void ShowTaskStatus(string status)
    {
        taskStatus = status;
        ShowTask();
    }

    private void ShowTask()
    {
        actionPoint = null;
        autoHide.Stop();
        expanded = true;
        message.MaxHeight = 250;
        message.Text = TaskText(taskActions, taskStatus);
        bubble.Visibility = Visibility.Visible;
        activityRing.Visibility = Visibility.Visible;
        var rotation = new RotateTransform();
        activityRing.RenderTransform = rotation;
        rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360,
            new Duration(TimeSpan.FromSeconds(1))) { RepeatBehavior = RepeatBehavior.Forever });
        SetContentSize(390, Math.Min(280, 82 + taskActions.Count * 24));
        ShowCompanion();
    }

    internal static string TaskText(IEnumerable<string> actions, string status) =>
        string.Join("\n", actions.Prepend(status));

    public void ShowResponse(string text)
    {
        actionPoint = null;
        expanded = true;
        activityRing.Visibility = Visibility.Collapsed;
        message.MaxHeight = 126;
        message.Text = string.IsNullOrWhiteSpace(text) ? "MSGuide is ready." : text;
        bubble.Visibility = Visibility.Visible;
        SetContentSize(390, 154);
        ShowCompanion();
        autoHide.Stop();
        autoHide.Start();
    }

    private void ShowCompanion()
    {
        UpdateInteraction();
        if (!IsVisible) Show();
        Position();
        if (!tracking.IsEnabled) tracking.Start();
    }

    public bool ShowActionTarget(Native.RECT window, double[] box)
    {
        if (!Safety.ValidBox(box)) return false;
        var target = Safety.PhysicalTarget(window, box);
        actionPoint = new Native.POINT
        {
            X = (int)Math.Round(target.X + target.Width / 2),
            Y = (int)Math.Round(target.Y + target.Height / 2)
        };
        autoHide.Stop();
        expanded = false;
        bubble.Visibility = Visibility.Collapsed;
        activityRing.Visibility = Visibility.Visible;
        SetContentSize(48, 48);
        ShowCompanion();
        return IsVisible && Position();
    }

    private bool Position()
    {
        var previousDpi = Native.SetThreadDpiAwarenessContext(new nint(-4));
        try { return PositionCore(); }
        finally { Native.SetThreadDpiAwarenessContext(previousDpi); }
    }

    private void SetContentSize(double width, double height)
    {
        // Keep DIPs separate from WPF dimensions updated by native pixel-sized moves.
        contentSize = new Size(width, height);
        if (!IsVisible) { Width = width; Height = height; }
    }

    private bool PositionCore()
    {
        if (actionPoint is { } point)
        {
            if (!CompanionPlacement.TryFloatingAtPoint(point, contentSize, 0, 0,
                out var anchored, out _, out followMonitor, centered: true)) return false;
            currentX = anchored.Left;
            currentY = anchored.Top;
            return Native.SetWindowPos(Handle, new nint(-1), anchored.Left, anchored.Top,
                anchored.Width, anchored.Height, 0x10);
        }
        if (!position.FollowPointer && position.Anchor is { } anchor)
        {
            if (!CompanionPlacement.TryPinned(anchor, contentSize.Width, contentSize.Height, out var pinned)) return false;
            followMonitor = 0;
            currentX = pinned.Left;
            currentY = pinned.Top;
            return Native.SetWindowPos(Handle, new nint(-1), pinned.Left, pinned.Top,
                pinned.Width, pinned.Height, 0x10);
        }
        int offsetX = expanded ? 22 : 35;
        int offsetY = expanded ? 12 : 25;
        if (!Native.GetCursorPos(out var cursor)
            || !CompanionPlacement.TryFloatingAtPoint(cursor, contentSize, offsetX, offsetY,
                out var rect, out var work, out var monitor)) return false;
        if (monitor != followMonitor || double.IsNaN(currentX)
            || Math.Abs(rect.Left - currentX) > 900 || Math.Abs(rect.Top - currentY) > 900)
        {
            currentX = rect.Left;
            currentY = rect.Top;
        }
        else
        {
            const double follow = 0.38;
            currentX += (rect.Left - currentX) * follow;
            currentY += (rect.Top - currentY) * follow;
        }
        followMonitor = monitor;
        var placed = CompanionPlacement.Pinned(
            new() { X = (int)Math.Round(currentX), Y = (int)Math.Round(currentY) }, work, rect.Width, rect.Height);
        currentX = placed.Left;
        currentY = placed.Top;
        return Native.SetWindowPos(Handle, new nint(-1), placed.Left, placed.Top,
            placed.Width, placed.Height, 0x10);
    }

    private void UpdateInteraction()
    {
        IsHitTestVisible = CanMove;
        moveHandle.IsEnabled = CanMove;
        if (Handle == 0) return;
        int style = Native.GetWindowLong(Handle, -20) | 0x80 | 0x08000000;
        Native.SetWindowLong(Handle, -20, CanMove ? style & ~0x20 : style | 0x20);
    }

    private void PlacementChanged()
    {
        UpdateInteraction();
        if (IsVisible && !Position())
            position.ReportError("The companion could not be positioned. Use the shortcut to open it again.");
    }

    public void Stop()
    {
        tracking.Stop();
        autoHide.Stop();
        Close();
    }
}

internal sealed class CompanionPromptWindow : Window
{
    internal const double PreferredWidth = 440, MinimumHeight = 160, MaximumHeight = 560;
    private readonly Grid contentLayout;
    private bool resizeQueued;
    private double lastContentHeight = double.NaN;
    private readonly CompanionPosition position;
    private readonly TextBlock placementText;
    private readonly CompanionMoveHandle logoMoveHandle;
    private bool updatingPlacement;
    internal CheckBox FollowPointerControl { get; }
    internal CompanionMoveHandle MoveControl { get; }
    internal CompanionVoiceControls Voice { get; }
    private readonly TextBox prompt;
    private readonly Button ask;
    private readonly Func<string, Task> submit;
    private readonly Func<string, Task>? continueTask;
    private CameraRecoveryInteractionMode displayedMode;
    private bool updatingMode;
    private readonly TextBlock modeHint;
    private readonly TextBlock taskText;
    private readonly StackPanel taskPanel;
    private readonly ContentControl cameraHost = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel workspace = new();
    private readonly StackPanel placementPanel = new() { Margin = new Thickness(0, 8, 0, 12) };
    private readonly ContentControl settingsHost = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel settingsContent = new();
    private readonly TextBlock feedback = new()
    {
        TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 10, 0, 0),
        Visibility = Visibility.Collapsed
    };
    private bool cameraVisible;
    private Native.POINT? promptOrigin;
    private TextBlock? workspaceStatus;
    internal Button SettingsButton { get; }
    internal ScrollViewer WorkspaceScroll { get; }
    internal event Action? Dismissed;
    internal Button ContinueTaskButton { get; }
    internal Button StopTaskButton { get; }
    internal RadioButton GuideModeOption { get; }
    internal RadioButton FixModeOption { get; }
    internal TextBox TaskReply { get; }
    private bool updatingDraft;
    private bool canSubmit = true;
    private readonly Action? dismissVoice;
    internal TextBox DraftControl => prompt;
    internal bool CanSubmit => ask.IsEnabled;
    private nint Handle => new WindowInteropHelper(this).Handle;

    public CompanionPromptWindow(Func<string, Task> submit, Action showSettings,
        Func<string, Task>? continueTask = null, Action? stopTask = null,
        Action<CameraRecoveryInteractionMode>? selectMode = null, Action<string>? editDraft = null,
        CompanionPosition? position = null, Action<bool>? setFollowing = null,
        CompanionVoiceActions? voice = null)
    {
        this.position = position ?? new();
        dismissVoice = voice?.Dismiss;
        this.submit = submit;
        this.continueTask = continueTask;
        Title = "Ask MSGuide";
        Width = PreferredWidth;
        Height = MinimumHeight;
        SizeToContent = SizeToContent.Manual;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        ShowInTaskbar = false;
        Topmost = true;

        var layout = contentLayout = new Grid { Margin = new Thickness(12) };
        for (int row = 0; row < 9; row++)
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        logoMoveHandle = CompanionMoveHandle.ForLogo(this.position, WindowsLogoVisual.CreateBadge(24));
        logoMoveHandle.Margin = new Thickness(0, 0, 8, 0);
        layout.Children.Add(logoMoveHandle);
        var heading = new TextBlock
        {
            Text = "MSGuide", FontSize = 14, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(heading, 1);
        layout.Children.Add(heading);
        prompt = new TextBox
        {
            MinWidth = 120,
            MaxLength = 4000,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 44,
            MaxHeight = 80,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 14,
            Margin = new Thickness(0, 8, 0, 6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8)
        };
        prompt.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        prompt.SetResourceReference(Control.BackgroundProperty, "InputBrush");
        prompt.SetResourceReference(TextBox.CaretBrushProperty, "TextBrush");
        prompt.SetResourceReference(Control.BorderBrushProperty, "BorderStrongBrush");
        prompt.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                await SubmitAsync();
            }
        };
        prompt.TextChanged += (_, _) =>
        {
            if (!updatingDraft) editDraft?.Invoke(prompt.Text);
            if (ask is not null) ask.IsEnabled = canSubmit && !string.IsNullOrWhiteSpace(prompt.Text);
        };
        System.Windows.Automation.AutomationProperties.SetName(prompt, "New question; editing replaces the previous task");
        Grid.SetRow(prompt, 1);
        Grid.SetColumnSpan(prompt, 3);
        layout.Children.Add(prompt);

        ask = new Button
        {
            Content = "Ask", MinWidth = 48, MinHeight = 44, Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(4, 0, 0, 0)
        };
        ask.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        ask.Click += async (_, _) => await SubmitAsync();
        SettingsButton = new Button
        {
            Content = "Settings",
            MinWidth = 60,
            MinHeight = 28,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(8, 0, 0, 0), FontSize = 12
        };
        SettingsButton.Click += (_, _) => showSettings();
        System.Windows.Automation.AutomationProperties.SetName(SettingsButton, "Settings in this compact view");
        Grid.SetColumn(SettingsButton, 2);
        layout.Children.Add(SettingsButton);

        var actionRow = new WrapPanel();
        var modePanel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        string modeGroup = "CompactMode-" + Guid.NewGuid().ToString("N");
        GuideModeOption = new RadioButton
        {
            Content = "Guide me", GroupName = modeGroup, IsChecked = true
        };
        FixModeOption = new RadioButton
        {
            Content = "Fix it for me", GroupName = modeGroup
        };
        foreach (var option in new[] { GuideModeOption, FixModeOption })
        {
            option.SetResourceReference(StyleProperty, "CompactModeChoiceStyle");
            System.Windows.Automation.AutomationProperties.SetName(option, (string)option.Content);
            ToolTipService.SetShowOnDisabled(option, true);
        }
        void SelectMode(CameraRecoveryInteractionMode mode)
        {
            if (!updatingMode && mode != displayedMode) selectMode?.Invoke(mode);
        }
        GuideModeOption.Checked += (_, _) => SelectMode(CameraRecoveryInteractionMode.Guide);
        FixModeOption.Checked += (_, _) => SelectMode(CameraRecoveryInteractionMode.Control);
        modePanel.Children.Add(GuideModeOption);
        modePanel.Children.Add(FixModeOption);
        System.Windows.Automation.AutomationProperties.SetName(modePanel, "Interaction mode");
        actionRow.Children.Add(modePanel);
        var composerActions = new StackPanel { Orientation = Orientation.Horizontal };
        actionRow.Children.Add(composerActions);
        Grid.SetRow(actionRow, 2);
        Grid.SetColumnSpan(actionRow, 3);
        layout.Children.Add(actionRow);
        modeHint = new TextBlock
        {
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        };
        modeHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        settingsContent.Children.Add(modeHint);

        Grid.SetRow(cameraHost, 5);
        Grid.SetColumnSpan(cameraHost, 3);
        layout.Children.Add(cameraHost);
        Grid.SetRow(feedback, 6);
        Grid.SetColumnSpan(feedback, 3);
        System.Windows.Automation.AutomationProperties.SetLiveSetting(feedback,
            System.Windows.Automation.AutomationLiveSetting.Polite);
        layout.Children.Add(feedback);

        placementPanel.Children.Add(new TextBlock { Text = "Companion position", FontWeight = FontWeights.SemiBold });
        var placementControls = new WrapPanel();
        FollowPointerControl = new CheckBox
        {
            Content = "Follow pointer",
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
        };
        System.Windows.Automation.AutomationProperties.SetName(FollowPointerControl,
            "Follow pointer; turn off to stay in place");
        void ChangePlacement(object sender, RoutedEventArgs args)
        {
            if (!updatingPlacement) setFollowing?.Invoke(FollowPointerControl.IsChecked == true);
        }
        FollowPointerControl.Checked += ChangePlacement;
        FollowPointerControl.Unchecked += ChangePlacement;
        MoveControl = new CompanionMoveHandle(this.position);
        placementControls.Children.Add(FollowPointerControl);
        placementControls.Children.Add(MoveControl);
        placementPanel.Children.Add(placementControls);
        placementText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11, Margin = new Thickness(0, 3, 0, 0)
        };
        System.Windows.Automation.AutomationProperties.SetLiveSetting(placementText,
            System.Windows.Automation.AutomationLiveSetting.Polite);
        placementPanel.Children.Add(placementText);
        settingsContent.Children.Add(placementPanel);
        settingsHost.Content = settingsContent;
        workspace.Children.Add(settingsHost);
        Voice = new CompanionVoiceControls(voice);
        Voice.AttachComposerToolbar(composerActions, settingsContent);
        composerActions.Children.Add(ask);
        Grid.SetRow(Voice, 4);
        Grid.SetColumnSpan(Voice, 3);
        layout.Children.Add(Voice);

        taskPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        taskText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        System.Windows.Automation.AutomationProperties.SetName(taskText, "Retained plan, progress, boundary and required input");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(taskText, System.Windows.Automation.AutomationLiveSetting.Polite);
        taskPanel.Children.Add(new ScrollViewer
        { Content = taskText, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        TaskReply = new TextBox
        {
            MaxLength = 1000, IsUndoEnabled = false, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 65, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 6, 0, 6)
        };
        TaskReply.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        TaskReply.SetResourceReference(Control.BackgroundProperty, "InputBrush");
        TaskReply.SetResourceReference(TextBox.CaretBrushProperty, "TextBrush");
        System.Windows.Automation.AutomationProperties.SetName(TaskReply, "Clarification for the retained task");
        taskPanel.Children.Add(TaskReply);
        var taskButtons = new WrapPanel();
        ContinueTaskButton = new Button { Content = "Review & continue" };
        ContinueTaskButton.Click += async (_, _) => await ContinueAsync();
        StopTaskButton = new Button { Content = "Stop task" };
        StopTaskButton.Click += (_, _) => stopTask?.Invoke();
        taskButtons.Children.Add(ContinueTaskButton);
        taskButtons.Children.Add(StopTaskButton);
        taskPanel.Children.Add(taskButtons);
        Grid.SetRow(taskPanel, 7);
        Grid.SetColumnSpan(taskPanel, 3);
        layout.Children.Add(taskPanel);
        Grid.SetRow(workspace, 8);
        Grid.SetColumnSpan(workspace, 3);
        layout.Children.Add(workspace);

        WorkspaceScroll = new CompactScrollViewer
        {
            Content = layout, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = WorkspaceScroll,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 24,
                Direction = 270,
                ShadowDepth = 8,
                Opacity = 0.55
            }
        };
        frame.SetResourceReference(Border.BackgroundProperty, "CanvasBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");
        Content = frame;
        LayoutUpdated += (_, _) => UpdateHeight();
        SourceInitialized += (_, _) =>
        {
            Native.SetWindowLong(Handle, -20, Native.GetWindowLong(Handle, -20) | 0x80);
            Native.SetWindowDisplayAffinity(Handle, Native.MSGuideDisplayAffinity);
        };
        Deactivated += (_, _) =>
        {
            dismissVoice?.Invoke();
            if (!cameraVisible) DismissPrompt();
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) voice?.Dismiss(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            DismissPrompt();
        };
        this.position.Changed += UpdatePlacement;
        Closed += (_, _) => this.position.Changed -= UpdatePlacement;
        UpdatePlacement();
    }

    public void Invoke(string draft)
    {
        UpdateDraft(draft, canSubmit);
        PositionPrompt(nearPointer: !IsVisible);
        Show();
        Activate();
        prompt.Focus();
        prompt.CaretIndex = prompt.Text.Length;
    }

    internal void UpdateDraft(string text, bool allowed)
    {
        canSubmit = allowed;
        updatingDraft = true;
        try
        {
            if (prompt.Text != text)
            {
                prompt.Text = text;
                prompt.CaretIndex = prompt.Text.Length;
            }
        }
        finally { updatingDraft = false; }
        ask.IsEnabled = allowed && !string.IsNullOrWhiteSpace(text);
    }

    internal void DismissPrompt()
    {
        dismissVoice?.Invoke();
        Dismissed?.Invoke();
        Hide();
    }

    internal void AttachWorkspace(FrameworkElement camera, Expander settings,
        FrameworkElement screenContext, FrameworkElement developer, TextBlock statusSource)
    {
        if (Window.GetWindow(camera) is { } owner && NameScope.GetNameScope(owner) is { } scope)
            NameScope.SetNameScope(camera, scope);
        foreach (var element in new[] { camera, settings, screenContext, developer })
        {
            if (LogicalTreeHelper.GetParent(element) is not Panel parent)
                throw new InvalidOperationException("Compact workspace content already has an unexpected owner.");
            parent.Children.Remove(element);
        }
        cameraHost.Content = camera;
        settingsContent.Children.Add(settings);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
        status.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = statusSource });
        System.Windows.Automation.AutomationProperties.SetLiveSetting(status,
            System.Windows.Automation.AutomationLiveSetting.Polite);
        workspaceStatus = status;
        workspace.Children.Insert(0, status);
        workspace.Children.Insert(1, screenContext);
        workspace.Children.Add(developer);
    }

    internal void SetSettingsVisible(bool visible)
    {
        settingsHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        UpdateHeight();
    }

    internal void SetFeedback(string text)
    {
        feedback.Text = text;
        feedback.Visibility = cameraVisible || string.IsNullOrWhiteSpace(text)
            ? Visibility.Collapsed : Visibility.Visible;
        UpdateHeight();
    }

    internal void UpdateCamera(bool visible)
    {
        cameraVisible = visible;
        cameraHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (workspaceStatus is not null)
            workspaceStatus.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        SetFeedback(feedback.Text);
        UpdateHeight();
    }

    internal bool HasCameraWorkspace => cameraHost.Content is FrameworkElement;
    internal bool SettingsVisible => settingsHost.Visibility == Visibility.Visible;
    internal bool CameraKeepsPromptVisible => cameraVisible;

    internal void ShowTaskWithoutActivation()
    {
        if (IsVisible) return;
        ShowActivated = false;
        PositionPrompt();
        Show();
    }

    private void UpdateHeight()
    {
        if (!IsVisible || resizeQueued || Dispatcher.HasShutdownStarted) return;
        if (contentLayout.IsMeasureValid
            && Math.Clamp(Math.Ceiling(contentLayout.DesiredSize.Height + 2), MinimumHeight, MaximumHeight) == lastContentHeight)
            return;
        resizeQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
        {
            resizeQueued = false;
            if (IsVisible) PositionPrompt();
        }));
    }

    internal double MeasureContentHeight()
    {
        double width = IsVisible && WorkspaceScroll.ViewportWidth > 0
            ? WorkspaceScroll.ViewportWidth : PreferredWidth - 2;
        contentLayout.Measure(new Size(width, double.PositiveInfinity));
        return Math.Clamp(Math.Ceiling(contentLayout.DesiredSize.Height + 2), MinimumHeight, MaximumHeight);
    }

    internal void PositionPrompt(bool nearPointer = false)
    {
        double height = MeasureContentHeight();
        if (!IsVisible) Height = height;
        Native.RECT rect;
        bool found = IsVisible && promptOrigin is { } current
            ? CompanionPlacement.TryAnchoredPrompt(current, PreferredWidth, height, out rect)
            : !position.FollowPointer && position.Anchor is { } anchor
                ? CompanionPlacement.TryPinned(anchor, PreferredWidth, height, out rect)
                : !nearPointer && promptOrigin is { } previous
                    ? CompanionPlacement.TryPinned(previous, PreferredWidth, height, out rect)
                    : CompanionPlacement.TryPrompt(PreferredWidth, height, out rect);
        new WindowInteropHelper(this).EnsureHandle();
        if (found) promptOrigin = new Native.POINT { X = rect.Left, Y = rect.Top };
        if (!found || !(Native.GetWindowRect(Handle, out var existing) && existing.Same(rect))
            && !Native.SetWindowPos(Handle, new nint(-1), rect.Left, rect.Top, rect.Width, rect.Height, 0x10))
            position.ReportError("The companion position could not be restored. Move it using the compact prompt.");
        else lastContentHeight = height;
    }

    private void UpdatePlacement()
    {
        updatingPlacement = true;
        try { FollowPointerControl.IsChecked = position.FollowPointer; }
        finally { updatingPlacement = false; }
        MoveControl.IsEnabled = !position.FollowPointer;
        logoMoveHandle.IsEnabled = !position.FollowPointer;
        placementText.Text = position.Description;
        if (IsVisible && !position.FollowPointer)
        {
            promptOrigin = position.Anchor;
            PositionPrompt();
        }
    }

    internal bool HasReadablePrompt =>
        ReferenceEquals(prompt.Foreground, Application.Current.FindResource("TextBrush"))
        && ReferenceEquals(prompt.Background, Application.Current.FindResource("InputBrush"))
        && prompt.ToolTip is null;

    internal string TaskDescription => taskText.Text;
    internal string ModeDescription => displayedMode == CameraRecoveryInteractionMode.Control ? "FIX IT FOR ME" : "GUIDE ME";

    internal void UpdateTask(CameraRecoveryInteractionMode mode, ScreenTaskSession? task,
        bool canContinue, bool controlAvailable, bool cameraActive = false)
    {
        bool fix = mode == CameraRecoveryInteractionMode.Control;
        displayedMode = mode;
        updatingMode = true;
        try
        {
            GuideModeOption.IsChecked = !fix;
            FixModeOption.IsChecked = fix;
        }
        finally { updatingMode = false; }
        GuideModeOption.IsEnabled = true;
        FixModeOption.IsEnabled = controlAvailable;
        modeHint.Text = fix ? "Camera-on once. Permissions and restart ask first."
            : "You make the change. MSGuide helps you check it.";
        System.Windows.Automation.AutomationProperties.SetHelpText(ask, fix
            ? "A submitted Teams camera request authorizes one verified camera-on action. Permission changes and Teams restart ask separately."
            : "Submit your question for guidance. MSGuide will not change camera or permission settings.");
        bool running = cameraActive || task?.Running == true;
        if (running) modeHint.Text += " Switching modes stops the current task.";
        string guideHelp = running
            ? "Select Guide me. Stops the current task and clears its approvals; completed changes are not undone."
            : "Select Guide me. You make changes; MSGuide provides guidance and checks.";
        string fixHelp = !controlAvailable ? "Fix it for me is unavailable: verified local control is not connected."
            : running ? "Select Fix it for me. Stops the current task and clears its approvals; submit a fresh request."
            : "Select Fix it for me. A submitted camera request allows camera-on once; permissions and restart ask separately.";
        GuideModeOption.ToolTip = guideHelp;
        FixModeOption.ToolTip = fixHelp;
        System.Windows.Automation.AutomationProperties.SetHelpText(GuideModeOption, guideHelp);
        System.Windows.Automation.AutomationProperties.SetHelpText(FixModeOption, fixHelp);
        taskPanel.Visibility = task is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateHeight();
        taskText.Text = task is null ? "" : task.Status + " - " + task.Detail
            + (task.Plan is null ? "" : "\n" + ScreenTaskSession.DescribePlan(task.Plan, task.PlanCursor));
        ContinueTaskButton.IsEnabled = canContinue && continueTask is not null;
        ContinueTaskButton.Content = task?.ReplanRequired == true ? "Review boundary & replan" : "Review & continue";
        StopTaskButton.IsEnabled = task is not null && (task.Running || task.Plan is not null && task.CanContinue);
        TaskReply.IsEnabled = task?.CanContinue == true;
    }

    private async Task ContinueAsync()
    {
        if (!ContinueTaskButton.IsEnabled || continueTask is null) return;
        ContinueTaskButton.IsEnabled = false;
        string reply = TaskReply.Text;
        Hide();
        await continueTask(reply);
    }

    internal async Task SubmitAsync()
    {
        if (!ask.IsEnabled) return;
        string text = prompt.Text.Trim();
        if (text.Length == 0) return;
        ask.IsEnabled = false;
        await submit(text);
        ask.IsEnabled = canSubmit && !string.IsNullOrWhiteSpace(prompt.Text);
    }
}

internal sealed class CompanionShell
{
    private readonly CursorCompanionWindow cursor;
    private readonly CompanionPromptWindow prompt;
    private bool active;
    internal CompanionPosition Position { get; }

    public CompanionShell(Func<string, Task> submit, Action showSettings,
        Func<string, Task> continueTask, Action stopTask, Action<CameraRecoveryInteractionMode> selectMode, Action<string> editDraft,
        CompanionPosition? position = null, Action? invoke = null, CompanionVoiceActions? voice = null)
    {
        Position = position ?? new CompanionPosition(CompanionPosition.DefaultPath);
        cursor = new CursorCompanionWindow(Position, invoke);
        prompt = new CompanionPromptWindow(submit, showSettings, continueTask, stopTask, selectMode,
            editDraft, Position, SetFollowing, voice);
        prompt.IsVisibleChanged += (_, _) => { if (active && !prompt.IsVisible) cursor.ShowIdle(); };
    }

    public nint PromptHandle => new WindowInteropHelper(prompt).Handle;
    internal CompanionPromptWindow Prompt => prompt;
    public void UpdateTask(CameraRecoveryInteractionMode mode, ScreenTaskSession? task,
        bool canContinue, bool controlAvailable, bool cameraActive = false)
        => prompt.UpdateTask(mode, task, canContinue, controlAvailable, cameraActive);
    public void Start() { Position.Load(); active = true; cursor.ShowIdle(); }
    internal void SetFollowing(bool follow)
    {
        if (follow == Position.FollowPointer) return;
        if (follow)
        {
            Position.SetFollowing(true, default);
            return;
        }
        var window = prompt.IsVisible ? (Window)prompt : cursor;
        if (!Native.GetWindowRect(new WindowInteropHelper(window).Handle, out var rect)
            && !CompanionPlacement.TryCurrent(48, 48, out rect))
        {
            Position.ReportError("Could not find a position to pin. Try opening the companion again.");
            return;
        }
        Position.SetFollowing(follow, new Native.POINT { X = rect.Left, Y = rect.Top });
    }
    public void Invoke(string draft)
    {
        active = true;
        cursor.Hide();
        prompt.Invoke(draft);
    }
    public void ShowProcessing() { if (active) cursor.ShowProcessing(); }
    public void ShowCameraTask()
    {
        if (!active) return;
        cursor.Hide();
        prompt.ShowTaskWithoutActivation();
    }
    public bool ShowActionTarget(Native.RECT window, double[] box) => active && cursor.ShowActionTarget(window, box);
    public void ShowResponse(string text) { if (active) cursor.ShowResponse(text); }
    public void BeginTask() { if (active) cursor.BeginTask(); }
    public void ShowTaskAction(int number, string action) { if (active) cursor.ShowTaskAction(number, action); }
    public void ShowTaskStatus(string status) { if (active) cursor.ShowTaskStatus(status); }
    public void FinishTask(string status) { if (active) cursor.FinishTask(status); }
    public void Stop()
    {
        active = false;
        prompt.Close();
        cursor.Stop();
    }
}
