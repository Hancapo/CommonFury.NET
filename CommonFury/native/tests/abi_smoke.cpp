#include "commonfury_texture.h"
#include <algorithm>
#include <array>
#include <cstdio>
#include <cstring>
#include <stdexcept>

namespace {

struct buffer {
    cftex_buffer value{};
    ~buffer() { cftex_release_buffer(value.data); }
};

void require(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}

void verify_images(const std::array<uint8_t, 64>& pixels) {
    for (int format = 0; format < 4; ++format) {
        buffer encoded, decoded, resized;
        int width = 0, height = 0;
        require(cftex_image_encode(pixels.data(), pixels.size(), 4, 4, format, 90, &encoded.value), "Image encode");
        require(cftex_image_decode(encoded.value.data, encoded.value.size, 64, &decoded.value, &width, &height), "Image decode");
        require(width == 4 && height == 4 && decoded.value.size == 64, "Image dimensions");
        if (format != 1) require(std::memcmp(pixels.data(), decoded.value.data, 64) == 0, "RGBA image roundtrip");
        require(cftex_image_resize(decoded.value.data, 64, 4, 4, 3, 2, 4, 1, &resized.value), "Image resize");
        require(resized.value.size == 24, "Resize dimensions");
    }
}

void verify_blocks(const std::array<uint8_t, 64>& pixels) {
    constexpr uint32_t formats[] = {71, 72, 74, 75, 77, 78, 80, 83, 98, 99};
    for (uint32_t format : formats) {
        buffer encoded, decoded;
        require(cftex_encode_mip(pixels.data(), pixels.size(), 4, 4, format, 1, 0.5f, &encoded.value), "BC encode");
        require(encoded.value.size == (format == 71 || format == 72 || format == 80 ? 8u : 16u), "BC block size");
        require(cftex_decode_mip(encoded.value.data, encoded.value.size, 4, 4, format, &decoded.value), "BC decode");
        require(decoded.value.size == 64, "BC decoded size");
        if (format == 71 || format == 72)
            require(decoded.value.data[3] == 0 && decoded.value.data[7] == 255, "BC1 punch-through alpha");
        if (format == 74 || format == 75)
            for (size_t index = 3; index < 64; index += 4)
                require(decoded.value.data[index] == pixels[index], "BC2 explicit alpha");
    }
    buffer opaque, decoded;
    require(cftex_encode_mip(pixels.data(), pixels.size(), 4, 4, 71, 0, 0.5f, &opaque.value), "Opaque BC1 encode");
    require(cftex_decode_mip(opaque.value.data, opaque.value.size, 4, 4, 71, &decoded.value), "Opaque BC1 decode");
    require(decoded.value.data[3] == 255, "Opaque BC1 alpha");
}

void verify_failure(const std::array<uint8_t, 64>& pixels) {
    cftex_buffer output{nullptr, 42};
    require(!cftex_encode_mip(pixels.data(), 63, 4, 4, 71, 0, 0.5f, &output), "Short input rejection");
    require(!output.data && output.size == 0 && std::strlen(cftex_last_error()), "Empty failure output and error");
    require(!cftex_encode_mip(pixels.data(), 64, 4, 4, 81, 0, 0.5f, &output), "Signed BC4 rejection");
    require(!cftex_decode_mip(pixels.data(), 16, 4, 4, 84, &output), "Signed BC5 rejection");
    require(!cftex_encode_mip(pixels.data(), 64, 4, 4, 95, 0, 0.5f, &output), "BC6H encode rejection");
    require(!cftex_image_resize(pixels.data(), 64, 4, 4, 1 << 25, 1, 4, 1, &output), "Oversized dimension rejection");
}

}

int main() {
    try {
        require(cftex_version() == 2, "ABI version");
        std::array<uint8_t, 64> pixels;
        for (size_t index = 0; index < 16; ++index) {
            pixels[index * 4] = 220;
            pixels[index * 4 + 1] = 40;
            pixels[index * 4 + 2] = 80;
            pixels[index * 4 + 3] = index ? 255 : 0;
        }
        verify_images(pixels);
        verify_blocks(pixels);
        verify_failure(pixels);
        std::puts("commonfury_texture ABI smoke passed");
        return 0;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "%s: %s\n", error.what(), cftex_last_error());
        return 1;
    }
}
