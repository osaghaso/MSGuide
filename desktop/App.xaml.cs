using System.Windows;
using System.IO;
using System.Text.Json;

namespace MSGuide.Desktop;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyAccessibilityTheme();
        bool integration = e.Args.Contains("--integration-test"), self = e.Args.Contains("--self-test");
        bool capture = e.Args.Contains("--capture-test");
        bool control = e.Args.Contains("--control-test");
        bool controlComponent = e.Args.Contains("--control-component-test");
        bool native = e.Args.Contains("--native-diagnostic");
        bool notepad = e.Args.Contains("--notepad-test");
        bool notepadGuide = e.Args.Contains("--notepad-guide-test");
        if (integration || self || capture || control || controlComponent || native || notepad || notepadGuide)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var checks = new List<string>();
            string stage = "arguments", failure = "", detail = "";
            string? results = null;
            nint notepadHandle = 0;
            try
            {
                if ((integration ? 1 : 0) + (self ? 1 : 0) + (capture ? 1 : 0) + (control ? 1 : 0) + (controlComponent ? 1 : 0) + (native ? 1 : 0) + (notepad ? 1 : 0) + (notepadGuide ? 1 : 0) != 1) throw new InvalidOperationException();
                for (int i = 0; i < e.Args.Length; i++)
                {
                    if (e.Args[i] is "--integration-test" or "--self-test" or "--capture-test" or "--control-test" or "--control-component-test" or "--native-diagnostic" or "--notepad-test" or "--notepad-guide-test") continue;
                    if (e.Args[i] == "--notepad-hwnd" && (notepad || notepadGuide) && notepadHandle == 0 && ++i < e.Args.Length)
                    {
                        notepadHandle = new nint(long.Parse(e.Args[i], System.Globalization.CultureInfo.InvariantCulture));
                        continue;
                    }
                    if (e.Args[i] != "--test-results" || results is not null || ++i == e.Args.Length)
                        throw new InvalidOperationException();
                    results = e.Args[i];
                }
                if (capture) results ??= "capture-results.json";
                if (integration) await IntegrationTests.Run(checks, value => stage = value);
                else if (capture) await CaptureTests.Run(checks, value => stage = value);
                else if (control) await ControlTests.Run(checks, value => stage = value);
                else if (controlComponent) await ControlTests.Run(checks, value => stage = value, component: true);
                else if (native) await NativeTests.Run(checks, value => stage = value);
                else if (notepad || notepadGuide)
                {
                    if (notepadHandle == 0) throw new InvalidOperationException();
                    await NotepadTests.RunNative(notepadHandle, checks, value => stage = value,
                        notepadGuide ? InteractionMode.Guide : InteractionMode.Control);
                }
                else { stage = "self-test"; SelfTests.Run(); checks.Add("desktop-safety"); NotepadTests.Run(checks); }
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name;
                // Only sealed source-line assertions or literal-only capture codes may supply detail.
                if (ex is IntegrationTests.AssertionFailure or CaptureTests.AssertionFailure or CaptureTests.CaptureFailure or ControlTests.AssertionFailure or NotepadTests.AssertionFailure) detail = ex.Message;
            }
            string Report() => JsonSerializer.Serialize(new { test = integration ? "integration" : capture ? "capture" : control ? "control" : controlComponent ? "control-component" : native ? "native-diagnostic" : notepadGuide ? "notepad-guide-native" : notepad ? "notepad-native" : "self", passed = failure.Length == 0, checks, stage, failure, detail });
            if (results is not null)
            {
                try { File.WriteAllText(results, Report() + Environment.NewLine); }
                catch (Exception ex) { stage = "write-report"; failure = ex.GetType().Name; }
            }
            try
            {
                using var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                stdout.WriteLine(Report());
            }
            catch { /* WinExe without redirected stdout: use --test-results for a persistent report. */ }
            Shutdown(failure.Length == 0 ? 0 : 1);
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private void ApplyAccessibilityTheme()
    {
        if (!SystemParameters.HighContrast) return;
        Resources["CanvasBrush"] = SystemColors.WindowBrush;
        Resources["SurfaceBrush"] = SystemColors.ControlBrush;
        Resources["SurfaceRaisedBrush"] = SystemColors.ControlBrush;
        Resources["SurfaceHoverBrush"] = SystemColors.HighlightBrush;
        Resources["InputBrush"] = SystemColors.WindowBrush;
        Resources["BorderBrush"] = SystemColors.ActiveBorderBrush;
        Resources["BorderStrongBrush"] = SystemColors.HighlightBrush;
        Resources["TextBrush"] = SystemColors.WindowTextBrush;
        Resources["MutedTextBrush"] = SystemColors.WindowTextBrush;
        Resources["AccentBrush"] = SystemColors.HighlightBrush;
        Resources["AccentStrongBrush"] = SystemColors.HighlightBrush;
        Resources["AccentHoverBrush"] = SystemColors.HighlightBrush;
        Resources["AccentSoftBrush"] = SystemColors.ControlBrush;
        Resources["AccentTextBrush"] = SystemColors.HighlightTextBrush;
        Resources["SuccessBrush"] = SystemColors.WindowTextBrush;
        Resources["SuccessSoftBrush"] = SystemColors.WindowBrush;
        Resources["WarningBrush"] = SystemColors.WindowTextBrush;
        Resources["WarningSoftBrush"] = SystemColors.WindowBrush;
        Resources["DangerBrush"] = SystemColors.WindowTextBrush;
        Resources["DangerSoftBrush"] = SystemColors.WindowBrush;
    }
}