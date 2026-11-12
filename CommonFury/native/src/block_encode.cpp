#include "block_surface.h"
#include "bc1_alpha.h"
#include "rgbcx.h"
#include "bc7e_ispc.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <mutex>

using namespace commonfury::texture;

namespace {

void initialize_encoders() {
    static std::once_flag initialized;
    std::call_once(initialized, [] {
        rgbcx::init(rgbcx::bc1_approx_mode::cBC1Ideal);
        ispc::bc7e_compress_block_init();
    });
}

void encode_bc7(const uint8_t* rgba, const surface_extent& extent, float quality,
    bool srgb, uint8_t* output) {
    ispc::bc7e_compress_block_params parameters{};
    switch (static_cast<int>(quality * 6)) {
    case 0: ispc::bc7e_compress_block_params_init_ultrafast(&parameters, srgb); break;
    case 1: ispc::bc7e_compress_block_params_init_veryfast(&parameters, srgb); break;
    case 2: ispc::bc7e_compress_block_params_init_fast(&parameters, srgb); break;
    case 3: ispc::bc7e_compress_block_params_init_basic(&parameters, srgb); break;
    case 4: ispc::bc7e_compress_block_params_init_slow(&parameters, srgb); break;
    case 5: ispc::bc7e_compress_block_params_init_veryslow(&parameters, srgb); break;
    case 6: ispc::bc7e_compress_block_params_init_slowest(&parameters, srgb); break;
    }
    constexpr size_t batch_size = 64;
    alignas(16) std::array<uint32_t, batch_size * 16> pixels;
    const size_t count = extent.blocks_x * extent.blocks_y;
    for (size_t start = 0; start < count; start += batch_size) {
        const size_t batch_count = std::min(batch_size, count - start);
        for (size_t index = 0; index < batch_count; ++index)
            read_pixel_block(rgba, extent, start + index,
                reinterpret_cast<uint8_t*>(pixels.data() + index * 16));
        ispc::bc7e_compress_blocks(static_cast<uint32_t>(batch_count),
            reinterpret_cast<uint64_t*>(output + start * 16), pixels.data(), &parameters);
    }
}

void encode_ldr_block(block_encoding format, const uint8_t* pixels,
    uint32_t quality_level, int bc1_alpha, uint8_t* output) {
    switch (format) {
    case block_encoding::bc1:
        if (bc1_alpha) encode_bc1_alpha(output, pixels, quality_level);
        else rgbcx::encode_bc1(quality_level, output, pixels, false, false);
        return;
    case block_encoding::bc2:
        // DXT3 stores two explicit four-bit alpha samples per byte followed by
        // a forced four-color BC1 block (its color has no transparent mode).
        for (int index = 0; index < 8; ++index) {
            const auto first = static_cast<uint8_t>((pixels[index * 8 + 3] + 8) / 17);
            const auto second = static_cast<uint8_t>((pixels[index * 8 + 7] + 8) / 17);
            output[index] = static_cast<uint8_t>(first | (second << 4));
        }
        rgbcx::encode_bc1(quality_level, output + 8, pixels, false, false);
        return;
    case block_encoding::bc3: rgbcx::encode_bc3(quality_level, output, pixels); return;
    case block_encoding::bc4: rgbcx::encode_bc4(output, pixels); return;
    case block_encoding::bc5: rgbcx::encode_bc5(output, pixels); return;
    default: throw std::invalid_argument("Unsupported LDR block encoding.");
    }
}

}

int CFTEX_CALL cftex_encode_mip(const uint8_t* rgba, size_t length,
    int width, int height, uint32_t dxgi_format, int bc1_alpha, float quality,
    cftex_buffer* output) noexcept {
    return write_output(output, [&] {
        const surface_extent extent(width, height);
        const auto format = describe_block_format(dxgi_format);
        require_input(rgba, length, extent.rgba_bytes);
        if (format.encoding == block_encoding::bc6_unsigned || format.encoding == block_encoding::bc6_signed)
            throw std::invalid_argument("BC6H encoding is unsupported.");
        if (bc1_alpha != 0 && bc1_alpha != 1)
            throw std::invalid_argument("BC1 alpha mode must be opaque or punch-through.");
        if (!std::isfinite(quality) || quality < 0 || quality > 1)
            throw std::invalid_argument("Texture quality must be finite and between 0 and 1.");
        initialize_encoders();
        owned_buffer result(extent.block_bytes(format.bytes));
        if (format.encoding == block_encoding::bc7) {
            encode_bc7(rgba, extent, quality, format.srgb, result.data());
        } else {
            const auto quality_level = static_cast<uint32_t>(quality * rgbcx::MAX_LEVEL);
            const size_t count = extent.blocks_x * extent.blocks_y;
            alignas(16) uint8_t pixels[64];
            for (size_t index = 0; index < count; ++index) {
                read_pixel_block(rgba, extent, index, pixels);
                encode_ldr_block(format.encoding, pixels, quality_level, bc1_alpha,
                    result.data() + index * format.bytes);
            }
        }
        return result;
    });
}
