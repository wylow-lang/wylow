// The source of interop/manifest_native. On Windows, it calls a function of shlwapi, which a program
// links only if the manifest names it.
#include "greeting.h"

#ifdef _WIN32
#include <windows.h>
#include <shlwapi.h>
#else
#include <cmath>
#endif

int greeting_value() { return GREETING_BASE + GREETING_OFFSET; }

int greeting_relative() {
#ifdef _WIN32
    return PathIsRelativeA("parser\include") ? 1 : 0;
#else
    return std::fabs(-1.0) == 1.0 ? 1 : 0;
#endif
}
