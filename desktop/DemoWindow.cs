using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace MSGuide.Desktop;

public sealed class DemoWindow : Window
{
    public event EventHandler? WorkflowChanged;
    private readonly StackPanel workflow = new();
    private readonly TextBlock step = new() { FontSize = 13, Foreground = Brushes.LightSkyBlue, Margin = new Thickness(0, 0, 0, 16) };

    public DemoWindow()
    {
        Title = "MSGuide Demo";
        AutomationProperties.SetName(this, "MSGuide Demo");
        Width = 820; Height = 560; MinWidth = 560; MinHeight = 450;
        var page = new StackPanel { Margin = new Thickness(30) };
        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        page.Children.Add(new TextBlock { Text = "BUILD CENTER  /  SYNTHETIC SANDBOX", FontSize = 12, Foreground = Brushes.LightSkyBlue });
        page.Children.Add(new TextBlock { Text = "Release pipeline", FontSize = 30, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) });
        page.Children.Add(new TextBlock { Text = "Contoso.Web  ·  main  ·  #2026.0914.3", Foreground = Brushes.LightSteelBlue, Margin = new Thickness(0, 0, 0, 24) });
        var card = new StackPanel { Margin = new Thickness(22) };
        page.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(24, 35, 52)), CornerRadius = new CornerRadius(12), Child = card });
        card.Children.Add(step);
        card.Children.Add(workflow);
        page.Children.Add(new TextBlock { Text = "Local demonstration only. These buttons change this window; no builds, files, tickets, deployments, or external systems are touched.", Foreground = Brushes.LightSteelBlue, Margin = new Thickness(0, 22, 0, 12) });
        var reset = new Button { Content = "Reset demo", HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(reset, "Reset demo");
        reset.Click += (_, _) => Render(0);
        page.Children.Add(reset);
        Render(0);
    }

    private void Render(int state)
    {
        WorkflowChanged?.Invoke(this, EventArgs.Empty);
        workflow.Children.Clear();
        step.Text = state == 3 ? "WORKFLOW COMPLETE" : $"INVESTIGATION  ·  STEP {state + 1} OF 3";
        string text = state switch
        {
            0 => "Build pipeline needs attention",
            1 => "Build failed: exit code 1",
            2 => "Check compiler errors and missing dependencies",
            _ => "Issue resolved"
        };
        var heading = new TextBlock { Text = text, FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
        AutomationProperties.SetName(heading, text);
        workflow.Children.Add(heading);
        workflow.Children.Add(new TextBlock { Text = state switch
        {
            0 => "The latest compilation did not complete. Inspect its output to begin the investigation.",
            1 => "Compile  •  FAILED\nDuration 00:42  ·  Agent windows-latest\nThe build process returned a non-zero exit code.",
            2 => "Review the first compiler diagnostic. Verify the referenced packages are present and compatible. This sandbox has no real dependency changes to make.",
            _ => "The synthetic investigation is complete. Reset the demo to try another guidance session."
        }, Margin = new Thickness(0, 0, 0, 16), Foreground = Brushes.LightSteelBlue });
        if (state < 3)
        {
            string label = state switch { 0 => "View logs", 1 => "Open troubleshooting", _ => "Mark resolved" };
            var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Color.FromRgb(32, 101, 143)) };
            AutomationProperties.SetName(button, label);
            AutomationProperties.SetAutomationId(button, "DemoStep" + state);
            button.Click += (_, _) => Render(state + 1);
            workflow.Children.Add(button);
        }
    }
}