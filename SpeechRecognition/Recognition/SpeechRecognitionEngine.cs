using System.Collections.ObjectModel;
using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Internal;
using System.Text.Json;
using Vosk;

namespace System.Speech.Recognition;

public class SpeechRecognitionEngine : IDisposable
{
    private const int MaxExpandedPhrases = 5000;
    private const CompareOptions DefaultEmulateOptions =
        CompareOptions.IgnoreCase | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;

    private readonly object syncRoot = new();
    private readonly List<Grammar> grammars = new();
    private readonly Queue<(object? Token, TimeSpan At)> updateRequests = new();
    private readonly Dictionary<string, int> settings = new(StringComparer.Ordinal)
    {
        ["CFGConfidenceRejectionThreshold"] = 20,
        ["HighConfidenceThreshold"] = 80,
        ["NormalConfidenceThreshold"] = 50,
        ["LowConfidenceThreshold"] = 20,
        ["ResponseSpeed"] = 150,
        ["ComplexResponseSpeed"] = 500,
        ["AdaptationOn"] = 1,
        ["PersistedBackgroundAdaptation"] = 1,
    };

    private AudioInput? input;
    private VoskRecognizer? recognizer;
    private TimeSpan recognizerEpoch;
    private MemoryStream utteranceAudio = new();
    private TimeSpan utteranceAudioStart;
    private DateTime runStartTime;
    private TimeSpan runStartPosition;

    private volatile bool grammarsChanged = true;
    private volatile bool stopRequested;
    private volatile bool cancelRequested;
    private int isRunning;
    private int workerThreadId;
    private Task? worker;
    private bool isDisposed;

    private long audioBytes;
    private int audioLevel;
    private AudioState audioState = AudioState.Stopped;
    private int maxAlternates = 1;
    private TimeSpan initialSilenceTimeout = TimeSpan.Zero;
    private TimeSpan babbleTimeout = TimeSpan.Zero;
    private TimeSpan endSilenceTimeout = TimeSpan.FromMilliseconds(150);
    private TimeSpan endSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(500);

    public SpeechRecognitionEngine()
    {
        RecognizerInfo = ModelCatalog.GetDefault();
    }

    public SpeechRecognitionEngine(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        RecognizerInfo = ModelCatalog.FindForCulture(culture)
            ?? throw new ArgumentException($"No Vosk model was found for culture {culture.Name}.", nameof(culture));
    }

    /// <param name="recognizerId">A recognizer id from <see cref="InstalledRecognizers"/>, or the path of a Vosk model folder.</param>
    public SpeechRecognitionEngine(string recognizerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(recognizerId);
        RecognizerInfo = ModelCatalog.FindById(recognizerId)
            ?? throw new ArgumentException($"No Vosk model was found for '{recognizerId}'.", nameof(recognizerId));
    }

    public SpeechRecognitionEngine(RecognizerInfo recognizerInfo)
    {
        ArgumentNullException.ThrowIfNull(recognizerInfo);
        RecognizerInfo = recognizerInfo;
    }

    public event EventHandler<RecognizeCompletedEventArgs>? RecognizeCompleted;

    public event EventHandler<EmulateRecognizeCompletedEventArgs>? EmulateRecognizeCompleted;

    public event EventHandler<LoadGrammarCompletedEventArgs>? LoadGrammarCompleted;

    public event EventHandler<SpeechDetectedEventArgs>? SpeechDetected;

    public event EventHandler<SpeechRecognizedEventArgs>? SpeechRecognized;

    public event EventHandler<SpeechRecognitionRejectedEventArgs>? SpeechRecognitionRejected;

    public event EventHandler<RecognizerUpdateReachedEventArgs>? RecognizerUpdateReached;

    public event EventHandler<SpeechHypothesizedEventArgs>? SpeechHypothesized;

#pragma warning disable CS0067 // Vosk gives no signal-quality diagnostics, so this is never raised.
    public event EventHandler<AudioSignalProblemOccurredEventArgs>? AudioSignalProblemOccurred;
#pragma warning restore CS0067

    public event EventHandler<AudioLevelUpdatedEventArgs>? AudioLevelUpdated;

    public event EventHandler<AudioStateChangedEventArgs>? AudioStateChanged;

    public TimeSpan InitialSilenceTimeout
    {
        get => initialSilenceTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            initialSilenceTimeout = value;
        }
    }

    public TimeSpan BabbleTimeout
    {
        get => babbleTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            babbleTimeout = value;
        }
    }

    /// <summary>Stored for compatibility. Vosk 0.3.38 does not expose endpointing, so this has no effect.</summary>
    public TimeSpan EndSilenceTimeout
    {
        get => endSilenceTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, TimeSpan.FromSeconds(10));
            endSilenceTimeout = value;
        }
    }

    /// <summary>Stored for compatibility. Vosk 0.3.38 does not expose endpointing, so this has no effect.</summary>
    public TimeSpan EndSilenceTimeoutAmbiguous
    {
        get => endSilenceTimeoutAmbiguous;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, TimeSpan.FromSeconds(10));
            endSilenceTimeoutAmbiguous = value;
        }
    }

    public ReadOnlyCollection<Grammar> Grammars
    {
        get
        {
            lock (syncRoot)
            {
                return new ReadOnlyCollection<Grammar>(grammars.ToList());
            }
        }
    }

    public RecognizerInfo RecognizerInfo { get; }

    public AudioState AudioState => audioState;

    public int AudioLevel => audioLevel;

    public TimeSpan RecognizerAudioPosition => AudioPosition;

    public TimeSpan AudioPosition => BytesToTime(Interlocked.Read(ref audioBytes));

    public SpeechAudioFormatInfo? AudioFormat => input?.SourceFormat;

    /// <summary>
    /// The number of alternates to request. Defaults to 1 because Vosk reports per-word confidence
    /// only for single results; with more than 1, Confidence is a relative score across the alternates.
    /// </summary>
    public int MaxAlternates
    {
        get => maxAlternates;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            if (maxAlternates != value)
            {
                maxAlternates = value;
                grammarsChanged = true;
            }
        }
    }

    internal bool IsRunning => Volatile.Read(ref isRunning) != 0;

    public static ReadOnlyCollection<RecognizerInfo> InstalledRecognizers()
    {
        return new ReadOnlyCollection<RecognizerInfo>(ModelCatalog.Discover().ToList());
    }

    public void SetInputToWaveFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ThrowIfBusy();

        FileStream stream = File.OpenRead(path);
        try
        {
            ReplaceInput(new StreamAudioInput(stream, WaveReader.ReadHeader(stream), ownsStream: true));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void SetInputToWaveStream(Stream audioSource)
    {
        ArgumentNullException.ThrowIfNull(audioSource);
        ThrowIfBusy();
        ReplaceInput(new StreamAudioInput(audioSource, WaveReader.ReadHeader(audioSource), ownsStream: false));
    }

    public void SetInputToAudioStream(Stream audioSource, SpeechAudioFormatInfo audioFormat)
    {
        ArgumentNullException.ThrowIfNull(audioSource);
        ArgumentNullException.ThrowIfNull(audioFormat);
        ThrowIfBusy();
        ReplaceInput(new StreamAudioInput(audioSource, audioFormat, ownsStream: false));
    }

    public void SetInputToNull()
    {
        ThrowIfBusy();
        ReplaceInput(null);
    }

    public void SetInputToDefaultAudioDevice()
    {
        ThrowIfBusy();
        ReplaceInput(new MicrophoneAudioInput());
    }

    public RecognitionResult? Recognize()
    {
        return Recognize(InitialSilenceTimeout);
    }

    public RecognitionResult? Recognize(TimeSpan initialSilenceTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialSilenceTimeout, TimeSpan.Zero);
        BeginRun();
        try
        {
            return RunRecognition(RecognizeMode.Single, initialSilenceTimeout, null).Result;
        }
        finally
        {
            EndRun();
        }
    }

    public void RecognizeAsync()
    {
        RecognizeAsync(RecognizeMode.Single);
    }

    public void RecognizeAsync(RecognizeMode mode)
    {
        BeginRun();
        SynchronizationContext? context = SynchronizationContext.Current;
        worker = Task.Factory.StartNew(() => RunAsyncWorker(mode, context), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void RecognizeAsyncCancel()
    {
        cancelRequested = true;
        stopRequested = true;
    }

    public void RecognizeAsyncStop()
    {
        stopRequested = true;
    }

    public object QueryRecognizerSetting(string settingName)
    {
        ArgumentException.ThrowIfNullOrEmpty(settingName);
        lock (syncRoot)
        {
            return settings.TryGetValue(settingName, out int value)
                ? value
                : throw new KeyNotFoundException($"Unknown recognizer setting '{settingName}'.");
        }
    }

    public void UpdateRecognizerSetting(string settingName, string updatedValue)
    {
        ArgumentNullException.ThrowIfNull(updatedValue);
        UpdateRecognizerSetting(settingName, int.Parse(updatedValue, CultureInfo.InvariantCulture));
    }

    public void UpdateRecognizerSetting(string settingName, int updatedValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(settingName);
        lock (syncRoot)
        {
            if (!settings.ContainsKey(settingName))
            {
                throw new KeyNotFoundException($"Unknown recognizer setting '{settingName}'.");
            }

            settings[settingName] = updatedValue;
        }
    }

    public void LoadGrammar(Grammar grammar)
    {
        ArgumentNullException.ThrowIfNull(grammar);
        ObjectDisposedException.ThrowIf(isDisposed, this);

        lock (syncRoot)
        {
            if (grammar.Loaded)
            {
                throw new InvalidOperationException("The grammar is already loaded.");
            }

            grammar.Loaded = true;
            grammar.Changed = () => grammarsChanged = true;
            grammars.Add(grammar);
            grammarsChanged = true;
        }
    }

    public void LoadGrammarAsync(Grammar grammar)
    {
        ArgumentNullException.ThrowIfNull(grammar);
        ObjectDisposedException.ThrowIf(isDisposed, this);

        SynchronizationContext? context = SynchronizationContext.Current;
        Task.Run(() =>
        {
            Exception? error = null;
            try
            {
                LoadGrammar(grammar);
            }
            catch (Exception exception)
            {
                error = exception;
            }

            Raise(context, () => LoadGrammarCompleted?.Invoke(this, new LoadGrammarCompletedEventArgs(grammar, error, false, null)));
        });
    }

    public void UnloadGrammar(Grammar grammar)
    {
        ArgumentNullException.ThrowIfNull(grammar);
        lock (syncRoot)
        {
            if (!grammars.Remove(grammar))
            {
                throw new InvalidOperationException("The grammar is not loaded in this recognizer.");
            }

            grammar.Loaded = false;
            grammar.Changed = null;
            grammarsChanged = true;
        }
    }

    public void UnloadAllGrammars()
    {
        lock (syncRoot)
        {
            foreach (Grammar grammar in grammars)
            {
                grammar.Loaded = false;
                grammar.Changed = null;
            }

            grammars.Clear();
            grammarsChanged = true;
        }
    }

    public RecognitionResult? EmulateRecognize(string inputText)
    {
        return EmulateRecognize(inputText, DefaultEmulateOptions);
    }

    public RecognitionResult? EmulateRecognize(string inputText, CompareOptions compareOptions)
    {
        return EmulateCore(TokenizeEmulated(inputText), compareOptions, null);
    }

    public RecognitionResult? EmulateRecognize(RecognizedWordUnit[] wordUnits, CompareOptions compareOptions)
    {
        return EmulateCore(TokensFromUnits(wordUnits), compareOptions, null);
    }

    public void EmulateRecognizeAsync(string inputText)
    {
        EmulateRecognizeAsync(inputText, DefaultEmulateOptions);
    }

    public void EmulateRecognizeAsync(string inputText, CompareOptions compareOptions)
    {
        StartEmulateAsync(TokenizeEmulated(inputText), compareOptions);
    }

    public void EmulateRecognizeAsync(RecognizedWordUnit[] wordUnits, CompareOptions compareOptions)
    {
        StartEmulateAsync(TokensFromUnits(wordUnits), compareOptions);
    }

    public void RequestRecognizerUpdate()
    {
        RequestRecognizerUpdate(null);
    }

    public void RequestRecognizerUpdate(object? userToken)
    {
        RequestRecognizerUpdate(userToken, TimeSpan.Zero);
    }

    public void RequestRecognizerUpdate(object? userToken, TimeSpan audioPositionAheadToRaiseUpdate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(audioPositionAheadToRaiseUpdate, TimeSpan.Zero);
        lock (syncRoot)
        {
            updateRequests.Enqueue((userToken, AudioPosition + audioPositionAheadToRaiseUpdate));
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (isDisposed || !disposing)
        {
            return;
        }

        isDisposed = true;
        RecognizeAsyncCancel();

        // A handler running on the worker cannot wait for its own thread; the worker cleans up on exit instead.
        if (Environment.CurrentManagedThreadId != workerThreadId)
        {
            worker?.Wait();
            ReleaseAudioResources();
        }
    }

    private void ReleaseAudioResources()
    {
        recognizer?.Dispose();
        recognizer = null;
        input?.Dispose();
        input = null;
    }

    private void ThrowIfBusy()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        if (IsRunning)
        {
            throw new InvalidOperationException("The recognizer is busy.");
        }
    }

    private void ReplaceInput(AudioInput? newInput)
    {
        input?.Dispose();
        input = newInput;
        Interlocked.Exchange(ref audioBytes, 0);
        DisposeRecognizer();
    }

    private void DisposeRecognizer()
    {
        recognizer?.Dispose();
        recognizer = null;
        grammarsChanged = true;
    }

    private void BeginRun()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        if (input is null)
        {
            throw new InvalidOperationException("No audio input has been set.");
        }

        lock (syncRoot)
        {
            if (grammars.Count == 0)
            {
                throw new InvalidOperationException("At least one grammar must be loaded.");
            }
        }

        if (Interlocked.CompareExchange(ref isRunning, 1, 0) != 0)
        {
            throw new InvalidOperationException("The recognizer is already running.");
        }

        stopRequested = false;
        cancelRequested = false;
    }

    private void EndRun()
    {
        Volatile.Write(ref isRunning, 0);
    }

    private void RunAsyncWorker(RecognizeMode mode, SynchronizationContext? context)
    {
        workerThreadId = Environment.CurrentManagedThreadId;
        RunOutcome outcome;
        Exception? error = null;
        try
        {
            outcome = RunRecognition(mode, InitialSilenceTimeout, context);
        }
        catch (Exception exception)
        {
            error = exception;
            outcome = default;
        }

        bool cancelled = cancelRequested && error is null;
        TimeSpan position = AudioPosition;
        EndRun();
        Raise(context, () => RecognizeCompleted?.Invoke(this, new RecognizeCompletedEventArgs(
            outcome.Result, outcome.InitialSilence, outcome.Babble, outcome.InputEnded, position, error, cancelled)));

        if (isDisposed)
        {
            ReleaseAudioResources();
        }

        workerThreadId = 0;
    }

    private RunOutcome RunRecognition(RecognizeMode mode, TimeSpan initialSilence, SynchronizationContext? context)
    {
        AudioInput audio = input!;
        audio.Start();
        try
        {
            return RunLoop(audio, mode, initialSilence, context);
        }
        finally
        {
            audio.Stop();
            SetAudioState(AudioState.Stopped, context);
        }
    }

    private RunOutcome RunLoop(AudioInput audio, RecognizeMode mode, TimeSpan initialSilence, SynchronizationContext? context)
    {
        bool applyTimeouts = mode == RecognizeMode.Single;
        TimeSpan start = AudioPosition;
        runStartPosition = start;
        runStartTime = DateTime.Now;
        utteranceAudio.SetLength(0);
        utteranceAudioStart = start;

        bool speechDetected = false;
        bool utteranceActive = false;
        TimeSpan utteranceStart = TimeSpan.Zero;
        string lastPartial = string.Empty;
        SetAudioState(AudioState.Silence, context);

        while (!stopRequested)
        {
            RaiseDueUpdates(context);

            if (grammarsChanged)
            {
                if (recognizer is not null)
                {
                    RecognitionResult? flushed = ProcessFinal(recognizer.FinalResult(), audio, context, out bool hadSpeech);
                    if (hadSpeech && mode == RecognizeMode.Single)
                    {
                        return new RunOutcome(flushed, false, false, false);
                    }
                }

                RebuildRecognizer(audio);
                utteranceActive = false;
                lastPartial = string.Empty;
            }

            int count = audio.Read();
            if (count == AudioInput.NoData)
            {
                continue;
            }

            if (count == AudioInput.EndOfStream)
            {
                RecognitionResult? last = ProcessFinal(recognizer!.FinalResult(), audio, context, out _);
                return new RunOutcome(last, false, false, true);
            }

            utteranceAudio.Write(audio.Buffer, 0, count);
            Interlocked.Add(ref audioBytes, count);
            UpdateAudioLevel(audio.Buffer, count, context);

            if (recognizer!.AcceptWaveform(audio.Buffer, count))
            {
                RecognitionResult? result = ProcessFinal(recognizer.Result(), audio, context, out bool hadSpeech);
                utteranceActive = false;
                lastPartial = string.Empty;
                SetAudioState(AudioState.Silence, context);

                if (hadSpeech && mode == RecognizeMode.Single)
                {
                    return new RunOutcome(result, false, false, false);
                }
            }
            else
            {
                string partial = VoskResultParser.ParsePartial(recognizer.PartialResult());
                if (partial.Length > 0)
                {
                    if (!utteranceActive)
                    {
                        utteranceActive = true;
                        speechDetected = true;
                        utteranceStart = AudioPosition;
                        SetAudioState(AudioState.Speech, context);
                        TimeSpan detectedAt = AudioPosition;
                        Raise(context, () => SpeechDetected?.Invoke(this, new SpeechDetectedEventArgs(detectedAt)));
                    }

                    if (partial != lastPartial)
                    {
                        lastPartial = partial;
                        RecognitionResult hypothesis = BuildHypothesis(partial);
                        Raise(context, () => SpeechHypothesized?.Invoke(this, new SpeechHypothesizedEventArgs(hypothesis)));
                    }
                }
            }

            if (applyTimeouts)
            {
                if (!speechDetected && initialSilence > TimeSpan.Zero && AudioPosition - start >= initialSilence)
                {
                    return new RunOutcome(null, true, false, false);
                }

                if (utteranceActive && BabbleTimeout > TimeSpan.Zero && AudioPosition - utteranceStart >= BabbleTimeout)
                {
                    return new RunOutcome(null, false, true, false);
                }
            }
        }

        if (cancelRequested || recognizer is null)
        {
            return default;
        }

        return new RunOutcome(ProcessFinal(recognizer.FinalResult(), audio, context, out _), false, false, false);
    }

    private void RebuildRecognizer(AudioInput audio)
    {
        grammarsChanged = false;
        recognizer?.Dispose();

        Model model = ModelCatalog.GetModel(RecognizerInfo.ModelPath);
        string? grammarJson = BuildVoskGrammar(model);
        recognizer = grammarJson is null
            ? new VoskRecognizer(model, audio.SampleRate)
            : new VoskRecognizer(model, audio.SampleRate, grammarJson);
        recognizer.SetWords(true);
        if (MaxAlternates > 1)
        {
            recognizer.SetMaxAlternatives(MaxAlternates);
        }

        recognizerEpoch = AudioPosition;
        utteranceAudio.SetLength(0);
        utteranceAudioStart = recognizerEpoch;
    }

    /// <summary>Builds the Vosk vocabulary restriction, or returns null when free-form recognition is needed.</summary>
    private string? BuildVoskGrammar(Model model)
    {
        Grammar[] enabled = EnabledGrammars();
        if (enabled.Any(grammar => GrammarNodeExpander.ContainsFreeText(grammar.Root)))
        {
            return null;
        }

        List<string> entries = new() { VoskResultParser.UnknownWord };
        List<string[]>? phrases = new();
        foreach (Grammar grammar in enabled)
        {
            IReadOnlyList<string[]>? expanded = GrammarNodeExpander.Expand(grammar.Root, MaxExpandedPhrases);
            if (expanded is null || phrases.Count + expanded.Count > MaxExpandedPhrases)
            {
                phrases = null;
                break;
            }

            phrases.AddRange(expanded);
        }

        if (phrases is not null)
        {
            // A phrase with a word the model doesn't know can never be recognized, and would otherwise
            // be silently shortened by Vosk into a different phrase.
            entries.AddRange(phrases
                .Where(phrase => phrase.Length > 0 && phrase.All(word => model.FindWord(word.ToLowerInvariant()) >= 0))
                .Select(phrase => string.Join(" ", phrase).ToLowerInvariant())
                .Distinct());
        }
        else
        {
            HashSet<string> words = new(StringComparer.OrdinalIgnoreCase);
            foreach (Grammar grammar in enabled)
            {
                GrammarNodeExpander.CollectWords(grammar.Root, words);
            }

            entries.AddRange(words.Select(word => word.ToLowerInvariant()).Where(word => model.FindWord(word) >= 0).Distinct());
        }

        return JsonSerializer.Serialize(entries);
    }

    private Grammar[] EnabledGrammars()
    {
        lock (syncRoot)
        {
            return grammars.Where(grammar => grammar.Enabled).OrderByDescending(grammar => grammar.Priority).ToArray();
        }
    }

    private RecognitionResult? ProcessFinal(string json, AudioInput audio, SynchronizationContext? context, out bool hadSpeech)
    {
        List<VoskHypothesis> hypotheses = VoskResultParser.ParseFinal(json);
        hadSpeech = hypotheses.Count > 0;
        if (!hadSpeech)
        {
            ResetUtteranceAudio();
            return null;
        }

        List<(VoskHypothesis Hypothesis, Grammar? Grammar, Fragment? Fragment)> matches = hypotheses
            .Select(MatchHypothesis)
            .ToList();

        var best = matches.FirstOrDefault(match => match.Grammar is not null);
        int threshold;
        lock (syncRoot)
        {
            threshold = settings["CFGConfidenceRejectionThreshold"];
        }

        RecognizedAudio? recognizedAudio = TakeUtteranceAudio(best.Grammar is null ? hypotheses[0] : best.Hypothesis, audio);
        if (best.Grammar is null || best.Hypothesis.Confidence * 100.0f < threshold)
        {
            RecognitionResult rejected = BuildResult(hypotheses[0], null, null, recognizedAudio, []);
            Raise(context, () => SpeechRecognitionRejected?.Invoke(this, new SpeechRecognitionRejectedEventArgs(rejected)));
            return null;
        }

        IEnumerable<RecognizedPhrase> others = matches
            .Where(match => match.Grammar is not null && !ReferenceEquals(match.Hypothesis, best.Hypothesis))
            .Select(match => BuildPhrase(match.Hypothesis, match.Grammar, match.Fragment));
        RecognitionResult result = BuildResult(best.Hypothesis, best.Grammar, best.Fragment, recognizedAudio, others);
        RaiseRecognized(result, context);
        return result;
    }

    private (VoskHypothesis Hypothesis, Grammar? Grammar, Fragment? Fragment) MatchHypothesis(VoskHypothesis hypothesis)
    {
        (Grammar? grammar, Fragment? fragment) = MatchGrammars(hypothesis.Words.Select(word => word.Text).ToArray(), CompareOptions.IgnoreCase);
        if (grammar is not null)
        {
            return (hypothesis, grammar, fragment);
        }

        VoskHypothesis? trimmed = VoskResultParser.TrimUnknownWords(hypothesis);
        if (trimmed is not null)
        {
            (grammar, fragment) = MatchGrammars(trimmed.Words.Select(word => word.Text).ToArray(), CompareOptions.IgnoreCase);
            if (grammar is not null)
            {
                return (trimmed, grammar, fragment);
            }
        }

        return (hypothesis, null, null);
    }

    private (Grammar? Grammar, Fragment? Fragment) MatchGrammars(string[] tokens, CompareOptions options)
    {
        GrammarMatcher matcher = new(tokens, options);
        foreach (Grammar grammar in EnabledGrammars())
        {
            Fragment? fragment = matcher.MatchAll(grammar.Root);
            if (fragment is not null)
            {
                return (grammar, fragment);
            }
        }

        return (null, null);
    }

    private static RecognizedWordUnit[] BuildWords(VoskHypothesis hypothesis, Fragment? fragment)
    {
        return hypothesis.Words.Select((word, index) =>
        {
            // Matched text uses the grammar's own spelling, as SAPI does.
            string text = fragment is not null && index < fragment.Words.Length ? fragment.Words[index] : word.Text;
            return new RecognizedWordUnit(
                text,
                word.Confidence,
                string.Empty,
                word.Text,
                DisplayAttributes.OneTrailingSpace,
                TimeSpan.FromSeconds(word.Start),
                TimeSpan.FromSeconds(Math.Max(0.0, word.End - word.Start)));
        }).ToArray();
    }

    private static SemanticValue BuildSemantics(Fragment? fragment, float confidence)
    {
        return fragment?.ToSemanticValue(confidence) ?? new SemanticValue(string.Empty, null, confidence);
    }

    private static RecognizedPhrase BuildPhrase(VoskHypothesis hypothesis, Grammar? grammar, Fragment? fragment)
    {
        return new RecognizedPhrase(BuildWords(hypothesis, fragment), hypothesis.Confidence, BuildSemantics(fragment, hypothesis.Confidence), grammar);
    }

    private static RecognitionResult BuildResult(
        VoskHypothesis hypothesis,
        Grammar? grammar,
        Fragment? fragment,
        RecognizedAudio? audio,
        IEnumerable<RecognizedPhrase> others)
    {
        return new RecognitionResult(
            BuildWords(hypothesis, fragment),
            hypothesis.Confidence,
            BuildSemantics(fragment, hypothesis.Confidence),
            grammar,
            audio,
            others);
    }

    private static RecognitionResult BuildHypothesis(string partial)
    {
        VoskWord[] words = GrammarNode.Tokenize(partial).Select(word => new VoskWord(word, 0.0f, 0.0, 0.0)).ToArray();
        return BuildResult(new VoskHypothesis(words, 0.0f), null, null, null, []);
    }

    private RecognizedAudio? TakeUtteranceAudio(VoskHypothesis hypothesis, AudioInput audio)
    {
        VoskWord first = hypothesis.Words[0];
        VoskWord last = hypothesis.Words[^1];
        byte[] pcm = utteranceAudio.ToArray();
        TimeSpan bufferStart = utteranceAudioStart;
        ResetUtteranceAudio();

        TimeSpan speechStart = recognizerEpoch + TimeSpan.FromSeconds(first.Start);
        int startByte = Math.Clamp(TimeToByteOffset(speechStart - bufferStart), 0, pcm.Length);
        int endByte = Math.Clamp(TimeToByteOffset(recognizerEpoch + TimeSpan.FromSeconds(last.End) - bufferStart), startByte, pcm.Length);

        if (endByte == startByte)
        {
            return null;
        }

        SpeechAudioFormatInfo format = new(audio.SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono);
        return new RecognizedAudio(pcm[startByte..endByte], format, runStartTime + (speechStart - runStartPosition), speechStart);

        int TimeToByteOffset(TimeSpan time)
        {
            int bytes = (int)(time.TotalSeconds * audio.SampleRate) * 2;
            return bytes;
        }
    }

    private void ResetUtteranceAudio()
    {
        utteranceAudio.SetLength(0);
        utteranceAudioStart = AudioPosition;
    }

    private void UpdateAudioLevel(byte[] buffer, int count, SynchronizationContext? context)
    {
        double sum = 0.0;
        int samples = count / 2;
        for (int i = 0; i < samples; i++)
        {
            short sample = BitConverter.ToInt16(buffer, i * 2);
            sum += sample * (double)sample;
        }

        double rms = samples == 0 ? 0.0 : Math.Sqrt(sum / samples);
        int level = (int)Math.Clamp(100.0 * Math.Sqrt(rms / short.MaxValue), 0.0, 100.0);
        if (level != audioLevel)
        {
            audioLevel = level;
            Raise(context, () => AudioLevelUpdated?.Invoke(this, new AudioLevelUpdatedEventArgs(level)));
        }
    }

    private void SetAudioState(AudioState state, SynchronizationContext? context)
    {
        if (audioState != state)
        {
            audioState = state;
            Raise(context, () => AudioStateChanged?.Invoke(this, new AudioStateChangedEventArgs(state)));
        }
    }

    private void RaiseDueUpdates(SynchronizationContext? context)
    {
        List<object?> due = new();
        TimeSpan position = AudioPosition;
        lock (syncRoot)
        {
            while (updateRequests.Count > 0 && updateRequests.Peek().At <= position)
            {
                due.Add(updateRequests.Dequeue().Token);
            }
        }

        foreach (object? token in due)
        {
            Raise(context, () => RecognizerUpdateReached?.Invoke(this, new RecognizerUpdateReachedEventArgs(token, position)));
        }
    }

    private void RaiseRecognized(RecognitionResult result, SynchronizationContext? context)
    {
        SpeechRecognizedEventArgs args = new(result);
        Raise(context, () =>
        {
            result.Grammar?.RaiseSpeechRecognized(args);
            SpeechRecognized?.Invoke(this, args);
        });
    }

    private static void Raise(SynchronizationContext? context, Action action)
    {
        if (context is null)
        {
            action();
        }
        else
        {
            context.Post(_ => action(), null);
        }
    }

    private TimeSpan BytesToTime(long bytes)
    {
        int rate = input?.SampleRate ?? 16000;
        return TimeSpan.FromSeconds(bytes / (2.0 * rate));
    }

    private static string[] TokenizeEmulated(string inputText)
    {
        ArgumentNullException.ThrowIfNull(inputText);
        string[] tokens = GrammarNode.Tokenize(inputText);
        return tokens.Length > 0 ? tokens : throw new ArgumentException("The input must contain text.", nameof(inputText));
    }

    private static string[] TokensFromUnits(RecognizedWordUnit[] wordUnits)
    {
        ArgumentNullException.ThrowIfNull(wordUnits);
        return wordUnits.Length > 0
            ? wordUnits.Select(unit => unit.Text).ToArray()
            : throw new ArgumentException("At least one word is required.", nameof(wordUnits));
    }

    private void StartEmulateAsync(string[] tokens, CompareOptions compareOptions)
    {
        SynchronizationContext? context = SynchronizationContext.Current;
        Task.Run(() =>
        {
            RecognitionResult? result = null;
            Exception? error = null;
            try
            {
                result = EmulateCore(tokens, compareOptions, context);
            }
            catch (Exception exception)
            {
                error = exception;
            }

            Raise(context, () => EmulateRecognizeCompleted?.Invoke(this, new EmulateRecognizeCompletedEventArgs(result, error, false, null)));
        });
    }

    private RecognitionResult? EmulateCore(string[] tokens, CompareOptions compareOptions, SynchronizationContext? context)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        if ((compareOptions & ~DefaultEmulateOptions) != 0)
        {
            throw new NotSupportedException("Only IgnoreCase, IgnoreKanaType and IgnoreWidth are supported.");
        }

        lock (syncRoot)
        {
            if (grammars.Count == 0)
            {
                throw new InvalidOperationException("At least one grammar must be loaded.");
            }
        }

        Raise(context, () => SpeechDetected?.Invoke(this, new SpeechDetectedEventArgs(AudioPosition)));

        VoskHypothesis hypothesis = new(tokens.Select(token => new VoskWord(token, 1.0f, 0.0, 0.0)).ToArray(), 1.0f);
        (Grammar? grammar, Fragment? fragment) = MatchGrammars(tokens, compareOptions);
        if (grammar is null)
        {
            RecognitionResult rejected = BuildResult(hypothesis, null, null, null, []);
            Raise(context, () => SpeechRecognitionRejected?.Invoke(this, new SpeechRecognitionRejectedEventArgs(rejected)));
            return null;
        }

        RecognitionResult result = BuildResult(hypothesis, grammar, fragment, null, []);
        RaiseRecognized(result, context);
        return result;
    }

    private readonly record struct RunOutcome(RecognitionResult? Result, bool InitialSilence, bool Babble, bool InputEnded);
}
