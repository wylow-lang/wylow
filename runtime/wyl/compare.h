#pragma once

#include "arithmetic.h"
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

    T wrappingAdd(const T& other) const
        requires std::is_integral_v<T>
    {
        return static_cast<T>(static_cast<Wide<T>>(Value) + static_cast<Wide<T>>(other));
    }

    T wrappingSub(const T& other) const
        requires std::is_integral_v<T>
    {
        return static_cast<T>(static_cast<Wide<T>>(Value) - static_cast<Wide<T>>(other));
    }

    T wrappingMul(const T& other) const
        requires std::is_integral_v<T>
    {
        return static_cast<T>(static_cast<Wide<T>>(Value) * static_cast<Wide<T>>(other));
    }

    Optional<T> checkedAdd(const T& other) const
        requires std::is_integral_v<T>
    {
        T sum = wrappingAdd(other);
        bool overflows;
        if constexpr (std::is_signed_v<T>) {
            overflows = ((Value ^ sum) & (other ^ sum)) < 0;
        } else {
            overflows = sum < Value;
        }
        return overflows ? Optional<T>() : Optional<T>(sum);
    }

    Optional<T> checkedSub(const T& other) const
        requires std::is_integral_v<T>
    {
        T difference = wrappingSub(other);
        bool overflows;
        if constexpr (std::is_signed_v<T>) {
            overflows = ((Value ^ other) & (Value ^ difference)) < 0;
        } else {
            overflows = Value < other;
        }
        return overflows ? Optional<T>() : Optional<T>(difference);
    }

    Optional<T> checkedMul(const T& other) const
        requires std::is_integral_v<T>
    {
        return MulOverflows(Value, other) ? Optional<T>() : Optional<T>(wrappingMul(other));
    }

    int32_t m_compareTo(const T& other) const {
        return compareTo(other);
    }

    String m_toString() const {
        return toString();
    }
};

}  // namespace wyl
