using System.Collections.ObjectModel;
using System.Globalization;
using System.Speech.AudioFormat;

namespace System.Speech.Recognition;

public class RecognizerInfo : IDisposable
{
    internal RecognizerInfo(string id, string modelPath, CultureInfo culture)
    {
        Id = id;
        ModelPath = modelPath;
        Name = id;
        Description = $"Vosk speech recognizer ({modelPath})";
        Culture = culture;
        SupportedAudioFormats = new ReadOnlyCollection<SpeechAudioFormatInfo>(
            new[] { new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono) });
        AdditionalInfo = new Dictionary<string, string> { ["ModelPath"] = modelPath };
    }

    public string Id { get; }

    public string Name { get; }

    public string Description { get; }

    public CultureInfo Culture { get; }

    public ReadOnlyCollection<SpeechAudioFormatInfo> SupportedAudioFormats { get; }

    public IDictionary<string, string> AdditionalInfo { get; }

    internal string ModelPath { get; }

    public void Dispose()
    {
    }
}

public static class SpeechUI
{
    public static bool SendTextFeedback(RecognitionResult result, string feedback, bool isSuccessfulAction)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(feedback);

        // There is no Windows Speech Recognition UI to show the feedback in.
        return false;
    }
}
