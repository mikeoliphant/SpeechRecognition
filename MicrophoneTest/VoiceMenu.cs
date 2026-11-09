using System.Speech.Recognition;

namespace MicrophoneTest;

/// <summary>A spoken menu with one grammar per screen, of which only the current screen's is enabled.</summary>
internal sealed class VoiceMenu
{
    private sealed record Screen(string Name, string[] Examples, Grammar Grammar, Action<RecognitionResult> Handle, Screen? Parent);

    private readonly SpeechRecognitionEngine engine;
    private readonly ManualResetEventSlim finished = new();
    private readonly List<Screen> screens = new();
    private readonly Dictionary<string, string> lights = new()
    {
        ["kitchen"] = "off",
        ["bedroom"] = "off",
        ["living room"] = "off",
    };

    private readonly Screen mainScreen;
    private readonly Screen lightsScreen;
    private readonly Screen thermostatScreen;
    private readonly Screen musicScreen;
    private readonly Screen playlistsScreen;

    private Screen current;
    private int temperature = 70;
    private int volume = 5;
    private string nowPlaying = "nothing";
    private bool hypothesisShown;
    private int exitCode;

    public VoiceMenu(SpeechRecognitionEngine engine)
    {
        this.engine = engine;

        mainScreen = AddScreen(
            "Main menu",
            ["lights", "go to the thermostat", "open music"],
            BuildMainGrammar(),
            HandleMain,
            null);
        lightsScreen = AddScreen(
            "Lights",
            ["turn off the kitchen lights please", "set the bedroom lights to fifty percent"],
            BuildLightsGrammar(),
            HandleLights,
            mainScreen);
        thermostatScreen = AddScreen(
            "Thermostat",
            ["set the temperature to seventy two degrees", "make it warmer", "what is the temperature"],
            BuildThermostatGrammar(),
            HandleThermostat,
            mainScreen);
        musicScreen = AddScreen(
            "Music",
            ["play some jazz music", "volume up", "stop the music", "playlists"],
            BuildMusicGrammar(),
            HandleMusic,
            mainScreen);
        playlistsScreen = AddScreen(
            "Playlists",
            ["open the road trip playlist", "shuffle the workout playlist"],
            BuildPlaylistsGrammar(),
            HandlePlaylists,
            musicScreen);

        current = mainScreen;

        // Global commands stay enabled on every screen and win over screen commands.
        Grammar global = new(BuildGlobalGrammar()) { Name = "Global", Priority = 10 };
        global.SpeechRecognized += (_, args) => HandleGlobal(args.Result);
        engine.LoadGrammar(global);

        engine.SpeechHypothesized += (_, args) => ShowHypothesis(args.Result.Text);
        engine.SpeechRecognized += (_, args) => ClearHypothesis();
        engine.SpeechRecognitionRejected += (_, args) => Say($"Didn't catch that (heard \"{args.Result.Text}\"). Say \"help\" for options.");
        engine.RecognizeCompleted += OnRecognizeCompleted;
    }

    public int Run()
    {
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            engine.RecognizeAsyncStop();
        };

        Console.WriteLine("Speak a command. Say \"help\" for options and \"quit\" to exit (or press Ctrl+C).");
        Navigate(mainScreen);
        engine.RecognizeAsync(RecognizeMode.Multiple);
        finished.Wait();
        return exitCode;
    }

    private Screen AddScreen(string name, string[] examples, GrammarBuilder builder, Action<RecognitionResult> handle, Screen? parent)
    {
        Grammar grammar = new(builder) { Name = name, Enabled = false };
        Screen screen = new(name, examples, grammar, handle, parent);
        grammar.SpeechRecognized += (_, args) => screen.Handle(args.Result);
        engine.LoadGrammar(grammar);
        screens.Add(screen);
        return screen;
    }

    private void Navigate(Screen screen)
    {
        current = screen;
        foreach (Screen candidate in screens)
        {
            candidate.Grammar.Enabled = ReferenceEquals(candidate, screen);
        }

        Console.WriteLine();
        Console.WriteLine($"== {screen.Name} ==");
        ShowHelp();
    }

    private void ShowHelp()
    {
        Console.WriteLine("  Try: " + string.Join(" | ", current.Examples.Select(example => $"\"{example}\"")));
        Console.WriteLine("  Anywhere: \"go back\" | \"main menu\" | \"status\" | \"where am I\" | \"help\" | \"quit\"");
    }

    private void ShowHypothesis(string text)
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        int width = Math.Max(1, Console.WindowWidth - 1);
        string line = ("  ... " + text).PadRight(width);
        Console.Write("\r" + line[..width]);
        hypothesisShown = true;
    }

    private void ClearHypothesis()
    {
        if (hypothesisShown)
        {
            Console.Write("\r" + new string(' ', Math.Max(1, Console.WindowWidth - 1)) + "\r");
            hypothesisShown = false;
        }
    }

    private void Say(string message)
    {
        ClearHypothesis();
        Console.WriteLine("  > " + message);
    }

    private void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs args)
    {
        if (args.Error is not null)
        {
            Console.Error.WriteLine($"Recognition failed: {args.Error.Message}");
            exitCode = 1;
        }

        Console.WriteLine("Stopped.");
        finished.Set();
    }

    private void HandleGlobal(RecognitionResult result)
    {
        switch ((string)result.Semantics.Value!)
        {
            case "back":
                if (current.Parent is null)
                {
                    Say("Already at the main menu.");
                }
                else
                {
                    Navigate(current.Parent);
                }

                break;
            case "main":
                Navigate(mainScreen);
                break;
            case "help":
                ShowHelp();
                break;
            case "where":
                Say($"You are in: {PathTo(current)}");
                break;
            case "status":
                Say("Lights: " + string.Join(", ", lights.Select(light => $"{light.Key} {light.Value}")));
                Say($"Temperature: {temperature} degrees. Now playing: {nowPlaying}. Volume: {volume}.");
                break;
            case "quit":
                Say("Goodbye.");
                engine.RecognizeAsyncStop();
                break;
        }
    }

    private static string PathTo(Screen screen)
    {
        return screen.Parent is null ? screen.Name : $"{PathTo(screen.Parent)} > {screen.Name}";
    }

    private void HandleMain(RecognitionResult result)
    {
        Navigate((string)result.Semantics.Value! switch
        {
            "lights" => lightsScreen,
            "thermostat" => thermostatScreen,
            _ => musicScreen,
        });
    }

    private void HandleLights(RecognitionResult result)
    {
        string room = (string)result.Semantics["room"].Value!;
        string newState = (string)result.Semantics.Value! == "switch"
            ? (string)result.Semantics["state"].Value!
            : $"{result.Semantics["level"].Value}%";

        foreach (string key in lights.Keys.Where(key => room == "all" || key == room).ToList())
        {
            lights[key] = newState;
        }

        Say($"{(room == "all" ? "All" : room)} lights: {newState} (confidence {result.Confidence:P0})");
    }

    private void HandleThermostat(RecognitionResult result)
    {
        switch ((string)result.Semantics.Value!)
        {
            case "set":
                int ones = result.Semantics.ContainsKey("ones") ? (int)result.Semantics["ones"].Value! : 0;
                temperature = (int)result.Semantics["tens"].Value! + ones;
                break;
            case "warmer":
                temperature++;
                break;
            case "colder":
                temperature--;
                break;
        }

        Say($"Temperature is {temperature} degrees.");
    }

    private void HandleMusic(RecognitionResult result)
    {
        switch ((string)result.Semantics.Value!)
        {
            case "play":
                nowPlaying = $"{result.Semantics["genre"].Value} music";
                Say($"Now playing {nowPlaying}.");
                break;
            case "stop":
                nowPlaying = "nothing";
                Say("Music stopped.");
                break;
            case "next":
                Say("Skipping to the next song.");
                break;
            case "volume":
                volume = Math.Clamp(volume + (int)result.Semantics["direction"].Value!, 0, 10);
                Say($"Volume is {volume}.");
                break;
            case "playlists":
                Navigate(playlistsScreen);
                break;
        }
    }

    private void HandlePlaylists(RecognitionResult result)
    {
        string playlist = (string)result.Semantics["playlist"].Value!;
        bool shuffle = (string)result.Semantics.Value! == "shuffle";
        nowPlaying = $"the {playlist} playlist{(shuffle ? " on shuffle" : string.Empty)}";
        Say($"Now playing {nowPlaying}.");
    }

    private static GrammarBuilder BuildGlobalGrammar()
    {
        return new Choices(
            Command("go back", "back"),
            Command("main menu", "main"),
            Command("help", "help"),
            Command("where am i", "where"),
            Command("status", "status"),
            Command(new GrammarBuilder(new Choices("quit", "exit")), "quit"));
    }

    private static GrammarBuilder BuildMainGrammar()
    {
        return new Choices(
            Command(Destination("lights"), "lights"),
            Command(Destination("thermostat"), "thermostat"),
            Command(Destination("music"), "music"));
    }

    /// <summary>Accepts "lights", "go to the lights", "open the lights menu" and similar.</summary>
    private static GrammarBuilder Destination(string name)
    {
        GrammarBuilder builder = new();
        builder.Append(new Choices("go to", "open", "show"), 0, 1);
        builder.Append("the", 0, 1);
        builder.Append(name);
        builder.Append("menu", 0, 1);
        return builder;
    }

    private static GrammarBuilder BuildLightsGrammar()
    {
        GrammarBuilder room = Key("room", ("kitchen", "kitchen"), ("bedroom", "bedroom"), ("living room", "living room"), ("all", "all"));

        GrammarBuilder switchLights = new("turn");
        switchLights.Append(Key("state", ("on", "on"), ("off", "off")));
        switchLights.Append("the", 0, 1);
        switchLights.Append(room);
        switchLights.Append("lights");

        GrammarBuilder dimLights = new(new Choices("set", "dim"));
        dimLights.Append("the", 0, 1);
        dimLights.Append(room);
        dimLights.Append("lights to");
        dimLights.Append(Key(
            "level",
            ("ten", 10), ("twenty", 20), ("thirty", 30), ("forty", 40), ("fifty", 50),
            ("sixty", 60), ("seventy", 70), ("eighty", 80), ("ninety", 90), ("one hundred", 100)));
        dimLights.Append("percent");

        GrammarBuilder builder = new(new Choices(Command(switchLights, "switch"), Command(dimLights, "dim")));
        builder.Append("please", 0, 1);
        return builder;
    }

    private static GrammarBuilder BuildThermostatGrammar()
    {
        GrammarBuilder set = new("set the temperature to");
        set.Append(Key("tens", ("sixty", 60), ("seventy", 70), ("eighty", 80)));
        set.Append(
            Key("ones", ("one", 1), ("two", 2), ("three", 3), ("four", 4), ("five", 5), ("six", 6), ("seven", 7), ("eight", 8), ("nine", 9)),
            0,
            1);
        set.Append("degrees");

        return new Choices(
            Command(set, "set"),
            Command("make it warmer", "warmer"),
            Command("make it colder", "colder"),
            Command("what is the temperature", "query"));
    }

    private static GrammarBuilder BuildMusicGrammar()
    {
        GrammarBuilder play = new("play");
        play.Append("some", 0, 1);
        play.Append(Key("genre", ("jazz", "jazz"), ("rock", "rock"), ("classical", "classical")));
        play.Append("music", 0, 1);

        GrammarBuilder volume = new("volume");
        volume.Append(Key("direction", ("up", 1), ("down", -1)));

        return new Choices(
            Command(play, "play"),
            Command(volume, "volume"),
            Command("stop the music", "stop"),
            Command("next song", "next"),
            Command(Destination("playlists"), "playlists"));
    }

    private static GrammarBuilder BuildPlaylistsGrammar()
    {
        GrammarBuilder Action(string verb)
        {
            GrammarBuilder builder = new(verb);
            builder.Append("the", 0, 1);
            builder.Append(Key("playlist", ("workout", "workout"), ("dinner", "dinner"), ("road trip", "road trip")));
            builder.Append("playlist", 0, 1);
            return builder;
        }

        return new Choices(Command(Action("open"), "open"), Command(Action("shuffle"), "shuffle"));
    }

    private static GrammarBuilder Command(GrammarBuilder phrase, string command)
    {
        return new SemanticResultValue(phrase, command);
    }

    private static GrammarBuilder Command(string phrase, string command)
    {
        return new SemanticResultValue(phrase, command);
    }

    private static GrammarBuilder Key(string key, params (string Phrase, object Value)[] options)
    {
        Choices choices = new();
        foreach ((string phrase, object value) in options)
        {
            choices.Add(new SemanticResultValue(phrase, value).ToGrammarBuilder());
        }

        return new SemanticResultKey(key, choices);
    }
}
