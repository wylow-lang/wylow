// VPL runtime support for generated C++. Requires C++20, which defines signed overflow on
// conversion and shifts as two's complement; everything here relies on that.
#pragma once

#include <array>
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
#include <memory>
#include <new>
#include <tuple>
#include <type_traits>
#include <utility>
#include <vector>

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

// Reported in debug builds, when the program works but could be faster.
inline void performance_warning(const char* message) {
    std::fflush(stdout);
    std::fprintf(stderr, "performance warning: %s\n", message);
    std::fflush(stderr);
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

template <class T>
struct is_std_array : std::false_type {};
template <class T, size_t N>
struct is_std_array<std::array<T, N>> : std::true_type {};

// The VPL default value of T, for generic code: struct field initializers run via make_default.
template <class T>
inline T default_value() {
    if constexpr (requires { T::make_default(); }) {
        return T::make_default();
    } else if constexpr (is_std_array<T>::value) {
        T result{};
        for (auto& element : result) element = default_value<typename T::value_type>();
        return result;
    } else {
        return T{};
    }
}

[[noreturn]] inline void panic_index(int64_t index, int64_t length, const char* location) {
    char message[96];
    std::snprintf(message, sizeof message, "index %" PRId64 " is out of bounds for length %" PRId64, index, length);
    panic(message, location);
}

// Fixed array indexing. The index may be given as a function, called after the array is found,
// so that the element stays a place while VPL's left-to-right order is kept.
template <class A, class I>
inline decltype(auto) at(A&& array, I&& index, const char* location) {
    int32_t i;
    if constexpr (std::is_invocable_v<I>) {
        i = index();
    } else {
        i = index;
    }
    constexpr size_t length = std::tuple_size_v<std::remove_cvref_t<A>>;
    if (static_cast<uint32_t>(i) >= length) panic_index(i, static_cast<int64_t>(length), location);
    return array[static_cast<size_t>(i)];
}

template <class T, size_t N>
constexpr int32_t length(const std::array<T, N>&) {
    return static_cast<int32_t>(N);
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

// Shared by an object and the weak references to it, so that they can tell when it is gone. The
// object holds one count and each weak reference one; the last to let go frees the block.
struct WeakBlock {
    Object* target;
    uintptr_t count;
    // The object's buffer slot, which the block takes over from the object's header.
    uint32_t slot;
};

template <class R>
class Weak;

// Called by the cycle collector for each strong reference an object holds, through the generated
// vpl_visit; returning true clears the reference.
class Visitor {
public:
    virtual bool visit(Object* target) = 0;

protected:
    ~Visitor() = default;
};

class CycleCollector;

// Whether objects of a generic class instantiation may be part of a cycle. The compiler specializes it
// to false for the instantiations it knows are acyclic; others only C++ sees are assumed cyclic.
template <class T>
inline constexpr bool is_cyclic = true;

// The VPL name of a generic class instantiation, for diagnostics; the compiler specializes it for the
// instantiations it knows of.
template <class T>
inline constexpr const char* class_name = nullptr;

class Object {
    // The flags below in the low bits and the reference count above them, so that the header is
    // the vtable pointer and two words. The count starts at 1 for the `new` expression that creates
    // the object, so a constructor can hand out and drop references to `this` without it reaching 0.
    static constexpr int flag_bits = 7;
    static constexpr uint64_t one_ref = uint64_t{1} << flag_bits;
    uint64_t state = one_ref;
    // The weak block once the object has one; otherwise, while buffered, one more than the
    // object's index in the cycle buffer, so that it can leave the buffer when destroyed.
    union {
        WeakBlock* weak;
        uint32_t slot;
    } side{nullptr};

    // Set once the count has reached zero, or once the collector has found the object to be
    // garbage: weak references to the object read null from then on.
    static constexpr uint64_t dying = 1;
    // The object's class may be part of a cycle; set by its constructor.
    static constexpr uint64_t cyclic = 2;
    static constexpr uint64_t buffered = 4;
    // Trial deletion's marks; an object with neither is black.
    static constexpr uint64_t gray = 8;
    static constexpr uint64_t white = 16;
    // The collector has run the object's destructors, which its C++ destructor then skips.
    static constexpr uint64_t finalized = 32;
    // `side` holds the weak block, which holds the buffer slot.
    static constexpr uint64_t has_weak = 64;
    static constexpr uint64_t flag_mask = one_ref - 1;

    template <class>
    friend class Weak;
    friend class CycleCollector;

    void destroy();
    void add_to_buffer();

    uint64_t refs() const { return state >> flag_bits; }
    // Only for the collector, which balances every one of these.
    void unretain() { state -= one_ref; }

    uint32_t slot() const { return (state & has_weak) ? side.weak->slot : side.slot; }
    void set_slot(uint32_t value) {
        if (state & has_weak) {
            side.weak->slot = value;
        } else {
            side.slot = value;
        }
    }

    // Null for a dying object, so that weak references taken by its destructor read null too.
    WeakBlock* weak_block() {
        if (state & dying) return nullptr;
        if (!(state & has_weak)) {
            side.weak = new WeakBlock{this, 1, (state & buffered) ? side.slot : 0};
            state |= has_weak;
        }
        return side.weak;
    }

    // Leaves the slot behind in the header, for an object still to leave the buffer.
    void clear_weak() {
        state |= dying;
        if (!(state & has_weak)) return;
        WeakBlock* weak = side.weak;
        side.slot = weak->slot;
        state &= ~has_weak;
        weak->target = nullptr;
        if (--weak->count == 0) delete weak;
    }

public:
    Object() = default;
    Object(const Object&) = delete;
    Object& operator=(const Object&) = delete;

    virtual ~Object() {
        if (refs() != 1) panic("an object's destructor kept a reference to the object", "<destructor>");
    }

    // Called around every borrow of the object: for a class implementing the standard library's
    // Borrowable, the generated override calls its beginBorrow and endBorrow.
    virtual void vpl_begin_borrow() {}
    virtual void vpl_end_borrow() {}

    // Generated for classes: visits each strong reference the object holds, for the collector.
    virtual void vpl_visit(Visitor&) {}

    // Generated for classes with destructors: runs the object's destructors without releasing its
    // fields, for the collector.
    virtual void vpl_finalize() {}

    // Generated for classes, for the collector's diagnostics.
    virtual const char* vpl_class_name() const { return "object"; }

    void vpl_set_cyclic(bool value) { state = value ? state | cyclic : state & ~cyclic; }
    bool vpl_finalized() const { return (state & finalized) != 0; }

    // 57 bits of count cannot overflow: the references alone would fill more than any address space.
    void retain() { state += one_ref; }

    // A release that leaves a cyclic object's count above zero may have left it in an unreachable
    // cycle, so it is buffered for the collector, once until the next collection.
    void release() {
        state -= one_ref;
        if (state < one_ref) {
            destroy();
        } else if ((state & (flag_mask & ~has_weak)) == cyclic) {
            add_to_buffer();
        }
    }
};

static_assert(sizeof(void*) != 8 || sizeof(Object) == 24);

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

// The standard library's Weak<T>, written Weak<Ref<T>> in C++ like any type argument. It keeps the
// target as a T* too, since a pointer to the virtual base Object cannot be cast back down.
template <class T>
class Weak<Ref<T>> {
    WeakBlock* block = nullptr;
    T* ptr = nullptr;

public:
    Weak() = default;

    template <class U>
        requires std::is_convertible_v<U*, T*>
    explicit Weak(const Ref<U>& target) : ptr(target.get()) {
        if (ptr == nullptr) return;
        block = static_cast<Object*>(ptr)->weak_block();
        if (block != nullptr) ++block->count;
    }

    Weak(const Weak& other) : block(other.block), ptr(other.ptr) {
        if (block != nullptr) ++block->count;
    }

    Weak(Weak&& other) noexcept : block(other.block), ptr(other.ptr) {
        other.block = nullptr;
        other.ptr = nullptr;
    }

    Weak& operator=(const Weak& other) {
        Weak copy(other);
        std::swap(block, copy.block);
        std::swap(ptr, copy.ptr);
        return *this;
    }

    Weak& operator=(Weak&& other) noexcept {
        std::swap(block, other.block);
        std::swap(ptr, other.ptr);
        return *this;
    }

    ~Weak() {
        if (block != nullptr && --block->count == 0) delete block;
    }

    Ref<T> get() const { return block != nullptr && block->target != nullptr ? Ref<T>(ptr) : Ref<T>(); }
};

template <class T>
struct is_ref : std::false_type {};
template <class T>
struct is_ref<Ref<T>> : std::true_type {};

template <class Signature>
class Function;

template <class T>
struct is_function_value : std::false_type {};
template <class S>
struct is_function_value<Function<S>> : std::true_type {};

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

// Holds an object and borrows it, for as long as a reference or span into its storage is used.
template <class T>
class Borrow {
    Ref<T> ref;

public:
    explicit Borrow(Ref<T> value) : ref(std::move(value)) { static_cast<Object*>(ref.get())->vpl_begin_borrow(); }
    Borrow(const Borrow&) = delete;
    Borrow& operator=(const Borrow&) = delete;
    ~Borrow() { static_cast<Object*>(ref.get())->vpl_end_borrow(); }

    T* operator->() const { return ref.operator->(); }
};

template <class T>
inline Borrow<T> borrow(Ref<T> value) {
    return Borrow<T>(std::move(value));
}

// Calls f with value, so that value is evaluated before anything f evaluates.
template <class T, class F>
inline decltype(auto) with(T&& value, F&& f) {
    return f(value);
}

// In generic code a value may be a struct, which lives in its place and needs no pin.
template <class T>
    requires(!is_ref<std::remove_cv_t<T>>::value && !is_function_value<std::remove_cv_t<T>>::value)
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

template <class B, class T>
inline B* as(const Borrow<T>& value) {
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

    // Between nullables of numbers whose conversion VPL makes implicit, such as `int?` to `long?`.
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

    // As for Ref, swapping releases nothing: the old value dies with `other`, after the store.
    Optional& operator=(Optional&&) requires std::is_trivially_move_assignable_v<T> = default;
    Optional& operator=(Optional&& other) noexcept requires(!std::is_trivially_move_assignable_v<T>) {
        if (has_ && other.has_) {
            using std::swap;
            swap(value_, other.value_);
        } else if (other.has_) {
            ::new (static_cast<void*>(&value_)) T(std::move(other.value_));
            has_ = true;
            other.reset();
        } else if (has_) {
            ::new (static_cast<void*>(&other.value_)) T(std::move(value_));
            other.has_ = true;
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
        if constexpr (VPL_DEBUG != 0) {
            if (!has_) panic("internal compiler error: null value used as narrowed", "<runtime>");
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
    requires requires(const T& value) { value.vpl_is_null(); }
class Optional<T> {
    T value_;

public:
    Optional() noexcept = default;
    Optional(std::nullptr_t) noexcept {}
    Optional(const T& value) : value_(value) {}
    Optional(T&& value) : value_(std::move(value)) {}

    bool has_value() const { return !value_.vpl_is_null(); }

    // Flow analysis proved there is a value.
    T& assumed() {
        if constexpr (VPL_DEBUG != 0) {
            if (value_.vpl_is_null()) panic("internal compiler error: null value used as narrowed", "<runtime>");
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
    if (!value.has_value()) panic("null value unwrapped with '!'", location);
    return value.assumed();
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

// ---- Cycle collector ----
//
// Synchronous trial deletion (Bacon and Rajan, "Concurrent Cycle Collection in Reference Counted
// Systems", 2001), run only when the program calls Memory.collectCycles. Traversals use explicit
// stacks, since a cycle may be a long chain. A reference vpl_visit misses only makes its target
// look referenced from outside, so the collector frees less, never something in use.

template <class T>
inline void visit(Visitor& visitor, Ref<T>& reference) {
    if (reference && visitor.visit(reference.get())) reference = nullptr;
}

template <class T>
inline void visit(Visitor& visitor, Optional<T>& value) {
    if (value.has_value()) visit(visitor, value.assumed());
}

// Fixed arrays, and structs and enums through their generated vpl_visit; other values hold no references.
template <class T>
inline void visit(Visitor& visitor, T& value) {
    if constexpr (is_std_array<T>::value) {
        for (auto& element : value) visit(visitor, element);
    } else if constexpr (requires { value.vpl_visit(visitor); }) {
        value.vpl_visit(visitor);
    }
}

template <class F>
class FunctionVisitor final : public Visitor {
    F function;

public:
    explicit FunctionVisitor(F f) : function(std::move(f)) {}
    bool visit(Object* target) override { return function(target); }
};

struct CycleStats {
    size_t buffered = 0;
    size_t visited = 0;
    size_t freed = 0;
};

class CycleCollector {
    // Objects whose count dropped without reaching zero since the last collection. An entry is
    // null where its object was destroyed meanwhile.
    std::vector<Object*> buffer;
    size_t holes = 0;
    // Destructors running, outside or inside a collection, which may not start one.
    int destroying = 0;
    CycleStats stats;

    // Working storage for a collection, kept between collections so that each one reuses it. The
    // roots trade storage with the buffer.
    std::vector<Object*> roots;
    std::vector<Object*> marked;
    std::vector<Object*> stack;
    std::vector<Object*> garbage;

    friend class Object;

    // Calls f on each cyclic object the object refers to. Objects of acyclic classes are never on a
    // cycle, so references to them are left out, and references they hold count as from outside.
    template <class F>
    static void for_each_child(Object* object, F f) {
        FunctionVisitor visitor([&](Object* target) {
            if (target->state & Object::cyclic) f(target);
            return false;
        });
        object->vpl_visit(visitor);
    }

    void add(Object* object) {
        if (buffer.size() == buffer.capacity() && holes * 2 >= buffer.size()) compact();
        if (buffer.size() >= UINT32_MAX) panic("too many objects are waiting for Memory.collectCycles", "<runtime>");
        buffer.push_back(object);
        object->state |= Object::buffered;
        object->set_slot(static_cast<uint32_t>(buffer.size()));
    }

    void remove(Object* object) {
        buffer[object->slot() - 1] = nullptr;
        ++holes;
        object->set_slot(0);
        object->state &= ~Object::buffered;
    }

    void compact() {
        size_t kept = 0;
        for (Object* object : buffer) {
            if (object == nullptr) continue;
            buffer[kept++] = object;
            object->set_slot(static_cast<uint32_t>(kept));
        }
        buffer.resize(kept);
        holes = 0;
    }

    // Subtracts the references among the objects reachable from root, marking them gray, breadth
    // first so that `marked` from `start` on is both the group and the queue. Fails, undoing every
    // mark and subtraction of the group, once more than `budget` objects are marked in all.
    bool mark_gray(Object* root, size_t budget) {
        if (root->state & Object::gray) return true;
        size_t start = marked.size();
        if (start >= budget) return false;
        root->state |= Object::gray;
        marked.push_back(root);
        size_t expanded = start;
        while (expanded < marked.size()) {
            for_each_child(marked[expanded++], [&](Object* child) {
                child->unretain();
                if (!(child->state & Object::gray)) {
                    child->state |= Object::gray;
                    marked.push_back(child);
                }
            });
            if (marked.size() > budget) {
                for (size_t i = start; i < expanded; ++i) for_each_child(marked[i], [](Object* child) { child->retain(); });
                for (size_t i = start; i < marked.size(); ++i) marked[i]->state &= ~Object::gray;
                marked.resize(start);
                return false;
            }
        }
        return true;
    }

    // Gray objects still referenced from outside the group are live, and so is everything they
    // reach; the rest turn white.
    void scan(Object* root) {
        stack.push_back(root);
        while (!stack.empty()) {
            Object* object = stack.back();
            stack.pop_back();
            if (!(object->state & Object::gray)) continue;
            if (object->refs() > 0) {
                scan_black(object);
            } else {
                object->state = (object->state & ~Object::gray) | Object::white;
                for_each_child(object, [&](Object* child) { stack.push_back(child); });
            }
        }
    }

    // Restores the references a live object and everything it reaches hold.
    void scan_black(Object* root) {
        size_t base = stack.size();
        root->state &= ~(Object::gray | Object::white);
        stack.push_back(root);
        while (stack.size() > base) {
            Object* object = stack.back();
            stack.pop_back();
            for_each_child(object, [&](Object* child) {
                child->retain();
                if (child->state & (Object::gray | Object::white)) {
                    child->state &= ~(Object::gray | Object::white);
                    stack.push_back(child);
                }
            });
        }
    }

    void collect_white(Object* root) {
        if (!(root->state & Object::white)) return;
        size_t next = garbage.size();
        root->state = (root->state & ~Object::white) | Object::dying;
        garbage.push_back(root);
        while (next < garbage.size()) {
            for_each_child(garbage[next++], [&](Object* child) {
                if (child->state & Object::white) {
                    child->state = (child->state & ~Object::white) | Object::dying;
                    garbage.push_back(child);
                }
            });
        }
    }

    // Trial deletion from the first `count` roots, leaving the garbage found in `garbage`.
    void find_garbage(size_t count) {
        for (size_t i = 0; i < count; ++i) {
            if (roots[i] != nullptr) scan(roots[i]);
        }
        for (size_t i = 0; i < count; ++i) {
            if (roots[i] != nullptr) collect_white(roots[i]);
        }
    }

    // ---- Debug builds' diagnostics ----

    static constexpr int growing_calls_to_warn = 5;
    static constexpr int collections_to_warn = 10;
    size_t last_waiting = 0;
    int growing_calls = 0;
    bool warned_growing = false;
    // For each class whose objects collections have freed, how many collections did; negative once
    // reported.
    std::vector<std::pair<const char*, int>> freed_classes;

    // A class's name, and how many objects of it.
    struct ClassCount {
        const char* name;
        unsigned long long count;
    };

    static std::vector<ClassCount> count_by_class(const std::vector<Object*>& objects) {
        std::vector<ClassCount> counts;
        for (Object* object : objects) {
            const char* name = object->vpl_class_name();
            bool found = false;
            for (auto& entry : counts) {
                if (std::strcmp(entry.name, name) == 0) {
                    ++entry.count;
                    found = true;
                    break;
                }
            }
            if (!found) counts.push_back({name, 1});
        }
        return counts;
    }

    void note_waiting() {
        size_t waiting = buffer.size() - holes;
        growing_calls = waiting > last_waiting ? growing_calls + 1 : 0;
        last_waiting = waiting;
        if (growing_calls < growing_calls_to_warn || warned_growing) return;
        warned_growing = true;
        char message[256];
        std::snprintf(message, sizeof message,
                      "%llu objects are waiting for Memory.collectCycles, more after each of the last %d calls; its "
                      "budget is too small to keep up",
                      static_cast<unsigned long long>(waiting), growing_calls);
        performance_warning(message);
    }

    void note_freed() {
        for (const ClassCount& freed : count_by_class(garbage)) {
            std::pair<const char*, int>* entry = nullptr;
            for (auto& existing : freed_classes) {
                if (std::strcmp(existing.first, freed.name) == 0) entry = &existing;
            }
            if (entry == nullptr) entry = &freed_classes.emplace_back(freed.name, 0);
            if (entry->second < 0 || ++entry->second < collections_to_warn) continue;
            entry->second = -1;
            char message[256];
            std::snprintf(message, sizeof message,
                          "objects of class '%s' were freed by %d calls of Memory.collectCycles; a Weak<T> in their "
                          "cycles would let them be destroyed as soon as they are unreachable",
                          freed.name, collections_to_warn);
            performance_warning(message);
        }
    }

    // Finds the garbage among the buffered objects without destroying it, at exit.
    void report_uncollected() {
        for (Object* root : buffer) {
            if (root == nullptr) continue;
            mark_gray(root, SIZE_MAX);
            roots.push_back(root);
        }
        find_garbage(roots.size());
        if (garbage.empty()) return;

        char message[1024];
        int length = std::snprintf(message, sizeof message, "at exit, %llu objects in cycles were never freed:",
                                   static_cast<unsigned long long>(garbage.size()));
        const char* separator = " ";
        for (const ClassCount& entry : count_by_class(garbage)) {
            if (length < 0 || static_cast<size_t>(length) >= sizeof message - 128) break;
            length += std::snprintf(message + length, sizeof message - static_cast<size_t>(length), "%s%llu %s", separator,
                                    entry.count, entry.name);
            separator = ", ";
        }
        if (length >= 0 && static_cast<size_t>(length) < sizeof message) {
            std::snprintf(message + length, sizeof message - static_cast<size_t>(length),
                          ". Call Memory.collectCycles to free them, or break the cycles with Weak<T>");
        }
        performance_warning(message);
    }

    // Runs every destructor while every object of the garbage is intact, then frees it. The
    // collector holds a count on each object meanwhile, so no release can destroy one early.
    void destroy_garbage() {
        if constexpr (VPL_DEBUG != 0) note_freed();
        for (Object* object : garbage) for_each_child(object, [](Object* child) { child->retain(); });
        for (Object* object : garbage) {
            object->retain();
            object->state &= ~Object::buffered;
            object->clear_weak();
            object->set_slot(0);
        }

        // Only the garbage is finalized, since earlier collections freed what they finalized.
        for (Object* object : garbage) {
            object->state |= Object::finalized;
            object->vpl_finalize();
        }

        // Destructors may have changed references among the garbage, but none may have left one
        // to it anywhere else: without the references among it, only the collector's count is left.
        for (Object* object : garbage) {
            for_each_child(object, [](Object* child) {
                if (child->state & Object::finalized) child->unretain();
            });
        }
        for (Object* object : garbage) {
            if (object->refs() != 1) panic("a destructor kept a reference to an object of the cycle being collected", "<destructor>");
        }

        // Clearing a reference among the garbage releases the count just taken off for it.
        FunctionVisitor clear([](Object* child) {
            if (child->state & Object::finalized) child->retain();
            return true;
        });
        for (Object* object : garbage) object->vpl_visit(clear);
        for (Object* object : garbage) delete object;
        stats.freed += garbage.size();
    }

public:
    void collect(size_t budget, const char* location) {
        if (destroying > 0) panic("Memory.collectCycles cannot be called from a destructor", location);
        ++destroying;
        roots.swap(buffer);
        holes = 0;

        size_t processed = 0;
        for (; processed < roots.size(); ++processed) {
            if (roots[processed] != nullptr && !mark_gray(roots[processed], budget)) break;
        }
        stats.visited += marked.size();

        find_garbage(processed);
        for (size_t i = 0; i < processed; ++i) {
            Object* root = roots[i];
            if (root == nullptr) continue;
            root->state &= ~Object::buffered;
            root->set_slot(0);
        }

        // Back in the buffer before any destructor runs, since one may destroy them.
        for (size_t i = processed; i < roots.size(); ++i) {
            Object* root = roots[i];
            if (root == nullptr || (root->state & Object::dying)) continue;
            add(root);
        }
        roots.clear();
        marked.clear();

        if (!garbage.empty()) destroy_garbage();
        garbage.clear();
        if constexpr (VPL_DEBUG != 0) note_waiting();
        --destroying;
    }

    CycleCollector() = default;
    CycleCollector(const CycleCollector&) = delete;
    CycleCollector& operator=(const CycleCollector&) = delete;

    ~CycleCollector() {
        if constexpr (VPL_DEBUG != 0) report_uncollected();
    }

    CycleStats take_stats() { return std::exchange(stats, CycleStats{}); }
};

// Objects belong to one thread, and so will the collector's state once VPL has threads.
inline CycleCollector cycles;

inline void Object::destroy() {
    // Holding the count at 1 lets the destructor create and drop temporary references to `this`
    // without destroying it twice; ~Object checks that none outlive it.
    retain();
    clear_weak();
    if (state & buffered) cycles.remove(this);
    ++cycles.destroying;
    delete this;
    --cycles.destroying;
}

inline void Object::add_to_buffer() {
    cycles.add(this);
    ++cycles.stats.buffered;
}

// ---- Functions ----

// A closure's name for diagnostics, as a template argument so that closures don't store it.
template <size_t N>
struct ClosureName {
    char text[N];
    consteval ClosureName(const char (&name)[N]) {
        for (size_t i = 0; i < N; ++i) text[i] = name[i];
    }
};

// An escaping function value: the code to call, and the closure it is called with, which holds
// copies of what its lambda captured and is null when the lambda captures nothing, so that such a
// function allocates nothing and copying it only tests for null. A null code pointer is a null
// function, which only storage that VPL assigns before reading ever holds, and Optional's null.
template <class R, class... A>
class Function<R(A...)> {
public:
    using Code = R (*)(const void*, A...);

private:
    // Declared first, so that an assignment stores the new code before releasing the old closure,
    // whose destructor may read this function.
    Code code = nullptr;
    Ref<Object> closure;

public:
    Function() = default;
    Function(Code function, Ref<Object> captured) : code(function), closure(std::move(captured)) {}

    // As for Ref, a move swaps, so the old value dies with `other`, after the store.
    Function(const Function&) = default;
    Function(Function&& other) noexcept : code(other.code), closure(std::move(other.closure)) { other.code = nullptr; }
    Function& operator=(const Function&) = default;
    Function& operator=(Function&& other) noexcept {
        std::swap(code, other.code);
        closure = std::move(other.closure);
        return *this;
    }

    R operator()(A... arguments) const {
        if constexpr (VPL_DEBUG != 0) {
            if (code == nullptr) panic("internal compiler error: null function called", "<runtime>");
        }
        return code(closure.get(), std::forward<A>(arguments)...);
    }

    Code vpl_code() const { return code; }
    const void* vpl_closure() const { return closure.get(); }
    bool vpl_is_null() const { return code == nullptr; }

    void vpl_visit(Visitor& visitor) { visit(visitor, closure); }
};

// A non-escaping function value where its callable's type can't be a template parameter: a
// parameter of a virtual or recursive function, or of a function type. It refers to the callable
// without owning it, which VPL's rules make safe: the callable is a lambda or function value the
// caller holds until the call returns. Made from a Function, it refers to the same closure and calls
// the same code, so that the call is no more indirect than the Function's.
template <class Signature>
class FunctionRef;

template <class R, class... A>
class FunctionRef<R(A...)> {
    const void* callable = nullptr;
    R (*call)(const void*, A...) = nullptr;

public:
    // Null, only as Optional's null.
    FunctionRef() = default;

    FunctionRef(const Function<R(A...)>& f) noexcept : callable(f.vpl_closure()), call(f.vpl_code()) {}

    template <class F>
        requires(!std::is_same_v<std::remove_cvref_t<F>, FunctionRef> && !std::is_same_v<std::remove_cvref_t<F>, Function<R(A...)>>)
    FunctionRef(const F& f) noexcept
        : callable(std::addressof(f)),
          call([](const void* c, A... arguments) -> R { return (*static_cast<const F*>(c))(std::forward<A>(arguments)...); }) {}

    R operator()(A... arguments) const { return call(callable, std::forward<A>(arguments)...); }

    bool vpl_is_null() const { return call == nullptr; }
};

// The closure of one lambda that captures something: Body is a C++ lambda that captures nothing
// and takes the captured values as its first parameters, so that the collector can visit them
// here. Capturing nothing, it can be default-constructed where it is called, so only the captures
// are stored.
template <class Signature, ClosureName Name, class Body, class... C>
class Closure;

template <class R, class... A, ClosureName Name, class Body, class... C>
class Closure<R(A...), Name, Body, C...> final : public Object {
    std::tuple<C...> captures;

public:
    template <class... V>
    explicit Closure(V&&... values) : captures(std::forward<V>(values)...) {}

    static R call([[maybe_unused]] const void* self, A... arguments) {
        if constexpr (sizeof...(C) == 0) {
            return Body{}(std::forward<A>(arguments)...);
        } else {
            const auto* closure = static_cast<const Closure*>(static_cast<const Object*>(self));
            return std::apply([&](const C&... values) -> R { return Body{}(values..., std::forward<A>(arguments)...); }, closure->captures);
        }
    }

    void vpl_visit([[maybe_unused]] Visitor& visitor) override {
        std::apply([&](C&... values) { (visit(visitor, values), ...); }, captures);
    }

    const char* vpl_class_name() const override { return Name.text; }
};

// A closure that captures no reference can't be on a cycle, so `cyclic` is false for it.
template <class Signature, ClosureName Name, class Body, class... C>
inline Function<Signature> closure([[maybe_unused]] bool cyclic, Body, C&&... captures) {
    if constexpr (sizeof...(C) == 0) {
        return Function<Signature>(&Closure<Signature, Name, Body>::call, nullptr);
    } else {
        using Created = Closure<Signature, Name, Body, std::remove_cvref_t<C>...>;
        auto* created = new Created(std::forward<C>(captures)...);
        created->vpl_set_cyclic(cyclic);
        return Function<Signature>(&Created::call, Ref<Object>::adopt(created));
    }
}

// A nullable non-escaping function argument, made where the call is: what it refers to (a lambda, or
// a copy of an escaping function) lives until the end of the full expression, which the call is in.
template <class S>
inline Optional<FunctionRef<S>> optional_ref(const Optional<FunctionRef<S>>& f) {
    return f;
}

template <class S>
inline Optional<FunctionRef<S>> optional_ref(const Optional<Function<S>>& f) {
    if (!f.has_value()) return nullptr;
    return FunctionRef<S>(f.assumed());
}

template <class S, class F>
inline Optional<FunctionRef<S>> optional_ref(const F& f) {
    return FunctionRef<S>(f);
}

// A strong copy that keeps a closure alive while it runs, as pin does for an object.
template <class S>
inline Function<S> pin(const Function<S>& value) {
    return value;
}

// A function chosen by ?: from several. It holds every callable it could be and calls the chosen
// one directly, so each can still be inlined where the choice is passed to a template.
template <class... F>
class Choice {
    std::tuple<F...> callables;
    int chosen;

    template <int I, class... A>
    auto call(A&&... arguments) const {
        if constexpr (I + 1 == static_cast<int>(sizeof...(F))) {
            return std::get<I>(callables)(std::forward<A>(arguments)...);
        } else {
            if (chosen == I) return std::get<I>(callables)(std::forward<A>(arguments)...);
            return call<I + 1>(std::forward<A>(arguments)...);
        }
    }

public:
    Choice(int index, F... options) : callables(std::move(options)...), chosen(index) {}

    template <class... A>
    auto operator()(A&&... arguments) const {
        return call<0>(std::forward<A>(arguments)...);
    }
};

template <class... F>
Choice<F...> choose(int index, F... options) {
    return Choice<F...>(index, std::move(options)...);
}

// A choice that may also pick an escaping function, which `index` evaluates into `held` only when it
// chooses it: unlike making a lambda, evaluating a field or a call can be observed.
template <class Signature, class I, class... F>
Choice<F..., Function<Signature>> choose_held(I index, F... options) {
    Function<Signature> held;
    int chosen = index(held);
    return Choice<F..., Function<Signature>>(chosen, std::move(options)..., std::move(held));
}

// ---- Standard library intrinsics ----
//
// The C++ side of extern functions in the standard library: vpl::memory is Memory, and vpl::bounds
// is Bounds. Memory's functions are unsafe; misusing them is undefined behavior.

namespace memory {

// Uninitialized storage for count values of T.
template <class T>
inline T* allocate(int32_t count, const char* location) {
    if (count < 0) panic("cannot allocate a negative number of elements", location);
    if (static_cast<size_t>(count) > SIZE_MAX / sizeof(T)) panic("allocation is too large", location);
    return static_cast<T*>(::operator new(sizeof(T) * static_cast<size_t>(count), std::align_val_t(alignof(T))));
}

template <class T>
inline void free(T* data) {
    ::operator delete(static_cast<void*>(data), std::align_val_t(alignof(T)));
}

template <class T>
inline void initialize(T* place, T value) {
    ::new (static_cast<void*>(place)) T(std::move(value));
}

template <class T>
inline void initializeDefault(T* data, int32_t count) {
    for (int32_t i = 0; i < count; ++i) ::new (static_cast<void*>(data + i)) T(default_value<T>());
}

template <class T>
inline void drop(T* place) {
    place->~T();
}

// Moves count values into uninitialized storage, leaving the source uninitialized. The ranges may
// overlap: every step constructs into storage that is outside the source or was just vacated.
template <class T>
inline void moveTo(T* from, T* to, int32_t count) {
    if (to < from) {
        for (int32_t i = 0; i < count; ++i) {
            ::new (static_cast<void*>(to + i)) T(std::move(from[i]));
            from[i].~T();
        }
    } else if (to > from) {
        for (int32_t i = count - 1; i >= 0; --i) {
            ::new (static_cast<void*>(to + i)) T(std::move(from[i]));
            from[i].~T();
        }
    }
}

inline void collectCycles(const char* location) { cycles.collect(SIZE_MAX, location); }

inline void collectCycles(int32_t maxVisits, const char* location) {
    if (maxVisits < 0) panic("cannot visit a negative number of objects", location);
    cycles.collect(static_cast<size_t>(maxVisits), location);
}

inline void takeCycleStats(int32_t& buffered, int32_t& visited, int32_t& freed) {
    auto clamp = [](size_t count) { return static_cast<int32_t>(count < INT32_MAX ? count : INT32_MAX); };
    CycleStats stats = cycles.take_stats();
    buffered = clamp(stats.buffered);
    visited = clamp(stats.visited);
    freed = clamp(stats.freed);
}

}  // namespace memory

namespace bounds {

inline void check(int32_t index, int32_t length, const char* location) {
    if (static_cast<uint32_t>(index) >= static_cast<uint32_t>(length)) panic_index(index, length, location);
}

inline void checkRange(int32_t start, int32_t count, int32_t length, const char* location) {
    if (start < 0 || count < 0 || static_cast<int64_t>(start) + count > length) {
        char message[128];
        std::snprintf(message, sizeof message, "range of %" PRId32 " from %" PRId32 " is out of bounds for length %" PRId32, count, start,
                      length);
        panic(message, location);
    }
}

}  // namespace bounds

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

template <class T>
inline void print(const Optional<T>& value) {
    if (value.has_value()) {
        print(value.assumed());
    } else {
        std::puts("null");
    }
}

}  // namespace vpl
