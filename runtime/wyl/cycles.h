#pragma once

#include <algorithm>

#include "optional.h"

namespace wyl {

// Synchronous trial deletion (Bacon and Rajan, "Concurrent Cycle Collection in Reference Counted
// Systems", 2001), run only when the program calls memory::collectCycles. Traversals use explicit
// stacks, since a cycle may be a long chain. A reference wyl_visit misses only makes its target
// look referenced from outside, so the collector frees less, never something in use.

template <class T>
inline void visit(Visitor& visitor, Ref<T>& reference) {
    if (reference && visitor.visit(reference.get())) reference = nullptr;
}

template <class T>
inline void visit(Visitor& visitor, Optional<T>& value) {
    if (value.has_value()) visit(visitor, value.assumed());
}

// Fixed arrays, and structs and enums through their generated wyl_visit; other values hold no references.
template <class T>
inline void visit(Visitor& visitor, T& value) {
    if constexpr (is_std_array<T>::value) {
        for (auto& element : value) visit(visitor, element);
    } else if constexpr (requires { value.wyl_visit(visitor); }) {
        value.wyl_visit(visitor);
    }
}

template <class F>
class FunctionVisitor final : public Visitor {
    F function;

public:
    explicit FunctionVisitor(F f) : function(std::move(f)) {}
    bool visit(Object* target) override { return function(target); }
};

struct CycleStats {
    size_t buffered = 0;
    size_t visited = 0;
    size_t freed = 0;
};

class CycleCollector {
    // Objects whose count dropped without reaching zero since the last collection. An entry is
    // null where its object was destroyed meanwhile.
    std::vector<Object*> buffer;
    size_t holes = 0;
    // Destructors running, outside or inside a collection, which may not start one.
    int destroying = 0;
    CycleStats stats;

    // Working storage for a collection, kept between collections so that each one reuses it. The
    // roots trade storage with the buffer.
    std::vector<Object*> roots;
    std::vector<Object*> marked;
    std::vector<Object*> stack;
    std::vector<Object*> garbage;

    friend class Object;

    // Calls f on each cyclic object the object refers to. Objects of acyclic classes are never on a
    // cycle, so references to them are left out, and references they hold count as from outside.
    template <class F>
    static void for_each_child(Object* object, F f) {
        FunctionVisitor visitor([&](Object* target) {
            if (target->state & Object::cyclic) f(target);
            return false;
        });
        object->wyl_visit(visitor);
    }

    void add(Object* object) {
        if (buffer.size() == buffer.capacity() && holes * 2 >= buffer.size()) compact();
        if (buffer.size() >= UINT32_MAX) raise_panic("too many objects are waiting for memory::collectCycles", "<runtime>");
        buffer.push_back(object);
        object->state |= Object::buffered;
        object->set_slot(static_cast<uint32_t>(buffer.size()));
    }

    void remove(Object* object) {
        buffer[object->slot() - 1] = nullptr;
        ++holes;
        object->set_slot(0);
        object->state &= ~Object::buffered;
    }

    void compact() {
        size_t kept = 0;
        for (Object* object : buffer) {
            if (object == nullptr) continue;
            buffer[kept++] = object;
            object->set_slot(static_cast<uint32_t>(kept));
        }
        buffer.resize(kept);
        holes = 0;
    }

    // Subtracts the references among the objects reachable from root, marking them gray, breadth
    // first so that `marked` from `start` on is both the group and the queue. Fails, undoing every
    // mark and subtraction of the group, once more than `budget` objects are marked in all.
    bool mark_gray(Object* root, size_t budget) {
        if (root->state & Object::gray) return true;
        size_t start = marked.size();
        if (start >= budget) return false;
        root->state |= Object::gray;
        marked.push_back(root);
        size_t expanded = start;
        while (expanded < marked.size()) {
            for_each_child(marked[expanded++], [&](Object* child) {
                child->unretain();
                if (!(child->state & Object::gray)) {
                    child->state |= Object::gray;
                    marked.push_back(child);
                }
            });
            if (marked.size() > budget) {
                for (size_t i = start; i < expanded; ++i) for_each_child(marked[i], [](Object* child) { child->retain(); });
                for (size_t i = start; i < marked.size(); ++i) marked[i]->state &= ~Object::gray;
                marked.resize(start);
                return false;
            }
        }
        return true;
    }

    // Gray objects still referenced from outside the group are live, and so is everything they
    // reach; the rest turn white.
    void scan(Object* root) {
        stack.push_back(root);
        while (!stack.empty()) {
            Object* object = stack.back();
            stack.pop_back();
            if (!(object->state & Object::gray)) continue;
            if (object->refs() > 0) {
                scan_black(object);
            } else {
                object->state = (object->state & ~Object::gray) | Object::white;
                for_each_child(object, [&](Object* child) { stack.push_back(child); });
            }
        }
    }

    // Restores the references a live object and everything it reaches hold.
    void scan_black(Object* root) {
        size_t base = stack.size();
        root->state &= ~(Object::gray | Object::white);
        stack.push_back(root);
        while (stack.size() > base) {
            Object* object = stack.back();
            stack.pop_back();
            for_each_child(object, [&](Object* child) {
                child->retain();
                if (child->state & (Object::gray | Object::white)) {
                    child->state &= ~(Object::gray | Object::white);
                    stack.push_back(child);
                }
            });
        }
    }

    void collect_white(Object* root) {
        if (!(root->state & Object::white)) return;
        size_t next = garbage.size();
        root->state = (root->state & ~Object::white) | Object::dying;
        garbage.push_back(root);
        while (next < garbage.size()) {
            for_each_child(garbage[next++], [&](Object* child) {
                if (child->state & Object::white) {
                    child->state = (child->state & ~Object::white) | Object::dying;
                    garbage.push_back(child);
                }
            });
        }
    }

    // Trial deletion from the first `count` roots, leaving the garbage found in `garbage`.
    void find_garbage(size_t count) {
        for (size_t i = 0; i < count; ++i) {
            if (roots[i] != nullptr) scan(roots[i]);
        }
        for (size_t i = 0; i < count; ++i) {
            if (roots[i] != nullptr) collect_white(roots[i]);
        }
    }

    // ---- Debug builds' diagnostics ----

    static constexpr int growing_calls_to_warn = 5;
    static constexpr int collections_to_warn = 10;
    size_t last_waiting = 0;
    int growing_calls = 0;
    bool warned_growing = false;
    // For each class whose objects collections have freed, how many collections did; negative once
    // reported.
    std::vector<std::pair<const char*, int>> freed_classes;

    // A class's name, and how many objects of it.
    struct ClassCount {
        const char* name;
        unsigned long long count;
    };

    static std::vector<ClassCount> count_by_class(const std::vector<Object*>& objects) {
        std::vector<ClassCount> counts;
        for (Object* object : objects) {
            const char* name = object->wyl_class_name();
            bool found = false;
            for (auto& entry : counts) {
                if (std::strcmp(entry.name, name) == 0) {
                    ++entry.count;
                    found = true;
                    break;
                }
            }
            if (!found) counts.push_back({name, 1});
        }
        return counts;
    }

    void note_waiting() {
        size_t waiting = buffer.size() - holes;
        growing_calls = waiting > last_waiting ? growing_calls + 1 : 0;
        last_waiting = waiting;
        if (growing_calls < growing_calls_to_warn || warned_growing) return;
        warned_growing = true;
        char message[256];
        std::snprintf(message, sizeof message,
                      "%llu objects are waiting for memory::collectCycles, more after each of the last %d calls; its "
                      "budget is too small to keep up",
                      static_cast<unsigned long long>(waiting), growing_calls);
        performance_warning(message);
    }

    void note_freed() {
        for (const ClassCount& freed : count_by_class(garbage)) {
            std::pair<const char*, int>* entry = nullptr;
            for (auto& existing : freed_classes) {
                if (std::strcmp(existing.first, freed.name) == 0) entry = &existing;
            }
            if (entry == nullptr) entry = &freed_classes.emplace_back(freed.name, 0);
            if (entry->second < 0 || ++entry->second < collections_to_warn) continue;
            entry->second = -1;
            char message[256];
            std::snprintf(message, sizeof message,
                          "objects of class '%s' were freed by %d calls of memory::collectCycles; a Weak<T> in their "
                          "cycles would let them be destroyed as soon as they are unreachable",
                          freed.name, collections_to_warn);
            performance_warning(message);
        }
    }

    // Finds the garbage among the buffered objects without destroying it, at exit.
    void report_uncollected() {
        for (Object* root : buffer) {
            if (root == nullptr) continue;
            mark_gray(root, SIZE_MAX);
            roots.push_back(root);
        }
        find_garbage(roots.size());
        if (garbage.empty()) return;

        char message[1024];
        int length = std::snprintf(message, sizeof message, "at exit, %llu objects in cycles were never freed:",
                                   static_cast<unsigned long long>(garbage.size()));
        const char* separator = " ";
        for (const ClassCount& entry : count_by_class(garbage)) {
            if (length < 0 || static_cast<size_t>(length) >= sizeof message - 128) break;
            length += std::snprintf(message + length, sizeof message - static_cast<size_t>(length), "%s%llu %s", separator,
                                    entry.count, entry.name);
            separator = ", ";
        }
        if (length >= 0 && static_cast<size_t>(length) < sizeof message) {
            std::snprintf(message + length, sizeof message - static_cast<size_t>(length),
                          ". Call memory::collectCycles to free them, or break the cycles with Weak<T>");
        }
        performance_warning(message);
    }

    // Runs every destructor while every object of the garbage is intact, then frees it. The
    // collector holds a count on each object meanwhile, so no release can destroy one early.
    // Destructors run oldest first: the order the collector found the objects in depends on which
    // releases buffered them, which the compiler's own temporaries change.
    void destroy_garbage() {
        std::sort(garbage.begin(), garbage.end(), [](Object* a, Object* b) { return a->age() > b->age(); });
        if constexpr (WYL_DEBUG != 0) note_freed();
        for (Object* object : garbage) for_each_child(object, [](Object* child) { child->retain(); });
        for (Object* object : garbage) {
            object->retain();
            object->state &= ~Object::buffered;
            object->clear_weak();
            object->set_slot(0);
        }

        // Only the garbage is finalized, since earlier collections freed what they finalized.
        for (Object* object : garbage) {
            object->state |= Object::finalized;
            object->wyl_finalize();
        }

        // Destructors may have changed references among the garbage, but none may have left one
        // to it anywhere else: without the references among it, only the collector's count is left.
        for (Object* object : garbage) {
            for_each_child(object, [](Object* child) {
                if (child->state & Object::finalized) child->unretain();
            });
        }
        for (Object* object : garbage) {
            if (object->refs() != 1) raise_panic("a destructor kept a reference to an object of the cycle being collected", "<destructor>");
        }

        // Clearing a reference among the garbage releases the count just taken off for it.
        FunctionVisitor clear([](Object* child) {
            if (child->state & Object::finalized) child->retain();
            return true;
        });
        for (Object* object : garbage) object->wyl_visit(clear);
        for (Object* object : garbage) delete object;
        stats.freed += garbage.size();
    }

public:
    void collect(size_t budget, const char* location) {
        if (destroying > 0) raise_panic("memory::collectCycles cannot be called from a destructor", location);
        ++destroying;
        roots.swap(buffer);
        holes = 0;

        size_t processed = 0;
        for (; processed < roots.size(); ++processed) {
            if (roots[processed] != nullptr && !mark_gray(roots[processed], budget)) break;
        }
        stats.visited += marked.size();

        find_garbage(processed);
        for (size_t i = 0; i < processed; ++i) {
            Object* root = roots[i];
            if (root == nullptr) continue;
            root->state &= ~Object::buffered;
            root->set_slot(0);
        }

        // Back in the buffer before any destructor runs, since one may destroy them.
        for (size_t i = processed; i < roots.size(); ++i) {
            Object* root = roots[i];
            if (root == nullptr || (root->state & Object::dying)) continue;
            add(root);
        }
        roots.clear();
        marked.clear();

        if (!garbage.empty()) destroy_garbage();
        garbage.clear();
        if constexpr (WYL_DEBUG != 0) note_waiting();
        --destroying;
    }

    CycleCollector() = default;
    CycleCollector(const CycleCollector&) = delete;
    CycleCollector& operator=(const CycleCollector&) = delete;

    ~CycleCollector() {
        if constexpr (WYL_DEBUG != 0) report_uncollected();
    }

    CycleStats take_stats() { return std::exchange(stats, CycleStats{}); }
};

// Objects belong to one thread, and so will the collector's state once Wylow has threads.
inline CycleCollector cycles;

inline void Object::destroy() {
    // Holding the count at 1 lets the destructor create and drop temporary references to `this`
    // without destroying it twice; ~Object checks that none outlive it.
    retain();
    clear_weak();
    if (state & buffered) cycles.remove(this);
    ++cycles.destroying;
    delete this;
    --cycles.destroying;
}

inline void Object::add_to_buffer() {
    cycles.add(this);
    ++cycles.stats.buffered;
}

// Its slot holds a reference for the object's whole life, so a release never frees it and could
// only buffer it, leaving the buffer pointing into a frame that will be gone. Nothing outside its
// scope refers to it, so it is never part of an unreachable cycle anyway. Its constructor may have
// buffered it already, by releasing a reference to `this`.
inline void Object::leave_collector() {
    state &= ~cyclic;
    if (state & buffered) cycles.remove(this);
}

// As destroy(), except that the slot's reference is still held: it is the 1 that ~Object checks for.
inline void Object::destroy_in_slot(bool in_place) {
    if constexpr (WYL_DEBUG != 0) {
        if (refs() != 1) raise_panic("internal compiler error: an object on the stack outlived its scope", "<runtime>");
    }
    clear_weak();
    if (state & buffered) cycles.remove(this);
    ++cycles.destroying;
    if (in_place) {
        this->~Object();
    } else {
        delete this;
    }
    --cycles.destroying;
}

}  // namespace wyl
