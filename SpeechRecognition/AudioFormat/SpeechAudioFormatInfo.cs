namespace System.Speech.AudioFormat;

public enum AudioBitsPerSample
{
    Eight = 8,
    Sixteen = 16,
}

public enum AudioChannel
{
    Mono = 1,
    Stereo = 2,
}

public enum EncodingFormat
{
    Pcm = 1,
    ALaw = 6,
    ULaw = 7,
}

public class SpeechAudioFormatInfo
{
    private readonly byte[] formatSpecificData;

    public SpeechAudioFormatInfo(int samplesPerSecond, AudioBitsPerSample bitsPerSample, AudioChannel channel)
        : this(
            EncodingFormat.Pcm,
            samplesPerSecond,
            (int)bitsPerSample,
            (int)channel,
            samplesPerSecond * (int)bitsPerSample / 8 * (int)channel,
            (int)bitsPerSample / 8 * (int)channel,
            null)
    {
    }

    public SpeechAudioFormatInfo(
        EncodingFormat encodingFormat,
        int samplesPerSecond,
        int bitsPerSample,
        int channelCount,
        int averageBytesPerSecond,
        int blockAlign,
        byte[]? formatSpecificData)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samplesPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bitsPerSample);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(averageBytesPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockAlign);

        EncodingFormat = encodingFormat;
        SamplesPerSecond = samplesPerSecond;
        BitsPerSample = bitsPerSample;
        ChannelCount = channelCount;
        AverageBytesPerSecond = averageBytesPerSecond;
        BlockAlign = blockAlign;
        this.formatSpecificData = formatSpecificData is null ? [] : (byte[])formatSpecificData.Clone();
    }

    public int AverageBytesPerSecond { get; }

    public int BitsPerSample { get; }

    public int BlockAlign { get; }

    public EncodingFormat EncodingFormat { get; }

    public int ChannelCount { get; }

    public int SamplesPerSecond { get; }

    public byte[] FormatSpecificData()
    {
        return (byte[])formatSpecificData.Clone();
    }

    public override bool Equals(object? obj)
    {
        return obj is SpeechAudioFormatInfo other
            && EncodingFormat == other.EncodingFormat
            && SamplesPerSecond == other.SamplesPerSecond
            && BitsPerSample == other.BitsPerSample
            && ChannelCount == other.ChannelCount
            && AverageBytesPerSecond == other.AverageBytesPerSecond
            && BlockAlign == other.BlockAlign
            && formatSpecificData.AsSpan().SequenceEqual(other.formatSpecificData);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(EncodingFormat, SamplesPerSecond, BitsPerSample, ChannelCount, BlockAlign);
    }
}
