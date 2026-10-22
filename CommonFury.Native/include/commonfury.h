#ifndef COMMONFURY_H
#define COMMONFURY_H
#include <stdint.h>

#if defined(_WIN32)
#define CF_CALL __cdecl
#define CF_API __declspec(dllimport)
#else
#define CF_CALL
#define CF_API
#endif
#ifdef __cplusplus
extern "C" {
#endif

/* ABI 1.0; verified on Windows x64. Default platform alignment; do not pack.
   All lengths are bytes unless named count. Text is UTF-8 and not NUL-terminated.
   Inputs are borrowed only during a call; the library keeps no state besides output buffers.
   Functions are stateless and may be called from any thread.
   Release each nonzero buffer handle exactly once with cf_buffer_release; do not free its pointer.
   On failure, output buffers are empty (zero handle) unless documented otherwise.
   Keep the library loaded until process exit; unloading/reloading is unsupported. */
typedef uint64_t cf_handle;
enum cf_status {
    CF_OK = 0, CF_INVALID_ARGUMENT = 1, CF_INVALID_DATA = 2, CF_UNSUPPORTED = 3,
    CF_VALIDATION_FAILED = 4, CF_BUFFER_TOO_SMALL = 5, CF_OUT_OF_MEMORY = 6,
    CF_INTERNAL_ERROR = 7, CF_INVALID_HANDLE = 8
};

typedef struct cf_buffer {
    cf_handle handle;
    const uint8_t *data;
    uint64_t length;
} cf_buffer;

CF_API uint32_t CF_CALL cf_abi_version(void); /* major << 16 | minor */
/* Error text is per calling OS thread. required includes the trailing NUL.
   Query with NULL/0, then copy. Other status-returning calls clear the error on success. */
CF_API int32_t CF_CALL cf_error_copy(uint8_t *destination, uint64_t capacity, uint64_t *required);
CF_API int32_t CF_CALL cf_buffer_release(cf_handle handle); /* zero is a no-op */

/* Hashing. Jenkins name hashes lowercase ASCII, map '\' to '/', stop at NUL and treat a leading '"'
   as a quoted name. Continue appends without quote handling; finalize applies the final mix. */
CF_API int32_t CF_CALL cf_jenkins_hash(const uint8_t *text, uint64_t length, uint32_t *out_hash);
CF_API int32_t CF_CALL cf_jenkins_partial(const uint8_t *text, uint64_t length, uint32_t *out_state);
CF_API int32_t CF_CALL cf_jenkins_continue(uint32_t state, const uint8_t *text, uint64_t length, uint32_t *out_state);
CF_API uint32_t CF_CALL cf_jenkins_finalize(uint32_t state);
CF_API int32_t CF_CALL cf_jenkins_hash_case_sensitive(const uint8_t *text, uint64_t length, uint32_t *out_hash);
CF_API int32_t CF_CALL cf_pso_checksum(const uint8_t *data, uint64_t length, uint32_t *out_hash);
CF_API int32_t CF_CALL cf_lookup3_words(const uint32_t *words, uint64_t count, uint32_t seed, uint32_t *out_hash);
/* Little-endian 32-bit words; length must be a multiple of 4. */
CF_API int32_t CF_CALL cf_one_at_a_time_words(const uint8_t *data, uint64_t length, uint32_t seed, uint32_t *out_hash);
CF_API int32_t CF_CALL cf_elf_hash(const uint8_t *ascii, uint64_t length, uint32_t *out_hash);
/* Little-endian 16-bit words; length must be even. */
CF_API int32_t CF_CALL cf_fletcher32(const uint8_t *data, uint64_t length, uint32_t *out_hash);

/* Raw DEFLATE (RFC 1951). level: 0 optimal, 1 fastest, 2 none, 3 smallest. Decoding rejects truncated
   streams and trailing bytes. zero_history != 0 seeds a 32 KiB zero window (GTA V RSC7 resources). */
CF_API int32_t CF_CALL cf_deflate_compress(const uint8_t *data, uint64_t length, uint32_t level, cf_buffer *out_buffer);
CF_API int32_t CF_CALL cf_deflate_decompress(const uint8_t *data, uint64_t length, uint32_t maximum_length, uint32_t zero_history, cf_buffer *out_buffer);
/* Output must be exactly destination_length bytes. */
CF_API int32_t CF_CALL cf_deflate_decompress_exact(const uint8_t *data, uint64_t length, uint8_t *destination, uint64_t destination_length, uint32_t zero_history);

/* Textures. format is a DXGI_FORMAT value. dimension: 0 2D, 1 cube, 3 volume. depth and array_size are at least 1;
   array_size counts images or whole cubes. Payload order: array element, face, mip, then depth slice. */
typedef struct cf_texture_info {
    uint32_t format, width, height, mip_count, dimension, depth, array_size, reserved;
} cf_texture_info;
CF_API int32_t CF_CALL cf_dds_read(const uint8_t *data, uint64_t length, cf_texture_info *out_info, uint32_t info_size, cf_buffer *out_payload);
CF_API int32_t CF_CALL cf_dds_write(const cf_texture_info *info, uint32_t info_size, const uint8_t *payload, uint64_t length, cf_buffer *out_buffer);
CF_API int32_t CF_CALL cf_texture_data_size(const cf_texture_info *info, uint32_t info_size, uint64_t *out_size);
/* Uncompressed formats only. RGBA is 4 bytes per pixel; both buffers describe the same pixel count. */
CF_API int32_t CF_CALL cf_pixel_decode(uint32_t format, const uint8_t *source, uint64_t length, uint8_t *rgba, uint64_t rgba_length);
CF_API int32_t CF_CALL cf_pixel_encode(uint32_t format, const uint8_t *rgba, uint64_t length, uint8_t *destination, uint64_t destination_length);
/* BC1: 8 bytes per 4x4 block, ceil(width/4) * ceil(height/4) blocks. */
CF_API int32_t CF_CALL cf_bc1_encode(const uint8_t *rgba, uint64_t length, uint32_t width, uint32_t height, uint8_t *destination, uint64_t destination_length);
CF_API int32_t CF_CALL cf_bc1_decode(const uint8_t *blocks, uint64_t length, uint32_t width, uint32_t height, cf_buffer *out_rgba);

/* RBF (RBF0). Validation returns a UTF-8 JSON diagnostic array even on CF_VALIDATION_FAILED; release it either way.
   Rewrite reads and writes the canonical encoding. */
CF_API int32_t CF_CALL cf_rbf_validate(const uint8_t *data, uint64_t length, cf_buffer *out_diagnostics);
CF_API int32_t CF_CALL cf_rbf_rewrite(const uint8_t *data, uint64_t length, cf_buffer *out_buffer);

/* PSO (PSIN). flags: 1 allow aliased schema definitions (RDR2), 2 force 12-bit block ids (GTA V),
   4 force 20-bit block ids (RDR2); without 2 or 4 the layout is detected. */
enum cf_pso_flags { CF_PSO_ALLOW_ALIASED_SCHEMA = 1, CF_PSO_BLOCK_ID_12 = 2, CF_PSO_BLOCK_ID_20 = 4 };
typedef struct cf_pso_info {
    uint32_t pointer_bits, sections, blocks, root_block_id, structs, enums;
} cf_pso_info;
CF_API int32_t CF_CALL cf_pso_get_info(const uint8_t *data, uint64_t length, uint32_t flags, cf_pso_info *out_info, uint32_t info_size);
/* Decodes the whole graph; field errors become diagnostics. Same JSON and release rules as cf_rbf_validate. */
CF_API int32_t CF_CALL cf_pso_validate(const uint8_t *data, uint64_t length, uint32_t flags, cf_buffer *out_diagnostics);
CF_API int32_t CF_CALL cf_pso_rewrite(const uint8_t *data, uint64_t length, uint32_t flags, cf_buffer *out_buffer);

/* heightmap.dat (HMAP big-endian / PAMH little-endian). cell_size: 1 uint8, 2 uint16, 4 float32.
   layout: 0 trimmed rows, 1 full rows, 2 dense. byte_order: 0 big, 1 little.
   Cells are row-major; the water mask has one byte (0/1) per cell when has_water_mask is set. */
typedef struct cf_heightmap_info {
    uint32_t columns, rows, cell_size, flags, version, layout, byte_order, reserved, has_water_mask, padding;
    float minimum[3], maximum[3];
} cf_heightmap_info;
/* Inverted cells (minimum above maximum) are read and preserved unless reject_inverted_cells is nonzero.
   Writing reproduces the recorded layout and byte order; a non-canonical trimmed-rows source is re-trimmed. */
CF_API int32_t CF_CALL cf_heightmap_read(const uint8_t *data, uint64_t length, uint32_t reject_inverted_cells, cf_heightmap_info *out_info, uint32_t info_size,
    cf_buffer *out_minimum, cf_buffer *out_maximum, cf_buffer *out_water);
/* cell_bytes is the length of each of minimum and maximum. Pass NULL/0 water when has_water_mask is 0. */
CF_API int32_t CF_CALL cf_heightmap_write(const cf_heightmap_info *info, uint32_t info_size, const uint8_t *minimum, const uint8_t *maximum,
    uint64_t cell_bytes, const uint8_t *water, uint64_t water_length, cf_buffer *out_buffer);

/* Vertex elements by DXGI format. range selects DXGI 24 (R10G10B10A2) decoding: 0 [0,1] (GTA V), 1 [-1,1] (RDR2).
   values holds count XYZW floats; elements are stride bytes apart. Encoding is little-endian. */
CF_API int32_t CF_CALL cf_vertex_format_size(uint32_t dxgi, uint32_t range, uint32_t *out_size);
CF_API int32_t CF_CALL cf_vertex_decode(uint32_t dxgi, uint32_t range, uint32_t big_endian, const uint8_t *data, uint64_t length,
    uint32_t stride, float *values, uint64_t count);
CF_API int32_t CF_CALL cf_vertex_encode(uint32_t dxgi, uint32_t range, const float *values, uint64_t count, uint32_t stride,
    uint8_t *data, uint64_t length);

/* BVH over count boxes given as (min xyz, max xyz) floats. split: 0 upper median (GTA V), 1 lower median (RDR2).
   leaf_ids: 0 position in out_order (geometry), 1 caller index (composites). out_nodes holds preorder cf_bvh_node
   records; out_order holds int32 caller indices in leaf storage order. */
typedef struct cf_bvh_node {
    float minimum[3], maximum[3];
    uint16_t item_id;
    uint8_t item_count, reserved; /* item_count 0 marks a branch; item_id is then the escape (subtree size) */
} cf_bvh_node;
CF_API int32_t CF_CALL cf_bvh_build(const float *boxes, uint64_t count, uint32_t leaf_size, uint32_t split, uint32_t leaf_ids,
    cf_buffer *out_nodes, cf_buffer *out_order);

#ifdef __cplusplus
}
#endif
#endif
