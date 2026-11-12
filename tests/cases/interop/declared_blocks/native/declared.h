// The C++ side of interop/declared_blocks: declarations that the program never calls.
#pragma once

#include <cstdint>

namespace parser {
constexpr int32_t MAX_TOKENS = 128;

struct Parser;

struct Range {
    int32_t first;
    int32_t length;
};

union Value {
    int32_t asInt;
    float asFloat;
};

Parser* parser_create(int32_t capacity);
uint64_t version();
}
