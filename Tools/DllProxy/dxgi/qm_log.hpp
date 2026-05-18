// Quartermaster logging - levels and compile-time diagnostic gating
// -----------------------------------------------------------------
// One file-backed log per process at:
//   %LOCALAPPDATA%/R5/Saved/Logs/Quartermaster_Inject.log
//
// Two macro families:
//
//   QM_LOG_<level>(fmt, ...)   - level-gated normal logging, prefixed by an
//                                 implicit "[Quartermaster] " timestamp header.
//                                 Compiled away when level > QM_LOG_LEVEL.
//
//   #if QM_DIAG ... #endif      - diagnostic code blocks (deep dumps, recon
//                                 routines, etc). Compiled completely out of
//                                 a production build.
//
// Build configurations:
//
//   default (dev)              QM_LOG_LEVEL=5, QM_DIAG=1
//                              -> everything on, useful while iterating.
//
//   -DQM_BUILD_PRODUCTION       QM_LOG_LEVEL=3, QM_DIAG=0
//                              -> ERROR/WARN/INFO only, diag code removed.
//                              Smaller binary, no per-hit spam, no read-only
//                              inspection passes.
//
// Levels (numerical so we can compare in #if):
//   1 = ERROR    - failures that disable a feature (init failed, hook gone).
//   2 = WARN     - degraded paths (fallback offset, FAULT during read).
//   3 = INFO     - lifecycle events (install, ready, success).
//   4 = DEBUG    - per-hit details, content of inspected data.
//   5 = TRACE    - very chatty (every item, every group walk).
//
// Notes:
//   - The LogLine() implementation lives in main.cpp. qm_ue.cpp / qm_scan.cpp
//     route through QmLogA() (a stringified forwarder) because they don't
//     pull in the variadic side here.
//   - LogLine() is intentionally __cdecl (...args) so callers can use printf-
//     style format strings without C++ variadic templates.

#pragma once

// ----- Build config knobs ---------------------------------------------------

#ifdef QM_BUILD_PRODUCTION
#  ifndef QM_LOG_LEVEL
#    define QM_LOG_LEVEL 3   // ERROR + WARN + INFO
#  endif
#  ifndef QM_DIAG
#    define QM_DIAG 0
#  endif
#else
#  ifndef QM_LOG_LEVEL
#    define QM_LOG_LEVEL 5   // everything
#  endif
#  ifndef QM_DIAG
#    define QM_DIAG 1
#  endif
#endif

// ----- Forwarders implemented in main.cpp -----------------------------------

#ifdef __cplusplus
extern "C" {
#endif

// Single-string log forwarder (used by qm_ue.cpp / qm_scan.cpp). Adds the
// standard timestamp + [Quartermaster] prefix, then a newline.
void QmLogA(const char* msg);

// Printf-style log entry point. The macros below funnel into this.
void QmLogF(const char* fmt, ...);

#ifdef __cplusplus
}

// C++-only lifecycle helpers - call from DllMain process-attach / detach.
void QmLogInit();
void QmLogShutdown();
#endif

// ----- Level-gated macros ---------------------------------------------------
// We use plain printf-style macros (no C++ templates) so they expand cleanly
// in both .c and .cpp translation units and so the variadic args evaluate
// only once.

#if QM_LOG_LEVEL >= 1
#  define QM_LOG_ERROR(...) QmLogF(__VA_ARGS__)
#else
#  define QM_LOG_ERROR(...) ((void)0)
#endif

#if QM_LOG_LEVEL >= 2
#  define QM_LOG_WARN(...)  QmLogF(__VA_ARGS__)
#else
#  define QM_LOG_WARN(...)  ((void)0)
#endif

#if QM_LOG_LEVEL >= 3
#  define QM_LOG_INFO(...)  QmLogF(__VA_ARGS__)
#else
#  define QM_LOG_INFO(...)  ((void)0)
#endif

#if QM_LOG_LEVEL >= 4
#  define QM_LOG_DEBUG(...) QmLogF(__VA_ARGS__)
#else
#  define QM_LOG_DEBUG(...) ((void)0)
#endif

#if QM_LOG_LEVEL >= 5
#  define QM_LOG_TRACE(...) QmLogF(__VA_ARGS__)
#else
#  define QM_LOG_TRACE(...) ((void)0)
#endif
