"""
UE5.6 AssetRegistry.bin surgical append-patcher.

Strategy: clone an existing AssetData record (the "source") into a new one
with a different name (the "target"), without mutating any vanilla entries.

Key insight: cooked AR.bin has NumDependsNodes=0 and NumPackageData=0, so we
only need to extend the NameMap and append an AssetData record. No SHA-1
computation, no DependsNode graph patching, no PackageData cloning.

Operations:
  1. NameMap extend: append new FName entries (asset_name + full_path) with
     correctly computed CityHash64 hashes (lowercase UTF-8).
  2. AssetData section: byte-clone the source record, remap FName indices
     to the new names, append at end of section, bump NumAssets counter.
  3. FStore: kept byte-identical (tag pair-range can be shared because the
     cloned asset has identical tags as the source).
  4. Dep/Pkg sections: passed through unchanged (cooked AR has them empty).

Header counters updated:
  - NumNames (u32 @ 0x18): += number of new FNames
  - NumStringBytes (u32 @ 0x1C): += total bytes of new strings
"""

import struct
import sys
from pathlib import Path
from typing import List, Tuple

import cityhash

sys.path.insert(0, str(Path(__file__).parent))
from ar_parser import (
    ARParser, AR_MAGIC, ASSET_REGISTRY_NUMBERED_NAME_BIT,
    FAssetData, FName, FSTORE_END_MAGIC,
)


def cityhash64_lower_utf8(name: str) -> int:
    return cityhash.CityHash64(name.lower().encode("utf-8"))


def encode_fname_header(length: int, is_wide: bool) -> bytes:
    """FSerializedNameHeader: 2 bytes BE, bit15=isWide, low15=length."""
    raw = (0x8000 if is_wide else 0) | (length & 0x7FFF)
    return struct.pack(">H", raw)


def encode_fname_wire(idx: int, number: int = 0) -> bytes:
    """FName wire encoding: u32 idx; if number != 0, also u32 number with high
    bit of idx set."""
    if number != 0:
        return struct.pack("<II", idx | ASSET_REGISTRY_NUMBERED_NAME_BIT, number)
    return struct.pack("<I", idx)


class ARPatcher:
    def __init__(self, ar_path: Path):
        self.ar_path = ar_path
        self.data = ar_path.read_bytes()
        self.parser = ARParser(self.data)
        self.parser.parse_all()

    def _fname_index_of(self, name: str) -> int:
        idx = self.parser.name_map.name_to_index.get(name)
        if idx is None:
            raise KeyError(f"FName not in vanilla NameMap: {name!r}")
        return idx

    def _append_fname(self, name: str) -> Tuple[int, int, int]:
        """Append a new FName to NameMap state. Returns (new_index, hash_bytes,
        header_bytes, string_bytes)."""
        nm = self.parser.name_map
        new_idx = len(nm.names)
        h = cityhash64_lower_utf8(name)
        # ANSI only (our names are pure ASCII)
        encoded = name.encode("utf-8")
        if any(c > 0x7F for c in encoded):
            raise NotImplementedError("Wide name support not implemented")
        nm.names.append(name)
        nm.name_to_index[name] = new_idx
        nm.hashes.append(h)
        nm.headers.append(len(encoded) & 0x7FFF)
        return new_idx

    @staticmethod
    def _decode_tag_handle(h: int) -> Tuple[int, int, bool]:
        """Decode FPartialMapHandle u64: (pair_begin, num, has_numberless_keys).
        Encoding: bit63 = has_numberless, bits47..32 = num (u16), low32 = pair_begin."""
        pair_begin = h & 0xFFFFFFFF
        num = (h >> 32) & 0xFFFF
        has_numberless = bool(h & (1 << 63))
        return pair_begin, num, has_numberless

    @staticmethod
    def _encode_tag_handle(pair_begin: int, num: int, has_numberless: bool) -> int:
        flag = (1 << 63) if has_numberless else 0
        return flag | ((num & 0xFFFF) << 32) | (pair_begin & 0xFFFFFFFF)

    @staticmethod
    def _decode_value_id(v: int) -> Tuple[int, int]:
        """value_id: low 3 bits = type, high 29 = index."""
        return (v >> 3), (v & 7)

    @staticmethod
    def _encode_value_id(index: int, type_: int) -> int:
        return ((index & 0x1FFFFFFF) << 3) | (type_ & 7)

    # Value-id types (from CUE4Parse FValueId)
    VTYPE_ANSI_STRING = 0
    VTYPE_WIDE_STRING = 1
    VTYPE_NUMBERLESS_NAME = 2
    VTYPE_NAME = 3
    VTYPE_NUMBERLESS_EXPORT_PATH = 4
    VTYPE_EXPORT_PATH = 5
    VTYPE_LOCALIZED_TEXT = 6

    def clone_asset(self, source_full_path: str, target_full_path: str) -> None:
        """Clone the AssetData record for source_full_path into a new record
        whose PackageName & AssetName point to target_full_path AND whose
        PrimaryAssetName tag points to the new asset (avoiding the
        AssetManager duplicate-PrimaryAssetID conflict observed in B2.6)."""
        # Resolve source
        src_pkg_idx = self._fname_index_of(source_full_path)
        src_asset_simple = source_full_path.rsplit("/", 1)[-1]
        src_asset_idx = self._fname_index_of(src_asset_simple)

        # Find source AssetData
        matches = [a for a in self.parser.assets if a.package_name.index == src_pkg_idx]
        if not matches:
            raise ValueError(f"No AssetData record found for {source_full_path}")
        if len(matches) > 1:
            raise ValueError(f"Multiple AssetData records found for {source_full_path}")
        src_asset = matches[0]

        # Resolve target (append new FNames)
        tgt_asset_simple = target_full_path.rsplit("/", 1)[-1]
        tgt_pkg_idx = self._append_fname(target_full_path)
        tgt_asset_idx = self._append_fname(tgt_asset_simple)

        # ===== Build new tag-pair range for cloned asset =====
        # Source tags layout (from forensic decode of vanilla Bedroll):
        #   pair[base+0] : NativeClass         = ANSI string (shared, reuse value_id)
        #   pair[base+1] : PrimaryAssetName    = NumberlessName -> source FName (REPLACE)
        #   pair[base+2] : PrimaryAssetType    = NumberlessName -> R5BuildingItem (shared)
        # We only need to rewrite pair[base+1]'s value to point to a new
        # NumberlessName entry that references our target FName.
        src_pair_begin, src_num, src_has_numberless = self._decode_tag_handle(src_asset.tag_handle_u64)
        if not src_has_numberless:
            raise NotImplementedError(
                "Source asset has no numberless keys flag - non-trivial Pairs path "
                "(not seen for R5BuildingItem in vanilla AR)."
            )
        fs = self.parser.fstore_info
        if src_pair_begin + src_num > len(fs.numberless_pairs):
            raise ValueError(
                f"Source pair range {src_pair_begin}..{src_pair_begin+src_num} "
                f"out of bounds (have {len(fs.numberless_pairs)})"
            )
        src_pairs = fs.numberless_pairs[src_pair_begin:src_pair_begin + src_num]

        nm = self.parser.name_map
        primary_asset_name_key = nm.name_to_index.get("PrimaryAssetName")
        if primary_asset_name_key is None:
            raise ValueError("FName 'PrimaryAssetName' not in vanilla NameMap")

        # Build a new NumberlessName pointing at the target asset's FName.
        # NumberlessNames is a list of u32 = FName indices. Append target FName idx.
        new_numberless_name_idx = len(fs.numberless_names)
        fs.numberless_names.append(tgt_asset_idx)

        # Build the new pairs by cloning source-pair tuples and rewriting
        # the PrimaryAssetName value to point at our new NumberlessName.
        new_pairs: List[Tuple[int, int]] = []
        rewrote = 0
        for key, value_id in src_pairs:
            if key == primary_asset_name_key:
                new_value_id = self._encode_value_id(
                    new_numberless_name_idx, self.VTYPE_NUMBERLESS_NAME
                )
                new_pairs.append((key, new_value_id))
                rewrote += 1
            else:
                new_pairs.append((key, value_id))
        if rewrote != 1:
            raise ValueError(
                f"Expected exactly 1 PrimaryAssetName tag in source pair-range, "
                f"got {rewrote} (source pairs: {src_pairs})"
            )

        # Append new pairs at end of NumberlessPairs section.
        new_pair_begin = len(fs.numberless_pairs)
        fs.numberless_pairs.extend(new_pairs)
        # Update FStore num counters.
        fs.nums[0] += 1                # NumberlessNames count
        fs.nums[9] += len(new_pairs)   # NumberlessPairs count

        # Compose new tag handle for the cloned asset.
        new_tag_handle = self._encode_tag_handle(
            new_pair_begin, len(new_pairs), has_numberless=True
        )

        # ===== Build new AssetData record =====
        out = bytearray()
        out += encode_fname_wire(src_asset.package_path.index, src_asset.package_path.number)
        out += encode_fname_wire(src_asset.asset_class_pkg.index, src_asset.asset_class_pkg.number)
        out += encode_fname_wire(src_asset.asset_class_name.index, src_asset.asset_class_name.number)
        out += encode_fname_wire(tgt_pkg_idx, 0)
        out += encode_fname_wire(tgt_asset_idx, 0)
        out += struct.pack("<Q", new_tag_handle)
        out += src_asset.bundle_data_raw
        # B3.1: try chunk_ids=[] (empty) instead of inheriting [0] from vanilla
        # vanilla [0] = "default chunk" which only contains vanilla content.
        # Our QmBedrl lives in a mod-pak chunk, not the default chunk - so
        # maybe an empty list signals "use default lookup via PackageStore" instead.
        chunk_ids_to_use = []  # was: src_asset.chunk_ids
        out += struct.pack("<i", len(chunk_ids_to_use))
        for cid in chunk_ids_to_use:
            out += struct.pack("<i", cid)
        out += struct.pack("<I", src_asset.package_flags)

        self._appended_asset_bytes = bytes(out)
        self._appended_asset_count = 1
        # Stash diagnostic info
        self._diag = {
            "src_pair_begin": src_pair_begin,
            "src_num": src_num,
            "src_pairs": src_pairs,
            "new_pair_begin": new_pair_begin,
            "new_pairs": new_pairs,
            "new_numberless_name_idx": new_numberless_name_idx,
            "new_tag_handle": new_tag_handle,
        }

    def serialize(self) -> bytes:
        """Serialize the patched AR.bin to bytes."""
        nm = self.parser.name_map
        h = self.parser.header

        out = bytearray()

        # ===== Header =====
        out += AR_MAGIC
        out += struct.pack("<i", h.version)
        out += struct.pack("<i", h.b_filter_editor_only)
        new_num_names = len(nm.names)
        # Compute new string-byte total
        new_string_bytes = 0
        encoded_strings: List[bytes] = []
        for i, name in enumerate(nm.names):
            hdr = nm.headers[i]
            is_wide = bool(hdr & 0x8000)
            if is_wide:
                b = name.encode("utf-16-le")
            else:
                b = name.encode("utf-8")
            encoded_strings.append(b)
            new_string_bytes += len(b)
        out += struct.pack("<I", new_num_names)
        out += struct.pack("<I", new_string_bytes)
        out += struct.pack("<Q", h.hash_version)

        # ===== NameMap =====
        # Hashes
        for hsh in nm.hashes:
            out += struct.pack("<Q", hsh)
        # Headers (u16 BE)
        for hdr in nm.headers:
            out += struct.pack(">H", hdr)
        # Strings (no terminators)
        for b in encoded_strings:
            out += b

        # ===== FStore (re-emitted with extended NumberlessNames + NumberlessPairs) =====
        fs = self.parser.fstore_info
        # Magic
        out += struct.pack("<I", fs.magic)
        # 11 i32 nums (with updated counts)
        for n in fs.nums:
            out += struct.pack("<i", n)
        # 4-byte pad (text-first variant)
        out += self.data[fs.pad_after_nums:fs.pad_after_nums + 4]
        # Texts (passthrough)
        out += self.data[fs.texts_start:fs.texts_end]
        # NumberlessNames: passthrough original + appended entries
        out += self.data[fs.numberless_names_start:fs.numberless_names_end]
        original_nn_count = (fs.numberless_names_end - fs.numberless_names_start) // 4
        for nn in fs.numberless_names[original_nn_count:]:
            out += struct.pack("<I", nn)
        # Names (passthrough)
        out += self.data[fs.names_start:fs.names_end]
        # NumberlessExportPaths (passthrough)
        out += self.data[fs.numberless_export_paths_start:fs.numberless_export_paths_end]
        # ExportPaths (passthrough)
        out += self.data[fs.export_paths_start:fs.export_paths_end]
        # AnsiStringOffsets / WideStringOffsets / AnsiStrings / WideStrings (passthrough)
        out += self.data[fs.ansi_string_offsets_start:fs.ansi_string_offsets_end]
        out += self.data[fs.wide_string_offsets_start:fs.wide_string_offsets_end]
        out += self.data[fs.ansi_strings_start:fs.ansi_strings_end]
        out += self.data[fs.wide_strings_start:fs.wide_strings_end]
        # NumberlessPairs: passthrough original + appended pairs
        out += self.data[fs.numberless_pairs_start:fs.numberless_pairs_end]
        original_np_count = (fs.numberless_pairs_end - fs.numberless_pairs_start) // 8
        for (k, v) in fs.numberless_pairs[original_np_count:]:
            out += struct.pack("<II", k, v)
        # Pairs (passthrough)
        out += self.data[fs.pairs_start:fs.pairs_end]
        # End magic
        out += struct.pack("<I", FSTORE_END_MAGIC)

        # ===== AssetData section =====
        # NumAssets (i32) + each record byte-passthrough + appended records
        new_num_assets = self.parser.num_assets + getattr(self, "_appended_asset_count", 0)
        out += struct.pack("<i", new_num_assets)
        # Vanilla assets byte-passthrough (offsets in self.data didn't change)
        assets_data_start = self.parser.assets_start + 4  # after i32 count
        out += self.data[assets_data_start:self.parser.assets_end]
        # Appended assets
        if hasattr(self, "_appended_asset_bytes"):
            out += self._appended_asset_bytes

        # ===== Tail (Deps + Pkg) byte-identical pass-through =====
        # Source layout: i64 dep_size + i32 num_nodes + i32 num_pkg
        # For cooked AR all are 4-byte zeros after i64 size=4.
        tail_start_in_src = self.parser.assets_end
        tail = self.data[tail_start_in_src:]
        out += tail

        return bytes(out)


def main():
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="ar_in", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--source", required=True,
                    help="Full asset path to clone, e.g. /Game/.../DA_BI_Bedroll_01")
    ap.add_argument("--target", required=True,
                    help="New full asset path, e.g. /Game/.../DA_BI_QmBedrl_01")
    args = ap.parse_args()

    patcher = ARPatcher(Path(args.ar_in))
    print(f"Loaded vanilla AR: {len(patcher.data):,} bytes, "
          f"{patcher.parser.header.num_names} names, "
          f"{patcher.parser.num_assets} assets")
    patcher.clone_asset(args.source, args.target)
    print(f"Cloned {args.source!r} -> {args.target!r}")
    print(f"New NameMap size: {len(patcher.parser.name_map.names)}")
    print(f"New asset record: {len(patcher._appended_asset_bytes)} bytes")
    d = patcher._diag
    print(f"Tag-pair patch: src=(begin={d['src_pair_begin']}, num={d['src_num']}), "
          f"new=(begin={d['new_pair_begin']}, num={len(d['new_pairs'])}), "
          f"new_NumberlessName_idx={d['new_numberless_name_idx']}, "
          f"new_tag_handle=0x{d['new_tag_handle']:016x}")
    print(f"FStore counts: NumberlessNames={patcher.parser.fstore_info.nums[0]} "
          f"NumberlessPairs={patcher.parser.fstore_info.nums[9]}")

    out_bytes = patcher.serialize()
    Path(args.out).write_bytes(out_bytes)
    print(f"Wrote {args.out}: {len(out_bytes):,} bytes")


if __name__ == "__main__":
    main()
