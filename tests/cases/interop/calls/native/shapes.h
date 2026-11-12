// The C++ side of interop/calls: a header and source that the test defines itself.
#pragma once

#include <cstddef>
#include <cstdint>

#define SHAPES_LIMIT 64
#define SHAPES_BIG 5000000000LL

static const unsigned int shapes_flags = 7u;

enum Color : uint32_t { Red = 1, Green = 2, Blue = 3 };

struct Point {
    int32_t x;
    int32_t y;

    int32_t sum() const { return x + y; }
    void shift(int32_t by) { x += by; y += by; }
};

union Value {
    int32_t asInt;
    float asFloat;
    uint8_t bytes[4];
};

struct Counter;

namespace shapes {
struct Box {
    Point corner;
    int64_t area;
};

int64_t box_area(Box box);
}

extern "C" {
int32_t point_dot(Point a, Point b);
void point_scale(Point* p, int32_t by);
Point point_make(int32_t x, int32_t y);
Point* point_new(int32_t x, int32_t y);
void point_free(Point* p);

Counter* counter_create(int64_t start);
int64_t counter_next(Counter* counter);
void counter_destroy(Counter* counter);

Value value_of_float(float f);
float value_float(Value v);

uint32_t color_code(Color color);
Color color_next(Color color);
size_t text_length(const char* text);
const char* text_greeting();
}

long long twice(long long value);
unsigned long long widen(unsigned short value);

int pick(int value);
int pick(double value);
