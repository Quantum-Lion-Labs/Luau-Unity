#include "luau_host.h"

#include "lua.h"
#include "lualib.h"
#include "luacode.h"

#include "lapi.h"
#include "ldebug.h"
#include "ldo.h"
#include "lgc.h"
#include "ltable.h"
#include "ludata.h"
#include "lvm.h"

#include "reference_tokens.h"
#include "tracked_allocation.h"

#include <atomic>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <condition_variable>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <mutex>
#include <new>
#include <string>
#include <unordered_map>
#include <utility>

#ifndef LUAU_HOST_UPSTREAM_REVISION
#define LUAU_HOST_UPSTREAM_REVISION "2a3d8937c19c8fd9f7b72b6ec9068c62760e6588"
#endif

#ifndef LUAU_HOST_BUILD_CONFIGURATION
#define LUAU_HOST_BUILD_CONFIGURATION "unknown"
#endif

#ifndef LUAU_HOST_BUILD_INPUT_FINGERPRINT
#define LUAU_HOST_BUILD_INPUT_FINGERPRINT "unverified-source-inputs"
#endif

static_assert(sizeof(uint8_t) == 1, "luau_host requires 8-bit bytes");
static_assert(sizeof(uint32_t) == 4, "luau_host requires 32-bit uint32_t");
static_assert(sizeof(uint64_t) == 8, "luau_host requires 64-bit uint64_t");
static_assert(LUAU_HOST_FRAME_SLOT_LIMIT == LUAI_MAXCSTACK, "The ABI frame slot limit must match the pinned Luau");
static_assert(sizeof(luau_host_compile_options) == 32, "Unexpected compile-options size");
static_assert(offsetof(luau_host_compile_options, struct_size) == 0, "Unexpected compile-options layout");
static_assert(offsetof(luau_host_compile_options, version) == 4, "Unexpected compile-options layout");
static_assert(offsetof(luau_host_compile_options, reserved0) == 6, "Unexpected compile-options layout");
static_assert(offsetof(luau_host_compile_options, optimization_level) == 8, "Unexpected compile-options layout");
static_assert(offsetof(luau_host_compile_options, debug_level) == 12, "Unexpected compile-options layout");
static_assert(offsetof(luau_host_compile_options, type_info_level) == 16, "Unexpected compile-options layout");
static_assert(offsetof(luau_host_compile_options, coverage_level) == 20, "Unexpected compile-options layout");
static_assert(offsetof(luau_host_compile_options, flags) == 24, "Unexpected compile-options layout");
static_assert(offsetof(luau_host_compile_options, reserved1) == 28, "Unexpected compile-options layout");
static_assert(sizeof(luau_host_state_options) == 16, "Unexpected state-options size");
static_assert(offsetof(luau_host_state_options, struct_size) == 0, "Unexpected state-options layout");
static_assert(offsetof(luau_host_state_options, version) == 4, "Unexpected state-options layout");
static_assert(offsetof(luau_host_state_options, flags) == 6, "Unexpected state-options layout");
static_assert(offsetof(luau_host_state_options, memory_limit_bytes) == 8, "Unexpected state-options layout");
static_assert(sizeof(luau_host_memory_info) == 48, "Unexpected memory-info size");
static_assert(offsetof(luau_host_memory_info, struct_size) == 0, "Unexpected memory-info layout");
static_assert(offsetof(luau_host_memory_info, failure) == 4, "Unexpected memory-info layout");
static_assert(offsetof(luau_host_memory_info, current_bytes) == 8, "Unexpected memory-info layout");
static_assert(offsetof(luau_host_memory_info, peak_bytes) == 16, "Unexpected memory-info layout");
static_assert(offsetof(luau_host_memory_info, limit_bytes) == 24, "Unexpected memory-info layout");
static_assert(offsetof(luau_host_memory_info, last_attempted_bytes) == 32, "Unexpected memory-info layout");
static_assert(offsetof(luau_host_memory_info, tracked) == 40, "Unexpected memory-info layout");
static_assert(offsetof(luau_host_memory_info, reserved) == 41, "Unexpected memory-info layout");
static_assert(sizeof(luau_host_buffer) == 16, "Unexpected host-buffer size");
static_assert(offsetof(luau_host_buffer, data) == 0, "Unexpected host-buffer layout");
static_assert(offsetof(luau_host_buffer, size) == 8, "Unexpected host-buffer layout");
static_assert(sizeof(luau_host_abi_info) == 112, "Unexpected ABI-information size");
static_assert(offsetof(luau_host_abi_info, struct_size) == 0, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, magic) == 4, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, abi_major) == 8, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, abi_minor) == 10, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, feature_flags) == 12, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, pointer_size) == 16, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, size_t_size) == 17, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, little_endian) == 18, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, reserved0) == 19, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, compile_options_size) == 20, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, callback_table_size) == 24, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, state_options_size) == 28, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, memory_info_size) == 32, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, buffer_size) == 36, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, type_nil) == 40, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, type_boolean) == 44, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, type_lightuserdata) == 48, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, type_number) == 52, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, type_integer) == 56, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, type_vector) == 60, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, type_string) == 64, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, type_table) == 68, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, type_function) == 72, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, type_userdata) == 76, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, type_thread) == 80, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, type_buffer) == 84, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, type_class) == 88, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, type_object) == 92, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, upstream_revision_hash) == 96, "Unexpected ABI-information layout");
static_assert(offsetof(luau_host_abi_info, host_build_fingerprint) == 104, "Unexpected ABI-information layout");

#if INTPTR_MAX == INT64_MAX
static_assert(sizeof(luau_host_callback_table) == 48, "Unexpected 64-bit callback-table size");
static_assert(offsetof(luau_host_callback_table, struct_size) == 0, "Unexpected callback-table layout");
static_assert(offsetof(luau_host_callback_table, version) == 4, "Unexpected callback-table layout");
static_assert(offsetof(luau_host_callback_table, reserved0) == 6, "Unexpected callback-table layout");
static_assert(offsetof(luau_host_callback_table, userdata) == 8, "Unexpected callback-table layout");
static_assert(offsetof(luau_host_callback_table, registration_id) == 16, "Unexpected callback-table layout");
static_assert(offsetof(luau_host_callback_table, managed_function) == 24, "Unexpected callback-table layout");
static_assert(offsetof(luau_host_callback_table, interrupt_poll) == 32, "Unexpected callback-table layout");
static_assert(offsetof(luau_host_callback_table, userdata_destructor) == 40, "Unexpected callback-table layout");
#endif

namespace
{
constexpr uint64_t kAllocatorMagic = UINT64_C(0x6c756175686f7374);
constexpr uint64_t kCallbackOwnerMagic = UINT64_C(0x6c75617563616c6c);
constexpr uint64_t kUserdataOwnerMagic = UINT64_C(0x6c75617575646174);
constexpr uint64_t kInterruptGateEnabled = UINT64_C(1) << 63;
constexpr uint64_t kInterruptGateCountMask = ~kInterruptGateEnabled;
using InterruptPoll = luau_host_interrupt_poll;
constexpr uint32_t kRequiredFeatures =
    LUAU_HOST_FEATURE_SELF_DESCRIPTION |
    LUAU_HOST_FEATURE_PROTECTED_OPERATIONS |
    LUAU_HOST_FEATURE_HOST_BUFFER |
    LUAU_HOST_FEATURE_TRACKED_ALLOCATOR |
    LUAU_HOST_FEATURE_MANAGED_CALLBACKS |
    LUAU_HOST_FEATURE_INTERRUPT |
    LUAU_HOST_FEATURE_TERMINAL_RESET |
    LUAU_HOST_FEATURE_INTEGER_VALUES |
    LUAU_HOST_FEATURE_SANDBOX |
    LUAU_HOST_FEATURE_OPAQUE_REFERENCE_TOKENS |
    LUAU_HOST_FEATURE_DIRECT_CALLBACK_IDENTITY |
    LUAU_HOST_FEATURE_OBSERVATION_ONLY_GC_INTERRUPT |
    LUAU_HOST_FEATURE_COROUTINE_SUSPENSION |
    LUAU_HOST_FEATURE_STACK_LIMIT |
    LUAU_HOST_FEATURE_INTERRUPT_THROTTLE |
    LUAU_HOST_FEATURE_RUNNABLE_THREAD_GUARD;

// Throttled interrupts read the monotonic clock every kClockStrideMin to
// kClockStrideMax counted safepoints. The stride adapts so that consecutive
// reads stay about kClockTargetSliceNs apart; luau_host.h documents the
// resulting wall-clock detection latency, so keep the two in step.
constexpr uint32_t kClockStrideMin = 1;
constexpr uint32_t kClockStrideInitial = 8;
constexpr uint32_t kClockStrideMax = 128;
constexpr int64_t kClockTargetSliceNs = 20000;

constexpr uint64_t fnv1a(const char* text, uint64_t value = UINT64_C(14695981039346656037))
{
    return *text == '\0'
        ? value
        : fnv1a(text + 1, (value ^ static_cast<uint8_t>(*text)) * UINT64_C(1099511628211));
}

constexpr uint64_t kUpstreamRevisionHash = fnv1a(LUAU_HOST_UPSTREAM_REVISION);
constexpr uint64_t kHostBuildFingerprint =
    fnv1a("luau-host-inputs;" LUAU_HOST_BUILD_INPUT_FINGERPRINT ";" LUAU_HOST_BUILD_CONFIGURATION);

#pragma pack(push, 1)
struct BinaryIdentityRecord
{
    char magic[16];
    uint32_t recordSize;
    uint32_t abiMagic;
    uint16_t abiMajor;
    uint16_t abiMinor;
    uint32_t featureFlags;
    uint8_t pointerSize;
    uint8_t sizeTSize;
    uint8_t littleEndian;
    uint8_t reserved;
    uint64_t upstreamRevisionHash;
    uint64_t hostBuildFingerprint;
    char buildInputSha256[65];
    char buildConfiguration[32];
};
#pragma pack(pop)

static_assert(sizeof(BinaryIdentityRecord) == 149, "Unexpected binary-identity record size");
static_assert(offsetof(BinaryIdentityRecord, recordSize) == 16, "Unexpected binary-identity layout");
static_assert(offsetof(BinaryIdentityRecord, upstreamRevisionHash) == 36, "Unexpected binary-identity layout");
static_assert(offsetof(BinaryIdentityRecord, hostBuildFingerprint) == 44, "Unexpected binary-identity layout");
static_assert(offsetof(BinaryIdentityRecord, buildInputSha256) == 52, "Unexpected binary-identity layout");
static_assert(sizeof(LUAU_HOST_BUILD_INPUT_FINGERPRINT) <= 65, "Build-input fingerprint is too long");
static_assert(sizeof(LUAU_HOST_BUILD_CONFIGURATION) <= 32, "Build configuration is too long");

// This hidden, referenced record lets release tooling verify the identity of a
// cross-compiled artifact without executing it. It deliberately adds no export.
const volatile BinaryIdentityRecord kBinaryIdentity = {
    "LUAUHABI-PROBE1",
    uint32_t(sizeof(BinaryIdentityRecord)),
    LUAU_HOST_ABI_MAGIC,
    LUAU_HOST_ABI_MAJOR,
    LUAU_HOST_ABI_MINOR,
    kRequiredFeatures,
    uint8_t(sizeof(void*)),
    uint8_t(sizeof(size_t)),
    1,
    0,
    kUpstreamRevisionHash,
    kHostBuildFingerprint,
    LUAU_HOST_BUILD_INPUT_FINGERPRINT,
    LUAU_HOST_BUILD_CONFIGURATION};

lua_State* native(luau_host_state* state)
{
    return reinterpret_cast<lua_State*>(state);
}

luau_host_state* host(lua_State* state)
{
    return reinterpret_cast<luau_host_state*>(state);
}

// Budget state for a configured (throttled) interrupt. Only the thread that
// executes or configures the root touches it, under the root-serialization
// contract, so plain fields suffice; cross-thread requests use the separate
// attention flag.
struct InterruptThrottle
{
    bool enabled = false;
    bool hasDeadline = false;
    uint32_t clockStride = kClockStrideInitial;
    // EXECUTION safepoints counted since the last configure.
    uint64_t count = 0;
    // The count at which the next count-limit or clock event is due.
    uint64_t nextEvent = UINT64_MAX;
    uint64_t countLimit = 0;
    uint64_t nextClockCheck = UINT64_MAX;
    int64_t deadline = 0;
    int64_t lastClock = 0;
};

struct AllocatorContext
{
    uint64_t magic = kAllocatorMagic;
    // The interrupt fast path reads these first; keep them together.
    InterruptThrottle throttle;
    std::atomic<uint32_t> interruptAttention = {0};
    size_t currentBytes = 0;
    size_t peakBytes = 0;
    size_t limitBytes = 0;
    size_t lastAttemptedBytes = 0;
    luau_host_allocator_failure failure = LUAU_HOST_ALLOCATOR_FAILURE_NONE;
    bool tracked = false;
    bool hasLimit = false;
    bool interrupted = false;
    std::atomic<InterruptPoll> interruptPoll = {nullptr};
    std::atomic<uint64_t> interruptGate = {0};
    std::mutex interruptLifecycleMutex;
    std::condition_variable interruptDrained;
};

size_t subtractsaturating(size_t value, size_t amount)
{
    return amount <= value ? value - amount : 0;
}

void setallocatorfailure(AllocatorContext* allocator, luau_host_allocator_failure failure, size_t attempted = 0)
{
    allocator->failure = failure;
    allocator->lastAttemptedBytes = attempted;
}

void* trackedallocator(void* userdata, void* block, size_t oldSize, size_t newSize)
{
    AllocatorContext* allocator = static_cast<AllocatorContext*>(userdata);
    if (!allocator || allocator->magic != kAllocatorMagic)
        return nullptr;

    (void)oldSize;
    const size_t previousRetainedSize = luau_host_internal::trackedallocationsize(block);

    if (newSize == 0)
    {
        luau_host_internal::freetrackedallocation(block);
        allocator->currentBytes = subtractsaturating(allocator->currentBytes, previousRetainedSize);
        return nullptr;
    }

    const size_t retainedBytes = subtractsaturating(allocator->currentBytes, previousRetainedSize);
    if (newSize > std::numeric_limits<size_t>::max() - retainedBytes)
    {
        setallocatorfailure(
            allocator,
            allocator->hasLimit ? LUAU_HOST_ALLOCATOR_FAILURE_QUOTA : LUAU_HOST_ALLOCATOR_FAILURE_SYSTEM,
            std::numeric_limits<size_t>::max());
        return nullptr;
    }

    const size_t requestedBytes = retainedBytes + newSize;
    const bool isGrowth = newSize > previousRetainedSize;

    if (isGrowth && allocator->hasLimit && requestedBytes > allocator->limitBytes)
    {
        setallocatorfailure(allocator, LUAU_HOST_ALLOCATOR_FAILURE_QUOTA, requestedBytes);
        return nullptr;
    }

    if (block && newSize == previousRetainedSize)
        return block;

    const luau_host_internal::TrackedAllocationResizeResult resized =
        luau_host_internal::resizetrackedallocation(block, newSize);
    if (resized.failed)
    {
        setallocatorfailure(allocator, LUAU_HOST_ALLOCATOR_FAILURE_SYSTEM, requestedBytes);
        return nullptr;
    }

    // A failed physical shrink returns the original allocation and retained
    // size. Keep charging that full size until a later realloc or free.
    allocator->currentBytes = retainedBytes + resized.retainedSize;
    if (allocator->currentBytes > allocator->peakBytes)
        allocator->peakBytes = allocator->currentBytes;

    return resized.block;
}

AllocatorContext* getallocator(lua_State* state)
{
    if (!state)
        return nullptr;

    void* userdata = nullptr;
    lua_Alloc allocator = lua_getallocf(state, &userdata);
    AllocatorContext* context = static_cast<AllocatorContext*>(userdata);
    return allocator == trackedallocator && context && context->magic == kAllocatorMagic ? context : nullptr;
}

constexpr uint32_t kMemoryInfoFixedPrefixSize = uint32_t(offsetof(luau_host_memory_info, current_bytes));

bool validmemoryinfooutput(const luau_host_memory_info* output)
{
    return output && output->struct_size >= kMemoryInfoFixedPrefixSize;
}

void fillmemoryinfo(const AllocatorContext* allocator, luau_host_memory_info* output)
{
    if (!output)
        return;

    const uint32_t callerSize = output->struct_size;
    luau_host_memory_info value = {};
    value.struct_size = callerSize;
    if (allocator)
    {
        value.failure = allocator->failure;
        value.current_bytes = uint64_t(allocator->currentBytes);
        value.peak_bytes = uint64_t(allocator->peakBytes);
        value.limit_bytes = allocator->hasLimit ? uint64_t(allocator->limitBytes) : 0;
        value.last_attempted_bytes = uint64_t(allocator->lastAttemptedBytes);
        value.tracked = allocator->tracked ? 1 : 0;
    }

    const uint32_t bytesToWrite = callerSize < sizeof(value) ? callerSize : uint32_t(sizeof(value));
    std::memcpy(output, &value, bytesToWrite);
}

luau_host_status allocatorstatus(lua_State* state)
{
    AllocatorContext* allocator = getallocator(state);
    if (allocator && allocator->interrupted)
        return LUAU_HOST_STATUS_CANCELED;
    if (allocator && allocator->failure == LUAU_HOST_ALLOCATOR_FAILURE_QUOTA)
        return LUAU_HOST_STATUS_MEMORY_QUOTA;
    return LUAU_HOST_STATUS_SYSTEM_OUT_OF_MEMORY;
}

luau_host_status mapstatus(lua_State* state, int status)
{
    switch (status)
    {
    case LUA_OK: return LUAU_HOST_STATUS_OK;
    case LUA_YIELD: return LUAU_HOST_STATUS_YIELDED;
    case LUA_BREAK: return LUAU_HOST_STATUS_BREAK;
    case LUA_ERRMEM: return allocatorstatus(state);
    default: return LUAU_HOST_STATUS_LUA_ERROR;
    }
}

bool validcallbacks(const luau_host_callback_table* callbacks)
{
    return callbacks &&
        callbacks->struct_size >= sizeof(luau_host_callback_table) &&
        callbacks->version == LUAU_HOST_CALLBACK_TABLE_VERSION &&
        callbacks->reserved0 == 0;
}

bool validordinaryindex(lua_State* state, int index)
{
    if (!state || index == 0 || index <= LUA_REGISTRYINDEX)
        return false;

    const int top = lua_gettop(state);
    return index > 0 ? index <= top : -int64_t(index) <= top;
}

bool validtableindex(lua_State* state, int index)
{
    return validordinaryindex(state, index) && lua_type(state, index) == LUA_TTABLE;
}

// Luau can run code only on a thread whose status is OK. The interpreter
// checks the thread status at each interrupt safepoint and after each call,
// and on a thread that yielded, is suspended by a break, or ended with an
// error it leaves a function started there as if that function had just
// suspended. luaD_call then returns with the function's call frame and stack
// still in place, and the next resume continues from that corrupted state.
// luaD_pcall does not make this a valid place to run code; it only contains
// errors. Every operation that can reach Luau code, through a call, a
// metamethod, or an import resolved while loading, checks this first.
bool runnable(lua_State* state)
{
    return lua_status(state) == LUA_OK;
}

struct ReferenceRecord
{
    lua_State* root;
    int registryReference;
};

luau_host_internal::MonotonicReferenceTokenAllocator referenceTokenAllocator;
std::mutex referenceMutex;
std::unordered_map<int32_t, ReferenceRecord> liveReferences;

bool lookupreference(lua_State* state, int32_t token, int* registryReference)
{
    if (!state || token <= 0 || !registryReference)
        return false;

    lua_State* root = lua_mainthread(state);
    std::lock_guard<std::mutex> lock(referenceMutex);
    const auto found = liveReferences.find(token);
    if (found == liveReferences.end() || found->second.root != root)
        return false;

    *registryReference = found->second.registryReference;
    return true;
}

luau_host_status registerreference(lua_State* state, int registryReference, int32_t* token)
{
    const int32_t allocated = referenceTokenAllocator.allocate();
    if (allocated == 0)
        return LUAU_HOST_STATUS_RESOURCE_EXHAUSTED;

    try
    {
        std::lock_guard<std::mutex> lock(referenceMutex);
        const bool inserted = liveReferences.emplace(
            allocated,
            ReferenceRecord{lua_mainthread(state), registryReference}).second;
        if (!inserted)
            return LUAU_HOST_STATUS_RESOURCE_EXHAUSTED;
    }
    catch (const std::bad_alloc&)
    {
        return LUAU_HOST_STATUS_SYSTEM_OUT_OF_MEMORY;
    }
    catch (...)
    {
        return LUAU_HOST_STATUS_SYSTEM_OUT_OF_MEMORY;
    }

    *token = allocated;
    return LUAU_HOST_STATUS_OK;
}

void erasereference(lua_State* state, int32_t token, int registryReference)
{
    std::lock_guard<std::mutex> lock(referenceMutex);
    const auto found = liveReferences.find(token);
    if (found != liveReferences.end() &&
        found->second.root == lua_mainthread(state) &&
        found->second.registryReference == registryReference)
        liveReferences.erase(found);
}

void erasereferencesforroot(lua_State* root)
{
    std::lock_guard<std::mutex> lock(referenceMutex);
    for (auto current = liveReferences.begin(); current != liveReferences.end();)
    {
        if (current->second.root == root)
            current = liveReferences.erase(current);
        else
            ++current;
    }
}

bool validcallbackframe(lua_State* state)
{
    // Direct protected calls into a C closure do not set isactive, while
    // coroutine execution does. A live C frame with an OK status identifies
    // both supported entry paths and excludes suspended/yielded callbacks.
    return state && lua_status(state) == LUA_OK && state->ci != state->base_ci && curr_func(state)->isC;
}

struct ProtectedCallContext
{
    Pfunc operation;
    void* operationContext;
    int slots;
};

void runprotectedoperation(lua_State* state, void* userdata)
{
    ProtectedCallContext* context = static_cast<ProtectedCallContext*>(userdata);
    // protect() already reserved these slots unless growing the stack failed.
    // Retrying turns that allocation failure into this operation's error.
    if (context->slots != 0 && !lua_checkstack(state, context->slots))
        luaD_throw(state, LUA_ERRMEM);
    context->operation(state, context->operationContext);
}

// Reserves slots above the current top before a protected operation runs.
// luaD_pcall writes a failed operation's error object without a bounds check,
// so the slot it lands in must exist before the operation starts.
luau_host_status reservestack(lua_State* state, int slots, int consumed)
{
    if (slots == 0)
        return LUAU_HOST_STATUS_OK;

    // Check the frame limit first: lua_checkstack reports it and a failed
    // allocation alike. Luau itself may leave a frame above the limit, for
    // example after returning thousands of values.
    const int frame = int(state->top - state->base);
    if (frame > LUAI_MAXCSTACK || slots > LUAI_MAXCSTACK - frame)
        return LUAU_HOST_STATUS_STACK_LIMIT;

    if (lua_checkstack(state, slots))
        return LUAU_HOST_STATUS_OK;

    // Growth failed and the allocator recorded why. While a slot is left for
    // the error object, let the operation report that failure normally. Once
    // none is, refuse rather than write past the stack.
    return consumed > 0 || state->top < state->stack_last
        ? LUAU_HOST_STATUS_OK
        : LUAU_HOST_STATUS_STACK_LIMIT;
}

// slots is the most values the operation holds above its entry top. A failed
// operation's error object replaces its first consumed input, or needs one
// more slot when it consumes nothing.
luau_host_status protect(lua_State* state, Pfunc operation, void* context, int consumed, int slots)
{
    if (!state || !operation || consumed < 0 || slots < 0 || lua_gettop(state) < consumed)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    const int reserved = consumed == 0 && slots == 0 ? 1 : slots;
    const luau_host_status reservation = reservestack(state, reserved, consumed);
    if (reservation != LUAU_HOST_STATUS_OK)
        return reservation;

    ProtectedCallContext call = {operation, context, slots};
    int status = luaD_pcall(state, runprotectedoperation, &call, savestack(state, state->top - consumed), 0);
    return mapstatus(state, status);
}

// Every status that leaves one error object on the stack.
bool appendserror(luau_host_status status)
{
    return status == LUAU_HOST_STATUS_LUA_ERROR ||
        status == LUAU_HOST_STATUS_MEMORY_QUOTA ||
        status == LUAU_HOST_STATUS_SYSTEM_OUT_OF_MEMORY ||
        status == LUAU_HOST_STATUS_CANCELED;
}

struct IndexContext
{
    int index;
    int result;
};

struct IntContext
{
    int value;
    int result;
};

struct Int64Context
{
    int64_t value;
};

struct NumberContext
{
    double value;
};

struct VectorContext
{
    float x;
    float y;
    float z;
};

struct StringContext
{
    const char* value;
    size_t length;
    int index;
    int result;
    const char* pointerResult;
};

using NativeUserdataDestructor = void (LUAU_HOST_CALL*)(void*);

struct AllocationContext
{
    size_t size;
    int tag;
    NativeUserdataDestructor destructor;
    void* result;
};

struct UserdataOwnerPayload
{
    uint64_t magic;
    luau_host_userdata_destructor destructor;
    size_t size;
#if INTPTR_MAX == INT64_MAX
    uint8_t alignmentPadding[8];
#endif
    uint8_t data[1];
};

static_assert(offsetof(UserdataOwnerPayload, data) % 16 == 0, "Userdata payload must retain 16-byte alignment");
static_assert(
    offsetof(UserdataOwnerPayload, data) % alignof(std::max_align_t) == 0,
    "Userdata payload must retain max_align_t alignment");

UserdataOwnerPayload* wrappeduserdata(Udata* userdata)
{
    constexpr size_t prefix = offsetof(UserdataOwnerPayload, data);
    if (!userdata || userdata->tag != UTAG_IDTOR || userdata->len < int(prefix + sizeof(NativeUserdataDestructor)))
        return nullptr;

    UserdataOwnerPayload* owner = reinterpret_cast<UserdataOwnerPayload*>(userdata->data);
    const size_t expectedSize = size_t(userdata->len) - prefix - sizeof(NativeUserdataDestructor);
    return owner->magic == kUserdataOwnerMagic && owner->size == expectedSize ? owner : nullptr;
}

// Upstream passes the collecting state, which is unsafe to use during GC
// traversal; the host destructors only touch their own payload.
void userdatadestructortrampoline(lua_State*, void* userdata)
{
    UserdataOwnerPayload* owner = static_cast<UserdataOwnerPayload*>(userdata);
    if (!owner || owner->magic != kUserdataOwnerMagic)
        return;

    luau_host_userdata_destructor destructor = owner->destructor;
    owner->magic = 0;
    owner->destructor = nullptr;
    if (!destructor)
        return;

    try
    {
        destructor(owner->data);
    }
    catch (...)
    {
        // The host ABI is a no-unwind boundary.
    }
}

void* exposeduserdata(const TValue* value)
{
    if (!value)
        return nullptr;
    if (ttislightuserdata(value))
        return pvalue(value);
    if (!ttisuserdata(value))
        return nullptr;

    Udata* userdata = uvalue(value);
    if (UserdataOwnerPayload* owner = wrappeduserdata(userdata))
        return owner->data;
    return userdata->data;
}

struct TableContext
{
    int arraySize;
    int recordSize;
};

struct ClosureContext
{
    luau_host_managed_function function;
    uint64_t registrationId;
    const char* debugName;
    size_t debugNameSize;
    int upvalues;
    void* ownerUserdata;
    luau_host_userdata_destructor ownerDestructor;
    bool ownerTransferred;
};

struct CallbackOwnerPayload
{
    uint64_t magic;
    uint64_t registrationId;
    void* userdata;
    luau_host_userdata_destructor destructor;
    luau_host_managed_function function;
    int callerUpvalues;
    char debugName[1];
};

int managedcallbacktrampoline(lua_State* state);

CallbackOwnerPayload* callbackpayload(const Closure* closure)
{
    if (!closure || !closure->isC || closure->c.f != managedcallbacktrampoline || closure->nupvalues == 0)
        return nullptr;

    const TValue* value = &closure->c.upvals[closure->nupvalues - 1];
    if (!ttisuserdata(value))
        return nullptr;

    CallbackOwnerPayload* owner = reinterpret_cast<CallbackOwnerPayload*>(uvalue(value)->data);
    return owner && owner->magic == kCallbackOwnerMagic ? owner : nullptr;
}

CallbackOwnerPayload* currentcallbackpayload(lua_State* state)
{
    return state && state->ci != state->base_ci ? callbackpayload(curr_func(state)) : nullptr;
}

int invalidcallbackreturn(lua_State* state, const char* message)
{
    (void)message;
    luaD_throw(state, LUA_ERRERR);
}

int validatecallbackreturn(lua_State* state, int result)
{
    const int status = lua_status(state);
    if (result >= 0 && status == LUA_OK && result <= lua_gettop(state))
        return result;
    if (result == LUAU_HOST_CALLBACK_YIELD && (status == LUA_YIELD || status == LUA_BREAK))
        return result;
    // A failed callback's arguments are dead, so it may drop them to make room
    // for its failure value. Only an empty frame has nothing to raise.
    if (result == LUAU_HOST_CALLBACK_ERROR && status == LUA_OK && lua_gettop(state) > 0)
        lua_error(state);
    return invalidcallbackreturn(state, "managed callback returned an invalid result count");
}

int managedcallbacktrampoline(lua_State* state)
{
    CallbackOwnerPayload* owner = currentcallbackpayload(state);
    if (!owner || !owner->function)
        return invalidcallbackreturn(state, "managed callback metadata is invalid");

    int result = 0;
    try
    {
        result = owner->function(host(state));
    }
    catch (...)
    {
        return invalidcallbackreturn(state, "managed callback crossed the no-unwind boundary");
    }
    return validatecallbackreturn(state, result);
}

// Upstream passes the collecting state, which is unsafe to use during GC
// traversal; the host destructors only touch their own payload.
void callbackownerdestructor(lua_State*, void* userdata)
{
    CallbackOwnerPayload* owner = static_cast<CallbackOwnerPayload*>(userdata);
    if (!owner || owner->magic != kCallbackOwnerMagic)
        return;

    luau_host_userdata_destructor destructor = owner->destructor;
    void* ownerUserdata = owner->userdata;
    owner->magic = 0;
    owner->registrationId = 0;
    owner->destructor = nullptr;
    owner->userdata = nullptr;
    owner->function = nullptr;
    if (!destructor)
        return;
    try
    {
        destructor(ownerUserdata);
    }
    catch (...)
    {
        // Host destructor callbacks are a no-unwind boundary. Managed reverse
        // callbacks are also required to contain their own exceptions.
    }
}

struct LightUserdataContext
{
    void* pointer;
    int tag;
};

struct LoadContext
{
    const char* chunkName;
    const char* bytecode;
    size_t size;
    int environment;
    int result;
};

struct LibraryContext
{
    int library;
    int result;
};

struct RawIndexContext
{
    int index;
    int item;
    int result;
};

struct MoveContext
{
    lua_State* destination;
    int count;
};

struct PCallContext
{
    int arguments;
    int results;
    int errorFunction;
    int status;
};

struct ResumeContext
{
    lua_State* from;
    int arguments;
    int status;
    bool withError;
};

void opnewthread(lua_State* state, void* userdata) { *static_cast<lua_State**>(userdata) = lua_newthread(state); }
// The host owns thread userdata. While coroutine.resume/wrap propagates a
// break, its Lua frame retains the child; this link needs no allocation or GC
// root of its own. Clear it before resetting or completing the continuation.
void recordcoroutinebreak(lua_State* parent, lua_Debug* debug)
{
    // interruptThread calls this hook before lua_break checks the parent.
    // A rejected break unwinds the frame that retains the child, so it must
    // never leave a continuation link behind.
    if (lua_isyieldable(parent))
        parent->userdata = debug->userdata;
}

lua_State* resumetarget(lua_State* state)
{
    while (state && state->userdata)
        state = static_cast<lua_State*>(state->userdata);
    return state;
}

void opresetthread(lua_State* state, void*)
{
    if (lua_State* child = static_cast<lua_State*>(state->userdata))
    {
        opresetthread(child, nullptr);
        state->userdata = nullptr;
    }
    lua_resetthread(state);
}
void oppushvalue(lua_State* state, void* userdata) { lua_pushvalue(state, static_cast<IndexContext*>(userdata)->index); }
void oppushnil(lua_State* state, void*) { lua_pushnil(state); }
void oppushboolean(lua_State* state, void* userdata) { lua_pushboolean(state, static_cast<IntContext*>(userdata)->value); }
void oppushinteger(lua_State* state, void* userdata) { lua_pushinteger64(state, static_cast<Int64Context*>(userdata)->value); }
void oppushnumber(lua_State* state, void* userdata) { lua_pushnumber(state, static_cast<NumberContext*>(userdata)->value); }
void oppushvector(lua_State* state, void* userdata) { VectorContext* c = static_cast<VectorContext*>(userdata); lua_pushvector(state, c->x, c->y, c->z); }
void oppushstring(lua_State* state, void* userdata) { StringContext* c = static_cast<StringContext*>(userdata); lua_pushlstring(state, c->value, c->length); }
void oppushlightuserdata(lua_State* state, void* userdata) { LightUserdataContext* c = static_cast<LightUserdataContext*>(userdata); lua_pushlightuserdatatagged(state, c->pointer, c->tag); }
void oppushthread(lua_State* state, void* userdata) { static_cast<IntContext*>(userdata)->result = lua_pushthread(state); }
void oppushcallback(lua_State* state, void* userdata)
{
    ClosureContext* c = static_cast<ClosureContext*>(userdata);
    const bool needsOwner = c->ownerUserdata && c->ownerDestructor;
    const bool needsDebugName = c->debugName && c->debugNameSize != 0;

    const size_t payloadSize = offsetof(CallbackOwnerPayload, debugName) + c->debugNameSize + 1;
    CallbackOwnerPayload* owner = static_cast<CallbackOwnerPayload*>(
        lua_newuserdatadtor(state, payloadSize, callbackownerdestructor));
    owner->magic = kCallbackOwnerMagic;
    owner->registrationId = c->registrationId;
    owner->userdata = c->ownerUserdata;
    owner->destructor = c->ownerDestructor;
    owner->function = c->function;
    owner->callerUpvalues = c->upvalues;
    c->ownerTransferred = needsOwner;
    if (needsDebugName)
        std::memcpy(owner->debugName, c->debugName, c->debugNameSize);
    owner->debugName[c->debugNameSize] = '\0';

    lua_pushcclosure(
        state,
        managedcallbacktrampoline,
        needsDebugName ? owner->debugName : nullptr,
        c->upvalues + 1);
}
void opnewuserdata(lua_State* state, void* userdata) { AllocationContext* c = static_cast<AllocationContext*>(userdata); c->result = lua_newuserdatatagged(state, c->size, c->tag); }
void opnewuserdatadtor(lua_State* state, void* userdata)
{
    AllocationContext* c = static_cast<AllocationContext*>(userdata);
    const size_t allocationSize = offsetof(UserdataOwnerPayload, data) + c->size;
    UserdataOwnerPayload* owner = static_cast<UserdataOwnerPayload*>(
        lua_newuserdatadtor(state, allocationSize, userdatadestructortrampoline));
    owner->magic = kUserdataOwnerMagic;
    owner->destructor = c->destructor;
    owner->size = c->size;
    c->result = owner->data;
}
void opnewbuffer(lua_State* state, void* userdata) { AllocationContext* c = static_cast<AllocationContext*>(userdata); c->result = lua_newbuffer(state, c->size); }
void opgettable(lua_State* state, void* userdata) { IndexContext* c = static_cast<IndexContext*>(userdata); c->result = lua_gettable(state, c->index); }
void opsettable(lua_State* state, void* userdata) { lua_settable(state, static_cast<IndexContext*>(userdata)->index); }
void oprawget(lua_State* state, void* userdata) { IndexContext* c = static_cast<IndexContext*>(userdata); c->result = lua_rawget(state, c->index); }
void oprawset(lua_State* state, void* userdata) { lua_rawset(state, static_cast<IndexContext*>(userdata)->index); }
void opnext(lua_State* state, void* userdata) { IndexContext* c = static_cast<IndexContext*>(userdata); c->result = lua_next(state, c->index); }
void opcreatetable(lua_State* state, void* userdata) { TableContext* c = static_cast<TableContext*>(userdata); lua_createtable(state, c->arraySize, c->recordSize); }
void opcleartable(lua_State* state, void* userdata) { lua_cleartable(state, static_cast<IndexContext*>(userdata)->index); }
void opclonetable(lua_State* state, void* userdata) { lua_clonetable(state, static_cast<IndexContext*>(userdata)->index); }
void opgetmetatable(lua_State* state, void* userdata) { IndexContext* c = static_cast<IndexContext*>(userdata); c->result = lua_getmetatable(state, c->index); }
void opsetmetatable(lua_State* state, void* userdata) { IndexContext* c = static_cast<IndexContext*>(userdata); c->result = lua_setmetatable(state, c->index); }
void opsetreadonly(lua_State* state, void* userdata) { IntContext* c = static_cast<IntContext*>(userdata); lua_setreadonly(state, c->result, c->value); }
void opgetfield(lua_State* state, void* userdata) { StringContext* c = static_cast<StringContext*>(userdata); c->result = lua_getfield(state, c->index, c->value); }
void opsetfield(lua_State* state, void* userdata) { StringContext* c = static_cast<StringContext*>(userdata); lua_setfield(state, c->index, c->value); }
void opglobalpush(lua_State* state, void*) { lua_pushvalue(state, LUA_GLOBALSINDEX); }
void oprefcreate(lua_State* state, void* userdata) { IndexContext* c = static_cast<IndexContext*>(userdata); c->result = lua_ref(state, c->index); }
void oprefpush(lua_State* state, void* userdata) { RawIndexContext* c = static_cast<RawIndexContext*>(userdata); c->result = lua_rawgeti(state, LUA_REGISTRYINDEX, c->item); }
void optostring(lua_State* state, void* userdata) { StringContext* c = static_cast<StringContext*>(userdata); c->pointerResult = lua_tolstring(state, c->index, &c->length); }
void opdisplaystring(lua_State* state, void* userdata)
{
    StringContext* c = static_cast<StringContext*>(userdata);
    c->pointerResult = luaL_tolstring(state, c->index, &c->length);
}
void opload(lua_State* state, void* userdata) { LoadContext* c = static_cast<LoadContext*>(userdata); c->result = luau_load(state, c->chunkName, c->bytecode, c->size, c->environment); }
void opcollect(lua_State* state, void*) { (void)lua_gc(state, LUA_GCCOLLECT, 0); }
void opsettop(lua_State* state, void* userdata)
{
    const int index = static_cast<IndexContext*>(userdata)->index;
    const int currentTop = lua_gettop(state);

    // protect() reserved the delta. Keep the check so lua_settop can never
    // push its own unchecked "stack overflow" message.
    if (index > currentTop && !lua_checkstack(state, index - currentTop))
        luaD_throw(state, LUA_ERRMEM);

    lua_settop(state, index);
}
void opmove(lua_State* state, void* userdata)
{
    MoveContext* c = static_cast<MoveContext*>(userdata);

    // Reserve destination capacity first, then translate a failed reservation
    // into the source state's protected error boundary. Both threads share the
    // same root allocator, so allocator diagnostics remain authoritative.
    if (c->destination != state && !lua_checkstack(c->destination, c->count))
        luaD_throw(state, LUA_ERRMEM);

    lua_xmove(state, c->destination, c->count);
}
void oppcall(lua_State* state, void* userdata)
{
    PCallContext* c = static_cast<PCallContext*>(userdata);
    const int required = c->results > c->arguments + 1
        ? c->results - (c->arguments + 1)
        : 0;

    // Fixed-result calls can need more slots than the function and arguments
    // currently occupy. protect() reserved them; keep the check explicit.
    if (required != 0 && !lua_checkstack(state, required))
        luaD_throw(state, LUA_ERRMEM);

    c->status = lua_pcall(state, c->arguments, c->results, c->errorFunction);
}
void opresume(lua_State* state, void* userdata)
{
    ResumeContext* c = static_cast<ResumeContext*>(userdata);
    // Resume the actual suspended callback first, then let each native
    // coroutine.resume/wrap continuation consume its child's results/error.
    // A new break leaves the chain intact for the next host dispatch.
    for (;;)
    {
        lua_State* target = state;
        lua_State* parent = c->from;
        while (target->userdata)
        {
            parent = target;
            target = static_cast<lua_State*>(target->userdata);
        }
        c->status = c->withError ? lua_resumeerror(target, parent) : lua_resume(target, parent, c->arguments);
        if (target == state || c->status == LUA_BREAK)
            return;

        parent->userdata = nullptr;
        c->arguments = 0;
        c->withError = false;
    }
}

// ---------------------------------------------------------------------------
// Long library calls
//
// Luau code reaches an interrupt safepoint at each call, return, and loop
// iteration, and upstream's pattern matcher reaches one at each matching step
// (lstrlib.cpp, match()). A few other library functions do work that grows
// with their inputs inside one C call and never reach one, so a wall-clock or
// interrupt-count limit, cancellation, or disposal could not stop them until
// they returned. The plain-text search behind string.find and string.split
// compares the needle at every position, so its time grows with the product
// of the two lengths: strings that fit the default 64 MB memory limit can keep
// one call running for hours. table.sort, table.concat, and utf8.len are
// linear or n log n, but one call can still run for hundreds of milliseconds.
//
// When it opens a library, the host replaces those entries of the library
// table with the versions below, before a sandbox can freeze the table, and
// before any script code can run (see "Opening a library" below). The string
// metatable's __index is the same table, so method calls, saved references,
// and calls through the table all reach them. Each version does exactly what
// upstream (as built here, with every fast flag at its default) does for every
// input, including argument coercion, errors, and the order of comparisons and
// table writes; luau_host_library_conformance compares them with upstream over
// a generated corpus. Where only one path of a function can run long, the host
// version handles that path and calls upstream's function for everything else,
// in the same frame, so argument errors still name the function. The version
// is a C closure in place of upstream's; upstream's function pointer, the same
// for every state, is kept when the library opens.
//
// Work between safepoints. Each version counts its work in units, and spend()
// reaches the interrupt callback, as a non-yieldable EXECUTION safepoint like
// a pattern-matching step, each time the units since the last one reach
// kLibraryPollUnits: it counts toward the interrupt-count limit, and a stop
// raises the sticky CANCELED hard stop. A unit is
//
// - one byte that a loop of the host's own compares or examines: a needle
//   byte in matchforward or matchbackward, a step of the critical
//   factorization, a pattern byte classified, or a UTF-8 position decoded;
// - sixteen bytes that memchr scans or memcmp compares, that a string buffer
//   copies, or that a new string hashes and copies;
// - a sixteenth of kLibraryStepUnits, which is charged for one candidate of
//   the short-needle search, one sort comparison, one concatenated field or
//   separator, one split piece, and one table slot that an insertion's rehash
//   moves.
//
// Every step of the work is charged when it ends. A step is at most
// kLibraryPollUnits + kLibraryStepUnits units of the host's own work, or is
// one VM operation that the host cannot divide: one comparison of two
// strings, the creation of one string, one table insertion with the rehash it
// causes, one append to a string buffer with the buffer's growth, or one sort
// comparison that calls a C comparison function, or a __lt metamethod that
// reaches no safepoint itself. Such an operation is charged for all of its
// size (the last two for a full kLibraryPollUnits), so when it is large the
// safepoint follows it at once.
// Between two safepoints, then, a version does fewer than
// 2 * kLibraryPollUnits + kLibraryStepUnits units of work, and at most one
// step of it is such an operation, of whatever size. The work before a
// version's first safepoint (argument checks, and a table length) is constant
// or logarithmic, and so is what follows its last one, apart from one final
// operation: the result string of table.concat. A comparison that calls a
// Luau function, as the comparison function or as __lt, is charged at most a
// step: that function reaches a safepoint of its own when it returns.
//
// Nothing here may own a resource with a destructor: the VM raises errors and
// hard stops with longjmp.
// ---------------------------------------------------------------------------

constexpr size_t kLibraryPollUnits = 16384;
// Memory scans and copies run about sixteen times as fast as byte loops.
constexpr size_t kLibraryBytesPerUnit = 16;
constexpr size_t kLibraryScanChunk = kLibraryPollUnits * kLibraryBytesPerUnit;
// One step: a short-needle candidate, a sort comparison, a concatenated field
// or separator, a split piece, or a table slot a rehash moves, beyond the
// bytes it touches.
constexpr size_t kLibraryStepUnits = 16;
// Calls with a subject and a needle (or pattern) of at most this many bytes
// each go to upstream's string.find, and calls with a subject of at most this
// many bytes to upstream's string.split, which keeps the common short call as
// fast as it was. Upstream's work is then bounded by the subject: it compares
// at most about four million bytes (a needle of half the subject that matches
// up to its last byte at every position), well under a millisecond, and
// examines at most this many pattern bytes. string.split compares nothing
// when the needle is longer than the subject.
constexpr size_t kPlainSearchDirectBytes = 4096;
// Needles up to this many bytes are searched with upstream's loop only. Longer
// needles are searched the same way while the bytes compared stay within one
// per position plus kNaiveSearchSlack, and with the two-way algorithm after.
constexpr size_t kShortNeedleBytes = 32;
constexpr size_t kNaiveSearchSlack = 4096;
// How many scans in a row may advance fewer than four positions before the
// two-way search stops scanning for the needle's first byte.
constexpr int kShortScanLimit = 64;

std::atomic<lua_CFunction> upstreamStringFind = {nullptr};
std::atomic<lua_CFunction> upstreamStringSplit = {nullptr};

#if defined(LUAU_HOST_LIBRARY_COVERAGE)
// Counts of the rarely taken paths below. Only luau_host_library_coverage, a
// build of the library conformance test that compiles this file into itself,
// defines LUAU_HOST_LIBRARY_COVERAGE: it reads them to show that its corpus
// reaches every one. No shipped build has them.
struct LibraryCoverage
{
    std::atomic<uint64_t> twoWayPeriodic = {0};
    std::atomic<uint64_t> twoWayNonPeriodic = {0};
    std::atomic<uint64_t> twoWayCriticalSkip = {0};
    std::atomic<uint64_t> twoWayResumed = {0};
    std::atomic<uint64_t> heapSort = {0};
    std::atomic<uint64_t> findHandedToUpstream = {0};
    std::atomic<uint64_t> concatBufferGrowth = {0};
    std::atomic<uint64_t> splitRehash = {0};
    std::atomic<uint64_t> openRefused = {0};
    std::atomic<uint64_t> openRepairedAfterError = {0};
};

LibraryCoverage libraryCoverage;

#define LIBRARY_COVERAGE(point) (void(libraryCoverage.point.fetch_add(1, std::memory_order_relaxed)))
#else
#define LIBRARY_COVERAGE(point) (void(0))
#endif

// The remaining work before the next safepoint.
struct LibraryWork
{
    explicit LibraryWork(lua_State* state)
        : L(state)
    {
    }

    lua_State* L;
    size_t remaining = kLibraryPollUnits;
};

// Reaches the interrupt callback the way upstream's pattern matcher does. The
// interrupt cannot yield here: raising nCcalls makes the thread non-yieldable,
// so a stop unwinds as a CANCELED error, which luaD_pcall contains and which
// restores nCcalls.
LUAU_NOINLINE void librarysafepoint(lua_State* L)
{
    void (*interrupt)(lua_State*, int) = L->global->cb.interrupt;
    if (interrupt)
    {
        L->nCcalls++;
        interrupt(L, -1);
        L->nCcalls--;
    }
}

// Records units of work, and reaches a safepoint when they add up. Returns
// whether it did.
inline bool spend(LibraryWork& work, size_t units)
{
    if (LUAU_LIKELY(units < work.remaining))
    {
        work.remaining -= units;
        return false;
    }
    work.remaining = kLibraryPollUnits;
    librarysafepoint(work.L);
    return true;
}

inline size_t smaller(size_t a, size_t b)
{
    return a < b ? a : b;
}

// The first index i in [from, end) with x[i] != y[i], or end.
size_t matchforward(LibraryWork& work, const uint8_t* x, const uint8_t* y, size_t from, size_t end)
{
    size_t i = from;
    for (;;)
    {
        const size_t stop = i + smaller(end - i, work.remaining);
        const size_t start = i;
        while (i < stop && x[i] == y[i])
            ++i;
        spend(work, i - start + 1);
        if (i != stop || stop == end)
            return i;
    }
}

// Compares x[k - 1] with y[k - 1] downward while k > lowest, and returns the k
// at which it stopped: lowest when every byte matched.
size_t matchbackward(LibraryWork& work, const uint8_t* x, const uint8_t* y, size_t from, size_t lowest)
{
    size_t k = from;
    for (;;)
    {
        const size_t stop = k - smaller(k - lowest, work.remaining);
        const size_t start = k;
        while (k > stop && x[k - 1] == y[k - 1])
            --k;
        spend(work, start - k + 1);
        if (k != stop || stop == lowest)
            return k;
    }
}

// The first position p in [from, to) with bytes[p] == value, or to.
size_t scanbyte(LibraryWork& work, const uint8_t* bytes, size_t from, size_t to, uint8_t value)
{
    while (from < to)
    {
        const size_t chunk = smaller(to - from, kLibraryScanChunk);
        if (const void* hit = std::memchr(bytes + from, value, chunk))
        {
            const size_t at = size_t(static_cast<const uint8_t*>(hit) - bytes);
            spend(work, (at - from) / kLibraryBytesPerUnit + 1);
            return at;
        }
        from += chunk;
        spend(work, chunk / kLibraryBytesPerUnit + 1);
    }
    return to;
}

bool equalbytes(LibraryWork& work, const uint8_t* a, const uint8_t* b, size_t size)
{
    while (size != 0)
    {
        const size_t chunk = smaller(size, kLibraryScanChunk);
        const bool equal = std::memcmp(a, b, chunk) == 0;
        spend(work, chunk / kLibraryBytesPerUnit + 1);
        if (!equal)
            return false;
        a += chunk;
        b += chunk;
        size -= chunk;
    }
    return true;
}

// A needle of at least two bytes, prepared for the two-way string-matching
// algorithm (Crochemore and Perrin, 1991), which compares at most about twice
// the subject length in bytes, whatever the inputs, and uses constant extra
// memory. This follows glibc's two_way_short_needle.
struct PlainNeedle
{
    // The critical position: the needle is split into x[0, critical) and
    // x[critical, size). Always less than size.
    size_t critical = 0;
    // The period of the needle when periodic; otherwise the shift after a
    // mismatch in the left half.
    size_t period = 0;
    bool periodic = false;
};

// glibc's critical_factorization: the later of the two maximal suffixes, for
// the byte order and its reverse, starts the right half.
size_t criticalfactorization(LibraryWork& work, const uint8_t* x, size_t size, size_t* period)
{
    // Index of the last byte of the left half; SIZE_MAX stands for -1.
    size_t suffix = SIZE_MAX;
    size_t j = 0;
    size_t k = 1;
    size_t p = 1;
    while (j + k < size)
    {
        const uint8_t a = x[j + k];
        const uint8_t b = x[suffix + k];
        if (a < b)
        {
            j += k;
            k = 1;
            p = j - suffix;
        }
        else if (a == b)
        {
            if (k != p)
                ++k;
            else
            {
                j += p;
                k = 1;
            }
        }
        else
        {
            suffix = j++;
            k = p = 1;
        }
        spend(work, 1);
    }
    *period = p;

    size_t reversed = SIZE_MAX;
    j = 0;
    k = p = 1;
    while (j + k < size)
    {
        const uint8_t a = x[j + k];
        const uint8_t b = x[reversed + k];
        if (b < a)
        {
            j += k;
            k = 1;
            p = j - reversed;
        }
        else if (a == b)
        {
            if (k != p)
                ++k;
            else
            {
                j += p;
                k = 1;
            }
        }
        else
        {
            reversed = j++;
            k = p = 1;
        }
        spend(work, 1);
    }

    if (reversed + 1 < suffix + 1)
        return suffix + 1;
    *period = p;
    return reversed + 1;
}

void prepareneedle(LibraryWork& work, PlainNeedle& needle, const uint8_t* x, size_t size)
{
    needle.critical = criticalfactorization(work, x, size, &needle.period);
    // The period of the right half never exceeds its length, so this stays
    // inside the needle.
    needle.periodic = equalbytes(work, x, x + needle.period, needle.critical);
    if (!needle.periodic)
    {
        const size_t right = size - needle.critical;
        needle.period = (needle.critical > right ? needle.critical : right) + 1;
        LIBRARY_COVERAGE(twoWayNonPeriodic);
    }
    else
        LIBRARY_COVERAGE(twoWayPeriodic);
}

// The plain search behind string.find and string.split. It searches as
// upstream's lmemfind does: memchr for the needle's first byte, then compare
// the rest. That is fast for ordinary text, but on a subject that matches most
// of a long needle at many positions it compares the needle at every one of
// them. For a needle of at most kShortNeedleBytes that is still linear. For a
// longer one the searcher counts the bytes it compares and, once they exceed
// one per position by kNaiveSearchSlack, finishes with the two-way algorithm.
// The counts carry over from one search to the next, which keeps string.split
// linear over all its pieces.
struct PlainSearcher
{
    PlainSearcher(const char* bytes, size_t size)
        : x(reinterpret_cast<const uint8_t*>(bytes))
        , m(size)
    {
    }

    const uint8_t* x;
    size_t m; // at least 2
    bool twoway = false;
    size_t positions = 0;
    size_t compared = 0;
    PlainNeedle needle;
};

const uint8_t* findtwoway(LibraryWork& work, const PlainSearcher& searcher, const uint8_t* subject, size_t size)
{
    const uint8_t* x = searcher.x;
    const size_t m = searcher.m;
    const PlainNeedle& needle = searcher.needle;
    const size_t critical = needle.critical;
    if (size < m)
        return nullptr;

    // Alignments j run from 0 to last. Alignments at which a chosen byte of
    // the needle does not match cannot match, and are skipped with memchr:
    // the first byte while that skips well, as upstream's search does, and
    // otherwise the byte at the critical position, where the comparison
    // starts. A skip only moves forward, so the scans stay linear.
    const size_t last = size - m;
    size_t skip = 0;
    int shortScans = 0;
    size_t j = 0;
    // In a periodic needle, the length of the prefix known to match at j.
    size_t memory = 0;
    while (j <= last)
    {
        if (memory <= skip)
        {
            const size_t at = scanbyte(work, subject + skip, j, last + 1, x[skip]);
            if (at > last)
                return nullptr;
            if (at != j)
                memory = 0;
            if (skip == 0)
            {
                shortScans = at - j < 4 ? shortScans + 1 : 0;
                if (shortScans == kShortScanLimit)
                {
                    skip = critical;
                    LIBRARY_COVERAGE(twoWayCriticalSkip);
                }
            }
            j = at;
        }

        const uint8_t* y = subject + j;
        const size_t i = matchforward(work, x, y, memory > critical ? memory : critical, m);
        if (i < m)
        {
            j += i - critical + 1;
            memory = 0;
            continue;
        }

        const size_t known = needle.periodic ? memory : 0;
        const size_t k = critical > known ? matchbackward(work, x, y, critical, known) : known;
        if (k <= known)
            return y;

        j += needle.period;
        memory = needle.periodic ? m - needle.period : 0;
    }
    return nullptr;
}

// The first occurrence of the needle in subject[0, size), or nullptr.
const uint8_t* findplain(LibraryWork& work, PlainSearcher& searcher, const uint8_t* subject, size_t size)
{
    const uint8_t* x = searcher.x;
    const size_t m = searcher.m;
    if (size < m)
        return nullptr;

    const size_t last = size - m;
    size_t j = 0;
    if (m <= kShortNeedleBytes)
    {
        // lmemfind's loop. It compares at most kShortNeedleBytes bytes at each
        // candidate, so it stays linear. Each candidate is charged as a step,
        // with the bytes memchr scanned to reach it; a scan that finds none
        // is charged for the bytes it scanned.
        const uint8_t first = x[0];
        const uint8_t* candidate = subject;
        const uint8_t* const end = subject + last + 1;
        for (;;)
        {
            const size_t span = smaller(size_t(end - candidate), kLibraryScanChunk);
            const uint8_t* hit = static_cast<const uint8_t*>(std::memchr(candidate, first, span));
            if (!hit)
            {
                candidate += span;
                spend(work, span / kLibraryBytesPerUnit + 1);
                if (candidate == end)
                    return nullptr;
                continue;
            }
            if (std::memcmp(hit + 1, x + 1, m - 1) == 0)
                return hit;
            spend(work, size_t(hit - candidate) / kLibraryBytesPerUnit + kLibraryStepUnits);
            candidate = hit + 1;
        }
    }

    if (!searcher.twoway)
    {
        const uint8_t first = x[0];
        // The verification compares at most this many bytes inline; a longer
        // match continues in matchforward, which charges its own bytes.
        const size_t inlineEnd = m < kLibraryPollUnits ? m : kLibraryPollUnits;
        for (;;)
        {
            const size_t span = smaller(last + 1 - j, kLibraryScanChunk);
            const void* hit = std::memchr(subject + j, first, span);
            if (!hit)
            {
                j += span;
                spend(work, span / kLibraryBytesPerUnit + 1);
                if (j > last)
                    return nullptr;
                continue;
            }

            const size_t at = size_t(static_cast<const uint8_t*>(hit) - subject);
            spend(work, (at - j) / kLibraryBytesPerUnit + 1);
            const uint8_t* y = subject + at;
            size_t i = 1;
            while (i < inlineEnd && x[i] == y[i])
                ++i;
            spend(work, i);
            if (i == inlineEnd && i < m)
                i = matchforward(work, x, y, i, m);
            if (i == m)
                return y;

            searcher.positions += at - j + 1;
            searcher.compared += i;
            j = at + 1;
            if (j > last)
                return nullptr;
            if (searcher.compared > searcher.positions + kNaiveSearchSlack)
                break;
        }
        searcher.twoway = true;
        prepareneedle(work, searcher.needle, x, m);
    }
    else
        LIBRARY_COVERAGE(twoWayResumed);
    return findtwoway(work, searcher, subject + j, size - j);
}

// lmemfind's contract: the first occurrence of s2 in s1, s1 when s2 is empty.
const char* plainsearch(LibraryWork& work, const char* s1, size_t l1, const char* s2, size_t l2)
{
    if (l2 == 0)
        return s1;
    if (l2 > l1)
        return nullptr;

    const uint8_t* subject = reinterpret_cast<const uint8_t*>(s1);
    if (l2 == 1)
    {
        const size_t at = scanbyte(work, subject, 0, l1, uint8_t(s2[0]));
        return at < l1 ? s1 + at : nullptr;
    }

    PlainSearcher searcher(s2, l2);
    const uint8_t* found = findplain(work, searcher, subject, l1);
    return found ? reinterpret_cast<const char*>(found) : nullptr;
}

// From lstrlib.cpp.
int stringposrelat(int pos, size_t len)
{
    if (pos < 0)
        pos += int(len) + 1;
    return pos >= 0 ? pos : 0;
}

// The bytes lstrlib.cpp's nospecials looks for: "^$*+?.([%-".
struct PatternSpecials
{
    uint8_t special[256] = {};

    constexpr PatternSpecials()
    {
        for (const char* c = "^$*+?.([%-"; *c; ++c)
            special[uint8_t(*c)] = 1;
    }
};

constexpr PatternSpecials kPatternSpecials;

// Whether a pattern has none of the special characters, as lstrlib.cpp's
// nospecials decides it: upstream looks for them with strpbrk in each
// zero-terminated segment, which examines every byte up to the first special
// one. This examines blocks of up to kLibraryPollUnits bytes, without a branch
// per byte, and is charged a unit per byte.
bool patternnospecials(LibraryWork& work, const char* p, size_t l)
{
    const uint8_t* bytes = reinterpret_cast<const uint8_t*>(p);
    for (size_t from = 0; from < l;)
    {
        const size_t block = smaller(l - from, kLibraryPollUnits);
        uint8_t found = 0;
        for (size_t i = from; i < from + block; ++i)
            found |= kPatternSpecials.special[bytes[i]];
        spend(work, block);
        if (found)
            return false;
        from += block;
    }
    return true;
}

// Whether argument n (1-based) is a string of at most `limit` bytes. Reads the
// argument without converting it.
inline bool shortstringargument(lua_State* L, int n, size_t limit)
{
    const TValue* argument = L->base + (n - 1);
    return argument < L->top && ttisstring(argument) && tsvalue(argument)->len <= limit;
}

// string.find. The host searches plain text itself (a true fourth argument,
// or a pattern without special characters), and hands a pattern search to
// upstream's function, whose pattern matcher reaches a safepoint at each step.
// It follows upstream's str_find_aux: the arguments and init first, then the
// classification of the pattern, once, with safepoints. Upstream's function
// then classifies the pattern again itself, examining it up to its first
// special character, in one scan without a safepoint.
LUAU_NOINLINE int hoststringfindlong(lua_State* L)
{
    size_t ls = 0;
    size_t lp = 0;
    const char* s = luaL_checklstring(L, 1, &ls);
    const char* p = luaL_checklstring(L, 2, &lp);
    int init = stringposrelat(luaL_optinteger(L, 3, 1), ls);
    if (init < 1)
        init = 1;
    else if (init > int(ls) + 1)
    {
        lua_pushnil(L);
        return 1;
    }

    LibraryWork work(L);
    if (!(lua_toboolean(L, 4) || patternnospecials(work, p, lp)))
    {
        // The checks above converted a number argument to a string, as
        // upstream's do, so upstream sees the same arguments again.
        LIBRARY_COVERAGE(findHandedToUpstream);
        return upstreamStringFind.load(std::memory_order_relaxed)(L);
    }

    const char* found = plainsearch(work, s + init - 1, ls - size_t(init) + 1, p, lp);
    if (found)
    {
        lua_pushinteger(L, int(found - s + 1));
        lua_pushinteger(L, int(found - s + ptrdiff_t(lp)));
        return 2;
    }
    lua_pushnil(L);
    return 1;
}

// Calls whose subject and pattern are both short go to upstream directly. The
// check reads the arguments without converting them, so upstream sees the call
// unchanged. The search is its own function, so that these calls do not pay
// for its frame.
int hoststringfind(lua_State* L)
{
    if (shortstringargument(L, 1, kPlainSearchDirectBytes) && shortstringargument(L, 2, kPlainSearchDirectBytes))
        return upstreamStringFind.load(std::memory_order_relaxed)(L);
    return hoststringfindlong(L);
}

// Stores piece number `index` in t, the table at the top, as upstream's
// str_split does, with lua_settable, and charges the string's creation and,
// when the insertion rehashed the table, the slots it moved. A string of a
// scan chunk or more is a step of its own, so that one step never holds both
// a large string and a large rehash.
inline void splitpiece(lua_State* L, LibraryWork& work, LuaTable* t, int index, const char* piece, size_t length)
{
    lua_pushinteger(L, index);
    lua_pushlstring(L, piece, length);
    size_t units = kLibraryStepUnits + length / kLibraryBytesPerUnit;
    if (LUAU_UNLIKELY(length >= kLibraryScanChunk))
    {
        spend(work, units);
        units = kLibraryStepUnits;
    }

    const int sizearray = t->sizearray;
    const LuaNode* node = t->node;
    lua_settable(L, -3);
    if (LUAU_UNLIKELY(t->sizearray != sizearray || t->node != node))
    {
        LIBRARY_COVERAGE(splitRehash);
        units += kLibraryStepUnits * (size_t(t->sizearray) + size_t(sizenode(t)));
    }
    spend(work, units);
}

// string.split, as upstream's str_split does it with LuauOptimizeStringSplit
// off: the same pieces, inserted with the same table operations, so the table
// it returns has the same layout. Only the search differs. Subjects of
// kPlainSearchDirectBytes or fewer go to upstream directly: upstream compares
// nothing when the needle is longer than the subject, so a long needle does
// not make that call long.
LUAU_NOINLINE int hoststringsplitlong(lua_State* L)
{
    size_t haystackLen = 0;
    const char* haystack = luaL_checklstring(L, 1, &haystackLen);
    size_t needleLen = 0;
    const char* needle = luaL_optlstring(L, 2, ",", &needleLen);

    const char* end = haystack + haystackLen;
    const char* spanStart = haystack;
    int numMatches = 0;
    LibraryWork work(L);

    lua_createtable(L, 0, 0);
    LuaTable* pieces = hvalue(L->top - 1);

    if (needleLen == 0)
    {
        // Every position after the first ends a piece of one byte, and no
        // piece follows the last.
        for (const char* iter = haystack + 1; iter <= end; iter++)
        {
            splitpiece(L, work, pieces, ++numMatches, spanStart, size_t(iter - spanStart));
            spanStart = iter;
        }
        return 1;
    }

    if (needleLen <= haystackLen)
    {
        PlainSearcher searcher(needle, needleLen);
        const char* iter = haystack;
        for (;;)
        {
            const uint8_t* subject = reinterpret_cast<const uint8_t*>(iter);
            const size_t remaining = size_t(end - iter);
            const char* found = nullptr;
            if (needleLen == 1)
            {
                const size_t at = scanbyte(work, subject, 0, remaining, uint8_t(needle[0]));
                found = at < remaining ? iter + at : nullptr;
            }
            else if (const uint8_t* at = findplain(work, searcher, subject, remaining))
                found = reinterpret_cast<const char*>(at);
            if (!found)
                break;

            splitpiece(L, work, pieces, ++numMatches, spanStart, size_t(found - spanStart));
            spanStart = found + needleLen;
            iter = spanStart;
        }
    }

    splitpiece(L, work, pieces, ++numMatches, spanStart, size_t(end - spanStart));
    return 1;
}

int hoststringsplit(lua_State* L)
{
    if (shortstringargument(L, 1, kPlainSearchDirectBytes))
        return upstreamStringSplit.load(std::memory_order_relaxed)(L);
    return hoststringsplitlong(L);
}

// table.concat, as upstream's tconcat and addfield do it. Each append is
// charged for the bytes it copies: the bytes added and, when the buffer grew,
// the bytes it held, which the growth copied. Concatenating numbers converts
// each one to an interned string, which makes a long table take a fifth of a
// second.
inline size_t bufferlength(const luaL_Strbuf* b)
{
    return size_t(b->p - (b->storage ? b->storage->data : b->buffer));
}

// Charges an append after which the buffer ends at `end` again, or, when it
// grew, also the copy of the `held` bytes it held before.
inline void spendappend(LibraryWork& work, const luaL_Strbuf* b, const char* end, size_t held, size_t added)
{
    if (LUAU_UNLIKELY(b->end != end))
    {
        LIBRARY_COVERAGE(concatBufferGrowth);
        added += held;
    }
    spend(work, kLibraryStepUnits + added / kLibraryBytesPerUnit);
}

// luaL_addlstring, with the copy into room the buffer already has inline.
inline void concatappend(LibraryWork& work, luaL_Strbuf* b, const char* s, size_t len)
{
    if (LUAU_LIKELY(size_t(b->end - b->p) >= len))
    {
        std::memcpy(b->p, s, len);
        b->p += len;
        spend(work, kLibraryStepUnits + len / kLibraryBytesPerUnit);
        return;
    }
    const char* end = b->end;
    const size_t held = bufferlength(b);
    luaL_addlstring(b, s, len);
    spendappend(work, b, end, held, len);
}

// Upstream computes i - 1 in int, which overflows for the most negative int;
// unsigned arithmetic gives the same index where that wraps, without the
// undefined behavior.
void tableconcatfield(lua_State* L, LibraryWork& work, luaL_Strbuf* b, int i, LuaTable* t)
{
    const unsigned index = unsigned(i) - 1u;
    if (t && index < unsigned(t->sizearray) && ttisstring(&t->array[index]))
    {
        TString* ts = tsvalue(&t->array[index]);
        concatappend(work, b, getstr(ts), ts->len);
        return;
    }

    const int tt = lua_rawgeti(L, 1, i);
    if (tt != LUA_TSTRING && tt != LUA_TNUMBER)
        luaL_errorL(L, "invalid value (%s) at index %d in table for 'concat'", luaL_typename(L, -1), i);
    const char* end = b->end;
    const size_t held = bufferlength(b);
    luaL_addvalue(b);
    spendappend(work, b, end, held, bufferlength(b) - held);
}

int hosttableconcat(lua_State* L)
{
    size_t lsep = 0;
    const char* sep = luaL_optlstring(L, 2, "", &lsep);
    luaL_checktype(L, 1, LUA_TTABLE);
    int i = luaL_optinteger(L, 3, 1);
    const int last = luaL_opt(L, luaL_checkinteger, 4, lua_objlen(L, 1));

    LuaTable* t = hvalue(L->base);
    LibraryWork work(L);

    luaL_Strbuf b;
    luaL_buffinit(L, &b);
    for (; i < last; i++)
    {
        tableconcatfield(L, work, &b, i, t);
        if (lsep != 0)
            concatappend(work, &b, sep, lsep);
    }
    if (i == last)
        tableconcatfield(L, work, &b, i, t);
    luaL_pushresult(&b);
    return 1;
}

// table.sort, as upstream's tsort does it: the same introsort, so that every
// comparison happens in the same order and an inconsistent comparison
// function produces the same permutation or error. The default comparison of
// two numbers or two strings, and a call of a C comparison function or of a
// __lt metamethod that is not a Luau function, reach no safepoint, so the sort
// charges them; a Luau comparison function reaches one when it returns, and
// the sort charges nothing for it.
enum class SortComparison
{
    Default,
    CFunction,
    LuauFunction,
};

int sortfunction(lua_State* L, const TValue* l, const TValue* r)
{
    LUAU_ASSERT(L->top == L->base + 2); // table, function

    setobj2s(L, L->top, &L->base[1]);
    setobj2s(L, L->top + 1, l);
    setobj2s(L, L->top + 2, r);
    L->top += 3; // safe because of LUA_MINSTACK guarantee
    luaD_call(L, L->top - 3, 1);
    L->top -= 1; // maintain stack depth

    return !l_isfalse(L->top);
}

inline void sortswap(lua_State* L, LuaTable* t, int i, int j)
{
    TValue* arr = t->array;
    LUAU_ASSERT(unsigned(i) < unsigned(t->sizearray) && unsigned(j) < unsigned(t->sizearray));

    // no barrier required because both elements are in the array before and after the swap
    TValue temp;
    setobj2s(L, &temp, &arr[i]);
    setobj2t(L, &arr[i], &arr[j]);
    setobj2t(L, &arr[j], &temp);
}

// The package's interrupt poll runs no Luau code, but the table is read
// again after a safepoint, and checked like after a comparison.
inline void sortaftersafepoint(lua_State* L, LuaTable* t, int n)
{
    if (t->sizearray != n)
        luaL_errorL(L, "table modified during sorting");
}

// The safepoints the interrupt throttle has counted: it counts each one while
// it is enabled, and the count moves at nothing else.
inline uint64_t safepointcount(lua_State* L)
{
    return static_cast<const AllocatorContext*>(L->global->ud)->throttle.count;
}

// The default comparison when it is not two numbers or two strings, or when a
// safepoint is due: luaV_lessthan. Values of any other type compare through a
// __lt metamethod. One that reached a safepoint itself (a Luau function, which
// reaches one when it returns) is charged a step; any other, such as the C
// function of the integer type, is charged a full kLibraryPollUnits, so that a
// safepoint follows it. Without an enabled throttle, the count never moves,
// and every such comparison is followed by a safepoint.
LUAU_NOINLINE int sortlessslow(lua_State* L, LuaTable* t, LibraryWork& work, int i, int j)
{
    const int n = t->sizearray;
    LUAU_ASSERT(unsigned(i) < unsigned(n) && unsigned(j) < unsigned(n));

    // A comparison of two strings costs up to their common length.
    const TValue* l = &t->array[i];
    const TValue* r = &t->array[j];
    size_t units = kLibraryStepUnits;
    const bool metamethod = !(ttisnumber(l) && ttisnumber(r)) && !(ttisstring(l) && ttisstring(r));
    if (ttisstring(l) && ttisstring(r))
        units += smaller(tsvalue(l)->len, tsvalue(r)->len) / kLibraryBytesPerUnit;
    const uint64_t counted = metamethod ? safepointcount(L) : 0;
    const int res = luaV_lessthan(L, l, r);

    // predicate call may resize the table, which is invalid
    if (t->sizearray != n)
        luaL_errorL(L, "table modified during sorting");

    if (metamethod && safepointcount(L) == counted)
        units = kLibraryPollUnits;
    if (spend(work, units))
        sortaftersafepoint(L, t, n);
    return res;
}

template<SortComparison Comparison>
LUAU_FORCEINLINE int sortless(lua_State* L, LuaTable* t, LibraryWork& work, int i, int j)
{
    if constexpr (Comparison == SortComparison::Default)
    {
        // luaV_lessthan's cases for two numbers and for two strings, inline:
        // they run no code, so the table cannot change. A comparison of two
        // strings costs up to their common length.
        LUAU_ASSERT(unsigned(i) < unsigned(t->sizearray) && unsigned(j) < unsigned(t->sizearray));
        const TValue* l = &t->array[i];
        const TValue* r = &t->array[j];
        if (LUAU_LIKELY(ttisnumber(l) && ttisnumber(r) && kLibraryStepUnits < work.remaining))
        {
            work.remaining -= kLibraryStepUnits;
            return nvalue(l) < nvalue(r);
        }
        if (ttisstring(l) && ttisstring(r))
        {
            const size_t units = kLibraryStepUnits + smaller(tsvalue(l)->len, tsvalue(r)->len) / kLibraryBytesPerUnit;
            if (LUAU_LIKELY(units < work.remaining))
            {
                work.remaining -= units;
                return luaV_strcmp(tsvalue(l), tsvalue(r)) < 0;
            }
        }
        return sortlessslow(L, t, work, i, j);
    }
    else
    {
        // As upstream's sort_less with sort_func.
        const int n = t->sizearray;
        LUAU_ASSERT(unsigned(i) < unsigned(n) && unsigned(j) < unsigned(n));
        const int res = sortfunction(L, &t->array[i], &t->array[j]);
        if (t->sizearray != n)
            luaL_errorL(L, "table modified during sorting");
        if constexpr (Comparison == SortComparison::CFunction)
        {
            // The C function's work is unknown, so a safepoint follows each
            // call.
            spend(work, kLibraryPollUnits);
            sortaftersafepoint(L, t, n);
        }
        return res;
    }
}

template<SortComparison Comparison>
void sortsiftheap(lua_State* L, LuaTable* t, LibraryWork& work, int l, int u, int root)
{
    LUAU_ASSERT(l <= u);
    const int count = u - l + 1;

    // process all elements with two children
    while (root * 2 + 2 < count)
    {
        const int left = root * 2 + 1;
        const int right = root * 2 + 2;
        int next = root;
        next = sortless<Comparison>(L, t, work, l + next, l + left) ? left : next;
        next = sortless<Comparison>(L, t, work, l + next, l + right) ? right : next;

        if (next == root)
            break;

        sortswap(L, t, l + root, l + next);
        root = next;
    }

    // process last element if it has just one child
    const int lastleft = root * 2 + 1;
    if (lastleft == count - 1 && sortless<Comparison>(L, t, work, l + root, l + lastleft))
        sortswap(L, t, l + root, l + lastleft);
}

template<SortComparison Comparison>
void sortheap(lua_State* L, LuaTable* t, LibraryWork& work, int l, int u)
{
    LUAU_ASSERT(l <= u);
    const int count = u - l + 1;
    LIBRARY_COVERAGE(heapSort);

    for (int i = count / 2 - 1; i >= 0; --i)
        sortsiftheap<Comparison>(L, t, work, l, u, i);

    for (int i = count - 1; i > 0; --i)
    {
        sortswap(L, t, l, l + i);
        sortsiftheap<Comparison>(L, t, work, l, l + i - 1, 0);
    }
}

template<SortComparison Comparison>
void sortrec(lua_State* L, LuaTable* t, LibraryWork& work, int l, int u, int limit)
{
    // sort range [l..u] (inclusive, 0-based)
    while (l < u)
    {
        // if the limit has been reached, quick sort is going over the permitted nlogn complexity, so we fall back to heap sort
        if (limit == 0)
            return sortheap<Comparison>(L, t, work, l, u);

        // sort elements a[l], a[(l+u)/2] and a[u]
        // note: this simultaneously acts as a small sort and a median selector
        if (sortless<Comparison>(L, t, work, u, l)) // a[u] < a[l]?
            sortswap(L, t, u, l);                   // swap a[l] - a[u]
        if (u - l == 1)
            break;                                          // only 2 elements
        const int m = l + ((u - l) >> 1);                   // midpoint
        if (sortless<Comparison>(L, t, work, m, l))         // a[m]<a[l]?
            sortswap(L, t, m, l);
        else if (sortless<Comparison>(L, t, work, u, m)) // a[u]<a[m]?
            sortswap(L, t, m, u);
        if (u - l == 2)
            break; // only 3 elements

        // here l, m, u are ordered; m will become the new pivot
        const int p = u - 1;
        sortswap(L, t, m, u - 1); // pivot is now (and always) at u-1

        // a[l] <= P == a[u-1] <= a[u], only need to sort from l+1 to u-2
        int i = l;
        int j = u - 1;
        for (;;)
        { // invariant: a[l..i] <= P <= a[j..u]
            // repeat ++i until a[i] >= P
            while (sortless<Comparison>(L, t, work, ++i, p))
            {
                if (i >= u)
                    luaL_errorL(L, "invalid order function for sorting");
            }
            // repeat --j until a[j] <= P
            while (sortless<Comparison>(L, t, work, p, --j))
            {
                if (j <= l)
                    luaL_errorL(L, "invalid order function for sorting");
            }
            if (j < i)
                break;
            sortswap(L, t, i, j);
        }

        // swap pivot a[p] with a[i], which is the new midpoint
        sortswap(L, t, p, i);

        // adjust limit to allow 1.5 log2N recursive steps
        limit = (limit >> 1) + (limit >> 2);

        // a[l..i-1] <= a[i] == P <= a[i+1..u]
        // sort smaller half recursively; the larger half is sorted in the next loop iteration
        if (i - l < u - i)
        {
            sortrec<Comparison>(L, t, work, l, i - 1, limit);
            l = i + 1;
        }
        else
        {
            sortrec<Comparison>(L, t, work, i + 1, u, limit);
            u = i - 1;
        }
    }
}

int hosttablesort(lua_State* L)
{
    luaL_checktype(L, 1, LUA_TTABLE);
    LuaTable* t = hvalue(L->base);
    const int n = luaH_getn(t);
    if (t->readonly)
        luaG_readonlyerror(L);

    SortComparison comparison = SortComparison::Default;
    if (!lua_isnoneornil(L, 2)) // is there a 2nd argument?
    {
        luaL_checktype(L, 2, LUA_TFUNCTION);
        comparison = SortComparison::CFunction;
    }
    lua_settop(L, 2); // make sure there are two arguments

    if (n > 0)
    {
        LibraryWork work(L);
        if (comparison == SortComparison::Default)
            sortrec<SortComparison::Default>(L, t, work, 0, n - 1, n);
        else if (clvalue(L->base + 1)->isC)
            sortrec<SortComparison::CFunction>(L, t, work, 0, n - 1, n);
        else
            sortrec<SortComparison::LuauFunction>(L, t, work, 0, n - 1, n);
    }
    return 0;
}

// utf8.len, as upstream's utflen and utf8_decode do it, with a safepoint
// after every block of positions decoded.
const char* utf8decode(const char* o, int* val)
{
    static const unsigned int limits[] = {0xFF, 0x7F, 0x7FF, 0xFFFF};
    const unsigned char* s = reinterpret_cast<const unsigned char*>(o);
    unsigned int c = s[0];
    unsigned int res = 0; // final result
    if (c < 0x80)         // ascii?
        res = c;
    else
    {
        int count = 0; // to count number of continuation bytes
        while (c & 0x40)
        {                                   // still have continuation bytes?
            const int cc = s[++count];      // read next byte
            if ((cc & 0xC0) != 0x80)        // not a continuation byte?
                return nullptr;             // invalid byte sequence
            res = (res << 6) | (cc & 0x3F); // add lower 6 bits from cont. byte
            c <<= 1;                        // to test next bit
        }
        res |= ((c & 0x7F) << (count * 5)); // add first byte
        if (count > 3 || res > 0x10FFFF || res <= limits[count])
            return nullptr; // invalid byte sequence
        if (unsigned(res - 0xD800) < 0x800)
            return nullptr; // surrogate
        s += count;         // skip continuation bytes read
    }
    if (val)
        *val = int(res);
    return reinterpret_cast<const char*>(s) + 1; // +1 to include first byte
}

// From lutf8lib.cpp.
int utf8posrelat(int pos, size_t len)
{
    if (pos >= 0)
        return pos;
    else if (0u - size_t(pos) > len)
        return 0;
    else
        return int(len) + pos + 1;
}

int hostutf8len(lua_State* L)
{
    int n = 0;
    size_t len = 0;
    const char* s = luaL_checklstring(L, 1, &len);
    int posi = utf8posrelat(luaL_optinteger(L, 2, 1), len);
    int posj = utf8posrelat(luaL_optinteger(L, 3, -1), len);
    luaL_argcheck(L, 1 <= posi && --posi <= int(len), 2, "initial position out of string");
    luaL_argcheck(L, --posj < int(len), 3, "final position out of string");

    LibraryWork work(L);
    while (posi <= posj)
    {
        // Each character takes at least one byte, so a block of positions
        // bounds the work between safepoints without a count in the inner
        // loop: at most kLibraryPollUnits + 1 characters, each of at most
        // seven bytes read.
        const int block = int(kLibraryPollUnits);
        const int stop = posj - posi > block ? posi + block : posj;
        while (posi <= stop)
        {
            const char* s1 = utf8decode(s + posi, nullptr);
            if (s1 == nullptr)
            {                                 // conversion error?
                lua_pushnil(L);               // return nil ...
                lua_pushinteger(L, posi + 1); // ... and current position
                return 2;
            }
            posi = int(s1 - s);
            n++;
        }
        spend(work, kLibraryPollUnits);
    }
    lua_pushinteger(L, n);
    return 1;
}

// Opening a library
//
// Upstream's luaL_register stores each function with lua_setfield, which runs
// a __newindex metamethod when the key is absent, and a new library table is
// stored in the globals table with lua_settable, which does the same. If the
// host replaced its functions only after luaopen_* returned, script code that
// ran during the open (a __newindex the script set on a library table opened
// before, or on the globals table) would be handed upstream's functions, which
// run without safepoints. An error during the open (memory runs out, or the
// globals table is read-only when luaopen_table stores unpack) would leave
// them in the table too. So for a library with replaced functions:
//
// 1. The open is refused, before anything is registered, when registration
//    could reach a metamethod. Every operation that luaopen_string,
//    luaopen_table, and luaopen_utf8 perform on a table a script can reach is
//    a lua_setfield or lua_settable of a string key into the table
//    luaL_register registers into, or into the globals table: the library
//    functions themselves, utf8.charpattern, a new library table, and
//    luaopen_table's global unpack. Such a store runs a metamethod only when
//    the key is absent and the table's metatable has __newindex (luaV_settable),
//    so the open is refused if the target table's metatable has __newindex, or
//    if a store of an absent key into the globals table would happen and its
//    metatable has __newindex. Every other operation is raw (lua_rawget in
//    luaL_findtable), touches only the registry and its _LOADED table, which
//    scripts cannot reach and which never have a metatable, or touches a table
//    or metatable the open itself just created (createmetatable in
//    luaopen_string), and lua_setmetatable on a string runs nothing. The
//    collector runs no Luau code: Luau has no __gc metamethod, and userdata
//    destructors are host C functions. Nothing else that the open does can
//    run a function.
// 2. Everything the replacement needs is created first: each host closure,
//    the keys, and the value the table holds under each name before the open.
//    luaopen_* then runs in a nested protected call, and afterwards, whether
//    it succeeded or failed, each name whose value changed, which only
//    upstream's registration does, gets the host's closure. That needs only
//    raw reads and a raw store to a key that is present, which allocate
//    nothing and cannot fail. A failure is then raised again unchanged.
//
// So no script code runs while upstream's function is in a library table, and
// none ever sees one there.

// A library function the host replaces.
struct ReplacedFunction
{
    // Also the closure's debug name, which the closure keeps: a string
    // literal.
    const char* name;
    lua_CFunction host;
    // Where upstream's version is kept, for a host version that calls it.
    std::atomic<lua_CFunction>* upstream;
};

struct ReplacingLibrary
{
    // The name luaL_register registers the library under.
    const char* name;
    lua_CFunction open;
    const ReplacedFunction* functions;
    int count;
    // luaopen_table stores the global unpack after it registers.
    bool storesUnpack;
};

const ReplacedFunction kStringReplacements[] = {
    {"find", hoststringfind, &upstreamStringFind},
    {"split", hoststringsplit, &upstreamStringSplit},
};

const ReplacedFunction kTableReplacements[] = {
    {"concat", hosttableconcat, nullptr},
    {"sort", hosttablesort, nullptr},
};

const ReplacedFunction kUtf8Replacements[] = {
    {"len", hostutf8len, nullptr},
};

const ReplacingLibrary kStringLibrary = {LUA_STRLIBNAME, luaopen_string, kStringReplacements, 2, false};
const ReplacingLibrary kTableLibrary = {LUA_TABLIBNAME, luaopen_table, kTableReplacements, 2, true};
const ReplacingLibrary kUtf8Library = {LUA_UTF8LIBNAME, luaopen_utf8, kUtf8Replacements, 1, false};

// The most values openreplacinglibrary holds above its entry top besides
// those luaopen_* uses: the target, two keys, three per replaced function, the
// results, and four while it replaces.
constexpr int kLibraryOpenSlots = 16;

// Whether a store of an absent key into t runs a metamethod: the condition
// luaV_settable checks.
bool hasnewindex(lua_State* L, LuaTable* t)
{
    return t->metatable && fasttm(L, t->metatable, TM_NEWINDEX) != nullptr;
}

// Pushes the table luaL_register will register the library into: _LOADED's
// entry when it is a table, and otherwise the global of that name when it is a
// table. Pushes nil when there is neither, and sets createsTable when
// luaL_findtable will then create the table and store it in the globals table
// (the global is nil) rather than raise a name conflict. luaL_register reads
// _LOADED with lua_getfield, which is raw there: _LOADED has no metatable.
void pushregistrationtarget(lua_State* L, const char* name, bool* createsTable)
{
    *createsTable = false;
    lua_rawgetfield(L, LUA_REGISTRYINDEX, "_LOADED");
    if (lua_istable(L, -1))
    {
        lua_rawgetfield(L, -1, name);
        lua_remove(L, -2);
        if (lua_istable(L, -1))
            return;
    }
    lua_pop(L, 1);

    lua_rawgetfield(L, LUA_GLOBALSINDEX, name);
    if (lua_istable(L, -1))
        return;
    *createsTable = lua_isnil(L, -1);
    lua_pop(L, 1);
    lua_pushnil(L);
}

// Why registration could run script code, or nullptr when it cannot.
const char* registrationhazard(lua_State* L, const ReplacingLibrary& library, int target, bool createsTable)
{
    const char* globals = "the globals table has a __newindex metamethod";
    if (lua_istable(L, target))
    {
        if (hasnewindex(L, hvalue(L->base + (target - 1))))
            return "the library table has a __newindex metamethod";
    }
    else if (createsTable && hasnewindex(L, L->gt))
        return globals;

    if (library.storesUnpack && hasnewindex(L, L->gt))
    {
        lua_rawgetfield(L, LUA_GLOBALSINDEX, "unpack");
        const bool absent = lua_isnil(L, -1);
        lua_pop(L, 1);
        if (absent)
            return globals;
    }
    return nullptr;
}

struct LibraryOpenCall
{
    lua_CFunction open;
    int results;
};

void runlibraryopen(lua_State* L, void* userdata)
{
    LibraryOpenCall* call = static_cast<LibraryOpenCall*>(userdata);
    call->results = call->open(L);
}

// Opens a library whose functions the host replaces; see above. Returns the
// number of results luaopen_* returned, which replace everything it pushed.
int openreplacinglibrary(lua_State* L, const ReplacingLibrary& library)
{
    const int base = lua_gettop(L);
    const int target = base + 1;
    bool createsTable = false;
    pushregistrationtarget(L, library.name, &createsTable);
    if (const char* hazard = registrationhazard(L, library, target, createsTable))
    {
        LIBRARY_COVERAGE(openRefused);
        lua_pushfstringL(L, "cannot open the %s library while %s", library.name, hazard);
        lua_error(L);
    }

    // The keys that find the table after a failure, then for each function
    // its name, the host's closure, and the value the table holds now.
    const int loadedKey = base + 2;
    const int nameKey = base + 3;
    const int firstFunction = base + 4;
    lua_pushliteral(L, "_LOADED");
    lua_pushstring(L, library.name);
    for (int k = 0; k < library.count; ++k)
    {
        const ReplacedFunction& function = library.functions[k];
        lua_pushstring(L, function.name);
        lua_pushcfunction(L, function.host, function.name);
        lua_pushvalue(L, -2);
        if (lua_istable(L, target))
            lua_rawget(L, target);
        else
        {
            lua_pop(L, 1);
            lua_pushnil(L);
        }
    }
    const int working = lua_gettop(L);

    LibraryOpenCall call = {library.open, 0};
    const int status = luaD_pcall(L, runlibraryopen, &call, savestack(L, L->top), 0);

    // luaL_register sets _LOADED's entry before it registers anything, so
    // the table that holds upstream's functions, if any does, is that entry.
    lua_pushvalue(L, loadedKey);
    lua_rawget(L, LUA_REGISTRYINDEX);
    if (lua_istable(L, -1))
    {
        lua_pushvalue(L, nameKey);
        lua_rawget(L, -2);
        lua_remove(L, -2);
    }
    if (lua_istable(L, -1))
    {
        const int table = lua_gettop(L);
        for (int k = 0; k < library.count; ++k)
        {
            const int key = firstFunction + 3 * k;
            lua_pushvalue(L, key);
            lua_rawget(L, table);
            if (!lua_rawequal(L, -1, key + 2))
            {
                // Only registration stores this name, so the value is
                // upstream's function, and the key is present.
                std::atomic<lua_CFunction>* upstream = library.functions[k].upstream;
                if (lua_CFunction original = lua_tocfunction(L, -1); upstream && original)
                    upstream->store(original, std::memory_order_relaxed);
                lua_pushvalue(L, key);
                lua_pushvalue(L, key + 1);
                lua_rawset(L, table);
                if (status != LUA_OK)
                    LIBRARY_COVERAGE(openRepairedAfterError);
            }
            lua_pop(L, 1);
        }
    }
    lua_pop(L, 1);

    // The error object is at the top again.
    if (status != LUA_OK)
        luaD_throw(L, status);

    for (int k = 1; k <= call.results; ++k)
    {
        lua_pushvalue(L, working + k);
        lua_replace(L, base + k);
    }
    lua_settop(L, base + call.results);
    return call.results;
}

void opopenlibrary(lua_State* state, void* userdata)
{
    LibraryContext* context = static_cast<LibraryContext*>(userdata);
    switch (context->library)
    {
    case LUAU_HOST_LIBRARY_BASE: context->result = luaopen_base(state); break;
    case LUAU_HOST_LIBRARY_COROUTINE: context->result = luaopen_coroutine(state); break;
    case LUAU_HOST_LIBRARY_TABLE: context->result = openreplacinglibrary(state, kTableLibrary); break;
    case LUAU_HOST_LIBRARY_OS: context->result = luaopen_os(state); break;
    case LUAU_HOST_LIBRARY_STRING: context->result = openreplacinglibrary(state, kStringLibrary); break;
    case LUAU_HOST_LIBRARY_BIT32: context->result = luaopen_bit32(state); break;
    case LUAU_HOST_LIBRARY_BUFFER: context->result = luaopen_buffer(state); break;
    case LUAU_HOST_LIBRARY_UTF8: context->result = openreplacinglibrary(state, kUtf8Library); break;
    case LUAU_HOST_LIBRARY_MATH: context->result = luaopen_math(state); break;
    case LUAU_HOST_LIBRARY_DEBUG: context->result = luaopen_debug(state); break;
    case LUAU_HOST_LIBRARY_VECTOR: context->result = luaopen_vector(state); break;
    case LUAU_HOST_LIBRARY_INTEGER: context->result = luaopen_integer(state); break;
    default: context->result = -1; break;
    }
}

void opsandboxroot(lua_State* state, void*) { luaL_sandbox(state); }
void opsandboxthread(lua_State* state, void*) { luaL_sandboxthread(state); }

bool enterinterrupt(AllocatorContext* allocator)
{
    if (!allocator)
        return false;

    uint64_t gate = allocator->interruptGate.load(std::memory_order_acquire);
    while ((gate & kInterruptGateEnabled) != 0)
    {
        if ((gate & kInterruptGateCountMask) == kInterruptGateCountMask)
            return false;
        if (allocator->interruptGate.compare_exchange_weak(
                gate,
                gate + 1,
                std::memory_order_acq_rel,
                std::memory_order_acquire))
            return true;
    }
    return false;
}

void leaveinterrupt(AllocatorContext* allocator)
{
    if (allocator)
    {
        const uint64_t previous = allocator->interruptGate.fetch_sub(1, std::memory_order_acq_rel);
        if ((previous & kInterruptGateCountMask) == 1 && (previous & kInterruptGateEnabled) == 0)
        {
            // Once uninstall has disabled the gate, synchronize its predicate
            // check with the final callback exit so the notification cannot be
            // lost between the waiter evaluating the count and blocking.
            { std::lock_guard<std::mutex> lifecycle(allocator->interruptLifecycleMutex); }
            allocator->interruptDrained.notify_all();
        }
    }
}

// Calls the installed poll under the interrupt gate and returns its action.
// The gate is left before returning, so the caller may yield or throw.
int callinterruptpoll(lua_State* state, AllocatorContext* allocator, luau_host_interrupt_kind kind)
{
    if (!enterinterrupt(allocator))
        return 0;

    int action = 0;
    InterruptPoll poll = allocator->interruptPoll.load(std::memory_order_acquire);
    if (poll)
    {
        try
        {
            action = poll(host(state), kind);
        }
        catch (...)
        {
            // Reverse callbacks are required to contain exceptions, but the
            // native ABI remains a no-unwind boundary for a nonconforming caller.
        }
    }

    leaveinterrupt(allocator);
    return action;
}

// Applies a nonzero EXECUTION poll result: suspend when possible, otherwise
// raise the sticky CANCELED hard stop.
void stopexecution(lua_State* state, AllocatorContext* allocator)
{
    if (lua_isyieldable(state))
    {
        lua_break(state);
        return;
    }

    allocator->interrupted = true;
    luaD_throw(state, LUA_ERRMEM);
}

int64_t monotonicnanoseconds()
{
    return int64_t(std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count());
}

void scheduleinterruptevent(InterruptThrottle& throttle)
{
    uint64_t next = UINT64_MAX;
    if (throttle.countLimit != 0)
    {
        // Past the limit, a poll that keeps execution going is asked again
        // at every later safepoint, as an unthrottled poll would be.
        const uint64_t threshold = throttle.count > throttle.countLimit ? throttle.count : throttle.countLimit;
        next = threshold == UINT64_MAX ? UINT64_MAX : threshold + 1;
    }
    if (throttle.hasDeadline && throttle.nextClockCheck < next)
        next = throttle.nextClockCheck;
    throttle.nextEvent = next;
}

// A counted safepoint reached its next count-limit or clock event.
LUAU_NOINLINE void throttledinterruptevent(lua_State* state, AllocatorContext* allocator)
{
    InterruptThrottle& throttle = allocator->throttle;
    bool due = throttle.countLimit != 0 && throttle.count > throttle.countLimit;

    if (throttle.hasDeadline && throttle.count >= throttle.nextClockCheck)
    {
        const int64_t now = monotonicnanoseconds();
        const int64_t slice = now - throttle.lastClock;
        throttle.lastClock = now;

        if (now >= throttle.deadline)
        {
            // Keep reading at every safepoint until the poll stops execution.
            due = true;
            throttle.clockStride = kClockStrideMin;
        }
        else if (slice > kClockTargetSliceNs)
        {
            // Safepoints slowed down: shrink the stride in proportion so the
            // next read lands about one target slice from now.
            const uint64_t scaled = uint64_t(throttle.clockStride) * uint64_t(kClockTargetSliceNs) / uint64_t(slice);
            throttle.clockStride = scaled < kClockStrideMin ? kClockStrideMin : uint32_t(scaled);
        }
        else if (slice < kClockTargetSliceNs / 2 && throttle.clockStride < kClockStrideMax)
        {
            // Shrinking leaves arbitrary strides, so doubling can overshoot.
            const uint32_t doubled = throttle.clockStride * 2;
            throttle.clockStride = doubled > kClockStrideMax ? kClockStrideMax : doubled;
        }

        throttle.nextClockCheck = throttle.count + throttle.clockStride;
    }

    scheduleinterruptevent(throttle);
    if (due && callinterruptpoll(state, allocator, LUAU_HOST_INTERRUPT_EXECUTION) != 0)
    {
        allocator->interruptAttention.store(1, std::memory_order_relaxed);
        stopexecution(state, allocator);
    }
}

// The safepoint observed a request. Poll before counting it, as an
// unthrottled poll that delivers pending work does not count either.
LUAU_NOINLINE void throttledinterruptattention(lua_State* state, AllocatorContext* allocator)
{
    // Acquire pairs with the requester's release store, so the poll observes
    // everything the requester wrote first. A request that arrives during the
    // poll stays raised for the next safepoint.
    allocator->interruptAttention.exchange(0, std::memory_order_acquire);
    if (callinterruptpoll(state, allocator, LUAU_HOST_INTERRUPT_EXECUTION) != 0)
    {
        // Keep polling at every safepoint until a poll lets execution go on,
        // so a hard stop that a script pcall catches is raised again.
        allocator->interruptAttention.store(1, std::memory_order_relaxed);
        stopexecution(state, allocator);
        return;
    }

    InterruptThrottle& throttle = allocator->throttle;
    if (++throttle.count >= throttle.nextEvent)
        throttledinterruptevent(state, allocator);
}

void unthrottledinterrupt(lua_State* state, AllocatorContext* allocator, int gc)
{
    const bool isGcNotification = gc >= 0;
    const int action = callinterruptpoll(
        state,
        allocator,
        isGcNotification ? LUAU_HOST_INTERRUPT_GC : LUAU_HOST_INTERRUPT_EXECUTION);

    // Collector notifications are observation-only. A hostile or buggy poll
    // cannot use its return value to yield, throw, or mark the VM canceled from
    // a collector phase.
    if (isGcNotification || action == 0)
        return;

    stopexecution(state, allocator);
}

void interrupttrampoline(lua_State* state, int gc)
{
    // Install accepts only roots created with the host allocator, so the
    // shared global allocator userdata is this root's context. The common
    // throttled path touches only that context: no gate, no clock, no poll.
    AllocatorContext* allocator = static_cast<AllocatorContext*>(state->global->ud);
    InterruptThrottle& throttle = allocator->throttle;
    if (LUAU_LIKELY(throttle.enabled))
    {
        if (gc >= 0)
            return;
        if (LUAU_UNLIKELY(allocator->interruptAttention.load(std::memory_order_relaxed) != 0))
        {
            throttledinterruptattention(state, allocator);
            return;
        }
        if (LUAU_LIKELY(++throttle.count < throttle.nextEvent))
            return;
        throttledinterruptevent(state, allocator);
        return;
    }

    unthrottledinterrupt(state, allocator, gc);
}
} // namespace

#if defined(LUAU_HOST_LIBRARY_COVERAGE)
// A coverage count of the long library calls, by the name of its
// LibraryCoverage member, or UINT64_MAX for an unknown name.
uint64_t luau_host_library_coverage_count(const char* point)
{
    const std::pair<const char*, const std::atomic<uint64_t>*> points[] = {
        {"twoWayPeriodic", &libraryCoverage.twoWayPeriodic},
        {"twoWayNonPeriodic", &libraryCoverage.twoWayNonPeriodic},
        {"twoWayCriticalSkip", &libraryCoverage.twoWayCriticalSkip},
        {"twoWayResumed", &libraryCoverage.twoWayResumed},
        {"heapSort", &libraryCoverage.heapSort},
        {"findHandedToUpstream", &libraryCoverage.findHandedToUpstream},
        {"concatBufferGrowth", &libraryCoverage.concatBufferGrowth},
        {"splitRehash", &libraryCoverage.splitRehash},
        {"openRefused", &libraryCoverage.openRefused},
        {"openRepairedAfterError", &libraryCoverage.openRepairedAfterError},
    };
    for (const auto& entry : points)
    {
        if (std::strcmp(entry.first, point) == 0)
            return entry.second->load(std::memory_order_relaxed);
    }
    return UINT64_MAX;
}
#endif

extern "C"
{
luau_host_status LUAU_HOST_CALL luau_host_get_abi_info(uint32_t callerSize, luau_host_abi_info* output)
{
    if (!output)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    if (kBinaryIdentity.magic[0] != 'L')
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    luau_host_abi_info value = {};
    value.struct_size = uint32_t(sizeof(value));
    value.magic = LUAU_HOST_ABI_MAGIC;
    value.abi_major = LUAU_HOST_ABI_MAJOR;
    value.abi_minor = LUAU_HOST_ABI_MINOR;
    value.feature_flags = kRequiredFeatures;
    value.pointer_size = uint8_t(sizeof(void*));
    value.size_t_size = uint8_t(sizeof(size_t));
    const uint16_t endianProbe = 1;
    value.little_endian = *reinterpret_cast<const uint8_t*>(&endianProbe);
    value.compile_options_size = uint32_t(sizeof(luau_host_compile_options));
    value.callback_table_size = uint32_t(sizeof(luau_host_callback_table));
    value.state_options_size = uint32_t(sizeof(luau_host_state_options));
    value.memory_info_size = uint32_t(sizeof(luau_host_memory_info));
    value.buffer_size = uint32_t(sizeof(luau_host_buffer));
    value.type_nil = LUA_TNIL;
    value.type_boolean = LUA_TBOOLEAN;
    value.type_lightuserdata = LUA_TLIGHTUSERDATA;
    value.type_number = LUA_TNUMBER;
    value.type_integer = LUA_TINTEGER;
    value.type_vector = LUA_TVECTOR;
    value.type_string = LUA_TSTRING;
    value.type_table = LUA_TTABLE;
    value.type_function = LUA_TFUNCTION;
    value.type_userdata = LUA_TUSERDATA;
    value.type_thread = LUA_TTHREAD;
    value.type_buffer = LUA_TBUFFER;
    value.type_class = LUA_TCLASS;
    value.type_object = LUA_TOBJECT;
    value.upstream_revision_hash = kUpstreamRevisionHash;
    value.host_build_fingerprint = kHostBuildFingerprint;

    const uint8_t* bytes = reinterpret_cast<const uint8_t*>(&value);
    const uint32_t bytesToWrite = callerSize < sizeof(value) ? callerSize : uint32_t(sizeof(value));
    if (bytesToWrite != 0)
        std::memcpy(output, bytes, bytesToWrite);

    constexpr uint32_t fixedPrefixSize = uint32_t(offsetof(luau_host_abi_info, compile_options_size));
    return callerSize < fixedPrefixSize ? LUAU_HOST_STATUS_INVALID_ARGUMENT : LUAU_HOST_STATUS_OK;
}

luau_host_status LUAU_HOST_CALL luau_host_compile(
    const uint8_t* source,
    uint64_t sourceSize,
    const luau_host_compile_options* options,
    luau_host_buffer* output)
{
    if (!output || output->data || output->size != 0)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    if ((!source && sourceSize != 0) ||
        sourceSize > uint64_t(std::numeric_limits<size_t>::max()) ||
        sourceSize > uint64_t(std::string().max_size()))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    lua_CompileOptions translated = {};
    lua_CompileOptions* translatedPointer = nullptr;
    if (options)
    {
        if (options->struct_size < sizeof(luau_host_compile_options) ||
            options->version != LUAU_HOST_COMPILE_OPTIONS_VERSION ||
            options->reserved0 != 0 || options->flags != 0 || options->reserved1 != 0 ||
            options->optimization_level < 0 || options->optimization_level > 2 ||
            options->debug_level < 0 || options->debug_level > 2 ||
            options->type_info_level < 0 || options->type_info_level > 1 ||
            options->coverage_level < 0 || options->coverage_level > 2)
            return LUAU_HOST_STATUS_INVALID_ARGUMENT;

        translated.optimizationLevel = options->optimization_level;
        translated.debugLevel = options->debug_level;
        translated.typeInfoLevel = options->type_info_level;
        translated.coverageLevel = options->coverage_level;
        translatedPointer = &translated;
    }

    static const uint8_t emptySource = 0;
    try
    {
        size_t compiledSize = 0;
        char* compiled = luau_compile(
            reinterpret_cast<const char*>(source ? source : &emptySource),
            size_t(sourceSize),
            translatedPointer,
            &compiledSize);
        if (!compiled)
            return LUAU_HOST_STATUS_SYSTEM_OUT_OF_MEMORY;

        output->data = reinterpret_cast<uint8_t*>(compiled);
        output->size = uint64_t(compiledSize);
        return LUAU_HOST_STATUS_OK;
    }
    catch (const std::bad_alloc&)
    {
        return LUAU_HOST_STATUS_SYSTEM_OUT_OF_MEMORY;
    }
    catch (...)
    {
        return LUAU_HOST_STATUS_COMPILER_ERROR;
    }
}

void LUAU_HOST_CALL luau_host_buffer_free(luau_host_buffer* buffer)
{
    if (!buffer)
        return;
    std::free(buffer->data);
    buffer->data = nullptr;
    buffer->size = 0;
}

luau_host_status LUAU_HOST_CALL luau_host_state_create(
    const luau_host_state_options* options,
    luau_host_state** output,
    luau_host_memory_info* failureInfo)
{
    if (!output)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    *output = nullptr;
    if (failureInfo && !validmemoryinfooutput(failureInfo))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    fillmemoryinfo(nullptr, failureInfo);

    bool tracked = false;
    bool hasLimit = false;
    size_t limit = 0;
    if (options)
    {
        if (options->struct_size < sizeof(luau_host_state_options) ||
            options->version != LUAU_HOST_STATE_OPTIONS_VERSION ||
            (options->flags & ~uint16_t(LUAU_HOST_STATE_OPTION_TRACK_MEMORY)) != 0 ||
            options->memory_limit_bytes > uint64_t(std::numeric_limits<size_t>::max()))
            return LUAU_HOST_STATUS_INVALID_ARGUMENT;

        tracked = (options->flags & LUAU_HOST_STATE_OPTION_TRACK_MEMORY) != 0;
        hasLimit = options->memory_limit_bytes != 0;
        limit = size_t(options->memory_limit_bytes);
    }

    AllocatorContext* allocator = new (std::nothrow) AllocatorContext();
    if (!allocator)
        return LUAU_HOST_STATUS_SYSTEM_OUT_OF_MEMORY;

    allocator->tracked = tracked;
    allocator->hasLimit = hasLimit;
    allocator->limitBytes = limit;

    lua_State* state = lua_newstate(trackedallocator, allocator);
    if (!state)
    {
        fillmemoryinfo(allocator, failureInfo);
        luau_host_status status = allocator->failure == LUAU_HOST_ALLOCATOR_FAILURE_QUOTA
            ? LUAU_HOST_STATUS_MEMORY_QUOTA
            : LUAU_HOST_STATUS_SYSTEM_OUT_OF_MEMORY;
        allocator->magic = 0;
        delete allocator;
        return status;
    }

    lua_callbacks(state)->debuginterrupt = recordcoroutinebreak;
    *output = host(state);
    return LUAU_HOST_STATUS_OK;
}

void LUAU_HOST_CALL luau_host_state_close(luau_host_state* root)
{
    lua_State* state = native(root);
    if (!state || lua_mainthread(state) != state)
        return;

    // Root close is the final lifecycle boundary. Do not leave this root's
    // interrupt callback registered if a caller omits the explicit uninstall.
    luau_host_interrupt_uninstall(root);
    erasereferencesforroot(state);
    AllocatorContext* allocator = getallocator(state);
    lua_close(state);
    if (allocator)
    {
        allocator->magic = 0;
        delete allocator;
    }
}

luau_host_status LUAU_HOST_CALL luau_host_memory_get(luau_host_state* state, luau_host_memory_info* output)
{
    if (!state || !validmemoryinfooutput(output))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    AllocatorContext* allocator = getallocator(native(state));
    if (!allocator)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    fillmemoryinfo(allocator, output);
    return LUAU_HOST_STATUS_OK;
}

luau_host_status LUAU_HOST_CALL luau_host_memory_reset_failure(luau_host_state* state)
{
    AllocatorContext* allocator = state ? getallocator(native(state)) : nullptr;
    if (!allocator)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    allocator->failure = LUAU_HOST_ALLOCATOR_FAILURE_NONE;
    allocator->lastAttemptedBytes = 0;
    allocator->interrupted = false;
    return LUAU_HOST_STATUS_OK;
}

luau_host_state* LUAU_HOST_CALL luau_host_main_thread(luau_host_state* state) { return state ? host(lua_mainthread(native(state))) : nullptr; }
luau_host_status LUAU_HOST_CALL luau_host_thread_status(luau_host_state* state) { return state ? mapstatus(native(state), lua_status(native(state))) : LUAU_HOST_STATUS_INVALID_ARGUMENT; }

luau_host_status LUAU_HOST_CALL luau_host_thread_create(luau_host_state* parent, luau_host_state** output)
{
    if (!parent || !output)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    *output = nullptr;
    lua_State* result = nullptr;
    luau_host_status status = protect(native(parent), opnewthread, &result, 0, 1);
    if (status == LUAU_HOST_STATUS_OK)
        *output = host(result);
    return status;
}

luau_host_status LUAU_HOST_CALL luau_host_thread_reset(luau_host_state* state)
{
    if (!state)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    lua_State* target = native(state);
    if (target->isactive || (lua_status(target) == LUA_OK && target->ci != target->base_ci))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    int status = luaD_rawrunprotected(target, opresetthread, nullptr);
    return status == LUA_OK ? LUAU_HOST_STATUS_OK : LUAU_HOST_STATUS_TERMINAL_RESET;
}

int32_t LUAU_HOST_CALL luau_host_stack_abs_index(luau_host_state* state, int32_t index)
{
    return state && validordinaryindex(native(state), index) ? lua_absindex(native(state), index) : 0;
}
int32_t LUAU_HOST_CALL luau_host_stack_get_top(luau_host_state* state) { return state ? lua_gettop(native(state)) : 0; }
int32_t LUAU_HOST_CALL luau_host_type(luau_host_state* state, int32_t index)
{
    return state && validordinaryindex(native(state), index) ? lua_type(native(state), index) : LUA_TNONE;
}
const uint8_t* LUAU_HOST_CALL luau_host_type_name(luau_host_state* state, int32_t type)
{
    return state && type >= LUA_TNONE && type < LUA_T_COUNT
        ? reinterpret_cast<const uint8_t*>(lua_typename(native(state), type))
        : nullptr;
}
int32_t LUAU_HOST_CALL luau_host_object_length(luau_host_state* state, int32_t index)
{
    if (!state || !validordinaryindex(native(state), index))
        return 0;

    const TValue* value = luaA_toobject(native(state), index);
    if (ttisuserdata(value))
    {
        if (UserdataOwnerPayload* owner = wrappeduserdata(uvalue(value)))
            return int32_t(owner->size);
    }
    return lua_objlen(native(state), index);
}
int32_t LUAU_HOST_CALL luau_host_is_yieldable(luau_host_state* state) { return state ? lua_isyieldable(native(state)) : 0; }

luau_host_status LUAU_HOST_CALL luau_host_stack_set_top(luau_host_state* state, int32_t index)
{
    if (!state)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    const int32_t top = lua_gettop(native(state));
    if ((index > LUAI_MAXCSTACK && index > top) || (index < 0 && -int64_t(index) - 1 > top))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    // Shrinking only moves the top down, so it needs no slot and cannot fail.
    // A caller must always be able to discard values, even from a frame that
    // Luau filled past the limit. Use the relative form: an absolute
    // lua_settop runs ensure_stack, which can raise on such a frame.
    const int32_t target = index >= 0 ? index : top + index + 1;
    if (target <= top)
    {
        lua_settop(native(state), target - top - 1);
        return LUAU_HOST_STATUS_OK;
    }

    IndexContext c = {index, 0};
    return protect(native(state), opsettop, &c, 0, target - top);
}

// Insert, remove, and replace neither allocate nor raise, so they cannot fail
// once arguments are valid, including on a frame above the slot limit. Remove
// and replace only move existing values. lua_insert also writes one temporary
// value at the top slot; that slot is always allocated, because the top never
// passes stack_last and Luau keeps EXTRA_STACK slots beyond it.
luau_host_status LUAU_HOST_CALL luau_host_stack_insert(luau_host_state* state, int32_t index)
{
    if (!state || !validordinaryindex(native(state), index))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    lua_insert(native(state), index);
    return LUAU_HOST_STATUS_OK;
}
luau_host_status LUAU_HOST_CALL luau_host_stack_remove(luau_host_state* state, int32_t index)
{
    if (!state || !validordinaryindex(native(state), index))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    lua_remove(native(state), index);
    return LUAU_HOST_STATUS_OK;
}
luau_host_status LUAU_HOST_CALL luau_host_stack_replace(luau_host_state* state, int32_t index)
{
    if (!state || !validordinaryindex(native(state), index))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    lua_replace(native(state), index);
    return LUAU_HOST_STATUS_OK;
}

luau_host_status LUAU_HOST_CALL luau_host_stack_move(luau_host_state* from, luau_host_state* to, int32_t count)
{
    if (!from || !to || count < 0 ||
        lua_mainthread(native(from)) != lua_mainthread(native(to)) ||
        count > lua_gettop(native(from)))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    if (from != to && count > LUAI_MAXCSTACK - lua_gettop(native(to)))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    // Once the destination has room, lua_xmove only copies values and moves
    // both tops, so it cannot fail and needs no slot on the source. This lets
    // a caller drain a full frame into another thread.
    if (from == to || lua_checkstack(native(to), count))
    {
        lua_xmove(native(from), native(to), count);
        return LUAU_HOST_STATUS_OK;
    }

    // Growing the destination failed. Report that through the source: restore
    // the complete source stack and append the error object there.
    MoveContext context = {native(to), count};
    return protect(native(from), opmove, &context, 0, 0);
}

luau_host_status LUAU_HOST_CALL luau_host_stack_check(luau_host_state* state, int32_t size, int32_t* result)
{
    if (!state || size < 0)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    // lua_checkstack contains its own growth failure and never raises.
    const int32_t reserved = lua_checkstack(native(state), size);
    if (result)
        *result = reserved;
    return LUAU_HOST_STATUS_OK;
}

int32_t LUAU_HOST_CALL luau_host_to_boolean(luau_host_state* state, int32_t index)
{
    return state && validordinaryindex(native(state), index) ? lua_toboolean(native(state), index) : 0;
}

double LUAU_HOST_CALL luau_host_to_number(luau_host_state* state, int32_t index, int32_t* isNumber)
{
    if (isNumber) *isNumber = 0;
    return state && validordinaryindex(native(state), index) ? lua_tonumberx(native(state), index, isNumber) : 0;
}

int32_t LUAU_HOST_CALL luau_host_to_integer32(luau_host_state* state, int32_t index, int32_t* isInteger)
{
    if (isInteger) *isInteger = 0;
    if (!state || !validordinaryindex(native(state), index))
        return 0;

    int isNumber = 0;
    const double value = lua_tonumberx(native(state), index, &isNumber);
    if (!isNumber || !std::isfinite(value))
        return 0;

    const double truncated = std::trunc(value);
    if (truncated < double(std::numeric_limits<int32_t>::min()) ||
        truncated > double(std::numeric_limits<int32_t>::max()))
        return 0;
    if (isInteger) *isInteger = 1;
    return int32_t(truncated);
}

uint32_t LUAU_HOST_CALL luau_host_to_unsigned32(luau_host_state* state, int32_t index, int32_t* isInteger)
{
    if (isInteger) *isInteger = 0;
    if (!state || !validordinaryindex(native(state), index))
        return 0;

    int isNumber = 0;
    const double value = lua_tonumberx(native(state), index, &isNumber);
    if (!isNumber || !std::isfinite(value) || value < 0.0)
        return 0;

    const double truncated = std::trunc(value);
    if (truncated > double(std::numeric_limits<uint32_t>::max()))
        return 0;
    if (isInteger) *isInteger = 1;
    return uint32_t(truncated);
}

int64_t LUAU_HOST_CALL luau_host_to_integer64(luau_host_state* state, int32_t index, int32_t* isInteger)
{
    if (isInteger) *isInteger = 0;
    return state && validordinaryindex(native(state), index) ? lua_tointeger64(native(state), index, isInteger) : 0;
}

const float* LUAU_HOST_CALL luau_host_to_vector(luau_host_state* state, int32_t index)
{
    return state && validordinaryindex(native(state), index) ? lua_tovector(native(state), index) : nullptr;
}

const uint8_t* LUAU_HOST_CALL luau_host_to_string_view(luau_host_state* state, int32_t index, uint64_t* length)
{
    if (length) *length = 0;
    if (!state || !validordinaryindex(native(state), index) || lua_type(native(state), index) != LUA_TSTRING)
        return nullptr;
    size_t nativeLength = 0;
    const char* value = lua_tolstring(native(state), index, &nativeLength);
    if (length) *length = uint64_t(nativeLength);
    return reinterpret_cast<const uint8_t*>(value);
}

void* LUAU_HOST_CALL luau_host_to_light_userdata(luau_host_state* state, int32_t index)
{
    return state && validordinaryindex(native(state), index) ? lua_tolightuserdata(native(state), index) : nullptr;
}

void* LUAU_HOST_CALL luau_host_to_userdata(luau_host_state* state, int32_t index)
{
    return state && validordinaryindex(native(state), index)
        ? exposeduserdata(luaA_toobject(native(state), index))
        : nullptr;
}

luau_host_state* LUAU_HOST_CALL luau_host_to_thread(luau_host_state* state, int32_t index)
{
    return state && validordinaryindex(native(state), index) ? host(lua_tothread(native(state), index)) : nullptr;
}

void* LUAU_HOST_CALL luau_host_to_buffer(luau_host_state* state, int32_t index, uint64_t* length)
{
    if (length) *length = 0;
    if (!state || !validordinaryindex(native(state), index))
        return nullptr;
    size_t nativeLength = 0;
    void* value = lua_tobuffer(native(state), index, &nativeLength);
    if (length) *length = uint64_t(nativeLength);
    return value;
}

const void* LUAU_HOST_CALL luau_host_to_pointer(luau_host_state* state, int32_t index)
{
    if (!state || !validordinaryindex(native(state), index))
        return nullptr;
    const TValue* value = luaA_toobject(native(state), index);
    return ttisuserdata(value) ? exposeduserdata(value) : lua_topointer(native(state), index);
}

uint64_t LUAU_HOST_CALL luau_host_callback_registration_id(luau_host_state* state)
{
    if (!state)
        return 0;

    lua_State* target = native(state);
    if (!validcallbackframe(target))
        return 0;

    CallbackOwnerPayload* owner = currentcallbackpayload(target);
    return owner ? owner->registrationId : 0;
}

luau_host_status LUAU_HOST_CALL luau_host_push_value(luau_host_state* state, int32_t index)
{
    if (!state || !validordinaryindex(native(state), index))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    IndexContext c = {index, 0};
    return protect(native(state), oppushvalue, &c, 0, 1);
}
luau_host_status LUAU_HOST_CALL luau_host_push_nil(luau_host_state* state) { return protect(native(state), oppushnil, nullptr, 0, 1); }
luau_host_status LUAU_HOST_CALL luau_host_push_boolean(luau_host_state* state, int32_t value) { IntContext c = {value, 0}; return protect(native(state), oppushboolean, &c, 0, 1); }
luau_host_status LUAU_HOST_CALL luau_host_push_integer(luau_host_state* state, int64_t value) { Int64Context c = {value}; return protect(native(state), oppushinteger, &c, 0, 1); }
luau_host_status LUAU_HOST_CALL luau_host_push_number(luau_host_state* state, double value) { NumberContext c = {value}; return protect(native(state), oppushnumber, &c, 0, 1); }
luau_host_status LUAU_HOST_CALL luau_host_push_vector(luau_host_state* state, float x, float y, float z) { VectorContext c = {x, y, z}; return protect(native(state), oppushvector, &c, 0, 1); }

luau_host_status LUAU_HOST_CALL luau_host_push_string(luau_host_state* state, const uint8_t* value, uint64_t length)
{
    if (!state || (!value && length != 0) || length > uint64_t(std::numeric_limits<size_t>::max()))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    static const uint8_t empty = 0;
    StringContext c = {reinterpret_cast<const char*>(value ? value : &empty), size_t(length), 0, 0, nullptr};
    return protect(native(state), oppushstring, &c, 0, 1);
}

luau_host_status LUAU_HOST_CALL luau_host_push_light_userdata(luau_host_state* state, void* value, int32_t tag)
{
    if (!state || tag < 0 || tag >= LUA_LUTAG_LIMIT)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    LightUserdataContext c = {value, tag};
    return protect(native(state), oppushlightuserdata, &c, 0, 1);
}

luau_host_status LUAU_HOST_CALL luau_host_push_thread(luau_host_state* state, int32_t* isMainThread)
{
    IntContext c = {};
    luau_host_status status = protect(native(state), oppushthread, &c, 0, 1);
    if (status == LUAU_HOST_STATUS_OK && isMainThread) *isMainThread = c.result;
    return status;
}

luau_host_status LUAU_HOST_CALL luau_host_push_callback(
    luau_host_state* state,
    const luau_host_callback_table* callbacks,
    const uint8_t* debugName,
    uint64_t debugNameSize,
    int32_t upvalueCount,
    int32_t* ownerTransferred,
    int32_t* errorObject)
{
    if (ownerTransferred)
        *ownerTransferred = 0;
    if (errorObject)
        *errorObject = 0;

    const bool hasOwnerUserdata = callbacks && callbacks->userdata != nullptr;
    const bool hasOwnerDestructor = callbacks && callbacks->userdata_destructor != nullptr;
    if (!state ||
        !validcallbacks(callbacks) ||
        !callbacks->managed_function ||
        callbacks->registration_id == 0 ||
        (!debugName && debugNameSize != 0) ||
        debugNameSize > uint64_t(std::numeric_limits<size_t>::max()) ||
        hasOwnerUserdata != hasOwnerDestructor ||
        upvalueCount < 0 ||
        lua_gettop(native(state)) < upvalueCount)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    if (upvalueCount > 254)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    const size_t nativeDebugNameSize = size_t(debugNameSize);
    const size_t ownerPrefix = offsetof(CallbackOwnerPayload, debugName);
    if (nativeDebugNameSize > std::numeric_limits<size_t>::max() - ownerPrefix - 1)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    ClosureContext c = {
        callbacks->managed_function,
        callbacks->registration_id,
        debugNameSize != 0 ? reinterpret_cast<const char*>(debugName) : nullptr,
        nativeDebugNameSize,
        upvalueCount,
        callbacks->userdata,
        callbacks->userdata_destructor,
        false};

    // The owner userdata is pushed above the upvalues before the closure
    // consumes them, so the operation briefly holds one extra value.
    const luau_host_status status = protect(native(state), oppushcallback, &c, upvalueCount, 1);
    if (ownerTransferred)
        *ownerTransferred = c.ownerTransferred ? 1 : 0;
    if (errorObject && appendserror(status))
        *errorObject = 1;
    return status;
}

luau_host_status LUAU_HOST_CALL luau_host_userdata_create(luau_host_state* state, uint64_t size, int32_t tag, void** output)
{
    if (!state || size > uint64_t(std::numeric_limits<size_t>::max()) || tag < 0 || tag >= LUA_UTAG_LIMIT)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    if (output) *output = nullptr;
    AllocationContext c = {size_t(size), tag, nullptr, nullptr};
    luau_host_status status = protect(native(state), opnewuserdata, &c, 0, 1);
    if (status == LUAU_HOST_STATUS_OK && output) *output = c.result;
    return status;
}

luau_host_status LUAU_HOST_CALL luau_host_userdata_create_with_destructor(
    luau_host_state* state,
    uint64_t size,
    const luau_host_callback_table* callbacks,
    void** output)
{
    constexpr size_t prefix = offsetof(UserdataOwnerPayload, data);
    if (!state || size > uint64_t(std::numeric_limits<size_t>::max() - prefix - sizeof(NativeUserdataDestructor)) ||
        !validcallbacks(callbacks) || !callbacks->userdata_destructor)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    if (output) *output = nullptr;
    AllocationContext c = {size_t(size), 0, reinterpret_cast<NativeUserdataDestructor>(callbacks->userdata_destructor), nullptr};
    luau_host_status status = protect(native(state), opnewuserdatadtor, &c, 0, 1);
    if (status == LUAU_HOST_STATUS_OK && output) *output = c.result;
    return status;
}

luau_host_status LUAU_HOST_CALL luau_host_buffer_create(luau_host_state* state, uint64_t size, void** output)
{
    if (!state || size > uint64_t(std::numeric_limits<size_t>::max())) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    if (output) *output = nullptr;
    AllocationContext c = {size_t(size), 0, nullptr, nullptr};
    luau_host_status status = protect(native(state), opnewbuffer, &c, 0, 1);
    if (status == LUAU_HOST_STATUS_OK && output) *output = c.result;
    return status;
}

luau_host_status LUAU_HOST_CALL luau_host_table_get(luau_host_state* state, int32_t index, int32_t* type)
{
    if (!state || !validordinaryindex(native(state), index)) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    // lua_gettable may call an __index metamethod.
    if (!runnable(native(state))) return LUAU_HOST_STATUS_THREAD_NOT_RUNNABLE;
    IndexContext c = {index, 0};
    luau_host_status s = protect(native(state), opgettable, &c, 1, 0);
    if (s == LUAU_HOST_STATUS_OK && type) *type = c.result;
    return s;
}

luau_host_status LUAU_HOST_CALL luau_host_table_set(luau_host_state* state, int32_t index)
{
    if (!state || !validordinaryindex(native(state), index) || lua_gettop(native(state)) < 2)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    // lua_settable may call a __newindex metamethod.
    if (!runnable(native(state))) return LUAU_HOST_STATUS_THREAD_NOT_RUNNABLE;
    IndexContext c = {index, 0};
    return protect(native(state), opsettable, &c, 2, 0);
}

luau_host_status LUAU_HOST_CALL luau_host_table_raw_get(luau_host_state* state, int32_t index, int32_t* type)
{
    if (!state || !validtableindex(native(state), index)) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    IndexContext c = {index, 0};
    luau_host_status s = protect(native(state), oprawget, &c, 1, 0);
    if (s == LUAU_HOST_STATUS_OK && type) *type = c.result;
    return s;
}

luau_host_status LUAU_HOST_CALL luau_host_table_raw_set(luau_host_state* state, int32_t index)
{
    if (!state || !validtableindex(native(state), index)) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    IndexContext c = {index, 0};
    return protect(native(state), oprawset, &c, 2, 0);
}

luau_host_status LUAU_HOST_CALL luau_host_table_next(luau_host_state* state, int32_t index, int32_t* hasNext)
{
    if (!state || !validtableindex(native(state), index)) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    IndexContext c = {index, 0};
    luau_host_status s = protect(native(state), opnext, &c, 1, 1);
    if (s == LUAU_HOST_STATUS_OK && hasNext) *hasNext = c.result;
    return s;
}

luau_host_status LUAU_HOST_CALL luau_host_table_create(luau_host_state* state, int32_t arraySize, int32_t recordSize)
{
    if (!state || arraySize < 0 || recordSize < 0) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    TableContext c = {arraySize, recordSize};
    return protect(native(state), opcreatetable, &c, 0, 1);
}

luau_host_status LUAU_HOST_CALL luau_host_table_clear(luau_host_state* state, int32_t index)
{
    if (!state || !validtableindex(native(state), index)) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    IndexContext c = {index, 0};
    return protect(native(state), opcleartable, &c, 0, 0);
}

luau_host_status LUAU_HOST_CALL luau_host_table_clone(luau_host_state* state, int32_t index)
{
    if (!state || !validtableindex(native(state), index)) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    IndexContext c = {index, 0};
    return protect(native(state), opclonetable, &c, 0, 1);
}

luau_host_status LUAU_HOST_CALL luau_host_metatable_get(luau_host_state* state, int32_t index, int32_t* hasMetatable)
{
    if (!state || !validordinaryindex(native(state), index)) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    IndexContext c = {index, 0};
    luau_host_status s = protect(native(state), opgetmetatable, &c, 0, 1);
    if (s == LUAU_HOST_STATUS_OK && hasMetatable) *hasMetatable = c.result;
    return s;
}

luau_host_status LUAU_HOST_CALL luau_host_metatable_set(luau_host_state* state, int32_t index, int32_t* result)
{
    if (!state || !validordinaryindex(native(state), index) || lua_gettop(native(state)) < 1)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    const int metatableType = lua_type(native(state), -1);
    if (metatableType != LUA_TNIL && metatableType != LUA_TTABLE)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    IndexContext c = {index, 0};
    luau_host_status s = protect(native(state), opsetmetatable, &c, 1, 0);
    if (s == LUAU_HOST_STATUS_OK && result) *result = c.result;
    return s;
}

luau_host_status LUAU_HOST_CALL luau_host_table_set_readonly(luau_host_state* state, int32_t index, int32_t enabled)
{
    if (!state || !validtableindex(native(state), index)) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    IntContext c = {enabled, index};
    return protect(native(state), opsetreadonly, &c, 0, 0);
}

luau_host_status LUAU_HOST_CALL luau_host_global_get(luau_host_state* state, const uint8_t* key, int32_t* type)
{
    if (!state || !key) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    // A globals table may have an __index metamethod.
    if (!runnable(native(state))) return LUAU_HOST_STATUS_THREAD_NOT_RUNNABLE;
    StringContext c = {reinterpret_cast<const char*>(key), 0, LUA_GLOBALSINDEX, 0, nullptr};
    luau_host_status s = protect(native(state), opgetfield, &c, 0, 1);
    if (s == LUAU_HOST_STATUS_OK && type) *type = c.result;
    return s;
}

luau_host_status LUAU_HOST_CALL luau_host_global_set(luau_host_state* state, const uint8_t* key)
{
    if (!state || !key || lua_gettop(native(state)) < 1) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    // A sandboxed thread's globals have a Luau __newindex guard.
    if (!runnable(native(state))) return LUAU_HOST_STATUS_THREAD_NOT_RUNNABLE;
    StringContext c = {reinterpret_cast<const char*>(key), 0, LUA_GLOBALSINDEX, 0, nullptr};
    return protect(native(state), opsetfield, &c, 1, 0);
}

luau_host_status LUAU_HOST_CALL luau_host_global_push(luau_host_state* state) { return protect(native(state), opglobalpush, nullptr, 0, 1); }
int32_t LUAU_HOST_CALL luau_host_is_global(luau_host_state* state, int32_t index)
{
    return state && validordinaryindex(native(state), index) ? lua_rawequal(native(state), index, LUA_GLOBALSINDEX) : 0;
}

luau_host_status LUAU_HOST_CALL luau_host_reference_create(luau_host_state* state, int32_t index, int32_t* reference)
{
    if (!reference)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    *reference = LUA_REFNIL;
    if (!state || !validordinaryindex(native(state), index)) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    IndexContext c = {index, 0};
    luau_host_status s = protect(native(state), oprefcreate, &c, 0, 0);
    if (s != LUAU_HOST_STATUS_OK)
        return s;

    s = registerreference(native(state), c.result, reference);
    if (s != LUAU_HOST_STATUS_OK)
    {
        lua_unref(native(state), c.result);

        // C++ registry bookkeeping happens after the protected lua_ref.  If
        // that host allocation fails, append a protected error sentinel so
        // SYSTEM_OUT_OF_MEMORY keeps the same +1 error shape as VM allocation
        // failures and managed error handling cannot consume a caller value.
        // protect() reserved this slot for oprefcreate's error object, and the
        // successful lua_ref left the top unchanged, so the push has room.
        if (s == LUAU_HOST_STATUS_SYSTEM_OUT_OF_MEMORY)
        {
            const luau_host_status errorStatus = protect(native(state), oppushnil, nullptr, 0, 1);
            if (errorStatus != LUAU_HOST_STATUS_OK)
                return errorStatus;
        }
    }
    return s;
}

luau_host_status LUAU_HOST_CALL luau_host_reference_push(luau_host_state* state, int32_t reference, int32_t* type)
{
    int registryReference = 0;
    if (!state || !lookupreference(native(state), reference, &registryReference))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    RawIndexContext c = {LUA_REGISTRYINDEX, registryReference, 0};
    luau_host_status s = protect(native(state), oprefpush, &c, 0, 1);
    if (s == LUAU_HOST_STATUS_OK && type) *type = c.result;
    return s;
}

luau_host_status LUAU_HOST_CALL luau_host_reference_release(luau_host_state* state, int32_t reference)
{
    if (!state) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    if (reference <= LUA_REFNIL) return LUAU_HOST_STATUS_OK;
    int registryReference = 0;
    if (!lookupreference(native(state), reference, &registryReference))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    // lua_unref overwrites an existing registry slot: it neither allocates nor
    // raises, so release needs no free slot and cannot fail. Callers release
    // references during cleanup, when the frame may be full.
    lua_unref(native(state), registryReference);
    erasereference(native(state), reference, registryReference);
    return LUAU_HOST_STATUS_OK;
}

luau_host_status LUAU_HOST_CALL luau_host_to_string(luau_host_state* state, int32_t index, const uint8_t** output, uint64_t* length)
{
    if (output) *output = nullptr;
    if (length) *length = 0;
    if (!state || !validordinaryindex(native(state), index))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    StringContext c = {nullptr, 0, index, 0, nullptr};
    luau_host_status s = protect(native(state), optostring, &c, 0, 0);
    if (s == LUAU_HOST_STATUS_OK)
    {
        if (output) *output = reinterpret_cast<const uint8_t*>(c.pointerResult);
        if (length) *length = uint64_t(c.length);
    }
    return s;
}

luau_host_status LUAU_HOST_CALL luau_host_to_display_string(luau_host_state* state, int32_t index, const uint8_t** output, uint64_t* length)
{
    if (output) *output = nullptr;
    if (length) *length = 0;
    if (!state || !validordinaryindex(native(state), index))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    // luaL_tolstring may call a __tostring metamethod.
    if (!runnable(native(state)))
        return LUAU_HOST_STATUS_THREAD_NOT_RUNNABLE;
    StringContext c = {nullptr, 0, index, 0, nullptr};
    luau_host_status s = protect(native(state), opdisplaystring, &c, 0, LUA_MINSTACK);
    if (s == LUAU_HOST_STATUS_OK)
    {
        if (output) *output = reinterpret_cast<const uint8_t*>(c.pointerResult);
        if (length) *length = uint64_t(c.length);
    }
    return s;
}

luau_host_status LUAU_HOST_CALL luau_host_load(
    luau_host_state* state,
    const uint8_t* chunkName,
    const uint8_t* bytecode,
    uint64_t bytecodeSize,
    int32_t environment,
    luau_host_status* loadStatus)
{
    if (!state || !chunkName || !bytecode || bytecodeSize == 0 || bytecodeSize > uint64_t(std::numeric_limits<size_t>::max()) ||
        (environment != 0 && !validtableindex(native(state), environment)))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    // In a safe environment luau_load resolves imports such as a.b.c with
    // ordinary table reads, which may call __index metamethods.
    if (!runnable(native(state)))
        return LUAU_HOST_STATUS_THREAD_NOT_RUNNABLE;
    LoadContext c = {
        reinterpret_cast<const char*>(chunkName),
        reinterpret_cast<const char*>(bytecode),
        size_t(bytecodeSize),
        environment,
        LUA_OK};
    luau_host_status outer = protect(native(state), opload, &c, 0, 1);
    // luau_load returns a boolean-like 0/1 load result, not lua_Status. A
    // nonzero result is an ordinary bytecode/load error retained on the stack.
    if (outer == LUAU_HOST_STATUS_OK && loadStatus)
        *loadStatus = c.result == 0 ? LUAU_HOST_STATUS_OK : LUAU_HOST_STATUS_LUA_ERROR;
    return outer;
}

luau_host_status LUAU_HOST_CALL luau_host_pcall(luau_host_state* state, int32_t argumentCount, int32_t resultCount, int32_t errorFunction)
{
    if (!state || argumentCount < 0 || resultCount < -1)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    lua_State* target = native(state);
    const int32_t top = lua_gettop(target);
    if (argumentCount >= top)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    if (errorFunction != 0)
    {
        const bool validPositive = errorFunction > 0 && errorFunction <= top;
        const bool validNegative = errorFunction < 0 && errorFunction > LUA_REGISTRYINDEX && -int64_t(errorFunction) <= top;
        if (!validPositive && !validNegative)
            return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    }

    const int32_t required = resultCount > argumentCount + 1
        ? resultCount - (argumentCount + 1)
        : 0;
    if (required > LUAI_MAXCSTACK - top)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    if (!runnable(target))
        return LUAU_HOST_STATUS_THREAD_NOT_RUNNABLE;

    PCallContext c = {argumentCount, resultCount, errorFunction, LUA_OK};
    luau_host_status outer = protect(target, oppcall, &c, argumentCount + 1, required);
    return outer == LUAU_HOST_STATUS_OK ? mapstatus(target, c.status) : outer;
}

luau_host_status LUAU_HOST_CALL luau_host_resume(luau_host_state* state, luau_host_state* from, int32_t argumentCount)
{
    if (!state || argumentCount < 0 || argumentCount > lua_gettop(resumetarget(native(state))) ||
        (from && lua_mainthread(native(state)) != lua_mainthread(native(from))))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    ResumeContext c = {native(from), argumentCount, LUA_OK, false};
    int outer = luaD_rawrunprotected(native(state), opresume, &c);
    return outer == LUA_OK ? mapstatus(native(state), c.status) : mapstatus(native(state), outer);
}

luau_host_status LUAU_HOST_CALL luau_host_resume_error(luau_host_state* state, luau_host_state* from)
{
    if (!state || lua_gettop(resumetarget(native(state))) < 1 ||
        (from && lua_mainthread(native(state)) != lua_mainthread(native(from))))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    ResumeContext c = {native(from), 0, LUA_OK, true};
    int outer = luaD_rawrunprotected(native(state), opresume, &c);
    return outer == LUA_OK ? mapstatus(native(state), c.status) : mapstatus(native(state), outer);
}

int32_t LUAU_HOST_CALL luau_host_yield(luau_host_state* state, int32_t resultCount)
{
    if (!state)
        return 0;
    lua_State* target = native(state);
    if (!validcallbackframe(target) || !lua_isyieldable(target) || resultCount < 0 || resultCount > lua_gettop(target))
        return 0;
    return lua_yield(target, resultCount);
}

int32_t LUAU_HOST_CALL luau_host_suspend(luau_host_state* state)
{
    lua_State* target = native(state);
    if (!validcallbackframe(target) || !lua_isyieldable(target))
        return 0;
    return lua_break(target);
}

luau_host_state* LUAU_HOST_CALL luau_host_resume_target(luau_host_state* state)
{
    return host(resumetarget(native(state)));
}

luau_host_status LUAU_HOST_CALL luau_host_collect(luau_host_state* state)
{
    if (!state)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    return protect(native(state), opcollect, nullptr, 0, 0);
}

luau_host_status LUAU_HOST_CALL luau_host_open_library(luau_host_state* state, luau_host_library library, int32_t* resultCount)
{
    if (!state || library < LUAU_HOST_LIBRARY_BASE || library > LUAU_HOST_LIBRARY_INTEGER)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    // Registration reads and writes the thread's globals, which may have
    // __index or __newindex metamethods.
    if (!runnable(native(state)))
        return LUAU_HOST_STATUS_THREAD_NOT_RUNNABLE;
    LibraryContext c = {library, 0};
    // The open holds the values that make the replacement of library
    // functions unable to fail, below what luaopen_* uses (see "Opening a
    // library").
    luau_host_status s = protect(native(state), opopenlibrary, &c, 0, LUA_MINSTACK + kLibraryOpenSlots);
    if (s == LUAU_HOST_STATUS_OK && resultCount) *resultCount = c.result;
    return s;
}

luau_host_status LUAU_HOST_CALL luau_host_sandbox_root(luau_host_state* state) { return protect(native(state), opsandboxroot, nullptr, 0, LUA_MINSTACK); }
luau_host_status LUAU_HOST_CALL luau_host_sandbox_thread(luau_host_state* state) { return protect(native(state), opsandboxthread, nullptr, 0, LUA_MINSTACK); }

luau_host_status LUAU_HOST_CALL luau_host_interrupt_install(luau_host_state* state, const luau_host_callback_table* callbacks)
{
    if (!state || !validcallbacks(callbacks) || !callbacks->interrupt_poll)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    AllocatorContext* allocator = getallocator(native(state));
    if (!allocator)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    lua_Callbacks* nativeCallbacks = lua_callbacks(native(state));
    InterruptPoll poll = callbacks->interrupt_poll;
    std::lock_guard<std::mutex> lifecycle(allocator->interruptLifecycleMutex);

    InterruptPoll current = allocator->interruptPoll.load(std::memory_order_acquire);
    if (nativeCallbacks->interrupt == interrupttrampoline)
    {
        const bool enabled = (allocator->interruptGate.load(std::memory_order_acquire) & kInterruptGateEnabled) != 0;
        return current == poll && enabled ? LUAU_HOST_STATUS_OK : LUAU_HOST_STATUS_INVALID_ARGUMENT;
    }

    if (allocator->interruptGate.load(std::memory_order_acquire) != 0)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    // A fresh install starts unthrottled; configure opts into throttling.
    allocator->throttle = InterruptThrottle();
    allocator->interruptAttention.store(0, std::memory_order_relaxed);
    allocator->interruptPoll.store(poll, std::memory_order_release);
    allocator->interruptGate.store(kInterruptGateEnabled, std::memory_order_release);
    nativeCallbacks->interrupt = interrupttrampoline;
    return LUAU_HOST_STATUS_OK;
}

void LUAU_HOST_CALL luau_host_interrupt_uninstall(luau_host_state* state)
{
    if (!state)
        return;

    AllocatorContext* allocator = getallocator(native(state));
    if (!allocator)
        return;
    lua_Callbacks* callbacks = lua_callbacks(native(state));
    std::unique_lock<std::mutex> lifecycle(allocator->interruptLifecycleMutex);
    if (callbacks->interrupt == interrupttrampoline)
    {
        allocator->interruptGate.fetch_and(kInterruptGateCountMask, std::memory_order_acq_rel);
        callbacks->interrupt = nullptr;
        allocator->interruptDrained.wait(lifecycle, [allocator] {
            return (allocator->interruptGate.load(std::memory_order_acquire) & kInterruptGateCountMask) == 0;
        });
        allocator->interruptPoll.store(nullptr, std::memory_order_release);
    }
}

luau_host_status LUAU_HOST_CALL luau_host_interrupt_configure(
    luau_host_state* state,
    uint64_t countLimit,
    uint64_t timeLimitNs)
{
    if (!state)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    AllocatorContext* allocator = getallocator(native(state));
    if (!allocator || lua_callbacks(native(state))->interrupt != interrupttrampoline)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    InterruptThrottle throttle;
    throttle.enabled = true;
    throttle.countLimit = countLimit;
    throttle.hasDeadline = timeLimitNs != 0;
    if (throttle.hasDeadline)
    {
        const int64_t now = monotonicnanoseconds();
        const uint64_t headroom = now >= 0 ? uint64_t(INT64_MAX) - uint64_t(now) : uint64_t(INT64_MAX);
        throttle.deadline = timeLimitNs >= headroom ? INT64_MAX : now + int64_t(timeLimitNs);
        throttle.lastClock = now;
        throttle.nextClockCheck = throttle.clockStride;
    }
    scheduleinterruptevent(throttle);

    allocator->throttle = throttle;
    allocator->interruptAttention.store(0, std::memory_order_relaxed);
    return LUAU_HOST_STATUS_OK;
}

void LUAU_HOST_CALL luau_host_interrupt_request(luau_host_state* state)
{
    // Only immutable global-state fields are read here, so this is safe
    // while another thread executes the root.
    AllocatorContext* allocator = state ? getallocator(native(state)) : nullptr;
    if (allocator)
        allocator->interruptAttention.store(1, std::memory_order_release);
}

uint64_t LUAU_HOST_CALL luau_host_interrupt_count(luau_host_state* state)
{
    AllocatorContext* allocator = state ? getallocator(native(state)) : nullptr;
    return allocator && allocator->throttle.enabled ? allocator->throttle.count : 0;
}
} // extern "C"
