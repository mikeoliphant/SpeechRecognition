namespace System.Speech.Recognition;

public enum AudioSignalProblem
{
    None = 0,
    TooNoisy = 1,
    NoSignal = 2,
    TooLoud = 3,
    TooSoft = 4,
    TooFast = 5,
    TooSlow = 6,
}

public enum AudioState
{
    Stopped = 0,
    Silence = 1,
    Speech = 2,
}

[Flags]
public enum DisplayAttributes
{
    None = 0,
    ZeroTrailingSpaces = 2,
    OneTrailingSpace = 4,
    TwoTrailingSpaces = 8,
    ConsumeLeadingSpaces = 16,
}

public enum RecognizeMode
{
    Single = 0,
    Multiple = 1,
}

public enum RecognizerState
{
    Stopped = 0,
    Listening = 1,
}

public enum SubsetMatchingMode
{
    Subsequence = 0,
    OrderedSubset = 1,
    SubsequenceContentRequired = 2,
    OrderedSubsetContentRequired = 3,
}
