#pragma once

#include <mutex>
#include <utility>

#include "isolated.h"
#include "threads.h"

namespace wyl {

namespace threading {

// The graph is declared after the value, so it is destroyed first: its buffer joins the releasing
// thread's before the value is released, as an isolated box's does.
template <class T>
struct MutexData {
    std::mutex Lock;
    T Value;
    IsolatedState Graph;

    MutexData(T value, IsolatedState graph) : Value(std::move(value)), Graph(std::move(graph)) {}
};

}  // namespace threading

// The mutex object owns the data and frees it in its destructor. A copy of the handle is only ever
// used while that object is alive, so the handle needs no count.
template <class T>
struct MutexState {
    threading::MutexData<T>* Data = nullptr;
};

namespace threading {

template <class T>
inline MutexState<T> createMutex(T value, IsolatedState& graph, const char* location) {
    if (AnyInUse(value)) RaisePanic("cannot move a value that is in use to another thread", location);
    return MutexState<T>{new MutexData<T>(std::move(value), std::move(graph))};
}

template <class T>
inline void releaseMutex(MutexState<T> state) {
    delete state.Data;
}

template <class T, class R, class Body>
inline R lockMutex(MutexState<T> state, const Body& body, const char* location) {
    ThreadState& thread = CurrentThread();
    if (thread.HoldsLock) RaisePanic("cannot lock a mutex while this thread holds a lock", location);
    MutexData<T>& data = *state.Data;
    std::lock_guard guard(data.Lock);
    thread.HoldsLock = true;
    thread.CycleState.SwapBuffer(data.Graph.Buffer);
    R result = body(data.Value);
    thread.CycleState.SwapBuffer(data.Graph.Buffer);
    thread.HoldsLock = false;
    return result;
}

}  // namespace threading

}  // namespace wyl
