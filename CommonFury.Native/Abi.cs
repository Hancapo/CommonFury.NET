using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CommonFury.Native;

internal enum Status { Ok, InvalidArgument, InvalidData, Unsupported, ValidationFailed, BufferTooSmall, OutOfMemory, InternalError, InvalidHandle }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Buffer
{
    internal ulong Handle;
    internal byte* Data;
    internal ulong Length;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TextureInfo
{
    internal uint Format, Width, Height, MipCount, Dimension, Depth, ArraySize, Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PsoInfo
{
    internal uint PointerLayout, Sections, Blocks, RootBlockId, Structs, Enums;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct HeightMapInfo
{
    internal uint Columns, Rows, CellSize, Flags, Version, Layout, ByteOrder, Reserved, HasWaterMask, Padding;
    internal fixed float Minimum[3];
    internal fixed float Maximum[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct BvhNode
{
    internal fixed float Minimum[3];
    internal fixed float Maximum[3];
    internal ushort ItemId;
    internal byte ItemCount, Reserved;
}

internal sealed class AbiException(Status status, string message) : Exception(message)
{
    internal Status Status { get; } = status;
}

// Owned output buffers and per-thread error text. Inputs are borrowed only for the duration of a call.
internal static unsafe class Abi
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Lock gate = new();
    private static readonly Dictionary<ulong, nint> buffers = [];
    private static ulong next;
    [ThreadStatic] internal static string? Error;

    internal static Buffer Copy(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return default;
        void* pointer = NativeMemory.Alloc((nuint)bytes.Length);
        try
        {
            bytes.CopyTo(new Span<byte>(pointer, bytes.Length));
            lock (gate)
            {
                if (next == ulong.MaxValue)
                    throw new InvalidOperationException("Buffer identifiers exhausted.");
                ulong id = ++next;
                buffers.Add(id, (nint)pointer);
                return new() { Handle = id, Data = (byte*)pointer, Length = (ulong)bytes.Length };
            }
        }
        catch { NativeMemory.Free(pointer); throw; }
    }

    internal static void Release(ulong id)
    {
        if (id == 0)
            return;
        nint pointer;
        lock (gate)
        {
            if (!buffers.Remove(id, out pointer))
                throw new AbiException(Status.InvalidHandle, "Unknown or released buffer handle.");
        }
        NativeMemory.Free((void*)pointer);
    }

    internal static ReadOnlySpan<byte> Input(byte* pointer, ulong length)
    {
        if (length > int.MaxValue || pointer == null && length != 0)
            throw new ArgumentException("Invalid pointer/byte-length pair.");
        return new(pointer, (int)length);
    }

    internal static Span<T> Output<T>(T* pointer, ulong count) where T : unmanaged
    {
        if (count > int.MaxValue / (ulong)sizeof(T) || pointer == null && count != 0)
            throw new ArgumentException("Invalid pointer/element-count pair.");
        return new(pointer, (int)count);
    }

    internal static string Text(byte* pointer, ulong length)
    {
        try { return Utf8.GetString(Input(pointer, length)); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Text is not valid UTF-8."); }
    }

    internal static void RequireOutput(void* pointer)
    {
        if (pointer == null)
            throw new ArgumentNullException(nameof(pointer), "Missing output pointer.");
    }

    // Pass sizeof(*out); a mismatched size fails without touching the structure.
    internal static void Structure<T>(T* pointer, uint size) where T : unmanaged
    {
        RequireOutput(pointer);
        if (size != sizeof(T))
            throw new ArgumentException("Incorrect ABI structure size.");
    }

    internal static Buffer Diagnostics(ValidationReport report)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (Diagnostic item in report.Diagnostics)
            {
                writer.WriteStartObject();
                writer.WriteString("code", item.Code);
                writer.WriteString("message", item.Message);
                writer.WriteString("path", item.Path);
                writer.WriteString("severity", item.Severity switch
                {
                    DiagnosticSeverity.Info => "info",
                    DiagnosticSeverity.Warning => "warning",
                    _ => "error"
                });
                writer.WriteString("scope", item.Scope == DiagnosticScope.Rewrite ? "rewrite" : "general");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Copy(stream.ToArray());
    }

    internal static int Success() { Error = null; return (int)Status.Ok; }

    internal static int Fail(Exception error)
    {
        if (error is OutOfMemoryException)
        { Error = "Out of memory."; return (int)Status.OutOfMemory; }
        try { Error = error.Message; }
        catch { Error = "Native operation failed."; }
        return (int)(error switch
        {
            AbiException failure => failure.Status,
            ValidationException => Status.ValidationFailed,
            ArgumentException or OverflowException => Status.InvalidArgument,
            InvalidDataException or FormatException or EndOfStreamException => Status.InvalidData,
            NotSupportedException => Status.Unsupported,
            _ => Status.InternalError
        });
    }
}
