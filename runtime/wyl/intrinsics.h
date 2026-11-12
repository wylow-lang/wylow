#pragma once

#include <string>

#include "cycles.h"
#include "strings.h"

#if defined(_WIN32)
// argv is in the ANSI code page on Windows, so the arguments are read from the UTF-16 command line.
// Declared here rather than by <windows.h>, whose macros (min, max, ...) would reach every program.
extern "C" __declspec(dllimport) wchar_t* __stdcall GetCommandLineW();
extern "C" __declspec(dllimport) wchar_t** __stdcall CommandLineToArgvW(const wchar_t* line, int* count);
extern "C" __declspec(dllimport) void* __stdcall LocalFree(void* memory);
#if defined(_MSC_VER)
#pragma comment(lib, "shell32.lib")
#endif
#endif

namespace wyl {

// The program's arguments (spec programs.entry): what main(String[] args) receives, without the
// program's name, as UTF-8 with each ill-formed part replaced by U+FFFD.
namespace detail {

inline std::vector<String>& ProgramArguments() {
    static std::vector<String> arguments;
    return arguments;
}

inline void AppendCodePoint(std::string& out, uint32_t c) {
    if (c < 0x80) {
        out += static_cast<char>(c);
    } else if (c < 0x800) {
        out += static_cast<char>(0xC0 | (c >> 6));
        out += static_cast<char>(0x80 | (c & 0x3F));
    } else if (c < 0x10000) {
        out += static_cast<char>(0xE0 | (c >> 12));
        out += static_cast<char>(0x80 | ((c >> 6) & 0x3F));
        out += static_cast<char>(0x80 | (c & 0x3F));
    } else {
        out += static_cast<char>(0xF0 | (c >> 18));
        out += static_cast<char>(0x80 | ((c >> 12) & 0x3F));
        out += static_cast<char>(0x80 | ((c >> 6) & 0x3F));
        out += static_cast<char>(0x80 | (c & 0x3F));
    }
}

// Bytes as UTF-8, each maximal subpart of an ill-formed sequence replaced by one U+FFFD, the
// practice Unicode recommends (The Unicode Standard, §3.9, "U+FFFD Substitution of Maximal
// Subparts"), which .NET's decoder, and so the interpreter, follows too.
inline std::string WellFormed(const char* text, size_t length) {
    std::string out;
    size_t i = 0;
    while (i < length) {
        auto lead = static_cast<unsigned char>(text[i]);
        if (lead < 0x80) {
            out += static_cast<char>(lead);
            i++;
            continue;
        }
        int needed = 0;
        unsigned char low = 0x80, high = 0xBF;
        if (lead >= 0xC2 && lead <= 0xDF) needed = 1;
        else if (lead == 0xE0) needed = 2, low = 0xA0;
        else if ((lead >= 0xE1 && lead <= 0xEC) || lead == 0xEE || lead == 0xEF) needed = 2;
        else if (lead == 0xED) needed = 2, high = 0x9F;
        else if (lead == 0xF0) needed = 3, low = 0x90;
        else if (lead >= 0xF1 && lead <= 0xF3) needed = 3;
        else if (lead == 0xF4) needed = 3, high = 0x8F;
        size_t end = i + 1;
        int found = 0;
        while (found < needed && end < length) {
            auto next = static_cast<unsigned char>(text[end]);
            if (next < low || next > high) break;
            low = 0x80, high = 0xBF;
            end++, found++;
        }
        if (needed > 0 && found == needed) out.append(text + i, end - i);
        else out += "\xEF\xBF\xBD";
        i = end;
    }
    return out;
}

inline String MakeString(const std::string& bytes) {
    StringBuffer buffer;
    buffer.Append(bytes.data(), bytes.size(), "program arguments");
    return buffer.Take();
}

#if defined(_WIN32)
// UTF-16 as UTF-8, each unpaired surrogate replaced by U+FFFD.
inline std::string FromUtf16(const wchar_t* text) {
    std::string out;
    for (size_t i = 0; text[i] != 0; i++) {
        uint32_t c = static_cast<uint16_t>(text[i]);
        if (c >= 0xD800 && c <= 0xDBFF && text[i + 1] >= 0xDC00 && text[i + 1] <= 0xDFFF) {
            c = 0x10000 + ((c - 0xD800) << 10) + (static_cast<uint16_t>(text[++i]) - 0xDC00);
        } else if (c >= 0xD800 && c <= 0xDFFF) {
            c = 0xFFFD;
        }
        AppendCodePoint(out, c);
    }
    return out;
}
#endif

}  // namespace detail

// Called by the emitted C++ main before anything else runs, when main takes the arguments.
inline void SetArguments([[maybe_unused]] int argc, [[maybe_unused]] char** argv) {
    auto& arguments = detail::ProgramArguments();
#if defined(_WIN32)
    int count = 0;
    wchar_t** wide = CommandLineToArgvW(GetCommandLineW(), &count);
    if (wide == nullptr) return;
    for (int i = 1; i < count; i++) arguments.push_back(detail::MakeString(detail::FromUtf16(wide[i])));
    LocalFree(wide);
#else
    for (int i = 1; i < argc; i++) arguments.push_back(detail::MakeString(detail::WellFormed(argv[i], std::strlen(argv[i]))));
#endif
}

inline int32_t argumentCount() { return static_cast<int32_t>(detail::ProgramArguments().size()); }

inline String argument(int32_t index) { return detail::ProgramArguments()[static_cast<size_t>(index)]; }

// The C++ side of extern functions in the standard library: wyl::memory is Memory, and wyl::bounds
// is Bounds. Memory's functions are unsafe; misusing them is undefined behavior.

namespace memory {

// Uninitialized storage for count values of T.
template <class T>
inline T* allocate(int32_t count, const char* location) {
    if (count < 0) RaisePanic("cannot allocate a negative number of elements", location);
    if (static_cast<size_t>(count) > SIZE_MAX / sizeof(T)) RaisePanic("allocation is too large", location);
    return static_cast<T*>(::operator new(sizeof(T) * static_cast<size_t>(count), std::align_val_t(alignof(T))));
}

// The size in bytes of count values of T.
template <class T>
inline int32_t byteLength(int32_t count, const char* location) {
    uint64_t size = static_cast<uint64_t>(count) * sizeof(T);
    if (size > INT32_MAX) RaisePanic("byte length is too large", location);
    return static_cast<int32_t>(size);
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
    for (int32_t i = 0; i < count; ++i) ::new (static_cast<void*>(data + i)) T(DefaultValue<T>());
}

template <class T>
inline void drop(T* place) {
    place->~T();
}

// Moves the value at place out, leaving its storage uninitialized.
template <class T>
inline T moveOut(T* place) {
    T value(std::move(*place));
    place->~T();
    return value;
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

// The standard library's ReadOnlySpan is complete only after the program defines it, so compare and
// equal are templates, which read its fields when they are instantiated. Each returns before
// memcmp when a span is empty, because an empty span's pointer may be null, and memcmp is undefined
// for a null pointer even with a count of 0.
template <class T>
inline int32_t compare(ReadOnlySpan<T> a, ReadOnlySpan<T> b) {
    static_assert(sizeof(T) == 1, "memory::compare compares bytes");
    size_t common = static_cast<size_t>(a.f_size < b.f_size ? a.f_size : b.f_size);
    if (common > 0) {
        int order = std::memcmp(a.f_elements, b.f_elements, common);
        if (order != 0) return order < 0 ? -1 : 1;
    }
    return a.f_size < b.f_size ? -1 : a.f_size > b.f_size ? 1 : 0;
}

template <class T>
inline bool equal(ReadOnlySpan<T> a, ReadOnlySpan<T> b) {
    static_assert(sizeof(T) == 1, "memory::equal compares bytes");
    if (a.f_size != b.f_size) return false;
    return a.f_size == 0 || std::memcmp(a.f_elements, b.f_elements, static_cast<size_t>(a.f_size)) == 0;
}

inline void collectCycles(const char* location) { CurrentCycles().Collect(SIZE_MAX, location); }

inline void collectCycles(int32_t maxVisits, const char* location) {
    if (maxVisits < 0) RaisePanic("cannot visit a negative number of objects", location);
    CurrentCycles().Collect(static_cast<size_t>(maxVisits), location);
}

inline void takeCycleStats(int32_t& buffered, int32_t& visited, int32_t& freed) {
    auto clamp = [](size_t count) { return static_cast<int32_t>(count < INT32_MAX ? count : INT32_MAX); };
    CycleStats stats = CurrentCycles().TakeStats();
    buffered = clamp(stats.Buffered);
    visited = clamp(stats.Visited);
    freed = clamp(stats.Freed);
}

}  // namespace memory

// Taking a value out of a place (spec library.replace). Nothing is released: the value moves out
// and the new one moves in, so no destructor runs.
template <class T>
inline void CheckNotInUse(const T& place, const char* message, const char* location) {
    if (AnyInUse(place)) RaisePanic(message, location);
}

template <class T>
inline T replace(T& place, T value, const char* location) {
    CheckNotInUse(place, "cannot replace a value that is in use", location);
    T previous(std::move(place));
    place = std::move(value);
    return previous;
}

template <class T>
inline void swap(T& a, T& b, const char* location) {
    CheckNotInUse(a, "cannot swap a value that is in use", location);
    CheckNotInUse(b, "cannot swap a value that is in use", location);
    if (std::addressof(a) == std::addressof(b)) return;
    using std::swap;
    swap(a, b);
}

template <class T>
inline Nullable<T> take(Nullable<T>& place, const char* location) {
    CheckNotInUse(place, "cannot take a value that is in use", location);
    Nullable<T> taken(std::move(place));
    // A trivial move copies, so the place is cleared explicitly. The value in it moved into
    // `taken`, so nothing is released.
    place = Nullable<T>();
    return taken;
}

// std::panic (spec library.panic): panic itself and the checks built-in arrays make.
namespace panic {

[[noreturn]] inline void panic(const String& message, const char* location) { RaisePanic(message, location); }

inline void checkIndex(int32_t index, int32_t length, const char* location) {
    if (static_cast<uint32_t>(index) >= static_cast<uint32_t>(length)) PanicIndex(index, length, location);
}

inline void checkRange(int32_t start, int32_t count, int32_t length, const char* location) {
    if (start < 0 || count < 0 || static_cast<int64_t>(start) + count > length) {
        char message[128];
        std::snprintf(message, sizeof message, "range of %" PRId32 " from %" PRId32 " is out of bounds for length %" PRId32, count, start,
                      length);
        RaisePanic(message, location);
    }
}

}  // namespace panic

template <class T>
    requires requires(FormatBuffer& buffer, T value) { Format(buffer, value); }
inline void print(T value) {
    FormatBuffer buffer;
    size_t length = Format(buffer, value);
    buffer[length] = '\n';
    std::fwrite(buffer, 1, length + 1, stdout);
}

inline void print(const String& value) {
    std::fwrite(value.Data(), 1, static_cast<size_t>(value.Length()), stdout);
    std::fputc('\n', stdout);
}

template <class T>
inline void print(const Optional<T>& value) {
    if (value.HasValue()) {
        print(value.Assumed());
    } else {
        std::puts("null");
    }
}

}  // namespace wyl
