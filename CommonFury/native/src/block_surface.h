#pragma once

#include "boundary.h"

namespace commonfury::texture {

enum class block_encoding { bc1, bc2, bc3, bc4, bc5, bc6_unsigned, bc6_signed, bc7 };

struct block_format {
    block_encoding encoding;
    size_t bytes;
    bool srgb;
};

block_format describe_block_format(uint32_t dxgi_format);
void read_pixel_block(const uint8_t* source, const surface_extent& extent,
    size_t block_index, uint8_t* block);
void write_pixel_block(const uint8_t* block, const surface_extent& extent,
    size_t block_index, uint8_t* destination);

}
