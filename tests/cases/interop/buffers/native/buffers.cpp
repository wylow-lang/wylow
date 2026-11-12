#include "buffers.h"

void size_of_grid(int32_t cells, Size* out) {
    out->width = cells / 2;
    out->height = 2;
}

int32_t divide(int32_t a, int32_t b, int32_t* remainder) {
    *remainder = a % b;
    return a / b;
}

int64_t sum_values(const int32_t* values, int32_t count) {
    int64_t sum = 0;
    for (int32_t i = 0; i < count; i++) sum += values[i];
    return sum;
}

void fill_squares(int32_t* values, int32_t count) {
    for (int32_t i = 0; i < count; i++) values[i] = i * i;
}
