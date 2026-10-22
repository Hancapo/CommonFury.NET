"""Functional checks of the published commonfury_native library through its C ABI.

Usage: python CommonFury.Native/tests/test_native.py [path/to/commonfury_native.dll]
COMMONFURY_NATIVE_LIBRARY also selects the library. Reference data comes from CommonFury.Tests/ReferenceData.
"""
import ctypes as C
import json
import os
import struct
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / "CommonFury.Tests" / "ReferenceData"
LIBRARY = (sys.argv.pop(1) if len(sys.argv) > 1 and not sys.argv[1].startswith("-") else None) \
    or os.environ.get("COMMONFURY_NATIVE_LIBRARY") or str(ROOT / "artifacts" / "native" / "win-x64" / "commonfury_native.dll")
OK, INVALID_ARGUMENT, INVALID_DATA, UNSUPPORTED, VALIDATION_FAILED, BUFFER_TOO_SMALL = 0, 1, 2, 3, 4, 5
INVALID_HANDLE = 8


class Buffer(C.Structure):
    _fields_ = [("handle", C.c_uint64), ("data", C.POINTER(C.c_uint8)), ("length", C.c_uint64)]


class TextureInfo(C.Structure):
    _fields_ = [(name, C.c_uint32) for name in ("format", "width", "height", "mip_count", "dimension", "depth", "array_size", "reserved")]


class PsoInfo(C.Structure):
    _fields_ = [(name, C.c_uint32) for name in ("pointer_bits", "sections", "blocks", "root_block_id", "structs", "enums")]


class HeightMapInfo(C.Structure):
    _fields_ = [(name, C.c_uint32) for name in ("columns", "rows", "cell_size", "flags", "version", "layout", "byte_order",
                                                 "reserved", "has_water_mask", "padding")] + \
               [("minimum", C.c_float * 3), ("maximum", C.c_float * 3)]


class BvhNode(C.Structure):
    _fields_ = [("minimum", C.c_float * 3), ("maximum", C.c_float * 3), ("item_id", C.c_uint16),
                ("item_count", C.c_uint8), ("reserved", C.c_uint8)]


lib = C.CDLL(LIBRARY)
u8p, u32, u64 = C.c_char_p, C.c_uint32, C.c_uint64
P = C.POINTER
signatures = {
    "cf_abi_version": (u32, []),
    "cf_error_copy": (C.c_int32, [C.c_void_p, u64, P(u64)]),
    "cf_buffer_release": (C.c_int32, [u64]),
    "cf_jenkins_hash": (C.c_int32, [u8p, u64, P(u32)]),
    "cf_jenkins_partial": (C.c_int32, [u8p, u64, P(u32)]),
    "cf_jenkins_continue": (C.c_int32, [u32, u8p, u64, P(u32)]),
    "cf_jenkins_finalize": (u32, [u32]),
    "cf_jenkins_hash_case_sensitive": (C.c_int32, [u8p, u64, P(u32)]),
    "cf_pso_checksum": (C.c_int32, [u8p, u64, P(u32)]),
    "cf_lookup3_words": (C.c_int32, [P(u32), u64, u32, P(u32)]),
    "cf_one_at_a_time_words": (C.c_int32, [u8p, u64, u32, P(u32)]),
    "cf_elf_hash": (C.c_int32, [u8p, u64, P(u32)]),
    "cf_fletcher32": (C.c_int32, [u8p, u64, P(u32)]),
    "cf_deflate_compress": (C.c_int32, [u8p, u64, u32, P(Buffer)]),
    "cf_deflate_decompress": (C.c_int32, [u8p, u64, u32, u32, P(Buffer)]),
    "cf_deflate_decompress_exact": (C.c_int32, [u8p, u64, C.c_void_p, u64, u32]),
    "cf_dds_read": (C.c_int32, [u8p, u64, P(TextureInfo), u32, P(Buffer)]),
    "cf_dds_write": (C.c_int32, [P(TextureInfo), u32, u8p, u64, P(Buffer)]),
    "cf_texture_data_size": (C.c_int32, [P(TextureInfo), u32, P(u64)]),
    "cf_pixel_decode": (C.c_int32, [u32, u8p, u64, C.c_void_p, u64]),
    "cf_pixel_encode": (C.c_int32, [u32, u8p, u64, C.c_void_p, u64]),
    "cf_bc1_encode": (C.c_int32, [u8p, u64, u32, u32, C.c_void_p, u64]),
    "cf_bc1_decode": (C.c_int32, [u8p, u64, u32, u32, P(Buffer)]),
    "cf_rbf_validate": (C.c_int32, [u8p, u64, P(Buffer)]),
    "cf_rbf_rewrite": (C.c_int32, [u8p, u64, P(Buffer)]),
    "cf_pso_get_info": (C.c_int32, [u8p, u64, u32, P(PsoInfo), u32]),
    "cf_pso_validate": (C.c_int32, [u8p, u64, u32, P(Buffer)]),
    "cf_pso_rewrite": (C.c_int32, [u8p, u64, u32, P(Buffer)]),
    "cf_heightmap_read": (C.c_int32, [u8p, u64, u32, P(HeightMapInfo), u32, P(Buffer), P(Buffer), P(Buffer)]),
    "cf_heightmap_write": (C.c_int32, [P(HeightMapInfo), u32, u8p, u8p, u64, u8p, u64, P(Buffer)]),
    "cf_vertex_format_size": (C.c_int32, [u32, u32, P(u32)]),
    "cf_vertex_decode": (C.c_int32, [u32, u32, u32, u8p, u64, u32, P(C.c_float), u64]),
    "cf_vertex_encode": (C.c_int32, [u32, u32, P(C.c_float), u64, u32, C.c_void_p, u64]),
    "cf_bvh_build": (C.c_int32, [P(C.c_float), u64, u32, u32, u32, P(Buffer), P(Buffer)]),
}
for name, (restype, argtypes) in signatures.items():
    function = getattr(lib, name)
    function.restype, function.argtypes = restype, argtypes


def error():
    required = u64()
    lib.cf_error_copy(None, 0, C.byref(required))
    text = C.create_string_buffer(required.value)
    assert lib.cf_error_copy(text, required.value, C.byref(required)) == OK
    return text.value.decode()


def check(status):
    if status != OK:
        raise AssertionError(f"status {status}: {error()}")


def take(buffer):
    """Copies an owned buffer and releases it."""
    data = C.string_at(buffer.data, buffer.length) if buffer.length else b""
    check(lib.cf_buffer_release(buffer.handle))
    return data


def hash_call(name, data, *extra):
    result = u32()
    check(getattr(lib, name)(*extra[:1], data, len(data), *extra[1:], C.byref(result)) if name == "cf_jenkins_continue"
          else getattr(lib, name)(data, len(data), *extra, C.byref(result)))
    return result.value


class Core(unittest.TestCase):
    def test_version_errors_and_buffers(self):
        self.assertEqual(lib.cf_abi_version(), 0x00010000)
        self.assertEqual(lib.cf_buffer_release(0), OK)
        self.assertEqual(lib.cf_buffer_release(123456789), INVALID_HANDLE)
        self.assertIn("buffer handle", error())
        out = Buffer()
        check(lib.cf_deflate_compress(b"x", 1, 0, C.byref(out)))
        self.assertEqual(take(out) != b"", True)
        self.assertEqual(lib.cf_buffer_release(out.handle), INVALID_HANDLE)  # released exactly once
        required = u64()
        self.assertEqual(lib.cf_error_copy(None, 0, C.byref(required)), BUFFER_TOO_SMALL)


class Hashing(unittest.TestCase):
    reference = json.loads((DATA / "hashing.json").read_text(encoding="utf-8"))

    def test_jenkins_vectors(self):
        self.assertEqual(hash_call("cf_jenkins_hash", b"a"), 3392050242)
        for item in self.reference["hashes"]:
            text = item["text"].encode()
            self.assertEqual(hash_call("cf_jenkins_hash", text), item["hash"])
            partial = hash_call("cf_jenkins_partial", text)
            self.assertEqual(partial, item["partial"])
            self.assertEqual(lib.cf_jenkins_finalize(partial), item["hash"])
            self.assertEqual(hash_call("cf_jenkins_continue", text, 0x89ABCDEF), item["continued"])
        self.assertEqual(hash_call("cf_jenkins_hash_case_sensitive", b"cmapdata"), hash_call("cf_jenkins_hash", b"CMapData"))
        self.assertNotEqual(hash_call("cf_jenkins_hash_case_sensitive", b"CMapData"), hash_call("cf_jenkins_hash", b"CMapData"))
        self.assertEqual(lib.cf_jenkins_hash_case_sensitive(b"\xff", 1, C.byref(u32())), INVALID_DATA)

    def test_checksums_and_words(self):
        import base64
        for item in self.reference["checksums"]:
            data = base64.b64decode(item["data"])
            self.assertEqual(hash_call("cf_pso_checksum", data), item["pso"])
            self.assertEqual(hash_call("cf_fletcher32", data), item["fletcher"])
        self.assertEqual(lib.cf_fletcher32(b"x", 1, C.byref(u32())), INVALID_ARGUMENT)
        for item in self.reference["words"]:
            words = (u32 * len(item["values"]))(*item["values"])
            result = u32()
            check(lib.cf_lookup3_words(words, len(item["values"]), item["seed"], C.byref(result)))
            self.assertEqual(result.value, item["hash"])
        data = bytes(range(32))
        state = 0x1234
        for (word,) in struct.iter_unpack("<I", data):
            state = (state + word) & 0xFFFFFFFF
            state = (state + (state << 10)) & 0xFFFFFFFF
            state ^= state >> 6
        state = (state + (state << 3)) & 0xFFFFFFFF
        state ^= state >> 11
        state = (state + (state << 15)) & 0xFFFFFFFF
        self.assertEqual(hash_call("cf_one_at_a_time_words", data, 0x1234), state)
        elf = 0
        for ch in b"Root_Bone":
            elf = (elf << 4) + ch
            high = elf & 0xF0000000
            elf = (elf ^ (high >> 24)) & ~high & 0xFFFFFFFF
        self.assertEqual(hash_call("cf_elf_hash", b"Root_Bone"), elf)


class Deflate(unittest.TestCase):
    fixture = bytes.fromhex("7374C40F00")  # zlib.deflateRawSync(Buffer.alloc(32, 65))

    def test_external_fixture_and_round_trip(self):
        out = Buffer()
        check(lib.cf_deflate_decompress(self.fixture, len(self.fixture), 32, 0, C.byref(out)))
        self.assertEqual(take(out), b"A" * 32)
        exact = C.create_string_buffer(32)
        check(lib.cf_deflate_decompress_exact(self.fixture, len(self.fixture), exact, 32, 1))
        self.assertEqual(exact.raw, b"A" * 32)
        payload = bytes(range(256)) * 300
        for level in range(4):
            check(lib.cf_deflate_compress(payload, len(payload), level, C.byref(out)))
            compressed = take(out)
            check(lib.cf_deflate_decompress(compressed, len(compressed), len(payload), 0, C.byref(out)))
            self.assertEqual(take(out), payload)
        check(lib.cf_deflate_compress(b"", 0, 0, C.byref(out)))
        self.assertEqual(take(out), b"\x03\x00")

    def test_strict_framing(self):
        out = Buffer()
        self.assertEqual(lib.cf_deflate_decompress(self.fixture + b"\0", 6, 32, 0, C.byref(out)), INVALID_DATA)
        self.assertEqual(out.handle, 0)
        self.assertEqual(lib.cf_deflate_decompress(self.fixture[:3], 3, 32, 0, C.byref(out)), INVALID_DATA)
        self.assertEqual(lib.cf_deflate_decompress(self.fixture, 5, 31, 0, C.byref(out)), INVALID_DATA)
        self.assertEqual(lib.cf_deflate_decompress_exact(self.fixture, 5, C.create_string_buffer(31), 31, 0), INVALID_DATA)
        self.assertEqual(lib.cf_deflate_compress(b"x", 1, 9, C.byref(out)), INVALID_ARGUMENT)


class Textures(unittest.TestCase):
    def test_dds_round_trip(self):
        for name in ("dds-Color.dds", "dds-normal.dds", "dds-Mixed_Diffuse.dds"):
            data = (DATA / name).read_bytes()
            info, payload = TextureInfo(), Buffer()
            check(lib.cf_dds_read(data, len(data), C.byref(info), C.sizeof(info), C.byref(payload)))
            pixels = take(payload)
            size = u64()
            check(lib.cf_texture_data_size(C.byref(info), C.sizeof(info), C.byref(size)))
            self.assertEqual(size.value, len(pixels), name)
            out = Buffer()
            check(lib.cf_dds_write(C.byref(info), C.sizeof(info), pixels, len(pixels), C.byref(out)))
            written = take(out)
            again = TextureInfo()
            check(lib.cf_dds_read(written, len(written), C.byref(again), C.sizeof(again), C.byref(payload)))
            self.assertEqual(take(payload), pixels)
            self.assertEqual(bytes(again), bytes(info))
        self.assertEqual(lib.cf_dds_read(b"DDS ", 4, C.byref(TextureInfo()), C.sizeof(TextureInfo), C.byref(Buffer())), INVALID_DATA)
        self.assertEqual(lib.cf_dds_read(b"", 0, C.byref(TextureInfo()), 4, C.byref(Buffer())), INVALID_ARGUMENT)

    def test_pixels_and_bc1(self):
        rgba = bytes([10, 20, 30, 40, 200, 150, 100, 255])
        for dxgi in (28, 87):  # R8G8B8A8_UNORM, B8G8R8A8_UNORM
            encoded = C.create_string_buffer(8)
            check(lib.cf_pixel_encode(dxgi, rgba, 8, encoded, 8))
            decoded = C.create_string_buffer(8)
            check(lib.cf_pixel_decode(dxgi, encoded.raw, 8, decoded, 8))
            self.assertEqual(decoded.raw, rgba)
        self.assertEqual(lib.cf_pixel_encode(71, rgba, 8, C.create_string_buffer(8), 8), UNSUPPORTED)  # BC1 is block-compressed
        solid = bytes([255, 0, 0, 255]) * 16
        blocks = C.create_string_buffer(8)
        check(lib.cf_bc1_encode(solid, len(solid), 4, 4, blocks, 8))
        out = Buffer()
        check(lib.cf_bc1_decode(blocks.raw, 8, 4, 4, C.byref(out)))
        self.assertEqual(take(out), solid)


class Metadata(unittest.TestCase):
    def test_rbf(self):
        for name in ("tree.ymt", "gtxd.ymt"):
            data = (DATA / "rbf" / name).read_bytes()
            out = Buffer()
            check(lib.cf_rbf_validate(data, len(data), C.byref(out)))
            self.assertEqual(json.loads(take(out)), [])
            check(lib.cf_rbf_rewrite(data, len(data), C.byref(out)))
            self.assertEqual(take(out), data, name)
        self.assertEqual(lib.cf_rbf_rewrite(b"RBF0", 4, C.byref(Buffer())), INVALID_DATA)

    def test_pso_both_pointer_layouts(self):
        expected_bits = {"all_types.pso": 12, "blockid20_sample.pso": 20, "blockid20_paged_txd.ymf": 20}
        for name, bits in expected_bits.items():
            data = (DATA / "pso" / name).read_bytes()
            flags = 1 if name.startswith("blockid20") else 0
            info = PsoInfo()
            check(lib.cf_pso_get_info(data, len(data), flags, C.byref(info), C.sizeof(info)))
            self.assertEqual(info.pointer_bits, bits, name)
            self.assertGreater(info.structs, 0)
            out = Buffer()
            status = lib.cf_pso_validate(data, len(data), flags, C.byref(out))
            diagnostics = json.loads(take(out))
            self.assertIn(status, (OK, VALIDATION_FAILED))
            self.assertEqual(status == OK, not any(d["severity"] == "error" for d in diagnostics))
            check(lib.cf_pso_rewrite(data, len(data), flags, C.byref(out)))
            rewritten = take(out)
            again = PsoInfo()
            check(lib.cf_pso_get_info(rewritten, len(rewritten), flags, C.byref(again), C.sizeof(again)))
            self.assertEqual(bytes(again), bytes(info), name)
        self.assertEqual(lib.cf_pso_get_info(b"PSIN", 4, 6, C.byref(PsoInfo()), C.sizeof(PsoInfo)), INVALID_ARGUMENT)


class HeightMaps(unittest.TestCase):
    def read(self, data, reject=0):
        info, low, high, water = HeightMapInfo(), Buffer(), Buffer(), Buffer()
        status = lib.cf_heightmap_read(data, len(data), reject, C.byref(info), C.sizeof(info), C.byref(low), C.byref(high), C.byref(water))
        if status != OK:
            self.assertEqual((low.handle, high.handle, water.handle), (0, 0, 0))
            return status, None
        return status, (info, take(low), take(high), take(water))

    def write(self, info, minimum, maximum, mask):
        out = Buffer()
        check(lib.cf_heightmap_write(C.byref(info), C.sizeof(info), minimum, maximum, len(minimum), mask or None, len(mask), C.byref(out)))
        return take(out)

    def test_valid_corpus_matches_reference_encodings(self):
        files = sorted((DATA / "heightmaps" / "valid").glob("*.hmap"))
        self.assertTrue(files)
        for path in files:
            data = path.read_bytes()
            status, (info, minimum, maximum, mask) = self.read(data)
            self.assertEqual(len(minimum), info.columns * info.rows * info.cell_size, path.name)
            self.assertEqual(bool(mask), bool(info.has_water_mask), path.name)
            recorded = self.write(info, minimum, maximum, mask)
            # Same rules as the managed suite: canonical layout is trimmed rows for row-compressed (flag 1) sources, else dense.
            canonical_info = HeightMapInfo.from_buffer_copy(info)
            canonical_info.layout = 0 if info.flags & 1 else 2
            canonical = self.write(canonical_info, minimum, maximum, mask)
            self.assertEqual(canonical, Path(str(path) + ".canonical").read_bytes(), path.name)
            dense_info = HeightMapInfo.from_buffer_copy(info)
            dense_info.layout, dense_info.byte_order = 2, 0
            self.assertEqual(self.write(dense_info, minimum, maximum, mask), Path(str(path) + ".dense").read_bytes(), path.name)
            if info.layout != 0 or data == canonical:
                self.assertEqual(recorded, data, path.name)

    def test_invalid_corpus_is_rejected(self):
        files = sorted((DATA / "heightmaps" / "invalid").glob("*.hmap"))
        self.assertTrue(files)
        for path in files:
            reject = 1 if path.name == "inverted_cell.hmap" else 0
            status, _ = self.read(path.read_bytes(), reject)
            self.assertNotEqual(status, OK, path.name)
        status, _ = self.read((DATA / "heightmaps" / "invalid" / "inverted_cell.hmap").read_bytes())
        self.assertEqual(status, OK)  # preserved by default


class Geometry(unittest.TestCase):
    def round_trip(self, dxgi, range_, values, tolerance):
        size = u32()
        check(lib.cf_vertex_format_size(dxgi, range_, C.byref(size)))
        count = len(values) // 4
        stride = size.value + 3
        data = C.create_string_buffer(stride * count)
        source = (C.c_float * len(values))(*values)
        check(lib.cf_vertex_encode(dxgi, range_, source, count, stride, data, len(data)))
        decoded = (C.c_float * len(values))()
        check(lib.cf_vertex_decode(dxgi, range_, 0, data.raw, len(data), stride, decoded, count))
        for got, want in zip(decoded, values):
            self.assertAlmostEqual(got, want, delta=tolerance)
        return size.value

    def test_vertex_formats(self):
        self.assertEqual(self.round_trip(6, 0, [1.5, -2.25, 3.0, 0.0, 4.0, 5.0, 6.0, 0.0], 0), 12)  # R32G32B32_FLOAT
        self.assertEqual(self.round_trip(10, 0, [0.5, -1.0, 2.0, 1.0], 1e-3), 8)  # R16G16B16A16_FLOAT
        self.assertEqual(self.round_trip(28, 0, [0.0, 0.5, 1.0, 1.0], 1 / 255), 4)  # R8G8B8A8_UNORM
        self.round_trip(24, 0, [0.0, 0.5, 1.0, 1.0], 1 / 1023)
        self.round_trip(24, 1, [-1.0, 0.0, 1.0, 1.0], 2 / 1023)
        packed = struct.pack("<I", 1023 | (0 << 10) | (1023 << 20) | (3 << 30))
        out = (C.c_float * 4)()
        check(lib.cf_vertex_decode(24, 0, 0, packed, 4, 4, out, 1))
        self.assertEqual(list(out)[:3], [1.0, 0.0, 1.0])
        check(lib.cf_vertex_decode(24, 1, 0, packed, 4, 4, out, 1))
        self.assertEqual(list(out)[:3], [1.0, -1.0, 1.0])
        self.assertEqual(lib.cf_vertex_format_size(24, 2, C.byref(u32())), INVALID_ARGUMENT)

    def test_bvh(self):
        boxes = []
        for i in range(8):
            boxes += [i * 2.0, 0.0, 0.0, i * 2.0 + 1.0, 1.0, 1.0]
        source = (C.c_float * len(boxes))(*boxes)
        for split in (0, 1):
            nodes, order = Buffer(), Buffer()
            check(lib.cf_bvh_build(source, 8, 4, split, 0, C.byref(nodes), C.byref(order)))
            raw_nodes, raw_order = take(nodes), take(order)
            parsed = [BvhNode.from_buffer_copy(raw_nodes, i * C.sizeof(BvhNode)) for i in range(len(raw_nodes) // C.sizeof(BvhNode))]
            indices = list(struct.unpack(f"<{len(raw_order) // 4}i", raw_order))
            self.assertEqual(sorted(indices), list(range(8)))
            self.assertEqual(sum(n.item_count for n in parsed), 8)
            self.assertEqual(parsed[0].item_count, 0)
            self.assertEqual(parsed[0].item_id, len(parsed))
            self.assertEqual(list(parsed[0].minimum) + list(parsed[0].maximum), [0, 0, 0, 15, 1, 1])
        self.assertEqual(lib.cf_bvh_build(source, 8, 4, 2, 0, C.byref(Buffer()), C.byref(Buffer())), INVALID_ARGUMENT)


if __name__ == "__main__":
    unittest.main(verbosity=1)
