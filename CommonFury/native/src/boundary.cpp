#include "boundary.h"
#include <cstring>
#include <limits>

namespace {
thread_local char last_error[1024] = {};
}

namespace commonfury::texture {

void clear_error() noexcept { last_error[0] = '\0'; }

void set_error(const char* message) noexcept {
    size_t count = 0;
    if (message) {
        while (count + 1 < sizeof(last_error) && message[count]) {
            last_error[count] = message[count];
            ++count;
        }
    }
    last_error[count] = '\0';
}

size_t checked_product(size_t left, size_t right) {
    if (right && left > std::numeric_limits<size_t>::max() / right)
        throw std::invalid_argument("Texture extent overflows native size_t.");
    return left * right;
}

surface_extent::surface_extent(int width_value, int height_value)
    : width(width_value), height(height_value), rgba_bytes(0), blocks_x(0), blocks_y(0) {
    if (width <= 0 || height <= 0)
        throw std::invalid_argument("Image dimensions must be positive.");
    // Match stb_image's dimension guard and leave headroom for stb_resize's
    // signed-int coefficient counts and floating-point scanline lengths.
    constexpr int maximum_dimension = 1 << 24;
    if (width > maximum_dimension || height > maximum_dimension)
        throw std::invalid_argument("Image dimensions cannot exceed 16777216.");
    rgba_bytes = checked_product(checked_product(static_cast<size_t>(width), height), 4);
    if (rgba_bytes > static_cast<size_t>(std::numeric_limits<int>::max()))
        throw std::invalid_argument("RGBA surface exceeds the native 2 GiB byte limit.");
    blocks_x = (static_cast<size_t>(width) + 3) / 4;
    blocks_y = (static_cast<size_t>(height) + 3) / 4;
}

size_t surface_extent::block_bytes(size_t bytes_per_block) const {
    return checked_product(checked_product(blocks_x, blocks_y), bytes_per_block);
}

void require_input(const uint8_t* data, size_t length, size_t expected) {
    if (!data) throw std::invalid_argument("Input buffer is null.");
    if (length != expected) throw std::invalid_argument("Input buffer length does not match its dimensions and format.");
}

owned_buffer::owned_buffer(size_t size) : size_(size) {
    if (!size) throw std::invalid_argument("An output buffer cannot be empty.");
    data_.reset(static_cast<uint8_t*>(std::malloc(size)));
    if (!data_) throw std::bad_alloc();
}

owned_buffer copy_buffer(const void* data, size_t length) {
    owned_buffer output(length);
    std::memcpy(output.data(), data, length);
    return output;
}

}

uint32_t CFTEX_CALL cftex_version(void) noexcept {
    commonfury::texture::clear_error();
    return 2;
}

void CFTEX_CALL cftex_release_buffer(void* data) noexcept { std::free(data); }

const char* CFTEX_CALL cftex_last_error(void) noexcept { return last_error; }
