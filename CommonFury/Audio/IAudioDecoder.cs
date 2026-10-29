namespace CommonFury.Audio;

/// <summary>Decodes encoded audio to PCM16. Input is borrowed for each call; the returned audio is owned by the caller.</summary>
public interface IAudioDecoder
{
    /// <summary>Decodes <paramref name="data"/> and converts it to the requested rate and channel count when given.</summary>
    /// <remarks>
    /// <c>format</c> is a file extension or format name, with or without a leading dot; when null, the format is detected
    /// from the content. Implementations follow the conversion rules of <see cref="PcmAudio.Convert"/>.
    /// </remarks>
    PcmAudio Decode(ReadOnlySpan<byte> data, string? format = null, int? sampleRate = null, int? channels = null);
}
