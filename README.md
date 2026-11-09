# SpeechRecognition

A drop-in replacement for Microsoft's `System.Speech.Recognition` API, built on [Vosk](https://github.com/alphacep/vosk-api). It uses the same namespaces and types, so existing code only needs its reference switched to this project.

Requires .NET 10. The Vosk package supplies x64 native libraries for Windows.

## Models

Download a model from <https://alphacephei.com/vosk/models> (the small models work best with grammars). Models are found in:

- the folder(s) listed in `VOSK_MODEL_PATH`
- a `models` folder next to the application
- `%LOCALAPPDATA%\vosk`
- `~/.cache/vosk`

Or pass a model folder directly: `new SpeechRecognitionEngine(path)`.

## Differences from System.Speech

- `SrgsDocument` is not implemented. SRGS `.grxml` files and streams load, with literal tags only.
- Phrases containing words the model doesn't know can never match, so write numbers as words ("five", not "5").
- `MaxAlternates` defaults to 1, because Vosk only reports per-word confidence for single results.
- `EndSilenceTimeout`, `EndSilenceTimeoutAmbiguous` and `PauseRecognizerOnRecognition` have no effect.
- `AudioSignalProblemOccurred` is never raised.
- `SpeechHypothesized` reports raw partial text, not filtered by grammars.

## Demo

`MicrophoneTest` is a voice-driven menu. Add `--wave file.wav` to use a file instead of the microphone:

```
dotnet run --project MicrophoneTest -- <model-folder>
```

## AI Disclaimer

This repository was created almost entirely using Claude Code. I claim no engineering credit (or responsibility) for any of it.
