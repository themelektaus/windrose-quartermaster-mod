"""Decode tag_handle_u64 for vanilla DA_BI_Bedroll_01 and list its tag pairs."""
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from ar_parser import ARParser, FSTORE_MAGIC_TEXT_FIRST, FSTORE_END_MAGIC


def parse_fstore_with_data(p: ARParser):
    """Re-parse FStore section but capture the pair lists this time."""
    data = p.data
    pos = p.fstore_info.start

    def u32(): nonlocal pos; v = struct.unpack_from("<I", data, pos)[0]; pos += 4; return v
    def i32(): nonlocal pos; v = struct.unpack_from("<i", data, pos)[0]; pos += 4; return v

    magic = u32()
    assert magic == FSTORE_MAGIC_TEXT_FIRST
    nums = [i32() for _ in range(11)]
    # nums layout: [0]NumberlessNames, [1]Names, [2]NumberlessExportPaths,
    # [3]ExportPaths, [4]Texts, [5]AnsiStringOffsets, [6]WideStringOffsets,
    # [7]AnsiStringBytes, [8]WideStringBytes_chars, [9]NumberlessPairs, [10]Pairs.
    pos += 4  # text-first padding

    # Texts: nums[4] x FUtf8String
    texts = []
    for _ in range(nums[4]):
        length = i32()
        if length == 0:
            texts.append("")
        else:
            s = data[pos:pos + length - 1].decode("utf-8", errors="replace")
            pos += length
            texts.append(s)

    # NumberlessNames: nums[0] x u32 = display index into NameMap (no number)
    numberless_names = [u32() for _ in range(nums[0])]
    # Names: nums[1] x FName (variable)
    names_with_number = []
    for _ in range(nums[1]):
        idx = u32()
        number = 0
        if idx & 0x80000000:
            idx &= 0x7FFFFFFF
            number = u32()
        names_with_number.append((idx, number))

    # NumberlessExportPaths: 16 bytes each = (ClassPackage, ClassObject, Object, Package) display indices
    numberless_export_paths = []
    for _ in range(nums[2]):
        cls_pkg = u32(); cls_obj = u32(); obj = u32(); pkg = u32()
        numberless_export_paths.append((cls_pkg, cls_obj, obj, pkg))
    # ExportPaths: nums[3] x 4 FName  (FName variable)
    export_paths = []
    for _ in range(nums[3]):
        eps = []
        for _ in range(4):
            idx = u32(); number = 0
            if idx & 0x80000000:
                idx &= 0x7FFFFFFF; number = u32()
            eps.append((idx, number))
        export_paths.append(tuple(eps))

    # Ansi offsets / Wide offsets / strings
    ansi_offsets = [u32() for _ in range(nums[5])]
    wide_offsets = [u32() for _ in range(nums[6])]
    ansi_bytes_start = pos; pos += nums[7]
    wide_bytes_start = pos; pos += nums[8] * 2

    def ansi_string_at(offset):
        # null-terminated C string
        end = data.index(b"\x00", ansi_bytes_start + offset)
        return data[ansi_bytes_start + offset:end].decode("utf-8", errors="replace")

    def wide_string_at(offset_in_chars):
        # null-terminated UTF-16LE
        start = wide_bytes_start + offset_in_chars * 2
        cur = start
        while True:
            ch = struct.unpack_from("<H", data, cur)[0]
            if ch == 0:
                break
            cur += 2
        return data[start:cur].decode("utf-16-le", errors="replace")

    # NumberlessPairs: u32 Key + u32 ValueId
    numberless_pairs = []
    for _ in range(nums[9]):
        key = u32(); val_id = u32()
        numberless_pairs.append((key, val_id))
    # Pairs: FName (variable) + u32 ValueId
    pairs = []
    for _ in range(nums[10]):
        idx = u32(); number = 0
        if idx & 0x80000000:
            idx &= 0x7FFFFFFF; number = u32()
        val_id = u32()
        pairs.append(((idx, number), val_id))

    end_magic = u32()
    assert end_magic == FSTORE_END_MAGIC

    return {
        "nums": nums,
        "texts": texts,
        "numberless_names": numberless_names,
        "names": names_with_number,
        "numberless_export_paths": numberless_export_paths,
        "export_paths": export_paths,
        "ansi_offsets": ansi_offsets,
        "wide_offsets": wide_offsets,
        "ansi_string_at": ansi_string_at,
        "wide_string_at": wide_string_at,
        "numberless_pairs": numberless_pairs,
        "pairs": pairs,
    }


def decode_value_id(val_id: int, fs, nm_names):
    """ValueId is type-tagged. Top bits = type, rest = index into a sub-pool.
    Type encoding (FValueId): high bit set = LooseObject; otherwise type field.
    For numberless: ValueId is bit-packed: high 3 bits = type, low 29 bits = index.

    Types (from CUE4Parse FValueId.cs):
      0 = AnsiString  -> ansi_offsets[idx]
      1 = WideString  -> wide_offsets[idx]
      2 = NumberlessName -> numberless_names[idx]
      3 = Name -> names[idx]
      4 = NumberlessExportPath -> numberless_export_paths[idx]
      5 = ExportPath -> export_paths[idx]
      6 = LocalizedText -> texts[idx]
    """
    # CUE4Parse FValueId: low 3 bits = type, high 29 bits = index
    type_id = val_id & 0x7
    idx = val_id >> 3
    if type_id == 0:
        return f"ansi[{idx}]={fs['ansi_string_at'](fs['ansi_offsets'][idx])!r}"
    elif type_id == 1:
        return f"wide[{idx}]={fs['wide_string_at'](fs['wide_offsets'][idx])!r}"
    elif type_id == 2:
        name_idx = fs['numberless_names'][idx]
        return f"numberlessName[{idx}]->NameMap[{name_idx}]={nm_names[name_idx]!r}"
    elif type_id == 3:
        ni, num = fs['names'][idx]
        return f"name[{idx}]->NameMap[{ni}]={nm_names[ni]!r} (num={num})"
    elif type_id == 4:
        ep = fs['numberless_export_paths'][idx]
        return f"numberlessExportPath[{idx}]={nm_names[ep[0]]!r}.{nm_names[ep[1]]!r}/{nm_names[ep[2]]!r}.{nm_names[ep[3]]!r}"
    elif type_id == 5:
        ep = fs['export_paths'][idx]
        return f"exportPath[{idx}]={nm_names[ep[0][0]]!r}.{nm_names[ep[1][0]]!r}/{nm_names[ep[2][0]]!r}.{nm_names[ep[3][0]]!r}"
    elif type_id == 6:
        return f"text[{idx}]={fs['texts'][idx]!r}"
    return f"<unknown type={type_id} idx={idx}>"


def main():
    ar_path = Path(sys.argv[1])
    p = ARParser(ar_path.read_bytes())
    p.parse_all()

    # Find Bedroll asset
    bedroll_path_idx = p.name_map.name_to_index["/Game/Gameplay/Building/BuildingDecoration/DA_BI_Bedroll_01"]
    bedroll_asset = next(a for a in p.assets if a.package_name.index == bedroll_path_idx)
    print(f"Bedroll asset: package_name={bedroll_asset.package_name.index} asset_name={bedroll_asset.asset_name.index}")
    print(f"  asset_class_pkg={bedroll_asset.asset_class_pkg.index}({p.name_map.names[bedroll_asset.asset_class_pkg.index]!r})")
    print(f"  asset_class_name={bedroll_asset.asset_class_name.index}({p.name_map.names[bedroll_asset.asset_class_name.index]!r})")
    print(f"  tag_handle_u64=0x{bedroll_asset.tag_handle_u64:016x}")

    # Decode tag_handle: CUE4Parse FPartialMapHandle encoding
    # Layout: u64 = (Num << 32) | (bHasNumberlessKeys << 31) | (PairBegin & 0x7FFFFFFF) ??
    # Actually CUE4Parse:
    #   u64 handle: low 32 bits = Num, high 32 bits = PairBegin
    # OR per FixedTagPrivate.cpp:
    #   uint32_t Num | (uint64_t)(PairBegin) << 32
    h = bedroll_asset.tag_handle_u64
    # UE5 FPartialMapHandle:
    #   bool bHasNumberlessKeys; uint16 Num; uint32 PairBegin;
    # Packed: bit 63=bHasNumberlessKeys; bits 32-47=Num; bits 0-31=PairBegin
    bHasNumberlessKeys_v1 = (h >> 63) & 1
    num_v1 = (h >> 32) & 0xFFFF
    pair_begin_v1 = h & 0xFFFFFFFF
    print(f"  Interpretation A: PairBegin={pair_begin_v1} Num={num_v1} bHasNumberless={bHasNumberlessKeys_v1}")

    # Alternative: bit 31 of low word = bHasNumberlessKeys, low 31=PairBegin, high32=Num
    bHasNumberlessKeys_v2 = (h >> 31) & 1
    pair_begin_v2 = h & 0x7FFFFFFF
    num_v2 = (h >> 32) & 0xFFFFFFFF
    print(f"  Interpretation B: PairBegin={pair_begin_v2} Num={num_v2} bHasNumberless={bHasNumberlessKeys_v2}")

    # Decode FStore
    print("Decoding FStore...")
    fs = parse_fstore_with_data(p)
    print(f"FStore: nums={fs['nums']}")
    print(f"  texts: {len(fs['texts'])}")
    print(f"  numberless_pairs: {len(fs['numberless_pairs'])}")
    print(f"  pairs: {len(fs['pairs'])}")
    print()

    # Try interpretation A first (most common in CUE4Parse)
    print("=== Trying Interpretation A ===")
    pb, num, has_numberless = pair_begin_v1, num_v1, bHasNumberlessKeys_v1
    pool = fs['numberless_pairs'] if has_numberless else fs['pairs']
    if pb + num <= len(pool):
        print(f"Tag pairs (pool={'numberless' if has_numberless else 'pairs'}, range [{pb}, {pb+num})):")
        for i in range(pb, pb + num):
            entry = pool[i]
            if has_numberless:
                key_idx, val_id = entry
                key_name = p.name_map.names[key_idx]
            else:
                (key_idx, key_num), val_id = entry
                key_name = p.name_map.names[key_idx] + (f"_{key_num}" if key_num else "")
            print(f"  [{i}] {key_name} key_idx={key_idx} raw_val_id=0x{val_id:08x}")
            try:
                val_decoded = decode_value_id(val_id, fs, p.name_map.names)
                print(f"       -> {val_decoded}")
            except Exception as e:
                print(f"       -> decode_error: {e}")
    else:
        print(f"  Out of bounds (pool size {len(pool)}), trying interpretation B")
        print("=== Trying Interpretation B ===")
        pb, num, has_numberless = pair_begin_v2, num_v2, bHasNumberlessKeys_v2
        pool = fs['numberless_pairs'] if has_numberless else fs['pairs']
        if pb + num <= len(pool):
            print(f"Tag pairs (pool={'numberless' if has_numberless else 'pairs'}, range [{pb}, {pb+num})):")
            for i in range(pb, pb + num):
                entry = pool[i]
                if has_numberless:
                    key_idx, val_id = entry
                    key_name = p.name_map.names[key_idx]
                else:
                    (key_idx, key_num), val_id = entry
                    key_name = p.name_map.names[key_idx] + (f"_{key_num}" if key_num else "")
                val_decoded = decode_value_id(val_id, fs, p.name_map.names)
                print(f"  [{i}] {key_name} = {val_decoded}")
        else:
            print(f"  Also out of bounds. Need different encoding.")


if __name__ == "__main__":
    main()
