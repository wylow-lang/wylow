#pragma once

#include "optional.h"

namespace vpl {

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

template <class F>
    requires std::is_floating_point_v<F>
inline size_t format(FormatBuffer& buffer, F value) {
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

// Builds a String in a block it grows, which the String then takes over without a copy.
class StringBuffer {
public:
    StringBuffer() = default;
    StringBuffer(const StringBuffer&) = delete;
    StringBuffer& operator=(const StringBuffer&) = delete;
    ~StringBuffer() { ::operator delete(block_); }

    void append(const char* bytes, size_t count, const char* location) {
        if (count > static_cast<size_t>(INT32_MAX) - length_) panic("string is too long", location);
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

    String take() {
        if (block_ == nullptr) return String();
        block_->bytes()[length_] = '\0';
        block_->count = 1;
        auto* block = std::exchange(block_, nullptr);
        return String::adopt(block, static_cast<int32_t>(std::exchange(length_, 0)));
    }

private:
    void grow(size_t needed) {
        size_t capacity = capacity_ < 16 ? 16 : capacity_;
        while (capacity < needed) capacity = capacity <= static_cast<size_t>(INT32_MAX) / 2 ? capacity * 2 : static_cast<size_t>(INT32_MAX);
        // One more byte for the terminator take() writes.
        auto* block = static_cast<String::Block*>(::operator new(sizeof(String::Block) + capacity + 1));
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

// The types written by format, and their nullables.
template <class T>
inline constexpr bool is_formatted = std::is_same_v<T, String> || requires(FormatBuffer& buffer, T value) { format(buffer, value); };
template <class T>
inline constexpr bool is_formatted<Optional<T>> = is_formatted<T>;

// Printable.toString through a generic bound I: formatted types satisfy the bound without
// implementing it. m_toString is the name CppEmitter gives the method.
template <class I, class T>
inline String to_string(const T& value) {
    if constexpr (std::is_same_v<T, String>) {
        return value;
    } else if constexpr (is_formatted<T>) {
        StringBuffer text;
        // One formatted value is far too short to reach the length limit, which needs a location.
        text.append(value, "");
        return text.take();
    } else {
        return bound<I>(value).m_toString();
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

}  // namespace vpl
