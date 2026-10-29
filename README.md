<img src="assets/icon.svg" width="64" height="64" alt="">

# CommonFury

A .NET 10 library of building blocks for 3D and 2D asset work: binary I/O, hashing, compression, math and
geometry, textures and pixel formats, PCM audio, vertex data and spatial hierarchies, plus readers and writers for RAGE
metadata formats such as PSO and RBF. Managed, AOT-compatible, with no native dependencies.

Nothing here is tied to a particular game. Where formats vary between uses (pointer widths, name tables, limits,
codecs), the caller passes the choice in.

## Contents

| Namespace | Types | Callers supply |
|---|---|---|
| `CommonFury` | `Diagnostic`, `ValidationReport`, `ValidationException`, severity/scope/purpose enums | |
| `CommonFury.Hashing` | `Jenkins`, `Hash32`, `PartialHash32`, `HashText`, `HashResolver`, `ElfHash`, `Checksums` | |
| `CommonFury.Binary` | `BinarySpanReader`/`Writer`, `BinaryStreamWriter`, `BinaryArrays`, `BinaryBitReader`/`Writer`, `MsbBitReader`, `BitField32`, `BinaryLayout`, `Endian`, `ByteOrder` | byte order |
| `CommonFury.IO` | `AtomicFile`, `AssetPaths`, `VirtualPaths`, `AssetNames`, `FileTree` | |
| `CommonFury.Numerics` | `VectorMath`, `QuaternionMath`, `TransformMath`, `MatrixMath`, `SpanMath`, `Aabb2`, `Aabb3`, `Sphere3`, `Matrix3x4`, `Vector3D`, `Quantization`, `PackedVectors`, `BoundsQuantization`, `SkinningMath`, `SkinningBatch`, `JointFrameMath`, `CurveMath`, `IntegerGrid` | grid steps, bounds and bit widths; influence counts |
| `CommonFury.Geometry` | `MeshMath`, `MeshPartitioning`, `PolygonMath`, `PlaneMath`, `HeightFieldMath`, `RasterMath`, `SpatialGrid2`, `ShapeGeometry`, `MassProperties` | vertex limits for splitting; up and fallback axes for plane fits |
| `CommonFury.Compression` | `RawDeflate`, `CompressionCache` | output size limit; zero-history window when the format needs it |
| `CommonFury.Audio` | `PcmAudio` (WAV read/write, mono/stereo conversion, resampling), `IAudioDecoder` | a decoder for compressed formats |
| `CommonFury.Textures` | `TextureFormat(s)`, `TextureShape`, `TextureLayout`, `TextureSurface`, `DdsCodec`, `PixelCodec`, `Bc1`; `Numerics.PixelMath` | |
| `CommonFury.Pso` | `PsoFile`, `PsoSchema`, `PsoBuilder`, `PsoGraphWriter`, typed `PsoValue`s, `PsoPointerLayout`, `IPsoNameResolver` | name resolver; pointer layout when not detected |
| `CommonFury.Rbf` | `RbfDocument`, nodes, `RbfLimits`, `RbfDescriptor`, `RbfRecordType`, `RbfDiagnosticCodes` | |
| `CommonFury.Xml` | `XmlDocumentCodec`, `XmlElementReader`, `XmlElements`, `XmlPlatformSelection` | platform names |
| `CommonFury.Dlc` | `DlcPackType`, `DlcContentGroup`, `DlcDataFileType`, `DlcDataFileContents` | additional token values, as C# 14 extension members |
| `CommonFury.HeightMaps` | `HeightMap<T>`, `HeightMapCodec`, `HeightMapExportLimits` | target export limits |
| `CommonFury.Animations` | channels, `SequenceTrack`, `AnimationSequence`, `AnimationSequenceFormat`, `AnimationChannelGroupTable` | group table, linear encoding, hash policy, compact codec |
| `CommonFury.Vertices` | `VertexSemantic`, `VertexElementFormat(s)`, `VertexElementCodec`, `Gen9VertexDeclaration` | DXGI 24 range (`Rgb10A2Range`) |
| `CommonFury.Collisions` | `BvhBuilder`, `BvhQuantization`, `QuantizedBvhNode`, `BoundPolygonKind` | split rule, leaf ids, quantization rounding |
| `CommonFury.Resources` | `ResourcePointer`, `ResourceSection` | whether pointer upper bits carry metadata |
| `CommonFury.Archives` | `ByteSource` (+ `StreamByteSource`, `FileByteSource`, `ByteSliceStream`), `ArchiveHandleCache<T>` | archive open and size callbacks |
| `CommonFury.Assets` | `DrawableLod`, `BoneChannelFlags` (bits 0–10), `YmapFlags`, `YmapContentFlags` | |

## Native library

`CommonFury.Native` publishes `commonfury_native.dll`, a Native AOT C ABI (1.7 MB on win-x64) over the data-in/data-out parts of
the library. See [CommonFury.Native/README.md](CommonFury.Native/README.md).

## Build and test

```
dotnet build CommonFury.slnx
dotnet test --solution CommonFury.slnx
```

## License

Dual-licensed under the [MIT License](LICENSE-MIT) or the [Unlicense](UNLICENSE), at your option.
