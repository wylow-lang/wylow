#include "forms.h"

char32_t letter_next(char32_t letter) { return letter + 1; }
intptr_t scale_pointer(intptr_t value) { return value * 2; }
uint32_t shade_code(uint32_t shade) { return shade * 10; }
uint32_t shade_of(uint32_t code) { return code; }

int32_t one_byte_sum(signed char* data, int32_t count) {
    int32_t sum = 0;
    for (int32_t i = 0; i < count; i++) sum += data[i];
    return sum;
}

int32_t byte_count(unsigned char* data, int32_t count) {
    int32_t sum = 0;
    for (int32_t i = 0; i < count; i++) sum += data[i];
    return sum;
}
