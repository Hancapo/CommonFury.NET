#include "stb_memory.h"

#define STBI_MALLOC(size) commonfury::texture::stb_memory::allocate(size)
#define STBI_REALLOC(data, size) commonfury::texture::stb_memory::reallocate(data, size)
#define STBI_FREE(data) commonfury::texture::stb_memory::release(data)
#define STBI_ONLY_PNG
#define STBI_ONLY_JPEG
#define STBI_ONLY_BMP
#define STBI_ONLY_TGA
#define STBI_NO_STDIO
#define STBI_NO_HDR
#define STBI_NO_LINEAR
#define STB_IMAGE_IMPLEMENTATION
#include "stb_image.h"

#define STBIW_MALLOC(size) commonfury::texture::stb_memory::allocate(size)
#define STBIW_REALLOC(data, size) commonfury::texture::stb_memory::reallocate(data, size)
#define STBIW_FREE(data) commonfury::texture::stb_memory::release(data)
#define STBI_WRITE_NO_STDIO
#define STB_IMAGE_WRITE_IMPLEMENTATION
#include "stb_image_write.h"

#define STBIR_MALLOC(size, context) commonfury::texture::stb_memory::allocate(size)
#define STBIR_FREE(data, context) commonfury::texture::stb_memory::release(data)
#define STB_IMAGE_RESIZE_IMPLEMENTATION
#include "stb_image_resize2.h"
