#pragma once

#include <system_error>
#include <thread>

#include "functions.h"

namespace wyl {

namespace threading {

struct Handle {
    std::thread Worker;

    void Join() {
        if (Worker.joinable()) Worker.join();
    }
};

template <class T>
struct HandleOf : Handle {
    Optional<T> Result;
};

template <class T>
inline uint8_t* spawnThread(Function<T()> body, const char* location) {
    if (!s_MainStarted.load(std::memory_order_acquire))
        RaisePanic("cannot start a thread before main begins", location);
    auto* handle = new HandleOf<T>();
    // The thread destroys its closure itself, before its state ends and collects cycles, instead of
    // leaving the closure's lifetime to std::thread. The state ends before the thread function
    // returns, while the C++ runtime's thread-local storage is still intact.
    try {
        handle->Worker = std::thread([handle, body = std::move(body)]() mutable {
            BeginThread();
            {
                Function<T()> run = std::move(body);
                handle->Result = Optional<T>(run());
            }
            EndCurrentThread();
        });
    } catch (const std::system_error&) {
        delete handle;
        RaisePanic("the system could not start a thread", location);
    }
    return reinterpret_cast<uint8_t*>(handle);
}

inline void RefuseWaitWhileLocked(const char* message, const char* location) {
    if (CurrentThread().HoldsLock) RaisePanic(message, location);
}

template <class T>
inline T joinThread(uint8_t* data, const char* location) {
    if (data == nullptr) RaisePanic("cannot join a thread that was never started", location);
    RefuseWaitWhileLocked("cannot wait for a thread while this thread holds a lock", location);
    auto* handle = reinterpret_cast<HandleOf<T>*>(data);
    handle->Join();
    T value = std::move(handle->Result.Assumed());
    delete handle;
    return value;
}

template <class T>
inline void releaseThread(uint8_t* data) {
    if (data == nullptr) return;
    RefuseWaitWhileLocked("cannot wait for a thread while this thread holds a lock", "<runtime>");
    auto* handle = reinterpret_cast<HandleOf<T>*>(data);
    handle->Join();
    delete handle;
}

}  // namespace threading

}  // namespace wyl
