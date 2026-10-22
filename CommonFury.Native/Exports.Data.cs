using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CommonFury.Compression;
using CommonFury.Pso;
using CommonFury.Rbf;

namespace CommonFury.Native;

internal static unsafe partial class Exports
{
    private const uint PsoAllowAliasedSchema = 1, PsoForceBlockId12 = 2, PsoForceBlockId20 = 4;

    [UnmanagedCallersOnly(EntryPoint = "cf_deflate_compress", CallConvs = [typeof(CallConvCdecl)])]
    internal static int DeflateCompress(byte* data, ulong length, uint level, Buffer* result)
    {
        try
        {
            Abi.RequireOutput(result);
            *result = default;
            if (level > (uint)CompressionLevel.SmallestSize)
                throw new ArgumentOutOfRangeException(nameof(level));
            *result = Abi.Copy(RawDeflate.Compress(Abi.Input(data, length), (CompressionLevel)level));
            return Abi.Success();
        }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_deflate_decompress", CallConvs = [typeof(CallConvCdecl)])]
    internal static int DeflateDecompress(byte* data, ulong length, uint maximumLength, uint zeroHistory, Buffer* result)
    {
        try
        {
            Abi.RequireOutput(result);
            *result = default;
            *result = Abi.Copy(RawDeflate.Decompress(Abi.Input(data, length), checked((int)maximumLength), zeroHistory != 0));
            return Abi.Success();
        }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_deflate_decompress_exact", CallConvs = [typeof(CallConvCdecl)])]
    internal static int DeflateDecompressExact(byte* data, ulong length, byte* destination, ulong destinationLength, uint zeroHistory)
    {
        try
        {
            RawDeflate.DecompressExact(Abi.Input(data, length), Abi.Output(destination, destinationLength), zeroHistory != 0);
            return Abi.Success();
        }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_rbf_validate", CallConvs = [typeof(CallConvCdecl)])]
    internal static int RbfValidate(byte* data, ulong length, Buffer* diagnostics)
    {
        try
        {
            Abi.RequireOutput(diagnostics);
            *diagnostics = default;
            ValidationReport report = RbfDocument.Read(Abi.Input(data, length)).Validate();
            *diagnostics = Abi.Diagnostics(report);
            if (report.IsValid)
                return Abi.Success();
            Abi.Error = "RBF validation failed; inspect the returned diagnostics.";
            return (int)Status.ValidationFailed;
        }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_rbf_rewrite", CallConvs = [typeof(CallConvCdecl)])]
    internal static int RbfRewrite(byte* data, ulong length, Buffer* result)
    {
        try
        {
            Abi.RequireOutput(result);
            *result = default;
            *result = Abi.Copy(RbfDocument.Read(Abi.Input(data, length)).Write());
            return Abi.Success();
        }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_pso_get_info", CallConvs = [typeof(CallConvCdecl)])]
    internal static int PsoGetInfo(byte* data, ulong length, uint flags, PsoInfo* info, uint size)
    {
        try
        {
            Abi.Structure(info, size);
            PsoFile file = PsoFile.Read(Abi.Input(data, length), PsoOptions(flags));
            *info = new()
            {
                PointerLayout = file.PointerLayout == PsoPointerLayout.BlockId20 ? 20u : 12u,
                Sections = (uint)file.Sections.Count,
                Blocks = (uint)file.Blocks.Count,
                RootBlockId = (uint)file.RootBlockId,
                Structs = (uint)file.Schema.Types.Count(type => type is PsoStruct),
                Enums = (uint)file.Schema.Types.Count(type => type is PsoEnum)
            };
            return Abi.Success();
        }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_pso_rewrite", CallConvs = [typeof(CallConvCdecl)])]
    internal static int PsoRewrite(byte* data, ulong length, uint flags, Buffer* result)
    {
        try
        {
            Abi.RequireOutput(result);
            *result = default;
            *result = Abi.Copy(PsoFile.Read(Abi.Input(data, length), PsoOptions(flags)).Write());
            return Abi.Success();
        }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_pso_validate", CallConvs = [typeof(CallConvCdecl)])]
    internal static int PsoValidate(byte* data, ulong length, uint flags, Buffer* diagnostics)
    {
        try
        {
            Abi.RequireOutput(diagnostics);
            *diagnostics = default;
            PsoDocument document = PsoFile.Read(Abi.Input(data, length), PsoOptions(flags)).ReadRoot(new(continueOnFieldErrors: true));
            ValidationReport report = document.Report;
            *diagnostics = Abi.Diagnostics(report);
            if (report.IsValid)
                return Abi.Success();
            Abi.Error = "PSO validation failed; inspect the returned diagnostics.";
            return (int)Status.ValidationFailed;
        }
        catch (Exception error) { return Abi.Fail(error); }
    }

    private static PsoFileOptions PsoOptions(uint flags)
    {
        if ((flags & ~(PsoAllowAliasedSchema | PsoForceBlockId12 | PsoForceBlockId20)) != 0
            || (flags & (PsoForceBlockId12 | PsoForceBlockId20)) == (PsoForceBlockId12 | PsoForceBlockId20))
            throw new ArgumentOutOfRangeException(nameof(flags));
        return new()
        {
            AllowAliasedSchemaDefinitions = (flags & PsoAllowAliasedSchema) != 0,
            PointerLayout = (flags & PsoForceBlockId12) != 0 ? PsoPointerLayout.BlockId12
                : (flags & PsoForceBlockId20) != 0 ? PsoPointerLayout.BlockId20 : null
        };
    }
}
