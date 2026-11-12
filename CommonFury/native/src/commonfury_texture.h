#ifndef COMMONFURY_TEXTURE_H
#define COMMONFURY_TEXTURE_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(COMMONFURY_TEXTURE_EXPORTS)
#    define CFTEX_API __declspec(dllexport)
#  else
#    define CFTEX_API __declspec(dllimport)
#  endif
#  define CFTEX_CALL __cdecl
#else
#  define CFTEX_API __attribute__((visibility("default")))
#  define CFTEX_CALL
#endif

#ifdef __cplusplus
#  define CFTEX_NOEXCEPT noexcept
extern "C" {
#else
#  define CFTEX_NOEXCEPT
#endif

/* Internal ABI between CommonFury's managed texture layer and this library; it is
 * not a public C API (that is CommonFury.Native) and may change with any release.
 *
 * ABI v2: native size_t, 32-bit int, cdecl. Inputs are borrowed for the call.
 * Each successful output is owned by the caller and must be released exactly
 * once with cftex_release_buffer. Failures return 0 and clear output fields.
 * Output arguments must be distinct writable storage, initially empty.
 * RGBA surfaces are limited to INT_MAX bytes and dimensions to 2^24.
 */
typedef struct cftex_buffer {
    uint8_t* data;
    size_t size;
} cftex_buffer;

CFTEX_API uint32_t CFTEX_CALL cftex_version(void) CFTEX_NOEXCEPT;

/* Decode PNG/JPEG/BMP/TGA to tightly packed, top-down RGBA8. maximum_bytes
 * limits decoded RGBA bytes; dimensions are checked before decoding pixels.
 */
CFTEX_API int CFTEX_CALL cftex_image_decode(const uint8_t* input, size_t length,
    size_t maximum_bytes, cftex_buffer* output, int* width, int* height) CFTEX_NOEXCEPT;

/* file_format: PNG=0, JPEG=1, BMP=2, TGA=3. JPEG quality is 1..100 and
 * discards alpha. Other formats ignore quality. RGBA length must be exact.
 */
CFTEX_API int CFTEX_CALL cftex_image_encode(const uint8_t* rgba, size_t length,
    int width, int height, int file_format, int quality, cftex_buffer* output) CFTEX_NOEXCEPT;

/* filter: Box=0, Triangle=1, CubicBSpline=2, CatmullRom=3, Mitchell=4, Point=5.
 * srgb: 0=linear RGB, 1=sRGB RGB. Straight alpha is weighted and stays linear.
 */
CFTEX_API int CFTEX_CALL cftex_image_resize(const uint8_t* rgba, size_t length,
    int width, int height, int new_width, int new_height, int filter, int srgb,
    cftex_buffer* output) CFTEX_NOEXCEPT;

/* DXGI: BC1 71/72, BC2 74/75, BC3 77/78, BC4 80, BC5 83, BC7 98/99.
 * bc1_alpha: 0=opaque, 1=punch-through at alpha <128. quality must be finite
 * and in [0,1]. Partial 4x4 blocks extend the nearest image edge.
 */
CFTEX_API int CFTEX_CALL cftex_encode_mip(const uint8_t* rgba,
    size_t length, int width, int height, uint32_t dxgi_format, int bc1_alpha,
    float quality, cftex_buffer* output) CFTEX_NOEXCEPT;

/* Also decodes BC6H UF16=95/SF16=96, clamped to [0,1] RGBA8 with alpha=255.
 * BC4 previews R as gray; BC5 produces RG, B=0, A=255. Signed BC4/BC5 are
 * unsupported. sRGB aliases preserve encoded channel values without a transfer.
 */
CFTEX_API int CFTEX_CALL cftex_decode_mip(const uint8_t* data,
    size_t length, int width, int height, uint32_t dxgi_format,
    cftex_buffer* output) CFTEX_NOEXCEPT;

CFTEX_API void CFTEX_CALL cftex_release_buffer(void* data) CFTEX_NOEXCEPT;

/* Thread-local UTF-8 text; valid until the next native operation on the thread.
 * Reading this text or releasing a buffer does not overwrite the last error.
 */
CFTEX_API const char* CFTEX_CALL cftex_last_error(void) CFTEX_NOEXCEPT;

#ifdef __cplusplus
}
#endif

#endif
