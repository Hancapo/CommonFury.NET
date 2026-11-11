<img src="assets/icon.svg" width="64" height="64" alt="">

# CommonFury

A .NET 10 library of building blocks for 3D and 2D asset work: binary I/O, hashing, DEFLATE and Zstandard, math and geometry,
collision hierarchies, textures (DDS, DXGI pixel formats, BC1, ASTC decoding, block-linear layouts), vertex data, skinning,
animation sequences, height maps and PCM audio, plus readers and writers for RAGE formats such as PSO, RBF and data XML.
Managed, AOT-compatible, with no native dependencies.

Nothing here is tied to a particular game. Where formats vary between uses (pointer widths, name tables, limits, codecs, memory
layouts), the caller passes the choice in. Readers treat input as untrusted: sizes are checked, decompression is capped and
malformed data fails with `InvalidDataException` or a diagnostic.

## Contents

| Namespace | What it covers | Callers supply |
|---|---|---|
| `CommonFury` | Validation results: `Diagnostic`, `ValidationReport`, `ValidationException` and their severity, scope and purpose enums | |
| `CommonFury.Binary` | Bounded span readers and writers (`BinarySpanReader`/`Writer`), `BinaryStreamWriter`, primitive arrays (`BinaryArrays`), LSB- and MSB-first bit readers and writers, `BitField32`, range and alignment checks (`BinaryLayout`) | byte order |
| `CommonFury.Hashing` | Jenkins one-at-a-time and lookup3 (`Jenkins`), name hashes with optional spellings (`Hash32`, `PartialHash32`, `HashText`, `HashResolver`), `ElfHash`, Fletcher-32 (`Checksums`), `XxHash64` | |
| `CommonFury.Compression` | Raw DEFLATE compression and decompression (`RawDeflate`, with optional output reuse through `CompressionCache`); Zstandard decompression from spans or streams, with dictionaries and content checksums (`Zstandard`, `ZstandardDictionary`, `ZstandardFrameHeader`). There is no Zstandard encoder | output size limit; zero-history window when the format needs it; Zstandard dictionary |
| `CommonFury.IO` | Atomic file writes (`AtomicFile`), logical asset and device paths (`AssetPaths`, `VirtualPaths`), asset names (`AssetNames`), deterministic directory walks (`FileTree`) | |
| `CommonFury.Archives` | Positional random-access reads (`ByteSource`, `StreamByteSource`, `FileByteSource`), range streams (`ByteSliceStream`), an LRU of open archives (`ArchiveHandleCache<T>`) | archive open and size callbacks |
| `CommonFury.Numerics` | Vector, quaternion, matrix and transform helpers; bounds (`Aabb2`, `Aabb3`, `Sphere3`), `Matrix3x4`, `Vector3D`; quantization, bounds quantization and packed GPU vectors; CPU skinning (`SkinningMath`, `SkinningBatch`); curves, joint frames, exact integer grids, pixel channel math (`PixelMath`) | grid steps, bounds and bit widths; influence counts |
| `CommonFury.Geometry` | Mesh validation, normals and tangents (`MeshMath`); strip and fan conversion and splitting by vertex count (`MeshPartitioning`); attribute- and seam-aware edge-collapse simplification onto source vertices (`MeshSimplification`); polygons, plane fitting, height fields, rasterization and grid filters, `SpatialGrid2`, shape areas and bounds, mass properties | vertex limits for splitting; up and fallback axes for plane fits; simplification targets, attribute weights and locks |
| `CommonFury.Collisions` | Median-split BVH building (`BvhBuilder`, `BvhBuild`) and 16-bit quantized nodes (`BvhQuantization`, `QuantizedBvhNode`); polygon record kinds (`BoundPolygonKind`) | split rule, leaf ids, quantization rounding |
| `CommonFury.Textures` | DXGI format identities and tightly packed layouts (`TextureFormat(s)`, `TextureLayout`, `TextureShape`, `TextureSurface`); DDS reading and writing (`DdsCodec`); uncompressed pixel conversion (`PixelCodec`); BC1 encoding and decoding (`Bc1`); decoding of every 2D ASTC footprint to RGBA8 or half floats (`Astc`, `AstcFormat`); block-linear swizzling, mip chains and layer padding (`BlockLinear`, `BlockLinearSurface`, `BlockLinearLayout`, `TextureElement`) | ASTC profile; GOB layout and block height for block-linear data |
| `CommonFury.Audio` | Owned PCM16 audio (`PcmAudio`): WAV reading and writing, sample-rate and channel conversion, float input, peak data; `IAudioDecoder` for compressed formats | a decoder for compressed formats |
| `CommonFury.Vertices` | Vertex element formats and their codec (`VertexElementFormat(s)`, `VertexElementCodec`), declaration semantics (`VertexSemantic`), gen9 vertex declarations (`Gen9VertexDeclaration`) | DXGI 24 range (`Rgb10A2Range`) |
| `CommonFury.Animations` | Animation sequence blocks read, written and validated in uncompressed or compact storage: static, raw, quantized, indirect, linear and quaternion-operation channels, `SequenceTrack`, `AnimationSequence`, `AnimationSequenceFormat`, `AnimationChannelGroupTable` | group table, linear encoding, hash policy, compact codec |
| `CommonFury.HeightMaps` | Minimum and maximum height grids (`HeightMap<T>`) read and written with any cell type, layout, byte order and water mask (`HeightMapCodec`), validated against export limits | target export limits |
| `CommonFury.Resources` | Tagged resource pointers (`ResourcePointer`, `ResourceSection`) | whether pointer upper bits carry metadata |
| `CommonFury.Pso` | PSO containers that rewrite byte for byte (`PsoFile`); decoded graphs with typed values (`PsoDocument`, `PsoNode`, `PsoValue`s, `PsoMap`); schemas (`PsoSchema`); building and graph writing (`PsoBuilder`, `PsoGraphWriter`); name tables (`PsoNameTable`, `PsoHashStrings`) | name resolver; pointer layout when not detected; read limits |
| `CommonFury.Rbf` | RBF documents read, written and validated (`RbfDocument`), with nodes, values, `RbfLimits`, `RbfDescriptor` and `RbfRecordType` | |
| `CommonFury.Xml` | Bounded XML loading and canonical writing (`XmlDocumentCodec`), strict element reading and building (`XmlElementReader`, `XmlElements`), `platform="…"` selectors (`XmlPlatformSelection`) | platform names |
| `CommonFury.Dlc` | Pack-type, content-group, data-file type and contents tokens that keep unrecognized values (`DlcPackType`, `DlcContentGroup`, `DlcDataFileType`, `DlcDataFileContents`) | additional token values, as C# 14 extension members |
| `CommonFury.Assets` | `DrawableLod`, `BoneChannelFlags` (bits 0–10), `YmapFlags`, `YmapContentFlags` | |

Each namespace with diagnostics has its own stable codes (`PsoDiagnosticCodes`, `RbfDiagnosticCodes`, `XmlDiagnosticCodes`,
`HeightMapDiagnosticCodes`, `AnimationDiagnosticCodes`).

## Native library

`CommonFury.Native` publishes `commonfury_native.dll`, a Native AOT C ABI (1.7 MB on win-x64) over a subset of the library: hashing,
DEFLATE, DDS, pixel and BC1 conversion, RBF and PSO validation and rewriting, height maps, vertex elements and BVH building. See
[CommonFury.Native/README.md](CommonFury.Native/README.md) for the exports and what stays .NET-only.

## Audio decoder

`CommonFury.Miniaudio` adds `MiniaudioDecoder`, an `IAudioDecoder` for WAV, FLAC, MP3 and Ogg Vorbis backed by a native
miniaudio build (`commonfury_audio.dll`, 385 KB on win-x64). The core library stays fully managed. On Windows x64 the
project builds the DLL with CMake and the Visual Studio C++ tools; see
[CommonFury.Miniaudio/native/THIRD_PARTY.md](CommonFury.Miniaudio/native/THIRD_PARTY.md) for sources, licenses and other
platforms.

## Build and test

```
dotnet build CommonFury.slnx
dotnet test --solution CommonFury.slnx
```

## License

Dual-licensed under the [MIT License](LICENSE-MIT) or the [Unlicense](UNLICENSE), at your option.
