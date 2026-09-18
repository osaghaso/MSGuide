using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace MSGuide.Desktop;

public sealed class OverlayWindow : Window
{
    public nint Handle => new WindowInteropHelper(this).Handle;

    public OverlayWindow()
    {
        Title = "MSGuide target overlay";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        IsHitTestVisible = false;
        Focusable = false;
        MinWidth = MinHeight = 1;
        var content = new Grid();
        content.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(104, 224, 255)),
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent
        });
        var badge = new Border
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(4),
            Margin = new Thickness(5),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(235, 15, 23, 42)),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = WindowsLogoVisual.Create()
        };
        content.Children.Add(badge);
        Content = content;
        SourceInitialized += (_, _) =>
        {
            // WS_EX_TRANSPARENT | TOOLWINDOW | NOACTIVATE. Native hit testing also fails through.
            Native.SetWindowLong(Handle, -20, Native.GetWindowLong(Handle, -20) | 0x20 | 0x80 | 0x08000000);
            Native.SetWindowDisplayAffinity(Handle, Native.MSGuideDisplayAffinity);
            var source = HwndSource.FromHwnd(Handle);
            if (source is not null)
            {
                source.AddHook(Hook);
                // WPF's default DPI resize can activate the HWND. This border-only
                // visual keeps its initial render scale; PointAt owns physical bounds.
                // Do not use this policy for a window containing text or controls.
                source.DpiChanged += (_, args) => args.Handled = true;
            }
        };
    }

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x84) { handled = true; return new nint(-1); } // HTTRANSPARENT
        if (message == 0x21) { handled = true; return new nint(3); } // MA_NOACTIVATE
        return 0;
    }

    public void PointAt(Native.RECT captureRect, double[] box)
    {
        if (!Safety.ValidBox(box)) { Hide(); return; }
        var target = Safety.PhysicalTarget(captureRect, box);
        // Native position/size are physical pixels. WPF only draws the border in local DIPs;
        // no desktop-coordinate division by a single primary-monitor scale.
        new WindowInteropHelper(this).EnsureHandle();
        bool Position() => Native.SetWindowPos(Handle, new nint(-1), (int)Math.Floor(target.X), (int)Math.Floor(target.Y),
            Math.Max(1, (int)Math.Ceiling(target.Width)), Math.Max(1, (int)Math.Ceiling(target.Height)), 0x10);
        if (!Position()) { Hide(); return; }
        Show();
        // Restore the physical rectangle after WPF's initial show/layout.
        if (!Position()) Hide();
    }
}