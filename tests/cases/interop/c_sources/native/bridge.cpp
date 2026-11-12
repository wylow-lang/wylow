// A C++ source that calls a function of a C source through the same header.
#include "tally.h"

extern "C" int32_t tally_from_cpp(int32_t value) { return tally_scaled(value) + 1; }
