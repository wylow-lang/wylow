#pragma once

#include "optional.h"

namespace wyl {

// Text for primitive values, shared by print and string interpolation so the two always agree.
// Every format fits in a FormatBuffer.
using FormatBuffer = char[64];

template <class I>
    requires std::is_integral_v<I>
inline size_t format(FormatBuffer& buffer, I value) {
    auto result = std::to_chars(buffer, buffer + sizeof buffer, value);
    return static_cast<size_t>(result.ptr - buffer);
}

inline size_t format(FormatBuffer& buffer, bool value) {
    const char* text = value ? "true" : "false";
    size_t length = std::strlen(text);
    std::memcpy(buffer, text, length);
    return length;
}

inline size_t format(FormatBuffer& buffer, char32_t value) {
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
inline size_t format_nan(char* buffer) {
    std::memcpy(buffer, "nan", 3);
    return 3;
}

template <class F>
    requires std::is_floating_point_v<F>
inline size_t format(FormatBuffer& buffer, F value) {
    if (std::isnan(value)) return format_nan(buffer);
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
inline constexpr bool is_formatted_integer = std::is_integral_v<I> && !std::is_same_v<I, bool> && !std::is_same_v<I, char32_t>;

template <class I>
    requires is_formatted_integer<I>
inline size_t format(SpecifiedBuffer& buffer, I value, int32_t kind, int32_t precision) {
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
inline size_t format(SpecifiedBuffer& buffer, F value, int32_t kind, int32_t precision) {
    if (std::isnan(value)) return format_nan(buffer);
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
    T value;
    int32_t kind;
    int32_t precision;
    int32_t alignment;

    operator String() const;
};

template <class T>
inline Formatted<T> formatted(const T& value, int32_t kind, int32_t precision, int32_t alignment) {
    return {value, kind, precision, alignment};
}

// Builds a String in a block it grows, which the String then takes over without a copy.
class StringBuffer {
public:
    StringBuffer() = default;
    StringBuffer(const StringBuffer&) = delete;
    StringBuffer& operator=(const StringBuffer&) = delete;
    ~StringBuffer() { ::operator delete(block_); }

    void append(const char* bytes, size_t count, const char* location) {
        // An empty buffer has no block to point into.
        if (count == 0) return;
        if (count > static_cast<size_t>(INT32_MAX) - length_) raise_panic("string is too long", location);
        size_t needed = length_ + count;
        if (needed > capacity_) grow(needed);
        std::memcpy(block_->bytes() + length_, bytes, count);
        length_ = needed;
    }

    void append(const String& value, const char* location) {
        append(value.data(), static_cast<size_t>(value.length()), location);
    }

    template <class T>
    void append(const Optional<T>& value, const char* location) {
        if (value.has_value()) {
            append(value.assumed(), location);
        } else {
            append("null", 4, location);
        }
    }

    template <class T>
        requires requires(FormatBuffer& buffer, T value) { format(buffer, value); }
    void append(T value, const char* location) {
        FormatBuffer buffer;
        append(buffer, format(buffer, value), location);
    }

    template <class T>
    void append(const Formatted<T>& value, const char* location) {
        size_t start = length_;
        append_specified(value.value, value.kind, value.precision, location);
        if (value.alignment != 0) pad(start, value.alignment, location);
    }

    String take() {
        if (block_ == nullptr) return String();
        block_->bytes()[length_] = '\0';
        block_->count = 1;
        auto* block = std::exchange(block_, nullptr);
        return String::adopt(block, static_cast<int32_t>(std::exchange(length_, 0)));
    }

private:
    template <class T>
    void append_specified(const T& value, int32_t kind, int32_t precision, const char* location) {
        if constexpr (is_formatted_integer<T> || std::is_floating_point_v<T>) {
            if (kind != 0) {
                SpecifiedBuffer buffer;
                append(buffer, format(buffer, value, kind, precision), location);
                return;
            }
        }
        append(value, location);
    }

    template <class T>
    void append_specified(const Optional<T>& value, int32_t kind, int32_t precision, const char* location) {
        if (value.has_value()) {
            append_specified(value.assumed(), kind, precision, location);
        } else {
            append("null", 4, location);
        }
    }

    // Pads the text written since start with spaces to the alignment's width in characters.
    void pad(size_t start, int32_t alignment, const char* location) {
        size_t characters = 0;
        for (size_t i = start; i < length_; i++) {
            if (!String::is_continuation(block_->bytes()[i])) characters++;
        }
        auto width = static_cast<size_t>(alignment < 0 ? -static_cast<int64_t>(alignment) : alignment);
        if (characters >= width) return;
        size_t count = width - characters;
        size_t end = length_;
        if (count > static_cast<size_t>(INT32_MAX) - length_) raise_panic("string is too long", location);
        if (length_ + count > capacity_) grow(length_ + count);
        char* bytes = block_->bytes();
        if (alignment > 0) {
            std::memmove(bytes + start + count, bytes + start, end - start);
            std::memset(bytes + start, ' ', count);
        } else {
            std::memset(bytes + end, ' ', count);
        }
        length_ = end + count;
    }

    void grow(size_t needed) {
        size_t capacity = capacity_ < 16 ? 16 : capacity_;
        while (capacity < needed) capacity = capacity <= static_cast<size_t>(INT32_MAX) / 2 ? capacity * 2 : static_cast<size_t>(INT32_MAX);
        // One more byte for the terminator take() writes.
        auto* block = ::new (static_cast<void*>(::operator new(sizeof(String::Block) + capacity + 1))) String::Block{};
        if (block_ != nullptr) {
            std::memcpy(block->bytes(), block_->bytes(), length_);
            ::operator delete(block_);
        }
        block_ = block;
        capacity_ = capacity;
    }

    String::Block* block_ = nullptr;
    size_t length_ = 0;
    size_t capacity_ = 0;
};

inline Optional<int32_t> String::indexOf(const String& text) const {
    if (text.length_ > length_) return nullptr;
    if (text.length_ == 0) return 0;
    const char* last = data_ + (length_ - text.length_);
    for (const char* p = data_; p <= last; p++) {
        p = static_cast<const char*>(std::memchr(p, text.data_[0], static_cast<size_t>(last - p) + 1));
        if (p == nullptr) break;
        if (std::memcmp(p, text.data_, static_cast<size_t>(text.length_)) == 0) return static_cast<int32_t>(p - data_);
    }
    return nullptr;
}

inline bool String::contains(const String& text) const { return indexOf(text).has_value(); }

inline String String::slice(int32_t start, int32_t count, const char* location) const {
    if (start < 0 || count < 0 || static_cast<int64_t>(start) + count > length_) {
        char message[128];
        std::snprintf(message, sizeof message, "byte range of %" PRId32 " from %" PRId32 " is out of bounds for length %" PRId32, count, start,
                      length_);
        raise_panic(message, location);
    }
    for (int32_t offset : {start, start + count}) {
        if (!is_boundary(offset)) {
            char message[96];
            std::snprintf(message, sizeof message, "byte offset %" PRId32 " is inside a character", offset);
            raise_panic(message, location);
        }
    }
    if (count == length_) return *this;
    StringBuffer text;
    text.append(data_ + start, static_cast<size_t>(count), location);
    return text.take();
}

template <class T>
inline Formatted<T>::operator String() const {
    StringBuffer text;
    // Only a huge alignment reaches the length limit, and a conversion has no location to report.
    text.append(*this, "");
    return text.take();
}

// The types written by format, and their nullables.
template <class T>
inline constexpr bool is_formatted = std::is_same_v<T, String> || requires(FormatBuffer& buffer, T value) { format(buffer, value); };
template <class T>
inline constexpr bool is_formatted<Optional<T>> = is_formatted<T>;

// toString() of a primitive or String.
template <class T>
    requires is_formatted<T>
inline String format_string(const T& value) {
    if constexpr (std::is_same_v<T, String>) {
        return value;
    } else {
        StringBuffer text;
        // One formatted value is far too short to reach the length limit, which needs a location.
        text.append(value, "");
        return text.take();
    }
}

// `$"a{b}c"`: the parts in order, then the call site's location.
template <class... A>
inline String interpolate(const A&... arguments) {
    constexpr size_t parts = sizeof...(A) - 1;
    auto all = std::forward_as_tuple(arguments...);
    const char* location = std::get<parts>(all);
    StringBuffer text;
    [&]<size_t... I>(std::index_sequence<I...>) { (text.append(std::get<I>(all), location), ...); }(std::make_index_sequence<parts>{});
    return text.take();
}

}  // namespace wyl
