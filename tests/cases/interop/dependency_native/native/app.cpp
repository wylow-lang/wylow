// Sees the program's own definitions and headers, and not the library's.
#include "util.h"

#ifdef CODEC_LEVEL
#error "a definition of the library reached the program's native source"
#endif

int app_value() { return APP_BASE + APP_KIND; }
