using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace MSGuide.Desktop;

internal sealed record CompanionPositionPreference(
    int Version = 1, bool FollowPointer = true, int? X = null, int? Y = null)
{
    internal bool Valid => Version == 1 && X.HasValue == Y.HasValue
        && (FollowPointer || X.HasValue)
        && (X is null || Math.Abs((long)X.Value) <= 1_000_000)
        && (Y is null || Math.Abs((long)Y.Value) <= 1_000_000);
}

internal sealed class CompanionPosition(string? path = null)
{
    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MSGuide", "companion-position.json");

    internal CompanionPositionPreference Preference { get; private set; } = new();
    internal bool FollowPointer => Preference.FollowPointer;
    internal Native.POINT? Anchor => Preference.X is { } x && Preference.Y is { } y
        ? new Native.POINT { X = x, Y = y } : null;
    internal string? Error { get; private set; }
    internal string Description => Error ?? (FollowPointer
        ? "Follows the pointer. Turn off to stay in place."
        : "Stays in place. Drag the logo or focus Move and use arrow keys.");
    internal event Action? Changed;

    internal void Load()
    {
        if (path is null) return;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 4096) throw new InvalidDataException();
            var saved = JsonSerializer.Deserialize<CompanionPositionPreference>(stream);
            if (saved is not { Valid: true }) throw new InvalidDataException();
            Preference = saved;
            Error = null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            Preference = new();
            Error = null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
            or JsonException or System.Security.SecurityException)
        {
            Preference = new();
            ReportError("Saved position could not be loaded. Following the pointer for now; choose a position to save again.", ex);
        }
        Changed?.Invoke();
    }

    internal void SetFollowing(bool follow, Native.POINT origin)
    {
        if (follow == FollowPointer) return;
        var next = follow ? Preference with { FollowPointer = true }
            : new(FollowPointer: false, X: origin.X, Y: origin.Y);
        if (!next.Valid) throw new ArgumentOutOfRangeException(nameof(origin));
        Preference = next;
        Save();
    }

    internal void MoveTo(Native.POINT point)
    {
        if (FollowPointer) throw new InvalidOperationException("Pin the companion before moving it.");
        var moved = Preference with { X = point.X, Y = point.Y };
        if (!moved.Valid) throw new ArgumentOutOfRangeException(nameof(point));
        if (moved == Preference) return;
        Preference = moved;
        Changed?.Invoke();
    }

    internal void Save()
    {
        if (!Preference.Valid) throw new InvalidOperationException("Invalid companion position.");
        try
        {
            if (path is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, JsonSerializer.Serialize(Preference));
                    File.Move(temporary, path, overwrite: true);
                }
                finally { File.Delete(temporary); }
            }
            Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            ReportError("Position applies for this session, but could not be saved on this PC.", ex);
        }
        Changed?.Invoke();
    }

    internal void ReportError(string message, Exception? error = null)
    {
        if (Error == message) return;
        Error = message;
        DiagnosticLog.Record("companion_position_error", new { errorType = error?.GetType().Name ?? "placement" });
        Changed?.Invoke();
    }
}

internal sealed class CompanionMoveHandle : Button
{
    private readonly CompanionPosition position;
    private (Native.POINT Pointer, Native.POINT Origin)? drag;
    private bool dragged;
    private readonly Action? activate;

    internal CompanionMoveHandle(CompanionPosition position, Action? activate = null)
    {
        this.position = position;
        this.activate = activate;
        // Implicit Button styles are not inherited by derived control types.
        SetResourceReference(StyleProperty, typeof(Button));
        Content = "Move";
        Padding = new Thickness(8, 4, 8, 4);
        Cursor = Cursors.SizeAll;
        ToolTip = activate is null
            ? "When pinned, drag to move. Keyboard: arrow keys move 10 pixels; Shift+arrow moves 1 pixel."
            : "Click to open MSGuide. Drag to move the pinned logo.";
        AutomationProperties.SetName(this, activate is null ? "Move pinned companion" : "Open MSGuide");
        AutomationProperties.SetHelpText(this, ToolTip.ToString());
        LostMouseCapture += (_, _) => FinishDrag();
    }

    internal static CompanionMoveHandle ForLogo(CompanionPosition position, UIElement visual,
        Action? activate = null)
    {
        var logo = new CompanionMoveHandle(position, activate)
        {
            Content = visual, Focusable = false, Padding = new Thickness(0),
            Margin = new Thickness(0), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Style = null,
            ToolTip = activate is null ? "Drag to move this window." : "Click to open MSGuide. Drag to move.",
            Template = (ControlTemplate)Application.Current.FindResource("CompanionLogoTemplate")
        };
        AutomationProperties.SetHelpText(logo, activate is null
            ? "Drag to move this window. For keyboard movement, use Move in Settings."
            : "Click to open MSGuide or drag to move. Ctrl+Alt+M also opens MSGuide.");
        return logo;
    }

    internal static bool ExceedsDragThreshold(Native.POINT start, Native.POINT current, double scale) =>
        Math.Abs((long)current.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance * scale
        || Math.Abs((long)current.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance * scale;

    internal static Native.POINT DragOrigin(Native.POINT origin, Native.POINT start, Native.POINT current) =>
        new() { X = origin.X + current.X - start.X, Y = origin.Y + current.Y - start.Y };

    private bool TryOrigin(out Native.POINT origin)
    {
        origin = default;
        var window = Window.GetWindow(this);
        if (window is null || !Native.GetWindowRect(new WindowInteropHelper(window).Handle, out var rect))
            return false;
        origin = new() { X = rect.Left, Y = rect.Top };
        return true;
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (position.FollowPointer) { base.OnPreviewMouseLeftButtonDown(e); return; }
        e.Handled = true;
        if (!Native.GetCursorPos(out var pointer) || !TryOrigin(out var origin) || !CaptureMouse())
        {
            position.ReportError("Could not start moving the companion. Try again from the compact prompt.");
            return;
        }
        if (Focusable) Focus();
        dragged = false;
        drag = (pointer, origin);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (drag is not { } start) return;
        if (position.FollowPointer) { FinishDrag(); return; }
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (Native.GetCursorPos(out var pointer))
        {
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            dragged |= ExceedsDragThreshold(start.Pointer, pointer, scale);
            if (dragged) position.MoveTo(DragOrigin(start.Origin, start.Pointer, pointer));
        }
        else
        {
            FinishDrag();
            position.ReportError("Could not read the pointer position. The companion stopped moving.");
        }
        e.Handled = true;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (drag is null) { base.OnPreviewMouseLeftButtonUp(e); return; }
        e.Handled = true;
        bool click = !dragged && new Rect(0, 0, ActualWidth, ActualHeight).Contains(e.GetPosition(this));
        FinishDrag();
        if (click) OnClick();
    }

    private void FinishDrag()
    {
        if (drag is null) return;
        drag = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (dragged) position.Save();
        dragged = false;
    }

    protected override void OnClick()
    {
        if (!IsEnabled || position.FollowPointer) return;
        base.OnClick();
        activate?.Invoke();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (MoveWithKey(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    internal bool MoveWithKey(Key key, ModifierKeys modifiers)
    {
        if (position.FollowPointer || key is not (Key.Left or Key.Right or Key.Up or Key.Down)
            || (modifiers & ~ModifierKeys.Shift) != ModifierKeys.None || !TryOrigin(out var origin))
            return false;
        int delta = modifiers == ModifierKeys.Shift ? 1 : 10;
        position.MoveTo(new Native.POINT
        {
            X = origin.X + (key == Key.Left ? -delta : key == Key.Right ? delta : 0),
            Y = origin.Y + (key == Key.Up ? -delta : key == Key.Down ? delta : 0)
        });
        position.Save();
        return true;
    }
}
