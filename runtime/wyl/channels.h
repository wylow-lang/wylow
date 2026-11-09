#pragma once

#include <condition_variable>
#include <deque>
#include <memory>
#include <mutex>
#include <utility>

#include "optional.h"
#include "threads.h"

namespace wyl {

namespace threading {

template <class T>
struct ChannelState {
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
    std::shared_ptr<threading::ChannelState<T>> m_State;

public:
    SyncState() = default;
    explicit SyncState(std::shared_ptr<threading::ChannelState<T>> state) : m_State(std::move(state)) {}

    threading::ChannelState<T>& Get() {
        if (m_State == nullptr) RaisePanic("the channel's state was used after it was released", "<runtime>");
        return *m_State;
    }
};

namespace threading {

template <class T>
inline SyncState<T> createState() {
    return SyncState<T>(std::make_shared<ChannelState<T>>());
}

template <class T>
inline void dropQueued(std::deque<T>& queue) {
    while (!queue.empty()) {
        T value = std::move(queue.front());
        queue.pop_front();
    }
}

template <class T>
inline void attachSender(SyncState<T> state, const char* location) {
    ChannelState<T>& shared = state.Get();
    std::lock_guard lock(shared.Mutex);
    if (shared.Closed) RaisePanic("cannot make a sender for a closed channel", location);
    ++shared.Senders;
}

template <class T>
inline void detachSender(SyncState<T> state) {
    ChannelState<T>& shared = state.Get();
    std::deque<T> dropped;
    {
        std::lock_guard lock(shared.Mutex);
        if (--shared.Senders > 0 || shared.Closed) return;
        shared.Closed = true;
        if (shared.Receivers == 0) dropped.swap(shared.Queue);
    }
    shared.Ready.notify_all();
    dropQueued(dropped);
}

template <class T>
inline void attachReceiver(SyncState<T> state) {
    ChannelState<T>& shared = state.Get();
    std::lock_guard lock(shared.Mutex);
    ++shared.Receivers;
}

template <class T>
inline void detachReceiver(SyncState<T> state) {
    ChannelState<T>& shared = state.Get();
    std::deque<T> dropped;
    {
        std::lock_guard lock(shared.Mutex);
        if (--shared.Receivers > 0 || !shared.Closed) return;
        dropped.swap(shared.Queue);
    }
    shared.Ready.notify_all();
    dropQueued(dropped);
}

template <class T>
inline void sendValue(SyncState<T> state, T value, const char* location) {
    if (AnyInUse(value)) RaisePanic("cannot move a value that is in use to another thread", location);
    ChannelState<T>& shared = state.Get();
    {
        std::lock_guard lock(shared.Mutex);
        shared.Queue.push_back(std::move(value));
    }
    shared.Ready.notify_one();
}

template <class T>
inline Nullable<T> receiveValue(SyncState<T> state, const char* location) {
    RefuseWaitWhileLocked("cannot receive from a channel while this thread holds a lock", location);
    ChannelState<T>& shared = state.Get();
    std::unique_lock lock(shared.Mutex);
    shared.Ready.wait(lock, [&shared] { return !shared.Queue.empty() || shared.Closed; });
    if (shared.Queue.empty()) return Nullable<T>();
    T value = std::move(shared.Queue.front());
    shared.Queue.pop_front();
    return Nullable<T>(std::move(value));
}

}

}
