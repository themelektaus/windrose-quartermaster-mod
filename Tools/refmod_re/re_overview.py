import pefile, sys, re

DLL = r"E:\Windrose\Mods\Quartermaster\References\Windrose Mod Settings\R5ModSettings\dlls\main.dll"
pe = pefile.PE(DLL, fast_load=False)
base = pe.OPTIONAL_HEADER.ImageBase
print("ImageBase=0x%X  EntryPoint=0x%X  Machine=0x%X" % (base, pe.OPTIONAL_HEADER.AddressOfEntryPoint, pe.FILE_HEADER.Machine))
print("\n== SECTIONS ==")
for s in pe.sections:
    print("  %-8s VA=0x%08X VSize=0x%06X RawSize=0x%06X chars=0x%08X" % (
        s.Name.rstrip(b"\0").decode(errors="replace"), s.VirtualAddress, s.Misc_VirtualSize, s.SizeOfRawData, s.Characteristics))

print("\n== IMPORTS ==")
if hasattr(pe, "DIRECTORY_ENTRY_IMPORT"):
    for imp in pe.DIRECTORY_ENTRY_IMPORT:
        dll = imp.dll.decode(errors="replace")
        print("  [%s]  (%d funcs)" % (dll, len(imp.imports)))
        for f in imp.imports:
            nm = f.name.decode(errors="replace") if f.name else ("ord#%s" % f.ordinal)
            print("      %s" % nm)
else:
    print("  (none / static)")

print("\n== DELAY IMPORTS ==")
if hasattr(pe, "DIRECTORY_ENTRY_DELAY_IMPORT"):
    for imp in pe.DIRECTORY_ENTRY_DELAY_IMPORT:
        dll = imp.dll.decode(errors="replace")
        print("  [%s]" % dll)
        for f in imp.imports:
            nm = f.name.decode(errors="replace") if f.name else ("ord#%s" % f.ordinal)
            print("      %s" % nm)
else:
    print("  (none)")

print("\n== EXPORTS ==")
if hasattr(pe, "DIRECTORY_ENTRY_EXPORT"):
    for e in pe.DIRECTORY_ENTRY_EXPORT.symbols:
        print("  %s @0x%X" % (e.name.decode(errors="replace") if e.name else "?", base + e.address))
else:
    print("  (none)")
