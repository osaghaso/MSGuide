using NAudio.Wave;

namespace MSGuide.Desktop;

internal sealed record MicrophoneChoice(int DeviceNumber, string Name)
{
    public static MicrophoneChoice Default { get; } = new(-1, "Windows default input");

    public override string ToString() => DeviceNumber < 0 ? Name : $"{Name} (input {DeviceNumber + 1})";

    public static MicrophoneChoice[] Enumerate()
    {
        var choices = new List<MicrophoneChoice> { Default };
        for (int index = 0; index < WaveInEvent.DeviceCount; index++)
            choices.Add(new(index, WaveInEvent.GetCapabilities(index).ProductName));
        return choices.ToArray();
    }
}
