using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace MSGuide.Desktop;

/// <summary>One visible native editor in an explicitly selected Microsoft Notepad process. No global input.</summary>
internal sealed class NotepadEditor : INotepadEditor
{
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, Native.EnumWindowProc callback, nint param);
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nuint result);

    private readonly nint companion, edit;
    private readonly DateTime startTime;
    private readonly string executable, editorClass;
    private readonly Native.RECT bounds;
    private int[]? tabId;
    private readonly bool packaged;
    private static int tabCheckBusy;
    public WindowChoice Window { get; }

    internal NotepadEditor(WindowChoice window, nint companion)
    {
        Window = window; this.companion = companion;
        using var process = Process.GetProcessById(checked((int)window.ProcessId));
        startTime = process.StartTime;
        executable = process.MainModule?.FileName ?? "";
        packaged = executable.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase);
        if (!TrustedExecutable(executable) || Class(window.Handle) != "Notepad")
            throw new InvalidOperationException("Select a supported Microsoft Notepad window. Other editors are not authorized.");
        if (!Native.GetWindowRect(window.Handle, out bounds)) throw new InvalidOperationException("Notepad bounds are unavailable.");
        edit = FindEditor();
        editorClass = Class(edit);
        if (packaged) tabId = CheckSingleTab();
        Validate();
    }

    internal static bool TrustedExecutable(string path)
    {
        string system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe");
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + Path.DirectorySeparatorChar;
        if (path.Equals(system, StringComparison.OrdinalIgnoreCase)) return true;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        var parts = path[root.Length..].Split(Path.DirectorySeparatorChar);
        return parts.Length == 3 && parts[0].StartsWith("Microsoft.WindowsNotepad_", StringComparison.OrdinalIgnoreCase)
            && parts[0].EndsWith("__8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase)
            && parts[1].Equals("Notepad", StringComparison.OrdinalIgnoreCase)
            && parts[2].Equals("Notepad.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string Class(nint handle)
    { var text = new StringBuilder(256); Native.GetClassName(handle, text, text.Capacity); return text.ToString(); }

    private nint FindEditor()
    {
        var editors = new List<nint>();
        EnumChildWindows(Window.Handle, (hwnd, _) =>
        {
            if (Native.IsWindowVisible(hwnd) && Class(hwnd) is "Edit" or "RichEditD2DPT") editors.Add(hwnd);
            return true;
        }, 0);
        if (editors.Count != 1) throw new InvalidOperationException("Notepad must expose exactly one visible supported editor. Dismiss welcome/settings dialogs and select a blank tab; no fallback input is used.");
        return editors[0];
    }

    public void Validate()
    {
        using var process = Process.GetProcessById(checked((int)Window.ProcessId));
        Native.GetWindowThreadProcessId(Window.Handle, out var pid);
        Native.GetWindowThreadProcessId(edit, out var editorPid);
        nint foreground = Native.GetForegroundWindow();
        if (process.StartTime != startTime || !string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)
            || pid != Window.ProcessId || editorPid != pid || !Native.NormalWindow(Window.Handle)
            || Class(Window.Handle) != "Notepad" || !IsWindowEnabled(Window.Handle)
            || !Native.GetWindowRect(Window.Handle, out var now) || !bounds.Same(now)
            || (foreground != companion && foreground != Window.Handle)
            || !IsChild(Window.Handle, edit) || !IsWindowEnabled(edit) || !Native.IsWindowVisible(edit)
            || Class(edit) != editorClass || FindEditor() != edit
            || (Native.GetWindowLong(edit, -16) & 0x820) != 0) // ES_READONLY | ES_PASSWORD
            throw new InvalidOperationException("Notepad scope changed: focus, editor/tab, window, permissions or bounds. Task stopped.");
        if (packaged && !CheckSingleTab().SequenceEqual(tabId!))
            throw new InvalidOperationException("Notepad tab identity changed. Task stopped.");
    }

    private int[] CheckSingleTab()
    {
        // UIA may hang in a provider. Bound the caller and permit only one outstanding worker globally.
        if (Interlocked.CompareExchange(ref tabCheckBusy, 1, 0) != 0)
            throw new InvalidOperationException("A Notepad tab check is still returning. No action authorized.");
        var check = Task.Run(() =>
        {
            try
            {
                var root = AutomationElement.FromHandle(Window.Handle);
                var tabs = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
                if (tabs.Count != 1 || tabs[0].Current.IsOffscreen
                    || tabs[0].Current.ProcessId != (int)Window.ProcessId
                    || !tabs[0].TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
                    || !((SelectionItemPattern)pattern).Current.IsSelected)
                    throw new InvalidOperationException("Use a separate Notepad window with exactly one blank tab. Multi-tab windows are not supported.");
                return tabs[0].GetRuntimeId(); // No tab title or document text is read.
            }
            finally { Interlocked.Exchange(ref tabCheckBusy, 0); }
        });
        try { return check.WaitAsync(TimeSpan.FromMilliseconds(500)).GetAwaiter().GetResult(); }
        catch (TimeoutException)
        {
            _ = check.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            throw new InvalidOperationException("Notepad tab validation timed out. No action authorized; no retry.");
        }
    }

    private nuint Message(uint message, nint wParam = 0, nint lParam = 0)
    {
        Validate();
        if (message == 0xC && Native.GetForegroundWindow() != companion)
            throw new InvalidOperationException("Focus changed before insertion. No action taken.");
        // Cross-process system messages are marshalled by Windows. No custom pointer messages or SendInput.
        const uint flags = 0x1 | 0x2 | 0x20; // SMTO_BLOCK | SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT
        // Nonzero function return means delivery succeeded; the separate result may legitimately be zero.
        if (SendMessageTimeout(edit, message, wParam, lParam, flags, 500, out var result) == 0)
            throw new InvalidOperationException("Notepad did not acknowledge the request. Outcome is unknown; no retry. An in-flight operation may finish after Stop.");
        return result;
    }

    public int Length() => checked((int)Message(0xE)); // WM_GETTEXTLENGTH; no text content read.
    public string Read()
    {
        const int capacity = 1002;
        nint buffer = Marshal.AllocHGlobal(capacity * 2);
        try
        {
            int length = checked((int)Message(0xD, capacity, buffer)); // WM_GETTEXT
            if (length > 1000) throw new InvalidOperationException("Unexpected editor length. Task stopped.");
            return Marshal.PtrToStringUni(buffer, length) ?? "";
        }
        finally { for (int i = 0; i < capacity; i++) Marshal.WriteInt16(buffer, i * 2, 0); Marshal.FreeHGlobal(buffer); }
    }

    public void Write(string text)
    {
        if (Environment.GetEnvironmentVariable("MSGUIDE_ENABLE_EXPERIMENTAL_NOTEPAD_CONTROL") != "1")
            throw new InvalidOperationException("Notepad control is experimental and has not passed native acceptance. Enable it explicitly for synthetic testing only; Guide me remains available.");
        // The user remains in the companion while this single write runs, not typing into the target.
        if (Native.GetForegroundWindow() != companion)
            throw new InvalidOperationException("Keep MSGuide foreground for approved text insertion. No action taken.");
        if (Length() != 0) throw new InvalidOperationException("Editor is no longer empty. Nothing overwritten.");
        nint buffer = Marshal.StringToHGlobalUni(text);
        try { if (Message(0xC, 0, buffer) == 0) throw new InvalidOperationException("Notepad rejected text insertion. No retry."); }
        finally { for (int i = 0; i <= text.Length; i++) Marshal.WriteInt16(buffer, i * 2, 0); Marshal.FreeHGlobal(buffer); }
    }

    public (Native.RECT Bounds, double[] Box) Target()
    {
        Validate();
        if (!Native.GetWindowRect(edit, out var rect)) throw new InvalidOperationException("Editor bounds unavailable.");
        var box = Safety.AutomationBox(new Rect(rect.Left, rect.Top, rect.Width, rect.Height), bounds)
            ?? throw new InvalidOperationException("Editor is outside the approved window.");
        return (bounds, box);
    }
}