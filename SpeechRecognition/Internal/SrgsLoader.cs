using System.Xml.Linq;

namespace System.Speech.Internal;

/// <summary>Loads SRGS XML grammars, with literal semantic tags only (no script).</summary>
internal static class SrgsLoader
{
    public static GrammarNode Load(string path, string? ruleName)
    {
        string localPath = Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) && uri.IsFile ? uri.LocalPath : path;
        using FileStream stream = File.OpenRead(localPath);
        return Load(stream, ruleName);
    }

    public static GrammarNode Load(Stream stream, string? ruleName)
    {
        XElement root;
        try
        {
            root = XDocument.Load(stream).Root!;
        }
        catch (System.Xml.XmlException exception)
        {
            throw new FormatException("Only SRGS XML grammars are supported by the Vosk backend.", exception);
        }

        if (root.Name.LocalName != "grammar")
        {
            throw new FormatException("The document is not an SRGS grammar.");
        }

        return new Compiler(root).Compile(ruleName ?? (string?)root.Attribute("root"));
    }

    private sealed class Compiler
    {
        private readonly Dictionary<string, XElement> rules;
        private readonly HashSet<string> activeRules = new();
        private readonly bool literalTags;

        public Compiler(XElement root)
        {
            rules = root.Elements()
                .Where(element => element.Name.LocalName == "rule")
                .ToDictionary(element => (string?)element.Attribute("id") ?? throw new FormatException("A rule has no id."));
            literalTags = (string?)root.Attribute("tag-format") == "semantics/1.0-literals";
        }

        public GrammarNode Compile(string? ruleName)
        {
            if (ruleName is null)
            {
                throw new FormatException("The grammar has no root rule and no rule name was given.");
            }

            return CompileRule(ruleName);
        }

        private GrammarNode CompileRule(string ruleName)
        {
            if (!rules.TryGetValue(ruleName, out XElement? rule))
            {
                throw new FormatException($"The grammar has no rule named '{ruleName}'.");
            }

            if (!activeRules.Add(ruleName))
            {
                throw new NotSupportedException($"Recursive rule references are not supported ('{ruleName}').");
            }

            GrammarNode node = CompileContent(rule);
            activeRules.Remove(ruleName);
            return node;
        }

        private GrammarNode CompileContent(XElement element)
        {
            List<GrammarNode> items = new();
            foreach (XNode child in element.Nodes())
            {
                if (child is XText text)
                {
                    string[] words = GrammarNode.Tokenize(text.Value);
                    if (words.Length > 0)
                    {
                        items.Add(new LiteralNode(words));
                    }
                }
                else if (child is XElement childElement)
                {
                    items.Add(CompileElement(childElement));
                }
            }

            return new SequenceNode(items.ToArray());
        }

        private GrammarNode CompileElement(XElement element)
        {
            switch (element.Name.LocalName)
            {
                case "item":
                    return ApplyRepeat(CompileContent(element), (string?)element.Attribute("repeat"));
                case "one-of":
                    return new AlternativesNode(element.Elements().Select(CompileElement).ToArray());
                case "token":
                    return new LiteralNode(GrammarNode.SplitPhrase(element.Value, "token"));
                case "ruleref":
                    return CompileRuleRef(element);
                case "tag":
                    if (!literalTags)
                    {
                        throw new NotSupportedException("Only tag-format=\"semantics/1.0-literals\" is supported by the Vosk backend.");
                    }

                    return new ValueNode(element.Value.Trim(), null);
                default:
                    throw new NotSupportedException($"The SRGS element <{element.Name.LocalName}> is not supported.");
            }
        }

        private GrammarNode CompileRuleRef(XElement element)
        {
            switch ((string?)element.Attribute("special"))
            {
                case "NULL":
                    return new SequenceNode([]);
                case "VOID":
                    return new AlternativesNode([]);
                case "GARBAGE":
                    return new FreeTextNode();
            }

            string? uri = (string?)element.Attribute("uri");
            if (uri is null || !uri.StartsWith('#'))
            {
                throw new NotSupportedException($"Only references to rules in the same file are supported ('{uri}').");
            }

            return CompileRule(uri[1..]);
        }

        private static GrammarNode ApplyRepeat(GrammarNode node, string? repeat)
        {
            if (repeat is null)
            {
                return node;
            }

            string[] parts = repeat.Split('-');
            int min = int.Parse(parts[0]);
            int max = parts.Length == 1 ? min : parts[1].Length == 0 ? 255 : int.Parse(parts[1]);
            return new RepeatNode(node, min, max);
        }
    }
}
