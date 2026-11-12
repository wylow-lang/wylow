#include "visit.h"

#include <cstdio>

void visit_values(void (*visit)(void* context, int32_t value), void* context, int32_t count) {
    for (int32_t i = 0; i < count; i++) visit(context, i);
    std::puts("visit_values returned");
}
