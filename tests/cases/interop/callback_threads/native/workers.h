// The C++ side of interop/callback_threads: a function that calls back on threads it starts.
#pragma once

#include <cstdint>

extern "C" int64_t workers_run(int64_t (*work)(int32_t index), int32_t threads);
