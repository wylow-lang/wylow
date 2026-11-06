#pragma once

#include "cycles.h"

namespace wyl {

// Holds an object made by a `new` that the compiler has proved can't outlive the scope declaring
// the slot: in the slot itself, or, for an object too large for the stack, on the heap as usual.
// The slot owns the reference that `new` starts the object with, so the count never reaches 0
// before the slot's end, which runs the destructors. The compiler ends a slot for a temporary at
// the end of its statement, and one for a local where the local goes out of scope.
inline constexpr size_t STACK_OBJECT_LIMIT = 512;

template <class T>
class StackSlot {
    static constexpr bool IN_PLACE = sizeof(T) <= STACK_OBJECT_LIMIT;
    alignas(T) unsigned char m_Storage[IN_PLACE ? sizeof(T) : 1];
    T* m_Object = nullptr;

public:
    StackSlot() = default;
    StackSlot(const StackSlot&) = delete;
    StackSlot& operator=(const StackSlot&) = delete;
    ~StackSlot() { End(); }

    template <class... A>
    Ref<T> Make(A&&... arguments) {
        if constexpr (WYL_DEBUG != 0) {
            if (m_Object != nullptr) RaisePanic("internal compiler error: a stack slot was filled twice", "<runtime>");
        }
        if constexpr (IN_PLACE) {
            m_Object = ::new (static_cast<void*>(m_Storage)) T(std::forward<A>(arguments)...);
        } else {
            m_Object = new T(std::forward<A>(arguments)...);
        }
        static_cast<Object*>(m_Object)->LeaveCollector();
        return Ref<T>(m_Object);
    }

    void End() {
        if (m_Object == nullptr) return;
        static_cast<Object*>(std::exchange(m_Object, nullptr))->DestroyInSlot(IN_PLACE);
    }
};

}  // namespace wyl
