#pragma once

#include <atomic>

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
// fill few chunks while the others drain. A chunk left empty goes on a stack that any size class
// reuses from, so that freeing and allocating make no system call while the pool's count of objects
// swings. Only an empty chunk beyond MAX_EMPTY_CHUNKS goes back to the OS. Larger objects use the
// global heap.
//
// A slot freed by another pool goes on its chunk's remote list, and nothing tells the owner. The
// owner finds such a list on its current chunk when that runs out. Before it maps a new chunk, it
// also checks the next SCAN_CHUNKS of its other chunks, so that no allocation pauses to visit them all.
// Memory that waits on a remote list stays within a quarter of the pool's chunks, since a full pass
// takes that many new chunks.
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
    static constexpr size_t SCAN_CHUNKS = 4;
    static constexpr size_t MAX_EMPTY_CHUNKS = 64;

    struct Slot {
        Slot* Next;
    };

    struct Chunk {
        Slot* Free = nullptr;
        char* Bump = nullptr;
        char* End = nullptr;
        Chunk* Prev = nullptr;
        Chunk* Next = nullptr;
        // Links in the list of every chunk the pool owns. Prev and Next link only the chunks of one bin.
        Chunk* AllPrev = nullptr;
        Chunk* AllNext = nullptr;
        uint32_t Live = 0;
        // A free that leaves fewer live slots than this has the chunk change bins: 0 for the
        // current chunk, which is in none, and the capacity for a full one, which isn't listed yet.
        uint32_t Floor = 0;
        uint32_t Capacity = 0;
        uint8_t SizeClass = 0;
        uint8_t Bin = UNLISTED;
        // Slots other pools freed, or Orphaned() once the owner has ended. One word holds both, so a
        // free either reaches a list that an owner will take back or finds that it must adopt the chunk.
        std::atomic<Slot*> Remote{nullptr};
        std::atomic<Pools*> Owner{nullptr};
    };

    static Slot* Orphaned() { return reinterpret_cast<Slot*>(uintptr_t{1}); }

    // Slots start 16-aligned, so over-aligned classes use the global heap instead.
    static constexpr size_t HEADER_SIZE = (sizeof(Chunk) + GRANULE - 1) / GRANULE * GRANULE;

    // Current until a size class first allocates, so that taking a slot needs no null check.
    static Chunk s_NoChunk;

    struct SizeClass {
        Chunk* Current = &s_NoChunk;
        Chunk* Bins[BIN_COUNT] = {};
    };

    SizeClass m_Classes[CLASS_COUNT];
    Chunk* m_AllChunks = nullptr;
    // The next chunk a scan checks, or null to start again from the list's head.
    Chunk* m_ScanNext = nullptr;
    size_t m_ChunkCount = 0;
    // A stack through Chunk::Next. These chunks stay in the list of all chunks.
    Chunk* m_EmptyChunks = nullptr;
    size_t m_EmptyChunkCount = 0;

    inline static std::atomic<size_t> s_MappedChunks{0};

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
        s_MappedChunks.fetch_sub(1, std::memory_order_relaxed);
#if defined(_WIN32)
        VirtualFree(chunk, 0, 0x8000);   // MEM_RELEASE
#else
        std::free(chunk);
#endif
    }

    static void Format(Chunk* chunk, size_t index) {
        chunk->SizeClass = static_cast<uint8_t>(index);
        chunk->Capacity = static_cast<uint32_t>((CHUNK_SIZE - HEADER_SIZE) / (index * GRANULE));
        chunk->Free = nullptr;
        chunk->Bump = reinterpret_cast<char*>(chunk) + HEADER_SIZE;
        chunk->End = chunk->Bump + size_t{chunk->Capacity} * chunk->SizeClass * GRANULE;
    }

    Chunk* NewChunk(size_t index) {
        void* memory = MapChunk();
        if (memory == nullptr) RaisePanic("out of memory", "<runtime>");
        s_MappedChunks.fetch_add(1, std::memory_order_relaxed);
        Chunk* chunk = ::new (memory) Chunk{};
        Format(chunk, index);
        chunk->Owner.store(this, std::memory_order_relaxed);
        Remember(chunk);
        return chunk;
    }

    void Remember(Chunk* chunk) {
        chunk->AllPrev = nullptr;
        chunk->AllNext = m_AllChunks;
        if (m_AllChunks != nullptr) m_AllChunks->AllPrev = chunk;
        m_AllChunks = chunk;
        ++m_ChunkCount;
    }

    void Forget(Chunk* chunk) {
        if (m_ScanNext == chunk) m_ScanNext = chunk->AllNext;
        if (chunk->AllPrev != nullptr) {
            chunk->AllPrev->AllNext = chunk->AllNext;
        } else {
            m_AllChunks = chunk->AllNext;
        }
        if (chunk->AllNext != nullptr) chunk->AllNext->AllPrev = chunk->AllPrev;
        --m_ChunkCount;
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

    Chunk* TakeHeldChunk(size_t index) {
        SizeClass& c = m_Classes[index];
        Chunk* chunk = nullptr;
        for (uint32_t bin = BIN_COUNT; bin-- > 0 && chunk == nullptr;) chunk = c.Bins[bin];
        if (chunk != nullptr) {
            Unlink(c, chunk);
            return chunk;
        }
        chunk = m_EmptyChunks;
        if (chunk != nullptr) {
            m_EmptyChunks = chunk->Next;
            --m_EmptyChunkCount;
            Format(chunk, index);
        }
        return chunk;
    }

    // A chunk that is full is in no bin, so only this scan finds what other pools freed from it.
    void ScanNextChunk() {
        Chunk* chunk = m_ScanNext != nullptr ? m_ScanNext : m_AllChunks;
        if (chunk == nullptr) return;
        m_ScanNext = chunk->AllNext;
        if (chunk != m_Classes[chunk->SizeClass].Current && DrainRemote(chunk)) Rebin(chunk);
    }

    void* Refill(size_t index) {
        SizeClass& c = m_Classes[index];
        if (c.Current != &s_NoChunk) c.Current->Floor = c.Current->Capacity;
        c.Current = &s_NoChunk;
        Chunk* chunk = TakeHeldChunk(index);
        for (size_t i = 0; i < SCAN_CHUNKS && chunk == nullptr; ++i) {
            ScanNextChunk();
            chunk = TakeHeldChunk(index);
        }
        if (chunk == nullptr) chunk = NewChunk(index);
        chunk->Floor = 0;
        c.Current = chunk;
        return Allocate(index * GRANULE);
    }

    void Rebin(Chunk* chunk) {
        SizeClass& c = m_Classes[chunk->SizeClass];
        if (chunk->Bin != UNLISTED) Unlink(c, chunk);
        if (chunk->Live > 0) {
            Link(c, chunk);
        } else if (m_EmptyChunkCount < MAX_EMPTY_CHUNKS) {
            chunk->Next = m_EmptyChunks;
            m_EmptyChunks = chunk;
            ++m_EmptyChunkCount;
        } else {
            Forget(chunk);
            UnmapChunk(chunk);
        }
    }

    bool DrainRemote(Chunk* chunk) {
        if (chunk->Remote.load(std::memory_order_relaxed) == nullptr) return false;
        Slot* list = chunk->Remote.exchange(nullptr, std::memory_order_acquire);
        uint32_t count = 1;
        Slot* tail = list;
        while (tail->Next != nullptr) {
            tail = tail->Next;
            ++count;
        }
        tail->Next = chunk->Free;
        chunk->Free = list;
        chunk->Live -= count;
        return true;
    }

    // Nothing touches the chunk after the push, since the owner may unmap it as soon as the slot is on
    // the list.
    void FreeRemotely(Chunk* chunk, void* slot) {
        Slot* head = chunk->Remote.load(std::memory_order_relaxed);
        Slot* node = ::new (slot) Slot{nullptr};
        while (true) {
            if (head == Orphaned()) {
                if (chunk->Remote.compare_exchange_weak(head, nullptr, std::memory_order_acquire, std::memory_order_relaxed)) {
                    Adopt(chunk, slot);
                    return;
                }
                continue;
            }
            node->Next = head;
            if (chunk->Remote.compare_exchange_weak(head, node, std::memory_order_release, std::memory_order_relaxed)) return;
        }
    }

    void Adopt(Chunk* chunk, void* slot) {
        chunk->Owner.store(this, std::memory_order_relaxed);
        chunk->Prev = nullptr;
        chunk->Next = nullptr;
        chunk->Bin = UNLISTED;
        chunk->Free = ::new (slot) Slot{chunk->Free};
        --chunk->Live;
        Remember(chunk);
        Rebin(chunk);
    }

public:
    Pools() = default;
    Pools(const Pools&) = delete;
    Pools& operator=(const Pools&) = delete;

    // Publishing Orphaned() gives the chunk away, so nothing touches it afterward. It succeeds only on
    // an empty list, so that no slot freed before the owner ended is left where nobody looks.
    ~Pools() {
        for (Chunk* next = m_AllChunks; next != nullptr;) {
            Chunk* chunk = next;
            next = chunk->AllNext;
            chunk->Owner.store(nullptr, std::memory_order_relaxed);
            while (true) {
                DrainRemote(chunk);
                if (chunk->Live == 0) {
                    UnmapChunk(chunk);
                    break;
                }
                Slot* expected = nullptr;
                if (chunk->Remote.compare_exchange_strong(expected, Orphaned(), std::memory_order_release, std::memory_order_relaxed)) break;
            }
        }
    }

    size_t ChunkCount() const { return m_ChunkCount; }

    size_t EmptyChunkCount() const { return m_EmptyChunkCount; }

    static size_t MappedChunks() { return s_MappedChunks.load(std::memory_order_relaxed); }

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
            if (DrainRemote(chunk)) {
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
        if (chunk->Owner.load(std::memory_order_relaxed) == this) {
            chunk->Free = ::new (slot) Slot{chunk->Free};
            if (--chunk->Live < chunk->Floor) Rebin(chunk);
        } else {
            FreeRemotely(chunk, slot);
        }
    }
};

inline Pools::Chunk Pools::s_NoChunk{};

Pools& CurrentPools();

}  // namespace wyl
