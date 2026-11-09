#pragma once

#include <atomic>

#include "arithmetic.h"

namespace wyl {

namespace threading {

template <class T>
struct AtomicData {
    std::atomic<T> Value;

    explicit AtomicData(T value) : Value(value) {}
};

}  // namespace threading

// The atomic object owns the data and frees it in its destructor. A copy of the handle is only ever
// used while that object is alive, so the handle needs no count.
template <class T>
struct AtomicState {
    threading::AtomicData<T>* Data = nullptr;
};

namespace threading {

// Every operation uses std::atomic's default order, memory_order_seq_cst.

template <class T>
inline AtomicState<T> createAtomic(T value) {
    return AtomicState<T>{new AtomicData<T>(value)};
}

template <class T>
inline void releaseAtomic(AtomicState<T> state) {
    delete state.Data;
}

template <class T>
inline T atomicGet(AtomicState<T> state) {
    return state.Data->Value.load();
}

template <class T>
inline void atomicSet(AtomicState<T> state, T value) {
    state.Data->Value.store(value);
}

template <class T>
inline T atomicExchange(AtomicState<T> state, T value) {
    return state.Data->Value.exchange(value);
}

template <class T>
inline T atomicCompareExchange(AtomicState<T> state, T expected, T value) {
    state.Data->Value.compare_exchange_strong(expected, value);
    return expected;
}

// A debug build checks the sum before it stores it, so an overflow panics with the value unchanged.
// A release build wraps, as fetch_add does for signed types too, so it needs no loop.
template <class T>
inline T atomicAdd(AtomicState<T> state, T delta, const char* location) {
    if constexpr (WYL_DEBUG == 0) {
        (void)location;
        return state.Data->Value.fetch_add(delta);
    } else {
        T current = state.Data->Value.load();
        while (!state.Data->Value.compare_exchange_weak(current, Add(current, delta, location))) {
        }
        return current;
    }
}

template <class T>
inline T atomicSubtract(AtomicState<T> state, T delta, const char* location) {
    if constexpr (WYL_DEBUG == 0) {
        (void)location;
        return state.Data->Value.fetch_sub(delta);
    } else {
        T current = state.Data->Value.load();
        while (!state.Data->Value.compare_exchange_weak(current, Sub(current, delta, location))) {
        }
        return current;
    }
}

}  // namespace threading

}  // namespace wyl
