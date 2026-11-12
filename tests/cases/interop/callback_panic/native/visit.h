// The C++ side of interop/callback_panic: a function that calls back for each value.
#pragma once

#include <cstdint>

extern "C" void visit_values(void (*visit)(void* context, int32_t value), void* context, int32_t count);
