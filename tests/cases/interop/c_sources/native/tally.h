/* The C side of interop/c_sources. A C++ file that includes this header sees C's names, because the
   declarations are inside extern "C". */
#ifndef TALLY_H
#define TALLY_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef int32_t (*tally_step)(void* context, int32_t value);

typedef struct Tally {
    int32_t count;
    int32_t sum;
} Tally;

int32_t tally_standard(void);
int32_t tally_start(void);
void tally_add(Tally* tally, int32_t value);
int32_t tally_each(tally_step step, void* context, int32_t count);
int32_t tally_scaled(int32_t value);
int32_t tally_from_cpp(int32_t value);

#ifdef __cplusplus
}
#endif

#endif
