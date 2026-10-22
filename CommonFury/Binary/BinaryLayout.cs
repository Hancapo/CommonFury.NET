namespace CommonFury.Binary;

/// <summary>Checks binary ranges and computes alignment for bounded readers and writers.</summary>
public static class BinaryLayout
{
    /// <summary>Rounds a nonnegative value up to a multiple of any positive alignment; throws on overflow.</summary>
    public static int Align(int value, int alignment) => checked((int)Align((long)value, alignment));

    /// <inheritdoc cref="Align(int, int)"/>
    public static long Align(long value, int alignment)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);
        long remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    /// <summary>Throws <see cref="InvalidDataException"/> when the range lies outside <paramref name="data"/>.</summary>
    public static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
            throw new InvalidDataException($"Binary range {offset}+{length} exceeds {data.Length} bytes.");
        return data.Slice(offset, length);
    }

    /// <summary>Throws <see cref="InvalidDataException"/> unless the range lies inside <paramref name="total"/> bytes; overflow-safe.</summary>
    public static void RequireRange(long offset, long length, long total)
    {
        if (offset < 0 || length < 0 || offset > total || length > total - offset)
            throw new InvalidDataException($"Binary range {offset}+{length} lies outside its {total}-byte allocation.");
    }

    /// <summary>Throws <see cref="InvalidDataException"/> with <paramref name="message"/> when <paramref name="condition"/> is false.</summary>
    public static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
