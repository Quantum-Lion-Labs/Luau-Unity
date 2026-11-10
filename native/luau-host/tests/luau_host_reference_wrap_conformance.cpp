// Reference-token tests at the end of the token range, through the exported
// functions of the reference-wrap fixture (tests/luau_host_reference_wrap_fixture.cpp).
// The fixture is the host compiled from its own source plus a seam that
// positions the process allocator, so these tests reach the last values of
// the range without creating two billion references first.

#include "luau_host.h"
#include "luau_host_reference_wrap_fixture.h"

#include <cstdint>
#include <exception>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <string>
#include <utility>

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

void test_exhaustion_is_permanent_for_every_root()
{
    const uint64_t baseline = luau_host_test_reference_tokens_live();
    Root first;
    Root second;

    const int32_t early = require_create(first.state, 7);

    luau_host_test_reference_tokens_seek(kMaximum - 1);
    const int32_t penultimate = require_create(first.state, 1);
    const int32_t last = require_create(second.state, 2);
    REQUIRE(penultimate == kMaximum - 1);
    REQUIRE(last == kMaximum);

    // Every root fails from here on, with the stack left as it was and the
    // output set to LUA_REFNIL, which is 0 in Luau.
    int32_t token = 0;
    REQUIRE(create(first.state, 3, &token) == LUAU_HOST_STATUS_RESOURCE_EXHAUSTED);
    REQUIRE(token == 0);
    REQUIRE(create(second.state, 3, &token) == LUAU_HOST_STATUS_RESOURCE_EXHAUSTED);
    {
        Root third;
        REQUIRE(create(third.state, 3, &token) == LUAU_HOST_STATUS_RESOURCE_EXHAUSTED);
    }

    // Live references keep working.
    REQUIRE(require_read(first.state, early) == 7);
    REQUIRE(require_read(first.state, penultimate) == 1);
    REQUIRE(require_read(second.state, last) == 2);

    // Releasing every reference, or closing a root, gives nothing back.
    REQUIRE(luau_host_reference_release(first.state, early) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_reference_release(first.state, penultimate) == LUAU_HOST_STATUS_OK);
    second.close();
    REQUIRE(luau_host_test_reference_tokens_live() == baseline);
    REQUIRE(create(first.state, 3, &token) == LUAU_HOST_STATUS_RESOURCE_EXHAUSTED);
    REQUIRE(token == 0);
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
    // Each process positions the allocator once; the test exhausts it.
    failures += run_test("exhaustion is permanent for every root", test_exhaustion_is_permanent_for_every_root);

    if (failures != 0)
        std::cerr << failures << " reference-wrap test group(s) failed\n";

    return failures == 0 ? 0 : 1;
}
