#pragma once

#include "object.h"

namespace vpl {

// compareTo() of an integer or char.
template <class T>
    requires std::is_integral_v<T>
inline int32_t compare(T value, T other) {
    return value < other ? -1 : value > other ? 1 : 0;
}

// Comparable.compareTo through a generic bound I: the integers, char, and String satisfy the bound
// without implementing it. m_compareTo is the name CppEmitter gives the method.
template <class I, class T>
inline int32_t compare_to(const T& value, const T& other) {
    if constexpr (std::is_integral_v<T>) {
        return compare(value, other);
    } else if constexpr (std::is_same_v<T, String>) {
        return value.compareTo(other);
    } else {
        return bound<I>(value).m_compareTo(other);
    }
}

}  // namespace vpl
