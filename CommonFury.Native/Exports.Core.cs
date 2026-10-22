using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CommonFury.Hashing;

namespace CommonFury.Native;

internal static unsafe partial class Exports
{
    [UnmanagedCallersOnly(EntryPoint = "cf_abi_version", CallConvs = [typeof(CallConvCdecl)])]
    internal static uint Version() => 0x00010000;

    [UnmanagedCallersOnly(EntryPoint = "cf_error_copy", CallConvs = [typeof(CallConvCdecl)])]
    internal static int ErrorCopy(byte* destination, ulong capacity, ulong* required)
    {
        try
        {
            if (required == null)
                return (int)Status.InvalidArgument;
            string message = Abi.Error ?? "";
            int bytes = Abi.Utf8.GetByteCount(message);
            *required = (ulong)bytes + 1;
            if (capacity < *required)
                return (int)Status.BufferTooSmall;
            if (destination == null)
                return (int)Status.InvalidArgument;
            Abi.Utf8.GetBytes(message, new Span<byte>(destination, bytes));
            destination[bytes] = 0;
            return (int)Status.Ok;
        }
        catch { return (int)Status.InternalError; }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_buffer_release", CallConvs = [typeof(CallConvCdecl)])]
    internal static int BufferRelease(ulong handle)
    {
        try { Abi.Release(handle); return Abi.Success(); }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_jenkins_hash", CallConvs = [typeof(CallConvCdecl)])]
    internal static int JenkinsHash(byte* text, ulong length, uint* result)
    {
        try { Abi.RequireOutput(result); *result = Jenkins.HashUtf8(Abi.Input(text, length)); return Abi.Success(); }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_jenkins_partial", CallConvs = [typeof(CallConvCdecl)])]
    internal static int JenkinsPartial(byte* text, ulong length, uint* result)
    {
        try { Abi.RequireOutput(result); *result = Jenkins.PartialUtf8(Abi.Input(text, length)); return Abi.Success(); }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_jenkins_continue", CallConvs = [typeof(CallConvCdecl)])]
    internal static int JenkinsContinue(uint state, byte* text, ulong length, uint* result)
    {
        try { Abi.RequireOutput(result); *result = Jenkins.ContinueUtf8(state, Abi.Input(text, length)); return Abi.Success(); }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_jenkins_finalize", CallConvs = [typeof(CallConvCdecl)])]
    internal static uint JenkinsFinalize(uint state) => Jenkins.Finalize(state);

    [UnmanagedCallersOnly(EntryPoint = "cf_jenkins_hash_case_sensitive", CallConvs = [typeof(CallConvCdecl)])]
    internal static int JenkinsCaseSensitive(byte* text, ulong length, uint* result)
    {
        try { Abi.RequireOutput(result); *result = Jenkins.HashCaseSensitive(Abi.Text(text, length)); return Abi.Success(); }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_pso_checksum", CallConvs = [typeof(CallConvCdecl)])]
    internal static int PsoChecksum(byte* data, ulong length, uint* result)
    {
        try { Abi.RequireOutput(result); *result = Jenkins.PsoChecksum(Abi.Input(data, length)); return Abi.Success(); }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_lookup3_words", CallConvs = [typeof(CallConvCdecl)])]
    internal static int Lookup3Words(uint* words, ulong count, uint seed, uint* result)
    {
        try { Abi.RequireOutput(result); *result = Jenkins.Lookup3Words(Abi.Output(words, count), seed); return Abi.Success(); }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_one_at_a_time_words", CallConvs = [typeof(CallConvCdecl)])]
    internal static int OneAtATimeWords(byte* data, ulong length, uint seed, uint* result)
    {
        try { Abi.RequireOutput(result); *result = Jenkins.OneAtATimeWords(Abi.Input(data, length), seed); return Abi.Success(); }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_elf_hash", CallConvs = [typeof(CallConvCdecl)])]
    internal static int Elf(byte* text, ulong length, uint* result)
    {
        try { Abi.RequireOutput(result); *result = ElfHash.Hash(Abi.Text(text, length)); return Abi.Success(); }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_fletcher32", CallConvs = [typeof(CallConvCdecl)])]
    internal static int Fletcher32(byte* data, ulong length, uint* result)
    {
        try { Abi.RequireOutput(result); *result = Checksums.Fletcher32(Abi.Input(data, length)); return Abi.Success(); }
        catch (Exception error) { return Abi.Fail(error); }
    }
}
