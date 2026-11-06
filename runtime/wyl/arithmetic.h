#pragma once

#include "core.h"

namespace wyl {

// Integer arithmetic: overflow panics when WYL_DEBUG, otherwise wraps. Unsigned math avoids
// UB; Wide<T> keeps small types from being promoted to signed int before the operation.
template <class T>
using Wide = std::common_type_t<std::make_unsigned_t<T>, unsigned>;

template <class T>
inline T Add(T a, T b, const char* location) {
    T r = static_cast<T>(static_cast<Wide<T>>(a) + static_cast<Wide<T>>(b));
    if constexpr (WYL_DEBUG != 0) {
        if constexpr (std::is_signed_v<T>) {
            if (((a ^ r) & (b ^ r)) < 0) RaisePanic("integer overflow", location);
        } else {
            if (r < a) RaisePanic("integer overflow", location);
        }
    }
    return r;
}

template <class T>
inline T Sub(T a, T b, const char* location) {
    T r = static_cast<T>(static_cast<Wide<T>>(a) - static_cast<Wide<T>>(b));
    if constexpr (WYL_DEBUG != 0) {
        if constexpr (std::is_signed_v<T>) {
            if (((a ^ b) & (a ^ r)) < 0) RaisePanic("integer overflow", location);
        } else {
            if (a < b) RaisePanic("integer overflow", location);
        }
    }
    return r;
}

template <class T>
inline bool MulOverflows(T a, T b) {
    constexpr T MAX = std::numeric_limits<T>::max();
    if (a == 0 || b == 0) return false;
    if constexpr (!std::is_signed_v<T>) {
        return a > MAX / b;
    } else {
        constexpr T MIN = std::numeric_limits<T>::min();
        if (a > 0) return b > 0 ? a > MAX / b : b < MIN / a;
        return b > 0 ? a < MIN / b : a < MAX / b;
    }
}

template <class T>
inline T Mul(T a, T b, const char* location) {
    if constexpr (WYL_DEBUG != 0) {
        if (MulOverflows(a, b)) RaisePanic("integer overflow", location);
    }
    return static_cast<T>(static_cast<Wide<T>>(a) * static_cast<Wide<T>>(b));
}

template <class T>
inline T Neg(T a, const char* location) {
    if constexpr (WYL_DEBUG != 0) {
        if (a == std::numeric_limits<T>::min()) RaisePanic("integer overflow", location);
    }
    return static_cast<T>(Wide<T>(0) - static_cast<Wide<T>>(a));
}

// A complement of a narrow type is promoted to int, so `~` beside a conversion can be folded with
// the conversion's sign. MSVC /O2 then miscompiles `-static_cast<double>(static_cast<int16_t>(~f()))`
// when f returns a signed narrow type, negating the complement in the narrow type; the call keeps
// the two apart.
template <class T>
inline T BitNot(T a) {
    return static_cast<T>(~static_cast<Wide<T>>(a));
}

// MIN / -1 has no wrapped result that is useful, so it panics in every build.
template <class T>
inline T Div(T a, T b, const char* location) {
    if (b == 0) RaisePanic("division by zero", location);
    if constexpr (std::is_signed_v<T>) {
        if (b == -1 && a == std::numeric_limits<T>::min()) RaisePanic("integer overflow", location);
    }
    return static_cast<T>(a / b);
}

// IEEE 754 defines division by zero as an infinity or a non-number. It always runs, since MSVC
// rejects a constant division by zero (C2124) even where the language defines its result.
template <class F>
    requires std::is_floating_point_v<F>
inline F FloatDiv(F a, F b) {
    return a / b;
}

// The signed remainder works on magnitudes: MSVC 14.50 and 14.51 /O2 remove the `b == -1` case
// and emit a bare `idiv` for a remainder whose dividend is the minimum value, which raises
// STATUS_INTEGER_FLOAT_OVERFLOW. Unsigned `%` never traps.
template <class T>
inline T Rem(T a, T b, const char* location) {
    if (b == 0) RaisePanic("division by zero", location);
    if constexpr (std::is_signed_v<T>) {
        using U = std::make_unsigned_t<T>;
        U ua = a < 0 ? static_cast<U>(0) - static_cast<U>(a) : static_cast<U>(a);
        U ub = b < 0 ? static_cast<U>(0) - static_cast<U>(b) : static_cast<U>(b);
        U r = static_cast<U>(ua % ub);
        return static_cast<T>(a < 0 && r != 0 ? static_cast<U>(0) - r : r);
    } else {
        return static_cast<T>(a % b);
    }
}

template <class T, class N>
inline T ShiftLeft(T a, N n) {
    constexpr int MASK = sizeof(T) * 8 - 1;
    return static_cast<T>(static_cast<Wide<T>>(a) << (static_cast<int>(n) & MASK));
}

template <class T, class N>
inline T ShiftRight(T a, N n) {
    constexpr int MASK = sizeof(T) * 8 - 1;
    return static_cast<T>(a >> (static_cast<int>(n) & MASK));
}

// Float to integer saturates and maps NaN to 0; a plain C++ cast is UB when out of range.
template <class T, class F>
inline T FloatToInt(F x) {
    constexpr T MIN = std::numeric_limits<T>::min();
    constexpr T MAX = std::numeric_limits<T>::max();
    if (std::isnan(x)) return 0;
    if (x <= static_cast<F>(MIN)) return MIN;
    if (x >= static_cast<F>(MAX)) return MAX;
    return static_cast<T>(x);
}

// A char holds a Unicode scalar value: at most 0x10FFFF, and not a surrogate.
template <class T>
inline char32_t ToChar(T value, const char* location) {
    if (std::in_range<uint32_t>(value)) {
        auto v = static_cast<uint32_t>(value);
        if (v <= 0x10FFFFu && (v < 0xD800u || v > 0xDFFFu)) return static_cast<char32_t>(v);
    }
    RaisePanic("integer is not a Unicode scalar value", location);
}

// Rounds to nearest like IEEE, including overflow to infinity; a plain C++ cast is UB out of range.
inline float DoubleToFloat(double x) {
    constexpr double FLOAT_OVERFLOW = static_cast<double>(FLT_MAX) + 0x1p103;
    if (std::isnan(x)) return std::numeric_limits<float>::quiet_NaN();
    if (x >= FLOAT_OVERFLOW) return std::numeric_limits<float>::infinity();
    if (x <= -FLOAT_OVERFLOW) return -std::numeric_limits<float>::infinity();
    if (x > FLT_MAX) return FLT_MAX;
    if (x < -FLT_MAX) return -FLT_MAX;
    return static_cast<float>(x);
}

}  // namespace wyl
