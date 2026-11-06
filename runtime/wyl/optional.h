#pragma once

#include "object.h"

namespace wyl {

// A nullable value type: the value, if any, and whether there is one. Copying, moving, and
// destroying it are trivial when they are for T, so `int?` is as cheap as an int and a bool.
template <class T>
class Optional {
    union {
        char m_Empty;
        T m_Value;
    };
    // Bit 1: it holds a value. Bit 2: something refers into that value, so the value is in use
    // (spec memory.holds).
    uint8_t m_State = 0;

    static constexpr uint8_t HAS_VALUE = 1;
    static constexpr uint8_t IS_IN_USE = 2;

    template <class>
    friend class Optional;

    bool Holds() const { return (m_State & HAS_VALUE) != 0; }

    void Reset() noexcept {
        if (Holds()) {
            if (GetInUse())
                RaisePanic("cannot release a value that is in use", "<runtime>");
            m_State &= uint8_t(~HAS_VALUE);
            m_Value.~T();
        }
    }

public:
    Optional() noexcept : m_Empty() {}
    Optional(std::nullptr_t) noexcept : m_Empty() {}
    Optional(const T& value) : m_Value(value), m_State(HAS_VALUE) {}
    Optional(T&& value) : m_Value(std::move(value)), m_State(HAS_VALUE) {}

    // Between nullables of numbers whose conversion Wylow makes implicit, such as `int?` to `long?`.
    template <class U>
    explicit Optional(const Optional<U>& other) : m_Empty() {
        if (other.Holds()) {
            ::new (static_cast<void*>(&m_Value)) T(static_cast<T>(other.m_Value));
            m_State = HAS_VALUE;
        }
    }

    Optional(const Optional&) requires std::is_trivially_copy_constructible_v<T> = default;
    Optional(const Optional& other) requires(!std::is_trivially_copy_constructible_v<T>) : m_Empty() {
        if (other.Holds()) {
            ::new (static_cast<void*>(&m_Value)) T(other.m_Value);
            m_State = other.m_State;
        }
    }

    Optional(Optional&&) requires std::is_trivially_move_constructible_v<T> = default;
    Optional(Optional&& other) noexcept requires(!std::is_trivially_move_constructible_v<T>) : m_Empty() {
        if (other.Holds()) {
            ::new (static_cast<void*>(&m_Value)) T(std::move(other.m_Value));
            m_State = other.m_State;
            other.m_State = 0;
        }
    }

    ~Optional() requires std::is_trivially_destructible_v<T> = default;
    ~Optional() requires(!std::is_trivially_destructible_v<T>) { Reset(); }

    // Every part of the new value is stored before any part of the old one is released, which
    // happens at the store, as for a copy: the old value dies with `taken`, not when `other` does.
    Optional& operator=(Optional&&) requires std::is_trivially_move_assignable_v<T> = default;
    Optional& operator=(Optional&& other) noexcept requires(!std::is_trivially_move_assignable_v<T>) {
        Optional taken(std::move(other));
        if (Holds() && taken.Holds()) {
            using std::swap;
            swap(m_Value, taken.m_Value);
        } else if (taken.Holds()) {
            ::new (static_cast<void*>(&m_Value)) T(std::move(taken.m_Value));
            m_State = taken.m_State;
            taken.m_State = 0;
        } else if (Holds()) {
            ::new (static_cast<void*>(&taken.m_Value)) T(std::move(m_Value));
            taken.m_State = m_State;
            Reset();
        }
        return *this;
    }

    Optional& operator=(const Optional&) requires std::is_trivially_copy_assignable_v<T> = default;
    Optional& operator=(const Optional& other) requires(!std::is_trivially_copy_assignable_v<T>) {
        Optional copy(other);
        *this = std::move(copy);
        return *this;
    }

    bool HasValue() const { return Holds(); }

    bool GetInUse() const { return (m_State & IS_IN_USE) != 0; }
    void SetInUse(bool on) { m_State = on ? uint8_t(m_State | IS_IN_USE) : uint8_t(m_State & ~IS_IN_USE); }
    bool AnyInUse() const { return GetInUse(); }

    // Flow analysis proved there is a value.
    T& Assumed() {
        if constexpr (WYL_DEBUG != 0) {
            if (!Holds()) RaisePanic("internal compiler error: null value used as narrowed", "<runtime>");
        }
        return m_Value;
    }
    const T& Assumed() const { return const_cast<Optional*>(this)->Assumed(); }

    friend bool operator==(const Optional& a, const Optional& b)
        requires requires(const T& x) { x == x; }
    {
        return a.Holds() == b.Holds() && (!a.Holds() || a.m_Value == b.m_Value);
    }
};

// A nullable function needs no flag: its null code pointer is the Optional's null.
template <class T>
    requires requires(const T& value) { value.IsNull(); }
class Optional<T> {
    T m_Value;

public:
    Optional() noexcept = default;
    Optional(std::nullptr_t) noexcept {}
    Optional(const T& value) : m_Value(value) {}
    Optional(T&& value) : m_Value(std::move(value)) {}

    bool HasValue() const { return !m_Value.IsNull(); }

    // Flow analysis proved there is a value.
    T& Assumed() {
        if constexpr (WYL_DEBUG != 0) {
            if (m_Value.IsNull()) RaisePanic("internal compiler error: null value used as narrowed", "<runtime>");
        }
        return m_Value;
    }
    const T& Assumed() const { return const_cast<Optional*>(this)->Assumed(); }
};

// `T?` in generic code, for whatever T stands for: a nullable reference is a Ref, and a nullable
// of a nullable is the same type.
template <class T>
struct NullableOf {
    using type = Optional<T>;
};
template <class T>
struct NullableOf<Ref<T>> {
    using type = Ref<T>;
};
template <class T>
struct NullableOf<Optional<T>> {
    using type = Optional<T>;
};
template <class T>
using Nullable = typename NullableOf<T>::type;

template <class T>
inline bool HasValue(const Ref<T>& value) {
    return static_cast<bool>(value);
}

template <class T>
inline bool HasValue(const Optional<T>& value) {
    return value.HasValue();
}

// The value of a nullable that flow analysis proved is not null; a Ref is its own value.
template <class T>
inline Ref<T>& AssumeValue(Ref<T>& value) {
    return value;
}

template <class T>
inline const Ref<T>& AssumeValue(const Ref<T>& value) {
    return value;
}

template <class T>
inline T& AssumeValue(Optional<T>& value) {
    return value.Assumed();
}

template <class T>
inline const T& AssumeValue(const Optional<T>& value) {
    return value.Assumed();
}

// A copy: a reference into the nullable could outlive its value.
template <class T>
inline T Unwrap(const Optional<T>& value, const char* location) {
    if (!value.HasValue()) RaisePanic("null value unwrapped with '!'", location);
    return value.Assumed();
}

// Checks printed apart from the value they guard, like CheckIndex.
template <class T>
inline void CheckNotNull(const Ref<T>& value, const char* location) {
    if (!value) RaisePanic("null value unwrapped with '!'", location);
}

template <class T>
inline void CheckHasValue(const Optional<T>& value, const char* location) {
    if (!value.HasValue()) RaisePanic("null value unwrapped with '!'", location);
}

// Stores every part of the new value before releasing any part of the old one: a memberwise copy
// could run a destructor between two fields, and that destructor may read or free the target.
template <class T>
inline void Assign(T& target, T value) {
    if constexpr (std::is_trivially_copyable_v<T>) {
        target = value;
    } else {
        using std::swap;
        swap(target, value);
    }
}

}  // namespace wyl
