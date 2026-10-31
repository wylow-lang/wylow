#pragma once

#include "object.h"

namespace wyl {

// compareTo() of an integer or char.
template <class T>
    requires std::is_integral_v<T>
inline int32_t compare(T value, T other) {
    return value < other ? -1 : value > other ? 1 : 0;
}

// compareTo() of a float or double: a total order, as Java's. -0 sorts before +0, and NaN after
// everything else and the same as any other NaN.
template <class F>
    requires std::is_floating_point_v<F>
inline int32_t compare(F value, F other) {
    if (value < other) return -1;
    if (value > other) return 1;
    bool valueIsNaN = std::isnan(value);
    bool otherIsNaN = std::isnan(other);
    if (valueIsNaN || otherIsNaN) return valueIsNaN == otherIsNaN ? 0 : valueIsNaN ? 1 : -1;
    // Equal numbers that aren't NaN differ only in the sign of a zero.
    bool valueIsNegative = std::signbit(value);
    return valueIsNegative == std::signbit(other) ? 0 : valueIsNegative ? -1 : 1;
}

// Comparable.compareTo through a generic bound I: the numbers, char, and String satisfy the bound
// without implementing it. m_compareTo is the name CppEmitter gives the method.
template <class I, class T>
inline int32_t compare_to(const T& value, const T& other) {
    if constexpr (std::is_integral_v<T> || std::is_floating_point_v<T>) {
        return compare(value, other);
    } else if constexpr (std::is_same_v<T, String>) {
        return value.compareTo(other);
    } else {
        return bound<I>(value).m_compareTo(other);
    }
}

}  // namespace wyl
