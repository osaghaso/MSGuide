using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace MSGuide.Desktop;

internal sealed record SpeechControlsState(
    string RecordLabel, string RecordName, bool CanRecord, bool ShowAppend, bool CanAppend,
    string InputState, bool CanSelectInput, string StopLabel, string StopName, bool ShowLevel)
{
    internal static SpeechControlsState From(SpeechService speech, bool hasDraft, bool hasInput) => new(
        speech.Stopping ? "Stopping microphone..." : speech.Finishing ? "Transcribing..."
            : speech.Listening ? "Stop & transcribe" : hasDraft ? "New voice question" : "Start microphone",
        speech.Listening ? "Stop recording and transcribe"
            : "Record a new voice question; replace the draft only after recognition succeeds",
        !speech.Finishing && !speech.Stopping && !speech.InputStopUnconfirmed && hasInput,
        hasDraft && !speech.Busy, !speech.Busy && hasInput,
        speech.InputStopUnconfirmed ? "MIC STATUS UNKNOWN" : speech.Stopping ? "MIC STOPPING"
            : speech.Finishing ? "TRANSCRIBING" : speech.Listening ? "MIC ON" : "MIC OFF",
        !speech.Busy, speech.Finishing ? "Cancel transcription" : "Stop audio",
        speech.Finishing ? "Cancel local transcription" : "Stop dictation and playback", speech.Listening);
}

internal sealed record CompanionVoiceActions(
    Action Record, Action Append, Action Stop, Action Refresh,
    Action<MicrophoneChoice> SelectInput, Action Dismiss);

internal sealed class CompanionVoiceControls : StackPanel
{
    internal Button RecordButton { get; } = new();
    internal Button AppendButton { get; } = new() { Content = "Append", Visibility = Visibility.Collapsed };
    internal Button StopButton { get; } = new() { Content = "Stop audio" };
    internal Button RefreshButton { get; } = new() { Content = "Refresh inputs" };
    internal ComboBox InputPicker { get; } = new();
    internal TextBlock StatusText { get; } = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    internal TextBlock PreviewText { get; } = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    internal ProgressBar InputLevel { get; } = new() { Maximum = 100, Height = 6, Margin = new Thickness(0, 5, 0, 5) };
    private static readonly Geometry MicrophoneIcon = Icon(
        "M 9,5 A 3,3 0 0 1 15,5 L 15,12 A 3,3 0 0 1 9,12 Z M 5,11 L 5,12 A 7,7 0 0 0 19,12 L 19,11 M 12,19 L 12,23 M 8,23 L 16,23");
    private static readonly Geometry StopIcon = Icon("M 4,4 L 20,4 L 20,20 L 4,20 Z");
    private static readonly Geometry ProcessingIcon = Icon(
        "M 5,3 L 19,3 M 5,21 L 19,21 M 7,3 L 7,7 L 17,17 L 17,21 M 17,3 L 17,7 L 7,17 L 7,21");
    private static readonly Geometry WarningIcon = Icon("M 12,2 L 23,22 L 1,22 Z M 12,9 L 12,14 M 12,18 L 12,18.5");
    private readonly Path recordIcon = new()
    {
        Width = 22, Height = 24, Stretch = Stretch.Uniform, StrokeThickness = 1.8,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false, Focusable = false,
        Data = MicrophoneIcon
    };
    private readonly TextBlock recordLabel = new()
    {
        Text = "Record question", FontSize = 14, FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(10, 0, 8, 0)
    };
    private readonly StackPanel secondaryActions = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel options = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly Grid controls = new();
    private readonly Expander optionsExpander;
    private readonly bool hasActions;
    private bool composerToolbar;
    private SpeechControlsState? currentState;
    private bool updatingInput;
    private string inputState = "MIC OFF";

    internal CompanionVoiceControls(CompanionVoiceActions? actions)
    {
        hasActions = actions is not null;
        Margin = new Thickness(0);
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition());
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foreach (var button in new[] { RecordButton, AppendButton, StopButton })
        {
            button.MinHeight = 44;
            button.Padding = new Thickness(10, 6, 10, 6);
            button.Margin = new Thickness(0);
            button.FontSize = 12;
            button.VerticalAlignment = VerticalAlignment.Center;
            button.Focusable = button.IsTabStop = true;
        }
        RecordButton.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        RecordButton.Width = RecordButton.Height = RecordButton.MinWidth = RecordButton.MinHeight = 48;
        RecordButton.Padding = new Thickness(10);
        RecordButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        RecordButton.VerticalContentAlignment = VerticalAlignment.Center;
        RecordButton.Content = recordIcon;
        recordIcon.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = RecordButton });
        AutomationProperties.SetName(RecordButton, "Record a new voice question");
        ToolTipService.SetShowOnDisabled(RecordButton, true);
        AppendButton.SetResourceReference(StyleProperty, "QuietButtonStyle");
        AppendButton.ToolTip = "Record more words and append them to the current draft. Nothing is submitted automatically.";
        AutomationProperties.SetHelpText(AppendButton, (string)AppendButton.ToolTip);
        AutomationProperties.SetName(StopButton, "Stop dictation and playback");
        RecordButton.Click += (_, _) => { if (RecordButton.IsEnabled) actions?.Record(); };
        AppendButton.Click += (_, _) =>
        {
            if (AppendButton.IsEnabled && AppendButton.Visibility == Visibility.Visible) actions?.Append();
        };
        StopButton.Click += (_, _) => { if (StopButton.IsEnabled) actions?.Stop(); };
        RefreshButton.Click += (_, _) => { if (RefreshButton.IsEnabled) actions?.Refresh(); };
        InputPicker.SelectionChanged += (_, _) =>
        {
            if (!updatingInput && InputPicker.IsEnabled && InputPicker.SelectedItem is MicrophoneChoice input)
                actions?.SelectInput(input);
        };
        AutomationProperties.SetName(AppendButton, "Append dictation to the current question");
        AutomationProperties.SetName(InputPicker, "Microphone for local dictation");
        AutomationProperties.SetName(RefreshButton, "Refresh microphone inputs");
        AutomationProperties.SetName(InputLevel, "Microphone input level");
        AutomationProperties.SetLiveSetting(StatusText, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(PreviewText, AutomationLiveSetting.Polite);
        controls.Children.Add(RecordButton);
        Grid.SetColumn(recordLabel, 1);
        controls.Children.Add(recordLabel);
        Grid.SetColumn(secondaryActions, 2);
        secondaryActions.Children.Add(AppendButton);
        controls.Children.Add(secondaryActions);
        Children.Add(controls);
        StatusText.Margin = new Thickness(0, 6, 0, 0);
        PreviewText.Margin = new Thickness(0, 4, 0, 0);
        Children.Add(StatusText);
        Children.Add(PreviewText);
        Children.Add(InputLevel);
        InputPicker.MinHeight = 40;
        RefreshButton.HorizontalAlignment = HorizontalAlignment.Left;
        options.Children.Add(InputPicker);
        options.Children.Add(RefreshButton);
        var disclosure = new TextBlock
        {
            Text = "Local Whisper. Audio stays on this device. Review the words before asking.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4)
        };
        disclosure.SetResourceReference(StyleProperty, "CaptionStyle");
        options.Children.Add(disclosure);
        StopButton.HorizontalAlignment = HorizontalAlignment.Left;
        options.Children.Add(StopButton);
        optionsExpander = new Expander
        {
            Header = "Microphone options", Content = options, Margin = new Thickness(0, 6, 0, 0)
        };
        Children.Add(optionsExpander);
        RecordButton.IsEnabled = AppendButton.IsEnabled = InputPicker.IsEnabled = RefreshButton.IsEnabled = false;
        StopButton.IsEnabled = hasActions;
        InputLevel.Visibility = PreviewText.Visibility = Visibility.Collapsed;
        StatusText.Text = inputState;
        if (actions is null) IsEnabled = false;
    }

    internal void AttachComposerToolbar(Panel toolbar, Panel settings)
    {
        if (composerToolbar) throw new InvalidOperationException("Voice controls already have a composer toolbar.");
        composerToolbar = true;
        controls.Children.Remove(RecordButton);
        controls.Children.Remove(secondaryActions);
        controls.Visibility = Visibility.Collapsed;
        RecordButton.Width = RecordButton.Height = RecordButton.MinWidth = RecordButton.MinHeight = 44;
        RecordButton.Padding = new Thickness(8);
        RecordButton.Margin = new Thickness(0, 0, 4, 0);
        AppendButton.MinWidth = 54;
        AppendButton.Padding = new Thickness(6, 4, 6, 4);
        StopButton.MinWidth = 54;
        StopButton.Padding = new Thickness(6, 4, 6, 4);
        toolbar.Children.Add(RecordButton);
        toolbar.Children.Add(secondaryActions);
        Children.Remove(optionsExpander);
        settings.Children.Add(optionsExpander);
        if (StatusText.Text == "MIC OFF") StatusText.Visibility = Visibility.Collapsed;
    }

    internal void Update(SpeechControlsState state, MicrophoneChoice[] inputs, MicrophoneChoice? selected,
        string status, string preview, double level)
    {
        bool recordHadFocus = RecordButton.IsKeyboardFocusWithin;
        bool appendHadFocus = AppendButton.IsKeyboardFocusWithin;
        bool stopHadFocus = StopButton.IsKeyboardFocusWithin;
        currentState = state;
        RecordButton.IsEnabled = hasActions && state.CanRecord;
        AppendButton.Visibility = state.ShowAppend ? Visibility.Visible : Visibility.Collapsed;
        AppendButton.IsEnabled = hasActions && state.CanAppend;
        StopButton.Content = state.StopLabel == "Cancel transcription" ? "Cancel" : state.StopLabel;
        AutomationProperties.SetName(StopButton, state.StopName);
        StopButton.ToolTip = state.StopLabel == "Cancel transcription"
            ? "Cancel local transcription and stop playback. Keep the current draft; nothing is submitted."
            : state.StopName;
        AutomationProperties.SetHelpText(StopButton, (string)StopButton.ToolTip);
        bool immediateStop = state.InputState is "MIC STOPPING" or "TRANSCRIBING" or "MIC STATUS UNKNOWN";
        var stopParent = immediateStop ? secondaryActions : options;
        if (!ReferenceEquals(StopButton.Parent, stopParent))
        {
            ((Panel)StopButton.Parent).Children.Remove(StopButton);
            stopParent.Children.Add(StopButton);
        }
        InputPicker.IsEnabled = RefreshButton.IsEnabled = hasActions && state.CanSelectInput;
        updatingInput = true;
        try
        {
            if (!InputPicker.Items.OfType<MicrophoneChoice>().SequenceEqual(inputs)) InputPicker.ItemsSource = inputs;
            InputPicker.SelectedItem = selected;
        }
        finally { updatingInput = false; }
        inputState = state.InputState;
        InputLevel.Visibility = state.ShowLevel ? Visibility.Visible : Visibility.Collapsed;
        UpdateFeedback(status, preview);
        InputLevel.Value = level;
        if (recordHadFocus && !RecordButton.IsEnabled && immediateStop) StopButton.Focus();
        else if ((appendHadFocus && !state.ShowAppend || stopHadFocus && !immediateStop) && RecordButton.IsEnabled)
            RecordButton.Focus();
    }

    internal void UpdateFeedback(string status, string preview)
    {
        string detail = status;
        if (inputState == "MIC OFF")
        {
            if (status is "Microphone off. Nothing is listening." or "Microphone off · never always listening") detail = "";
            else if (status.StartsWith("Microphone off. ", StringComparison.Ordinal)) detail = status["Microphone off. ".Length..];
        }
        StatusText.Text = string.IsNullOrWhiteSpace(detail) ? inputState : $"{inputState} · {detail}";
        StatusText.Visibility = composerToolbar && inputState == "MIC OFF" && string.IsNullOrWhiteSpace(detail)
            ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetHelpText(StatusText, $"{inputState} · {status}");
        bool failure = inputState == "MIC STATUS UNKNOWN" || HasInputFailure(detail);
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, failure ? "DangerBrush"
            : status.Contains("Uncertain", StringComparison.Ordinal) ? "WarningBrush" : "MutedTextBrush");
        if (currentState is { } state) UpdatePrimary(state, status, failure);
        PreviewText.Text = preview;
        PreviewText.Visibility = string.IsNullOrWhiteSpace(preview) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdatePrimary(SpeechControlsState state, string status, bool failure)
    {
        recordIcon.Data = state.InputState switch
        {
            "MIC STATUS UNKNOWN" => WarningIcon,
            "MIC STOPPING" or "TRANSCRIBING" => ProcessingIcon,
            "MIC ON" => StopIcon,
            _ => failure ? WarningIcon : MicrophoneIcon
        };
        recordLabel.Text = state.InputState switch
        {
            "MIC STATUS UNKNOWN" => "Microphone status unknown",
            "MIC STOPPING" => "Stopping microphone…",
            "TRANSCRIBING" => "Transcribing…",
            "MIC ON" => "Stop & transcribe",
            _ => !state.CanRecord ? "Choose microphone" : failure ? "Retry microphone"
                : state.ShowAppend ? "Replace with voice" : "Record question"
        };
        string name = state.InputState switch
        {
            "MIC STATUS UNKNOWN" => "Microphone status unknown; recording unavailable",
            "MIC STOPPING" => "Stopping microphone; recording unavailable",
            "TRANSCRIBING" => "Transcribing locally; recording unavailable",
            _ => !state.CanRecord ? "Microphone unavailable; choose an input in Microphone options"
                : failure ? $"Retry microphone; {state.RecordName}" : state.RecordName
        };
        AutomationProperties.SetName(RecordButton, name);
        string help = state.InputState switch
        {
            "MIC ON" => "Stop recording and transcribe locally into the draft. Review the words before asking; nothing is submitted automatically.",
            "MIC STOPPING" => "Waiting for confirmed microphone closure. Cancellation and audio controls remain available beside the microphone.",
            "TRANSCRIBING" => "Transcribing locally. Select Cancel to discard further words and keep the current draft; nothing is submitted automatically.",
            "MIC STATUS UNKNOWN" => status,
            _ => (!state.CanRecord ? "Choose an available input in Microphone options. " : "")
                + "Record a new voice question. Replace the current draft only after recognition succeeds; use Append to keep it and add more words. Nothing is submitted automatically."
        };
        RecordButton.ToolTip = help;
        AutomationProperties.SetHelpText(RecordButton, help);
    }

    private static bool HasInputFailure(string status) =>
        status.StartsWith("Microphone access is blocked.", StringComparison.Ordinal)
        || status.StartsWith("A local speech model or runtime is missing.", StringComparison.Ordinal)
        || status.StartsWith("Local dictation failed", StringComparison.Ordinal)
        || status.StartsWith("The previously selected microphone is unavailable.", StringComparison.Ordinal)
        || status.StartsWith("Microphone devices could not be listed.", StringComparison.Ordinal);

    private static Geometry Icon(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
