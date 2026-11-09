using System.ComponentModel;

namespace System.Speech.Recognition;

public class AudioLevelUpdatedEventArgs : EventArgs
{
    internal AudioLevelUpdatedEventArgs(int audioLevel)
    {
        AudioLevel = audioLevel;
    }

    public int AudioLevel { get; }
}

public class AudioSignalProblemOccurredEventArgs : EventArgs
{
    internal AudioSignalProblemOccurredEventArgs(
        AudioSignalProblem audioSignalProblem,
        int audioLevel,
        TimeSpan audioPosition,
        TimeSpan recognizerAudioPosition)
    {
        AudioSignalProblem = audioSignalProblem;
        AudioLevel = audioLevel;
        AudioPosition = audioPosition;
        RecognizerAudioPosition = recognizerAudioPosition;
    }

    public AudioSignalProblem AudioSignalProblem { get; }

    public int AudioLevel { get; }

    public TimeSpan AudioPosition { get; }

    public TimeSpan RecognizerAudioPosition { get; }
}

public class AudioStateChangedEventArgs : EventArgs
{
    internal AudioStateChangedEventArgs(AudioState audioState)
    {
        AudioState = audioState;
    }

    public AudioState AudioState { get; }
}

public class EmulateRecognizeCompletedEventArgs : AsyncCompletedEventArgs
{
    internal EmulateRecognizeCompletedEventArgs(RecognitionResult? result, Exception? error, bool cancelled, object? userState)
        : base(error, cancelled, userState)
    {
        Result = result;
    }

    public RecognitionResult? Result { get; }
}

public class LoadGrammarCompletedEventArgs : AsyncCompletedEventArgs
{
    internal LoadGrammarCompletedEventArgs(Grammar grammar, Exception? error, bool cancelled, object? userState)
        : base(error, cancelled, userState)
    {
        Grammar = grammar;
    }

    public Grammar Grammar { get; }
}

public class RecognitionEventArgs : EventArgs
{
    internal RecognitionEventArgs(RecognitionResult result)
    {
        Result = result;
    }

    public RecognitionResult Result { get; }
}

public class SpeechHypothesizedEventArgs : RecognitionEventArgs
{
    internal SpeechHypothesizedEventArgs(RecognitionResult result)
        : base(result)
    {
    }
}

public class SpeechRecognitionRejectedEventArgs : RecognitionEventArgs
{
    internal SpeechRecognitionRejectedEventArgs(RecognitionResult result)
        : base(result)
    {
    }
}

public class SpeechRecognizedEventArgs : RecognitionEventArgs
{
    internal SpeechRecognizedEventArgs(RecognitionResult result)
        : base(result)
    {
    }
}

public class RecognizeCompletedEventArgs : AsyncCompletedEventArgs
{
    internal RecognizeCompletedEventArgs(
        RecognitionResult? result,
        bool initialSilenceTimeout,
        bool babbleTimeout,
        bool inputStreamEnded,
        TimeSpan audioPosition,
        Exception? error,
        bool cancelled)
        : base(error, cancelled, null)
    {
        Result = result;
        InitialSilenceTimeout = initialSilenceTimeout;
        BabbleTimeout = babbleTimeout;
        InputStreamEnded = inputStreamEnded;
        AudioPosition = audioPosition;
    }

    public RecognitionResult? Result { get; }

    public bool InitialSilenceTimeout { get; }

    public bool BabbleTimeout { get; }

    public bool InputStreamEnded { get; }

    public TimeSpan AudioPosition { get; }
}

public class RecognizerUpdateReachedEventArgs : EventArgs
{
    internal RecognizerUpdateReachedEventArgs(object? userToken, TimeSpan audioPosition)
    {
        UserToken = userToken;
        AudioPosition = audioPosition;
    }

    public object? UserToken { get; }

    public TimeSpan AudioPosition { get; }
}

public class SpeechDetectedEventArgs : EventArgs
{
    internal SpeechDetectedEventArgs(TimeSpan audioPosition)
    {
        AudioPosition = audioPosition;
    }

    public TimeSpan AudioPosition { get; }
}

public class StateChangedEventArgs : EventArgs
{
    internal StateChangedEventArgs(RecognizerState recognizerState)
    {
        RecognizerState = recognizerState;
    }

    public RecognizerState RecognizerState { get; }
}
