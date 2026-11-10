#include "luau_host.h"

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>

namespace
{
thread_local int32_t callbackAction = 0;

int32_t LUAU_HOST_CALL fuzz_callback(luau_host_state* state)
{
    const uint64_t registrationId = luau_host_callback_registration_id(state);
    switch (callbackAction & 3)
    {
    case 0: return 0;
    case 1:
        return luau_host_push_integer(state, static_cast<int64_t>(registrationId)) == LUAU_HOST_STATUS_OK ? 1 : 0;
    case 2: return LUAU_HOST_CALLBACK_ERROR;
    default: return std::numeric_limits<int32_t>::max();
    }
}

int32_t LUAU_HOST_CALL fuzz_interrupt(luau_host_state*, luau_host_interrupt_kind)
{
    return callbackAction & 1;
}

int32_t LUAU_HOST_CALL fuzz_break(luau_host_state* state)
{
    return luau_host_suspend(state);
}

// Drives every operation that can run Luau code on a child suspended by a
// host break, with fuzz-chosen keys and library, then continues or resets it.
void fuzz_not_runnable_thread(luau_host_state* root, const uint8_t* payload, uint64_t payloadSize, uint8_t selector)
{
    luau_host_state* child = nullptr;
    if (luau_host_thread_create(root, &child) != LUAU_HOST_STATUS_OK || !child)
        return;

    luau_host_callback_table callbacks = {};
    callbacks.struct_size = sizeof(callbacks);
    callbacks.version = LUAU_HOST_CALLBACK_TABLE_VERSION;
    callbacks.registration_id = 1;
    callbacks.managed_function = fuzz_break;
    if (luau_host_push_callback(child, &callbacks, nullptr, 0, 0, nullptr, nullptr) != LUAU_HOST_STATUS_OK ||
        luau_host_resume(child, root, 0) != LUAU_HOST_STATUS_BREAK)
    {
        (void)luau_host_thread_reset(child);
        return;
    }

    std::array<char, 32> key = {};
    const size_t keySize = payloadSize < key.size() - 1 ? size_t(payloadSize) : key.size() - 1;
    if (keySize != 0)
        std::memcpy(key.data(), payload, keySize);
    const uint8_t* keyBytes = reinterpret_cast<const uint8_t*>(key.data());

    int32_t type = -1;
    int32_t count = -1;
    const uint8_t* text = nullptr;
    uint64_t length = 0;
    luau_host_status loadStatus = LUAU_HOST_STATUS_INVALID_ARGUMENT;
    (void)luau_host_global_get(child, keyBytes, &type);
    (void)luau_host_push_string(child, payload, payloadSize);
    (void)luau_host_global_set(child, keyBytes);
    (void)luau_host_table_create(child, 0, 0);
    (void)luau_host_push_string(child, payload, payloadSize);
    (void)luau_host_table_get(child, -2, &type);
    (void)luau_host_push_string(child, payload, payloadSize);
    (void)luau_host_table_set(child, -3);
    (void)luau_host_to_display_string(child, -1, &text, &length);
    (void)luau_host_load(child, keyBytes, payload, payloadSize, 0, &loadStatus);
    (void)luau_host_open_library(child, luau_host_library(selector % 12), &count);
    if (luau_host_stack_get_top(child) > 0)
        (void)luau_host_pcall(child, 0, 0, 0);

    // The callback's frame must still accept its results.
    (void)luau_host_stack_set_top(child, 0);
    if ((selector & 16) != 0)
        (void)luau_host_resume(child, root, 0);
    (void)luau_host_thread_reset(child);
}

uint64_t read_u64(const uint8_t* data, size_t size)
{
    uint64_t value = 0;
    const size_t copied = size < sizeof(value) ? size : sizeof(value);
    if (copied != 0)
        std::memcpy(&value, data, copied);
    return value;
}
} // namespace

extern "C" int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size)
{
    if (!data || size > 4096)
        return 0;

    const uint8_t selector = size == 0 ? 0 : data[0];
    callbackAction = selector;

    alignas(luau_host_abi_info) std::array<uint8_t, sizeof(luau_host_abi_info)> abiBytes = {};
    const uint32_t callerSize = size > 1
        ? uint32_t(read_u64(data + 1, size - 1))
        : uint32_t(selector);
    (void)luau_host_get_abi_info(callerSize, reinterpret_cast<luau_host_abi_info*>(abiBytes.data()));
    (void)luau_host_get_abi_info(callerSize, nullptr);

    luau_host_state_options options = {};
    options.struct_size = sizeof(options);
    options.version = LUAU_HOST_STATE_OPTIONS_VERSION;
    options.flags = LUAU_HOST_STATE_OPTION_TRACK_MEMORY;
    options.memory_limit_bytes = UINT64_C(16) * 1024 * 1024;

    luau_host_state* state = nullptr;
    if (luau_host_state_create(&options, &state, nullptr) != LUAU_HOST_STATUS_OK || !state)
        return 0;

    const size_t payloadOffset = size < 9 ? size : 9;
    const uint8_t* payload = data + payloadOffset;
    const uint64_t payloadSize = uint64_t(size - payloadOffset);
    (void)luau_host_push_string(state, payload, payloadSize);

    int32_t token = 0;
    if (luau_host_stack_get_top(state) > 0 &&
        luau_host_reference_create(state, -1, &token) == LUAU_HOST_STATUS_OK)
    {
        int32_t type = -1;
        (void)luau_host_reference_push(state, token, &type);
        (void)luau_host_stack_set_top(state, 1);
        (void)luau_host_reference_release(state, token);
        (void)luau_host_reference_push(state, token, &type);
        (void)luau_host_reference_release(state, token);
    }

    const int32_t arbitraryToken = static_cast<int32_t>(read_u64(data, size));
    int32_t ignoredType = -1;
    (void)luau_host_reference_push(state, arbitraryToken, &ignoredType);
    (void)luau_host_reference_release(state, arbitraryToken);

    (void)luau_host_stack_set_top(state, 0);
    luau_host_callback_table callbacks = {};
    callbacks.struct_size = sizeof(callbacks);
    callbacks.version = LUAU_HOST_CALLBACK_TABLE_VERSION;
    callbacks.registration_id = read_u64(data, size);
    callbacks.managed_function = fuzz_callback;
    if (luau_host_push_callback(state, &callbacks, payload, payloadSize, 0, nullptr, nullptr) == LUAU_HOST_STATUS_OK)
        (void)luau_host_pcall(state, 0, 1, 0);

    (void)luau_host_stack_set_top(state, 0);
    fuzz_not_runnable_thread(state, payload, payloadSize, selector);

    (void)luau_host_stack_set_top(state, 0);
    callbacks = {};
    callbacks.struct_size = sizeof(callbacks);
    callbacks.version = LUAU_HOST_CALLBACK_TABLE_VERSION;
    callbacks.interrupt_poll = fuzz_interrupt;
    if (luau_host_interrupt_install(state, &callbacks) == LUAU_HOST_STATUS_OK)
    {
        (void)luau_host_collect(state);

        // Throttled delivery with fuzz-chosen budgets: a small count limit, a
        // short or absent deadline, and an optional pending request, over a
        // loop that allocates so GC notifications interleave with safepoints.
        const uint64_t budget = read_u64(payload, size_t(payloadSize));
        const uint64_t countLimit = (selector & 2) != 0 ? budget % 4096 : 0;
        const uint64_t timeLimitNs = (selector & 4) != 0 ? (budget >> 12) % 2000000 : 0;
        (void)luau_host_interrupt_configure(state, countLimit, timeLimitNs);
        if ((selector & 8) != 0)
            luau_host_interrupt_request(state);

        static const char loopSource[] = "local t = {} for i = 1, 2048 do t[i] = {i} end return #t";
        luau_host_buffer bytecode = {};
        if (luau_host_compile(
                reinterpret_cast<const uint8_t*>(loopSource),
                sizeof(loopSource) - 1,
                nullptr,
                &bytecode) == LUAU_HOST_STATUS_OK)
        {
            luau_host_status loadStatus = LUAU_HOST_STATUS_INVALID_ARGUMENT;
            if (luau_host_load(state, reinterpret_cast<const uint8_t*>("=fuzz"), bytecode.data, bytecode.size, 0, &loadStatus) ==
                    LUAU_HOST_STATUS_OK &&
                loadStatus == LUAU_HOST_STATUS_OK)
                (void)luau_host_pcall(state, 0, 1, 0);
            luau_host_buffer_free(&bytecode);
        }
        (void)luau_host_interrupt_count(state);
        (void)luau_host_stack_set_top(state, 0);
        (void)luau_host_memory_reset_failure(state);
        luau_host_interrupt_uninstall(state);
        luau_host_interrupt_request(state);
    }

    luau_host_state_close(state);
    return 0;
}
