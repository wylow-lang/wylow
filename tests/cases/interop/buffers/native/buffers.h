// The C++ side of interop/buffers: a header and source that the test defines itself.
#pragma once

#include <cstdint>

struct Size {
    int32_t width;
    int32_t height;
};

extern "C" {
void size_of_grid(int32_t cells, Size* out);
int32_t divide(int32_t a, int32_t b, int32_t* remainder);
int64_t sum_values(const int32_t* values, int32_t count);
void fill_squares(int32_t* values, int32_t count);
}
