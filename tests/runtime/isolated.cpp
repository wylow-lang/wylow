#include <cstdio>
#include <utility>

#include "wyl/isolated.h"

using namespace wyl;

namespace {

int s_Failures = 0;

void Expect(bool condition, const char* what) {
    if (condition) return;
    std::fprintf(stderr, "failed: %s\n", what);
    ++s_Failures;
}

struct Plain final : Object {
    const char* ClassName() const override { return "Plain"; }
};

size_t VisitedAfterCollect() {
    CurrentCycles().Collect(SIZE_MAX, "<runtime>");
    return CurrentCycles().TakeStats().Visited;
}

void MoveAssigningKeepsTheReplacedGraph() {
    auto* kept = new Plain();
    auto* replaced = new Plain();
    {
        IsolatedState source;
        IsolatedState target;
        target.Buffer.Objects.push_back(replaced);
        source.Buffer.Objects.push_back(kept);
        target = std::move(source);
    }
    Expect(VisitedAfterCollect() == 2, "a move assignment merges the graph it replaced");
    kept->Release();
    replaced->Release();
}

void MoveConstructingKeepsTheGraph() {
    auto* only = new Plain();
    {
        IsolatedState source;
        source.Buffer.Objects.push_back(only);
        IsolatedState moved(std::move(source));
    }
    Expect(VisitedAfterCollect() == 1, "a move construction keeps the graph");
    only->Release();
}

void ReleasingMergesTheGraph() {
    auto* only = new Plain();
    {
        IsolatedState state;
        state.Buffer.Objects.push_back(only);
    }
    Expect(VisitedAfterCollect() == 1, "releasing a state merges its graph");
    only->Release();
}

}  // namespace

int main() {
    MoveAssigningKeepsTheReplacedGraph();
    MoveConstructingKeepsTheGraph();
    ReleasingMergesTheGraph();
    return s_Failures == 0 ? 0 : 1;
}
