#pragma once

#include <array>
#include <bit>
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

// The standard library's ReadOnlySpan, which memory::compare, memory::equal, and String.copyOf
// read. The runtime names it, and the emitter defines it under this name (TypeSymbol.RuntimeName).
template <class T>
struct ReadOnlySpan;

// A unique struct with a destructor keeps its state in one byte: whether it still holds a value,
// which a move or a discard takes away, and whether a reference into it is live (spec memory.holds).
inline constexpr uint8_t ENGAGED = 1;
inline constexpr uint8_t IN_USE = 2;

template <class T>
struct ReleaseAtEnd {
    T& Value;

    ~ReleaseAtEnd() { [[maybe_unused]] T released = std::move(Value); }
};

inline std::atomic<bool> s_Panicking{false};

[[noreturn]] inline void RaisePanic(const char* message, const char* location) {
    s_Panicking.store(true, std::memory_order_relaxed);
    std::fflush(stdout);
    std::fprintf(stderr, "panic: %s\n    at %s\n", message, location);
    std::fflush(stderr);
    std::abort();
}

template <class T>
class Optional;

// An immutable, null-terminated UTF-8 string. A literal points at static bytes and owns nothing, so
// literals never allocate; a built string shares one counted block among its copies.
// The camelCase members are String's extern methods in Wylow (string.wyl), whose other methods are
// written in Wylow on top of them.
class String {
public:
    struct Block {
        std::atomic<size_t> Count;
        // The bytes follow the header, then a null terminator.
        char* Bytes() { return reinterpret_cast<char*>(this + 1); }
    };

    String() : String("", 0) {}
    String(const char* data, int32_t length) : m_Data(data), m_Length(length), m_Block(nullptr) {}

    // Takes over a block holding one count, with length bytes and their terminator written.
    static String Adopt(Block* block, int32_t length) { return String(block->Bytes(), length, block); }

    String(const String& other) : m_Data(other.m_Data), m_Length(other.m_Length), m_Block(other.m_Block) {
        if (m_Block != nullptr) ++m_Block->Count;
    }
    String(String&& other) noexcept : m_Data(other.m_Data), m_Length(other.m_Length), m_Block(other.m_Block) {
        other.m_Block = nullptr;
    }
    ~String() { Release(); }

    String& operator=(const String& other) {
        String copy(other);
        return *this = std::move(copy);
    }
    String& operator=(String&& other) noexcept {
        if (this != &other) {
            Release();
            m_Data = other.m_Data;
            m_Length = other.m_Length;
            m_Block = other.m_Block;
            other.m_Block = nullptr;
        }
        return *this;
    }

    const char* Data() const { return m_Data; }
    int32_t Length() const { return m_Length; }

    // A byte after the first of a character's UTF-8 encoding.
    static bool IsContinuation(char byte) { return (static_cast<unsigned char>(byte) & 0xC0) == 0x80; }

    friend bool operator==(const String& a, const String& b) {
        return a.m_Length == b.m_Length && (a.m_Data == b.m_Data || std::memcmp(a.m_Data, b.m_Data, static_cast<size_t>(a.m_Length)) == 0);
    }

    int32_t byteLength() const { return m_Length; }

    // Wylow reads the bytes through a byte*, and the spec makes writing through it undefined
    // behavior, so a literal's static bytes are never written.
    uint8_t* cstr() const { return reinterpret_cast<uint8_t*>(const_cast<char*>(m_Data)); }

    String toString() const { return *this; }

private:
    String(const char* data, int32_t length, Block* block) : m_Data(data), m_Length(length), m_Block(block) {}

    void Release() {
        if (m_Block != nullptr && --m_Block->Count == 0) ::operator delete(m_Block);
    }

    const char* m_Data;
    int32_t m_Length;
    Block* m_Block;
};

[[noreturn]] inline void RaisePanic(const String& message, const char* location) {
    s_Panicking.store(true, std::memory_order_relaxed);
    std::fflush(stdout);
    std::fprintf(stderr, "panic: %.*s\n    at %s\n", static_cast<int>(message.Length()), message.Data(), location);
    std::fflush(stderr);
    std::abort();
}

// Reported in debug builds, when the program works but could be faster.
inline void PerformanceWarning(const char* message) {
    std::fflush(stdout);
    std::fprintf(stderr, "performance warning: %s\n", message);
    std::fflush(stderr);
}

// Flow analysis rules this out; reaching it means a compiler bug, which must still not be UB.
[[noreturn]] inline void Unreachable(const char* location) {
    RaisePanic("reached the end of a non-void function", location);
}

// Exhaustiveness checking rules this out too.
[[noreturn]] inline void NoMatch(const char* location) {
    RaisePanic("internal compiler error: no switch case matched", location);
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
template <class... TRest>
struct Arguments {};

template <class T, class... TRest>
struct Arguments<T, TRest...> {
    T First;
    Arguments<TRest...> Rest;

    explicit Arguments(T value, TRest... restValues) : First(std::move(value)), Rest(std::move(restValues)...) {}
};

template <class... T>
Arguments(T...) -> Arguments<T...>;

template <size_t I, class T, class... TRest>
const auto& Argument(const Arguments<T, TRest...>& arguments) {
    if constexpr (I == 0)
        return arguments.First;
    else
        return Argument<I - 1>(arguments.Rest);
}

// A delegating constructor moves its arguments into the constructor it calls, since a unique
// value cannot be copied.
template <size_t I, class T, class... TRest>
auto& Argument(Arguments<T, TRest...>& arguments) {
    if constexpr (I == 0)
        return arguments.First;
    else
        return Argument<I - 1>(arguments.Rest);
}

void MarkMainThread();

inline void Init() {
    MarkMainThread();
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
struct IsStdArray : std::false_type {};
template <class T, size_t N>
struct IsStdArray<std::array<T, N>> : std::true_type {};

// The Wylow default value of T, for generic code: struct field initializers run via make_default.
template <class T>
inline T DefaultValue() {
    if constexpr (requires { T::make_default(); }) {
        return T::make_default();
    } else if constexpr (IsStdArray<T>::value) {
        T result{};
        for (auto& element : result) element = DefaultValue<typename T::value_type>();
        return result;
    } else {
        return T{};
    }
}

// Clears the state of every value in a place, so that zeroed storage holds nothing a destructor
// may run on. A fixed array's elements and a unique struct's fields are cleared recursively.
template <class T>
inline void Disengage(T& value) {
    if constexpr (requires { value.Disengage(); }) value.Disengage();
}

template <class T, size_t N>
inline void Disengage(std::array<T, N>& value) {
    for (auto& element : value) Disengage(element);
}

// Zeroed storage of T, which a constructor starts from.
template <class T>
inline T Zeroed() {
    T value{};
    Disengage(value);
    return value;
}

// A value whose field initializers have run, but which is not a value of its type yet.
template <class T>
inline T Disengaged(T value) {
    if constexpr (requires { value.DisengageSelf(); }) value.DisengageSelf();
    return value;
}

// Whether a value, or one in its fields, is in use (spec memory.holds). Only a unique struct with
// a destructor, an enum with data, and a nullable of a non-copyable value record it.
template <class T>
inline bool AnyInUse(const T& value) {
    if constexpr (requires { value.AnyInUse(); }) return value.AnyInUse();
    else return false;
}

template <class T, size_t N>
inline bool AnyInUse(const std::array<T, N>& value) {
    for (const auto& element : value) {
        if (AnyInUse(element)) return true;
    }
    return false;
}

template <class T>
inline bool GetInUse(const T& value) {
    if constexpr (requires { value.GetInUse(); }) return value.GetInUse();
    else return false;
}

template <class T>
inline void SetInUse(T& value, bool on) {
    if constexpr (requires { value.SetInUse(on); }) value.SetInUse(on);
}

// Marks a value in use while it lives, restoring the state the value had before.
class InUseMark {
    const void* m_Value = nullptr;
    bool m_Previous = false;
    void (*m_Restore)(const void*, bool) = nullptr;

public:
    InUseMark() = default;
    template <class T>
    explicit InUseMark(T& value) { Begin(value); }
    template <class T>
    explicit InUseMark(T* value) { Begin(value); }
    InUseMark(const InUseMark&) = delete;
    InUseMark& operator=(const InUseMark&) = delete;
    ~InUseMark() { End(); }

    template <class T>
    void Begin(T& value) {
        End();
        m_Value = &value;
        m_Previous = GetInUse(value);
        m_Restore = [](const void* p, bool before) {
            SetInUse(*const_cast<T*>(static_cast<const T*>(p)), before);
        };
        SetInUse(value, true);
    }

    template <class T>
    void Begin(T* value) {
        if (value != nullptr) Begin(*value);
    }

    void End() {
        if (m_Value != nullptr) {
            m_Restore(m_Value, m_Previous);
            m_Value = nullptr;
            m_Restore = nullptr;
        }
    }
};

[[noreturn]] inline void PanicIndex(int64_t index, int64_t length, const char* location) {
    char message[96];
    std::snprintf(message, sizeof message, "index %" PRId64 " is out of bounds for length %" PRId64, index, length);
    RaisePanic(message, location);
}

// Fixed array indexing. The index may be given as a function, called after the array is found,
// so that the element stays a place while Wylow's left-to-right order is kept.
template <class A, class I>
inline decltype(auto) At(A&& array, I index, const char* location) {
    int32_t i;
    if constexpr (std::is_invocable_v<I>) {
        i = index();
    } else {
        i = index;
    }
    constexpr size_t length = std::tuple_size_v<std::remove_cvref_t<A>>;
    if (static_cast<uint32_t>(i) >= length) PanicIndex(i, static_cast<int64_t>(length), location);
    return array[static_cast<size_t>(i)];
}

template <class T, size_t N>
constexpr int32_t Length(const std::array<T, N>&) {
    return static_cast<int32_t>(N);
}

// A 'ref?' or 'in?' is a pointer to the place it refers to, or null. A temporary passed to an 'in?'
// parameter lives until the end of the caller's full expression, which includes the call.
template <class T>
inline std::remove_reference_t<T>* AddressOf(T&& place) {
    return std::addressof(place);
}

template <class T>
inline T* Unwrap(T* reference, const char* location) {
    if (reference == nullptr) RaisePanic("null reference unwrapped with '!'", location);
    return reference;
}

// A check printed apart from what it guards, where it can't be part of the expression using it.
inline void CheckIndex(int32_t index, int32_t length, const char* location) {
    if (static_cast<uint32_t>(index) >= static_cast<uint32_t>(length)) PanicIndex(index, length, location);
}

template <class T>
inline void CheckNotNull(const T* reference, const char* location) {
    if (reference == nullptr) RaisePanic("null reference unwrapped with '!'", location);
}

}  // namespace wyl
