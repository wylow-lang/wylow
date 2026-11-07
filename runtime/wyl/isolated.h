#pragma once

#include <utility>

#include "cycles.h"

namespace wyl {

struct IsolatedState {
    CycleBuffer Buffer;

    IsolatedState() = default;
    IsolatedState(IsolatedState&& other) noexcept : Buffer(std::move(other.Buffer)) { other.Buffer = CycleBuffer{}; }
    IsolatedState& operator=(IsolatedState&& other) noexcept {
        if (this != &other) {
            Buffer = std::move(other.Buffer);
            other.Buffer = CycleBuffer{};
        }
        return *this;
    }
    IsolatedState(const IsolatedState&) = delete;
    IsolatedState& operator=(const IsolatedState&) = delete;

    ~IsolatedState() { CurrentCycles().MergeBuffer(Buffer); }
};

namespace isolated {

template <class T, class Build>
inline T buildIsolated(IsolatedState& state, const Build& build) {
    CycleCollector& cycles = CurrentCycles();
    cycles.SwapBuffer(state.Buffer);
    T value = build();
    cycles.SwapBuffer(state.Buffer);
    return value;
}

template <class T, class R, class Body>
inline R openIsolated(IsolatedState& state, T& value, const Body& body) {
    CycleCollector& cycles = CurrentCycles();
    cycles.SwapBuffer(state.Buffer);
    R result = body(value);
    cycles.SwapBuffer(state.Buffer);
    return result;
}

template <class T>
inline T takeValue(T& value) {
    return std::move(value);
}

}  // namespace isolated

}  // namespace wyl
