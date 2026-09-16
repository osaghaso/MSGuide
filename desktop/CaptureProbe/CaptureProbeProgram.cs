using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using MSGuide.Desktop;

internal static class CaptureProbeProgram
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? hwndValue = null, output = null;
        bool selfTest = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--hwnd" && hwndValue is null && ++i < args.Length) hwndValue = args[i];
            else if (args[i] == "--output" && output is null && ++i < args.Length) output = args[i];
            else if (args[i] == "--self-test" && !selfTest) selfTest = true;
            else return Fail(output, "invalid-arguments");
        }
        if (output is null || selfTest == (hwndValue is not null))
            return Fail(output, "invalid-arguments");
        if (selfTest) return RunSelfTest(output);
        if (!TryHandle(hwndValue!, out var hwnd)) return Fail(output, "invalid-arguments");
        try
        {
            CaptureProbe.WriteJson(hwnd, output);
            return 0;
        }
        catch (OperationCanceledException) { return Fail(output, "cancelled"); }
        catch (InvalidOperationException) { return Fail(output, "window-unavailable-or-changed"); }
        catch (Exception) { return Fail(output, "probe-error"); }
    }

    private static int RunSelfTest(string output)
    {
        int exitCode = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new Window
        {
            Width = 520,
            Height = 360,
            Title = "MSGuide WGC Probe",
            ShowActivated = false,
            Content = new Border
            {
                Background = Brushes.White,
                Padding = new Thickness(32),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Windows Graphics Capture",
                            FontSize = 28,
                            Foreground = Brushes.Black
                        },
                        new Button
                        {
                            Content = "Synthetic camera control",
                            Margin = new Thickness(0, 30, 0, 0)
                        }
                    }
                }
            }
        };
        window.ContentRendered += async (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                Native.GetWindowThreadProcessId(hwnd, out var pid);
                var choice = new WindowChoice(hwnd, pid, window.Title);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var snapshot = await CaptureService.Capture(choice, deadline.Token);
                if (!snapshot.Valid() || snapshot.Png.Length <= 8
                    || !snapshot.Png.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                    || snapshot.Elements.Any(element => string.IsNullOrWhiteSpace(element.TargetId)))
                    throw new InvalidOperationException();
                var report = await Task.Run(() => CaptureProbe.RunWgcOnly(hwnd));
                Write(output, report);
                exitCode = report.Attempts is [{ Backend: "WindowsGraphicsCapture", Accepted: true }]
                    && report.Automation.RootMatched ? 0 : 1;
            }
            catch (Exception) { Fail(output, "probe-error"); }
            finally
            {
                window.Close();
                app.Shutdown();
            }
        };
        window.Show();
        app.Run();
        return exitCode;
    }

    private static bool TryHandle(string value, out nint hwnd)
    {
        bool hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        string digits = hex ? value[2..] : value;
        bool parsed = long.TryParse(digits, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
            CultureInfo.InvariantCulture, out long handle);
        hwnd = new nint(handle);
        return parsed && handle != 0;
    }

    private static int Fail(string? output, string code)
    {
        if (output is not null)
        {
            try
            {
                File.WriteAllText(output, JsonSerializer.Serialize(new
                {
                    version = 1,
                    capturedAt = DateTimeOffset.UtcNow,
                    completed = false,
                    failure = code
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true })
                    + Environment.NewLine);
            }
            catch { }
        }
        return 1;
    }

    private static void Write(string output, CaptureProbeReport report) =>
        File.WriteAllText(output, JsonSerializer.Serialize(report,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true })
            + Environment.NewLine);
}
