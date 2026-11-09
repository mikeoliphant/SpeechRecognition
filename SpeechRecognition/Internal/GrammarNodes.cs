using System.Globalization;
using System.Speech.Recognition;

namespace System.Speech.Internal;

internal abstract class GrammarNode
{
    private static readonly char[] Separators = [' ', '\t', '\r', '\n'];

    public static string[] SplitPhrase(string phrase, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(phrase, parameterName);

        string[] words = phrase.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            throw new ArgumentException("The phrase must contain at least one word.", parameterName);
        }

        return words;
    }

    public static GrammarNode FromPhrase(string phrase, string parameterName)
    {
        return new LiteralNode(SplitPhrase(phrase, parameterName));
    }

    public static string[] Tokenize(string text)
    {
        return text.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
    }
}

internal sealed class LiteralNode(string[] words) : GrammarNode
{
    public string[] Words { get; } = words;
}

internal sealed class SequenceNode(GrammarNode[] items) : GrammarNode
{
    public GrammarNode[] Items { get; } = items;
}

internal sealed class AlternativesNode(GrammarNode[] items) : GrammarNode
{
    public GrammarNode[] Items { get; } = items;
}

internal sealed class RepeatNode(GrammarNode item, int min, int max) : GrammarNode
{
    public GrammarNode Item { get; } = item;

    public int Min { get; } = min;

    public int Max { get; } = max;
}

internal sealed class KeyNode(string key, GrammarNode item) : GrammarNode
{
    public string Key { get; } = key;

    public GrammarNode Item { get; } = item;
}

internal sealed class ValueNode(object value, GrammarNode? item) : GrammarNode
{
    public object Value { get; } = value;

    public GrammarNode? Item { get; } = item;
}

/// <summary>Matches one or more arbitrary words; used for dictation and wildcards.</summary>
internal sealed class FreeTextNode : GrammarNode
{
}

internal sealed class SubsetNode(string[] words, SubsetMatchingMode mode) : GrammarNode
{
    public string[] Words { get; } = words;

    public SubsetMatchingMode Mode { get; } = mode;

    public bool IsContiguous => Mode is SubsetMatchingMode.Subsequence or SubsetMatchingMode.SubsequenceContentRequired;

    public bool RequiresContent => Mode is SubsetMatchingMode.SubsequenceContentRequired or SubsetMatchingMode.OrderedSubsetContentRequired;

    private static readonly HashSet<string> FunctionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "as", "at", "but", "by", "for", "from", "in", "into", "nor", "of", "on", "onto", "or",
        "the", "to", "with",
    };

    public static bool IsContentWord(string word)
    {
        return !FunctionWords.Contains(word);
    }
}

/// <summary>The words and semantics captured by a matched piece of a grammar.</summary>
internal sealed class Fragment
{
    public static readonly Fragment Empty = new([], false, null, []);

    public Fragment(string[] words, bool hasValue, object? value, KeyValuePair<string, Fragment>[] keys)
    {
        Words = words;
        HasValue = hasValue;
        Value = value;
        Keys = keys;
    }

    public string[] Words { get; }

    public bool HasValue { get; }

    public object? Value { get; }

    public KeyValuePair<string, Fragment>[] Keys { get; }

    public Fragment Then(Fragment next)
    {
        return new Fragment(
            [.. Words, .. next.Words],
            HasValue || next.HasValue,
            next.HasValue ? next.Value : Value,
            [.. Keys, .. next.Keys]);
    }

    public SemanticValue ToSemanticValue(float confidence)
    {
        return ToSemanticValue(string.Empty, confidence, Value);
    }

    private SemanticValue ToSemanticValue(string keyName, float confidence, object? value)
    {
        SemanticValue result = new(keyName, value, confidence);
        foreach (KeyValuePair<string, Fragment> pair in Keys)
        {
            object? childValue = pair.Value.HasValue ? pair.Value.Value : string.Join(" ", pair.Value.Words);
            result.SetChild(pair.Key, pair.Value.ToSemanticValue(pair.Key, confidence, childValue));
        }

        return result;
    }
}

internal sealed class GrammarMatcher(string[] tokens, CompareOptions compareOptions)
{
    private readonly CompareInfo compareInfo = CultureInfo.InvariantCulture.CompareInfo;

    /// <summary>Finds a parse of the grammar that consumes every token.</summary>
    public Fragment? MatchAll(GrammarNode root)
    {
        foreach ((int end, Fragment fragment) in Match(root, 0))
        {
            if (end == tokens.Length)
            {
                return fragment;
            }
        }

        return null;
    }

    private IEnumerable<(int End, Fragment Fragment)> Match(GrammarNode node, int position)
    {
        return node switch
        {
            LiteralNode literal => MatchLiteral(literal, position),
            SequenceNode sequence => MatchSequence(sequence.Items, 0, position),
            AlternativesNode alternatives => alternatives.Items.SelectMany(item => Match(item, position)),
            RepeatNode repeat => MatchRepeat(repeat, 0, position),
            KeyNode key => Match(key.Item, position).Select(
                match => (match.End, new Fragment(match.Fragment.Words, false, null, [new(key.Key, match.Fragment)]))),
            ValueNode value => MatchValue(value, position),
            FreeTextNode => MatchFreeText(position),
            SubsetNode subset => MatchSubset(subset, position),
            _ => throw new InvalidOperationException($"Unknown grammar node {node.GetType().Name}."),
        };
    }

    private IEnumerable<(int End, Fragment Fragment)> MatchLiteral(LiteralNode literal, int position)
    {
        if (position + literal.Words.Length > tokens.Length)
        {
            yield break;
        }

        for (int i = 0; i < literal.Words.Length; i++)
        {
            if (!WordEquals(literal.Words[i], tokens[position + i]))
            {
                yield break;
            }
        }

        yield return (position + literal.Words.Length, new Fragment(literal.Words, false, null, []));
    }

    private IEnumerable<(int End, Fragment Fragment)> MatchSequence(GrammarNode[] items, int index, int position)
    {
        if (index == items.Length)
        {
            yield return (position, Fragment.Empty);
            yield break;
        }

        foreach ((int end, Fragment head) in Match(items[index], position))
        {
            foreach ((int finalEnd, Fragment tail) in MatchSequence(items, index + 1, end))
            {
                yield return (finalEnd, head.Then(tail));
            }
        }
    }

    private IEnumerable<(int End, Fragment Fragment)> MatchRepeat(RepeatNode repeat, int count, int position)
    {
        if (count < repeat.Max)
        {
            foreach ((int end, Fragment head) in Match(repeat.Item, position))
            {
                // A repetition that consumes nothing can never make progress.
                if (end == position && count >= repeat.Min)
                {
                    continue;
                }

                foreach ((int finalEnd, Fragment tail) in MatchRepeat(repeat, count + 1, end))
                {
                    yield return (finalEnd, head.Then(tail));
                }
            }
        }

        if (count >= repeat.Min)
        {
            yield return (position, Fragment.Empty);
        }
    }

    private IEnumerable<(int End, Fragment Fragment)> MatchValue(ValueNode value, int position)
    {
        if (value.Item is null)
        {
            yield return (position, new Fragment([], true, value.Value, []));
            yield break;
        }

        foreach ((int end, Fragment fragment) in Match(value.Item, position))
        {
            yield return (end, new Fragment(fragment.Words, true, value.Value, fragment.Keys));
        }
    }

    private IEnumerable<(int End, Fragment Fragment)> MatchFreeText(int position)
    {
        for (int end = tokens.Length; end > position; end--)
        {
            yield return (end, new Fragment(tokens[position..end], false, null, []));
        }
    }

    private IEnumerable<(int End, Fragment Fragment)> MatchSubset(SubsetNode subset, int position)
    {
        return subset.IsContiguous ? MatchSubsequence(subset, position) : MatchOrderedSubset(subset, 0, position, []);
    }

    private IEnumerable<(int End, Fragment Fragment)> MatchSubsequence(SubsetNode subset, int position)
    {
        for (int start = 0; start < subset.Words.Length; start++)
        {
            int length = 0;
            while (start + length < subset.Words.Length
                && position + length < tokens.Length
                && WordEquals(subset.Words[start + length], tokens[position + length]))
            {
                length++;
            }

            for (; length > 0; length--)
            {
                string[] words = subset.Words[start..(start + length)];
                if (IsAcceptableSubset(subset, words))
                {
                    yield return (position + length, new Fragment(words, false, null, []));
                }
            }
        }
    }

    private IEnumerable<(int End, Fragment Fragment)> MatchOrderedSubset(SubsetNode subset, int wordIndex, int position, string[] matched)
    {
        if (position < tokens.Length)
        {
            for (int i = wordIndex; i < subset.Words.Length; i++)
            {
                if (WordEquals(subset.Words[i], tokens[position]))
                {
                    foreach ((int End, Fragment Fragment) match in MatchOrderedSubset(subset, i + 1, position + 1, [.. matched, subset.Words[i]]))
                    {
                        yield return match;
                    }
                }
            }
        }

        if (matched.Length > 0 && IsAcceptableSubset(subset, matched))
        {
            yield return (position, new Fragment(matched, false, null, []));
        }
    }

    private static bool IsAcceptableSubset(SubsetNode subset, string[] words)
    {
        return !subset.RequiresContent || words.Any(SubsetNode.IsContentWord);
    }

    private bool WordEquals(string left, string right)
    {
        return compareInfo.Compare(left, right, compareOptions) == 0;
    }
}

internal static class GrammarNodeExpander
{
    /// <summary>Returns every phrase the grammar accepts, or null if it is open-ended or exceeds the limit.</summary>
    public static IReadOnlyList<string[]>? Expand(GrammarNode node, int limit)
    {
        switch (node)
        {
            case LiteralNode literal:
                return [literal.Words];
            case SequenceNode sequence:
                return ExpandSequence(sequence.Items, limit);
            case AlternativesNode alternatives:
                List<string[]> all = new();
                foreach (GrammarNode item in alternatives.Items)
                {
                    IReadOnlyList<string[]>? expanded = Expand(item, limit);
                    if (expanded is null || all.Count + expanded.Count > limit)
                    {
                        return null;
                    }

                    all.AddRange(expanded);
                }

                return all;
            case RepeatNode repeat:
                return ExpandRepeat(repeat, limit);
            case KeyNode key:
                return Expand(key.Item, limit);
            case ValueNode value:
                return value.Item is null ? [[]] : Expand(value.Item, limit);
            case SubsetNode subset:
                return ExpandSubset(subset, limit);
            default:
                return null;
        }
    }

    public static bool ContainsFreeText(GrammarNode node)
    {
        return node switch
        {
            FreeTextNode => true,
            SequenceNode sequence => sequence.Items.Any(ContainsFreeText),
            AlternativesNode alternatives => alternatives.Items.Any(ContainsFreeText),
            RepeatNode repeat => ContainsFreeText(repeat.Item),
            KeyNode key => ContainsFreeText(key.Item),
            ValueNode { Item: not null } value => ContainsFreeText(value.Item),
            _ => false,
        };
    }

    public static void CollectWords(GrammarNode node, HashSet<string> words)
    {
        switch (node)
        {
            case LiteralNode literal:
                words.UnionWith(literal.Words);
                break;
            case SubsetNode subset:
                words.UnionWith(subset.Words);
                break;
            case SequenceNode sequence:
                Array.ForEach(sequence.Items, item => CollectWords(item, words));
                break;
            case AlternativesNode alternatives:
                Array.ForEach(alternatives.Items, item => CollectWords(item, words));
                break;
            case RepeatNode repeat:
                CollectWords(repeat.Item, words);
                break;
            case KeyNode key:
                CollectWords(key.Item, words);
                break;
            case ValueNode { Item: not null } value:
                CollectWords(value.Item, words);
                break;
        }
    }

    private static IReadOnlyList<string[]>? ExpandSequence(GrammarNode[] items, int limit)
    {
        List<string[]> current = [[]];
        foreach (GrammarNode item in items)
        {
            IReadOnlyList<string[]>? expanded = Expand(item, limit);
            if (expanded is null || (long)current.Count * expanded.Count > limit)
            {
                return null;
            }

            current = current.SelectMany(head => expanded.Select(tail => (string[])[.. head, .. tail])).ToList();
        }

        return current;
    }

    private static IReadOnlyList<string[]>? ExpandRepeat(RepeatNode repeat, int limit)
    {
        IReadOnlyList<string[]>? item = Expand(repeat.Item, limit);
        if (item is null)
        {
            return null;
        }

        List<string[]> result = new();
        List<string[]> current = [[]];
        for (int count = 0; count <= repeat.Max; count++)
        {
            if (count >= repeat.Min)
            {
                result.AddRange(current);
            }

            if (result.Count > limit)
            {
                return null;
            }

            if (count < repeat.Max)
            {
                if ((long)current.Count * item.Count > limit)
                {
                    return null;
                }

                current = current.SelectMany(head => item.Select(tail => (string[])[.. head, .. tail])).ToList();
            }
        }

        return result;
    }

    private static IReadOnlyList<string[]>? ExpandSubset(SubsetNode subset, int limit)
    {
        int count = subset.Words.Length;
        if (!subset.IsContiguous && (count >= 31 || (1L << count) > limit))
        {
            return null;
        }

        List<string[]> result = new();
        if (subset.IsContiguous)
        {
            for (int start = 0; start < count; start++)
            {
                for (int end = start + 1; end <= count; end++)
                {
                    result.Add(subset.Words[start..end]);
                }
            }
        }
        else
        {
            for (int mask = 1; mask < (1 << count); mask++)
            {
                result.Add(subset.Words.Where((_, index) => (mask & (1 << index)) != 0).ToArray());
            }
        }

        result.RemoveAll(words => subset.RequiresContent && !words.Any(SubsetNode.IsContentWord));
        return result.Count > limit ? null : result;
    }
}
