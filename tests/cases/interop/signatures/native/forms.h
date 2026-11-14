#pragma once

#include <cstddef>
#include <cstdint>

struct Pair {
    int32_t first;
    int32_t second;
};

union Bits {
    int32_t whole;
    float real;
};

char32_t letter_next(char32_t letter);
intptr_t scale_pointer(intptr_t value);
uint32_t shade_code(uint32_t shade);
uint32_t shade_of(uint32_t code);
int32_t one_byte_sum(signed char* data, int32_t count);
int32_t byte_count(unsigned char* data, int32_t count);
