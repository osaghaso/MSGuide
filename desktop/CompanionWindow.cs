using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MSGuide.Desktop;

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
    internal static Size PhysicalSize(Size logical, DpiScale dpi) =>
        new(Math.Ceiling(logical.Width * dpi.DpiScaleX), Math.Ceiling(logical.Height * dpi.DpiScaleY));

    internal static Point FlightPoint(Point start, Point destination, double progress)
    {
        double t = Math.Clamp(progress, 0, 1);
        t = t * t * (3 - 2 * t);
        var control = new Point((start.X + destination.X) / 2,
            (start.Y + destination.Y) / 2 - Math.Min(60, (destination - start).Length * 0.15));
        double remaining = 1 - t;
        return new Point(remaining * remaining * start.X + 2 * remaining * t * control.X + t * t * destination.X,
            remaining * remaining * start.Y + 2 * remaining * t * control.Y + t * t * destination.Y);
    }

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
}

internal sealed class CursorCompanionWindow : Window
{
    private readonly Grid root;
    private readonly Border badge;
    private readonly Border bubble;
    private readonly TextBlock message;
    private readonly Ellipse activityRing;
    private readonly DispatcherTimer tracking = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly List<string> taskActions = [];
    private string taskStatus = "";
    private Size requestedSize = new(48, 48);
    private Native.POINT? actionPoint;
    private int actionVersion;
    private double currentX = double.NaN;
    private double currentY = double.NaN;
    private nint Handle => new WindowInteropHelper(this).Handle;

    public CursorCompanionWindow()
    {
        Title = "MSGuide cursor companion";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        IsHitTestVisible = false;
        Focusable = false;
        SizeToContent = SizeToContent.Manual;

        root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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
        root.Children.Add(logoLayer);

        message = new TextBlock
        {
            MaxWidth = 300,
            MaxHeight = 126,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13,
            LineHeight = 18
        };
        System.Windows.Automation.AutomationProperties.SetLiveSetting(message,
            System.Windows.Automation.AutomationLiveSetting.Polite);
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
            Native.SetWindowLong(Handle, -20,
                Native.GetWindowLong(Handle, -20) | 0x20 | 0x80 | 0x08000000);
            Native.SetWindowDisplayAffinity(Handle, Native.MSGuideDisplayAffinity);
            HwndSource.FromHwnd(Handle)?.AddHook(Hook);
        };
        tracking.Tick += (_, _) => Position();
    }

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x84) { handled = true; return new nint(-1); }
        if (message == 0x21) { handled = true; return new nint(3); }
        return 0;
    }

    public void ShowIdle()
    {
        actionVersion++;
        actionPoint = null;
        taskActions.Clear();
        taskStatus = "";
        message.Text = "";
        bubble.Visibility = Visibility.Collapsed;
        activityRing.Visibility = Visibility.Collapsed;
        activityRing.BeginAnimation(RenderTransformProperty, null);
        SetRequestedSize(48, 48);
        ShowCompanion();
    }

    public void ShowProcessing()
    {
        actionVersion++;
        actionPoint = null;
        message.Text = "Thinking about the app you invoked MSGuide from...";
        bubble.Visibility = Visibility.Visible;
        activityRing.Visibility = Visibility.Visible;
        var rotation = new RotateTransform();
        activityRing.RenderTransform = rotation;
        rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360,
            new Duration(TimeSpan.FromSeconds(1))) { RepeatBehavior = RepeatBehavior.Forever });
        SetRequestedSize(390, 100);
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
    }

    public void ShowTaskStatus(string status)
    {
        taskStatus = status;
        ShowTask();
    }

    private void ShowTask()
    {
        actionVersion++;
        actionPoint = null;
        message.MaxHeight = 250;
        message.Text = TaskText(taskActions, taskStatus);
        bubble.Visibility = Visibility.Visible;
        activityRing.Visibility = Visibility.Visible;
        var rotation = new RotateTransform();
        activityRing.RenderTransform = rotation;
        rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360,
            new Duration(TimeSpan.FromSeconds(1))) { RepeatBehavior = RepeatBehavior.Forever });
        FitMessage();
        ShowCompanion();
    }

    internal static string TaskText(IEnumerable<string> actions, string status) =>
        string.Join("\n", actions.Prepend(status));

    internal static string ResultText(string status, string detail, int actions) =>
        (status switch
        {
            "needs_input" => "Needs your input",
            "failed" => "Task failed",
            "blocked" => "Task blocked",
            "unknown" => "Outcome unknown - check the app",
            "cancelled" => "Task cancelled",
            "no_progress" => "No progress - task paused",
            "review_required" => "Completion needs review",
            _ => "Task paused"
        }) + $"\nActions invoked: {actions}\n{detail}";

    public void ShowResponse(string text)
    {
        actionVersion++;
        actionPoint = null;
        activityRing.Visibility = Visibility.Collapsed;
        message.MaxHeight = 126;
        message.Text = string.IsNullOrWhiteSpace(text) ? "MSGuide is ready." : text;
        bubble.Visibility = Visibility.Visible;
        FitMessage();
        ShowCompanion();
    }

    private void SetRequestedSize(double width, double height)
    {
        requestedSize = new(width, height);
        Width = width;
        Height = height;
    }

    private void FitMessage()
    {
        message.Measure(new Size(message.MaxWidth, double.PositiveInfinity));
        SetRequestedSize(390, Math.Clamp(Math.Ceiling(message.DesiredSize.Height) + 28, 100, 280));
    }

    internal Size ExpectedPhysicalSize => CompanionPlacement.PhysicalSize(requestedSize, VisualTreeHelper.GetDpi(this));
    internal bool HasVisibleFeedback => bubble.Visibility == Visibility.Visible && message.Text.Length > 0;
    internal string FeedbackText => message.Text;

    internal void ShowCompanion()
    {
        if (!IsVisible) Show();
        Position();
        if (!tracking.IsEnabled) tracking.Start();
    }

    public bool ShowActionTarget(Native.RECT window, double[] box)
    {
        if (!Safety.ValidBox(box)) return false;
        actionVersion++;
        var target = Safety.PhysicalTarget(window, box);
        actionPoint = new Native.POINT
        {
            X = (int)Math.Round(target.X + target.Width / 2),
            Y = (int)Math.Round(target.Y + target.Height / 2)
        };
        bubble.Visibility = Visibility.Collapsed;
        activityRing.Visibility = Visibility.Visible;
        SetRequestedSize(48, 48);
        ShowCompanion();
        return IsVisible && Position();
    }

    public async Task<bool> MoveToActionTargetAsync(Native.RECT window, double[] box, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Safety.ValidBox(box)) return false;
        int mine = ++actionVersion;
        var target = Safety.PhysicalTarget(window, box);
        var destination = new Point(target.X + target.Width / 2, target.Y + target.Height / 2);
        var dpi = VisualTreeHelper.GetDpi(this);
        var start = new Point(double.IsNaN(currentX) ? destination.X : currentX + 24 * dpi.DpiScaleX,
            double.IsNaN(currentY) ? destination.Y : currentY + 24 * dpi.DpiScaleY);
        bubble.Visibility = Visibility.Collapsed;
        activityRing.Visibility = Visibility.Visible;
        SetRequestedSize(48, 48);
        actionPoint = new() { X = (int)Math.Round(start.X), Y = (int)Math.Round(start.Y) };
        ShowCompanion();
        double duration = Math.Clamp((destination - start).Length / 1600, 0.15, 0.5);
        var clock = Stopwatch.StartNew();
        try
        {
            while (clock.Elapsed.TotalSeconds < duration)
            {
                token.ThrowIfCancellationRequested();
                if (mine != actionVersion) return false;
                var position = CompanionPlacement.FlightPoint(start, destination, clock.Elapsed.TotalSeconds / duration);
                actionPoint = new() { X = (int)Math.Round(position.X), Y = (int)Math.Round(position.Y) };
                if (!Position()) return false;
                await Task.Delay(16, token);
            }
            token.ThrowIfCancellationRequested();
            if (mine != actionVersion) return false;
            return ShowActionTarget(window, box);
        }
        finally
        {
            if (mine == actionVersion) actionPoint = null;
        }
    }

    private bool Position()
    {
        var physical = ExpectedPhysicalSize;
        int width = (int)physical.Width;
        int height = (int)physical.Height;
        if (actionPoint is { } point)
        {
            if (!CompanionPlacement.TryAtPoint(point, width, height, out var anchored,
                -width / 2, -height / 2)) return false;
            currentX = anchored.Left;
            currentY = anchored.Top;
            return Native.SetWindowPos(Handle, new nint(-1), anchored.Left, anchored.Top,
                anchored.Width, anchored.Height, 0x10);
        }
        bool expanded = requestedSize.Width > 48;
        int offsetX = expanded ? 22 : 35;
        int offsetY = expanded ? 12 : 25;
        if (!CompanionPlacement.TryCurrent(width, height, out var rect, offsetX, offsetY)) return false;
        if (double.IsNaN(currentX) || Math.Abs(rect.Left - currentX) > 900 || Math.Abs(rect.Top - currentY) > 900)
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
        return Native.SetWindowPos(Handle, new nint(-1), (int)Math.Round(currentX), (int)Math.Round(currentY),
            rect.Width, rect.Height, 0x10);
    }

    public void Stop()
    {
        actionVersion++;
        tracking.Stop();
        Close();
    }
}

internal sealed class CompanionPromptWindow : Window
{
    private readonly TextBox prompt;
    private readonly Button ask;
    private readonly Func<string, Task> submit;
    private readonly Func<string, Task>? continueTask;
    private readonly TextBlock modeText;
    private readonly TextBlock taskText;
    private readonly StackPanel taskPanel;
    internal Button ContinueTaskButton { get; }
    internal Button StopTaskButton { get; }
    internal Button SwitchModeButton { get; }
    internal TextBox TaskReply { get; }
    private bool updatingDraft;
    private nint Handle => new WindowInteropHelper(this).Handle;

    public CompanionPromptWindow(Func<string, Task> submit, Action showDetails,
        Func<string, Task>? continueTask = null, Action? stopTask = null,
        Action? switchMode = null, Action<string>? editDraft = null)
    {
        this.submit = submit;
        this.continueTask = continueTask;
        Title = "Ask MSGuide";
        Width = 470;
        Height = 164;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;

        var layout = new Grid { Margin = new Thickness(1) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var logo = WindowsLogoVisual.CreateBadge(42);
        logo.Margin = new Thickness(12);
        layout.Children.Add(logo);
        prompt = new TextBox
        {
            MinWidth = 278,
            MaxLength = 4000,
            AcceptsReturn = false,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
            CaretBrush = new SolidColorBrush(Color.FromRgb(96, 205, 255)),
            SelectionBrush = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(96, 205, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 0, 10, 0)
        };
        prompt.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                await SubmitAsync();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Hide();
            }
        };
        prompt.TextChanged += (_, _) => { if (!updatingDraft) editDraft?.Invoke(prompt.Text); };
        System.Windows.Automation.AutomationProperties.SetName(prompt, "New question; editing replaces the previous task");
        Grid.SetColumn(prompt, 1);
        layout.Children.Add(prompt);

        var buttons = new StackPanel { Margin = new Thickness(0, 12, 8, 10) };
        ask = new Button { Content = "Ask", MinWidth = 60, Padding = new Thickness(10, 5, 10, 5) };
        ask.Click += async (_, _) => await SubmitAsync();
        var details = new Button
        {
            Content = "Details",
            MinWidth = 60,
            Padding = new Thickness(8, 3, 8, 3),
            FontSize = 11
        };
        details.Click += (_, _) => { Hide(); showDetails(); };
        buttons.Children.Add(ask);
        buttons.Children.Add(details);
        Grid.SetColumn(buttons, 2);
        layout.Children.Add(buttons);

        var modePanel = new WrapPanel { Margin = new Thickness(12, 0, 12, 8) };
        modeText = new TextBlock
        { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        SwitchModeButton = new Button { Content = "Switch mode", Padding = new Thickness(8, 4, 8, 4) };
        SwitchModeButton.Click += (_, _) => switchMode?.Invoke();
        modePanel.Children.Add(modeText);
        modePanel.Children.Add(SwitchModeButton);
        Grid.SetRow(modePanel, 1);
        Grid.SetColumnSpan(modePanel, 3);
        layout.Children.Add(modePanel);

        taskPanel = new StackPanel { Margin = new Thickness(12, 0, 12, 12), Visibility = Visibility.Collapsed };
        taskText = new TextBlock { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        System.Windows.Automation.AutomationProperties.SetName(taskText, "Retained plan, progress, boundary and required input");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(taskText, System.Windows.Automation.AutomationLiveSetting.Polite);
        taskPanel.Children.Add(new ScrollViewer
        { Content = taskText, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        TaskReply = new TextBox
        {
            MaxLength = 1000, IsUndoEnabled = false, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 65, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
            CaretBrush = Brushes.White, Margin = new Thickness(0, 6, 0, 6)
        };
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
        Grid.SetRow(taskPanel, 2);
        Grid.SetColumnSpan(taskPanel, 3);
        layout.Children.Add(taskPanel);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(250, 27, 27, 27)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(96, 205, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = layout,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 24,
                Direction = 270,
                ShadowDepth = 8,
                Opacity = 0.55
            }
        };
        SourceInitialized += (_, _) =>
        {
            Native.SetWindowLong(Handle, -20, Native.GetWindowLong(Handle, -20) | 0x80);
            Native.SetWindowDisplayAffinity(Handle, Native.MSGuideDisplayAffinity);
        };
        Deactivated += (_, _) => Hide();
    }

    public void Invoke(string draft)
    {
        updatingDraft = true;
        try { prompt.Text = draft; }
        finally { updatingDraft = false; }
        ask.IsEnabled = true;
        Width = 470;
        Height = taskPanel.Visibility == Visibility.Visible ? 440 : 164;
        new WindowInteropHelper(this).EnsureHandle();
        var physical = CompanionPlacement.PhysicalSize(new Size(Width, Height), VisualTreeHelper.GetDpi(this));
        if (CompanionPlacement.TryCurrent((int)physical.Width, (int)physical.Height, out var rect))
        {
            Native.SetWindowPos(Handle, new nint(-1), rect.Left, rect.Top, rect.Width, rect.Height, 0x10);
        }
        Show();
        Activate();
        prompt.Focus();
        prompt.CaretIndex = prompt.Text.Length;
    }

    internal bool HasReadablePrompt =>
        prompt.Foreground == Brushes.White
        && prompt.Background is SolidColorBrush background
        && background.Color == Color.FromRgb(45, 45, 48)
        && prompt.ToolTip is null;

    internal string TaskDescription => taskText.Text;
    internal string ModeDescription => modeText.Text;

    internal void UpdateTask(CameraRecoveryInteractionMode mode, ScreenTaskSession? task,
        bool canContinue, bool resourceHandoff, bool controlAvailable)
    {
        bool fix = mode == CameraRecoveryInteractionMode.Control;
        modeText.Text = fix ? "FIX IT FOR ME - approved actions only" : "GUIDE ME - no automatic actions";
        System.Windows.Automation.AutomationProperties.SetName(modeText, modeText.Text);
        SwitchModeButton.Content = fix ? "Stop & switch to Guide" : "Stop & switch to Fix";
        SwitchModeButton.IsEnabled = fix || controlAvailable;
        System.Windows.Automation.AutomationProperties.SetName(SwitchModeButton, SwitchModeButton.Content.ToString());
        taskPanel.Visibility = task is null ? Visibility.Collapsed : Visibility.Visible;
        Height = task is null ? 164 : 440;
        taskText.Text = task is null ? "" : task.Status + " - " + task.Detail
            + (task.Plan is null ? "" : "\n" + ScreenTaskSession.DescribePlan(task.Plan, task.PlanCursor));
        ContinueTaskButton.IsEnabled = canContinue && continueTask is not null;
        ContinueTaskButton.Content = resourceHandoff
            ? "Use selected window & continue"
            : task?.ReplanRequired == true ? "Review boundary & replan" : "Review & continue";
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

    private async Task SubmitAsync()
    {
        string text = prompt.Text.Trim();
        if (text.Length == 0) return;
        ask.IsEnabled = false;
        Hide();
        await submit(text);
        ask.IsEnabled = true;
    }
}

internal sealed class CompanionShell
{
    private readonly CursorCompanionWindow cursor = new();
    private readonly CompanionPromptWindow prompt;
    private bool active;

    public CompanionShell(Func<string, Task> submit, Action showDetails,
        Func<string, Task> continueTask, Action stopTask, Action switchMode, Action<string> editDraft)
    {
        prompt = new CompanionPromptWindow(submit, showDetails, continueTask, stopTask, switchMode, editDraft);
        prompt.IsVisibleChanged += (_, _) =>
        {
            if (active && !prompt.IsVisible) cursor.ShowCompanion();
        };
    }

    public nint PromptHandle => new WindowInteropHelper(prompt).Handle;
    internal CompanionPromptWindow Prompt => prompt;
    internal CursorCompanionWindow Cursor => cursor;
    public void UpdateTask(CameraRecoveryInteractionMode mode, ScreenTaskSession? task,
        bool canContinue, bool resourceHandoff, bool controlAvailable)
        => prompt.UpdateTask(mode, task, canContinue, resourceHandoff, controlAvailable);
    public void Start() { active = true; cursor.ShowIdle(); }
    public void Invoke(string draft)
    {
        active = true;
        cursor.Hide();
        prompt.Invoke(draft);
    }
    public void ShowProcessing() { if (active) cursor.ShowProcessing(); }
    public Task<bool> MoveToActionTargetAsync(Native.RECT window, double[] box, CancellationToken token) =>
        active ? cursor.MoveToActionTargetAsync(window, box, token) : Task.FromResult(false);
    public void ShowResponse(string text) { if (active) cursor.ShowResponse(text); }
    public void BeginTask() { if (active) cursor.BeginTask(); }
    public void ShowTaskAction(int number, string action) { if (active) cursor.ShowTaskAction(number, action); }
    public void ShowTaskStatus(string status) { if (active) cursor.ShowTaskStatus(status); }
    public void FinishTask(ScreenTaskSession task)
    {
        if (active) cursor.FinishTask(CursorCompanionWindow.ResultText(task.Status, task.Detail, task.ActionsTaken));
    }
    public void ClearFeedback()
    {
        if (!active) return;
        cursor.ShowIdle();
        if (prompt.IsVisible) cursor.Hide();
    }
    public void Stop()
    {
        active = false;
        prompt.Close();
        cursor.Stop();
    }
}
