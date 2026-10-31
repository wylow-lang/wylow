#pragma once

#include "cycles.h"

namespace wyl {

// Holds an object made by a `new` that the compiler has proved can't outlive the scope declaring
// the slot: in the slot itself, or, for an object too large for the stack, on the heap as usual.
// The slot owns the reference that `new` starts the object with, so the count never reaches 0
// before the slot's end, which runs the destructors. The compiler ends a slot for a temporary at
// the end of its statement, and one for a local where the local goes out of scope.
inline constexpr size_t stack_object_limit = 512;

template <class T>
class StackSlot {
    static constexpr bool in_place = sizeof(T) <= stack_object_limit;
    alignas(T) unsigned char storage[in_place ? sizeof(T) : 1];
    T* object = nullptr;

public:
    StackSlot() = default;
    StackSlot(const StackSlot&) = delete;
    StackSlot& operator=(const StackSlot&) = delete;
    ~StackSlot() { end(); }

    template <class... A>
    Ref<T> make(A&&... arguments) {
        if constexpr (WYL_DEBUG != 0) {
            if (object != nullptr) panic("internal compiler error: a stack slot was filled twice", "<runtime>");
        }
        if constexpr (in_place) {
            object = ::new (static_cast<void*>(storage)) T(std::forward<A>(arguments)...);
        } else {
            object = new T(std::forward<A>(arguments)...);
        }
        static_cast<Object*>(object)->leave_collector();
        return Ref<T>(object);
    }

    void end() {
        if (object == nullptr) return;
        static_cast<Object*>(std::exchange(object, nullptr))->destroy_in_slot(in_place);
    }
};

}  // namespace wyl
