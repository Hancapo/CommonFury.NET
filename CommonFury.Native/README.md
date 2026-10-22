# CommonFury C ABI

`commonfury_native` is a Native AOT shared library over CommonFury. ABI 1.0 (`0x00010000`), verified on **Windows x64**.
Callers load it in-process; no .NET runtime, SDK or JIT is needed. The contract is [include/commonfury.h](include/commonfury.h):
cdecl exports, status codes, per-thread error text, and output buffers owned by the library until `cf_buffer_release`.

```powershell
dotnet publish CommonFury.Native/CommonFury.Native.csproj -c Release -r win-x64 -o artifacts/native/win-x64
python CommonFury.Native/tests/test_native.py
```

Ship `commonfury_native.dll` and the header; the `.pdb` and `.xml` files are only for debugging and IntelliSense.
Publishing needs the MSVC linker. From Git Bash, put `C:\Program Files (x86)\Microsoft Visual Studio\Installer` on `PATH`
so the build can find `vswhere.exe`.

## Exported functionality

| Area | Exports |
|---|---|
| Core | `cf_abi_version`, `cf_error_copy`, `cf_buffer_release` |
| Hashing | Jenkins (`hash`, `partial`, `continue`, `finalize`, `hash_case_sensitive`), `cf_pso_checksum`, `cf_lookup3_words`, `cf_one_at_a_time_words`, `cf_elf_hash`, `cf_fletcher32` |
| DEFLATE | `cf_deflate_compress`, `cf_deflate_decompress`, `cf_deflate_decompress_exact` (strict framing, optional zero window) |
| Textures | `cf_dds_read`/`write` (2D, cube, volume, arrays), `cf_texture_data_size`, `cf_pixel_decode`/`encode`, `cf_bc1_encode`/`decode` |
| RBF | `cf_rbf_validate` (JSON diagnostics), `cf_rbf_rewrite` |
| PSO | `cf_pso_get_info`, `cf_pso_validate`, `cf_pso_rewrite`, for 12-bit (GTA V) and 20-bit (RDR2) pointer layouts |
| Height maps | `cf_heightmap_read`/`write`: all cell types, layouts, byte orders, water masks |
| Vertices | `cf_vertex_format_size`, `cf_vertex_decode`/`encode` by DXGI format, with an explicit range for DXGI 24 |
| BVH | `cf_bvh_build` with the split rule and leaf-id meaning of either game |

Not exported: APIs that need game-supplied tables or live .NET objects. These are animation sequences (they need a
game-supplied group table and compact codec), PSO/RBF graph editing, archive caches and byte sources, XML helpers
and DLC tokens. Those remain available to .NET callers.

## Size

Every row passes the native test suite. Settings accumulate, Windows x64, .NET 10; sizes shift by a few KB as the library changes:

| Configuration | DLL bytes |
|---|---|
| Default Native AOT | 2,224,128 |
| + `OptimizationPreference=Size` | 1,981,952 |
| + `InvariantGlobalization` | 1,866,752 |
| + debugger, EventSource, hot-reload and HTTP feature switches off, identical-method folding | 1,848,320 |
| + `StackTraceSupport=false` (**project default**) | **1,722,368** |
| + `UseSystemResourceKeys` (not used: framework error text becomes resource keys) | 1,704,960 |
| + `IlcDisableReflection` (not used: unsupported, no gain) | 1,704,960 |

For reference, an empty Native AOT library with one export and the same settings is 727,040 bytes: that is the runtime floor.
CommonFury's own compiled code is about 157 KB; the rest of the ~1 MB above the floor is framework code it uses
(collections, LINQ, number formatting, JSON diagnostics, DEFLATE/zlib).
