// Wylow runtime support for generated C++. Requires C++20, which defines signed overflow on
// conversion and shifts as two's complement; everything here relies on that.
#pragma once

#include "wyl/core.h"
#include "wyl/arithmetic.h"
#include "wyl/pools.h"
#include "wyl/object.h"
#include "wyl/optional.h"
#include "wyl/cycles.h"
#include "wyl/stack_slot.h"
#include "wyl/functions.h"
#include "wyl/strings.h"
#include "wyl/compare.h"
#include "wyl/intrinsics.h"
