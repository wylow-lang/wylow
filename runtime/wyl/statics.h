#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include "core.h"

namespace wyl {

inline std::vector<const char*>& static_initializing() {
    static std::vector<const char*> names;
    return names;
}

inline void static_begin(const char* name) { static_initializing().push_back(name); }

inline void static_end() { static_initializing().pop_back(); }

[[noreturn]] inline void static_cycle(const char* name, const char* location) {
    std::string message = "reading static '";
    message += name;
    message += "' while its initializer is still running (";
    const auto& names = static_initializing();
    for (size_t i = 0; i < names.size(); ++i) {
        if (i > 0) message += " -> ";
        message += names[i];
    }
    if (!names.empty()) message += " -> ";
    message += name;
    message += ")";
    raise_panic(message.c_str(), location == nullptr ? "" : location);
}

template <class T>
struct StaticStorage {
    alignas(T) unsigned char bytes[sizeof(T)];
    uint8_t state = 0;
    const char* name;

    explicit StaticStorage(const char* n) : name(n) {}
    ~StaticStorage() {}

    T* place() { return reinterpret_cast<T*>(bytes); }
    T& value() { return *place(); }
};

}  // namespace wyl
