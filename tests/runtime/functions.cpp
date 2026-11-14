#include <cstdio>
#include <utility>

#include "wyl/functions.h"

using namespace wyl;

namespace {

int s_Failures = 0;

void Expect(bool condition, const char* what) {
    if (condition) return;
    std::fprintf(stderr, "failed: %s\n", what);
    ++s_Failures;
}

struct Counted {
    static int Live;

    Counted() { ++Live; }
    Counted(const Counted&) { ++Live; }
    Counted(Counted&&) noexcept { ++Live; }
    Counted& operator=(const Counted&) = default;
    Counted& operator=(Counted&&) noexcept = default;
    ~Counted() { --Live; }
};

int Counted::Live = 0;

Function<int(int)> MakeCounted() {
    return MakeClosure<int(int), "counted">(
        false, [](const Counted&, int delta) -> int { return delta + 1; }, Counted());
}

void DefaultFunctionsAreNull() {
    Function<int(int)> empty;
    Expect(empty.IsNull(), "a default function is null");
    Optional<Function<int(int)>> none;
    Expect(!none.HasValue(), "a default nullable of a function is null");
}

void CopiesShareTheClosure() {
    {
        Function<int(int)> original = MakeCounted();
        Function<int(int)> copy = original;
        Expect(original(4) == 5, "the original calls its code");
        Expect(copy(6) == 7, "the copy calls the same code");
        Expect(Counted::Live == 1, "a copy keeps the closure");
    }
    Expect(Counted::Live == 0, "the last copy releases the closure");
}

void MovingTheClosureEmptiesTheSource() {
    {
        Function<int(int)> source = MakeCounted();
        Function<int(int)> moved = std::move(source);
        Expect(source.IsNull(), "a move construction empties the source");
        Expect(!moved.IsNull(), "a move construction fills the destination");
        Expect(moved(4) == 5, "the moved function calls its code");

        Function<int(int)> assigned;
        assigned = std::move(moved);
        Expect(moved.IsNull(), "a move assignment empties the source");
        Expect(!assigned.IsNull(), "a move assignment fills the destination");
        Expect(assigned(4) == 5, "the assigned function calls its code");
        Expect(Counted::Live == 1, "a move keeps the closure");
    }
    Expect(Counted::Live == 0, "the last function releases the closure");
}

void MovingANullableEmptiesTheSource() {
    {
        Optional<Function<int(int)>> source = MakeCounted();
        Expect(source.HasValue(), "a nullable of a function holds one");
        Optional<Function<int(int)>> assigned;
        assigned = std::move(source);
        Expect(!source.HasValue(), "a move assignment empties the source nullable");
        Expect(assigned.HasValue(), "a move assignment fills the destination nullable");
        Expect(assigned.Assumed()(4) == 5, "the moved function calls its code");
        Expect(Counted::Live == 1, "a move keeps the closure");
    }
    Expect(Counted::Live == 0, "the last function releases the closure");
}

void MovingAFunctionIntoItselfKeepsIt() {
    {
        Function<int(int)> value = MakeCounted();
        Function<int(int)>& reference = value;
        value = std::move(reference);
        Expect(!value.IsNull(), "a self move keeps the function");
        Expect(value(4) == 5, "the self-moved function calls its code");
        Expect(Counted::Live == 1, "a self move keeps the closure");
    }
    Expect(Counted::Live == 0, "the value releases the closure");
}

void AssigningReleasesTheClosureItHeld() {
    {
        Function<int(int)> value = MakeCounted();
        value = MakeCounted();
        Expect(Counted::Live == 1, "an assignment releases the closure the value held");
        Expect(value(4) == 5, "the assigned function calls its code");
    }
    Expect(Counted::Live == 0, "the stored closure is released");
}

void CopyingOverAValueReleasesItsClosure() {
    {
        Function<int(int)> left = MakeCounted();
        Function<int(int)> right = MakeCounted();
        left = right;
        Expect(Counted::Live == 1, "a copy assignment releases the closure the destination held");
        Expect(left(4) == 5 && right(4) == 5, "both functions call the same code");
    }
    Expect(Counted::Live == 0, "the shared closure is released");
}

}  // namespace

int main() {
    DefaultFunctionsAreNull();
    CopiesShareTheClosure();
    MovingTheClosureEmptiesTheSource();
    MovingANullableEmptiesTheSource();
    MovingAFunctionIntoItselfKeepsIt();
    AssigningReleasesTheClosureItHeld();
    CopyingOverAValueReleasesItsClosure();
    return s_Failures == 0 ? 0 : 1;
}
