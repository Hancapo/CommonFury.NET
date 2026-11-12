#pragma once

#include <cstdint>

namespace commonfury::texture {

void encode_bc1_alpha(uint8_t* destination, const uint8_t* rgba_block, uint32_t quality_level);

}
