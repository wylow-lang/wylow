#pragma once

#include <atomic>
#include <optional>
#include <thread>

#include "pools.h"

namespace wyl {

// Class objects derive virtually from Object, so every object has exactly one reference count
// however many interfaces it implements. A normal object's count is non-atomic, because the object is
// thread-confined. A shared object's count is atomic.

class Object;

// Shared by an object and the weak references to it, so that they can tell when it is gone. The
// object holds one count and each weak reference one; the last to let go frees the block.
struct WeakBlock {
    Object* Target;
    std::atomic<uintptr_t> Count;
    // The object's buffer slot and creation number, which the block takes over from the object's header.
    uint32_t Slot;
    uint32_t Created;
    // A shared object's block: Target changes under Guard, and a reader takes its strong reference
    // under Guard, so the object cannot be destroyed between the read and the count.
    bool Shared;
    std::atomic_flag Guard;

    WeakBlock(Object* target, uintptr_t count, uint32_t slot, uint32_t created, bool shared = false)
        : Target(target), Count(count), Slot(slot), Created(created), Shared(shared) {}
};

// A spin lock rather than a mutex: the section is a few instructions, and unlocking touches nothing
// after its one store, so the next holder may free the block.
class WeakGuard {
    WeakBlock& m_Block;

public:
    explicit WeakGuard(WeakBlock& block) : m_Block(block) {
        while (m_Block.Guard.test_and_set(std::memory_order_acquire)) std::this_thread::yield();
    }
    WeakGuard(const WeakGuard&) = delete;
    WeakGuard& operator=(const WeakGuard&) = delete;
    ~WeakGuard() { m_Block.Guard.clear(std::memory_order_release); }
};

template <class R>
class Weak;

// Called by the cycle collector for each strong reference an object holds, through the generated
// Visit; returning true clears the reference.
class Visitor {
public:
    virtual bool Visit(Object* target) = 0;

protected:
    ~Visitor() = default;
};

class CycleCollector;

// Whether objects of a generic class instantiation may be part of a cycle. The compiler specializes it
// to false for the instantiations it knows are acyclic; others only C++ sees are assumed cyclic.
template <class T>
inline constexpr bool IS_CYCLIC = true;

// The Wylow name of a generic class instantiation, for diagnostics; the compiler specializes it for the
// instantiations it knows of.
template <class T>
inline constexpr const char* CLASS_NAME = nullptr;

class Object {
    // The flags below in the low bits and the reference count above them, so that the header is
    // the vtable pointer and two words. The count starts at 1 for the `new` expression that creates
    // the object, so a constructor can hand out and drop references to `this` without it reaching 0.
    // A shared object's state is SHARED alone and never changes once its constructor has run, so any
    // thread may read it; its count is in `m_Side`.
    static constexpr int FLAG_BITS = 8;
    static constexpr uint64_t ONE_REF = uint64_t{1} << FLAG_BITS;
    uint64_t m_State = ONE_REF;
    // The weak block once the object has one; otherwise, while buffered, one more than the
    // object's index in the cycle buffer, so that it can leave the buffer when destroyed, and the
    // object's creation number, which orders a collected cycle's destructors. A shared object's
    // holds its atomic count, above the SHARED_ flags.
    union {
        WeakBlock* WeakPointer;
        struct {
            uint32_t Slot;
            uint32_t Created;
        } Own;
        std::atomic<uint64_t> SharedState;
    } m_Side{.Own = {0, s_ObjectsCreated++}};

    inline static thread_local uint32_t s_ObjectsCreated = 0;

    // Set once the count has reached zero, or once the collector has found the object to be
    // garbage: weak references to the object read null from then on.
    static constexpr uint64_t DYING = 1;
    // The object's class may be part of a cycle; set by its constructor.
    static constexpr uint64_t CYCLIC = 2;
    static constexpr uint64_t BUFFERED = 4;
    // Trial deletion's marks; an object with neither is black.
    static constexpr uint64_t GRAY = 8;
    static constexpr uint64_t WHITE = 16;
    // The collector has run the object's destructors, which its C++ destructor then skips.
    static constexpr uint64_t FINALIZED = 32;
    // `m_Side` holds the weak block, which holds the buffer slot.
    static constexpr uint64_t HAS_WEAK = 64;
    // The count is `m_Side.SharedState`, and this object is a SharedObject.
    static constexpr uint64_t SHARED = 128;
    static constexpr uint64_t FLAG_MASK = ONE_REF - 1;

    // The object has a weak block, so a weak reference may add to the count during its last release.
    static constexpr uint64_t SHARED_WEAK = 1;
    // The last release has begun, so weak references taken by the destructor read null.
    static constexpr uint64_t SHARED_DYING = 2;
    static constexpr uint64_t SHARED_ONE_REF = 4;

    template <class>
    friend class Weak;
    friend class CycleCollector;
    friend class SharedObject;
    template <class>
    friend class StackSlot;

    void Destroy();
    void AddToBuffer();
    void LeaveCollector();
    void DestroyInSlot(bool inPlace);

    uint64_t Refs() const {
        return (m_State & SHARED) ? m_Side.SharedState.load(std::memory_order_relaxed) / SHARED_ONE_REF : m_State >> FLAG_BITS;
    }
    // Only for the collector, which balances every one of these.
    void Unretain() { m_State -= ONE_REF; }

    uint32_t Slot() const { return (m_State & HAS_WEAK) ? m_Side.WeakPointer->Slot : m_Side.Own.Slot; }
    void SetSlot(uint32_t value) {
        if (m_State & HAS_WEAK) {
            m_Side.WeakPointer->Slot = value;
        } else {
            m_Side.Own.Slot = value;
        }
    }

    // How many objects were created after this one, which stays in creation order across the counter's wrap.
    uint32_t Age() const { return s_ObjectsCreated - ((m_State & HAS_WEAK) ? m_Side.WeakPointer->Created : m_Side.Own.Created); }

    // Null for a dying object, so that weak references taken by its destructor read null too.
    virtual WeakBlock* GetWeakBlock() {
        if (m_State & DYING) return nullptr;
        if (!(m_State & HAS_WEAK)) {
            m_Side.WeakPointer = new WeakBlock{this, 1, (m_State & BUFFERED) ? m_Side.Own.Slot : 0, m_Side.Own.Created};
            m_State |= HAS_WEAK;
        }
        return m_Side.WeakPointer;
    }

    // Ends a shared object, given the state in which its last release found it. Only a SharedObject
    // has a shared state, and this virtual call is how the release reaches its weak block.
    virtual void EndShared(uint64_t) {}

    // One atomic step, so that two releases cannot each see the other's reference and both drop theirs.
    bool DropUnlessLast(uint64_t& state) {
        while (state / SHARED_ONE_REF > 1) {
            if (m_Side.SharedState.compare_exchange_weak(state, state - SHARED_ONE_REF, std::memory_order_acq_rel, std::memory_order_acquire)) return true;
        }
        return false;
    }

    void ReleaseShared() {
        uint64_t state = m_Side.SharedState.load(std::memory_order_acquire);
        if (!DropUnlessLast(state)) EndShared(state);
    }

    // Leaves the slot behind in the header, for an object still to leave the buffer.
    void ClearWeak() {
        m_State |= DYING;
        if (!(m_State & HAS_WEAK)) return;
        WeakBlock* weak = m_Side.WeakPointer;
        m_Side.Own = {weak->Slot, weak->Created};
        m_State &= ~HAS_WEAK;
        weak->Target = nullptr;
        if (--weak->Count == 0) delete weak;
    }

public:
    Object() = default;
    Object(const Object&) = delete;
    Object& operator=(const Object&) = delete;

    virtual ~Object() {
        if (Refs() != 1) RaisePanic("an object's destructor kept a reference to the object", "<destructor>");
    }

    // The virtual destructor has every delete pass the size of the object's dynamic type.
    static void* operator new(size_t size) { return CurrentPools().Allocate(size); }
    static void* operator new(size_t size, std::align_val_t align) { return ::operator new(size, align); }
    static void operator delete(void* object, size_t size) { CurrentPools().Deallocate(object, size); }
    static void operator delete(void* object, size_t size, std::align_val_t align) { ::operator delete(object, size, align); }

    // Called around every borrow of the object: for a class implementing the standard library's
    // Borrowable, the generated override calls its beginBorrow and endBorrow.
    virtual void BeginBorrow() {}
    virtual void EndBorrow() {}

    // Generated for classes: visits each strong reference the object holds, for the collector.
    virtual void Visit(Visitor&) {}

    // Generated for classes with destructors: runs the object's destructors without releasing its
    // fields, for the collector.
    virtual void Finalize() {}

    // Generated for classes, for the collector's diagnostics.
    virtual const char* ClassName() const { return "object"; }

    void SetCyclic(bool value) { m_State = value ? m_State | CYCLIC : m_State & ~CYCLIC; }
    bool Finalized() const { return (m_State & FINALIZED) != 0; }

    // 56 bits of count cannot overflow: the references alone would fill more than any address space.
    void Retain() {
        if (m_State & SHARED) [[unlikely]] {
            m_Side.SharedState.fetch_add(SHARED_ONE_REF, std::memory_order_relaxed);
            return;
        }
        m_State += ONE_REF;
    }

    // A release that leaves a cyclic object's count above zero may have left it in an unreachable
    // cycle, so it is buffered for the collector, once until the next collection.
    void Release() {
        if (m_State & SHARED) [[unlikely]] {
            ReleaseShared();
            return;
        }
        m_State -= ONE_REF;
        if (m_State < ONE_REF) {
            Destroy();
        } else if ((m_State & (FLAG_MASK & ~HAS_WEAK)) == CYCLIC) {
            AddToBuffer();
        }
    }
};

class SharedObject : public virtual Object {
    std::atomic<WeakBlock*> m_Weak{nullptr};

    // The caller holds a strong reference, so no last release runs during this, and the last release
    // sees SHARED_WEAK because it happens after the caller lets go.
    WeakBlock* GetWeakBlock() override {
        if (m_Side.SharedState.load(std::memory_order_acquire) & SHARED_DYING) return nullptr;
        if (WeakBlock* block = m_Weak.load(std::memory_order_acquire)) return block;
        WeakBlock* made = new WeakBlock{this, 1, 0, 0, true};
        WeakBlock* expected = nullptr;
        if (!m_Weak.compare_exchange_strong(expected, made, std::memory_order_acq_rel)) {
            delete made;
            return expected;
        }
        m_Side.SharedState.fetch_or(SHARED_WEAK, std::memory_order_acq_rel);
        return made;
    }

    void EndShared(uint64_t state) override;

public:
    SharedObject() {
        m_State |= SHARED;
        ::new (static_cast<void*>(&m_Side.SharedState)) std::atomic<uint64_t>(SHARED_ONE_REF);
    }
    SharedObject(const SharedObject&) = delete;
    SharedObject& operator=(const SharedObject&) = delete;

    static void* operator new(size_t size) { return ::operator new(size); }
    static void* operator new(size_t size, std::align_val_t align) { return ::operator new(size, align); }
    static void operator delete(void* object, size_t size) { ::operator delete(object, size); }
    static void operator delete(void* object, size_t size, std::align_val_t align) { ::operator delete(object, size, align); }
};

static_assert(sizeof(void*) != 8 || sizeof(Object) == 24);

template <class T>
class Ref {
    T* m_Ptr = nullptr;

    template <class>
    friend class Ref;

    static void Retain(T* p) {
        if (p != nullptr) static_cast<Object*>(p)->Retain();
    }
    static void Release(T* p) {
        if (p != nullptr) static_cast<Object*>(p)->Release();
    }

public:
    Ref() = default;
    Ref(std::nullptr_t) {}
    explicit Ref(T* p) : m_Ptr(p) { Retain(p); }
    Ref(const Ref& other) : m_Ptr(other.m_Ptr) { Retain(m_Ptr); }
    Ref(Ref&& other) noexcept : m_Ptr(other.m_Ptr) { other.m_Ptr = nullptr; }

    template <class U>
        requires std::is_convertible_v<U*, T*>
    Ref(const Ref<U>& other) : m_Ptr(other.m_Ptr) {
        Retain(m_Ptr);
    }

    template <class U>
        requires std::is_convertible_v<U*, T*>
    Ref(Ref<U>&& other) noexcept : m_Ptr(other.m_Ptr) {
        other.m_Ptr = nullptr;
    }

    ~Ref() { Release(m_Ptr); }

    // Takes over the reference that `new` starts every object with.
    static Ref Adopt(T* p) {
        Ref result;
        result.m_Ptr = p;
        return result;
    }

    // The new value is stored before the old one is released, since the old object's destructor
    // may read this reference. Nothing touches `this` afterwards: that destructor may free it.
    void operator=(const Ref& other) {
        T* old = m_Ptr;
        Retain(other.m_Ptr);
        m_Ptr = other.m_Ptr;
        Release(old);
    }

    // As for a copy, the old value is released at the store, not when `other` dies later.
    Ref& operator=(Ref&& other) noexcept {
        if (&other == this) return *this;
        T* old = m_Ptr;
        m_Ptr = other.m_Ptr;
        other.m_Ptr = nullptr;
        Release(old);
        return *this;
    }

    T* operator->() const {
        if constexpr (WYL_DEBUG != 0) {
            if (m_Ptr == nullptr) RaisePanic("internal compiler error: null dereference", "<runtime>");
        }
        return m_Ptr;
    }

    T* Get() const { return m_Ptr; }
    explicit operator bool() const { return m_Ptr != nullptr; }

    friend bool operator==(const Ref& a, const Ref& b) {
        return static_cast<const Object*>(a.m_Ptr) == static_cast<const Object*>(b.m_Ptr);
    }
};

// The standard library's Weak<T>, written Weak<Ref<T>> in C++ like any type argument. It keeps the
// target as a T* too, since a pointer to the virtual base Object cannot be cast back down.
template <class T>
class Weak<Ref<T>> {
    WeakBlock* m_Block = nullptr;
    T* m_Ptr = nullptr;

public:
    Weak() = default;

    template <class U>
        requires std::is_convertible_v<U*, T*>
    explicit Weak(const Ref<U>& target) : m_Ptr(target.Get()) {
        if (m_Ptr == nullptr) return;
        m_Block = static_cast<Object*>(m_Ptr)->GetWeakBlock();
        if (m_Block != nullptr) ++m_Block->Count;
    }

    Weak(const Weak& other) : m_Block(other.m_Block), m_Ptr(other.m_Ptr) {
        if (m_Block != nullptr) ++m_Block->Count;
    }

    Weak(Weak&& other) noexcept : m_Block(other.m_Block), m_Ptr(other.m_Ptr) {
        other.m_Block = nullptr;
        other.m_Ptr = nullptr;
    }

    Weak& operator=(const Weak& other) {
        Weak copy(other);
        std::swap(m_Block, copy.m_Block);
        std::swap(m_Ptr, copy.m_Ptr);
        return *this;
    }

    Weak& operator=(Weak&& other) noexcept {
        std::swap(m_Block, other.m_Block);
        std::swap(m_Ptr, other.m_Ptr);
        return *this;
    }

    ~Weak() {
        if (m_Block != nullptr && --m_Block->Count == 0) delete m_Block;
    }

    Ref<T> get() const {
        if (m_Block == nullptr) return Ref<T>();
        if (!m_Block->Shared) return m_Block->Target != nullptr ? Ref<T>(m_Ptr) : Ref<T>();
        WeakGuard guard(*m_Block);
        return m_Block->Target != nullptr ? Ref<T>(m_Ptr) : Ref<T>();
    }
};

template <class T>
struct IsRef : std::false_type {};
template <class T>
struct IsRef<Ref<T>> : std::true_type {};

template <class Signature>
class Function;

template <class T>
struct IsFunctionValue : std::false_type {};
template <class S>
struct IsFunctionValue<Function<S>> : std::true_type {};

template <class T>
inline Ref<T> Adopt(T* object) {
    return Ref<T>::Adopt(object);
}

template <class T>
inline const Object* Identity(const Ref<T>& value) {
    return value.Get();
}
inline const Object* Identity(std::nullptr_t) { return nullptr; }

// A strong copy that keeps an object alive while a method runs on it or a reference into it is used.
template <class T>
inline Ref<T> Pin(const Ref<T>& value) {
    return value;
}

// Holds an object and borrows it, for as long as a reference or span into its storage is used.
template <class T>
class Borrow {
    Ref<T> m_Ref;

public:
    explicit Borrow(Ref<T> value) : m_Ref(std::move(value)) { static_cast<Object*>(m_Ref.Get())->BeginBorrow(); }
    Borrow(const Borrow&) = delete;
    Borrow& operator=(const Borrow&) = delete;
    ~Borrow() { static_cast<Object*>(m_Ref.Get())->EndBorrow(); }

    T* operator->() const { return m_Ref.operator->(); }
};

template <class T>
inline Borrow<T> MakeBorrow(Ref<T> value) {
    return Borrow<T>(std::move(value));
}

template <class T>
struct IsBorrow : std::false_type {};

template <class T>
struct IsBorrow<Borrow<T>> : std::true_type {};

// An 'is ref' pattern's holder keeps the object its name points into alive, and borrowed if that is
// how its storage was handed out, for as long as the name is in scope. A failed test lets it go.
template <class T>
inline const Borrow<T>& Hold(std::optional<Borrow<T>>& holder, Ref<T> value) {
    holder.reset();
    return holder.emplace(std::move(value));
}

template <class T>
inline const Ref<T>& Hold(Ref<T>& holder, Ref<T> value) {
    holder = std::move(value);
    return holder;
}

template <class T>
inline bool Unhold(std::optional<Borrow<T>>& holder) {
    holder.reset();
    return false;
}

template <class T>
inline bool Unhold(Ref<T>& holder) {
    holder = nullptr;
    return false;
}

// Calls f with value, so that value is evaluated before anything f evaluates.
template <class T, class F>
inline decltype(auto) With(T&& value, F&& f) {
    return f(value);
}

// A copy that keeps a string's bytes alive while a span views them.
inline String Pin(const String& value) {
    return value;
}

// In generic code a value may be a struct, which lives in its place and needs no pin.
template <class T>
    requires(!IsRef<std::remove_cv_t<T>>::value && !IsFunctionValue<std::remove_cv_t<T>>::value && !std::is_same_v<std::remove_cv_t<T>, String>)
inline T& Pin(T& value) {
    return value;
}

// In generic code a value may be a struct, which is borrowed through whatever holds it.
template <class T>
    requires(!IsRef<std::remove_cv_t<T>>::value)
inline T& MakeBorrow(T& value) {
    return value;
}

// Passes a by-value argument as a copy of a caller-owned temporary. C++ ABIs differ on whether a
// parameter dies when the callee returns or at the end of the caller's statement; the temporary
// always lives to the end of the statement, so objects are destroyed at the same point everywhere.
template <class T>
inline const T& Keep(const T& value) {
    return value;
}

// Calls go through the class that declares the method, so C++ name hiding and ambiguity between
// bases cannot pick a different overload than Wylow did.
template <class B, class T>
inline B* As(const Ref<T>& value) {
    return value.operator->();
}

template <class B, class T>
inline B* As(const Borrow<T>& value) {
    return value.operator->();
}

// The members the standard library declares for a primitive type or String (compare.h).
template <class T>
struct Builtin;

template <class T>
inline constexpr bool IS_BUILTIN = std::is_arithmetic_v<T> || std::is_same_v<T, String>;

// A call through a generic bound: objects through the bound interface, primitives and String
// through their Builtin, structs directly.
template <class I, class T>
inline decltype(auto) Bound(T&& value) {
    using V = std::remove_cvref_t<T>;
    if constexpr (IsRef<V>::value || IsBorrow<V>::value) {
        return static_cast<I&>(*value.operator->());
    } else if constexpr (IS_BUILTIN<V>) {
        return Builtin<V>(value);
    } else {
        return (value);
    }
}

template <class T, class U>
inline bool Is(const Ref<U>& value) {
    return dynamic_cast<T*>(value.Get()) != nullptr;
}

template <class T, class U>
inline bool Is(const Ref<U>& value, Ref<T>& out) {
    T* p = dynamic_cast<T*>(value.Get());
    if (p == nullptr) return false;
    out = Ref<T>(p);
    return true;
}

template <class T, class U>
inline Ref<T> Downcast(const Ref<U>& value) {
    return Ref<T>(dynamic_cast<T*>(value.Get()));
}

template <class T>
inline Ref<T> Unwrap(Ref<T> value, const char* location) {
    if (!value) RaisePanic("null value unwrapped with '!'", location);
    return value;
}

}  // namespace wyl
