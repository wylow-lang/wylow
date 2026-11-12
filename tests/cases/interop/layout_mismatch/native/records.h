// The C++ side of interop/layout_mismatch, which differs from the extern block on purpose.
#pragma once

#include <cstdint>

struct Pair {
    int32_t first;
    int32_t second;
};

struct Wide {
    int64_t value;
    int32_t tag;
};

struct Reordered {
    int32_t b;
    int32_t a;
};

struct Longer {
    int32_t value;
    int32_t extra;
};

union Cell {
    int64_t wide;
    int32_t narrow;
};
