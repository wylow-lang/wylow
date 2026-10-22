#pragma once

#include "cycles.h"

namespace vpl {

// The C++ side of extern functions in the standard library: vpl::memory is Memory, and vpl::bounds
// is Bounds. Memory's functions are unsafe; misusing them is undefined behavior.

namespace memory {

// Uninitialized storage for count values of T.
template <class T>
inline T* allocate(int32_t count, const char* location) {
    if (count < 0) panic("cannot allocate a negative number of elements", location);
    if (static_cast<size_t>(count) > SIZE_MAX / sizeof(T)) panic("allocation is too large", location);
    return static_cast<T*>(::operator new(sizeof(T) * static_cast<size_t>(count), std::align_val_t(alignof(T))));
}

template <class T>
inline void free(T* data) {
    ::operator delete(static_cast<void*>(data), std::align_val_t(alignof(T)));
}

template <class T>
inline void initialize(T* place, T value) {
    ::new (static_cast<void*>(place)) T(std::move(value));
}

template <class T>
inline void initializeDefault(T* data, int32_t count) {
    for (int32_t i = 0; i < count; ++i) ::new (static_cast<void*>(data + i)) T(default_value<T>());
}

template <class T>
inline void drop(T* place) {
    place->~T();
}

// Moves count values into uninitialized storage, leaving the source uninitialized. The ranges may
// overlap: every step constructs into storage that is outside the source or was just vacated.
template <class T>
inline void moveTo(T* from, T* to, int32_t count) {
    if (to < from) {
        for (int32_t i = 0; i < count; ++i) {
            ::new (static_cast<void*>(to + i)) T(std::move(from[i]));
            from[i].~T();
        }
    } else if (to > from) {
        for (int32_t i = count - 1; i >= 0; --i) {
            ::new (static_cast<void*>(to + i)) T(std::move(from[i]));
            from[i].~T();
        }
    }
}

inline void collectCycles(const char* location) { cycles.collect(SIZE_MAX, location); }

inline void collectCycles(int32_t maxVisits, const char* location) {
    if (maxVisits < 0) panic("cannot visit a negative number of objects", location);
    cycles.collect(static_cast<size_t>(maxVisits), location);
}

inline void takeCycleStats(int32_t& buffered, int32_t& visited, int32_t& freed) {
    auto clamp = [](size_t count) { return static_cast<int32_t>(count < INT32_MAX ? count : INT32_MAX); };
    CycleStats stats = cycles.take_stats();
    buffered = clamp(stats.buffered);
    visited = clamp(stats.visited);
    freed = clamp(stats.freed);
}

}  // namespace memory

namespace bounds {

inline void check(int32_t index, int32_t length, const char* location) {
    if (static_cast<uint32_t>(index) >= static_cast<uint32_t>(length)) panic_index(index, length, location);
}

inline void checkRange(int32_t start, int32_t count, int32_t length, const char* location) {
    if (start < 0 || count < 0 || static_cast<int64_t>(start) + count > length) {
        char message[128];
        std::snprintf(message, sizeof message, "range of %" PRId32 " from %" PRId32 " is out of bounds for length %" PRId32, count, start,
                      length);
        panic(message, location);
    }
}

}  // namespace bounds

inline void print(int32_t value) { std::printf("%" PRId32 "\n", value); }
inline void print(int64_t value) { std::printf("%" PRId64 "\n", value); }
inline void print(bool value) { std::puts(value ? "true" : "false"); }

inline void print(String value) {
    std::fwrite(value.data, 1, static_cast<size_t>(value.length), stdout);
    std::fputc('\n', stdout);
}

template <class F>
inline void print_float(F value) {
    char buffer[64];
    auto result = std::to_chars(buffer, buffer + sizeof buffer, value);
    size_t length = static_cast<size_t>(result.ptr - buffer);
    std::fwrite(buffer, 1, length, stdout);
    // Whole numbers print as "3.0", like Java; "inf" and "nan" are left alone.
    if (std::memchr(buffer, '.', length) == nullptr && std::memchr(buffer, 'e', length) == nullptr &&
        std::memchr(buffer, 'n', length) == nullptr) {
        std::fputs(".0", stdout);
    }
    std::fputc('\n', stdout);
}

inline void print(float value) { print_float(value); }
inline void print(double value) { print_float(value); }

template <class T>
inline void print(const Optional<T>& value) {
    if (value.has_value()) {
        print(value.assumed());
    } else {
        std::puts("null");
    }
}

}  // namespace vpl
