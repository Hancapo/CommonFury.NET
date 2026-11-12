// Adapted from TexFury 1.6.3 (Hancapo, MIT); see ../LICENSES/TexFury.txt.
#include "bc1_alpha.h"
#include "rgbcx.h"
#include <algorithm>
#include <cstring>
#include <limits>

namespace commonfury::texture {
namespace {

uint16_t pack_rgb565(const uint8_t* pixel) {
    return static_cast<uint16_t>(((pixel[0] >> 3) << 11) | ((pixel[1] >> 2) << 5) | (pixel[2] >> 3));
}

void unpack_rgb565(uint16_t color, uint8_t* pixel) {
    const auto r = static_cast<uint8_t>((color >> 11) & 31);
    const auto g = static_cast<uint8_t>((color >> 5) & 63);
    const auto b = static_cast<uint8_t>(color & 31);
    pixel[0] = static_cast<uint8_t>((r << 3) | (r >> 2));
    pixel[1] = static_cast<uint8_t>((g << 2) | (g >> 4));
    pixel[2] = static_cast<uint8_t>((b << 3) | (b >> 2));
}

int rgb_squared_error(const uint8_t* left, const uint8_t* right) {
    int error = 0;
    for (int channel = 0; channel < 3; ++channel) {
        const int difference = left[channel] - right[channel];
        error += difference * difference;
    }
    return error;
}

}

void encode_bc1_alpha(uint8_t* destination, const uint8_t* pixels, uint32_t quality_level) {
    int opaque_count = 0;
    int minimum_index = -1, maximum_index = -1;
    int minimum_luma = std::numeric_limits<int>::max(), maximum_luma = -1;
    for (int index = 0; index < 16; ++index) {
        const auto* pixel = pixels + index * 4;
        if (pixel[3] < 128) continue;
        ++opaque_count;
        const int luma = 77 * pixel[0] + 150 * pixel[1] + 29 * pixel[2];
        if (luma < minimum_luma) { minimum_luma = luma; minimum_index = index; }
        if (luma > maximum_luma) { maximum_luma = luma; maximum_index = index; }
    }
    if (opaque_count == 16) {
        rgbcx::encode_bc1(quality_level, destination, pixels, false, false);
        return;
    }
    uint16_t first = 0, second = 0;
    uint32_t selectors = 0xffffffffu;
    if (opaque_count) {
        first = pack_rgb565(pixels + minimum_index * 4);
        second = pack_rgb565(pixels + maximum_index * 4);
        if (first > second) std::swap(first, second);
        uint8_t palette[3][3];
        unpack_rgb565(first, palette[0]);
        unpack_rgb565(second, palette[1]);
        for (int channel = 0; channel < 3; ++channel)
            palette[2][channel] = static_cast<uint8_t>((palette[0][channel] + palette[1][channel]) / 2);
        selectors = 0;
        for (int index = 0; index < 16; ++index) {
            const auto* pixel = pixels + index * 4;
            uint32_t selector = 3;
            if (pixel[3] >= 128) {
                int best_error = std::numeric_limits<int>::max();
                for (uint32_t candidate = 0; candidate < 3; ++candidate) {
                    const int error = rgb_squared_error(pixel, palette[candidate]);
                    if (error < best_error) { best_error = error; selector = candidate; }
                }
            }
            selectors |= selector << (index * 2);
        }
    }
    std::memcpy(destination, &first, 2);
    std::memcpy(destination + 2, &second, 2);
    std::memcpy(destination + 4, &selectors, 4);
}

}
