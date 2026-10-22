// VPL runtime support for generated C++. Requires C++20, which defines signed overflow on
// conversion and shifts as two's complement; everything here relies on that.
#pragma once

#include "vpl/core.h"
#include "vpl/arithmetic.h"
#include "vpl/pools.h"
#include "vpl/object.h"
#include "vpl/optional.h"
#include "vpl/cycles.h"
#include "vpl/stack_slot.h"
#include "vpl/functions.h"
#include "vpl/intrinsics.h"
