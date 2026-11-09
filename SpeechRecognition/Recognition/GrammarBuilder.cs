using System.Globalization;
using System.Speech.Internal;

namespace System.Speech.Recognition;

public class Choices
{
    private readonly List<GrammarNode> alternatives = new();

    public Choices()
    {
    }

    public Choices(params string[] phrases)
    {
        Add(phrases);
    }

    public Choices(params GrammarBuilder[] alternateChoices)
    {
        Add(alternateChoices);
    }

    public void Add(params string[] phrases)
    {
        ArgumentNullException.ThrowIfNull(phrases);

        foreach (string phrase in phrases)
        {
            alternatives.Add(GrammarNode.FromPhrase(phrase, nameof(phrases)));
        }
    }

    public void Add(params GrammarBuilder[] alternateChoices)
    {
        ArgumentNullException.ThrowIfNull(alternateChoices);

        foreach (GrammarBuilder choice in alternateChoices)
        {
            ArgumentNullException.ThrowIfNull(choice, nameof(alternateChoices));
            alternatives.Add(choice.Build());
        }
    }

    public GrammarBuilder ToGrammarBuilder()
    {
        return new GrammarBuilder(this);
    }

    internal GrammarNode Build()
    {
        return new AlternativesNode(alternatives.ToArray());
    }
}

public class SemanticResultKey
{
    private readonly GrammarNode node;

    public SemanticResultKey(string semanticResultKey, params string[] phrases)
    {
        ArgumentException.ThrowIfNullOrEmpty(semanticResultKey);
        ArgumentNullException.ThrowIfNull(phrases);

        node = new KeyNode(
            semanticResultKey,
            new SequenceNode(phrases.Select(phrase => GrammarNode.FromPhrase(phrase, nameof(phrases))).ToArray()));
    }

    public SemanticResultKey(string semanticResultKey, params GrammarBuilder[] builders)
    {
        ArgumentException.ThrowIfNullOrEmpty(semanticResultKey);
        ArgumentNullException.ThrowIfNull(builders);

        node = new KeyNode(semanticResultKey, new SequenceNode(builders.Select(builder => builder.Build()).ToArray()));
    }

    public GrammarBuilder ToGrammarBuilder()
    {
        return new GrammarBuilder(this);
    }

    internal GrammarNode Build()
    {
        return node;
    }
}

public class SemanticResultValue
{
    private readonly GrammarNode node;

    public SemanticResultValue(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        node = new ValueNode(value, null);
    }

    public SemanticResultValue(string phrase, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        node = new ValueNode(value, GrammarNode.FromPhrase(phrase, nameof(phrase)));
    }

    public SemanticResultValue(GrammarBuilder builder, object value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(value);
        node = new ValueNode(value, builder.Build());
    }

    public GrammarBuilder ToGrammarBuilder()
    {
        return new GrammarBuilder(this);
    }

    internal GrammarNode Build()
    {
        return node;
    }
}

public class GrammarBuilder
{
    private readonly List<GrammarNode> parts = new();
    private CultureInfo culture = CultureInfo.CurrentUICulture;

    public GrammarBuilder()
    {
    }

    public GrammarBuilder(string phrase)
    {
        Append(phrase);
    }

    public GrammarBuilder(string phrase, SubsetMatchingMode subsetMatchingCriteria)
    {
        Append(phrase, subsetMatchingCriteria);
    }

    public GrammarBuilder(string phrase, int minRepeat, int maxRepeat)
    {
        Append(phrase, minRepeat, maxRepeat);
    }

    public GrammarBuilder(GrammarBuilder builder, int minRepeat, int maxRepeat)
    {
        Append(builder, minRepeat, maxRepeat);
    }

    public GrammarBuilder(Choices alternateChoices)
    {
        Append(alternateChoices);
    }

    public GrammarBuilder(SemanticResultKey key)
    {
        Append(key);
    }

    public GrammarBuilder(SemanticResultValue value)
    {
        Append(value);
    }

    public CultureInfo Culture
    {
        get => culture;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            culture = value;
        }
    }

    public string DebugShowPhrases
    {
        get
        {
            IReadOnlyList<string[]>? phrases = GrammarNodeExpander.Expand(Build(), 100);
            return phrases is null
                ? "(too many phrases, or contains dictation)"
                : string.Join(Environment.NewLine, phrases.Select(phrase => string.Join(" ", phrase)));
        }
    }

    public void Append(string phrase)
    {
        parts.Add(GrammarNode.FromPhrase(phrase, nameof(phrase)));
    }

    public void Append(string phrase, SubsetMatchingMode subsetMatchingCriteria)
    {
        if (!Enum.IsDefined(subsetMatchingCriteria))
        {
            throw new ArgumentOutOfRangeException(nameof(subsetMatchingCriteria));
        }

        parts.Add(new SubsetNode(GrammarNode.SplitPhrase(phrase, nameof(phrase)), subsetMatchingCriteria));
    }

    public void Append(string phrase, int minRepeat, int maxRepeat)
    {
        ValidateRepeat(minRepeat, maxRepeat);
        parts.Add(new RepeatNode(GrammarNode.FromPhrase(phrase, nameof(phrase)), minRepeat, maxRepeat));
    }

    public void Append(GrammarBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        parts.Add(builder.Build());
    }

    public void Append(GrammarBuilder builder, int minRepeat, int maxRepeat)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ValidateRepeat(minRepeat, maxRepeat);
        parts.Add(new RepeatNode(builder.Build(), minRepeat, maxRepeat));
    }

    public void Append(Choices alternateChoices)
    {
        ArgumentNullException.ThrowIfNull(alternateChoices);
        parts.Add(alternateChoices.Build());
    }

    public void Append(SemanticResultKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        parts.Add(key.Build());
    }

    public void Append(SemanticResultValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        parts.Add(value.Build());
    }

    public void AppendDictation()
    {
        parts.Add(new FreeTextNode());
    }

    public void AppendDictation(string category)
    {
        ArgumentException.ThrowIfNullOrEmpty(category);
        parts.Add(new FreeTextNode());
    }

    public void AppendWildcard()
    {
        parts.Add(new FreeTextNode());
    }

    public void AppendRuleReference(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        parts.Add(SrgsLoader.Load(path, null));
    }

    public void AppendRuleReference(string path, string rule)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(rule);
        parts.Add(SrgsLoader.Load(path, rule));
    }

    public static GrammarBuilder operator +(string phrase, GrammarBuilder builder)
    {
        return Add(phrase, builder);
    }

    public static GrammarBuilder Add(string phrase, GrammarBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        GrammarBuilder result = new(phrase);
        result.Append(builder);
        return result;
    }

    public static GrammarBuilder operator +(GrammarBuilder builder, string phrase)
    {
        return Add(builder, phrase);
    }

    public static GrammarBuilder Add(GrammarBuilder builder, string phrase)
    {
        ArgumentNullException.ThrowIfNull(builder);
        GrammarBuilder result = builder.Clone();
        result.Append(phrase);
        return result;
    }

    public static GrammarBuilder operator +(Choices choices, GrammarBuilder builder)
    {
        return Add(choices, builder);
    }

    public static GrammarBuilder Add(Choices choices, GrammarBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(builder);
        GrammarBuilder result = new(choices);
        result.Append(builder);
        return result;
    }

    public static GrammarBuilder operator +(GrammarBuilder builder, Choices choices)
    {
        return Add(builder, choices);
    }

    public static GrammarBuilder Add(GrammarBuilder builder, Choices choices)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(choices);
        GrammarBuilder result = builder.Clone();
        result.Append(choices);
        return result;
    }

    public static GrammarBuilder operator +(GrammarBuilder builder1, GrammarBuilder builder2)
    {
        return Add(builder1, builder2);
    }

    public static GrammarBuilder Add(GrammarBuilder builder1, GrammarBuilder builder2)
    {
        ArgumentNullException.ThrowIfNull(builder1);
        ArgumentNullException.ThrowIfNull(builder2);
        GrammarBuilder result = builder1.Clone();
        result.Append(builder2);
        return result;
    }

    public static implicit operator GrammarBuilder(string phrase)
    {
        return new GrammarBuilder(phrase);
    }

    public static implicit operator GrammarBuilder(Choices choices)
    {
        return new GrammarBuilder(choices);
    }

    public static implicit operator GrammarBuilder(SemanticResultKey semanticKey)
    {
        return new GrammarBuilder(semanticKey);
    }

    public static implicit operator GrammarBuilder(SemanticResultValue semanticValue)
    {
        return new GrammarBuilder(semanticValue);
    }

    internal GrammarNode Build()
    {
        return new SequenceNode(parts.ToArray());
    }

    private GrammarBuilder Clone()
    {
        GrammarBuilder clone = new() { culture = culture };
        clone.parts.AddRange(parts);
        return clone;
    }

    private static void ValidateRepeat(int minRepeat, int maxRepeat)
    {
        if (minRepeat < 0 || minRepeat > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(minRepeat));
        }

        if (maxRepeat < minRepeat || maxRepeat > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRepeat));
        }
    }
}
