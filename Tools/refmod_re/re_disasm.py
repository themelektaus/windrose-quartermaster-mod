import pefile, struct, sys
from capstone import Cs, CS_ARCH_X86, CS_MODE_64, CS_OP_MEM, CS_OP_IMM
from capstone.x86 import X86_REG_RIP

def rip_targets(ins):
    outs=[]
    for op in ins.operands:
        if op.type==CS_OP_MEM and op.mem.base==X86_REG_RIP:
            outs.append(ins.address + ins.size + op.mem.disp)
    return outs

DLL = r"E:\Windrose\Mods\Quartermaster\References\Windrose Mod Settings\R5ModSettings\dlls\main.dll"
pe = pefile.PE(DLL, fast_load=False)
base = pe.OPTIONAL_HEADER.ImageBase
img = pe.get_memory_mapped_image()
size = len(img)

def rva(va): return va - base
def at(va, n):
    r = rva(va); return img[r:r+n]

# ---- IAT name map: VA of IAT slot -> import name ----
iat = {}
if hasattr(pe, "DIRECTORY_ENTRY_IMPORT"):
    for imp in pe.DIRECTORY_ENTRY_IMPORT:
        for f in imp.imports:
            nm = f.name.decode(errors="replace") if f.name else ("%s#%s"%(imp.dll.decode(),f.ordinal))
            iat[f.address] = nm   # f.address is the VA of the thunk slot

# ---- .pdata function ranges ----
funcs = []  # (begin_va, end_va)
pd = None
for s in pe.sections:
    if s.Name.rstrip(b"\0") == b".pdata":
        pd = s.get_data(); pdva = base + s.VirtualAddress
for off in range(0, len(pd), 12):
    b,e,u = struct.unpack_from("<III", pd, off)
    if b==0 and e==0: continue
    funcs.append((base+b, base+e))
funcs.sort()
def func_of(va):
    lo,hi=0,len(funcs)
    while lo<hi:
        m=(lo+hi)//2
        if funcs[m][0]<=va: lo=m+1
        else: hi=m
    i=lo-1
    if i>=0 and funcs[i][0]<=va<funcs[i][1]: return funcs[i]
    if i>=0 and funcs[i][0]<=va: return funcs[i]
    return None

# ---- text range ----
for s in pe.sections:
    if s.Name.rstrip(b"\0")==b".text":
        text=s.get_data(); textva=base+s.VirtualAddress; textend=textva+s.Misc_VirtualSize

md = Cs(CS_ARCH_X86, CS_MODE_64)
md.detail = True

# Known interesting string VAs (from strings dump) we want to resolve and the IAT
STR_LABELS = {
 0x1800686E0:"'CookTabs post sc tabCount tabs'",
 0x180068690:"'CookTabs ready check failed'",
 0x1800685E0:"'settings screen sc screen tabs hbox tabCount panel extra'",
 0x180069610:"'Could not mount Mods panel'",
 0x1800696A0:"'Native Mods panel built'",
 0x180069770:"'Native Mods panel entries complete'",
 0x180068EA0:"'Could not construct Mods panel widgets stage'",
 0x180069430:"'Cannot build Mods panel'",
 0x1800687C0:"'Mods tab clicked; panel shown'",
 0x180067A00:"'..BP_Settings_SC_C:CookTabs'",0x180067A80:"'..CookTabs(R5)'",
 0x1800649F0:"'..TabsGroup_C:SetData'",
 0x1800678B0:"'..Screen_C:OnTabsStateChanged'",
 0x180067B00:"'..SC_C:OnExit'",
 0x180064948:"'Registered hook: {}'",0x180064900:"'Hook target not found yet: {}'",
 0x180069418:"'WidgetTree'",0x1800694A0:"'ScrollBox'",0x1800694B8:"'VerticalBox'",
 0x180068FB8:"'R5ModSettings_ModsPanel'",0x1800694F0:"'R5ModSettings_ModsContent'",
 0x18006A278:"'hbox_Tabs'",0x18006A1E8:"'TabsWidget'",0x18006A1B8:"'SettingsScreenWidget'",
 0x180063840:"'SetData'",0x180063B88:"'SetMainDescription'",
}

# Full string map for annotation
import re as _re
strmap={}
ar=_re.compile(rb"[\x20-\x7e]{3,}")
for m in ar.finditer(img):
    strmap[base+m.start()]=('A',m.group().decode('latin1'))
i=0
while i+1<size:
    if 0x20<=img[i]<=0x7e and img[i+1]==0:
        j=i;ch=[]
        while j+1<size and 0x20<=img[j]<=0x7e and img[j+1]==0:
            ch.append(chr(img[j]));j+=2
        if len(ch)>=3: strmap[base+i]=('W',''.join(ch))
        i=j+2
    else: i+=1

def ann_target(va):
    if va in STR_LABELS: return STR_LABELS[va]
    if va in iat: return "IMP:"+iat[va]
    if va in strmap:
        k,t=strmap[va];
        return "%s\"%s\""%(k, t[:60])
    f=func_of(va)
    if f and f[0]==va: return "sub_%X"%va
    return None

def disasm_func(fbegin, fend, title):
    print("\n===== %s  sub_%X .. %X (%d bytes) =====" % (title, fbegin, fend, fend-fbegin))
    code = at(fbegin, fend-fbegin)
    for ins in md.disasm(code, fbegin):
        line = "0x%X  %-9s %s" % (ins.address, ins.mnemonic, ins.op_str)
        note=""
        # RIP-relative target
        for op in ins.operands:
            if op.type==CS_OP_MEM and op.mem.base==0 and op.mem.index==0 and ins.modrm and (op.mem.disp or True):
                pass
        # compute rip-rel for lea/mov/call mem
        rts = rip_targets(ins)
        if rts:
            tgt = rts[0]
            lab = ann_target(tgt)
            note = "   -> 0x%X %s" % (tgt, lab or "")
        if ins.mnemonic=="call" and ins.op_str.startswith("0x"):
            tgt=int(ins.op_str,16); lab=ann_target(tgt)
            note="   -> %s"%(lab or ("sub_%X"%tgt))
        print(line+note)

# Build xref map by disassembling each .pdata function range separately
print("== XREFS to key strings/functions ==")
xrefs = {}  # target_va -> list of (instr_va, mnemonic)
for fb,fe in funcs:
    if not (textva <= fb < textend): continue
    for ins in md.disasm(at(fb, fe-fb), fb):
        for tgt in rip_targets(ins):
            xrefs.setdefault(tgt, []).append((ins.address, ins.mnemonic))

for sva,lab in sorted(STR_LABELS.items()):
    refs = xrefs.get(sva, [])
    if refs:
        fns = sorted(set(("sub_%X"%func_of(a)[0]) if func_of(a) else "?" for a,_ in refs))
        print("  %-55s xrefs=%d in %s" % (lab, len(refs), ",".join(fns)))

# Disassemble functions passed on argv (hex VAs)
for a in sys.argv[1:]:
    va=int(a,16)
    f=func_of(va)
    if not f: print("no func for 0x%X"%va); continue
    disasm_func(f[0], f[1], "REQUESTED 0x%X"%va)
