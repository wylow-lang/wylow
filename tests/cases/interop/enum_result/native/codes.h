// The C++ side of interop/enum_result: a function that returns an enumeration's integer values.
#pragma once

#include <cstdint>

enum Status : int32_t { Ok = 0, Busy = 5, Failed = -1 };

extern "C" Status status_of(int32_t code);
