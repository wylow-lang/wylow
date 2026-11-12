/* Compiles only as C: C converts a void* to another object pointer implicitly, and C++ does not. */
#include <stdlib.h>
#include "tally.h"

int32_t tally_standard(void) { return (int32_t)__STDC_VERSION__; }

int32_t tally_start(void) { return TALLY_START; }

void tally_add(Tally* tally, int32_t value) {
    tally->count += 1;
    tally->sum += value;
}

int32_t tally_each(tally_step step, void* context, int32_t count) {
    int32_t* values = malloc(sizeof(int32_t) * (size_t)count);
    int32_t total = 0;
    for (int32_t i = 0; i < count; i++)
        values[i] = step(context, i);
    for (int32_t i = 0; i < count; i++)
        total += values[i];
    free(values);
    return total;
}
