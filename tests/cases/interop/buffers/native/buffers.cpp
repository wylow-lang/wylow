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

uint32_t checksum(const void* data, int32_t size) {
    const unsigned char* bytes = static_cast<const unsigned char*>(data);
    uint32_t sum = 0;
    for (int32_t i = 0; i < size; i++) sum = sum * 31 + bytes[i];
    return sum;
}

void* advance(void* data, int32_t size) {
    return static_cast<unsigned char*>(data) + size;
}

void fill_squares(int32_t* values, int32_t count) {
    for (int32_t i = 0; i < count; i++) values[i] = i * i;
}
