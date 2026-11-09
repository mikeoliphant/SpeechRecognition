using System.Collections.Concurrent;
using System.Globalization;
using System.Speech.Recognition;
using Vosk;

namespace System.Speech.Internal;

/// <summary>Presents Vosk model folders on disk as installed recognizers.</summary>
internal static class ModelCatalog
{
    public const string EnvironmentVariable = "VOSK_MODEL_PATH";

    // Models can be gigabytes and are shared between engines, so they stay loaded for the life of the process.
    private static readonly ConcurrentDictionary<string, Lazy<Model>> Models = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<RecognizerInfo> Discover()
    {
        Dictionary<string, RecognizerInfo> found = new(StringComparer.OrdinalIgnoreCase);
        foreach (string root in SearchRoots().Where(Directory.Exists))
        {
            IEnumerable<string> candidates = IsModelDirectory(root) ? [root] : Directory.EnumerateDirectories(root);
            foreach (string directory in candidates.Where(IsModelDirectory))
            {
                RecognizerInfo info = CreateInfo(directory);
                found.TryAdd(info.Id, info);
            }
        }

        return found.Values.OrderBy(info => info.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static RecognizerInfo GetDefault()
    {
        return FindForCulture(CultureInfo.CurrentUICulture)
            ?? Discover().FirstOrDefault()
            ?? throw new PlatformNotSupportedException(
                $"No Vosk model was found. Set {EnvironmentVariable} to a model folder, or unpack a model into a 'models' folder next to the application.");
    }

    public static RecognizerInfo? FindForCulture(CultureInfo culture)
    {
        IReadOnlyList<RecognizerInfo> all = Discover();
        return all.FirstOrDefault(info => info.Culture.Equals(culture))
            ?? all.FirstOrDefault(info => info.Culture.TwoLetterISOLanguageName == culture.TwoLetterISOLanguageName);
    }

    /// <summary>Finds a recognizer by its id or by the path of a model folder.</summary>
    public static RecognizerInfo? FindById(string idOrPath)
    {
        return Discover().FirstOrDefault(info => string.Equals(info.Id, idOrPath, StringComparison.OrdinalIgnoreCase))
            ?? (IsModelDirectory(idOrPath) ? CreateInfo(idOrPath) : null);
    }

    public static Model GetModel(string path)
    {
        return Models.GetOrAdd(
            Path.GetFullPath(path),
            fullPath => new Lazy<Model>(() =>
            {
                global::Vosk.Vosk.SetLogLevel(-1);
                return new Model(fullPath);
            })).Value;
    }

    internal static CultureInfo ParseCulture(string modelName)
    {
        string[] parts = modelName.ToLowerInvariant().Split('-');
        string[] tokens = parts.SkipWhile(part => part is "vosk" or "model" or "small" or "tiny").ToArray();
        if (tokens.Length == 0)
        {
            return CultureInfo.InvariantCulture;
        }

        string language = tokens[0] switch
        {
            "cn" => "zh",
            "jp" => "ja",
            "ua" => "uk",
            string other => other,
        };

        string[] candidates = tokens[0] switch
        {
            "cn" => ["zh-CN"],
            _ when tokens.Length > 1 => [$"{language}-{tokens[1].ToUpperInvariant()}", language],
            _ => [language],
        };

        foreach (string name in candidates)
        {
            try
            {
                return CultureInfo.GetCultureInfo(name, predefinedOnly: true);
            }
            catch (CultureNotFoundException)
            {
                // The second token is often a version number rather than a region.
            }
        }

        return CultureInfo.InvariantCulture;
    }

    private static IEnumerable<string> SearchRoots()
    {
        string? configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrEmpty(configured))
        {
            foreach (string path in configured.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                yield return path;
            }
        }

        yield return Path.Combine(AppContext.BaseDirectory, "models");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "vosk");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "vosk");
    }

    private static bool IsModelDirectory(string directory)
    {
        return File.Exists(Path.Combine(directory, "am", "final.mdl")) || File.Exists(Path.Combine(directory, "final.mdl"));
    }

    private static RecognizerInfo CreateInfo(string directory)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));
        return new RecognizerInfo(name, directory, ParseCulture(name));
    }
}
