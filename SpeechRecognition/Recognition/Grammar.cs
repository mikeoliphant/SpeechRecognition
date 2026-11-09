using System.Speech.Internal;

namespace System.Speech.Recognition;

public class Grammar
{
    private bool enabled = true;
    private float weight = 1.0f;
    private int priority;
    private string name = string.Empty;

    protected Grammar()
    {
        Root = new SequenceNode([]);
    }

    public Grammar(string path)
        : this(path, null!)
    {
    }

    public Grammar(string path, string ruleName)
        : this(path, ruleName, null!)
    {
    }

    public Grammar(string path, string ruleName, object[] parameters)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ThrowIfParameters(parameters);
        Root = SrgsLoader.Load(path, ruleName);
        RuleName = ruleName;
    }

    public Grammar(Stream stream)
        : this(stream, null!)
    {
    }

    public Grammar(Stream stream, string ruleName)
        : this(stream, ruleName, (object[])null!)
    {
    }

    public Grammar(Stream stream, string ruleName, object[] parameters)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ThrowIfParameters(parameters);
        Root = SrgsLoader.Load(stream, ruleName);
        RuleName = ruleName;
    }

    public Grammar(Stream stream, string ruleName, Uri baseUri)
        : this(stream, ruleName, baseUri, null!)
    {
    }

    public Grammar(Stream stream, string ruleName, Uri baseUri, object[] parameters)
        : this(stream, ruleName, parameters)
    {
    }

    public Grammar(GrammarBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        Root = builder.Build();
    }

    public event EventHandler<SpeechRecognizedEventArgs>? SpeechRecognized;

    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled != value)
            {
                enabled = value;
                Changed?.Invoke();
            }
        }
    }

    public float Weight
    {
        get => weight;
        set
        {
            if (value < 0.0f || value > 1.0f)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            weight = value;
        }
    }

    public int Priority
    {
        get => priority;
        set
        {
            if (value < -128 || value > 127)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            priority = value;
        }
    }

    public string Name
    {
        get => name;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            name = value;
        }
    }

    public string? RuleName { get; }

    public bool Loaded { get; internal set; }

    internal GrammarNode Root { get; init; }

    internal Action? Changed { get; set; }

    public static Grammar LoadLocalizedGrammarFromType(Type type, params object[] onInitParameters)
    {
        throw new NotSupportedException("Grammars compiled into assemblies are not supported by the Vosk backend.");
    }

    internal void RaiseSpeechRecognized(SpeechRecognizedEventArgs args)
    {
        SpeechRecognized?.Invoke(this, args);
    }

    private static void ThrowIfParameters(object[]? parameters)
    {
        if (parameters is { Length: > 0 })
        {
            throw new NotSupportedException("Grammar initialization parameters are not supported by the Vosk backend.");
        }
    }
}

public class DictationGrammar : Grammar
{
    public DictationGrammar()
    {
        Root = new FreeTextNode();
    }

    public DictationGrammar(string topic)
        : this()
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
    }

    public void SetDictationContext(string? precedingText, string? subsequentText)
    {
        if (precedingText is null && subsequentText is not null)
        {
            throw new ArgumentNullException(nameof(precedingText));
        }
    }
}
