#include "shapes.h"

#include <cstring>

struct Counter {
    int64_t value;
};

int32_t point_dot(Point a, Point b) { return a.x * b.x + a.y * b.y; }
void point_scale(Point* p, int32_t by) { p->x *= by; p->y *= by; }
Point point_make(int32_t x, int32_t y) { return Point{x, y}; }
Point* point_new(int32_t x, int32_t y) { return new Point{x, y}; }
void point_free(Point* p) { delete p; }

Counter* counter_create(int64_t start) { return new Counter{start}; }
int64_t counter_next(Counter* counter) { return counter->value++; }
void counter_destroy(Counter* counter) { delete counter; }

Value value_of_float(float f) {
    Value v;
    v.asFloat = f;
    return v;
}

float value_float(Value v) { return v.asFloat; }

uint32_t color_code(Color color) { return static_cast<uint32_t>(color) * 10; }
Color color_next(Color color) { return color == Blue ? Red : static_cast<Color>(color + 1); }
size_t text_length(const char* text) { return std::strlen(text); }
const char* text_greeting() { return "hello from C++"; }

long long twice(long long value) { return value * 2; }
unsigned long long widen(unsigned short value) { return value + 70000ull; }

int pick(int value) { return value + 1; }
int pick(double value) { return static_cast<int>(value * 10); }

namespace shapes {
int64_t box_area(Box box) { return box.area + box.corner.x; }
}
