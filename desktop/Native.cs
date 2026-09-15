using System.Runtime.InteropServices;
using System.Text;

namespace MSGuide.Desktop;

public static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly bool Same(RECT other) => Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;
    }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPels, YPels; public uint ClrUsed, ClrImportant;
    }
    public delegate bool EnumWindowProc(nint hwnd, nint param);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowProc callback, nint param);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsHungAppWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] public static extern nint GetShellWindow();
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern nint WindowFromPoint(POINT point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] public static extern int SetWindowLong(nint hwnd, int index, int value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] public static extern nint MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] public static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);
    [DllImport("user32.dll")] public static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("gdi32.dll")] public static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] public static extern nint CreateDIBSection(nint dc, ref BITMAPINFO info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] public static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(nint dc);

    public static string Title(nint hwnd)
    {
        var text = new StringBuilder(512);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    public static bool NormalWindow(nint hwnd)
    {
        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd) || hwnd == GetShellWindow()
            || GetWindow(hwnd, 4) != 0 || (GetWindowLong(hwnd, -20) & 0x80) != 0) return false;
        if (DwmGetWindowAttribute(hwnd, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        var name = new StringBuilder(128);
        GetClassName(hwnd, name, name.Capacity);
        return name.ToString() is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            && !string.IsNullOrWhiteSpace(Title(hwnd));
    }
}

public sealed record WindowChoice(nint Handle, uint ProcessId, string Title)
{
    public string Id => $"{ProcessId}:{Handle.ToInt64():X}";
    public override string ToString() => Title;
    public bool Matches()
    {
        Native.GetWindowThreadProcessId(Handle, out var pid);
        return pid == ProcessId && Native.NormalWindow(Handle) && Native.Title(Handle) == Title;
    }
    public static List<WindowChoice> List(nint companion, nint overlay)
    {
        var windows = new List<WindowChoice>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (hwnd != companion && hwnd != overlay && Native.NormalWindow(hwnd))
            {
                Native.GetWindowThreadProcessId(hwnd, out var pid);
                windows.Add(new(hwnd, pid, Native.Title(hwnd)));
            }
            return true;
        }, 0);
        return windows.OrderBy(w => w.Title).ToList();
    }
}