; Quartermaster dxgi.dll Passthrough Trampolines
; -----------------------------------------------
; One 1-instruction PROC per exported dxgi function. Each does:
;   jmp [g_real_<Name>]
; where g_real_<Name> is a function pointer populated by passthrough.cpp's
; ResolveSystemDxgi() during DllMain. After resolve, calls into our exports
; tail-jump straight to the system DLL with calling convention preserved
; (rcx/rdx/r8/r9, xmm0-3, stack layout - everything untouched).
;
; Used together with passthrough.def which declares the 19 names as exports
; in the PE header. The MASM PUBLIC + DEF combo gives the linker enough info
; to resolve the export -> code address mapping.

.code

EXTRN g_real_ApplyCompatResolutionQuirking:QWORD
EXTRN g_real_CompatString:QWORD
EXTRN g_real_CompatValue:QWORD
EXTRN g_real_DXGIDumpJournal:QWORD
EXTRN g_real_PIXBeginCapture:QWORD
EXTRN g_real_PIXEndCapture:QWORD
EXTRN g_real_PIXGetCaptureState:QWORD
EXTRN g_real_SetAppCompatStringPointer:QWORD
EXTRN g_real_UpdateHMDEmulationStatus:QWORD
EXTRN g_real_CreateDXGIFactory:QWORD
EXTRN g_real_CreateDXGIFactory1:QWORD
EXTRN g_real_CreateDXGIFactory2:QWORD
EXTRN g_real_DXGID3D10CreateDevice:QWORD
EXTRN g_real_DXGID3D10CreateLayeredDevice:QWORD
EXTRN g_real_DXGID3D10GetLayeredDeviceSize:QWORD
EXTRN g_real_DXGID3D10RegisterLayers:QWORD
EXTRN g_real_DXGIDeclareAdapterRemovalSupport:QWORD
EXTRN g_real_DXGIGetDebugInterface1:QWORD
EXTRN g_real_DXGIReportAdapterConfiguration:QWORD

QM_PASSTHROUGH MACRO Name
PUBLIC Name
ALIGN 16
Name PROC
    jmp QWORD PTR [g_real_&Name]
Name ENDP
ENDM

QM_PASSTHROUGH ApplyCompatResolutionQuirking
QM_PASSTHROUGH CompatString
QM_PASSTHROUGH CompatValue
QM_PASSTHROUGH DXGIDumpJournal
QM_PASSTHROUGH PIXBeginCapture
QM_PASSTHROUGH PIXEndCapture
QM_PASSTHROUGH PIXGetCaptureState
QM_PASSTHROUGH SetAppCompatStringPointer
QM_PASSTHROUGH UpdateHMDEmulationStatus
QM_PASSTHROUGH CreateDXGIFactory
QM_PASSTHROUGH CreateDXGIFactory1
QM_PASSTHROUGH CreateDXGIFactory2
QM_PASSTHROUGH DXGID3D10CreateDevice
QM_PASSTHROUGH DXGID3D10CreateLayeredDevice
QM_PASSTHROUGH DXGID3D10GetLayeredDeviceSize
QM_PASSTHROUGH DXGID3D10RegisterLayers
QM_PASSTHROUGH DXGIDeclareAdapterRemovalSupport
QM_PASSTHROUGH DXGIGetDebugInterface1
QM_PASSTHROUGH DXGIReportAdapterConfiguration

END
