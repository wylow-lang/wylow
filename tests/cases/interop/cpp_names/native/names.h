#pragma once

#include <cstdint>

inline int32_t codec_level = 7;

struct Settings {
    int32_t width;
};

int32_t codec_parse(int32_t value);

namespace codec {
struct Parser {
    static constexpr int32_t LIMIT = 9;

    int32_t code;

    int32_t take(int32_t value) const { return value + 1; }
};

Parser* parser_create();
}
