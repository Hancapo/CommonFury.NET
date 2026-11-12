# Native texture codec provenance

CommonFury owns the memory-only wrapper and builds its own `commonfury_texture`
library, an internal dependency of the managed `CommonFury.Textures` layer. It
does not load TexFury or link to a TexFury binary. Container I/O, mip-chain
ownership and uncompressed pixel conversion remain managed.

The algorithm selection and BC1 punch-through kernel are adapted from
[TexFury 1.6.3](https://github.com/Hancapo/texfury/tree/ccb5c9f227ecbf22daf9231febb6fa0839b8c02b),
commit `ccb5c9f227ecbf22daf9231febb6fa0839b8c02b`. That checkout's
`pyproject.toml` declares the MIT license and author Hancapo. Its repository
does not contain a standalone LICENSE file; `LICENSES/TexFury.txt` records
the declared license and attribution. The adapted BC1 source is marked.

| Component | Pinned source | Included code and license |
| --- | --- | --- |
| bc7enc_rdo | `dbe416d28a5530b4e8cc45b14bf034dc6b96bbde` | `rgbcx.cpp` and its headers/tables; MIT option, Richard Geldreich, Jr. |
| bc7e ISPC | Same bc7enc_rdo commit | `bc7e.ispc`; Apache 2.0, Binomial LLC |
| stb | `28d546d5eb77d4585506a20480f4de2e706dff4c` | `stb_image.h`, `stb_image_write.h`, `stb_image_resize2.h`; MIT option, Sean Barrett and contributors |
| bcdec | Unmodified header from the TexFury commit above | BC1–7 decompression; MIT option, Sergii Kudlai |
| ISPC | Official Windows release 1.26.0 | Build compiler and generated CPU dispatch; BSD 3-Clause, Intel Corporation |

CMake fetches bc7enc_rdo, stb and the official ISPC compiler from fixed HTTPS
URLs with SHA-256 verification. It does not run the codecs' CMake projects,
build scripts or container command-line tools. The small bcdec header is vendored with its complete
license; SHA-256 of the upstream file (CRLF line endings) is
`e055a91084bbcada329179c6fb0f679d74ccb2c7514081a16c8e5e95d986e41b`.
The repository stores it with LF line endings.

The build includes no RDO/container/metric source, LodePNG or Python code.
The complete upstream license notices and Apache 2.0 license text are
retained in `LICENSES/`. The bc7enc_rdo umbrella notice mentions LodePNG
because that is part of upstream; CommonFury does not compile it.

ISPC emits SSE2, SSE4, AVX and AVX2 implementations and a CPU dispatcher.
The C++ code has no AVX2 baseline requirement. MSVC uses its static runtime,
and only the eight `cftex_*` ABI functions are exported. All transient image
allocations belong to a per-thread scope; they are released on exception as
well as on normal completion. Native outputs always use the allocating
library's matching release export.
