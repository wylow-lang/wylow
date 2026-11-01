#pragma once

#include "object.h"

namespace wyl {

// A nullable value type: the value, if any, and whether there is one. Copying, moving, and
// destroying it are trivial when they are for T, so `int?` is as cheap as an int and a bool.
template <class T>
class Optional {
    union {
        char empty_;
        T value_;
    };
    bool has_ = false;

    template <class>
    friend class Optional;

    void reset() noexcept {
        if (has_) {
            has_ = false;
            value_.~T();
        }
    }

public:
    Optional() noexcept : empty_() {}
    Optional(std::nullptr_t) noexcept : empty_() {}
    Optional(const T& value) : value_(value), has_(true) {}
    Optional(T&& value) : value_(std::move(value)), has_(true) {}

    // Between nullables of numbers whose conversion Wylow makes implicit, such as `int?` to `long?`.
    template <class U>
    explicit Optional(const Optional<U>& other) : empty_() {
        if (other.has_) {
            ::new (static_cast<void*>(&value_)) T(static_cast<T>(other.value_));
            has_ = true;
        }
    }

    Optional(const Optional&) requires std::is_trivially_copy_constructible_v<T> = default;
    Optional(const Optional& other) requires(!std::is_trivially_copy_constructible_v<T>) : empty_() {
        if (other.has_) {
            ::new (static_cast<void*>(&value_)) T(other.value_);
            has_ = true;
        }
    }

    Optional(Optional&&) requires std::is_trivially_move_constructible_v<T> = default;
    Optional(Optional&& other) noexcept requires(!std::is_trivially_move_constructible_v<T>) : empty_() {
        if (other.has_) {
            ::new (static_cast<void*>(&value_)) T(std::move(other.value_));
            has_ = true;
        }
    }

    ~Optional() requires std::is_trivially_destructible_v<T> = default;
    ~Optional() requires(!std::is_trivially_destructible_v<T>) { reset(); }

    // Every part of the new value is stored before any part of the old one is released, which
    // happens at the store, as for a copy: the old value dies with `taken`, not when `other` does.
    Optional& operator=(Optional&&) requires std::is_trivially_move_assignable_v<T> = default;
    Optional& operator=(Optional&& other) noexcept requires(!std::is_trivially_move_assignable_v<T>) {
        Optional taken(std::move(other));
        if (has_ && taken.has_) {
            using std::swap;
            swap(value_, taken.value_);
        } else if (taken.has_) {
            ::new (static_cast<void*>(&value_)) T(std::move(taken.value_));
            has_ = true;
            taken.reset();
        } else if (has_) {
            ::new (static_cast<void*>(&taken.value_)) T(std::move(value_));
            taken.has_ = true;
            reset();
        }
        return *this;
    }

    Optional& operator=(const Optional&) requires std::is_trivially_copy_assignable_v<T> = default;
    Optional& operator=(const Optional& other) requires(!std::is_trivially_copy_assignable_v<T>) {
        Optional copy(other);
        *this = std::move(copy);
        return *this;
    }

    bool has_value() const { return has_; }

    // Flow analysis proved there is a value.
    T& assumed() {
        if constexpr (WYL_DEBUG != 0) {
            if (!has_) raise_panic("internal compiler error: null value used as narrowed", "<runtime>");
        }
        return value_;
    }
    const T& assumed() const { return const_cast<Optional*>(this)->assumed(); }

    friend bool operator==(const Optional& a, const Optional& b)
        requires requires(const T& x) { x == x; }
    {
        return a.has_ == b.has_ && (!a.has_ || a.value_ == b.value_);
    }
};

// A nullable function needs no flag: its null code pointer is the Optional's null.
template <class T>
    requires requires(const T& value) { value.wyl_is_null(); }
class Optional<T> {
    T value_;

public:
    Optional() noexcept = default;
    Optional(std::nullptr_t) noexcept {}
    Optional(const T& value) : value_(value) {}
    Optional(T&& value) : value_(std::move(value)) {}

    bool has_value() const { return !value_.wyl_is_null(); }

    // Flow analysis proved there is a value.
    T& assumed() {
        if constexpr (WYL_DEBUG != 0) {
            if (value_.wyl_is_null()) raise_panic("internal compiler error: null value used as narrowed", "<runtime>");
        }
        return value_;
    }
    const T& assumed() const { return const_cast<Optional*>(this)->assumed(); }
};

// `T?` in generic code, for whatever T stands for: a nullable reference is a Ref, and a nullable
// of a nullable is the same type.
template <class T>
struct nullable_of {
    using type = Optional<T>;
};
template <class T>
struct nullable_of<Ref<T>> {
    using type = Ref<T>;
};
template <class T>
struct nullable_of<Optional<T>> {
    using type = Optional<T>;
};
template <class T>
using Nullable = typename nullable_of<T>::type;

template <class T>
inline bool has_value(const Ref<T>& value) {
    return static_cast<bool>(value);
}

template <class T>
inline bool has_value(const Optional<T>& value) {
    return value.has_value();
}

// The value of a nullable that flow analysis proved is not null; a Ref is its own value.
template <class T>
inline Ref<T>& assume_value(Ref<T>& value) {
    return value;
}

template <class T>
inline const Ref<T>& assume_value(const Ref<T>& value) {
    return value;
}

template <class T>
inline T& assume_value(Optional<T>& value) {
    return value.assumed();
}

template <class T>
inline const T& assume_value(const Optional<T>& value) {
    return value.assumed();
}

// A copy: a reference into the nullable could outlive its value.
template <class T>
inline T unwrap(const Optional<T>& value, const char* location) {
    if (!value.has_value()) raise_panic("null value unwrapped with '!'", location);
    return value.assumed();
}

// Checks printed apart from the value they guard, like check_index.
template <class T>
inline void check_not_null(const Ref<T>& value, const char* location) {
    if (!value) raise_panic("null value unwrapped with '!'", location);
}

template <class T>
inline void check_has_value(const Optional<T>& value, const char* location) {
    if (!value.has_value()) raise_panic("null value unwrapped with '!'", location);
}

// Stores every part of the new value before releasing any part of the old one: a memberwise copy
// could run a destructor between two fields, and that destructor may read or free the target.
template <class T>
inline void assign(T& target, T value) {
    if constexpr (std::is_trivially_copyable_v<T>) {
        target = value;
    } else {
        using std::swap;
        swap(target, value);
    }
}

}  // namespace wyl
