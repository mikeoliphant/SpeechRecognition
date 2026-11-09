using System.Globalization;
using System.Speech.Internal;
using System.Speech.Recognition;
using System.Text;
using Xunit;

namespace SpeechRecognition.Tests;

public sealed class GrammarTests : IDisposable
{
    private readonly string modelDirectory;
    private readonly SpeechRecognitionEngine engine;

    public GrammarTests()
    {
        // EmulateRecognize never loads the model, so only its marker file is needed.
        modelDirectory = Path.Combine(Path.GetTempPath(), "vosk-model-en-us-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(modelDirectory, "am"));
        File.WriteAllText(Path.Combine(modelDirectory, "am", "final.mdl"), string.Empty);
        engine = new SpeechRecognitionEngine(modelDirectory);
    }

    public void Dispose()
    {
        engine.Dispose();
        Directory.Delete(modelDirectory, recursive: true);
    }

    [Fact]
    public void ResultUsesGrammarSpelling()
    {
        engine.LoadGrammar(new Grammar(new GrammarBuilder("Turn ON")));

        RecognitionResult? result = engine.EmulateRecognize("turn on");

        Assert.NotNull(result);
        Assert.Equal("Turn ON", result.Text);
    }

    [Fact]
    public void ChoicesAndSemanticKeysProduceSemantics()
    {
        GrammarBuilder builder = new("set color to");
        builder.Append(new SemanticResultKey("color", new Choices(
            new SemanticResultValue("red", 1).ToGrammarBuilder(),
            new SemanticResultValue("dark blue", 2).ToGrammarBuilder())));
        engine.LoadGrammar(new Grammar(builder));

        RecognitionResult? result = engine.EmulateRecognize("set color to dark blue");

        Assert.NotNull(result);
        Assert.Equal(2, result.Semantics["color"].Value);
    }

    [Fact]
    public void SemanticKeyWithoutValueUsesMatchedText()
    {
        GrammarBuilder builder = new("call");
        builder.Append(new SemanticResultKey("who", new Choices("alice", "bob")));
        engine.LoadGrammar(new Grammar(builder));

        Assert.Equal("bob", engine.EmulateRecognize("call bob")!.Semantics["who"].Value);
    }

    [Fact]
    public void RepeatHonorsBounds()
    {
        GrammarBuilder builder = new("go");
        builder.Append("very", 1, 2);
        builder.Append("fast");
        engine.LoadGrammar(new Grammar(builder));

        Assert.Null(engine.EmulateRecognize("go fast"));
        Assert.NotNull(engine.EmulateRecognize("go very fast"));
        Assert.NotNull(engine.EmulateRecognize("go very very fast"));
        Assert.Null(engine.EmulateRecognize("go very very very fast"));
    }

    [Fact]
    public void DictationCapturesFreeTextBetweenLiterals()
    {
        GrammarBuilder builder = new("note");
        builder.Append(new SemanticResultKey("body", WrapDictation()));
        builder.Append("done");
        engine.LoadGrammar(new Grammar(builder));

        RecognitionResult? result = engine.EmulateRecognize("note buy some milk done");

        Assert.NotNull(result);
        Assert.Equal("buy some milk", result.Semantics["body"].Value);

        static GrammarBuilder WrapDictation()
        {
            GrammarBuilder dictation = new();
            dictation.AppendDictation();
            return dictation;
        }
    }

    [Theory]
    [InlineData(SubsetMatchingMode.Subsequence, "quick brown", true)]
    [InlineData(SubsetMatchingMode.Subsequence, "the brown", false)]
    [InlineData(SubsetMatchingMode.OrderedSubset, "the brown", true)]
    [InlineData(SubsetMatchingMode.OrderedSubset, "brown the", false)]
    [InlineData(SubsetMatchingMode.SubsequenceContentRequired, "the", false)]
    [InlineData(SubsetMatchingMode.OrderedSubsetContentRequired, "the fox", true)]
    public void SubsetModes(SubsetMatchingMode mode, string spoken, bool expected)
    {
        engine.LoadGrammar(new Grammar(new GrammarBuilder("the quick brown fox", mode)));

        Assert.Equal(expected, engine.EmulateRecognize(spoken) is not null);
    }

    [Fact]
    public void HigherPriorityGrammarWins()
    {
        Grammar low = new(new GrammarBuilder("stop")) { Name = "low", Priority = 1 };
        Grammar high = new(new GrammarBuilder("stop")) { Name = "high", Priority = 5 };
        engine.LoadGrammar(low);
        engine.LoadGrammar(high);

        Assert.Equal("high", engine.EmulateRecognize("stop")!.Grammar!.Name);
    }

    [Fact]
    public void DisabledGrammarDoesNotMatch()
    {
        engine.LoadGrammar(new Grammar(new GrammarBuilder("stop")) { Enabled = false });

        Assert.Null(engine.EmulateRecognize("stop"));
    }

    [Fact]
    public void EventsFireForRecognizedAndRejected()
    {
        Grammar grammar = new(new GrammarBuilder("yes"));
        engine.LoadGrammar(grammar);
        List<string> events = new();
        grammar.SpeechRecognized += (_, _) => events.Add("grammar");
        engine.SpeechRecognized += (_, args) => events.Add("engine:" + args.Result.Text);
        engine.SpeechRecognitionRejected += (_, args) => events.Add("rejected:" + args.Result.Text);

        engine.EmulateRecognize("yes");
        engine.EmulateRecognize("no");

        Assert.Equal(["grammar", "engine:yes", "rejected:no"], events);
    }

    [Fact]
    public void CompareOptionsNoneIsCaseSensitive()
    {
        engine.LoadGrammar(new Grammar(new GrammarBuilder("Yes")));

        Assert.Null(engine.EmulateRecognize("yes", CompareOptions.None));
        Assert.NotNull(engine.EmulateRecognize("Yes", CompareOptions.None));
    }

    [Fact]
    public void LoadingSameGrammarTwiceThrows()
    {
        Grammar grammar = new(new GrammarBuilder("yes"));
        engine.LoadGrammar(grammar);

        Assert.Throws<InvalidOperationException>(() => engine.LoadGrammar(grammar));
    }

    [Fact]
    public void RecognizeWithoutGrammarOrInputThrows()
    {
        Assert.Throws<InvalidOperationException>(() => engine.Recognize());
    }

    [Fact]
    public void ExpandedPhrasesCoverAlternativesAndRepeats()
    {
        GrammarBuilder builder = new(new Choices("a", "b"));
        builder.Append("c", 0, 1);

        IReadOnlyList<string[]>? phrases = GrammarNodeExpander.Expand(builder.Build(), 100);

        Assert.NotNull(phrases);
        Assert.Equal(
            new[] { "a", "a c", "b", "b c" },
            phrases.Select(phrase => string.Join(" ", phrase)).Order());
    }

    [Fact]
    public void SrgsGrammarLoadsFromStream()
    {
        const string Srgs = """
            <grammar version="1.0" xmlns="http://www.w3.org/2001/06/grammar" root="main" tag-format="semantics/1.0-literals">
              <rule id="main"><item>play</item><ruleref uri="#what"/><item repeat="0-1">now</item></rule>
              <rule id="what"><one-of><item>music<tag>audio</tag></item><item>video<tag>film</tag></item></one-of></rule>
            </grammar>
            """;
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(Srgs));
        engine.LoadGrammar(new Grammar(stream));

        RecognitionResult? result = engine.EmulateRecognize("play video now");

        Assert.NotNull(result);
        Assert.Equal("film", result.Semantics.Value);
    }

    [Theory]
    [InlineData("vosk-model-small-en-us-0.15", "en-US")]
    [InlineData("vosk-model-small-cn-0.22", "zh-CN")]
    [InlineData("vosk-model-de-0.21", "de")]
    public void ModelFolderNamesMapToCultures(string folder, string culture)
    {
        Assert.Equal(culture, ModelCatalog.ParseCulture(folder).Name);
    }

    [Fact]
    public void ALawAndULawDecodeKnownValues()
    {
        Assert.Equal(0, AudioConverter.DecodeULaw(0xFF));
        Assert.Equal(-32124, AudioConverter.DecodeULaw(0x00));
        Assert.Equal(8, AudioConverter.DecodeALaw(0xD5));
    }

    [Theory]
    [InlineData("[unk] main menu", "main menu")]
    [InlineData("main menu [unk] [unk]", "main menu")]
    [InlineData("main [unk] menu", null)]
    [InlineData("[unk] [unk]", null)]
    [InlineData("main menu", null)]
    public void TrimsUnknownWordsFromEndsOnly(string text, string? expected)
    {
        VoskWord[] words = GrammarNode.Tokenize(text).Select(word => new VoskWord(word, 0.5f, 0.0, 0.0)).ToArray();

        VoskHypothesis? trimmed = VoskResultParser.TrimUnknownWords(new VoskHypothesis(words, 0.5f));

        Assert.Equal(expected, trimmed is null ? null : string.Join(" ", trimmed.Words.Select(word => word.Text)));
    }

    [Fact]
    public void ParsesVoskWordsAndEmptyResults()
    {
        List<VoskHypothesis> parsed = VoskResultParser.ParseFinal("""{"result":[{"conf":0.5,"end":1.0,"start":0.5,"word":"hi"}],"text":"hi"}""");

        Assert.Equal(0.5f, Assert.Single(parsed).Confidence);
        Assert.Empty(VoskResultParser.ParseFinal("""{"text":""}"""));
    }
}
