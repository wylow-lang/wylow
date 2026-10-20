// VPL runtime support for generated C++. Requires C++20, which defines signed overflow on
// conversion and shifts as two's complement; everything here relies on that.
#pragma once

#include <cfloat>
#include <charconv>
#include <cinttypes>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <new>
#include <type_traits>
#include <utility>

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

// Exhaustiveness checking rules this out too.
[[noreturn]] inline void no_match(const char* location) {
    panic("internal compiler error: no switch case matched", location);
}

// Selects the constructor that sets an enum's tag and leaves its payload to be constructed.
struct RawTag {};

inline void init() {
#if defined(_WIN32) && defined(_WRITE_ABORT_MSG)
    // Abort quietly instead of showing a dialog or invoking Windows Error Reporting.
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
#endif
}

// The VPL default value of T, for generic code: struct field initializers run via make_default.
template <class T>
inline T default_value() {
    if constexpr (requires { T::make_default(); }) {
        return T::make_default();
    } else {
        return T{};
    }
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

// ---- Objects ----
//
// Class objects derive virtually from Object, so every object has exactly one reference count
// however many interfaces it implements. The count is non-atomic: objects are thread-confined.

class Object;

// A weak reference, linked into its target's list so the target can clear it when it dies.
struct WeakLink {
    WeakLink* prev = nullptr;
    WeakLink* next = nullptr;
    Object* target = nullptr;
};

template <class T>
class Weak;

class Object {
    // Starts at 1 for the `new` expression that creates the object, so a constructor can hand out
    // and drop references to `this` without the count reaching 0.
    uintptr_t refs = 1;
    WeakLink* weak = nullptr;

    template <class>
    friend class Weak;

    void clear_weak() {
        while (weak != nullptr) {
            WeakLink* link = weak;
            weak = link->next;
            link->prev = link->next = nullptr;
            link->target = nullptr;
        }
    }

public:
    Object() = default;
    Object(const Object&) = delete;
    Object& operator=(const Object&) = delete;

    virtual ~Object() {
        // Weak references taken by the destructor itself would otherwise dangle.
        clear_weak();
        if (refs != 1) panic("an object's destructor kept a reference to the object", "<destructor>");
    }

    void retain() { ++refs; }

    void release() {
        if (--refs == 0) {
            // Holding the count at 1 lets the destructor create and drop temporary references to
            // `this` without destroying it twice; ~Object checks that none outlive it.
            refs = 1;
            clear_weak();
            delete this;
        }
    }
};

template <class T>
class Ref {
    T* ptr = nullptr;

    template <class>
    friend class Ref;

    static void retain(T* p) {
        if (p != nullptr) static_cast<Object*>(p)->retain();
    }
    static void release(T* p) {
        if (p != nullptr) static_cast<Object*>(p)->release();
    }

public:
    Ref() = default;
    Ref(std::nullptr_t) {}
    explicit Ref(T* p) : ptr(p) { retain(p); }
    Ref(const Ref& other) : ptr(other.ptr) { retain(ptr); }
    Ref(Ref&& other) noexcept : ptr(other.ptr) { other.ptr = nullptr; }

    template <class U>
        requires std::is_convertible_v<U*, T*>
    Ref(const Ref<U>& other) : ptr(other.ptr) {
        retain(ptr);
    }

    template <class U>
        requires std::is_convertible_v<U*, T*>
    Ref(Ref<U>&& other) noexcept : ptr(other.ptr) {
        other.ptr = nullptr;
    }

    ~Ref() { release(ptr); }

    // Takes over the reference that `new` starts every object with.
    static Ref adopt(T* p) {
        Ref result;
        result.ptr = p;
        return result;
    }

    // The new value is stored before the old one is released, since the old object's destructor
    // may read this reference. Nothing touches `this` afterwards: that destructor may free it.
    void operator=(const Ref& other) {
        T* old = ptr;
        retain(other.ptr);
        ptr = other.ptr;
        release(old);
    }

    // Swapping releases nothing; the old value dies with `other`, after the store.
    Ref& operator=(Ref&& other) noexcept {
        std::swap(ptr, other.ptr);
        return *this;
    }

    T* operator->() const {
        if constexpr (VPL_DEBUG != 0) {
            if (ptr == nullptr) panic("internal compiler error: null dereference", "<runtime>");
        }
        return ptr;
    }

    T* get() const { return ptr; }
    explicit operator bool() const { return ptr != nullptr; }

    friend bool operator==(const Ref& a, const Ref& b) {
        return static_cast<const Object*>(a.ptr) == static_cast<const Object*>(b.ptr);
    }
};

template <class T>
class Weak : WeakLink {
    T* ptr = nullptr;

    void unlink() {
        if (target == nullptr) return;
        if (prev != nullptr) {
            prev->next = next;
        } else {
            target->weak = next;
        }
        if (next != nullptr) next->prev = prev;
        prev = next = nullptr;
        target = nullptr;
    }

public:
    Weak() = default;
    Weak(const Weak&) = delete;
    Weak& operator=(const Weak&) = delete;
    ~Weak() { unlink(); }

    void operator=(Ref<T> value) {
        unlink();
        ptr = value.get();
        if (ptr == nullptr) return;
        target = ptr;
        next = target->weak;
        if (next != nullptr) next->prev = this;
        target->weak = this;
    }

    Ref<T> get() const { return target != nullptr ? Ref<T>(ptr) : Ref<T>(); }
};

template <class T>
struct is_ref : std::false_type {};
template <class T>
struct is_ref<Ref<T>> : std::true_type {};

template <class T>
inline Ref<T> adopt(T* object) {
    return Ref<T>::adopt(object);
}

template <class T>
inline const Object* identity(const Ref<T>& value) {
    return value.get();
}
inline const Object* identity(std::nullptr_t) { return nullptr; }

// A strong copy that keeps an object alive while a method runs on it or a reference into it is used.
template <class T>
inline Ref<T> pin(const Ref<T>& value) {
    return value;
}

// In generic code a value may be a struct, which lives in its place and needs no pin.
template <class T>
    requires(!is_ref<std::remove_cv_t<T>>::value)
inline T& pin(T& value) {
    return value;
}

// Passes a by-value argument as a copy of a caller-owned temporary. C++ ABIs differ on whether a
// parameter dies when the callee returns or at the end of the caller's statement; the temporary
// always lives to the end of the statement, so objects are destroyed at the same point everywhere.
template <class T>
inline const T& keep(const T& value) {
    return value;
}

// Calls go through the class that declares the method, so C++ name hiding and ambiguity between
// bases cannot pick a different overload than VPL did.
template <class B, class T>
inline B* as(const Ref<T>& value) {
    return value.operator->();
}

// A call through a generic bound: objects through the bound interface, structs directly.
template <class I, class T>
inline decltype(auto) bound(T&& value) {
    if constexpr (is_ref<std::remove_cvref_t<T>>::value) {
        return static_cast<I&>(*value.operator->());
    } else {
        return (value);
    }
}

template <class T, class U>
inline bool is(const Ref<U>& value) {
    return dynamic_cast<T*>(value.get()) != nullptr;
}

template <class T, class U>
inline bool is(const Ref<U>& value, Ref<T>& out) {
    T* p = dynamic_cast<T*>(value.get());
    if (p == nullptr) return false;
    out = Ref<T>(p);
    return true;
}

template <class T, class U>
inline Ref<T> downcast(const Ref<U>& value) {
    return Ref<T>(dynamic_cast<T*>(value.get()));
}

template <class T>
inline Ref<T> unwrap(Ref<T> value, const char* location) {
    if (!value) panic("null value unwrapped with '!'", location);
    return value;
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
