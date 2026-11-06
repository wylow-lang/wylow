#pragma once

#include <algorithm>

#include "optional.h"

namespace wyl {

// Synchronous trial deletion (Bacon and Rajan, "Concurrent Cycle Collection in Reference Counted
// Systems", 2001), run only when the program calls memory::collectCycles. Traversals use explicit
// stacks, since a cycle may be a long chain. A reference Visit misses only makes its target
// look referenced from outside, so the collector frees less, never something in use.

template <class T>
inline void Visit(Visitor& visitor, Ref<T>& reference) {
    if (reference && visitor.Visit(reference.Get())) reference = nullptr;
}

template <class T>
inline void Visit(Visitor& visitor, Optional<T>& value) {
    if (value.HasValue()) Visit(visitor, value.Assumed());
}

// Fixed arrays, and structs and enums through their generated Visit; other values hold no references.
template <class T>
inline void Visit(Visitor& visitor, T& value) {
    if constexpr (IsStdArray<T>::value) {
        for (auto& element : value) Visit(visitor, element);
    } else if constexpr (requires { value.Visit(visitor); }) {
        value.Visit(visitor);
    }
}

template <class F>
class FunctionVisitor final : public Visitor {
    F m_Function;

public:
    explicit FunctionVisitor(F f) : m_Function(std::move(f)) {}
    bool Visit(Object* target) override { return m_Function(target); }
};

struct CycleStats {
    size_t Buffered = 0;
    size_t Visited = 0;
    size_t Freed = 0;
};

class CycleCollector {
    // Objects whose count dropped without reaching zero since the last collection. An entry is
    // null where its object was destroyed meanwhile.
    std::vector<Object*> m_Buffer;
    size_t m_Holes = 0;
    // Destructors running, outside or inside a collection, which may not start one.
    int m_Destroying = 0;
    CycleStats m_Stats;

    // Working storage for a collection, kept between collections so that each one reuses it. The
    // roots trade storage with the buffer.
    std::vector<Object*> m_Roots;
    std::vector<Object*> m_Marked;
    std::vector<Object*> m_Stack;
    std::vector<Object*> m_Garbage;

    friend class Object;

    // Calls f on each cyclic object the object refers to. Objects of acyclic classes are never on a
    // cycle, so references to them are left out, and references they hold count as from outside.
    template <class F>
    static void ForEachChild(Object* object, F f) {
        FunctionVisitor visitor([&](Object* target) {
            if (target->m_State & Object::CYCLIC) f(target);
            return false;
        });
        object->Visit(visitor);
    }

    void Add(Object* object) {
        if (m_Buffer.size() == m_Buffer.capacity() && m_Holes * 2 >= m_Buffer.size()) Compact();
        if (m_Buffer.size() >= UINT32_MAX) RaisePanic("too many objects are waiting for memory::collectCycles", "<runtime>");
        m_Buffer.push_back(object);
        object->m_State |= Object::BUFFERED;
        object->SetSlot(static_cast<uint32_t>(m_Buffer.size()));
    }

    void Remove(Object* object) {
        m_Buffer[object->Slot() - 1] = nullptr;
        ++m_Holes;
        object->SetSlot(0);
        object->m_State &= ~Object::BUFFERED;
    }

    void Compact() {
        size_t kept = 0;
        for (Object* object : m_Buffer) {
            if (object == nullptr) continue;
            m_Buffer[kept++] = object;
            object->SetSlot(static_cast<uint32_t>(kept));
        }
        m_Buffer.resize(kept);
        m_Holes = 0;
    }

    // Subtracts the references among the objects reachable from root, marking them gray, breadth
    // first so that `marked` from `start` on is both the group and the queue. Fails, undoing every
    // mark and subtraction of the group, once more than `budget` objects are marked in all.
    bool MarkGray(Object* root, size_t budget) {
        if (root->m_State & Object::GRAY) return true;
        size_t start = m_Marked.size();
        if (start >= budget) return false;
        root->m_State |= Object::GRAY;
        m_Marked.push_back(root);
        size_t expanded = start;
        while (expanded < m_Marked.size()) {
            ForEachChild(m_Marked[expanded++], [&](Object* child) {
                child->Unretain();
                if (!(child->m_State & Object::GRAY)) {
                    child->m_State |= Object::GRAY;
                    m_Marked.push_back(child);
                }
            });
            if (m_Marked.size() > budget) {
                for (size_t i = start; i < expanded; ++i) ForEachChild(m_Marked[i], [](Object* child) { child->Retain(); });
                for (size_t i = start; i < m_Marked.size(); ++i) m_Marked[i]->m_State &= ~Object::GRAY;
                m_Marked.resize(start);
                return false;
            }
        }
        return true;
    }

    // Gray objects still referenced from outside the group are live, and so is everything they
    // reach; the rest turn white.
    void Scan(Object* root) {
        m_Stack.push_back(root);
        while (!m_Stack.empty()) {
            Object* object = m_Stack.back();
            m_Stack.pop_back();
            if (!(object->m_State & Object::GRAY)) continue;
            if (object->Refs() > 0) {
                ScanBlack(object);
            } else {
                object->m_State = (object->m_State & ~Object::GRAY) | Object::WHITE;
                ForEachChild(object, [&](Object* child) { m_Stack.push_back(child); });
            }
        }
    }

    // Restores the references a live object and everything it reaches hold.
    void ScanBlack(Object* root) {
        size_t base = m_Stack.size();
        root->m_State &= ~(Object::GRAY | Object::WHITE);
        m_Stack.push_back(root);
        while (m_Stack.size() > base) {
            Object* object = m_Stack.back();
            m_Stack.pop_back();
            ForEachChild(object, [&](Object* child) {
                child->Retain();
                if (child->m_State & (Object::GRAY | Object::WHITE)) {
                    child->m_State &= ~(Object::GRAY | Object::WHITE);
                    m_Stack.push_back(child);
                }
            });
        }
    }

    void CollectWhite(Object* root) {
        if (!(root->m_State & Object::WHITE)) return;
        size_t next = m_Garbage.size();
        root->m_State = (root->m_State & ~Object::WHITE) | Object::DYING;
        m_Garbage.push_back(root);
        while (next < m_Garbage.size()) {
            ForEachChild(m_Garbage[next++], [&](Object* child) {
                if (child->m_State & Object::WHITE) {
                    child->m_State = (child->m_State & ~Object::WHITE) | Object::DYING;
                    m_Garbage.push_back(child);
                }
            });
        }
    }

    // Trial deletion from the first `count` roots, leaving the garbage found in `garbage`.
    void FindGarbage(size_t count) {
        for (size_t i = 0; i < count; ++i) {
            if (m_Roots[i] != nullptr) Scan(m_Roots[i]);
        }
        for (size_t i = 0; i < count; ++i) {
            if (m_Roots[i] != nullptr) CollectWhite(m_Roots[i]);
        }
    }

    // ---- Debug builds' diagnostics ----

    static constexpr int GROWING_CALLS_TO_WARN = 5;
    static constexpr int COLLECTIONS_TO_WARN = 10;
    size_t m_LastWaiting = 0;
    int m_GrowingCalls = 0;
    bool m_WarnedGrowing = false;
    // For each class whose objects collections have freed, how many collections did; negative once
    // reported.
    std::vector<std::pair<const char*, int>> m_FreedClasses;

    // A class's name, and how many objects of it.
    struct ClassCount {
        const char* Name;
        unsigned long long Count;
    };

    static std::vector<ClassCount> CountByClass(const std::vector<Object*>& objects) {
        std::vector<ClassCount> counts;
        for (Object* object : objects) {
            const char* name = object->ClassName();
            bool found = false;
            for (auto& entry : counts) {
                if (std::strcmp(entry.Name, name) == 0) {
                    ++entry.Count;
                    found = true;
                    break;
                }
            }
            if (!found) counts.push_back({name, 1});
        }
        // By name, since the order objects are found in may change with optimization.
        std::sort(counts.begin(), counts.end(), [](const ClassCount& a, const ClassCount& b) { return std::strcmp(a.Name, b.Name) < 0; });
        return counts;
    }

    void NoteWaiting() {
        size_t waiting = m_Buffer.size() - m_Holes;
        m_GrowingCalls = waiting > m_LastWaiting ? m_GrowingCalls + 1 : 0;
        m_LastWaiting = waiting;
        if (m_GrowingCalls < GROWING_CALLS_TO_WARN || m_WarnedGrowing) return;
        m_WarnedGrowing = true;
        char message[256];
        std::snprintf(message, sizeof message,
                      "%llu objects are waiting for memory::collectCycles, more after each of the last %d calls; its "
                      "budget is too small to keep up",
                      static_cast<unsigned long long>(waiting), m_GrowingCalls);
        PerformanceWarning(message);
    }

    void NoteFreed() {
        for (const ClassCount& freed : CountByClass(m_Garbage)) {
            std::pair<const char*, int>* entry = nullptr;
            for (auto& existing : m_FreedClasses) {
                if (std::strcmp(existing.first, freed.Name) == 0) entry = &existing;
            }
            if (entry == nullptr) entry = &m_FreedClasses.emplace_back(freed.Name, 0);
            if (entry->second < 0 || ++entry->second < COLLECTIONS_TO_WARN) continue;
            entry->second = -1;
            char message[256];
            std::snprintf(message, sizeof message,
                          "objects of class '%s' were freed by %d calls of memory::collectCycles; a Weak<T> in their "
                          "cycles would let them be destroyed as soon as they are unreachable",
                          freed.Name, COLLECTIONS_TO_WARN);
            PerformanceWarning(message);
        }
    }

    // Finds the garbage among the buffered objects without destroying it, at exit.
    void ReportUncollected() {
        for (Object* root : m_Buffer) {
            if (root == nullptr) continue;
            MarkGray(root, SIZE_MAX);
            m_Roots.push_back(root);
        }
        FindGarbage(m_Roots.size());
        if (m_Garbage.empty()) return;

        char message[1024];
        int length = std::snprintf(message, sizeof message, "at exit, %llu objects in cycles were never freed:",
                                   static_cast<unsigned long long>(m_Garbage.size()));
        const char* separator = " ";
        for (const ClassCount& entry : CountByClass(m_Garbage)) {
            if (length < 0 || static_cast<size_t>(length) >= sizeof message - 128) break;
            length += std::snprintf(message + length, sizeof message - static_cast<size_t>(length), "%s%llu %s", separator,
                                    entry.Count, entry.Name);
            separator = ", ";
        }
        if (length >= 0 && static_cast<size_t>(length) < sizeof message) {
            std::snprintf(message + length, sizeof message - static_cast<size_t>(length),
                          ". Call memory::collectCycles to free them, or break the cycles with Weak<T>");
        }
        PerformanceWarning(message);
    }

    // Runs every destructor while every object of the garbage is intact, then frees it. The
    // collector holds a count on each object meanwhile, so no release can destroy one early.
    // Destructors run oldest first: the order the collector found the objects in depends on which
    // releases buffered them, which the compiler's own temporaries change.
    void DestroyGarbage() {
        std::sort(m_Garbage.begin(), m_Garbage.end(), [](Object* a, Object* b) { return a->Age() > b->Age(); });
        if constexpr (WYL_DEBUG != 0) NoteFreed();
        for (Object* object : m_Garbage) ForEachChild(object, [](Object* child) { child->Retain(); });
        for (Object* object : m_Garbage) {
            object->Retain();
            object->m_State &= ~Object::BUFFERED;
            object->ClearWeak();
            object->SetSlot(0);
        }

        // Only the garbage is finalized, since earlier collections freed what they finalized.
        for (Object* object : m_Garbage) {
            object->m_State |= Object::FINALIZED;
            object->Finalize();
        }

        // Destructors may have changed references among the garbage, but none may have left one
        // to it anywhere else: without the references among it, only the collector's count is left.
        for (Object* object : m_Garbage) {
            ForEachChild(object, [](Object* child) {
                if (child->m_State & Object::FINALIZED) child->Unretain();
            });
        }
        for (Object* object : m_Garbage) {
            if (object->Refs() != 1) RaisePanic("a destructor kept a reference to an object of the cycle being collected", "<destructor>");
        }

        // Clearing a reference among the garbage releases the count just taken off for it.
        FunctionVisitor clear([](Object* child) {
            if (child->m_State & Object::FINALIZED) child->Retain();
            return true;
        });
        for (Object* object : m_Garbage) object->Visit(clear);
        for (Object* object : m_Garbage) delete object;
        m_Stats.Freed += m_Garbage.size();
    }

public:
    void Collect(size_t budget, const char* location) {
        if (m_Destroying > 0) RaisePanic("memory::collectCycles cannot be called from a destructor", location);
        ++m_Destroying;
        m_Roots.swap(m_Buffer);
        m_Holes = 0;

        size_t processed = 0;
        for (; processed < m_Roots.size(); ++processed) {
            if (m_Roots[processed] != nullptr && !MarkGray(m_Roots[processed], budget)) break;
        }
        m_Stats.Visited += m_Marked.size();

        FindGarbage(processed);
        for (size_t i = 0; i < processed; ++i) {
            Object* root = m_Roots[i];
            if (root == nullptr) continue;
            root->m_State &= ~Object::BUFFERED;
            root->SetSlot(0);
        }

        // Back in the buffer before any destructor runs, since one may destroy them.
        for (size_t i = processed; i < m_Roots.size(); ++i) {
            Object* root = m_Roots[i];
            if (root == nullptr || (root->m_State & Object::DYING)) continue;
            Add(root);
        }
        m_Roots.clear();
        m_Marked.clear();

        if (!m_Garbage.empty()) DestroyGarbage();
        m_Garbage.clear();
        if constexpr (WYL_DEBUG != 0) NoteWaiting();
        --m_Destroying;
    }

    CycleCollector() = default;
    CycleCollector(const CycleCollector&) = delete;
    CycleCollector& operator=(const CycleCollector&) = delete;

    ~CycleCollector() {
        if constexpr (WYL_DEBUG != 0) ReportUncollected();
    }

    CycleStats TakeStats() { return std::exchange(m_Stats, CycleStats{}); }
};

// Objects belong to one thread, and so will the collector's state once Wylow has threads.
inline CycleCollector s_Cycles;

inline void Object::Destroy() {
    // Holding the count at 1 lets the destructor create and drop temporary references to `this`
    // without destroying it twice; ~Object checks that none outlive it.
    Retain();
    ClearWeak();
    if (m_State & BUFFERED) s_Cycles.Remove(this);
    ++s_Cycles.m_Destroying;
    delete this;
    --s_Cycles.m_Destroying;
}

inline void Object::AddToBuffer() {
    s_Cycles.Add(this);
    ++s_Cycles.m_Stats.Buffered;
}

// Its slot holds a reference for the object's whole life, so a release never frees it and could
// only buffer it, leaving the buffer pointing into a frame that will be gone. Nothing outside its
// scope refers to it, so it is never part of an unreachable cycle anyway. Its constructor may have
// buffered it already, by releasing a reference to `this`.
inline void Object::LeaveCollector() {
    m_State &= ~CYCLIC;
    if (m_State & BUFFERED) s_Cycles.Remove(this);
}

// As Destroy(), except that the slot's reference is still held: it is the 1 that ~Object checks for.
inline void Object::DestroyInSlot(bool inPlace) {
    if constexpr (WYL_DEBUG != 0) {
        if (Refs() != 1) RaisePanic("internal compiler error: an object on the stack outlived its scope", "<runtime>");
    }
    ClearWeak();
    if (m_State & BUFFERED) s_Cycles.Remove(this);
    ++s_Cycles.m_Destroying;
    if (inPlace) {
        this->~Object();
    } else {
        delete this;
    }
    --s_Cycles.m_Destroying;
}

}  // namespace wyl
