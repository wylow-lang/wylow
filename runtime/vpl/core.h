#pragma once

#include <array>
#include <cfloat>
#include <charconv>
#include <cinttypes>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <memory>
#include <new>
#include <tuple>
#include <type_traits>
#include <utility>
#include <vector>

#ifndef VPL_DEBUG
#define VPL_DEBUG 0
#endif

static_assert(std::numeric_limits<float>::is_iec559 && std::numeric_limits<double>::is_iec559,
              "VPL requires IEEE 754 floating point");

namespace vpl {

[[noreturn]] inline void panic(const char* message, const char* location) {
    std::fflush(stdout);
    std::fprintf(stderr, "panic: %s\n    at %s\n", message, location);
    std::fflush(stderr);
    std::abort();
}

// An immutable, null-terminated UTF-8 string. A literal points at static bytes and owns nothing, so
// literals never allocate; a built string shares one counted block among its copies.
// The count is non-atomic like objects' (object.h). String is a shared class, so it must become
// atomic once threads exist.
class String {
public:
    struct Block {
        size_t count;
        // The bytes follow the header, then a null terminator.
        char* bytes() { return reinterpret_cast<char*>(this + 1); }
    };

    String() : String("", 0) {}
    String(const char* data, int32_t length) : data_(data), length_(length), block_(nullptr) {}

    // Takes over a block holding one count, with length bytes and their terminator written.
    static String adopt(Block* block, int32_t length) { return String(block->bytes(), length, block); }

    String(const String& other) : data_(other.data_), length_(other.length_), block_(other.block_) {
        if (block_ != nullptr) ++block_->count;
    }
    String(String&& other) noexcept : data_(other.data_), length_(other.length_), block_(other.block_) {
        other.block_ = nullptr;
    }
    ~String() { release(); }

    String& operator=(const String& other) {
        String copy(other);
        return *this = std::move(copy);
    }
    String& operator=(String&& other) noexcept {
        if (this != &other) {
            release();
            data_ = other.data_;
            length_ = other.length_;
            block_ = other.block_;
            other.block_ = nullptr;
        }
        return *this;
    }

    const char* data() const { return data_; }
    int32_t length() const { return length_; }

    friend bool operator==(const String& a, const String& b) {
        return a.length_ == b.length_ && (a.data_ == b.data_ || std::memcmp(a.data_, b.data_, static_cast<size_t>(a.length_)) == 0);
    }

private:
    String(const char* data, int32_t length, Block* block) : data_(data), length_(length), block_(block) {}

    void release() {
        if (block_ != nullptr && --block_->count == 0) ::operator delete(block_);
    }

    const char* data_;
    int32_t length_;
    Block* block_;
};

[[noreturn]] inline void panic(const String& message, const char* location) {
    std::fflush(stdout);
    std::fprintf(stderr, "panic: %.*s\n    at %s\n", static_cast<int>(message.length()), message.data(), location);
    std::fflush(stderr);
    std::abort();
}

// Reported in debug builds, when the program works but could be faster.
inline void performance_warning(const char* message) {
    std::fflush(stdout);
    std::fprintf(stderr, "performance warning: %s\n", message);
    std::fflush(stderr);
}

// Flow analysis rules this out; reaching it means a compiler bug, which must still not be UB.
[[noreturn]] inline void unreachable(const char* location) {
    panic("reached the end of a non-void function", location);
}

// Exhaustiveness checking rules this out too.
[[noreturn]] inline void no_match(const char* location) {
    panic("internal compiler error: no switch case matched", location);
}

// Selects the constructor that sets an enum's tag and leaves its payload to be constructed.
struct RawTag {};

inline void init() {
#if defined(_WIN32) && defined(_WRITE_ABORT_MSG)
    // Abort quietly instead of showing a dialog or invoking Windows Error Reporting.
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
#endif
}

template <class T>
struct is_std_array : std::false_type {};
template <class T, size_t N>
struct is_std_array<std::array<T, N>> : std::true_type {};

// The VPL default value of T, for generic code: struct field initializers run via make_default.
template <class T>
inline T default_value() {
    if constexpr (requires { T::make_default(); }) {
        return T::make_default();
    } else if constexpr (is_std_array<T>::value) {
        T result{};
        for (auto& element : result) element = default_value<typename T::value_type>();
        return result;
    } else {
        return T{};
    }
}

[[noreturn]] inline void panic_index(int64_t index, int64_t length, const char* location) {
    char message[96];
    std::snprintf(message, sizeof message, "index %" PRId64 " is out of bounds for length %" PRId64, index, length);
    panic(message, location);
}

// Fixed array indexing. The index may be given as a function, called after the array is found,
// so that the element stays a place while VPL's left-to-right order is kept.
template <class A, class I>
inline decltype(auto) at(A&& array, I index, const char* location) {
    int32_t i;
    if constexpr (std::is_invocable_v<I>) {
        i = index();
    } else {
        i = index;
    }
    constexpr size_t length = std::tuple_size_v<std::remove_cvref_t<A>>;
    if (static_cast<uint32_t>(i) >= length) panic_index(i, static_cast<int64_t>(length), location);
    return array[static_cast<size_t>(i)];
}

template <class T, size_t N>
constexpr int32_t length(const std::array<T, N>&) {
    return static_cast<int32_t>(N);
}

}  // namespace vpl
