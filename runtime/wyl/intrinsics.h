#pragma once

#include "cycles.h"
#include "strings.h"

namespace wyl {

// The C++ side of extern functions in the standard library: wyl::memory is Memory, and wyl::bounds
// is Bounds. Memory's functions are unsafe; misusing them is undefined behavior.

namespace memory {

// Uninitialized storage for count values of T.
template <class T>
inline T* allocate(int32_t count, const char* location) {
    if (count < 0) raise_panic("cannot allocate a negative number of elements", location);
    if (static_cast<size_t>(count) > SIZE_MAX / sizeof(T)) raise_panic("allocation is too large", location);
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
    if (maxVisits < 0) raise_panic("cannot visit a negative number of objects", location);
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

// std::panic (spec library.panic): panic itself and the checks built-in arrays make.
namespace panic {

[[noreturn]] inline void panic(const String& message, const char* location) { raise_panic(message, location); }

inline void checkIndex(int32_t index, int32_t length, const char* location) {
    if (static_cast<uint32_t>(index) >= static_cast<uint32_t>(length)) panic_index(index, length, location);
}

inline void checkRange(int32_t start, int32_t count, int32_t length, const char* location) {
    if (start < 0 || count < 0 || static_cast<int64_t>(start) + count > length) {
        char message[128];
        std::snprintf(message, sizeof message, "range of %" PRId32 " from %" PRId32 " is out of bounds for length %" PRId32, count, start,
                      length);
        raise_panic(message, location);
    }
}

}  // namespace panic

template <class T>
    requires requires(FormatBuffer& buffer, T value) { format(buffer, value); }
inline void print(T value) {
    FormatBuffer buffer;
    size_t length = format(buffer, value);
    buffer[length] = '\n';
    std::fwrite(buffer, 1, length + 1, stdout);
}

inline void print(const String& value) {
    std::fwrite(value.data(), 1, static_cast<size_t>(value.length()), stdout);
    std::fputc('\n', stdout);
}

template <class T>
inline void print(const Optional<T>& value) {
    if (value.has_value()) {
        print(value.assumed());
    } else {
        std::puts("null");
    }
}

}  // namespace wyl
