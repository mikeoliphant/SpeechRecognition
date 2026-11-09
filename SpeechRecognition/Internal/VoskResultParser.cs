using System.Text.Json;

namespace System.Speech.Internal;

internal readonly record struct VoskWord(string Text, float Confidence, double Start, double End);

internal sealed record VoskHypothesis(VoskWord[] Words, float Confidence);

internal static class VoskResultParser
{
    public const string UnknownWord = "[unk]";

    /// <summary>Parses a Vosk final result into hypotheses, best first. Empty when nothing was said.</summary>
    public static List<VoskHypothesis> ParseFinal(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("alternatives", out JsonElement alternatives))
        {
            return ParseAlternatives(alternatives);
        }

        VoskWord[] words = ParseWords(root, null);
        if (words.Length == 0)
        {
            return [];
        }

        return [new VoskHypothesis(words, words.Average(word => word.Confidence))];
    }

    /// <summary>
    /// Removes unknown-word markers from the ends of a hypothesis. Vosk attaches noise and out-of-grammar
    /// speech to the following utterance, which would otherwise cause a valid command to be rejected.
    /// Returns null if nothing changed or no words remain.
    /// </summary>
    public static VoskHypothesis? TrimUnknownWords(VoskHypothesis hypothesis)
    {
        VoskWord[] words = hypothesis.Words
            .SkipWhile(word => word.Text == UnknownWord)
            .Reverse()
            .SkipWhile(word => word.Text == UnknownWord)
            .Reverse()
            .ToArray();

        return words.Length == 0 || words.Length == hypothesis.Words.Length
            ? null
            : new VoskHypothesis(words, words.Average(word => word.Confidence));
    }

    public static string ParsePartial(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("partial", out JsonElement partial)
            ? partial.GetString() ?? string.Empty
            : string.Empty;
    }

    private static List<VoskHypothesis> ParseAlternatives(JsonElement alternatives)
    {
        List<(JsonElement Element, double Score)> entries = alternatives.EnumerateArray()
            .Select(element => (element, element.TryGetProperty("confidence", out JsonElement score) ? score.GetDouble() : 0.0))
            .ToList();

        if (entries.Count == 0)
        {
            return [];
        }

        // N-best scores are lattice costs on an arbitrary scale, so normalize them across the list.
        double best = entries.Max(entry => entry.Score);
        double total = entries.Sum(entry => Math.Exp(entry.Score - best));

        List<VoskHypothesis> result = new();
        foreach ((JsonElement element, double score) in entries)
        {
            float confidence = (float)(Math.Exp(score - best) / total);
            VoskWord[] words = ParseWords(element, confidence);
            if (words.Length > 0)
            {
                result.Add(new VoskHypothesis(words, confidence));
            }
        }

        return result;
    }

    private static VoskWord[] ParseWords(JsonElement element, float? fallbackConfidence)
    {
        if (element.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.Array)
        {
            return result.EnumerateArray().Select(word => new VoskWord(
                word.GetProperty("word").GetString() ?? string.Empty,
                word.TryGetProperty("conf", out JsonElement confidence) ? Math.Clamp(confidence.GetSingle(), 0.0f, 1.0f) : fallbackConfidence ?? 1.0f,
                word.TryGetProperty("start", out JsonElement start) ? start.GetDouble() : 0.0,
                word.TryGetProperty("end", out JsonElement end) ? end.GetDouble() : 0.0)).ToArray();
        }

        string text = element.TryGetProperty("text", out JsonElement textElement) ? textElement.GetString() ?? string.Empty : string.Empty;
        return GrammarNode.Tokenize(text).Select(word => new VoskWord(word, fallbackConfidence ?? 1.0f, 0.0, 0.0)).ToArray();
    }
}
