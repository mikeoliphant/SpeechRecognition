using System.Collections.ObjectModel;
using System.Globalization;
using System.Speech.AudioFormat;

namespace System.Speech.Recognition;

/// <summary>
/// Equivalent of the shared desktop recognizer. Vosk has no shared service, so this owns a private
/// engine that listens on the default microphone whenever it is enabled and has grammars loaded.
/// </summary>
public class SpeechRecognizer : IDisposable
{
    private readonly SpeechRecognitionEngine engine;
    private bool enabled = true;
    private bool inputAssigned;
    private bool isDisposed;

    public SpeechRecognizer()
    {
        engine = new SpeechRecognitionEngine();
        engine.RecognizeCompleted += (_, _) => StateChanged?.Invoke(this, new StateChangedEventArgs(State));
    }

    public event EventHandler<StateChangedEventArgs>? StateChanged;

    public event EventHandler<EmulateRecognizeCompletedEventArgs>? EmulateRecognizeCompleted
    {
        add => engine.EmulateRecognizeCompleted += value;
        remove => engine.EmulateRecognizeCompleted -= value;
    }

    public event EventHandler<LoadGrammarCompletedEventArgs>? LoadGrammarCompleted
    {
        add => engine.LoadGrammarCompleted += value;
        remove => engine.LoadGrammarCompleted -= value;
    }

    public event EventHandler<SpeechDetectedEventArgs>? SpeechDetected
    {
        add => engine.SpeechDetected += value;
        remove => engine.SpeechDetected -= value;
    }

    public event EventHandler<SpeechRecognizedEventArgs>? SpeechRecognized
    {
        add => engine.SpeechRecognized += value;
        remove => engine.SpeechRecognized -= value;
    }

    public event EventHandler<SpeechRecognitionRejectedEventArgs>? SpeechRecognitionRejected
    {
        add => engine.SpeechRecognitionRejected += value;
        remove => engine.SpeechRecognitionRejected -= value;
    }

    public event EventHandler<RecognizerUpdateReachedEventArgs>? RecognizerUpdateReached
    {
        add => engine.RecognizerUpdateReached += value;
        remove => engine.RecognizerUpdateReached -= value;
    }

    public event EventHandler<SpeechHypothesizedEventArgs>? SpeechHypothesized
    {
        add => engine.SpeechHypothesized += value;
        remove => engine.SpeechHypothesized -= value;
    }

    public event EventHandler<AudioSignalProblemOccurredEventArgs>? AudioSignalProblemOccurred
    {
        add => engine.AudioSignalProblemOccurred += value;
        remove => engine.AudioSignalProblemOccurred -= value;
    }

    public event EventHandler<AudioLevelUpdatedEventArgs>? AudioLevelUpdated
    {
        add => engine.AudioLevelUpdated += value;
        remove => engine.AudioLevelUpdated -= value;
    }

    public event EventHandler<AudioStateChangedEventArgs>? AudioStateChanged
    {
        add => engine.AudioStateChanged += value;
        remove => engine.AudioStateChanged -= value;
    }

    public RecognizerState State => engine.IsRunning ? RecognizerState.Listening : RecognizerState.Stopped;

    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled != value)
            {
                enabled = value;
                UpdateListening();
            }
        }
    }

    /// <summary>Stored for compatibility; a private engine has no other clients to pause for.</summary>
    public bool PauseRecognizerOnRecognition { get; set; }

    public ReadOnlyCollection<Grammar> Grammars => engine.Grammars;

    public RecognizerInfo RecognizerInfo => engine.RecognizerInfo;

    public AudioState AudioState => engine.AudioState;

    public int AudioLevel => engine.AudioLevel;

    public TimeSpan AudioPosition => engine.AudioPosition;

    public TimeSpan RecognizerAudioPosition => engine.RecognizerAudioPosition;

    public SpeechAudioFormatInfo? AudioFormat => engine.AudioFormat;

    public int MaxAlternates
    {
        get => engine.MaxAlternates;
        set => engine.MaxAlternates = value;
    }

    public void LoadGrammar(Grammar grammar)
    {
        engine.LoadGrammar(grammar);
        UpdateListening();
    }

    public void LoadGrammarAsync(Grammar grammar)
    {
        engine.LoadGrammarCompleted += OnLoaded;
        engine.LoadGrammarAsync(grammar);

        void OnLoaded(object? sender, LoadGrammarCompletedEventArgs args)
        {
            engine.LoadGrammarCompleted -= OnLoaded;
            UpdateListening();
        }
    }

    public void UnloadGrammar(Grammar grammar)
    {
        engine.UnloadGrammar(grammar);
        UpdateListening();
    }

    public void UnloadAllGrammars()
    {
        engine.UnloadAllGrammars();
        UpdateListening();
    }

    public RecognitionResult? EmulateRecognize(string inputText)
    {
        return engine.EmulateRecognize(inputText);
    }

    public RecognitionResult? EmulateRecognize(string inputText, CompareOptions compareOptions)
    {
        return engine.EmulateRecognize(inputText, compareOptions);
    }

    public RecognitionResult? EmulateRecognize(RecognizedWordUnit[] wordUnits, CompareOptions compareOptions)
    {
        return engine.EmulateRecognize(wordUnits, compareOptions);
    }

    public void EmulateRecognizeAsync(string inputText)
    {
        engine.EmulateRecognizeAsync(inputText);
    }

    public void EmulateRecognizeAsync(string inputText, CompareOptions compareOptions)
    {
        engine.EmulateRecognizeAsync(inputText, compareOptions);
    }

    public void EmulateRecognizeAsync(RecognizedWordUnit[] wordUnits, CompareOptions compareOptions)
    {
        engine.EmulateRecognizeAsync(wordUnits, compareOptions);
    }

    public void RequestRecognizerUpdate()
    {
        engine.RequestRecognizerUpdate();
    }

    public void RequestRecognizerUpdate(object? userToken)
    {
        engine.RequestRecognizerUpdate(userToken);
    }

    public void RequestRecognizerUpdate(object? userToken, TimeSpan audioPositionAheadToRaiseUpdate)
    {
        engine.RequestRecognizerUpdate(userToken, audioPositionAheadToRaiseUpdate);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing && !isDisposed)
        {
            isDisposed = true;
            engine.Dispose();
        }
    }

    private void UpdateListening()
    {
        bool shouldListen = enabled && engine.Grammars.Count > 0;
        if (shouldListen && !engine.IsRunning)
        {
            if (!inputAssigned)
            {
                engine.SetInputToDefaultAudioDevice();
                inputAssigned = true;
            }

            engine.RecognizeAsync(RecognizeMode.Multiple);
            StateChanged?.Invoke(this, new StateChangedEventArgs(RecognizerState.Listening));
        }
        else if (!shouldListen && engine.IsRunning)
        {
            engine.RecognizeAsyncStop();
        }
    }
}
