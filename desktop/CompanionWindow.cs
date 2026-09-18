using System.Runtime.InteropServices;
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
        var monitor = Native.MonitorFromPoint(cursor, 2);
        var info = new Native.MONITORINFO { Size = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(monitor, ref info)) return false;
        rect = NearCursor(cursor, info.Work, width, height, offsetX, offsetY);
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
    private readonly DispatcherTimer autoHide = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly List<string> taskActions = [];
    private string taskStatus = "";
    private bool expanded;
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
            Native.SetWindowLong(Handle, -20,
                Native.GetWindowLong(Handle, -20) | 0x20 | 0x80 | 0x08000000);
            Native.SetWindowDisplayAffinity(Handle, Native.MSGuideDisplayAffinity);
            HwndSource.FromHwnd(Handle)?.AddHook(Hook);
        };
        tracking.Tick += (_, _) => Position();
        autoHide.Tick += (_, _) => ShowIdle();
    }

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x84) { handled = true; return new nint(-1); }
        if (message == 0x21) { handled = true; return new nint(3); }
        return 0;
    }

    public void ShowIdle()
    {
        autoHide.Stop();
        expanded = false;
        bubble.Visibility = Visibility.Collapsed;
        activityRing.Visibility = Visibility.Collapsed;
        activityRing.BeginAnimation(RenderTransformProperty, null);
        Width = 48;
        Height = 48;
        ShowCompanion();
    }

    public void ShowProcessing()
    {
        autoHide.Stop();
        expanded = true;
        message.Text = "Thinking about the app you invoked MSGuide from...";
        bubble.Visibility = Visibility.Visible;
        activityRing.Visibility = Visibility.Visible;
        var rotation = new RotateTransform();
        activityRing.RenderTransform = rotation;
        rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360,
            new Duration(TimeSpan.FromSeconds(1))) { RepeatBehavior = RepeatBehavior.Forever });
        Width = 390;
        Height = 76;
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
        Width = 390;
        Height = Math.Min(280, 82 + taskActions.Count * 24);
        ShowCompanion();
    }

    internal static string TaskText(IEnumerable<string> actions, string status) =>
        string.Join("\n", actions.Prepend(status));

    public void ShowResponse(string text)
    {
        expanded = true;
        activityRing.Visibility = Visibility.Collapsed;
        message.MaxHeight = 126;
        message.Text = string.IsNullOrWhiteSpace(text) ? "MSGuide is ready." : text;
        bubble.Visibility = Visibility.Visible;
        Width = 390;
        Height = 154;
        ShowCompanion();
        autoHide.Stop();
        autoHide.Start();
    }

    private void ShowCompanion()
    {
        if (!IsVisible) Show();
        Position();
        if (!tracking.IsEnabled) tracking.Start();
    }

    private void Position()
    {
        int width = expanded ? 390 : 48;
        int height = expanded ? (int)Height : 48;
        int offsetX = expanded ? 22 : 35;
        int offsetY = expanded ? 12 : 25;
        if (!CompanionPlacement.TryCurrent(width, height, out var rect, offsetX, offsetY)) return;
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
        Native.SetWindowPos(Handle, new nint(-1), (int)Math.Round(currentX), (int)Math.Round(currentY),
            rect.Width, rect.Height, 0x10);
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
    private readonly TextBox prompt;
    private readonly Button ask;
    private readonly Func<string, Task> submit;
    private nint Handle => new WindowInteropHelper(this).Handle;

    public CompanionPromptWindow(Func<string, Task> submit, Action showDetails)
    {
        this.submit = submit;
        Title = "Ask MSGuide";
        Width = 440;
        Height = 116;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;

        var layout = new Grid { Margin = new Thickness(1) };
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
        prompt.Text = draft;
        ask.IsEnabled = true;
        if (CompanionPlacement.TryCurrent((int)Width, (int)Height, out var rect))
        {
            new WindowInteropHelper(this).EnsureHandle();
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

    public CompanionShell(Func<string, Task> submit, Action showDetails)
    {
        prompt = new CompanionPromptWindow(submit, showDetails);
    }

    public nint PromptHandle => new WindowInteropHelper(prompt).Handle;
    public void Start() => cursor.ShowIdle();
    public void Invoke(string draft)
    {
        cursor.Hide();
        prompt.Invoke(draft);
    }
    public void ShowProcessing() => cursor.ShowProcessing();
    public void ShowResponse(string text) => cursor.ShowResponse(text);
    public void BeginTask() => cursor.BeginTask();
    public void ShowTaskAction(int number, string action) => cursor.ShowTaskAction(number, action);
    public void ShowTaskStatus(string status) => cursor.ShowTaskStatus(status);
    public void FinishTask(string status) => cursor.FinishTask(status);
    public void Stop()
    {
        prompt.Close();
        cursor.Stop();
    }
}
