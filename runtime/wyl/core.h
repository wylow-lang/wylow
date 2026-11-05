#pragma once

#include <array>
#include <atomic>
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

#if defined(_WIN32)
#include <fcntl.h>
#include <io.h>
#endif

#ifndef WYL_DEBUG
#define WYL_DEBUG 0
#endif

static_assert(std::numeric_limits<float>::is_iec559 && std::numeric_limits<double>::is_iec559,
              "Wylow requires IEEE 754 floating point");

namespace wyl {

// The standard library's ReadOnlySpan, which String::bytes returns. The runtime names it, and the
// emitter defines it under this name (TypeSymbol.RuntimeName).
template <class t_T>
struct ReadOnlySpan;

// A unique struct with a destructor keeps its state in one byte: whether it still holds a value,
// which a move or a discard takes away, and whether a reference into it is live (spec memory.holds).
inline constexpr uint8_t k_engaged = 1;
inline constexpr uint8_t k_in_use = 2;

template <class t_T>
struct ReleaseAtEnd {
    t_T& value;

    ~ReleaseAtEnd() { [[maybe_unused]] t_T released = std::move(value); }
};

[[noreturn]] inline void raise_panic(const char* message, const char* location) {
    std::fflush(stdout);
    std::fprintf(stderr, "panic: %s\n    at %s\n", message, location);
    std::fflush(stderr);
    std::abort();
}

template <class T>
class Optional;

// An immutable, null-terminated UTF-8 string. A literal points at static bytes and owns nothing, so
// literals never allocate; a built string shares one counted block among its copies.
// The camelCase members are String's methods in Wylow (String.wyl); indexOf, contains, and slice are
// defined in strings.h.
class String {
public:
    struct Block {
        std::atomic<size_t> count;
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

    // A byte after the first of a character's UTF-8 encoding.
    static bool is_continuation(char byte) { return (static_cast<unsigned char>(byte) & 0xC0) == 0x80; }

    friend bool operator==(const String& a, const String& b) {
        return a.length_ == b.length_ && (a.data_ == b.data_ || std::memcmp(a.data_, b.data_, static_cast<size_t>(a.length_)) == 0);
    }

    int32_t byteLength() const { return length_; }
    bool isEmpty() const { return length_ == 0; }

    // A template so that the span is a complete type by the time a program instantiates it. The
    // span is read-only, so a literal's static bytes are never written.
    template <class Byte = uint8_t>
    wyl::ReadOnlySpan<Byte> bytes() const {
        return wyl::ReadOnlySpan<Byte>::make(reinterpret_cast<Byte*>(const_cast<char*>(data_)), length_);
    }

    int32_t charCount() const {
        int32_t count = 0;
        for (int32_t i = 0; i < length_; i++) {
            if (!is_continuation(data_[i])) count++;
        }
        return count;
    }

    bool startsWith(const String& prefix) const {
        return prefix.length_ <= length_ && std::memcmp(data_, prefix.data_, static_cast<size_t>(prefix.length_)) == 0;
    }

    bool endsWith(const String& suffix) const {
        return suffix.length_ <= length_ &&
               std::memcmp(data_ + (length_ - suffix.length_), suffix.data_, static_cast<size_t>(suffix.length_)) == 0;
    }

    bool contains(const String& text) const;
    Optional<int32_t> indexOf(const String& text) const;
    String slice(int32_t start, int32_t count, const char* location) const;

    // A string is always valid UTF-8, and the compiler passes only offsets where a character starts.
    char32_t charAt(int32_t offset) const {
        auto byte = [&](int32_t i) { return static_cast<uint32_t>(static_cast<unsigned char>(data_[offset + i])); };
        uint32_t lead = byte(0);
        if (lead < 0x80) return static_cast<char32_t>(lead);
        if (lead < 0xE0) return static_cast<char32_t>(((lead & 0x1F) << 6) | (byte(1) & 0x3F));
        if (lead < 0xF0) return static_cast<char32_t>(((lead & 0x0F) << 12) | ((byte(1) & 0x3F) << 6) | (byte(2) & 0x3F));
        return static_cast<char32_t>(((lead & 0x07) << 18) | ((byte(1) & 0x3F) << 12) | ((byte(2) & 0x3F) << 6) | (byte(3) & 0x3F));
    }

    int32_t nextChar(int32_t offset) const {
        auto lead = static_cast<unsigned char>(data_[offset]);
        return offset + (lead < 0x80 ? 1 : lead < 0xE0 ? 2 : lead < 0xF0 ? 3 : 4);
    }

    // memcmp compares bytes as unsigned, so UTF-8 sorts by code point.
    int32_t compareTo(const String& other) const {
        auto common = static_cast<size_t>(length_ < other.length_ ? length_ : other.length_);
        int order = std::memcmp(data_, other.data_, common);
        if (order == 0) order = length_ < other.length_ ? -1 : length_ > other.length_ ? 1 : 0;
        return order < 0 ? -1 : order > 0 ? 1 : 0;
    }

    String toString() const { return *this; }

private:
    String(const char* data, int32_t length, Block* block) : data_(data), length_(length), block_(block) {}

    // Whether a character starts at the byte offset, or it is the end.
    bool is_boundary(int32_t offset) const { return offset == length_ || !is_continuation(data_[offset]); }

    void release() {
        if (block_ != nullptr && --block_->count == 0) ::operator delete(block_);
    }

    const char* data_;
    int32_t length_;
    Block* block_;
};

[[noreturn]] inline void raise_panic(const String& message, const char* location) {
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
    raise_panic("reached the end of a non-void function", location);
}

// Exhaustiveness checking rules this out too.
[[noreturn]] inline void no_match(const char* location) {
    raise_panic("internal compiler error: no switch case matched", location);
}

// Selects the constructor that sets an enum's tag and leaves its payload to be constructed.
struct RawTag {};

// Selects the constructor that a class's Nth constructor delegates to with its base constructor's
// arguments, computed in order: MSVC doesn't always evaluate a braced mem-initializer left to right.
template <int N>
struct BaseArguments {};

// The arguments a delegating constructor passes on, by value or, for one passed by 'in' or 'ref',
// as the address of its place; unlike std::tuple's elements they are destroyed in a fixed order,
// the last first.
template <class... T>
struct Arguments {};

template <class T, class... Rest>
struct Arguments<T, Rest...> {
    T first;
    Arguments<Rest...> rest;

    explicit Arguments(T value, Rest... rest_values) : first(std::move(value)), rest(std::move(rest_values)...) {}
};

template <class... T>
Arguments(T...) -> Arguments<T...>;

template <size_t I, class T, class... Rest>
const auto& argument(const Arguments<T, Rest...>& arguments) {
    if constexpr (I == 0)
        return arguments.first;
    else
        return argument<I - 1>(arguments.rest);
}

// A delegating constructor moves its arguments into the constructor it calls, since a unique
// value cannot be copied.
template <size_t I, class T, class... Rest>
auto& argument(Arguments<T, Rest...>& arguments) {
    if constexpr (I == 0)
        return arguments.first;
    else
        return argument<I - 1>(arguments.rest);
}

inline void init() {
#if defined(_WIN32)
    // Output is the bytes the program writes, on every platform: in text mode, print's '\n' would
    // become "\r\n" and a program's own '\r' would merge with it.
    _setmode(_fileno(stdout), _O_BINARY);
#if defined(_WRITE_ABORT_MSG)
    // Abort quietly instead of showing a dialog or invoking Windows Error Reporting.
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
#endif
#endif
}

template <class T>
struct is_std_array : std::false_type {};
template <class T, size_t N>
struct is_std_array<std::array<T, N>> : std::true_type {};

// The Wylow default value of T, for generic code: struct field initializers run via make_default.
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

// Clears the state of every value in a place, so that zeroed storage holds nothing a destructor
// may run on. A fixed array's elements and a unique struct's fields are cleared recursively.
template <class T>
inline void disengage(T& value) {
    if constexpr (requires { value.wyl_disengage(); }) value.wyl_disengage();
}

template <class T, size_t N>
inline void disengage(std::array<T, N>& value) {
    for (auto& element : value) disengage(element);
}

// Zeroed storage of T, which a constructor starts from.
template <class T>
inline T zeroed() {
    T value{};
    disengage(value);
    return value;
}

// A value whose field initializers have run, but which is not a value of its type yet.
template <class T>
inline T disengaged(T value) {
    if constexpr (requires { value.wyl_disengage_self(); }) value.wyl_disengage_self();
    return value;
}

// Whether a value, or one in its fields, is in use (spec memory.holds). Only a unique struct with
// a destructor, an enum with data, and a nullable of a non-copyable value record it.
template <class T>
inline bool any_in_use(const T& value) {
    if constexpr (requires { value.wyl_any_in_use(); }) return value.wyl_any_in_use();
    else return false;
}

template <class T, size_t N>
inline bool any_in_use(const std::array<T, N>& value) {
    for (const auto& element : value) {
        if (any_in_use(element)) return true;
    }
    return false;
}

template <class T>
inline bool get_in_use(const T& value) {
    if constexpr (requires { value.wyl_get_in_use(); }) return value.wyl_get_in_use();
    else return false;
}

template <class T>
inline void set_in_use(T& value, bool on) {
    if constexpr (requires { value.wyl_set_in_use(on); }) value.wyl_set_in_use(on);
}

// Marks a value in use while it lives, restoring the state the value had before.
class InUseMark {
    const void* value_ = nullptr;
    bool previous_ = false;
    void (*restore_)(const void*, bool) = nullptr;

public:
    InUseMark() = default;
    template <class T>
    explicit InUseMark(T& value) { begin(value); }
    template <class T>
    explicit InUseMark(T* value) { begin(value); }
    InUseMark(const InUseMark&) = delete;
    InUseMark& operator=(const InUseMark&) = delete;
    ~InUseMark() { end(); }

    template <class T>
    void begin(T& value) {
        end();
        value_ = &value;
        previous_ = get_in_use(value);
        restore_ = [](const void* p, bool before) {
            set_in_use(*const_cast<T*>(static_cast<const T*>(p)), before);
        };
        set_in_use(value, true);
    }

    template <class T>
    void begin(T* value) {
        if (value != nullptr) begin(*value);
    }

    void end() {
        if (value_ != nullptr) {
            restore_(value_, previous_);
            value_ = nullptr;
            restore_ = nullptr;
        }
    }
};

[[noreturn]] inline void panic_index(int64_t index, int64_t length, const char* location) {
    char message[96];
    std::snprintf(message, sizeof message, "index %" PRId64 " is out of bounds for length %" PRId64, index, length);
    raise_panic(message, location);
}

// Fixed array indexing. The index may be given as a function, called after the array is found,
// so that the element stays a place while Wylow's left-to-right order is kept.
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

// A 'ref?' or 'in?' is a pointer to the place it refers to, or null. A temporary passed to an 'in?'
// parameter lives until the end of the caller's full expression, which includes the call.
template <class T>
inline std::remove_reference_t<T>* address_of(T&& place) {
    return std::addressof(place);
}

template <class T>
inline T* unwrap(T* reference, const char* location) {
    if (reference == nullptr) raise_panic("null reference unwrapped with '!'", location);
    return reference;
}

// A check printed apart from what it guards, where it can't be part of the expression using it.
inline void check_index(int32_t index, int32_t length, const char* location) {
    if (static_cast<uint32_t>(index) >= static_cast<uint32_t>(length)) panic_index(index, length, location);
}

template <class T>
inline void check_not_null(const T* reference, const char* location) {
    if (reference == nullptr) raise_panic("null reference unwrapped with '!'", location);
}

}  // namespace wyl
