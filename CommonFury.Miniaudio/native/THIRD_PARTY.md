# Native audio decoder

`commonfury_audio` is a decode-only build of miniaudio with stb_vorbis. It decodes WAV, FLAC, MP3 and Ogg Vorbis to
floating-point samples at the native rate; CommonFury does channel conversion and resampling in managed code. Device
I/O, the engine, threading and all encoders are compiled out. Its exports are private to `CommonFury.Miniaudio` and
carry no compatibility promise.

| Component | Pinned source | License |
| --- | --- | --- |
| miniaudio, including its WAV, FLAC and MP3 decoders | [0.11.25 release archive](https://github.com/mackron/miniaudio/archive/refs/tags/0.11.25.tar.gz) | Public domain (Unlicense) or MIT No Attribution, at your option: [LICENSES/miniaudio.txt](LICENSES/miniaudio.txt) |
| stb_vorbis 1.22 | `extras/stb_vorbis.c` from the same archive | MIT or public domain (Unlicense), at your option: [LICENSES/stb_vorbis.txt](LICENSES/stb_vorbis.txt) |

CMake verifies the archive against SHA-256 `b900edcffe979816e2560a0580b9b1216d674b4f17fbadeca8f777a7f8ab0274`.
No source is copied into this repository.

On Windows x64 the library is built with MSVC for size (`MinSizeRel`, link-time code generation, static C runtime)
and depends only on `KERNEL32.dll`. Builds copy it to consuming projects and pack it under `runtimes/win-x64/native`,
with these notices under `licenses/commonfury_audio`. On other platforms, build `native/CMakeLists.txt` with any C
compiler, deploy `libcommonfury_audio` next to the application, and build with `BuildNativeAudio=false`.
