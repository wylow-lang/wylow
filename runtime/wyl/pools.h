#pragma once

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
using wyl_win_size = unsigned long long;
#else
using wyl_win_size = unsigned long;
#endif
extern "C" __declspec(dllimport) void* __stdcall VirtualAlloc(void*, wyl_win_size, unsigned long, unsigned long);
extern "C" __declspec(dllimport) int __stdcall VirtualFree(void*, wyl_win_size, unsigned long);
#endif

namespace wyl {

// Objects up to max_size bytes come from 64 KiB chunks, each split into equal slots of one size
// class. A slot is taken from the size class's current chunk, from its free list or else its
// uncarved end; a freed slot goes back to its own chunk's list, found by masking its address. When
// the current chunk is full, the fullest chunk that has room becomes current, so that new objects
// fill few chunks while the others drain. A chunk left empty goes back to the OS, except one kept
// per size class, so that a count of objects hovering at a chunk's edge doesn't allocate and free
// a chunk each time. Larger objects use the global heap.
class Pools {
public:
    static constexpr size_t max_size = 512;

private:
    static constexpr size_t granule = 16;
    static constexpr size_t chunk_size = size_t{64} * 1024;
    static constexpr size_t class_count = max_size / granule + 1;
    // Chunks that are neither current nor full are listed by how full they are, in bins of equal width.
    static constexpr uint32_t bin_count = 8;
    static constexpr uint8_t unlisted = bin_count;

    struct Slot {
        Slot* next;
    };

    struct Chunk {
        Slot* free = nullptr;
        char* bump = nullptr;
        char* end = nullptr;
        Chunk* prev = nullptr;
        Chunk* next = nullptr;
        uint32_t live = 0;
        // A free that leaves fewer live slots than this has the chunk change bins: 0 for the
        // current chunk, which is in none, and the capacity for a full one, which isn't listed yet.
        uint32_t floor = 0;
        uint32_t capacity = 0;
        uint8_t size_class = 0;
        uint8_t bin = unlisted;
    };

    // Slots start 16-aligned, so over-aligned classes use the global heap instead.
    static constexpr size_t header_size = (sizeof(Chunk) + granule - 1) / granule * granule;

    // Current until a size class first allocates, so that taking a slot needs no null check.
    static Chunk no_chunk;

    struct SizeClass {
        Chunk* current = &no_chunk;
        Chunk* spare = nullptr;
        Chunk* bins[bin_count] = {};
    };

    SizeClass classes[class_count];

    static Chunk* chunk_of(void* slot) {
        return reinterpret_cast<Chunk*>(reinterpret_cast<uintptr_t>(slot) & ~uintptr_t{chunk_size - 1});
    }

    static void* map_chunk() {
#if defined(_WIN32)
        // Windows hands out address space 64 KiB-aligned.
        return VirtualAlloc(nullptr, chunk_size, 0x1000 | 0x2000, 0x04);   // MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE
#else
        return std::aligned_alloc(chunk_size, chunk_size);
#endif
    }

    static void unmap_chunk(Chunk* chunk) {
#if defined(_WIN32)
        VirtualFree(chunk, 0, 0x8000);   // MEM_RELEASE
#else
        std::free(chunk);
#endif
    }

    static void reset(Chunk* chunk) {
        chunk->free = nullptr;
        chunk->bump = reinterpret_cast<char*>(chunk) + header_size;
        chunk->end = chunk->bump + size_t{chunk->capacity} * chunk->size_class * granule;
    }

    static Chunk* new_chunk(size_t index) {
        void* memory = map_chunk();
        if (memory == nullptr) raise_panic("out of memory", "<runtime>");
        Chunk* chunk = ::new (memory) Chunk{};
        chunk->size_class = static_cast<uint8_t>(index);
        chunk->capacity = static_cast<uint32_t>((chunk_size - header_size) / (index * granule));
        reset(chunk);
        return chunk;
    }

    void link(SizeClass& c, Chunk* chunk) {
        uint32_t bin = chunk->live * bin_count / chunk->capacity;
        uint32_t floor = (bin * chunk->capacity + bin_count - 1) / bin_count;
        chunk->bin = static_cast<uint8_t>(bin);
        chunk->floor = floor > 0 ? floor : 1;
        chunk->prev = nullptr;
        chunk->next = c.bins[bin];
        if (chunk->next != nullptr) chunk->next->prev = chunk;
        c.bins[bin] = chunk;
    }

    void unlink(SizeClass& c, Chunk* chunk) {
        if (chunk->prev != nullptr) {
            chunk->prev->next = chunk->next;
        } else {
            c.bins[chunk->bin] = chunk->next;
        }
        if (chunk->next != nullptr) chunk->next->prev = chunk->prev;
        chunk->bin = unlisted;
    }

    void* refill(size_t index) {
        SizeClass& c = classes[index];
        if (c.current != &no_chunk) c.current->floor = c.current->capacity;
        Chunk* chunk = nullptr;
        for (uint32_t bin = bin_count; bin-- > 0 && chunk == nullptr;) chunk = c.bins[bin];
        if (chunk != nullptr) {
            unlink(c, chunk);
        } else {
            chunk = c.spare != nullptr ? std::exchange(c.spare, nullptr) : new_chunk(index);
        }
        chunk->floor = 0;
        c.current = chunk;
        return allocate(index * granule);
    }

    void rebin(Chunk* chunk) {
        SizeClass& c = classes[chunk->size_class];
        if (chunk->bin != unlisted) unlink(c, chunk);
        if (chunk->live > 0) {
            link(c, chunk);
        } else if (c.spare == nullptr) {
            reset(chunk);
            c.spare = chunk;
        } else {
            unmap_chunk(chunk);
        }
    }

public:
    void* allocate(size_t size) {
        if constexpr (WYL_POOLS == 0) {
            return ::operator new(size);
        } else {
            if (size > max_size) return ::operator new(size);
            size_t index = (size + granule - 1) / granule;
            Chunk* chunk = classes[index].current;
            if (Slot* slot = chunk->free) {
                chunk->free = slot->next;
                ++chunk->live;
                return slot;
            }
            if (chunk->bump != chunk->end) {
                char* slot = chunk->bump;
                chunk->bump += index * granule;
                ++chunk->live;
                return slot;
            }
            return refill(index);
        }
    }

    void deallocate(void* slot, size_t size) {
        if (WYL_POOLS == 0 || size > max_size) {
            ::operator delete(slot, size);
            return;
        }
        Chunk* chunk = chunk_of(slot);
        chunk->free = ::new (slot) Slot{chunk->free};
        if (--chunk->live < chunk->floor) rebin(chunk);
    }
};

inline Pools::Chunk Pools::no_chunk{};

// Objects belong to one thread, and so will the pools once Wylow has threads; a `shared class`
// object, which may be freed on any thread, will need the global heap or its own pools.
inline Pools pools;

}  // namespace wyl
