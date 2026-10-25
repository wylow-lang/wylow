#pragma once

#include <optional>

#include "pools.h"

namespace vpl {

// Class objects derive virtually from Object, so every object has exactly one reference count
// however many interfaces it implements. The count is non-atomic: objects are thread-confined.

class Object;

// Shared by an object and the weak references to it, so that they can tell when it is gone. The
// object holds one count and each weak reference one; the last to let go frees the block.
struct WeakBlock {
    Object* target;
    uintptr_t count;
    // The object's buffer slot and creation number, which the block takes over from the object's header.
    uint32_t slot;
    uint32_t created;

    static void* operator new(size_t size) { return pools.allocate(size); }
    static void operator delete(void* block, size_t size) { pools.deallocate(block, size); }
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
    // object's index in the cycle buffer, so that it can leave the buffer when destroyed, and the
    // object's creation number, which orders a collected cycle's destructors.
    union {
        WeakBlock* weak;
        struct {
            uint32_t slot;
            uint32_t created;
        } own;
    } side{.own = {0, objects_created++}};

    // One counter for the program, until objects belong to a thread and the collector with them. It
    // wraps after 2^32 objects, which only objects that far apart in age can tell (see age()).
    inline static uint32_t objects_created = 0;

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
    template <class>
    friend class StackSlot;

    void destroy();
    void add_to_buffer();
    void leave_collector();
    void destroy_in_slot(bool in_place);

    uint64_t refs() const { return state >> flag_bits; }
    // Only for the collector, which balances every one of these.
    void unretain() { state -= one_ref; }

    uint32_t slot() const { return (state & has_weak) ? side.weak->slot : side.own.slot; }
    void set_slot(uint32_t value) {
        if (state & has_weak) {
            side.weak->slot = value;
        } else {
            side.own.slot = value;
        }
    }

    // How many objects were created after this one, which stays in creation order across the counter's wrap.
    uint32_t age() const { return objects_created - ((state & has_weak) ? side.weak->created : side.own.created); }

    // Null for a dying object, so that weak references taken by its destructor read null too.
    WeakBlock* weak_block() {
        if (state & dying) return nullptr;
        if (!(state & has_weak)) {
            side.weak = new WeakBlock{this, 1, (state & buffered) ? side.own.slot : 0, side.own.created};
            state |= has_weak;
        }
        return side.weak;
    }

    // Leaves the slot behind in the header, for an object still to leave the buffer.
    void clear_weak() {
        state |= dying;
        if (!(state & has_weak)) return;
        WeakBlock* weak = side.weak;
        side.own = {weak->slot, weak->created};
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

    // The virtual destructor has every delete pass the size of the object's dynamic type.
    static void* operator new(size_t size) { return pools.allocate(size); }
    static void* operator new(size_t size, std::align_val_t align) { return ::operator new(size, align); }
    static void operator delete(void* object, size_t size) { pools.deallocate(object, size); }
    static void operator delete(void* object, size_t size, std::align_val_t align) { ::operator delete(object, size, align); }

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

    // As for a copy, the old value is released at the store, not when `other` dies later.
    Ref& operator=(Ref&& other) noexcept {
        if (&other == this) return *this;
        T* old = ptr;
        ptr = other.ptr;
        other.ptr = nullptr;
        release(old);
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

template <class T>
struct is_borrow : std::false_type {};

template <class T>
struct is_borrow<Borrow<T>> : std::true_type {};

// An 'is ref' pattern's holder keeps the object its name points into alive, and borrowed if that is
// how its storage was handed out, for as long as the name is in scope. A failed test lets it go.
template <class T>
inline const Borrow<T>& hold(std::optional<Borrow<T>>& holder, Ref<T> value) {
    holder.reset();
    return holder.emplace(std::move(value));
}

template <class T>
inline const Ref<T>& hold(Ref<T>& holder, Ref<T> value) {
    holder = std::move(value);
    return holder;
}

template <class T>
inline bool unhold(std::optional<Borrow<T>>& holder) {
    holder.reset();
    return false;
}

template <class T>
inline bool unhold(Ref<T>& holder) {
    holder = nullptr;
    return false;
}

// Calls f with value, so that value is evaluated before anything f evaluates.
template <class T, class F>
inline decltype(auto) with(T&& value, F&& f) {
    return f(value);
}

// A copy that keeps a string's bytes alive while a span views them.
inline String pin(const String& value) {
    return value;
}

// In generic code a value may be a struct, which lives in its place and needs no pin.
template <class T>
    requires(!is_ref<std::remove_cv_t<T>>::value && !is_function_value<std::remove_cv_t<T>>::value && !std::is_same_v<std::remove_cv_t<T>, String>)
inline T& pin(T& value) {
    return value;
}

// Ends a string's pin before its scope does.
inline bool unhold(String& holder) {
    holder = String();
    return false;
}

// A generic value's pin of a struct is the struct in place, which there is nothing to let go of.
template <class T>
    requires(!is_ref<std::remove_cv_t<T>>::value && !is_function_value<std::remove_cv_t<T>>::value && !std::is_same_v<std::remove_cv_t<T>, String>)
inline bool unhold(T&) {
    return false;
}

// In generic code a value may be a struct, which is borrowed through whatever holds it.
template <class T>
    requires(!is_ref<std::remove_cv_t<T>>::value)
inline T& borrow(T& value) {
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
    if constexpr (is_ref<std::remove_cvref_t<T>>::value || is_borrow<std::remove_cvref_t<T>>::value) {
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

}  // namespace vpl
