#include "names.h"

int32_t codec_parse(int32_t value) { return value + 1; }

namespace codec {
Parser* parser_create() { return new Parser(); }
}
