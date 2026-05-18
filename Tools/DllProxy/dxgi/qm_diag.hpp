// Quartermaster diagnostic inspectors
// -----------------------------------
// Read-only deep dumps used during recon. Entirely compiled out in production
// builds (QM_BUILD_PRODUCTION) via the QM_DIAG=0 gate. Every function below
// is callable unconditionally; the gate happens in this header so call sites
// stay clean.

#pragma once

#include "qm_log.hpp"
#include "qm_ue.hpp"

#if QM_DIAG

void DiagInspectInputs(void* Result, void* Stack);
void DiagInspectGroupResult(void* Result, bool deep);
void DiagInspectFirstGroupSoftPaths(void* Result);
int  DiagFindUFunctionsByName(const char* funcName, int maxLog);
void DiagDumpClassBytes(QmUE::UClass* cls, const char* label);

#else

// Stubs - compile away in production.
static inline void DiagInspectInputs(void*, void*) {}
static inline void DiagInspectGroupResult(void*, bool) {}
static inline void DiagInspectFirstGroupSoftPaths(void*) {}
static inline int  DiagFindUFunctionsByName(const char*, int) { return 0; }
static inline void DiagDumpClassBytes(QmUE::UClass*, const char*) {}

#endif // QM_DIAG
