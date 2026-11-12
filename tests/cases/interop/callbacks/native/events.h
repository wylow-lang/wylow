// The C++ side of interop/callbacks: functions that call back through C function pointers.
#pragma once

#include <cstdint>

typedef int32_t (*event_handler)(void* context, int32_t value);
typedef void (*text_handler)(void* context, const char* text);

struct Watcher {
    event_handler on_event;
    int32_t limit;
};

// A sentinel that is not the address of a function, as SQLite's SQLITE_TRANSIENT is.
#define EVENTS_NONE ((event_handler) - 1)

extern "C" {
int32_t events_run(event_handler handler, void* context, int32_t count);
int32_t events_run_optional(event_handler handler, void* context);
void events_words(text_handler handler, void* context);
int32_t events_watch(const Watcher* watcher);
event_handler events_default();
bool events_is_none(event_handler handler);
}
