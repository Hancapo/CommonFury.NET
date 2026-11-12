using System.Runtime.InteropServices;

namespace CommonFury.Native;

// The host process resolves commonfury_texture from its own directories, not from the one holding this library. Loading
// it once by full path from beside this library makes CommonFury's later loads by name reuse that module.
internal static unsafe partial class TextureLibrary
{
    private const uint FromAddress = 0x4, UnchangedRefCount = 0x2;
    private static volatile bool loaded;

    internal static void EnsureLoaded()
    {
        if (loaded || !OperatingSystem.IsWindows())
            return;
        if (GetModuleHandleExW(FromAddress | UnchangedRefCount, (nint)(delegate* unmanaged[Cdecl]<uint>)&Exports.Version, out nint module))
        {
            char* path = stackalloc char[32768];
            uint length = GetModuleFileNameW(module, path, 32768);
            if (length is > 0 and < 32768 && Path.GetDirectoryName(new string(path, 0, (int)length)) is { } directory)
                NativeLibrary.TryLoad(Path.Combine(directory, "commonfury_texture.dll"), out _);
        }
        loaded = true;
    }

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetModuleHandleExW(uint flags, nint address, out nint module);

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial uint GetModuleFileNameW(nint module, char* path, uint size);
}
