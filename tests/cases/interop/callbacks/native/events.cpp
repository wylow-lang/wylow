#include "events.h"

int32_t events_run(event_handler handler, void* context, int32_t count) {
    int32_t sum = 0;
    for (int32_t i = 1; i <= count; i++) sum += handler(context, i);
    return sum;
}

int32_t events_run_optional(event_handler handler, void* context) {
    return handler == nullptr ? -1 : handler(context, 7);
}

void events_words(text_handler handler, void* context) {
    handler(context, "alpha");
    handler(context, "beta");
}

int32_t events_watch(const Watcher* watcher) {
    return watcher->on_event == nullptr ? 0 : watcher->on_event(nullptr, watcher->limit);
}

static int32_t triple(void*, int32_t value) {
    return value * 3;
}

event_handler events_default() {
    return triple;
}

bool events_is_none(event_handler handler) {
    return handler == EVENTS_NONE;
}
