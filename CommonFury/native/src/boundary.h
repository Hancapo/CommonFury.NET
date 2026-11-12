#pragma once

#include "commonfury_texture.h"
#include <cstdlib>
#include <exception>
#include <memory>
#include <stdexcept>

namespace commonfury::texture {

void clear_error() noexcept;
void set_error(const char* message) noexcept;
size_t checked_product(size_t left, size_t right);
void require_input(const uint8_t* data, size_t length, size_t expected);

struct surface_extent {
    int width;
    int height;
    size_t rgba_bytes;
    size_t blocks_x;
    size_t blocks_y;

    surface_extent(int width, int height);
    size_t block_bytes(size_t bytes_per_block) const;
};

class owned_buffer {
public:
    explicit owned_buffer(size_t size);
    uint8_t* data() noexcept { return data_.get(); }
    cftex_buffer release() noexcept { return {data_.release(), size_}; }

private:
    std::unique_ptr<uint8_t, decltype(&std::free)> data_{nullptr, std::free};
    size_t size_;
};

owned_buffer copy_buffer(const void* data, size_t length);

template<typename Operation>
int write_output(cftex_buffer* output, Operation&& operation) noexcept {
    clear_error();
    if (output) *output = {};
    try {
        if (!output) throw std::invalid_argument("Output buffer is null.");
        auto result = operation();
        *output = result.release();
        return 1;
    } catch (const std::bad_alloc&) {
        set_error("Native texture allocation failed.");
    } catch (const std::exception& error) {
        set_error(error.what());
    } catch (...) {
        set_error("Unexpected native texture failure.");
    }
    return 0;
}

}
