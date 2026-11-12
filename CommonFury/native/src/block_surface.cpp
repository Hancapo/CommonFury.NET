#include "block_surface.h"
#include <algorithm>
#include <cstring>

namespace commonfury::texture {

block_format describe_block_format(uint32_t format) {
    switch (format) {
    case 71: return {block_encoding::bc1, 8, false};
    case 72: return {block_encoding::bc1, 8, true};
    case 74: return {block_encoding::bc2, 16, false};
    case 75: return {block_encoding::bc2, 16, true};
    case 77: return {block_encoding::bc3, 16, false};
    case 78: return {block_encoding::bc3, 16, true};
    case 80: return {block_encoding::bc4, 8, false};
    case 83: return {block_encoding::bc5, 16, false};
    case 95: return {block_encoding::bc6_unsigned, 16, false};
    case 96: return {block_encoding::bc6_signed, 16, false};
    case 98: return {block_encoding::bc7, 16, false};
    case 99: return {block_encoding::bc7, 16, true};
    case 81:
    case 84: throw std::invalid_argument("Signed BC4/BC5 processing is unsupported.");
    default: throw std::invalid_argument("Unsupported BC DXGI format.");
    }
}

void read_pixel_block(const uint8_t* source, const surface_extent& extent,
    size_t block_index, uint8_t* block) {
    const size_t x = (block_index % extent.blocks_x) * 4;
    const size_t y = (block_index / extent.blocks_x) * 4;
    for (size_t row = 0; row < 4; ++row) {
        const size_t source_y = std::min(y + row, static_cast<size_t>(extent.height - 1));
        for (size_t column = 0; column < 4; ++column) {
            const size_t source_x = std::min(x + column, static_cast<size_t>(extent.width - 1));
            std::memcpy(block + (row * 4 + column) * 4,
                source + (source_y * extent.width + source_x) * 4, 4);
        }
    }
}

void write_pixel_block(const uint8_t* block, const surface_extent& extent,
    size_t block_index, uint8_t* destination) {
    const size_t x = (block_index % extent.blocks_x) * 4;
    const size_t y = (block_index / extent.blocks_x) * 4;
    const size_t rows = std::min(size_t{4}, static_cast<size_t>(extent.height) - y);
    const size_t columns = std::min(size_t{4}, static_cast<size_t>(extent.width) - x);
    for (size_t row = 0; row < rows; ++row)
        std::memcpy(destination + ((y + row) * extent.width + x) * 4,
            block + row * 16, columns * 4);
}

}
