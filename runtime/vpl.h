// VPL runtime support for generated C++. Requires C++20, which defines signed overflow on
// conversion and shifts as two's complement; everything here relies on that.
#pragma once

#include <cfloat>
#include <charconv>
#include <cinttypes>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <type_traits>

#ifndef VPL_DEBUG
#define VPL_DEBUG 0
#endif

static_assert(std::numeric_limits<float>::is_iec559 && std::numeric_limits<double>::is_iec559,
              "VPL requires IEEE 754 floating point");

namespace vpl {

struct String {
    const char* data;
    int32_t length;
};

[[noreturn]] inline void panic(const char* message, const char* location) {
    std::fflush(stdout);
    std::fprintf(stderr, "panic: %s\n    at %s\n", message, location);
    std::fflush(stderr);
    std::abort();
}

[[noreturn]] inline void panic(String message, const char* location) {
    std::fflush(stdout);
    std::fprintf(stderr, "panic: %.*s\n    at %s\n", static_cast<int>(message.length), message.data, location);
    std::fflush(stderr);
    std::abort();
}

// Flow analysis rules this out; reaching it means a compiler bug, which must still not be UB.
[[noreturn]] inline void unreachable(const char* location) {
    panic("reached the end of a non-void function", location);
}

inline void init() {
#if defined(_WIN32) && defined(_WRITE_ABORT_MSG)
    // Abort quietly instead of showing a dialog or invoking Windows Error Reporting.
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
#endif
}

// Integer arithmetic: overflow panics when VPL_DEBUG, otherwise wraps. Unsigned math avoids
// UB; Wide<T> keeps small types from being promoted to signed int before the operation.
template <class T>
using Wide = std::common_type_t<std::make_unsigned_t<T>, unsigned>;

template <class T>
inline T add(T a, T b, const char* location) {
    T r = static_cast<T>(static_cast<Wide<T>>(a) + static_cast<Wide<T>>(b));
    if constexpr (VPL_DEBUG != 0) {
        if (((a ^ r) & (b ^ r)) < 0) panic("integer overflow", location);
    }
    return r;
}

template <class T>
inline T sub(T a, T b, const char* location) {
    T r = static_cast<T>(static_cast<Wide<T>>(a) - static_cast<Wide<T>>(b));
    if constexpr (VPL_DEBUG != 0) {
        if (((a ^ b) & (a ^ r)) < 0) panic("integer overflow", location);
    }
    return r;
}

template <class T>
inline bool mul_overflows(T a, T b) {
    constexpr T max = std::numeric_limits<T>::max();
    constexpr T min = std::numeric_limits<T>::min();
    if (a == 0 || b == 0) return false;
    if (a > 0) return b > 0 ? a > max / b : b < min / a;
    return b > 0 ? a < min / b : a < max / b;
}

template <class T>
inline T mul(T a, T b, const char* location) {
    if constexpr (VPL_DEBUG != 0) {
        if (mul_overflows(a, b)) panic("integer overflow", location);
    }
    return static_cast<T>(static_cast<Wide<T>>(a) * static_cast<Wide<T>>(b));
}

template <class T>
inline T neg(T a, const char* location) {
    if constexpr (VPL_DEBUG != 0) {
        if (a == std::numeric_limits<T>::min()) panic("integer overflow", location);
    }
    return static_cast<T>(Wide<T>(0) - static_cast<Wide<T>>(a));
}

// MIN / -1 has no wrapped result that is useful, so it panics in every build.
template <class T>
inline T div(T a, T b, const char* location) {
    if (b == 0) panic("division by zero", location);
    if (b == -1 && a == std::numeric_limits<T>::min()) panic("integer overflow", location);
    return a / b;
}

template <class T>
inline T rem(T a, T b, const char* location) {
    if (b == 0) panic("division by zero", location);
    if (b == -1) return 0;
    return a % b;
}

template <class T, class N>
inline T shl(T a, N n) {
    constexpr int mask = sizeof(T) * 8 - 1;
    return static_cast<T>(static_cast<Wide<T>>(a) << (static_cast<int>(n) & mask));
}

template <class T, class N>
inline T shr(T a, N n) {
    constexpr int mask = sizeof(T) * 8 - 1;
    return static_cast<T>(a >> (static_cast<int>(n) & mask));
}

// Float to integer saturates and maps NaN to 0; a plain C++ cast is UB when out of range.
template <class T, class F>
inline T f2i(F x) {
    constexpr T min = std::numeric_limits<T>::min();
    constexpr T max = std::numeric_limits<T>::max();
    if (std::isnan(x)) return 0;
    if (x <= static_cast<F>(min)) return min;
    if (x >= static_cast<F>(max)) return max;
    return static_cast<T>(x);
}

// Rounds to nearest like IEEE, including overflow to infinity; a plain C++ cast is UB out of range.
inline float d2f(double x) {
    constexpr double overflow = static_cast<double>(FLT_MAX) + 0x1p103;
    if (std::isnan(x)) return std::numeric_limits<float>::quiet_NaN();
    if (x >= overflow) return std::numeric_limits<float>::infinity();
    if (x <= -overflow) return -std::numeric_limits<float>::infinity();
    if (x > FLT_MAX) return FLT_MAX;
    if (x < -FLT_MAX) return -FLT_MAX;
    return static_cast<float>(x);
}

inline void print(int32_t value) { std::printf("%" PRId32 "\n", value); }
inline void print(int64_t value) { std::printf("%" PRId64 "\n", value); }
inline void print(bool value) { std::puts(value ? "true" : "false"); }

inline void print(String value) {
    std::fwrite(value.data, 1, static_cast<size_t>(value.length), stdout);
    std::fputc('\n', stdout);
}

template <class F>
inline void print_float(F value) {
    char buffer[64];
    auto result = std::to_chars(buffer, buffer + sizeof buffer, value);
    size_t length = static_cast<size_t>(result.ptr - buffer);
    std::fwrite(buffer, 1, length, stdout);
    // Whole numbers print as "3.0", like Java; "inf" and "nan" are left alone.
    if (std::memchr(buffer, '.', length) == nullptr && std::memchr(buffer, 'e', length) == nullptr &&
        std::memchr(buffer, 'n', length) == nullptr) {
        std::fputs(".0", stdout);
    }
    std::fputc('\n', stdout);
}

inline void print(float value) { print_float(value); }
inline void print(double value) { print_float(value); }

}  // namespace vpl
