using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CommonFury.Audio;

/// <summary>Decodes WAV, FLAC, MP3 and Ogg Vorbis with the bundled native miniaudio library; instances are stateless and can be shared across threads.</summary>
/// <remarks>
/// WAV covers PCM, IEEE float, A-law, µ-law and ADPCM encodings. MP3 covers MPEG-1/2/2.5 Layer III, with or without
/// ID3v2 tags, and decodes a run of as little as one frame; untagged input is not trimmed, so each frame yields its full
/// sample count. Other formats (AAC/M4A, WMA, Opus, AIFF) throw <see cref="NotSupportedException"/>; convert them to
/// WAV, MP3, Ogg Vorbis or FLAC, or supply another <see cref="IAudioDecoder"/>. <c>maximumBytes</c> caps the encoded
/// input and the decoded PCM16 output.
/// </remarks>
public sealed partial class MiniaudioDecoder(int maximumBytes = 256 * 1024 * 1024) : IAudioDecoder
{
    private const string Library = "commonfury_audio";
    private const int ChunkSamples = 65536;
    private const string ConvertAdvice = "convert it to WAV, MP3, Ogg Vorbis or FLAC";

    private enum Encoding { Wav = 1, Flac = 2, Vorbis = 3, Mp3 = 4 }

    public int MaximumBytes { get; } = maximumBytes > 0 ? maximumBytes : throw new ArgumentOutOfRangeException(nameof(maximumBytes));

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">The format is not WAV, FLAC, MP3 or Ogg Vorbis, or the channel conversion is unsupported.</exception>
    /// <exception cref="InvalidDataException">The input cannot be decoded, or the decoded audio exceeds <see cref="MaximumBytes"/>.</exception>
    public unsafe PcmAudio Decode(ReadOnlySpan<byte> data, string? format = null, int? sampleRate = null, int? channels = null)
    {
        if (data.IsEmpty)
            throw new ArgumentException("Audio input is empty.", nameof(data));
        if (data.Length > MaximumBytes)
            throw new ArgumentException("Audio input exceeds the configured limit.", nameof(data));
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is <= 0 or > ushort.MaxValue / 2)
            throw new ArgumentOutOfRangeException(nameof(channels));
        Encoding encoding = format is null ? Detect(data) : Parse(format);
        if (encoding == Encoding.Vorbis && IsOpus(data))
            throw new NotSupportedException($"Ogg Opus audio is not supported; {ConvertAdvice}.");

        var samples = new ArrayBufferWriter<float>();
        int sourceChannels, sourceRate;
        fixed (byte* input = data)
        {
            int status = Open(input, (nuint)data.Length, (int)encoding, out nint decoder, out uint nativeChannels, out uint nativeRate);
            if (status != 0)
                throw new InvalidDataException($"The audio could not be decoded as {encoding} (miniaudio result {status}).");
            try
            {
                if (nativeChannels is < 1 or > ushort.MaxValue / 2 || nativeRate is < 1 or > int.MaxValue)
                    throw new InvalidDataException("Audio has invalid sample-rate/channel metadata.");
                (sourceChannels, sourceRate) = ((int)nativeChannels, (int)nativeRate);
                long limit = MaximumBytes / 2;
                while (true)
                {
                    Span<float> chunk = samples.GetSpan(Math.Max(ChunkSamples, sourceChannels));
                    long read;
                    fixed (float* output = chunk)
                        read = Read(decoder, output, (ulong)(chunk.Length / sourceChannels));
                    if (read < 0)
                        throw new InvalidDataException($"The {encoding} audio is corrupt (miniaudio result {read}).");
                    if (read == 0)
                        break;
                    int count = (int)read * sourceChannels;
                    if (count > limit - samples.WrittenCount)
                        throw new InvalidDataException("Decoded audio exceeds the configured limit.");
                    foreach (float sample in chunk[..count])
                        if (!float.IsFinite(sample))
                            throw new InvalidDataException("Audio contains non-finite samples.");
                    samples.Advance(count);
                }
            }
            finally
            {
                Close(decoder);
            }
        }

        int targetRate = sampleRate ?? sourceRate, targetChannels = channels ?? sourceChannels;
        long outputFrames = ((long)(samples.WrittenCount / sourceChannels) * targetRate + sourceRate - 1) / sourceRate;
        if (outputFrames > MaximumBytes / (targetChannels * 2))
            throw new InvalidDataException("Decoded audio exceeds the configured limit.");
        return PcmAudio.FromFloat(samples.WrittenSpan, sourceRate, sourceChannels, targetRate, targetChannels);
    }

    private static Encoding Parse(string format) => format.TrimStart('.').ToLowerInvariant() switch
    {
        "wav" or "wave" => Encoding.Wav,
        "flac" => Encoding.Flac,
        "ogg" or "oga" or "vorbis" => Encoding.Vorbis,
        "mp3" => Encoding.Mp3,
        _ => throw new NotSupportedException($"Audio format '{format}' is not supported; {ConvertAdvice}."),
    };

    private static Encoding Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 12 && (data[..4].SequenceEqual("RIFF"u8) || data[..4].SequenceEqual("RF64"u8)) && data.Slice(8, 4).SequenceEqual("WAVE"u8))
            return Encoding.Wav;
        if (data.StartsWith("fLaC"u8))
            return Encoding.Flac;
        if (data.StartsWith("OggS"u8))
            return Encoding.Vorbis;
        if (data.StartsWith("ID3"u8) || IsMpegLayer3Header(data))
            return Encoding.Mp3;
        throw new NotSupportedException($"The audio format was not recognized; {ConvertAdvice}.");
    }

    // Frame sync, a defined MPEG version, Layer III, and a bitrate and sample-rate index that are not reserved.
    // ADTS AAC shares the sync but always has layer bits 00.
    private static bool IsMpegLayer3Header(ReadOnlySpan<byte> data)
        => data.Length >= 4 && data[0] == 0xff && (data[1] & 0xe0) == 0xe0 && (data[1] & 0x18) != 0x08 && (data[1] & 0x06) == 0x02
            && (data[2] & 0xf0) != 0xf0 && (data[2] & 0x0c) != 0x0c;

    // The first Ogg page holds a single identification packet after a 27-byte header and its segment table.
    private static bool IsOpus(ReadOnlySpan<byte> data)
        => data.Length >= 27 && data.StartsWith("OggS"u8) && data.Length >= 27 + data[26] + 8 && data.Slice(27 + data[26], 8).SequenceEqual("OpusHead"u8);

    [LibraryImport(Library, EntryPoint = "cfa_decoder_open")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int Open(byte* data, nuint size, int encoding, out nint decoder, out uint channels, out uint sampleRate);

    [LibraryImport(Library, EntryPoint = "cfa_decoder_read")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial long Read(nint decoder, float* output, ulong frames);

    [LibraryImport(Library, EntryPoint = "cfa_decoder_close")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void Close(nint decoder);
}
