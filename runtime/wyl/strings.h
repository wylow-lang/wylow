#pragma once

#include "optional.h"

namespace wyl {

// Text for primitive values, shared by print and string interpolation so the two always agree.
// Every format fits in a FormatBuffer.
using FormatBuffer = char[64];

template <class I>
    requires std::is_integral_v<I>
inline size_t Format(FormatBuffer& buffer, I value) {
    auto result = std::to_chars(buffer, buffer + sizeof buffer, value);
    return static_cast<size_t>(result.ptr - buffer);
}

inline size_t Format(FormatBuffer& buffer, bool value) {
    const char* text = value ? "true" : "false";
    size_t length = std::strlen(text);
    std::memcpy(buffer, text, length);
    return length;
}

inline size_t Format(FormatBuffer& buffer, char32_t value) {
    auto c = static_cast<uint32_t>(value);
    if (c < 0x80) {
        buffer[0] = static_cast<char>(c);
        return 1;
    }
    if (c < 0x800) {
        buffer[0] = static_cast<char>(0xC0 | (c >> 6));
        buffer[1] = static_cast<char>(0x80 | (c & 0x3F));
        return 2;
    }
    if (c < 0x10000) {
        buffer[0] = static_cast<char>(0xE0 | (c >> 12));
        buffer[1] = static_cast<char>(0x80 | ((c >> 6) & 0x3F));
        buffer[2] = static_cast<char>(0x80 | (c & 0x3F));
        return 3;
    }
    buffer[0] = static_cast<char>(0xF0 | (c >> 18));
    buffer[1] = static_cast<char>(0x80 | ((c >> 12) & 0x3F));
    buffer[2] = static_cast<char>(0x80 | ((c >> 6) & 0x3F));
    buffer[3] = static_cast<char>(0x80 | (c & 0x3F));
    return 4;
}

// Every NaN is written "nan": C++ libraries differ on writing its sign and payload.
inline size_t FormatNan(char* buffer) {
    std::memcpy(buffer, "nan", 3);
    return 3;
}

template <class F>
    requires std::is_floating_point_v<F>
inline size_t Format(FormatBuffer& buffer, F value) {
    if (std::isnan(value)) return FormatNan(buffer);
    // Leaves room for the ".0" below and print's newline.
    auto result = std::to_chars(buffer, buffer + sizeof buffer - 3, value);
    auto length = static_cast<size_t>(result.ptr - buffer);
    // Whole numbers read as "3.0", like Java; "inf" and "nan" are left alone.
    if (std::memchr(buffer, '.', length) == nullptr && std::memchr(buffer, 'e', length) == nullptr &&
        std::memchr(buffer, 'n', length) == nullptr) {
        buffer[length++] = '.';
        buffer[length++] = '0';
    }
    return length;
}

// Text for a number with a format specifier ({x:F2}), which the binder has checked applies to its
// type. A precision is at most 99, so the longest, a double's F99, fits.
using SpecifiedBuffer = char[416];

template <class I>
inline constexpr bool IS_FORMATTED_INTEGER = std::is_integral_v<I> && !std::is_same_v<I, bool> && !std::is_same_v<I, char32_t>;

template <class I>
    requires IS_FORMATTED_INTEGER<I>
inline size_t Format(SpecifiedBuffer& buffer, I value, int32_t kind, int32_t precision) {
    using U = std::make_unsigned_t<I>;
    size_t length = 0;
    int base = 10;
    auto magnitude = static_cast<U>(value);
    if (kind == 'D' || kind == 'd') {
        if (value < 0) {
            buffer[length++] = '-';
            magnitude = static_cast<U>(U(0) - magnitude);
        }
    } else {
        // Hexadecimal and binary write a negative value's two's complement bits, as C# does.
        base = kind == 'B' || kind == 'b' ? 2 : 16;
    }
    char digits[64];
    auto count = static_cast<size_t>(std::to_chars(digits, digits + sizeof digits, magnitude, base).ptr - digits);
    if (precision > 0 && static_cast<size_t>(precision) > count) {
        std::memset(buffer + length, '0', static_cast<size_t>(precision) - count);
        length += static_cast<size_t>(precision) - count;
    }
    for (size_t i = 0; i < count; i++) {
        buffer[length++] = kind == 'X' && digits[i] >= 'a' ? static_cast<char>(digits[i] - 'a' + 'A') : digits[i];
    }
    return length;
}

template <class F>
    requires std::is_floating_point_v<F>
inline size_t Format(SpecifiedBuffer& buffer, F value, int32_t kind, int32_t precision) {
    if (std::isnan(value)) return FormatNan(buffer);
    bool exponential = kind == 'E' || kind == 'e';
    if (precision < 0) precision = exponential ? 6 : 2;
    auto style = exponential ? std::chars_format::scientific : std::chars_format::fixed;
    auto length = static_cast<size_t>(std::to_chars(buffer, buffer + sizeof buffer, value, style, precision).ptr - buffer);
    if (!exponential || !std::isfinite(value)) return length;
    // C# writes the exponent with its sign and at least three digits: 1.50E+003.
    auto e = static_cast<size_t>(static_cast<char*>(std::memchr(buffer, 'e', length)) - buffer);
    buffer[e] = static_cast<char>(kind);
    size_t digits = length - (e + 2);
    if (digits < 3) {
        std::memmove(buffer + e + 2 + (3 - digits), buffer + e + 2, digits);
        std::memset(buffer + e + 2, '0', 3 - digits);
        length += 3 - digits;
    }
    return length;
}

// A value interpolated with a format specifier or alignment, written in place by the StringBuffer
// building the string. kind is the specifier's letter or 0, precision -1 if not given, and a
// positive alignment pads on the left, a negative one on the right.
template <class T>
struct Formatted {
    T Value;
    int32_t Kind;
    int32_t Precision;
    int32_t Alignment;

    operator String() const;
};

template <class T>
inline Formatted<T> MakeFormatted(const T& value, int32_t kind, int32_t precision, int32_t alignment) {
    return {value, kind, precision, alignment};
}

// Builds a String in a block it grows, which the String then takes over without a copy.
class StringBuffer {
public:
    StringBuffer() = default;
    StringBuffer(const StringBuffer&) = delete;
    StringBuffer& operator=(const StringBuffer&) = delete;
    ~StringBuffer() { ::operator delete(m_Block); }

    void Append(const char* bytes, size_t count, const char* location) {
        // An empty buffer has no block to point into.
        if (count == 0) return;
        if (count > static_cast<size_t>(INT32_MAX) - m_Length) RaisePanic("string is too long", location);
        size_t needed = m_Length + count;
        if (needed > m_Capacity) Grow(needed);
        std::memcpy(m_Block->Bytes() + m_Length, bytes, count);
        m_Length = needed;
    }

    void Append(const String& value, const char* location) {
        Append(value.Data(), static_cast<size_t>(value.Length()), location);
    }

    template <class T>
    void Append(const Optional<T>& value, const char* location) {
        if (value.HasValue()) {
            Append(value.Assumed(), location);
        } else {
            Append("null", 4, location);
        }
    }

    template <class T>
        requires requires(FormatBuffer& buffer, T value) { Format(buffer, value); }
    void Append(T value, const char* location) {
        FormatBuffer buffer;
        Append(buffer, Format(buffer, value), location);
    }

    template <class T>
    void Append(const Formatted<T>& value, const char* location) {
        size_t start = m_Length;
        AppendSpecified(value.Value, value.Kind, value.Precision, location);
        if (value.Alignment != 0) Pad(start, value.Alignment, location);
    }

    String Take() {
        if (m_Block == nullptr) return String();
        m_Block->Bytes()[m_Length] = '\0';
        m_Block->Count = 1;
        auto* block = std::exchange(m_Block, nullptr);
        return String::Adopt(block, static_cast<int32_t>(std::exchange(m_Length, 0)));
    }

private:
    template <class T>
    void AppendSpecified(const T& value, int32_t kind, int32_t precision, const char* location) {
        if constexpr (IS_FORMATTED_INTEGER<T> || std::is_floating_point_v<T>) {
            if (kind != 0) {
                SpecifiedBuffer buffer;
                Append(buffer, Format(buffer, value, kind, precision), location);
                return;
            }
        }
        Append(value, location);
    }

    template <class T>
    void AppendSpecified(const Optional<T>& value, int32_t kind, int32_t precision, const char* location) {
        if (value.HasValue()) {
            AppendSpecified(value.Assumed(), kind, precision, location);
        } else {
            Append("null", 4, location);
        }
    }

    // Pads the text written since start with spaces to the alignment's width in characters.
    void Pad(size_t start, int32_t alignment, const char* location) {
        size_t characters = 0;
        for (size_t i = start; i < m_Length; i++) {
            if (!String::IsContinuation(m_Block->Bytes()[i])) characters++;
        }
        auto width = static_cast<size_t>(alignment < 0 ? -static_cast<int64_t>(alignment) : alignment);
        if (characters >= width) return;
        size_t count = width - characters;
        size_t end = m_Length;
        if (count > static_cast<size_t>(INT32_MAX) - m_Length) RaisePanic("string is too long", location);
        if (m_Length + count > m_Capacity) Grow(m_Length + count);
        char* bytes = m_Block->Bytes();
        if (alignment > 0) {
            std::memmove(bytes + start + count, bytes + start, end - start);
            std::memset(bytes + start, ' ', count);
        } else {
            std::memset(bytes + end, ' ', count);
        }
        m_Length = end + count;
    }

    void Grow(size_t needed) {
        size_t capacity = m_Capacity < 16 ? 16 : m_Capacity;
        while (capacity < needed) capacity = capacity <= static_cast<size_t>(INT32_MAX) / 2 ? capacity * 2 : static_cast<size_t>(INT32_MAX);
        // One more byte for the terminator Take() writes.
        auto* block = ::new (static_cast<void*>(::operator new(sizeof(String::Block) + capacity + 1))) String::Block{};
        if (m_Block != nullptr) {
            std::memcpy(block->Bytes(), m_Block->Bytes(), m_Length);
            ::operator delete(m_Block);
        }
        m_Block = block;
        m_Capacity = capacity;
    }

    String::Block* m_Block = nullptr;
    size_t m_Length = 0;
    size_t m_Capacity = 0;
};

inline Optional<int32_t> String::indexOf(const String& text) const {
    if (text.m_Length > m_Length) return nullptr;
    if (text.m_Length == 0) return 0;
    const char* last = m_Data + (m_Length - text.m_Length);
    for (const char* p = m_Data; p <= last; p++) {
        p = static_cast<const char*>(std::memchr(p, text.m_Data[0], static_cast<size_t>(last - p) + 1));
        if (p == nullptr) break;
        if (std::memcmp(p, text.m_Data, static_cast<size_t>(text.m_Length)) == 0) return static_cast<int32_t>(p - m_Data);
    }
    return nullptr;
}

inline bool String::contains(const String& text) const { return indexOf(text).HasValue(); }

inline String String::slice(int32_t start, int32_t count, const char* location) const {
    if (start < 0 || count < 0 || static_cast<int64_t>(start) + count > m_Length) {
        char message[128];
        std::snprintf(message, sizeof message, "byte range of %" PRId32 " from %" PRId32 " is out of bounds for length %" PRId32, count, start,
                      m_Length);
        RaisePanic(message, location);
    }
    for (int32_t offset : {start, start + count}) {
        if (!IsBoundary(offset)) {
            char message[96];
            std::snprintf(message, sizeof message, "byte offset %" PRId32 " is inside a character", offset);
            RaisePanic(message, location);
        }
    }
    if (count == m_Length) return *this;
    StringBuffer text;
    text.Append(m_Data + start, static_cast<size_t>(count), location);
    return text.Take();
}

template <class T>
inline Formatted<T>::operator String() const {
    StringBuffer text;
    // Only a huge alignment reaches the length limit, and a conversion has no location to report.
    text.Append(*this, "");
    return text.Take();
}

// The types written by Format, and their nullables.
template <class T>
inline constexpr bool IS_FORMATTED = std::is_same_v<T, String> || requires(FormatBuffer& buffer, T value) { Format(buffer, value); };
template <class T>
inline constexpr bool IS_FORMATTED<Optional<T>> = IS_FORMATTED<T>;

// toString() of a primitive or String.
template <class T>
    requires IS_FORMATTED<T>
inline String FormatString(const T& value) {
    if constexpr (std::is_same_v<T, String>) {
        return value;
    } else {
        StringBuffer text;
        // One formatted value is far too short to reach the length limit, which needs a location.
        text.Append(value, "");
        return text.Take();
    }
}

// `$"a{b}c"`: the parts in order, then the call site's location.
template <class... A>
inline String Interpolate(const A&... arguments) {
    constexpr size_t parts = sizeof...(A) - 1;
    auto all = std::forward_as_tuple(arguments...);
    const char* location = std::get<parts>(all);
    StringBuffer text;
    [&]<size_t... I>(std::index_sequence<I...>) { (text.Append(std::get<I>(all), location), ...); }(std::make_index_sequence<parts>{});
    return text.Take();
}

}  // namespace wyl
