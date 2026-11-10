// Reference-token tests at the end of the token range, through the exported
// functions of the reference-wrap fixture (tests/luau_host_reference_wrap_fixture.cpp).
// The fixture is the host compiled from its own source plus a seam that
// positions the process allocator, so these tests reach the last values of
// the range without creating two billion references first.

#include "luau_host.h"
#include "luau_host_reference_wrap_fixture.h"

#include <atomic>
#include <cstdint>
#include <exception>
#include <iostream>
#include <iterator>
#include <limits>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <unordered_map>
#include <utility>
#include <vector>

namespace
{
class TestFailure final : public std::runtime_error
{
public:
    TestFailure(const char* expression, const char* file, int line)
        : std::runtime_error(
              std::string(file) + ":" + std::to_string(line) + ": requirement failed: " + expression)
    {
    }
};

#define REQUIRE(expression)                                                                                              \
    do                                                                                                                   \
    {                                                                                                                    \
        if (!(expression))                                                                                               \
            throw TestFailure(#expression, __FILE__, __LINE__);                                                         \
    } while (false)

constexpr int32_t kMaximum = std::numeric_limits<int32_t>::max();

struct Root
{
    luau_host_state* state = nullptr;

    Root()
    {
        luau_host_memory_info failure = {};
        failure.struct_size = sizeof(failure);
        REQUIRE(luau_host_state_create(nullptr, &state, &failure) == LUAU_HOST_STATUS_OK);
        REQUIRE(state != nullptr);
    }

    Root(const Root&) = delete;
    Root& operator=(const Root&) = delete;

    ~Root()
    {
        close();
    }

    void close()
    {
        if (state)
        {
            luau_host_state_close(state);
            state = nullptr;
        }
    }
};

// Creates a reference to a new integer value and leaves the stack as it was.
luau_host_status create(luau_host_state* state, int64_t value, int32_t* token)
{
    REQUIRE(luau_host_push_integer(state, value) == LUAU_HOST_STATUS_OK);
    const int32_t top = luau_host_stack_get_top(state);
    const luau_host_status status = luau_host_reference_create(state, -1, token);
    REQUIRE(luau_host_stack_get_top(state) == top);
    REQUIRE(luau_host_stack_set_top(state, -2) == LUAU_HOST_STATUS_OK);
    return status;
}

int32_t require_create(luau_host_state* state, int64_t value)
{
    int32_t token = 0;
    REQUIRE(create(state, value, &token) == LUAU_HOST_STATUS_OK);
    REQUIRE(token > 0);
    return token;
}

// Pushes a reference and returns the integer it holds, or nothing when the
// host rejects the token; the stack is left as it was either way.
bool read(luau_host_state* state, int32_t token, int64_t* value)
{
    const int32_t top = luau_host_stack_get_top(state);
    int32_t type = -1;
    const luau_host_status status = luau_host_reference_push(state, token, &type);
    if (status != LUAU_HOST_STATUS_OK)
    {
        REQUIRE(status == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        REQUIRE(luau_host_stack_get_top(state) == top);
        return false;
    }

    int32_t isInteger = 0;
    *value = luau_host_to_integer64(state, -1, &isInteger);
    REQUIRE(isInteger == 1);
    REQUIRE(luau_host_stack_set_top(state, -2) == LUAU_HOST_STATUS_OK);
    return true;
}

int64_t require_read(luau_host_state* state, int32_t token)
{
    int64_t value = 0;
    REQUIRE(read(state, token, &value));
    return value;
}

// The process keeps issuing tokens after the last value: the cursor continues
// from 1, passing values that are live, on every root, new ones included.
void test_creation_continues_after_the_last_token()
{
    const uint64_t baseline = luau_host_test_reference_tokens_live();
    Root first;
    Root second;

    luau_host_test_reference_tokens_seek(1);
    const int32_t early = require_create(first.state, 7);
    REQUIRE(early == 1);

    luau_host_test_reference_tokens_seek(kMaximum - 1);
    const int32_t penultimate = require_create(first.state, 1);
    const int32_t last = require_create(second.state, 2);
    REQUIRE(penultimate == kMaximum - 1);
    REQUIRE(last == kMaximum);

    const int32_t wrapped = require_create(first.state, 3);
    REQUIRE(wrapped == 2);
    REQUIRE(require_create(second.state, 4) == 3);
    int32_t fromNewRoot = 0;
    {
        Root third;
        fromNewRoot = require_create(third.state, 5);
        REQUIRE(fromNewRoot == 4);
        REQUIRE(require_read(third.state, fromNewRoot) == 5);
    }

    REQUIRE(require_read(first.state, early) == 7);
    REQUIRE(require_read(first.state, penultimate) == 1);
    REQUIRE(require_read(second.state, last) == 2);
    REQUIRE(require_read(first.state, wrapped) == 3);
    REQUIRE(require_read(second.state, 3) == 4);

    // Closing the third root released its value; the cursor has not reached
    // it again, so it stays invalid everywhere.
    int64_t value = 0;
    REQUIRE(!read(first.state, fromNewRoot, &value));
    REQUIRE(!read(second.state, fromNewRoot, &value));

    REQUIRE(luau_host_reference_release(first.state, early) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_reference_release(first.state, penultimate) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_reference_release(first.state, wrapped) == LUAU_HOST_STATUS_OK);
    second.close();
    REQUIRE(luau_host_test_reference_tokens_live() == baseline);
}

// Across the wrap, on three roots, a value is never issued while it is live,
// and every live token reads back its own value on its own root only.
void test_live_values_are_never_issued_twice()
{
    const uint64_t baseline = luau_host_test_reference_tokens_live();
    Root roots[3];
    std::unordered_map<int32_t, std::pair<size_t, int64_t>> live;
    uint32_t random = 0x2545f491u;
    auto next = [&random] {
        random ^= random << 13;
        random ^= random >> 17;
        random ^= random << 5;
        return random;
    };

    // Long-lived values at the bottom of the range, which the cursor must
    // pass after the wrap.
    luau_host_test_reference_tokens_seek(1);
    for (int64_t index = 0; index < 64; ++index)
    {
        const size_t root = size_t(index % 3);
        const int32_t token = require_create(roots[root].state, 1'000'000 + index);
        REQUIRE(live.emplace(token, std::make_pair(root, 1'000'000 + index)).second);
    }

    luau_host_test_reference_tokens_seek(kMaximum - 500);
    bool wrapped = false;
    int32_t previous = 0;
    for (int64_t step = 0; step < 20'000; ++step)
    {
        if (live.size() < 200 || (next() & 3) != 0)
        {
            const size_t root = next() % 3;
            const int32_t token = require_create(roots[root].state, step);
            REQUIRE(live.emplace(token, std::make_pair(root, step)).second);
            wrapped = wrapped || token < previous;
            previous = token;
        }
        else
        {
            auto victim = live.begin();
            std::advance(victim, next() % live.size());
            const auto [token, owner] = *victim;
            REQUIRE(require_read(roots[owner.first].state, token) == owner.second);
            int64_t ignored = 0;
            REQUIRE(!read(roots[(owner.first + 1) % 3].state, token, &ignored));
            REQUIRE(luau_host_reference_release(roots[owner.first].state, token) == LUAU_HOST_STATUS_OK);
            REQUIRE(!read(roots[owner.first].state, token, &ignored));
            live.erase(victim);
        }
    }
    REQUIRE(wrapped);
    REQUIRE(luau_host_test_reference_tokens_live() == baseline + live.size());

    for (const auto& [token, owner] : live)
    {
        REQUIRE(require_read(roots[owner.first].state, token) == owner.second);
        REQUIRE(luau_host_reference_release(roots[owner.first].state, token) == LUAU_HOST_STATUS_OK);
    }
    REQUIRE(luau_host_test_reference_tokens_live() == baseline);
}

// The same with several threads, a root each, creating and releasing at once
// while the cursor wraps.
void test_live_values_are_never_issued_twice_concurrently()
{
    constexpr int kThreads = 6;
    constexpr int kSteps = 20'000;
    const uint64_t baseline = luau_host_test_reference_tokens_live();
    std::mutex holderMutex;
    std::unordered_map<int32_t, int> holder;
    std::atomic<int> failures{0};
    std::atomic<int> ready{0};
    std::atomic<bool> wrapped{false};

    // Long-lived values at the bottom of the range, which the threads' cursor
    // must pass after the wrap.
    Root keeper;
    std::vector<int32_t> kept;
    luau_host_test_reference_tokens_seek(1);
    for (int64_t index = 0; index < 256; ++index)
    {
        kept.push_back(require_create(keeper.state, 5'000'000 + index));
        REQUIRE(holder.emplace(kept.back(), -1).second);
    }

    luau_host_test_reference_tokens_seek(kMaximum - 2'000);
    std::vector<std::thread> threads;
    for (int thread = 0; thread < kThreads; ++thread)
    {
        threads.emplace_back([&, thread] {
            try
            {
                Root root;
                std::vector<std::pair<int32_t, int64_t>> held;
                uint32_t random = 0x9e3779b9u * uint32_t(thread + 1);
                int32_t previous = 0;
                ready.fetch_add(1);
                while (ready.load() != kThreads)
                    std::this_thread::yield();

                for (int step = 0; step < kSteps; ++step)
                {
                    random ^= random << 13;
                    random ^= random >> 17;
                    random ^= random << 5;
                    if (held.size() < 16 || (held.size() < 64 && (random & 1) != 0))
                    {
                        const int64_t value = int64_t(thread) * 1'000'000 + step;
                        const int32_t token = require_create(root.state, value);
                        {
                            std::lock_guard<std::mutex> lock(holderMutex);
                            if (!holder.emplace(token, thread).second)
                                failures.fetch_add(1);
                        }
                        if (token < previous)
                            wrapped.store(true);
                        previous = token;
                        held.emplace_back(token, value);
                    }
                    else
                    {
                        const size_t index = size_t(random >> 8) % held.size();
                        const auto [token, value] = held[index];
                        held[index] = held.back();
                        held.pop_back();
                        if (require_read(root.state, token) != value)
                            failures.fetch_add(1);
                        {
                            std::lock_guard<std::mutex> lock(holderMutex);
                            if (holder.erase(token) != 1)
                                failures.fetch_add(1);
                        }
                        REQUIRE(luau_host_reference_release(root.state, token) == LUAU_HOST_STATUS_OK);
                    }
                }

                // Closing the root releases what it still holds.
                std::lock_guard<std::mutex> lock(holderMutex);
                for (const auto& entry : held)
                    holder.erase(entry.first);
                root.close();
            }
            catch (...)
            {
                failures.fetch_add(1);
            }
        });
    }

    for (auto& thread : threads)
        thread.join();

    REQUIRE(failures.load() == 0);
    REQUIRE(wrapped.load());
    for (size_t index = 0; index < kept.size(); ++index)
    {
        REQUIRE(require_read(keeper.state, kept[index]) == 5'000'000 + int64_t(index));
        REQUIRE(holder.erase(kept[index]) == 1);
    }
    REQUIRE(holder.empty());
    keeper.close();
    REQUIRE(luau_host_test_reference_tokens_live() == baseline);
}

// A released token stays invalid before and after the wrap, on every root,
// until the cursor issues its value again; then it names the new reference,
// on the new owner's root only.
void test_stale_tokens_stay_invalid_until_reissued()
{
    const uint64_t baseline = luau_host_test_reference_tokens_live();
    Root first;
    Root second;
    int64_t ignored = 0;

    luau_host_test_reference_tokens_seek(kMaximum - 3);
    const int32_t stale = require_create(first.state, 11);
    REQUIRE(stale == kMaximum - 3);
    REQUIRE(luau_host_reference_release(first.state, stale) == LUAU_HOST_STATUS_OK);
    REQUIRE(!read(first.state, stale, &ignored));
    REQUIRE(luau_host_reference_release(first.state, stale) == LUAU_HOST_STATUS_INVALID_ARGUMENT);

    // Through the wrap: the value is not issued again.
    std::vector<int32_t> issued;
    for (int index = 0; index < 8; ++index)
    {
        issued.push_back(require_create(second.state, index));
        REQUIRE(issued.back() != stale);
    }
    REQUIRE(issued.front() == kMaximum - 2);
    REQUIRE(issued.back() < issued.front());
    REQUIRE(!read(first.state, stale, &ignored));
    REQUIRE(!read(second.state, stale, &ignored));

    // Reissued to the second root, the value names the new reference there
    // and is still rejected on the first.
    luau_host_test_reference_tokens_seek(stale);
    const int32_t reissued = require_create(second.state, 22);
    REQUIRE(reissued == stale);
    REQUIRE(require_read(second.state, reissued) == 22);
    REQUIRE(!read(first.state, reissued, &ignored));
    REQUIRE(luau_host_reference_release(first.state, reissued) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(require_read(second.state, reissued) == 22);

    REQUIRE(luau_host_reference_release(second.state, reissued) == LUAU_HOST_STATUS_OK);
    for (const int32_t token : issued)
        REQUIRE(luau_host_reference_release(second.state, token) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_test_reference_tokens_live() == baseline);
}

// A token is valid only on the root that created it, whether it was issued
// before or after the wrap, and a child thread uses its root's tokens.
void test_root_checks_hold_across_the_wrap()
{
    const uint64_t baseline = luau_host_test_reference_tokens_live();
    Root first;
    Root second;
    int64_t ignored = 0;

    luau_host_test_reference_tokens_seek(kMaximum);
    const int32_t before = require_create(first.state, 1);
    const int32_t after = require_create(first.state, 2);
    REQUIRE(before == kMaximum);
    REQUIRE(after < before);

    luau_host_state* child = nullptr;
    REQUIRE(luau_host_thread_create(first.state, &child) == LUAU_HOST_STATUS_OK);
    REQUIRE(require_read(child, before) == 1);
    REQUIRE(require_read(child, after) == 2);
    const int32_t fromChild = require_create(child, 3);
    REQUIRE(require_read(first.state, fromChild) == 3);

    for (const int32_t token : {before, after, fromChild})
    {
        REQUIRE(!read(second.state, token, &ignored));
        REQUIRE(luau_host_reference_release(second.state, token) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    }

    REQUIRE(luau_host_reference_release(child, fromChild) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_reference_release(first.state, before) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_reference_release(first.state, after) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_set_top(first.state, 0) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_test_reference_tokens_live() == baseline);
}

// Closing a root with live references frees their values, and the cursor
// issues them to other roots when it reaches them; the closed root's tokens
// then name the new owners' references only.
void test_closing_a_root_frees_its_values()
{
    const uint64_t baseline = luau_host_test_reference_tokens_live();
    Root survivor;
    std::vector<int32_t> closedTokens;
    {
        Root closing;
        luau_host_test_reference_tokens_seek(kMaximum - 1);
        for (int64_t index = 0; index < 4; ++index)
            closedTokens.push_back(require_create(closing.state, index));
    }
    REQUIRE(closedTokens == std::vector<int32_t>({kMaximum - 1, kMaximum, 1, 2}));
    REQUIRE(luau_host_test_reference_tokens_live() == baseline);

    int64_t ignored = 0;
    for (const int32_t token : closedTokens)
        REQUIRE(!read(survivor.state, token, &ignored));

    luau_host_test_reference_tokens_seek(kMaximum - 1);
    for (size_t index = 0; index < closedTokens.size(); ++index)
    {
        const int32_t token = require_create(survivor.state, 100 + int64_t(index));
        REQUIRE(token == closedTokens[index]);
        REQUIRE(require_read(survivor.state, token) == 100 + int64_t(index));
    }
    for (const int32_t token : closedTokens)
        REQUIRE(luau_host_reference_release(survivor.state, token) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_test_reference_tokens_live() == baseline);
}

using TestFunction = void (*)();

int run_test(const char* name, TestFunction test)
{
    try
    {
        test();
        std::cout << "[PASS] " << name << '\n';
        return 0;
    }
    catch (const std::exception& exception)
    {
        std::cerr << "[FAIL] " << name << ": " << exception.what() << '\n';
        return 1;
    }
    catch (...)
    {
        std::cerr << "[FAIL] " << name << ": unknown exception\n";
        return 1;
    }
}
} // namespace

int main()
{
    int failures = 0;
    failures += run_test("creation continues after the last token", test_creation_continues_after_the_last_token);
    failures += run_test("live values are never issued twice", test_live_values_are_never_issued_twice);
    failures += run_test(
        "live values are never issued twice by concurrent roots",
        test_live_values_are_never_issued_twice_concurrently);
    failures += run_test("stale tokens stay invalid until reissued", test_stale_tokens_stay_invalid_until_reissued);
    failures += run_test("root checks hold across the wrap", test_root_checks_hold_across_the_wrap);
    failures += run_test("closing a root frees its values", test_closing_a_root_frees_its_values);

    if (failures != 0)
        std::cerr << failures << " reference-wrap test group(s) failed\n";

    return failures == 0 ? 0 : 1;
}
