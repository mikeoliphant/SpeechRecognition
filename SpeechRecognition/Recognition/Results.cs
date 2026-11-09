using System.Buffers.Binary;
using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Speech.AudioFormat;
using System.Xml;
using System.Xml.XPath;

namespace System.Speech.Recognition;

public class RecognizedWordUnit
{
    public RecognizedWordUnit(
        string? text,
        float confidence,
        string? pronunciation,
        string? lexicalForm,
        DisplayAttributes displayAttributes,
        TimeSpan audioPosition,
        TimeSpan audioDuration)
    {
        if (lexicalForm is null)
        {
            throw new ArgumentNullException(nameof(lexicalForm));
        }

        if (confidence < 0.0f || confidence > 1.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence));
        }

        Text = string.IsNullOrEmpty(text) ? lexicalForm : text;
        Confidence = confidence;
        Pronunciation = pronunciation ?? string.Empty;
        LexicalForm = lexicalForm;
        DisplayAttributes = displayAttributes;
        AudioPosition = audioPosition;
        AudioDuration = audioDuration;
    }

    public string Text { get; }

    public float Confidence { get; }

    public string Pronunciation { get; }

    public string LexicalForm { get; }

    public DisplayAttributes DisplayAttributes { get; }

    internal TimeSpan AudioPosition { get; }

    internal TimeSpan AudioDuration { get; }
}

public class ReplacementText
{
    internal ReplacementText(DisplayAttributes displayAttributes, string text, int firstWordIndex, int countOfWords)
    {
        DisplayAttributes = displayAttributes;
        Text = text;
        FirstWordIndex = firstWordIndex;
        CountOfWords = countOfWords;
    }

    public DisplayAttributes DisplayAttributes { get; }

    public string Text { get; }

    public int FirstWordIndex { get; }

    public int CountOfWords { get; }
}

public class SemanticValue : IDictionary<string, SemanticValue>
{
    private readonly Dictionary<string, SemanticValue> children = new();

    public SemanticValue(string keyName, object? value, float confidence)
    {
        ArgumentNullException.ThrowIfNull(keyName);
        KeyName = keyName;
        Value = value;
        Confidence = confidence;
    }

    public SemanticValue(object? value)
        : this(string.Empty, value, -1.0f)
    {
    }

    internal string KeyName { get; }

    public object? Value { get; internal set; }

    public float Confidence { get; }

    public int Count => children.Count;

    public SemanticValue this[string key]
    {
        get => children[key];
        set => throw new InvalidOperationException("SemanticValue is read-only.");
    }

    bool ICollection<KeyValuePair<string, SemanticValue>>.IsReadOnly => true;

    ICollection<string> IDictionary<string, SemanticValue>.Keys => children.Keys;

    ICollection<SemanticValue> IDictionary<string, SemanticValue>.Values => children.Values;

    internal void SetChild(string key, SemanticValue value)
    {
        children[key] = value;
    }

    public bool Contains(KeyValuePair<string, SemanticValue> item)
    {
        return children.TryGetValue(item.Key, out SemanticValue? value) && Equals(value, item.Value);
    }

    public bool ContainsKey(string key)
    {
        return children.ContainsKey(key);
    }

    public override bool Equals(object? obj)
    {
        return obj is SemanticValue other
            && Equals(Value, other.Value)
            && Count == other.Count
            && children.All(pair => other.children.TryGetValue(pair.Key, out SemanticValue? value) && pair.Value.Equals(value));
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Value, Count);
    }

    bool IDictionary<string, SemanticValue>.TryGetValue(string key, out SemanticValue value)
    {
        return children.TryGetValue(key, out value!);
    }

    void IDictionary<string, SemanticValue>.Add(string key, SemanticValue value)
    {
        throw new NotSupportedException();
    }

    bool IDictionary<string, SemanticValue>.Remove(string key)
    {
        throw new NotSupportedException();
    }

    void ICollection<KeyValuePair<string, SemanticValue>>.Add(KeyValuePair<string, SemanticValue> item)
    {
        throw new NotSupportedException();
    }

    void ICollection<KeyValuePair<string, SemanticValue>>.Clear()
    {
        throw new NotSupportedException();
    }

    bool ICollection<KeyValuePair<string, SemanticValue>>.Remove(KeyValuePair<string, SemanticValue> item)
    {
        throw new NotSupportedException();
    }

    void ICollection<KeyValuePair<string, SemanticValue>>.CopyTo(KeyValuePair<string, SemanticValue>[] array, int arrayIndex)
    {
        ((ICollection<KeyValuePair<string, SemanticValue>>)children).CopyTo(array, arrayIndex);
    }

    IEnumerator<KeyValuePair<string, SemanticValue>> IEnumerable<KeyValuePair<string, SemanticValue>>.GetEnumerator()
    {
        return children.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return children.GetEnumerator();
    }
}

public class RecognizedPhrase
{
    internal RecognizedPhrase(IReadOnlyList<RecognizedWordUnit> words, float confidence, SemanticValue semantics, Grammar? grammar)
    {
        Words = new ReadOnlyCollection<RecognizedWordUnit>(words.ToList());
        Text = string.Join(" ", words.Select(word => word.Text));
        Confidence = confidence;
        Semantics = semantics;
        Grammar = grammar;
    }

    public string Text { get; }

    public float Confidence { get; }

    public ReadOnlyCollection<RecognizedWordUnit> Words { get; }

    public SemanticValue Semantics { get; }

    public ReadOnlyCollection<RecognizedPhrase> Homophones { get; } = new(new List<RecognizedPhrase>());

    public Grammar? Grammar { get; }

    public Collection<ReplacementText> ReplacementWordUnits { get; } = new();

    public int HomophoneGroupId => 0;

    public IXPathNavigable ConstructSmlFromSemantics()
    {
        XmlDocument document = new();
        XmlElement root = document.CreateElement("SML");
        root.SetAttribute("text", Text);
        root.SetAttribute("confidence", Confidence.ToString("0.000", CultureInfo.InvariantCulture));
        document.AppendChild(root);
        AppendSemantics(document, root, Semantics);
        return document;
    }

    private static void AppendSemantics(XmlDocument document, XmlElement parent, SemanticValue value)
    {
        if (value.Value is not null)
        {
            parent.AppendChild(document.CreateTextNode(Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? string.Empty));
        }

        foreach (KeyValuePair<string, SemanticValue> pair in value)
        {
            XmlElement child = document.CreateElement(XmlConvert.EncodeLocalName(pair.Key));
            parent.AppendChild(child);
            AppendSemantics(document, child, pair.Value);
        }
    }
}

public class RecognitionResult : RecognizedPhrase
{
    private readonly List<RecognizedPhrase> alternates = new();

    internal RecognitionResult(
        IReadOnlyList<RecognizedWordUnit> words,
        float confidence,
        SemanticValue semantics,
        Grammar? grammar,
        RecognizedAudio? audio,
        IEnumerable<RecognizedPhrase> otherAlternates)
        : base(words, confidence, semantics, grammar)
    {
        Audio = audio;
        alternates.Add(this);
        alternates.AddRange(otherAlternates);
        Alternates = new ReadOnlyCollection<RecognizedPhrase>(alternates);
    }

    public RecognizedAudio? Audio { get; }

    public ReadOnlyCollection<RecognizedPhrase> Alternates { get; }

    public RecognizedAudio? GetAudioForWordRange(RecognizedWordUnit firstWord, RecognizedWordUnit lastWord)
    {
        ArgumentNullException.ThrowIfNull(firstWord);
        ArgumentNullException.ThrowIfNull(lastWord);

        return Audio?.GetRange(
            firstWord.AudioPosition - Words[0].AudioPosition,
            lastWord.AudioPosition + lastWord.AudioDuration - firstWord.AudioPosition);
    }
}

public class RecognizedAudio
{
    private readonly byte[] audio;

    internal RecognizedAudio(byte[] audio, SpeechAudioFormatInfo format, DateTime startTime, TimeSpan audioPosition)
    {
        this.audio = audio;
        Format = format;
        StartTime = startTime;
        AudioPosition = audioPosition;
        Duration = TimeSpan.FromSeconds(audio.Length / (double)format.AverageBytesPerSecond);
    }

    public SpeechAudioFormatInfo Format { get; }

    public DateTime StartTime { get; }

    public TimeSpan AudioPosition { get; }

    public TimeSpan Duration { get; }

    public void WriteToAudioStream(Stream outputStream)
    {
        ArgumentNullException.ThrowIfNull(outputStream);
        outputStream.Write(audio, 0, audio.Length);
    }

    public void WriteToWaveStream(Stream outputStream)
    {
        ArgumentNullException.ThrowIfNull(outputStream);

        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + audio.Length);
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], (short)Format.EncodingFormat);
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], (short)Format.ChannelCount);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], Format.SamplesPerSecond);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], Format.AverageBytesPerSecond);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], (short)Format.BlockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], (short)Format.BitsPerSample);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], audio.Length);
        outputStream.Write(header);
        outputStream.Write(audio, 0, audio.Length);
    }

    public RecognizedAudio GetRange(TimeSpan audioPosition, TimeSpan duration)
    {
        if (audioPosition < TimeSpan.Zero || audioPosition > Duration)
        {
            throw new ArgumentOutOfRangeException(nameof(audioPosition));
        }

        if (duration < TimeSpan.Zero || audioPosition + duration > Duration + TimeSpan.FromMilliseconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        int start = ToAlignedOffset(audioPosition);
        int end = Math.Min(audio.Length, start + ToAlignedOffset(duration));
        return new RecognizedAudio(audio[start..end], Format, StartTime + audioPosition, AudioPosition + audioPosition);
    }

    private int ToAlignedOffset(TimeSpan time)
    {
        int bytes = (int)(time.TotalSeconds * Format.AverageBytesPerSecond);
        return bytes - (bytes % Format.BlockAlign);
    }
}
