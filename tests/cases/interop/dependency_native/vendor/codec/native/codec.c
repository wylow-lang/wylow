/* Sees the library's own definitions and headers, and not the program's. On Windows, it calls a
   function of shlwapi, which the program links only because the library's manifest names it. */
#include "util.h"

#ifdef _WIN32
#include <windows.h>
#include <shlwapi.h>
#else
#include <math.h>
#endif

#ifdef APP_BASE
#error "a definition of the program reached the library's native source"
#endif

int codec_scaled(void) { return CODEC_LEVEL * 10; }

int codec_relative(void) {
#ifdef _WIN32
    return PathIsRelativeA("codec") ? 1 : 0;
#else
    volatile double value = -1.0;
    return fabs(value) == 1.0 ? 1 : 0;
#endif
}
