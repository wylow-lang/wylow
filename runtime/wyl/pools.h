#pragma once

#include <atomic>
#include <vector>

#include "core.h"

// Objects come from the runtime's pools, except under AddressSanitizer, which can only catch a use
// after free of memory the global heap hands out.
#ifndef WYL_POOLS
#if defined(__SANITIZE_ADDRESS__)
#define WYL_POOLS 0
#elif defined(__has_feature)
#if __has_feature(address_sanitizer)
#define WYL_POOLS 0
#endif
#endif
#endif
#ifndef WYL_POOLS
#define WYL_POOLS 1
#endif

// Declared here rather than by including <windows.h>, whose macros would collide with Wylow names.
#if defined(_WIN32)
#if defined(_WIN64)
using WylWinSize = unsigned long long;
#else
using WylWinSize = unsigned long;
#endif
extern "C" __declspec(dllimport) void* __stdcall VirtualAlloc(void*, WylWinSize, unsigned long, unsigned long);
extern "C" __declspec(dllimport) int __stdcall VirtualFree(void*, WylWinSize, unsigned long);
#endif

namespace wyl {

// Objects up to MAX_SIZE bytes come from 64 KiB chunks, each split into equal slots of one size
// class. A slot is taken from the size class's current chunk, from its free list or else its
// uncarved end; a freed slot goes back to its own chunk's list, found by masking its address. When
// the current chunk is full, the fullest chunk that has room becomes current, so that new objects
// fill few chunks while the others drain. A chunk left empty goes back to the OS, except one kept
// per size class, so that a count of objects hovering at a chunk's edge doesn't allocate and free
// a chunk each time. Larger objects use the global heap.
class Pools {
public:
    static constexpr size_t MAX_SIZE = 512;

private:
    static constexpr size_t GRANULE = 16;
    static constexpr size_t CHUNK_SIZE = size_t{64} * 1024;
    static constexpr size_t CLASS_COUNT = MAX_SIZE / GRANULE + 1;
    // Chunks that are neither current nor full are listed by how full they are, in bins of equal width.
    static constexpr uint32_t BIN_COUNT = 8;
    static constexpr uint8_t UNLISTED = BIN_COUNT;

    struct Slot {
        Slot* Next;
    };

    struct Chunk {
        Slot* Free = nullptr;
        char* Bump = nullptr;
        char* End = nullptr;
        Chunk* Prev = nullptr;
        Chunk* Next = nullptr;
        uint32_t Live = 0;
        // A free that leaves fewer live slots than this has the chunk change bins: 0 for the
        // current chunk, which is in none, and the capacity for a full one, which isn't listed yet.
        uint32_t Floor = 0;
        uint32_t Capacity = 0;
        uint8_t SizeClass = 0;
        uint8_t Bin = UNLISTED;
        std::atomic<Slot*> Remote{nullptr};
        std::atomic<Pools*> Owner{nullptr};
    };

    // Slots start 16-aligned, so over-aligned classes use the global heap instead.
    static constexpr size_t HEADER_SIZE = (sizeof(Chunk) + GRANULE - 1) / GRANULE * GRANULE;

    // Current until a size class first allocates, so that taking a slot needs no null check.
    static Chunk s_NoChunk;

    struct SizeClass {
        Chunk* Current = &s_NoChunk;
        Chunk* Spare = nullptr;
        Chunk* Bins[BIN_COUNT] = {};
    };

    SizeClass m_Classes[CLASS_COUNT];
    std::vector<Chunk*> m_AllChunks;

    static Chunk* ChunkOf(void* slot) {
        return reinterpret_cast<Chunk*>(reinterpret_cast<uintptr_t>(slot) & ~uintptr_t{CHUNK_SIZE - 1});
    }

    static void* MapChunk() {
#if defined(_WIN32)
        // Windows hands out address space 64 KiB-aligned.
        return VirtualAlloc(nullptr, CHUNK_SIZE, 0x1000 | 0x2000, 0x04);   // MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE
#else
        return std::aligned_alloc(CHUNK_SIZE, CHUNK_SIZE);
#endif
    }

    static void UnmapChunk(Chunk* chunk) {
#if defined(_WIN32)
        VirtualFree(chunk, 0, 0x8000);   // MEM_RELEASE
#else
        std::free(chunk);
#endif
    }

    static void Reset(Chunk* chunk) {
        chunk->Free = nullptr;
        chunk->Bump = reinterpret_cast<char*>(chunk) + HEADER_SIZE;
        chunk->End = chunk->Bump + size_t{chunk->Capacity} * chunk->SizeClass * GRANULE;
    }

    Chunk* NewChunk(size_t index) {
        void* memory = MapChunk();
        if (memory == nullptr) RaisePanic("out of memory", "<runtime>");
        Chunk* chunk = ::new (memory) Chunk{};
        chunk->SizeClass = static_cast<uint8_t>(index);
        chunk->Capacity = static_cast<uint32_t>((CHUNK_SIZE - HEADER_SIZE) / (index * GRANULE));
        Reset(chunk);
        chunk->Owner.store(this, std::memory_order_relaxed);
        m_AllChunks.push_back(chunk);
        return chunk;
    }

    void Forget(Chunk* chunk) {
        for (size_t i = 0; i < m_AllChunks.size(); ++i) {
            if (m_AllChunks[i] != chunk) continue;
            m_AllChunks[i] = m_AllChunks.back();
            m_AllChunks.pop_back();
            return;
        }
    }

    void Link(SizeClass& c, Chunk* chunk) {
        uint32_t bin = chunk->Live * BIN_COUNT / chunk->Capacity;
        uint32_t floor = (bin * chunk->Capacity + BIN_COUNT - 1) / BIN_COUNT;
        chunk->Bin = static_cast<uint8_t>(bin);
        chunk->Floor = floor > 0 ? floor : 1;
        chunk->Prev = nullptr;
        chunk->Next = c.Bins[bin];
        if (chunk->Next != nullptr) chunk->Next->Prev = chunk;
        c.Bins[bin] = chunk;
    }

    void Unlink(SizeClass& c, Chunk* chunk) {
        if (chunk->Prev != nullptr) {
            chunk->Prev->Next = chunk->Next;
        } else {
            c.Bins[chunk->Bin] = chunk->Next;
        }
        if (chunk->Next != nullptr) chunk->Next->Prev = chunk->Prev;
        chunk->Bin = UNLISTED;
    }

    void* Refill(size_t index) {
        SizeClass& c = m_Classes[index];
        if (c.Current != &s_NoChunk) c.Current->Floor = c.Current->Capacity;
        Chunk* chunk = nullptr;
        for (uint32_t bin = BIN_COUNT; bin-- > 0 && chunk == nullptr;) chunk = c.Bins[bin];
        if (chunk != nullptr) {
            Unlink(c, chunk);
        } else {
            chunk = c.Spare != nullptr ? std::exchange(c.Spare, nullptr) : NewChunk(index);
        }
        chunk->Floor = 0;
        c.Current = chunk;
        return Allocate(index * GRANULE);
    }

    void Rebin(Chunk* chunk) {
        SizeClass& c = m_Classes[chunk->SizeClass];
        if (chunk->Bin != UNLISTED) Unlink(c, chunk);
        if (chunk->Live > 0) {
            Link(c, chunk);
        } else if (c.Spare == nullptr) {
            Reset(chunk);
            c.Spare = chunk;
        } else {
            Forget(chunk);
            UnmapChunk(chunk);
        }
    }

    Slot* DrainRemote(Chunk* chunk) {
        Slot* list = chunk->Remote.exchange(nullptr, std::memory_order_acquire);
        if (list == nullptr) return nullptr;
        uint32_t count = 1;
        Slot* tail = list;
        while (tail->Next != nullptr) {
            tail = tail->Next;
            ++count;
        }
        tail->Next = chunk->Free;
        chunk->Free = list;
        chunk->Live -= count;
        return list;
    }

    void FreeRemotely(Chunk* chunk, void* slot) {
        Slot* node = ::new (slot) Slot{nullptr};
        Slot* head = chunk->Remote.load(std::memory_order_relaxed);
        while (!chunk->Remote.compare_exchange_weak(head, node, std::memory_order_release, std::memory_order_relaxed)) {
            node->Next = head;
        }
    }

    void Adopt(Chunk* chunk, void* slot) {
        Pools* expected = nullptr;
        if (!chunk->Owner.compare_exchange_strong(expected, this, std::memory_order_acq_rel, std::memory_order_acquire)) {
            FreeRemotely(chunk, slot);
            return;
        }
        chunk->Prev = nullptr;
        chunk->Next = nullptr;
        chunk->Bin = UNLISTED;
        chunk->Free = ::new (slot) Slot{chunk->Free};
        --chunk->Live;
        m_AllChunks.push_back(chunk);
        Rebin(chunk);
    }

public:
    Pools() = default;
    Pools(const Pools&) = delete;
    Pools& operator=(const Pools&) = delete;

    ~Pools() {
        for (Chunk* chunk : m_AllChunks) {
            if (chunk->Live == 0) {
                UnmapChunk(chunk);
            } else {
                chunk->Owner.store(nullptr, std::memory_order_release);
            }
        }
    }

    void* Allocate(size_t size) {
        if constexpr (WYL_POOLS == 0) {
            return ::operator new(size);
        } else {
            if (size > MAX_SIZE) return ::operator new(size);
            size_t index = (size + GRANULE - 1) / GRANULE;
            Chunk* chunk = m_Classes[index].Current;
            if (Slot* slot = chunk->Free) {
                chunk->Free = slot->Next;
                ++chunk->Live;
                return slot;
            }
            if (chunk->Remote.load(std::memory_order_relaxed) != nullptr && DrainRemote(chunk) != nullptr) {
                Slot* slot = chunk->Free;
                chunk->Free = slot->Next;
                ++chunk->Live;
                return slot;
            }
            if (chunk->Bump != chunk->End) {
                char* slot = chunk->Bump;
                chunk->Bump += index * GRANULE;
                ++chunk->Live;
                return slot;
            }
            return Refill(index);
        }
    }

    void Deallocate(void* slot, size_t size) {
        if (WYL_POOLS == 0 || size > MAX_SIZE) {
            ::operator delete(slot, size);
            return;
        }
        Chunk* chunk = ChunkOf(slot);
        Pools* owner = chunk->Owner.load(std::memory_order_acquire);
        if (owner == this) {
            chunk->Free = ::new (slot) Slot{chunk->Free};
            if (--chunk->Live < chunk->Floor) Rebin(chunk);
        } else if (owner == nullptr) {
            Adopt(chunk, slot);
        } else {
            FreeRemotely(chunk, slot);
        }
    }
};

inline Pools::Chunk Pools::s_NoChunk{};

Pools& CurrentPools();

}  // namespace wyl
