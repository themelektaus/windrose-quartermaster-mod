import pefile, struct, sys
from capstone import Cs, CS_ARCH_X86, CS_MODE_64
from capstone.x86 import X86_REG_RIP, X86_OP_IMM, X86_OP_MEM

DLL = r"E:\Windrose\Mods\Quartermaster\References\Windrose Mod Settings\R5ModSettings\dlls\main.dll"
pe = pefile.PE(DLL, fast_load=False)
base = pe.OPTIONAL_HEADER.ImageBase
img = pe.get_memory_mapped_image()
def at(va,n): return img[va-base:va-base+n]

iat={}
for imp in pe.DIRECTORY_ENTRY_IMPORT:
    for f in imp.imports:
        iat[f.address]= f.name.decode(errors='replace') if f.name else ("ord%s"%f.ordinal)

funcs=[]
for s in pe.sections:
    if s.Name.rstrip(b"\0")==b".pdata": pd=s.get_data()
    if s.Name.rstrip(b"\0")==b".text": textva=base+s.VirtualAddress; textend=textva+s.Misc_VirtualSize
for off in range(0,len(pd),12):
    b,e,u=struct.unpack_from("<III",pd,off)
    if b or e: funcs.append((base+b,base+e))
funcs=sorted(set(funcs))
starts=[f[0] for f in funcs]
import bisect
def func_of(va):
    i=bisect.bisect_right(starts,va)-1
    if i>=0 and funcs[i][0]<=va<funcs[i][1]: return funcs[i][0]
    return None

md=Cs(CS_ARCH_X86,CS_MODE_64); md.detail=True
callers={}  # callee -> set(caller_start)
calls={}    # caller_start -> list((site,callee,kind))
for fb,fe in funcs:
    if not(textva<=fb<textend): continue
    for ins in md.disasm(at(fb,fe-fb),fb):
        if ins.mnemonic in ("call","jmp"):
            op=ins.operands[0] if ins.operands else None
            if op is None: continue
            if op.type==X86_OP_IMM:
                tgt=op.imm
                callers.setdefault(tgt,set()).add(fb)
                calls.setdefault(fb,[]).append((ins.address,tgt,ins.mnemonic))
            elif op.type==X86_OP_MEM and op.mem.base==X86_REG_RIP:
                tgt=ins.address+ins.size+op.mem.disp
                nm=iat.get(tgt)
                if nm:
                    calls.setdefault(fb,[]).append((ins.address,"IMP:"+nm,ins.mnemonic))

def name(va):
    return "sub_%X"%va

mode=sys.argv[1]
for a in sys.argv[2:]:
    va=int(a,16); fb=func_of(va) or va
    if mode=="callers":
        cs=sorted(callers.get(fb,[]))
        print("CALLERS of sub_%X (%d):"%(fb,len(cs)))
        for c in cs: print("   sub_%X"%c)
    elif mode=="calls":
        print("CALLS from sub_%X:"%fb)
        for site,tgt,kind in calls.get(fb,[]):
            t = tgt if isinstance(tgt,str) else ("sub_%X"%tgt)
            print("   0x%X %s %s"%(site,kind,t))
