#include "workers.h"

#include <thread>
#include <vector>

int64_t workers_run(int64_t (*work)(int32_t index), int32_t threads) {
    std::vector<int64_t> results(static_cast<size_t>(threads));
    std::vector<std::thread> started;
    for (int32_t i = 0; i < threads; i++)
        started.emplace_back([&results, work, i] { results[static_cast<size_t>(i)] = work(i); });
    for (auto& thread : started) thread.join();
    int64_t sum = 0;
    for (int64_t result : results) sum += result;
    return sum;
}
