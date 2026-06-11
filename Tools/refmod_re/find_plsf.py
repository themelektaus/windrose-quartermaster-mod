# Find ProcessLocalScriptFunction in the Windrose shipping EXE.
#
# Why: UFunction::Func (ExecFunction) is only read on the ProcessEvent->Invoke
# path. BP-internal calls (EX_LocalFinalFunction etc.) execute through
# ProcessLocalScriptFunction (PLSF) directly and never touch Func - proven by
# the 18q log (swap verified in the field via enum readback, zero thunk fires
# across 8 tab clicks). UE4SS catches these via its HookProcessLocalScriptFunction
# layer. We replicate that: hook PLSF with MinHook.
#
# How: UObject::ProcessInternal calls PLSF directly (UE5 ScriptCore: the
# Local-callspace branch is "ProcessLocalScriptFunction(Context, Stack, RESULT)").
# We know ProcessInternal's runtime VA from the log. Disassemble its body,
# resolve all rel32 call/jmp targets, and identify PLSF by its body signature:
# the bytecode step loop "while (*Stack.Code != EX_Return) Stack.Step(...)"
# -> `cmp byte ptr [...], 4` (EX_Return == 0x04) + the GNatives dispatch
# `call qword ptr [reg + rax*8 (+disp)]`.

import struct
import sys

import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64
from capstone.x86 import X86_OP_MEM, X86_OP_IMM

EXE = r"E:\Games\steamapps\common\Windrose\R5\Binaries\Win64\Windrose-Win64-Shipping.exe"
RUNTIME_BASE = 0x00007FF670650000
PI_VA = 0x00007FF671D739D0   # UObject::ProcessInternal (runtime, 512/512 BP-exec votes)
PE_VA = 0x00007FF671D735E0   # UObject::ProcessEvent  (runtime, scan) - context only

PI_RVA = PI_VA - RUNTIME_BASE
PE_RVA = PE_VA - RUNTIME_BASE

pe = pefile.PE(EXE, fast_load=True)

# ---- raw .pdata access (full pefile exception-dir parse is too slow on a big EXE)
pdata = None
text = None
for s in pe.sections:
    name = s.Name.rstrip(b"\x00").decode()
    if name == ".pdata":
        pdata = s
    if name == ".text":
        text = s
assert pdata is not None and text is not None
pdata_raw = pe.__data__[pdata.PointerToRawData:pdata.PointerToRawData + pdata.SizeOfRawData]


def func_bounds(rva):
    """Binary-search RUNTIME_FUNCTION entries (12 bytes: begin, end, unwind)."""
    lo, hi = 0, len(pdata_raw) // 12
    while lo < hi:
        mid = (lo + hi) // 2
        begin, end, _ = struct.unpack_from("<III", pdata_raw, mid * 12)
        if begin == 0:          # zero padding at tail
            hi = mid
            continue
        if rva < begin:
            hi = mid
        elif rva >= end:
            lo = mid + 1
        else:
            return begin, end
    return None


def read_rva(rva, size):
    off = pe.get_offset_from_rva(rva)
    return pe.__data__[off:off + size]


md = Cs(CS_ARCH_X86, CS_MODE_64)
md.detail = True
imgbase = pe.OPTIONAL_HEADER.ImageBase


def disasm_func(rva, max_bytes=None):
    b = func_bounds(rva)
    if not b:
        return None, []
    begin, end = b
    if max_bytes:
        end = min(end, begin + max_bytes)
    code = read_rva(begin, end - begin)
    return b, list(md.disasm(code, imgbase + begin))


def rel_targets(insns):
    """rel32 call/jmp targets as RVAs."""
    out = []
    for i in insns:
        if i.mnemonic in ("call", "jmp") and len(i.operands) == 1:
            op = i.operands[0]
            if op.type == X86_OP_IMM:
                out.append((i.address - imgbase, i.mnemonic, op.imm - imgbase))
    return out


def plsf_score(insns):
    """Signature: cmp byte [..],4 (EX_Return) + call [reg+rax*8] (GNatives)."""
    has_exreturn = any(
        i.mnemonic == "cmp" and i.op_str.startswith("byte ptr") and i.op_str.endswith(", 4")
        for i in insns)
    has_gnatives = any(
        i.mnemonic == "call" and i.operands and i.operands[0].type == X86_OP_MEM
        and i.operands[0].mem.scale == 8
        for i in insns)
    return has_exreturn, has_gnatives


bounds, insns = disasm_func(PI_RVA)
if not bounds:
    print(f"ProcessInternal RVA {PI_RVA:#x} not found in .pdata - wrong base/VA?")
    sys.exit(1)
print(f"ProcessInternal: RVA {bounds[0]:#x}..{bounds[1]:#x} ({bounds[1]-bounds[0]} bytes, {len(insns)} insns)")
print(f"  (ProcessEvent RVA for context: {PE_RVA:#x})")
print()
for i in insns:
    print(f"  {i.address - imgbase:#010x}  {i.mnemonic:8s} {i.op_str}")
print()

targets = rel_targets(insns)
print(f"{len(targets)} rel32 call/jmp target(s) from ProcessInternal:")
for src, mn, tgt in targets:
    tb = func_bounds(tgt)
    size = (tb[1] - tb[0]) if tb else -1
    _, tinsns = disasm_func(tgt, max_bytes=0x400)
    exr, gn = plsf_score(tinsns)
    mark = " <=== PLSF CANDIDATE" if (exr and gn) else ""
    print(f"  {mn} @{src:#x} -> RVA {tgt:#010x} (size={size}, EX_Return-cmp={exr}, GNatives-call={gn}){mark}")
    if exr and gn:
        runtime_va = RUNTIME_BASE + tgt
        print(f"       runtime VA (this session's base): {runtime_va:#018x}")
        print(f"       OFFSET_ProcessLocalScriptFunction = 0x{tgt:X}")
