// Quartermaster crash diagnostics
// -------------------------------
// VEH (first-chance) + UEF (unhandled) handlers that snapshot Quartermaster
// state on hard crashes (AV / SO / Illegal / IN_PAGE / NONCONTINUABLE).
//
// __try/__except-handled AVs from our probe reads are filtered out because
// they only fire as second-chance via the SEH unwind, not as VEH first-chance
// at the original RIP. The filter QmIsHardCrash() additionally limits noise.
//
// QmCrashInstallHandler() is idempotent and safe to call from WorkerThread.

#pragma once

void QmCrashInstallHandler();
