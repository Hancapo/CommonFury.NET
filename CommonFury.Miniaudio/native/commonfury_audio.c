/* Decode-only miniaudio build for CommonFury.Miniaudio: WAV, FLAC and Ogg Vorbis (stb_vorbis) to native-rate f32.
   The exports are private to the managed wrapper and carry no compatibility promise. */
#include <stdint.h>
#include <stdlib.h>

#define STB_VORBIS_HEADER_ONLY
#include "extras/stb_vorbis.c"

#define MA_NO_DEVICE_IO
#define MA_NO_ENGINE
#define MA_NO_NODE_GRAPH
#define MA_NO_RESOURCE_MANAGER
#define MA_NO_GENERATION
#define MA_NO_ENCODING
#define MA_NO_THREADING
#define MA_NO_MP3
#define MINIAUDIO_IMPLEMENTATION
#include "miniaudio.h"

#undef STB_VORBIS_HEADER_ONLY
#include "extras/stb_vorbis.c"

#if defined(_WIN32)
#define CFA_API __declspec(dllexport)
#else
#define CFA_API __attribute__((visibility("default")))
#endif

enum { CFA_WAV = 1, CFA_FLAC = 2, CFA_VORBIS = 3 };

/* Returns 0 or a negative miniaudio result. The input must stay valid until cfa_decoder_close. */
CFA_API int32_t cfa_decoder_open(const void* data, size_t size, int32_t encoding, void** decoder, uint32_t* channels, uint32_t* sample_rate)
{
    ma_decoder_config config = ma_decoder_config_init(ma_format_f32, 0, 0);
    ma_decoder* result;
    ma_result status;

    *decoder = NULL;
    switch (encoding) {
        case CFA_WAV: config.encodingFormat = ma_encoding_format_wav; break;
        case CFA_FLAC: config.encodingFormat = ma_encoding_format_flac; break;
        case CFA_VORBIS: config.encodingFormat = ma_encoding_format_vorbis; break;
        default: return MA_INVALID_ARGS;
    }
    result = (ma_decoder*)malloc(sizeof(ma_decoder));
    if (result == NULL)
        return MA_OUT_OF_MEMORY;
    status = ma_decoder_init_memory(data, size, &config, result);
    if (status != MA_SUCCESS) {
        free(result);
        return status;
    }
    *decoder = result;
    *channels = result->outputChannels;
    *sample_rate = result->outputSampleRate;
    return MA_SUCCESS;
}

/* Returns the number of interleaved frames written to output, 0 at the end, or a negative miniaudio result. */
CFA_API int64_t cfa_decoder_read(void* decoder, float* output, uint64_t frames)
{
    ma_uint64 read = 0;
    ma_result status = ma_decoder_read_pcm_frames((ma_decoder*)decoder, output, frames, &read);
    if (status != MA_SUCCESS && status != MA_AT_END)
        return status;
    return (int64_t)read;
}

CFA_API void cfa_decoder_close(void* decoder)
{
    if (decoder != NULL) {
        ma_decoder_uninit((ma_decoder*)decoder);
        free(decoder);
    }
}
