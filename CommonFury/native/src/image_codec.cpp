#include "boundary.h"
#include "stb_memory.h"
#define STBI_NO_STDIO
#include "stb_image.h"
#define STBI_WRITE_NO_STDIO
#include "stb_image_write.h"
#include <limits>
#include <vector>

using namespace commonfury::texture;

namespace {

void append_image_bytes(void* context, void* data, int length) {
    auto& encoded = *static_cast<std::vector<uint8_t>*>(context);
    if (length < 0 || static_cast<size_t>(length) > encoded.max_size() - encoded.size())
        throw std::length_error("Encoded image exceeds the output buffer limit.");
    const auto* bytes = static_cast<const uint8_t*>(data);
    encoded.insert(encoded.end(), bytes, bytes + length);
}

}

int CFTEX_CALL cftex_image_decode(const uint8_t* input, size_t length,
    size_t maximum_bytes, cftex_buffer* output, int* width, int* height) noexcept {
    if (width) *width = 0;
    if (height) *height = 0;
    return write_output(output, [&] {
        if (!input || !length || length > static_cast<size_t>(std::numeric_limits<int>::max()))
            throw std::invalid_argument("Encoded image must contain 1..INT_MAX bytes.");
        if (!width || !height) throw std::invalid_argument("Image dimension output is null.");
        stb_memory::scope allocations;
        int decoded_width = 0, decoded_height = 0, channels = 0;
        if (!stbi_info_from_memory(input, static_cast<int>(length), &decoded_width, &decoded_height, &channels))
            throw std::invalid_argument("Input is not a supported PNG, JPEG, BMP or TGA image.");
        const surface_extent extent(decoded_width, decoded_height);
        if (extent.rgba_bytes > maximum_bytes)
            throw std::invalid_argument("Decoded image exceeds maximum_bytes.");
        const auto* pixels = stbi_load_from_memory(input, static_cast<int>(length),
            &decoded_width, &decoded_height, &channels, 4);
        if (!pixels) throw std::invalid_argument("Image pixel data is invalid or truncated.");
        if (decoded_width != extent.width || decoded_height != extent.height)
            throw std::invalid_argument("Decoded dimensions disagree with the image header.");
        auto result = copy_buffer(pixels, extent.rgba_bytes);
        *width = decoded_width;
        *height = decoded_height;
        return result;
    });
}

int CFTEX_CALL cftex_image_encode(const uint8_t* rgba, size_t length, int width,
    int height, int file_format, int quality, cftex_buffer* output) noexcept {
    return write_output(output, [&] {
        const surface_extent extent(width, height);
        require_input(rgba, length, extent.rgba_bytes);
        if (file_format < 0 || file_format > 3)
            throw std::invalid_argument("Unsupported image file format.");
        if (file_format == 1 && (quality < 1 || quality > 100))
            throw std::invalid_argument("JPEG quality must be between 1 and 100.");
        if ((file_format == 1 || file_format == 3) && (width > 65535 || height > 65535))
            throw std::invalid_argument("JPEG and TGA dimensions cannot exceed 65535.");
        // stb's PNG compressor uses signed int buffer capacities which may grow
        // geometrically. Reserve sufficient integer headroom before entering it.
        if (file_format == 0 && extent.rgba_bytes > static_cast<size_t>(std::numeric_limits<int>::max()) / 4)
            throw std::invalid_argument("PNG encoding supports at most 512 MiB of RGBA pixels.");
        stb_memory::scope allocations;
        std::vector<uint8_t> encoded;
        int success = 0;
        switch (file_format) {
        case 0: success = stbi_write_png_to_func(append_image_bytes, &encoded, width, height, 4, rgba, width * 4); break;
        case 1: success = stbi_write_jpg_to_func(append_image_bytes, &encoded, width, height, 4, rgba, quality); break;
        case 2: success = stbi_write_bmp_to_func(append_image_bytes, &encoded, width, height, 4, rgba); break;
        case 3: success = stbi_write_tga_to_func(append_image_bytes, &encoded, width, height, 4, rgba); break;
        }
        if (!success || encoded.empty()) throw std::runtime_error("Image encoder failed.");
        return copy_buffer(encoded.data(), encoded.size());
    });
}
