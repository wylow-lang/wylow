// The C++ side of interop/signature_mismatch, which differs from the extern block on purpose.
#pragma once

#include <cstdint>

#define CODEC_LIMIT 10u
#define CODEC_LIMIT_VALUE 10u

int64_t codec_encode(int32_t value);
int32_t codec_decode(int64_t value);
int32_t codec_check(int32_t value, int32_t mask);
void codec_reset();
int32_t codec_flags(const char* name);
int32_t codec_apply(int64_t (*step)(int32_t value));
int32_t codec_visit(void (*visit)(const char* text, int32_t length));
