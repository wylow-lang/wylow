// The C++ side of interop/constant_values: constants as macros, as an enumerator, and as constexpr
// variables.
#pragma once

#include <cstdint>

#define CODE_OK 0
#define CODE_ROW 100
#define CODE_DONE 101
#define CODE_LIMIT 64

enum { CODE_BUSY = 5 };

constexpr int64_t CODE_WIDE = -5000000000;
constexpr bool CODE_STRICT = true;

inline int32_t code_next(int32_t step) { return step == 0 ? CODE_ROW : step == 1 ? CODE_BUSY : step == 2 ? CODE_DONE : 7; }
