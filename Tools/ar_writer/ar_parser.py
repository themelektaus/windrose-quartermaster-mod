"""
UE5.6 AssetRegistry.bin parser.

Format verified against R5 AssetRegistry.bin (Magic GUID 0x717F9EE7..., Version 21).
Reader sequences transcribed from CUE4Parse master branch.

UE5.6 specifics:
- Version 21 (ExternalActorToWorldIsEditorOnly), post-FixedTags, post-ClassPaths,
  post-WorkspaceDomain, post-PackageSavedHash, post-MarshalledTextAsUTF8String,
  post-AssetPackageDataHasExtension, pre-MemoryMappedTagDataStore.
- bFilterEditorOnlyData=1 in cooked AR ? OptionalOuterPath in FAssetData is SKIPPED.
- AlignPosInArchive is a no-op.
- FTopLevelAssetPath = 2 FNames (ClassPackage + ClassName).
- ChunkIDs is i32-count + i32[].
- FSHAHash = 20 raw bytes; FMD5Hash = i32 hasHash + (16 bytes if !=0).
- FString in AR-archive reads as FUtf8String (i32 length incl null-terminator + UTF8 bytes).
"""

import struct
import sys
from dataclasses import dataclass, field
from typing import List, Optional, Tuple


AR_MAGIC = bytes.fromhex("e79e7f713a49b0e93291b3880781381b")
FSTORE_MAGIC_TEXT_FIRST = 0x12345679
FSTORE_MAGIC_MEMBER_FIRST = 0x12345678
FSTORE_END_MAGIC = 0x87654321

# UE5.6 = Version 21
VERSION_UE56 = 21
ASSET_REGISTRY_NUMBERED_NAME_BIT = 0x80000000


# ---------------- Data classes ----------------


@dataclass
class ARHeader:
    magic: bytes
    version: int
    b_filter_editor_only: int
    num_names: int
    num_string_bytes: int
    hash_version: int

    HEADER_SIZE = 40  # 16 + 4 + 4 + 4 + 4 + 8 (CUE4Parse: version+bool, then LoadNameBatch reads num,strBytes,hashVer)


@dataclass
class NameMap:
    hashes: List[int] = field(default_factory=list)         # u64 each
    headers: List[int] = field(default_factory=list)        # raw u16 BE values
    names: List[str] = field(default_factory=list)
    name_to_index: dict = field(default_factory=dict)
    hashes_start: int = 0
    headers_start: int = 0
    strings_start: int = 0
    strings_end: int = 0


@dataclass
class FStoreInfo:
    """FStore byte bounds + per-subsection byte ranges. Needed for re-emitting
    the section with extended NumberlessNames + NumberlessPairs (B2.7)."""
    start: int = 0
    end: int = 0
    magic: int = 0
    nums: List[int] = field(default_factory=list)  # 11 i32 sizes
    # Byte ranges per subsection (text-first order, after nums+pad)
    pad_after_nums: int = 0           # 4-byte pad start
    texts_start: int = 0
    texts_end: int = 0
    numberless_names_start: int = 0    # nums[0] * u32
    numberless_names_end: int = 0
    names_start: int = 0
    names_end: int = 0
    numberless_export_paths_start: int = 0
    numberless_export_paths_end: int = 0
    export_paths_start: int = 0
    export_paths_end: int = 0
    ansi_string_offsets_start: int = 0
    ansi_string_offsets_end: int = 0
    wide_string_offsets_start: int = 0
    wide_string_offsets_end: int = 0
    ansi_strings_start: int = 0
    ansi_strings_end: int = 0
    wide_strings_start: int = 0
    wide_strings_end: int = 0
    numberless_pairs_start: int = 0    # nums[9] * 8B
    numberless_pairs_end: int = 0
    pairs_start: int = 0
    pairs_end: int = 0
    end_magic_start: int = 0
    # Convenience: parsed NumberlessPairs as (u32 key, u32 value_id) tuples
    numberless_pairs: List[Tuple[int, int]] = field(default_factory=list)
    # Convenience: parsed NumberlessNames as u32 FName indices
    numberless_names: List[int] = field(default_factory=list)


@dataclass
class FName:
    index: int
    number: int = 0

    def __repr__(self):
        return f"FName(idx={self.index}, num={self.number})"


@dataclass
class FAssetData:
    """Captures the raw byte slice of an asset data record AND its parsed fields,
    so we can byte-clone + patch FName indices."""
    raw_start: int
    raw_end: int
    package_path: FName
    asset_class_pkg: FName       # FTopLevelAssetPath part 1
    asset_class_name: FName      # FTopLevelAssetPath part 2
    package_name: FName
    asset_name: FName
    tag_handle_u64: int
    bundle_data_raw: bytes       # FAssetBundleData serialized bytes
    chunk_ids: List[int]
    package_flags: int


@dataclass
class FAssetIdentifier:
    field_bits: int
    package_name: Optional[FName] = None
    primary_asset_type: Optional[FName] = None
    object_name: Optional[FName] = None
    value_name: Optional[FName] = None


@dataclass
class FDependsNode:
    raw_start: int
    raw_end: int
    identifier: FAssetIdentifier
    package_dependencies: List[int]
    package_flags_words: List[int]   # raw i32 words for the bit-array (no count prefix)
    name_dependencies: List[int]
    manage_dependencies: List[int]
    manage_flags_words: List[int]
    referencers: List[int]


@dataclass
class FAssetPackageData:
    raw_start: int
    raw_end: int
    package_name: FName
    disk_size: int
    package_saved_hash: bytes   # 20 bytes
    cooked_hash_present: int    # i32
    cooked_hash: bytes          # 16 bytes if present, else b""
    file_version_ue4: int
    file_version_ue5: int
    file_version_licensee: int
    flags: int
    custom_versions_raw: bytes  # full serialized blob (count + entries) for byte clone
    imported_classes: List[FName]
    extension_text_raw: bytes   # full FUtf8String serialized bytes


# ---------------- Reader ----------------


class Reader:
    def __init__(self, data: bytes, pos: int = 0):
        self.data = data
        self.pos = pos

    def u8(self) -> int:
        v = self.data[self.pos]; self.pos += 1; return v

    def i32(self) -> int:
        v = struct.unpack_from("<i", self.data, self.pos)[0]; self.pos += 4; return v

    def u32(self) -> int:
        v = struct.unpack_from("<I", self.data, self.pos)[0]; self.pos += 4; return v

    def i64(self) -> int:
        v = struct.unpack_from("<q", self.data, self.pos)[0]; self.pos += 8; return v

    def u64(self) -> int:
        v = struct.unpack_from("<Q", self.data, self.pos)[0]; self.pos += 8; return v

    def u16_be(self) -> int:
        v = struct.unpack_from(">H", self.data, self.pos)[0]; self.pos += 2; return v

    def bytes(self, n: int) -> bytes:
        v = self.data[self.pos:self.pos + n]; self.pos += n; return v

    def fname(self) -> FName:
        idx = self.u32()
        number = 0
        if idx & ASSET_REGISTRY_NUMBERED_NAME_BIT:
            idx &= ~ASSET_REGISTRY_NUMBERED_NAME_BIT
            number = self.u32()
        return FName(index=idx, number=number)

    def futf8_string(self) -> bytes:
        """ReadFUtf8String: i32 length (incl null terminator) + that many UTF-8 bytes.
        Returns raw serialized bytes (length-prefix + bytes) for round-trip."""
        start = self.pos
        length = self.i32()
        if length == 0:
            return self.data[start:self.pos]
        # length includes null terminator (1 byte)
        self.pos += length
        return self.data[start:self.pos]


# ---------------- Full parser ----------------


class ARParser:
    def __init__(self, data: bytes):
        self.data = data
        self.r = Reader(data)
        self.header: Optional[ARHeader] = None
        self.name_map: Optional[NameMap] = None
        self.fstore_info: Optional[FStoreInfo] = None
        self.num_assets: int = 0
        self.assets: List[FAssetData] = []
        self.assets_start: int = 0
        self.assets_end: int = 0
        self.dep_section_size: int = 0
        self.dep_section_start: int = 0
        self.dep_section_end: int = 0
        self.num_depends_nodes: int = 0
        self.depends_nodes: List[FDependsNode] = []
        self.num_package_data: int = 0
        self.package_data_start: int = 0
        self.package_data_end: int = 0
        self.package_data: List[FAssetPackageData] = []

    # ----- header -----
    def parse_header(self) -> ARHeader:
        self.r.pos = 0
        magic = self.r.bytes(16)
        if magic != AR_MAGIC:
            raise ValueError(f"Bad magic: {magic.hex()}")
        version = self.r.i32()
        # CUE4Parse reads ReadBoolean (1 byte), but actual R5 AR.bin serializes
        # bFilterEditorOnlyData as i32 (UE's standard bool wire format).
        # Verified empirically: offset 0x14 holds 0x00000001 (4 bytes), and
        # NumNames=92406=0x000168F6 lives at 0x18.
        b_filter = self.r.i32()
        # Then LoadNameBatch reads num,strBytes,hashVer
        num_names = self.r.i32()
        num_string_bytes = self.r.u32()
        hash_version = self.r.u64()
        self.header = ARHeader(
            magic=magic, version=version, b_filter_editor_only=b_filter,
            num_names=num_names, num_string_bytes=num_string_bytes,
            hash_version=hash_version,
        )
        return self.header

    # ----- name map -----
    def parse_name_map(self) -> NameMap:
        assert self.header is not None
        nm = NameMap()
        n = self.header.num_names
        nm.hashes_start = self.r.pos
        for _ in range(n):
            nm.hashes.append(self.r.u64())
        nm.headers_start = self.r.pos
        for _ in range(n):
            nm.headers.append(self.r.u16_be())
        nm.strings_start = self.r.pos
        for i in range(n):
            h = nm.headers[i]
            is_wide = bool(h & 0x8000)
            length = h & 0x7FFF
            if is_wide:
                byte_len = length * 2
                s = self.r.bytes(byte_len).decode("utf-16-le")
            else:
                byte_len = length
                s = self.r.bytes(byte_len).decode("utf-8")
            nm.names.append(s)
            if s not in nm.name_to_index:
                nm.name_to_index[s] = i
        nm.strings_end = self.r.pos
        assert nm.strings_end - nm.strings_start == self.header.num_string_bytes
        self.name_map = nm
        return nm

    # ----- FStore -----
    def parse_fstore(self) -> FStoreInfo:
        """Parses FStore structurally to determine its byte size. We do NOT keep
        the data - we just need the end-offset so we can locate AssetData."""
        info = FStoreInfo()
        info.start = self.r.pos
        magic = self.r.u32()
        info.magic = magic
        if magic not in (FSTORE_MAGIC_TEXT_FIRST, FSTORE_MAGIC_MEMBER_FIRST):
            raise ValueError(f"Bad FStore magic at 0x{info.start:x}: 0x{magic:08x}")
        text_first = (magic == FSTORE_MAGIC_TEXT_FIRST)
        nums = [self.r.i32() for _ in range(11)]
        info.nums = nums
        # nums layout: [0]NumberlessNames, [1]Names, [2]NumberlessExportPaths,
        # [3]ExportPaths, [4]Texts, [5]AnsiStringOffsets, [6]WideStringOffsets,
        # [7]AnsiStringBytes, [8]WideStringBytes(in_chars), [9]NumberlessPairs,
        # [10]Pairs.

        if text_first:
            info.pad_after_nums = self.r.pos
            self.r.pos += 4  # pad

            # Texts: nums[4] x FUtf8String
            info.texts_start = self.r.pos
            for _ in range(nums[4]):
                self.r.futf8_string()
            info.texts_end = self.r.pos

            # NumberlessNames: nums[0] x u32 (FName indices)
            info.numberless_names_start = self.r.pos
            for _ in range(nums[0]):
                info.numberless_names.append(self.r.u32())
            info.numberless_names_end = self.r.pos

            # Names: nums[1] x FName (variable: 4 or 8 bytes each)
            info.names_start = self.r.pos
            for _ in range(nums[1]):
                self.r.fname()
            info.names_end = self.r.pos

            # NumberlessExportPaths: nums[2] x 16 bytes (UE5.6 ClassPaths)
            info.numberless_export_paths_start = self.r.pos
            self.r.pos += nums[2] * 16
            info.numberless_export_paths_end = self.r.pos

            # ExportPaths: nums[3] x 4 FNames each
            info.export_paths_start = self.r.pos
            for _ in range(nums[3]):
                self.r.fname()  # ClassPackage
                self.r.fname()  # ClassName (TopLevelAssetPath part 2)
                self.r.fname()  # Object
                self.r.fname()  # Package
            info.export_paths_end = self.r.pos

            # AnsiStringOffsets: nums[5] x u32
            info.ansi_string_offsets_start = self.r.pos
            self.r.pos += nums[5] * 4
            info.ansi_string_offsets_end = self.r.pos

            # WideStringOffsets: nums[6] x u32
            info.wide_string_offsets_start = self.r.pos
            self.r.pos += nums[6] * 4
            info.wide_string_offsets_end = self.r.pos

            # AnsiStrings: nums[7] bytes
            info.ansi_strings_start = self.r.pos
            self.r.pos += nums[7]
            info.ansi_strings_end = self.r.pos

            # WideStrings: nums[8]*2 bytes
            info.wide_strings_start = self.r.pos
            self.r.pos += nums[8] * 2
            info.wide_strings_end = self.r.pos

            # NumberlessPairs: nums[9] x 8 bytes (u32 Key + u32 ValueId)
            info.numberless_pairs_start = self.r.pos
            for _ in range(nums[9]):
                k = self.r.u32()
                v = self.r.u32()
                info.numberless_pairs.append((k, v))
            info.numberless_pairs_end = self.r.pos

            # Pairs: nums[10] x (FName + u32)
            info.pairs_start = self.r.pos
            for _ in range(nums[10]):
                self.r.fname()
                self.r.u32()
            info.pairs_end = self.r.pos
            info.end_magic_start = self.r.pos
        else:
            raise NotImplementedError("Member-first FStore order not implemented")

        end_magic = self.r.u32()
        if end_magic != FSTORE_END_MAGIC:
            raise ValueError(f"Bad FStore end magic at 0x{self.r.pos - 4:x}: 0x{end_magic:08x}")
        info.end = self.r.pos
        self.fstore_info = info
        return info

    # ----- FAssetData -----
    def parse_asset_data(self) -> FAssetData:
        start = self.r.pos
        package_path = self.r.fname()
        asset_class_pkg = self.r.fname()   # FTopLevelAssetPath part 1
        asset_class_name = self.r.fname()  # part 2
        package_name = self.r.fname()
        asset_name = self.r.fname()
        # OptionalOuterPath skipped when bFilterEditorOnlyData=true (cooked)
        if not self.header.b_filter_editor_only:
            self.r.fname()  # discard
        # SerializeTagsAndBundles: u64 handle + FAssetBundleData
        tag_handle = self.r.u64()
        # FAssetBundleData: i32 numBundles + per-bundle (FName + i32 numAssets +
        # per-asset (2 FNames + FString))
        bundle_start = self.r.pos
        num_bundles = self.r.i32()
        for _ in range(num_bundles):
            self.r.fname()  # BundleName
            num_bundle_assets = self.r.i32()
            for _ in range(num_bundle_assets):
                self.r.fname()  # TopLevelAssetPath part 1
                self.r.fname()  # part 2
                self.r.futf8_string()  # SubPathString
        bundle_end = self.r.pos
        bundle_raw = self.data[bundle_start:bundle_end]

        # ChunkIDs: i32 count + i32[]
        num_chunks = self.r.i32()
        chunk_ids = [self.r.i32() for _ in range(num_chunks)]
        # PackageFlags u32
        package_flags = self.r.u32()
        end = self.r.pos
        return FAssetData(
            raw_start=start, raw_end=end,
            package_path=package_path,
            asset_class_pkg=asset_class_pkg,
            asset_class_name=asset_class_name,
            package_name=package_name,
            asset_name=asset_name,
            tag_handle_u64=tag_handle,
            bundle_data_raw=bundle_raw,
            chunk_ids=chunk_ids,
            package_flags=package_flags,
        )

    def parse_asset_data_section(self):
        self.assets_start = self.r.pos
        self.num_assets = self.r.i32()
        for _ in range(self.num_assets):
            self.assets.append(self.parse_asset_data())
        self.assets_end = self.r.pos

    # ----- FDependsNode -----
    def parse_depends_section(self):
        self.dep_section_size = self.r.i64()
        self.dep_section_start = self.r.pos
        self.dep_section_end = self.dep_section_start + self.dep_section_size
        self.num_depends_nodes = self.r.i32()
        for _ in range(self.num_depends_nodes):
            self.depends_nodes.append(self.parse_depends_node())
        # Force-align to declared end (defensive)
        if self.r.pos != self.dep_section_end:
            # CUE4Parse seeks to end after errors. We do the same.
            self.r.pos = self.dep_section_end

    def parse_depends_node(self) -> FDependsNode:
        start = self.r.pos
        flag_bits = self.r.u8()
        pkg_name = self.r.fname() if (flag_bits & 1) else None
        pri_type = self.r.fname() if (flag_bits & 2) else None
        obj_name = self.r.fname() if (flag_bits & 4) else None
        val_name = self.r.fname() if (flag_bits & 8) else None
        identifier = FAssetIdentifier(field_bits=flag_bits, package_name=pkg_name,
                                       primary_asset_type=pri_type, object_name=obj_name,
                                       value_name=val_name)
        pkg_dep_count = self.r.i32()
        pkg_deps = [self.r.i32() for _ in range(pkg_dep_count)]
        pkg_flag_words_n = (5 * pkg_dep_count + 31) // 32
        pkg_flag_words = [self.r.i32() for _ in range(pkg_flag_words_n)]
        name_dep_count = self.r.i32()
        name_deps = [self.r.i32() for _ in range(name_dep_count)]
        mgr_dep_count = self.r.i32()
        mgr_deps = [self.r.i32() for _ in range(mgr_dep_count)]
        mgr_flag_words_n = (1 * mgr_dep_count + 31) // 32  # UE5.6 width=1
        mgr_flag_words = [self.r.i32() for _ in range(mgr_flag_words_n)]
        ref_count = self.r.i32()
        refs = [self.r.i32() for _ in range(ref_count)]
        end = self.r.pos
        return FDependsNode(
            raw_start=start, raw_end=end,
            identifier=identifier,
            package_dependencies=pkg_deps,
            package_flags_words=pkg_flag_words,
            name_dependencies=name_deps,
            manage_dependencies=mgr_deps,
            manage_flags_words=mgr_flag_words,
            referencers=refs,
        )

    # ----- FAssetPackageData -----
    def parse_package_data_section(self):
        self.package_data_start = self.r.pos
        self.num_package_data = self.r.i32()
        for _ in range(self.num_package_data):
            self.package_data.append(self.parse_package_data())
        self.package_data_end = self.r.pos

    def parse_package_data(self) -> FAssetPackageData:
        start = self.r.pos
        package_name = self.r.fname()
        disk_size = self.r.i64()
        package_saved_hash = self.r.bytes(20)  # FSHAHash
        # FMD5Hash
        cooked_present = self.r.i32()
        cooked_hash = self.r.bytes(16) if cooked_present != 0 else b""
        # ChunkHashes: i32 count + count*32 + 4 pad
        chunk_count = self.r.i32()
        self.r.pos += chunk_count * 32 + 4
        # FileVersionUE (2 x i32)
        fv_ue4 = self.r.i32()
        fv_ue5 = self.r.i32()
        fv_lic = self.r.i32()
        flags = self.r.u32()
        # FCustomVersionContainer: i32 count + count*(FGuid 16B + i32)
        cv_start = self.r.pos
        cv_count = self.r.i32()
        self.r.pos += cv_count * 20
        custom_versions_raw = self.data[cv_start:self.r.pos]
        # ImportedClasses: i32 count + count x FName
        imp_count = self.r.i32()
        imported = [self.r.fname() for _ in range(imp_count)]
        # ExtensionText: FUtf8String
        ext_raw = self.r.futf8_string()
        end = self.r.pos
        return FAssetPackageData(
            raw_start=start, raw_end=end,
            package_name=package_name, disk_size=disk_size,
            package_saved_hash=package_saved_hash,
            cooked_hash_present=cooked_present, cooked_hash=cooked_hash,
            file_version_ue4=fv_ue4, file_version_ue5=fv_ue5,
            file_version_licensee=fv_lic, flags=flags,
            custom_versions_raw=custom_versions_raw,
            imported_classes=imported,
            extension_text_raw=ext_raw,
        )

    # ----- orchestrator -----
    def parse_all(self):
        self.parse_header()
        self.parse_name_map()
        self.parse_fstore()
        self.parse_asset_data_section()
        self.parse_depends_section()
        self.parse_package_data_section()


def main():
    if len(sys.argv) < 2:
        print("Usage: ar_parser.py <AssetRegistry.bin>", file=sys.stderr)
        sys.exit(1)
    with open(sys.argv[1], "rb") as f:
        data = f.read()
    p = ARParser(data)
    p.parse_all()
    h = p.header
    nm = p.name_map
    print(f"Header: version={h.version} bFilterEditorOnly={h.b_filter_editor_only} "
          f"num_names={h.num_names} num_string_bytes=0x{h.num_string_bytes:x}")
    print(f"NameMap: 0x28..0x{nm.strings_end:x}")
    fs = p.fstore_info
    print(f"FStore: 0x{fs.start:x}..0x{fs.end:x} ({fs.end - fs.start:,}B) "
          f"magic=0x{fs.magic:08x} nums={fs.nums}")
    print(f"AssetData: 0x{p.assets_start:x}..0x{p.assets_end:x} ({p.assets_end - p.assets_start:,}B) "
          f"NumAssets={p.num_assets}")
    print(f"DependsSection: 0x{p.dep_section_start:x}..0x{p.dep_section_end:x} "
          f"({p.dep_section_size:,}B) NumNodes={p.num_depends_nodes}")
    print(f"PackageData: 0x{p.package_data_start:x}..0x{p.package_data_end:x} "
          f"({p.package_data_end - p.package_data_start:,}B) NumEntries={p.num_package_data}")
    print(f"Total file end: 0x{p.r.pos:x} (file size 0x{len(data):x})")
    print(f"Trailing bytes: {len(data) - p.r.pos}")

    # Locate Bedroll
    bedroll_path_idx = nm.name_to_index.get("/Game/Gameplay/Building/BuildingDecoration/DA_BI_Bedroll_01")
    bedroll_asset_idx = nm.name_to_index.get("DA_BI_Bedroll_01")
    print(f"\nBedroll FName indices: path={bedroll_path_idx} asset={bedroll_asset_idx}")

    # Find AssetData record for bedroll
    bedroll_assets = [(i, a) for i, a in enumerate(p.assets)
                      if a.asset_name.index == bedroll_asset_idx]
    print(f"AssetData records for DA_BI_Bedroll_01: {len(bedroll_assets)}")
    for i, a in bedroll_assets[:3]:
        print(f"  [#{i}] pkg_path={a.package_path.index}({nm.names[a.package_path.index]!r}) "
              f"class={a.asset_class_pkg.index},{a.asset_class_name.index}"
              f"({nm.names[a.asset_class_pkg.index]!r}.{nm.names[a.asset_class_name.index]!r}) "
              f"pkg_name={a.package_name.index}({nm.names[a.package_name.index]!r}) "
              f"raw_len={a.raw_end - a.raw_start}B")

    # Find DependsNode for bedroll (PackageName FName index matches)
    bedroll_dn = [(i, dn) for i, dn in enumerate(p.depends_nodes)
                  if dn.identifier.package_name and dn.identifier.package_name.index == bedroll_path_idx]
    print(f"DependsNode records for /Game/.../DA_BI_Bedroll_01 package: {len(bedroll_dn)}")
    for i, dn in bedroll_dn[:3]:
        print(f"  [#{i}] flag_bits={dn.identifier.field_bits} "
              f"pkg_deps={len(dn.package_dependencies)} name_deps={len(dn.name_dependencies)} "
              f"mgr_deps={len(dn.manage_dependencies)} refs={len(dn.referencers)} "
              f"raw_len={dn.raw_end - dn.raw_start}B")

    # Find PackageData entry
    bedroll_pd = [(i, pd) for i, pd in enumerate(p.package_data)
                  if pd.package_name.index == bedroll_path_idx]
    print(f"PackageData records for /Game/.../DA_BI_Bedroll_01: {len(bedroll_pd)}")
    for i, pd in bedroll_pd[:3]:
        print(f"  [#{i}] disk_size={pd.disk_size} "
              f"saved_hash={pd.package_saved_hash.hex()[:32]}... "
              f"cooked_present={pd.cooked_hash_present} "
              f"imp_classes={len(pd.imported_classes)} raw_len={pd.raw_end - pd.raw_start}B")


if __name__ == "__main__":
    main()
