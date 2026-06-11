import pefile, re

DLL = r"E:\Windrose\Mods\Quartermaster\References\Windrose Mod Settings\R5ModSettings\dlls\main.dll"
pe = pefile.PE(DLL, fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase

# Map file data per section for VA computation
secs = []
for s in pe.sections:
    secs.append((s.VirtualAddress, s.Misc_VirtualSize, s.PointerToRawData, s.SizeOfRawData, s.get_data()))

data = pe.get_memory_mapped_image()  # indexed by RVA
size = len(data)

def va(rva): return base + rva

ascii_re = re.compile(rb"[\x20-\x7e]{4,}")
strings = {}  # rva -> ('A'|'W', text)

# ASCII
for m in ascii_re.finditer(data):
    strings[m.start()] = ('A', m.group().decode('latin1'))

# UTF-16LE: sequences of (printable, 0x00)
i = 0
while i + 1 < size:
    if 0x20 <= data[i] <= 0x7e and data[i+1] == 0:
        j = i
        chars = []
        while j + 1 < size and 0x20 <= data[j] <= 0x7e and data[j+1] == 0:
            chars.append(chr(data[j])); j += 2
        if len(chars) >= 4:
            strings[i] = ('W', ''.join(chars))
        i = j + 2
    else:
        i += 1

items = sorted(strings.items())
out_lines = []
for rva,(k,t) in items:
    out_lines.append("0x%08X %s %s" % (va(rva), k, t))

with open(r"E:\Windrose\Mods\Quartermaster\Tools\refmod_re\strings_all.txt","w",encoding="utf-8") as f:
    f.write("\n".join(out_lines))

print("total strings:", len(items))
KW = ["CookTab","Tab","Setting","Widget","SetData","AddChild","Visib","Construct","WBP_","Vertical","Scroll","Panel","Mods","Hook","Regist","Content","Slot","SetMainDescription","EntrySwitcher","EntryScalar","KeyBinding","Switcher","Screen","OnClick","Button","SizeBox","Realize","Collapse","Caption","FName","Class","Create","Initialize"]
print("\n== interesting strings ==")
seen=0
for rva,(k,t) in items:
    if any(w.lower() in t.lower() for w in KW):
        print("0x%08X %s %s" % (va(rva), k, t))
        seen+=1
print("\ninteresting:", seen)
