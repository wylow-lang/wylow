#pragma once

#include <condition_variable>
#include <deque>
#include <memory>
#include <mutex>
#include <utility>

#include "optional.h"

namespace wyl {

namespace channel {

template <class T>
struct State {
    std::mutex Mutex;
    std::condition_variable Ready;
    std::deque<T> Queue;
    int Senders = 0;
    int Receivers = 0;
    bool Closed = false;
};

}

template <class T>
class SyncState {
    std::shared_ptr<channel::State<T>> m_State;

public:
    SyncState() = default;
    explicit SyncState(std::shared_ptr<channel::State<T>> state) : m_State(std::move(state)) {}

    channel::State<T>& Get() {
        if (m_State == nullptr) RaisePanic("the channel's state was used after it was released", "<runtime>");
        return *m_State;
    }
};

namespace channel {

template <class T>
inline SyncState<T> createState() {
    return SyncState<T>(std::make_shared<State<T>>());
}

template <class T>
inline void attachSender(SyncState<T> state, const char* location) {
    State<T>& shared = state.Get();
    std::lock_guard lock(shared.Mutex);
    if (shared.Closed) RaisePanic("cannot make a sender for a closed channel", location);
    ++shared.Senders;
}

template <class T>
inline void detachSender(SyncState<T> state) {
    State<T>& shared = state.Get();
    std::deque<T> dropped;
    {
        std::lock_guard lock(shared.Mutex);
        if (--shared.Senders > 0 || shared.Closed) return;
        shared.Closed = true;
        if (shared.Receivers == 0) dropped.swap(shared.Queue);
    }
    shared.Ready.notify_all();
}

template <class T>
inline void attachReceiver(SyncState<T> state) {
    State<T>& shared = state.Get();
    std::lock_guard lock(shared.Mutex);
    ++shared.Receivers;
}

template <class T>
inline void detachReceiver(SyncState<T> state) {
    State<T>& shared = state.Get();
    std::deque<T> dropped;
    {
        std::lock_guard lock(shared.Mutex);
        if (--shared.Receivers > 0 || !shared.Closed) return;
        dropped.swap(shared.Queue);
    }
    shared.Ready.notify_all();
}

template <class T>
inline void sendValue(SyncState<T> state, T value, const char* location) {
    if (AnyInUse(value)) RaisePanic("cannot move a value that is in use to another thread", location);
    State<T>& shared = state.Get();
    {
        std::lock_guard lock(shared.Mutex);
        shared.Queue.push_back(std::move(value));
    }
    shared.Ready.notify_one();
}

template <class T>
inline Nullable<T> receiveValue(SyncState<T> state) {
    State<T>& shared = state.Get();
    std::unique_lock lock(shared.Mutex);
    shared.Ready.wait(lock, [&shared] { return !shared.Queue.empty() || shared.Closed; });
    if (shared.Queue.empty()) return Nullable<T>();
    T value = std::move(shared.Queue.front());
    shared.Queue.pop_front();
    return Nullable<T>(std::move(value));
}

}

}
