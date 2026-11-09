using System.Speech.Recognition;

namespace MicrophoneTest;

/// <summary>
/// Usage: MicrophoneTest [model-folder] [--wave file.wav]
/// With no model folder, the recognizer is chosen from the installed Vosk models.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string? modelPath = null;
        string? wavePath = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--wave" && i + 1 < args.Length)
            {
                wavePath = args[++i];
            }
            else
            {
                modelPath = args[i];
            }
        }

        try
        {
            using SpeechRecognitionEngine engine = modelPath is null
                ? new SpeechRecognitionEngine()
                : new SpeechRecognitionEngine(modelPath);

            Console.WriteLine($"Using recognizer: {engine.RecognizerInfo.Name}");

            if (wavePath is null)
            {
                engine.SetInputToDefaultAudioDevice();
            }
            else
            {
                engine.SetInputToWaveFile(wavePath);
            }

            return new VoiceMenu(engine).Run();
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or ArgumentException)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine("Download a model from https://alphacephei.com/vosk/models and pass its folder as the first argument.");
            return 1;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or FormatException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
