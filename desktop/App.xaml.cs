using System.Windows;
using System.IO;
using System.Text.Json;

namespace MSGuide.Desktop;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool integration = e.Args.Contains("--integration-test"), self = e.Args.Contains("--self-test");
        bool capture = e.Args.Contains("--capture-test");
        if (integration || self || capture)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var checks = new List<string>();
            string stage = "arguments", failure = "", detail = "";
            string? results = null;
            try
            {
                if ((integration ? 1 : 0) + (self ? 1 : 0) + (capture ? 1 : 0) != 1) throw new InvalidOperationException();
                for (int i = 0; i < e.Args.Length; i++)
                {
                    if (e.Args[i] is "--integration-test" or "--self-test" or "--capture-test") continue;
                    if (e.Args[i] != "--test-results" || results is not null || ++i == e.Args.Length)
                        throw new InvalidOperationException();
                    results = e.Args[i];
                }
                if (capture) results ??= "capture-results.json";
                if (integration) await IntegrationTests.Run(checks, value => stage = value);
                else if (capture) await CaptureTests.Run(checks, value => stage = value);
                else { stage = "self-test"; SelfTests.Run(); checks.Add("desktop-safety"); }
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name;
                // Only sealed source-line assertions or literal-only capture codes may supply detail.
                if (ex is IntegrationTests.AssertionFailure or CaptureTests.AssertionFailure or CaptureTests.CaptureFailure) detail = ex.Message;
            }
            string Report() => JsonSerializer.Serialize(new { test = integration ? "integration" : capture ? "capture" : "self", passed = failure.Length == 0, checks, stage, failure, detail });
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
}