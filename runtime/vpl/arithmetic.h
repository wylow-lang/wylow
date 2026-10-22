#pragma once

#include "core.h"

namespace vpl {

// Integer arithmetic: overflow panics when VPL_DEBUG, otherwise wraps. Unsigned math avoids
// UB; Wide<T> keeps small types from being promoted to signed int before the operation.
template <class T>
using Wide = std::common_type_t<std::make_unsigned_t<T>, unsigned>;

template <class T>
inline T add(T a, T b, const char* location) {
    T r = static_cast<T>(static_cast<Wide<T>>(a) + static_cast<Wide<T>>(b));
    if constexpr (VPL_DEBUG != 0) {
        if constexpr (std::is_signed_v<T>) {
            if (((a ^ r) & (b ^ r)) < 0) panic("integer overflow", location);
        } else {
            if (r < a) panic("integer overflow", location);
        }
    }
    return r;
}

template <class T>
inline T sub(T a, T b, const char* location) {
    T r = static_cast<T>(static_cast<Wide<T>>(a) - static_cast<Wide<T>>(b));
    if constexpr (VPL_DEBUG != 0) {
        if constexpr (std::is_signed_v<T>) {
            if (((a ^ b) & (a ^ r)) < 0) panic("integer overflow", location);
        } else {
            if (a < b) panic("integer overflow", location);
        }
    }
    return r;
}

template <class T>
inline bool mul_overflows(T a, T b) {
    constexpr T max = std::numeric_limits<T>::max();
    if (a == 0 || b == 0) return false;
    if constexpr (!std::is_signed_v<T>) {
        return a > max / b;
    } else {
        constexpr T min = std::numeric_limits<T>::min();
        if (a > 0) return b > 0 ? a > max / b : b < min / a;
        return b > 0 ? a < min / b : a < max / b;
    }
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
    if constexpr (std::is_signed_v<T>) {
        if (b == -1 && a == std::numeric_limits<T>::min()) panic("integer overflow", location);
    }
    return static_cast<T>(a / b);
}

template <class T>
inline T rem(T a, T b, const char* location) {
    if (b == 0) panic("division by zero", location);
    if constexpr (std::is_signed_v<T>) {
        if (b == -1) return 0;
    }
    return static_cast<T>(a % b);
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

// A char holds a Unicode scalar value: at most 0x10FFFF, and not a surrogate.
template <class T>
inline char32_t to_char(T value, const char* location) {
    if (std::in_range<uint32_t>(value)) {
        auto v = static_cast<uint32_t>(value);
        if (v <= 0x10FFFFu && (v < 0xD800u || v > 0xDFFFu)) return static_cast<char32_t>(v);
    }
    panic("integer is not a Unicode scalar value", location);
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

}  // namespace vpl
