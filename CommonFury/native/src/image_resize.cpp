#include "boundary.h"
#include "stb_memory.h"
#include "stb_image_resize2.h"

using namespace commonfury::texture;

int CFTEX_CALL cftex_image_resize(const uint8_t* rgba, size_t length, int width,
    int height, int new_width, int new_height, int filter, int srgb,
    cftex_buffer* output) noexcept {
    return write_output(output, [&] {
        const surface_extent source(width, height);
        const surface_extent destination(new_width, new_height);
        require_input(rgba, length, source.rgba_bytes);
        if (filter < 0 || filter > 5) throw std::invalid_argument("Unsupported image resize filter.");
        if (srgb != 0 && srgb != 1) throw std::invalid_argument("Image color space must be linear or sRGB.");
        if (width == new_width && height == new_height) return copy_buffer(rgba, length);
        owned_buffer result(destination.rgba_bytes);
        stb_memory::scope allocations;
        STBIR_RESIZE resize;
        stbir_resize_init(&resize, rgba, width, height, width * 4,
            result.data(), new_width, new_height, new_width * 4,
            STBIR_RGBA, srgb ? STBIR_TYPE_UINT8_SRGB : STBIR_TYPE_UINT8);
        const auto selected_filter = static_cast<stbir_filter>(filter + 1);
        stbir_set_filters(&resize, selected_filter, selected_filter);
        if (!stbir_resize_extended(&resize)) throw std::runtime_error("Image resize failed.");
        return result;
    });
}
