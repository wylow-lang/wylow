#pragma once

#include "strings.h"

namespace wyl {

// compareTo() of an integer or char.
template <class T>
    requires std::is_integral_v<T>
inline int32_t Compare(T value, T other) {
    return value < other ? -1 : value > other ? 1 : 0;
}

// compareTo() of a float or double: a total order. -0 sorts before +0, and NaN after everything
// else and the same as any other NaN.
template <class F>
    requires std::is_floating_point_v<F>
inline int32_t Compare(F value, F other) {
    if (value < other) return -1;
    if (value > other) return 1;
    bool valueIsNaN = std::isnan(value);
    bool otherIsNaN = std::isnan(other);
    if (valueIsNaN || otherIsNaN) return valueIsNaN == otherIsNaN ? 0 : valueIsNaN ? 1 : -1;
    // Equal numbers that aren't NaN differ only in the sign of a zero.
    bool valueIsNegative = std::signbit(value);
    return valueIsNegative == std::signbit(other) ? 0 : valueIsNegative ? -1 : 1;
}

// The extern members of `extern struct int` and the other primitive types (Stdlib/primitives.wyl),
// and String's through a generic bound. A direct call uses the declared name; a call through a
// bound uses the m_ name CppEmitter gives an interface method.
template <class T>
struct Builtin {
    const T& Value;

    explicit Builtin(const T& initial) : Value(initial) {}

    int32_t compareTo(const T& other) const {
        if constexpr (std::is_same_v<T, String>) {
            return Value.compareTo(other);
        } else {
            return Compare(Value, other);
        }
    }

    String toString() const {
        return FormatString(Value);
    }

    int32_t m_compareTo(const T& other) const {
        return compareTo(other);
    }

    String m_toString() const {
        return toString();
    }
};

}  // namespace wyl
