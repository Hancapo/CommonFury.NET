#include "block_surface.h"
#define BCDEC_STATIC
#define BCDEC_BC4BC5_PRECISE
#define BCDEC_IMPLEMENTATION
#include "bcdec.h"
#include <cstring>

using namespace commonfury::texture;

namespace {

uint8_t preview_channel(float value) {
    if (!(value > 0)) return 0; // Also maps NaN to black.
    if (value >= 1) return 255;
    return static_cast<uint8_t>(value * 255 + 0.5f);
}

void decode_block(block_encoding format, const uint8_t* block, uint8_t* pixels) {
    switch (format) {
    case block_encoding::bc1: bcdec_bc1(block, pixels, 16); return;
    case block_encoding::bc2: bcdec_bc2(block, pixels, 16); return;
    case block_encoding::bc3: bcdec_bc3(block, pixels, 16); return;
    case block_encoding::bc7: bcdec_bc7(block, pixels, 16); return;
    case block_encoding::bc4: {
        uint8_t red[16];
        bcdec_bc4(block, red, 4, 0);
        for (int index = 0; index < 16; ++index) {
            pixels[index * 4] = red[index];
            pixels[index * 4 + 1] = red[index];
            pixels[index * 4 + 2] = red[index];
            pixels[index * 4 + 3] = 255;
        }
        return;
    }
    case block_encoding::bc5: {
        uint8_t red_green[32];
        bcdec_bc5(block, red_green, 8, 0);
        for (int index = 0; index < 16; ++index) {
            pixels[index * 4] = red_green[index * 2];
            pixels[index * 4 + 1] = red_green[index * 2 + 1];
            pixels[index * 4 + 2] = 0;
            pixels[index * 4 + 3] = 255;
        }
        return;
    }
    case block_encoding::bc6_unsigned:
    case block_encoding::bc6_signed: {
        float rgb[48];
        bcdec_bc6h_float(block, rgb, 12, format == block_encoding::bc6_signed);
        for (int index = 0; index < 16; ++index) {
            for (int channel = 0; channel < 3; ++channel)
                pixels[index * 4 + channel] = preview_channel(rgb[index * 3 + channel]);
            pixels[index * 4 + 3] = 255;
        }
        return;
    }
    }
    throw std::invalid_argument("Unsupported BC block decoding.");
}

}

int CFTEX_CALL cftex_decode_mip(const uint8_t* data, size_t length,
    int width, int height, uint32_t dxgi_format, cftex_buffer* output) noexcept {
    return write_output(output, [&] {
        const surface_extent extent(width, height);
        const auto format = describe_block_format(dxgi_format);
        require_input(data, length, extent.block_bytes(format.bytes));
        owned_buffer result(extent.rgba_bytes);
        const size_t count = extent.blocks_x * extent.blocks_y;
        alignas(16) uint8_t pixels[64];
        alignas(16) uint8_t block[16];
        for (size_t index = 0; index < count; ++index) {
            // Upstream decoders use integer loads. The public input may be
            // unaligned, so copy exactly one block into aligned local storage.
            std::memcpy(block, data + index * format.bytes, format.bytes);
            decode_block(format.encoding, block, pixels);
            write_pixel_block(pixels, extent, index, result.data());
        }
        return result;
    });
}
