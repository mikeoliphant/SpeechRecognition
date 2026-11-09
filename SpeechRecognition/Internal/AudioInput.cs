using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Speech.AudioFormat;
using NAudio.Wave;

namespace System.Speech.Internal;

/// <summary>Supplies audio to the recognizer as mono 16-bit little-endian PCM.</summary>
internal abstract class AudioInput : IDisposable
{
    public const int EndOfStream = 0;
    public const int NoData = -1;

    /// <summary>The format the caller supplied, as reported by the engine's AudioFormat property.</summary>
    public abstract SpeechAudioFormatInfo SourceFormat { get; }

    public abstract int SampleRate { get; }

    public byte[] Buffer { get; protected set; } = [];

    public virtual void Start()
    {
    }

    public virtual void Stop()
    {
    }

    /// <summary>Fills Buffer and returns the byte count, EndOfStream, or NoData if nothing is available yet.</summary>
    public abstract int Read();

    public virtual void Dispose()
    {
    }
}

internal sealed class StreamAudioInput : AudioInput
{
    private readonly Stream stream;
    private readonly bool ownsStream;
    private readonly byte[] raw;

    public StreamAudioInput(Stream stream, SpeechAudioFormatInfo format, bool ownsStream)
    {
        if (format.EncodingFormat == EncodingFormat.Pcm && format.BitsPerSample is not (8 or 16))
        {
            throw new FormatException("Only 8-bit and 16-bit PCM audio is supported.");
        }

        if (format.EncodingFormat != EncodingFormat.Pcm && format.BitsPerSample != 8)
        {
            throw new FormatException("A-law and u-law audio must be 8-bit.");
        }

        this.stream = stream;
        this.ownsStream = ownsStream;
        SourceFormat = format;
        int blocks = Math.Max(1, format.SamplesPerSecond / 10);
        raw = new byte[blocks * format.BlockAlign];
        Buffer = new byte[blocks * 2];
    }

    public override SpeechAudioFormatInfo SourceFormat { get; }

    public override int SampleRate => SourceFormat.SamplesPerSecond;

    public override int Read()
    {
        int total = 0;
        while (total < raw.Length)
        {
            int count = stream.Read(raw, total, raw.Length - total);
            if (count == 0)
            {
                break;
            }

            total += count;
        }

        int blocks = total / SourceFormat.BlockAlign;
        for (int block = 0; block < blocks; block++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(Buffer.AsSpan(block * 2), AudioConverter.ReadMonoSample(raw, block, SourceFormat));
        }

        return blocks * 2;
    }

    public override void Dispose()
    {
        if (ownsStream)
        {
            stream.Dispose();
        }
    }
}

internal sealed class MicrophoneAudioInput : AudioInput
{
    private const int MicrophoneSampleRate = 16000;

    private readonly BlockingCollection<byte[]> chunks = new();
    private WaveInEvent? waveIn;

    public MicrophoneAudioInput()
    {
        if (WaveInEvent.DeviceCount == 0)
        {
            throw new InvalidOperationException("No audio input device is available.");
        }
    }

    public override SpeechAudioFormatInfo SourceFormat { get; } = new(MicrophoneSampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono);

    public override int SampleRate => MicrophoneSampleRate;

    public override void Start()
    {
        waveIn = new WaveInEvent
        {
            DeviceNumber = -1,
            WaveFormat = new WaveFormat(MicrophoneSampleRate, 16, 1),
            BufferMilliseconds = 100,
        };
        waveIn.DataAvailable += (_, args) => chunks.Add(args.Buffer.AsSpan(0, args.BytesRecorded).ToArray());
        waveIn.StartRecording();
    }

    public override void Stop()
    {
        waveIn?.StopRecording();
        waveIn?.Dispose();
        waveIn = null;
        while (chunks.TryTake(out _))
        {
        }
    }

    public override int Read()
    {
        if (!chunks.TryTake(out byte[]? chunk, 50))
        {
            return NoData;
        }

        Buffer = chunk;
        return chunk.Length;
    }

    public override void Dispose()
    {
        Stop();
        chunks.Dispose();
    }
}

internal static class AudioConverter
{
    public static short ReadMonoSample(byte[] data, int block, SpeechAudioFormatInfo format)
    {
        int offset = block * format.BlockAlign;
        int bytesPerChannelSample = format.BitsPerSample / 8;
        int sum = 0;
        for (int channel = 0; channel < format.ChannelCount; channel++)
        {
            sum += ReadSample(data.AsSpan(offset + (channel * bytesPerChannelSample)), format);
        }

        return (short)(sum / format.ChannelCount);
    }

    private static short ReadSample(ReadOnlySpan<byte> data, SpeechAudioFormatInfo format)
    {
        return format.EncodingFormat switch
        {
            EncodingFormat.ULaw => DecodeULaw(data[0]),
            EncodingFormat.ALaw => DecodeALaw(data[0]),
            _ when format.BitsPerSample == 8 => (short)((data[0] - 128) << 8),
            _ => BinaryPrimitives.ReadInt16LittleEndian(data),
        };
    }

    internal static short DecodeULaw(byte encoded)
    {
        int value = ~encoded & 0xFF;
        int magnitude = (((value & 0x0F) << 3) + 0x84) << ((value & 0x70) >> 4);
        return (short)((value & 0x80) != 0 ? 0x84 - magnitude : magnitude - 0x84);
    }

    internal static short DecodeALaw(byte encoded)
    {
        int value = encoded ^ 0x55;
        int segment = (value & 0x70) >> 4;
        int magnitude = (value & 0x0F) << 4;
        magnitude = segment == 0 ? magnitude + 8 : (magnitude + 0x108) << (segment - 1);
        return (short)((value & 0x80) != 0 ? magnitude : -magnitude);
    }
}

internal static class WaveReader
{
    /// <summary>Parses a RIFF header, leaving the stream positioned at the start of the audio data.</summary>
    public static SpeechAudioFormatInfo ReadHeader(Stream stream)
    {
        using BinaryReader reader = new(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        try
        {
            if (new string(reader.ReadChars(4)) != "RIFF")
            {
                throw new FormatException("The stream is not a RIFF file.");
            }

            reader.ReadInt32();
            if (new string(reader.ReadChars(4)) != "WAVE")
            {
                throw new FormatException("The stream is not a WAVE file.");
            }

            SpeechAudioFormatInfo? format = null;
            while (true)
            {
                string chunkId = new(reader.ReadChars(4));
                int chunkSize = reader.ReadInt32();
                if (chunkId == "data")
                {
                    return format ?? throw new FormatException("The WAVE file has no format chunk before its data.");
                }

                if (chunkId == "fmt ")
                {
                    format = ReadFormat(reader, chunkSize);
                }
                else
                {
                    // Chunks are word aligned, so odd sizes have a padding byte.
                    reader.ReadBytes(chunkSize + (chunkSize & 1));
                }
            }
        }
        catch (EndOfStreamException exception)
        {
            throw new FormatException("The WAVE file is truncated.", exception);
        }
    }

    private static SpeechAudioFormatInfo ReadFormat(BinaryReader reader, int chunkSize)
    {
        const int WaveFormatExtensible = 0xFFFE;

        int tag = reader.ReadUInt16();
        int channels = reader.ReadUInt16();
        int sampleRate = reader.ReadInt32();
        int bytesPerSecond = reader.ReadInt32();
        int blockAlign = reader.ReadUInt16();
        int bitsPerSample = reader.ReadUInt16();
        byte[] extra = reader.ReadBytes((chunkSize - 16) + (chunkSize & 1));

        if (tag == WaveFormatExtensible && extra.Length >= 10)
        {
            // cbSize (2), valid bits (2), channel mask (4), then the SubFormat GUID whose first two bytes are the tag.
            tag = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(8));
        }

        if (tag is not ((int)EncodingFormat.Pcm or (int)EncodingFormat.ALaw or (int)EncodingFormat.ULaw))
        {
            throw new FormatException($"WAVE format tag {tag} is not supported.");
        }

        return new SpeechAudioFormatInfo((EncodingFormat)tag, sampleRate, bitsPerSample, channels, bytesPerSecond, blockAlign, null);
    }
}
