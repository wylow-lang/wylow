// The pools hold memory that no program can observe, so these scenarios count the chunks that stay
// mapped. A chunk's owner is a Pools object and not a thread, so most scenarios drive two Pools from
// one thread, which makes the result deterministic.

#include <cstdio>
#include <set>
#include <thread>
#include <vector>

#include "wyl/pools.h"

using wyl::Pools;

namespace {

constexpr size_t SLOT_SIZE = 48;
// Enough slots to fill many chunks of this size class.
constexpr size_t SLOTS = 20000;

int s_Failures = 0;

void Expect(bool condition, const char* what) {
    if (condition) return;
    std::fprintf(stderr, "failed: %s\n", what);
    ++s_Failures;
}

std::vector<void*> AllocateAll(Pools& pools, size_t count = SLOTS) {
    std::vector<void*> slots(count);
    for (void*& slot : slots) slot = pools.Allocate(SLOT_SIZE);
    return slots;
}

void FreeAll(Pools& pools, const std::vector<void*>& slots) {
    for (void* slot : slots) pools.Deallocate(slot, SLOT_SIZE);
}

// Slots another pool frees are used again, however full their chunks were.
void RemoteFreesAreReused() {
    Pools owner;
    Pools other;
    auto slots = AllocateAll(owner);
    size_t first = owner.ChunkCount();
    Expect(first > 4, "the scenario fills several chunks");
    for (int round = 0; round < 20; ++round) {
        FreeAll(other, slots);
        slots = AllocateAll(owner);
    }
    Expect(owner.ChunkCount() <= first + first / 2, "remote frees keep the owner's chunk count bounded");
    Expect(other.ChunkCount() == 0, "a pool that frees into a live owner's chunk does not adopt it");
    FreeAll(owner, slots);
}

// A remote list holds every slot pushed onto it, not only the last.
void RemoteListKeepsEverySlot() {
    Pools other;
    size_t before = Pools::MappedChunks();
    {
        Pools ending;
        auto slots = AllocateAll(ending, 100);
        FreeAll(other, slots);
    }
    Expect(Pools::MappedChunks() == before, "an owner that ends counts all of a chunk's remote frees");
}

// An owner that ends takes back its remote lists first, so it unmaps a chunk whose slots are all freed.
void EndingOwnerUnmapsFreedChunks() {
    size_t before = Pools::MappedChunks();
    Pools other;
    {
        Pools owner;
        auto slots = AllocateAll(owner);
        FreeAll(other, slots);
    }
    Expect(Pools::MappedChunks() == before, "an ending owner unmaps the chunks other pools emptied");
}

// A chunk with live slots outlives its owner, and the next pool to free a slot adopts it.
void OrphanedChunksAreAdopted() {
    size_t before = Pools::MappedChunks();
    {
        Pools other;
        std::vector<void*> slots;
        {
            Pools owner;
            slots = AllocateAll(owner);
        }
        Expect(Pools::MappedChunks() > before, "an ending owner leaves chunks with live slots mapped");
        FreeAll(other, slots);
        Expect(other.ChunkCount() <= 1, "a pool that adopts chunks returns the emptied ones");
    }
    Expect(Pools::MappedChunks() == before, "the adopting pool unmaps what it adopted");
}

// Slots freed before the owner ends and slots freed after it both count.
void FreesBeforeAndAfterOwnerEnds() {
    size_t before = Pools::MappedChunks();
    {
        Pools remote;
        std::vector<void*> early;
        std::vector<void*> late;
        {
            Pools owner;
            auto slots = AllocateAll(owner);
            for (size_t i = 0; i < slots.size(); ++i) (i % 2 == 0 ? early : late).push_back(slots[i]);
            FreeAll(remote, early);
        }
        Pools adopter;
        FreeAll(adopter, late);
        Expect(adopter.ChunkCount() <= 1, "an adopter returns chunks emptied by frees before and after the owner ended");
    }
    Expect(Pools::MappedChunks() == before, "every chunk goes back");
}

// A chunk unmapped while a scan is due to check it next does not stop the scan.
void ScanSurvivesUnmappedChunks() {
    Pools owner;
    for (int round = 0; round < 4; ++round) {
        auto slots = AllocateAll(owner);
        FreeAll(owner, slots);
    }
    Expect(owner.ChunkCount() <= 2, "a pool keeps only its current chunk and its spare once its slots are freed");
}

// Taking back a remote list leaves the slots still in use alone.
void LiveSlotsSurviveReuse() {
    Pools owner;
    Pools other;
    auto slots = AllocateAll(owner);
    for (size_t i = 0; i < slots.size(); ++i) static_cast<size_t*>(slots[i])[1] = i;
    std::vector<void*> kept;
    std::vector<size_t> ids;
    for (size_t i = 0; i < slots.size(); ++i) {
        if (i % 2 == 0) {
            other.Deallocate(slots[i], SLOT_SIZE);
        } else {
            kept.push_back(slots[i]);
            ids.push_back(i);
        }
    }
    auto reused = AllocateAll(owner);
    std::set<void*> distinct(kept.begin(), kept.end());
    distinct.insert(reused.begin(), reused.end());
    Expect(distinct.size() == kept.size() + reused.size(), "no live slot is handed out again");
    for (void* slot : reused) static_cast<size_t*>(slot)[1] = SIZE_MAX;
    bool intact = true;
    for (size_t i = 0; i < kept.size(); ++i) intact = intact && static_cast<size_t*>(kept[i])[1] == ids[i];
    Expect(intact, "reused slots do not overlap live ones");
    FreeAll(owner, kept);
    FreeAll(owner, reused);
}

// Real threads, for ThreadSanitizer: one pool allocates while another frees, and the consumer's pool
// ends while chunks are still in use.
void ThreadsFreeConcurrently() {
    size_t before = Pools::MappedChunks();
    {
        constexpr size_t COUNT = 100000;
        std::vector<std::atomic<void*>> mailbox(COUNT);
        Pools owner;
        std::thread consumer([&mailbox] {
            Pools pools;
            for (auto& entry : mailbox) {
                void* slot = entry.load(std::memory_order_acquire);
                while (slot == nullptr) {
                    std::this_thread::yield();
                    slot = entry.load(std::memory_order_acquire);
                }
                pools.Deallocate(slot, SLOT_SIZE);
            }
        });
        for (auto& entry : mailbox) entry.store(owner.Allocate(SLOT_SIZE), std::memory_order_release);
        consumer.join();
    }
    Expect(Pools::MappedChunks() == before, "every chunk goes back after both pools end");
}

}  // namespace

int main() {
    if constexpr (WYL_POOLS != 0) {
        RemoteFreesAreReused();
        RemoteListKeepsEverySlot();
        EndingOwnerUnmapsFreedChunks();
        OrphanedChunksAreAdopted();
        FreesBeforeAndAfterOwnerEnds();
        ScanSurvivesUnmappedChunks();
        LiveSlotsSurviveReuse();
        ThreadsFreeConcurrently();
    }
    return s_Failures == 0 ? 0 : 1;
}
