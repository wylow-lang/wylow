#pragma once

#include <thread>

#include "functions.h"

namespace wyl {

namespace thread {

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
    handle->Worker = std::thread([handle, body = std::move(body)]() {
        (void)CurrentThread();
        handle->Result = Optional<T>(body());
    });
    return reinterpret_cast<uint8_t*>(handle);
}

template <class T>
inline T joinThread(uint8_t* data) {
    if (data == nullptr) RaisePanic("cannot join a thread that was never started", "<runtime>");
    auto* handle = reinterpret_cast<HandleOf<T>*>(data);
    handle->Join();
    T value = std::move(handle->Result.Assumed());
    delete handle;
    return value;
}

template <class T>
inline void releaseThread(uint8_t* data) {
    if (data == nullptr) return;
    auto* handle = reinterpret_cast<HandleOf<T>*>(data);
    handle->Join();
    delete handle;
}

}  // namespace thread

}  // namespace wyl
