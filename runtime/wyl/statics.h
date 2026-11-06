#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include "core.h"

namespace wyl {

inline std::vector<const char*>& StaticInitializing() {
    static std::vector<const char*> names;
    return names;
}

inline void StaticBegin(const char* name) { StaticInitializing().push_back(name); }

inline void StaticEnd() { StaticInitializing().pop_back(); }

[[noreturn]] inline void StaticCycle(const char* name, const char* location) {
    std::string message = "reading static '";
    message += name;
    message += "' while its initializer is still running (";
    const auto& names = StaticInitializing();
    for (size_t i = 0; i < names.size(); ++i) {
        if (i > 0) message += " -> ";
        message += names[i];
    }
    if (!names.empty()) message += " -> ";
    message += name;
    message += ")";
    RaisePanic(message.c_str(), location == nullptr ? "" : location);
}

template <class T>
struct StaticStorage {
    alignas(T) unsigned char Bytes[sizeof(T)];
    uint8_t State = 0;
    const char* Name;

    explicit StaticStorage(const char* name) : Name(name) {}
    ~StaticStorage() {}

    T* Place() { return reinterpret_cast<T*>(Bytes); }
    T& Value() { return *Place(); }
};

}  // namespace wyl
