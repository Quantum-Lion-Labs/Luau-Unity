#include "luau_host.h"

#include "lua.h"
#include "lualib.h"
#include "luacode.h"

#include "lapi.h"
#include "ldo.h"
#include "ludata.h"

#include "reference_tokens.h"
#include "tracked_allocation.h"

#include <atomic>
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

#ifndef LUAU_HOST_UPSTREAM_REVISION
#define LUAU_HOST_UPSTREAM_REVISION "6e9b580e2e24643214caf0f4bbbb3db911ca30f3"
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
    LUAU_HOST_FEATURE_COROUTINE_SUSPENSION;

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

struct AllocatorContext
{
    uint64_t magic = kAllocatorMagic;
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

void requirestack(lua_State* state, int size)
{
    if (!lua_checkstack(state, size))
        luaD_throw(state, LUA_ERRMEM);
}

struct ProtectedCallContext
{
    Pfunc operation;
    void* operationContext;
};

void runprotectedoperation(lua_State* state, void* userdata)
{
    ProtectedCallContext* context = static_cast<ProtectedCallContext*>(userdata);
    if (!lua_checkstack(state, 1))
        luaD_throw(state, LUA_ERRMEM);
    context->operation(state, context->operationContext);
}

luau_host_status protect(lua_State* state, Pfunc operation, void* context, int consumed)
{
    if (!state || !operation || consumed < 0 || lua_gettop(state) < consumed)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    ProtectedCallContext call = {operation, context};
    int status = luaD_pcall(state, runprotectedoperation, &call, savestack(state, state->top - consumed), 0);
    return mapstatus(state, status);
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

void userdatadestructortrampoline(void* userdata)
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

int validatecallbackreturn(lua_State* state, int result, int entryTop)
{
    const int status = lua_status(state);
    if (result >= 0 && status == LUA_OK && result <= lua_gettop(state))
        return result;
    if (result == LUAU_HOST_CALLBACK_YIELD && (status == LUA_YIELD || status == LUA_BREAK))
        return result;
    if (result == LUAU_HOST_CALLBACK_ERROR && status == LUA_OK && lua_gettop(state) > entryTop)
        lua_error(state);
    return invalidcallbackreturn(state, "managed callback returned an invalid result count");
}

int managedcallbacktrampoline(lua_State* state)
{
    CallbackOwnerPayload* owner = currentcallbackpayload(state);
    if (!owner || !owner->function)
        return invalidcallbackreturn(state, "managed callback metadata is invalid");

    const int entryTop = lua_gettop(state);
    int result = 0;
    try
    {
        result = owner->function(host(state));
    }
    catch (...)
    {
        return invalidcallbackreturn(state, "managed callback crossed the no-unwind boundary");
    }
    return validatecallbackreturn(state, result, entryTop);
}

void callbackownerdestructor(void* userdata)
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

void opcheckstack(lua_State* state, void* userdata) { IndexContext* c = static_cast<IndexContext*>(userdata); c->result = lua_checkstack(state, c->index); }
void opnewthread(lua_State* state, void* userdata) { *static_cast<lua_State**>(userdata) = lua_newthread(state); }
// The host owns thread userdata. While coroutine.resume/wrap propagates a
// break, its Lua frame retains the child; this link needs no allocation or GC
// root of its own. Clear it before resetting or completing the continuation.
void recordcoroutinebreak(lua_State* parent, lua_Debug* debug)
{
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
void oprefrelease(lua_State* state, void* userdata) { lua_unref(state, static_cast<IntContext*>(userdata)->value); }
void optostring(lua_State* state, void* userdata) { StringContext* c = static_cast<StringContext*>(userdata); c->pointerResult = lua_tolstring(state, c->index, &c->length); }
void opdisplaystring(lua_State* state, void* userdata)
{
    requirestack(state, LUA_MINSTACK);
    StringContext* c = static_cast<StringContext*>(userdata);
    c->pointerResult = luaL_tolstring(state, c->index, &c->length);
}
void opload(lua_State* state, void* userdata) { LoadContext* c = static_cast<LoadContext*>(userdata); c->result = luau_load(state, c->chunkName, c->bytecode, c->size, c->environment); }
void opcollect(lua_State* state, void*) { (void)lua_gc(state, LUA_GCCOLLECT, 0); }
void opsettop(lua_State* state, void* userdata)
{
    const int index = static_cast<IndexContext*>(userdata)->index;
    const int currentTop = lua_gettop(state);

    // The approved Luau revision builds with LuauAutoStack disabled, so the
    // public lua_settop API does not grow the stack for a positive absolute
    // top. Reserve the exact delta inside this protected boundary before the
    // API initializes the new slots.
    if (index > currentTop && !lua_checkstack(state, index - currentTop))
        luaD_throw(state, LUA_ERRMEM);

    lua_settop(state, index);
}
void opinsert(lua_State* state, void* userdata) { lua_insert(state, static_cast<IndexContext*>(userdata)->index); }
void opremove(lua_State* state, void* userdata) { lua_remove(state, static_cast<IndexContext*>(userdata)->index); }
void opreplace(lua_State* state, void* userdata) { lua_replace(state, static_cast<IndexContext*>(userdata)->index); }
void opmove(lua_State* state, void* userdata)
{
    MoveContext* c = static_cast<MoveContext*>(userdata);

    // lua_xmove has the same LuauAutoStack-dependent behavior as lua_settop.
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
    // currently occupy. Upstream lua_pcall only performs this reservation when
    // LuauAutoStack is enabled, so make it explicit inside our boundary.
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

void opopenlibrary(lua_State* state, void* userdata)
{
    requirestack(state, LUA_MINSTACK);
    LibraryContext* context = static_cast<LibraryContext*>(userdata);
    switch (context->library)
    {
    case LUAU_HOST_LIBRARY_BASE: context->result = luaopen_base(state); break;
    case LUAU_HOST_LIBRARY_COROUTINE: context->result = luaopen_coroutine(state); break;
    case LUAU_HOST_LIBRARY_TABLE: context->result = luaopen_table(state); break;
    case LUAU_HOST_LIBRARY_OS: context->result = luaopen_os(state); break;
    case LUAU_HOST_LIBRARY_STRING: context->result = luaopen_string(state); break;
    case LUAU_HOST_LIBRARY_BIT32: context->result = luaopen_bit32(state); break;
    case LUAU_HOST_LIBRARY_BUFFER: context->result = luaopen_buffer(state); break;
    case LUAU_HOST_LIBRARY_UTF8: context->result = luaopen_utf8(state); break;
    case LUAU_HOST_LIBRARY_MATH: context->result = luaopen_math(state); break;
    case LUAU_HOST_LIBRARY_DEBUG: context->result = luaopen_debug(state); break;
    case LUAU_HOST_LIBRARY_VECTOR: context->result = luaopen_vector(state); break;
    case LUAU_HOST_LIBRARY_INTEGER: context->result = luaopen_integer(state); break;
    default: context->result = -1; break;
    }
}

void opsandboxroot(lua_State* state, void*) { requirestack(state, LUA_MINSTACK); luaL_sandbox(state); }
void opsandboxthread(lua_State* state, void*) { requirestack(state, LUA_MINSTACK); luaL_sandboxthread(state); }

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

void interrupttrampoline(lua_State* state, int gc)
{
    AllocatorContext* allocator = getallocator(state);
    if (!enterinterrupt(allocator))
        return;

    InterruptPoll poll = allocator->interruptPoll.load(std::memory_order_acquire);
    if (!poll)
    {
        leaveinterrupt(allocator);
        return;
    }

    const bool isGcNotification = gc >= 0;
    int action = 0;
    try
    {
        const luau_host_interrupt_kind kind = isGcNotification
            ? LUAU_HOST_INTERRUPT_GC
            : LUAU_HOST_INTERRUPT_EXECUTION;
        action = poll(host(state), kind);
    }
    catch (...)
    {
        // Reverse callbacks are required to contain exceptions, but the native
        // ABI remains a no-unwind boundary for a nonconforming caller.
    }

    // Yield/throw may bypass C++ destructors; decrement before changing VM flow.
    leaveinterrupt(allocator);

    // Collector notifications are observation-only. A hostile or buggy poll
    // cannot use its return value to yield, throw, or mark the VM canceled from
    // a collector phase.
    if (isGcNotification)
        return;

    if (action == 0)
        return;

    if (lua_isyieldable(state))
    {
        lua_break(state);
        return;
    }

    allocator->interrupted = true;
    luaD_throw(state, LUA_ERRMEM);
}
} // namespace

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
    luau_host_status status = protect(native(parent), opnewthread, &result, 0);
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
    if (index > LUAI_MAXCSTACK || (index < 0 && -int64_t(index) - 1 > top))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    IndexContext c = {index, 0};
    return protect(native(state), opsettop, &c, 0);
}
luau_host_status LUAU_HOST_CALL luau_host_stack_insert(luau_host_state* state, int32_t index)
{
    if (!state || !validordinaryindex(native(state), index))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    IndexContext c = {index, 0};
    return protect(native(state), opinsert, &c, 0);
}
luau_host_status LUAU_HOST_CALL luau_host_stack_remove(luau_host_state* state, int32_t index)
{
    if (!state || !validordinaryindex(native(state), index))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    IndexContext c = {index, 0};
    return protect(native(state), opremove, &c, 0);
}
luau_host_status LUAU_HOST_CALL luau_host_stack_replace(luau_host_state* state, int32_t index)
{
    if (!state || !validordinaryindex(native(state), index))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    IndexContext c = {index, 0};
    return protect(native(state), opreplace, &c, 1);
}

luau_host_status LUAU_HOST_CALL luau_host_stack_move(luau_host_state* from, luau_host_state* to, int32_t count)
{
    if (!from || !to || count < 0 ||
        lua_mainthread(native(from)) != lua_mainthread(native(to)) ||
        count > lua_gettop(native(from)))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    if (from != to && count > LUAI_MAXCSTACK - lua_gettop(native(to)))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    MoveContext context = {native(to), count};
    // lua_xmove grows the destination before consuming any source values. On
    // failure restore the complete source stack; the managed boundary then
    // consumes only the appended error object.
    return protect(native(from), opmove, &context, 0);
}

luau_host_status LUAU_HOST_CALL luau_host_stack_check(luau_host_state* state, int32_t size, int32_t* result)
{
    if (!state || size < 0)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    IndexContext context = {size, 0};
    luau_host_status status = protect(native(state), opcheckstack, &context, 0);
    if (status == LUAU_HOST_STATUS_OK && result)
        *result = context.result;
    return status;
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
    return protect(native(state), oppushvalue, &c, 0);
}
luau_host_status LUAU_HOST_CALL luau_host_push_nil(luau_host_state* state) { return protect(native(state), oppushnil, nullptr, 0); }
luau_host_status LUAU_HOST_CALL luau_host_push_boolean(luau_host_state* state, int32_t value) { IntContext c = {value, 0}; return protect(native(state), oppushboolean, &c, 0); }
luau_host_status LUAU_HOST_CALL luau_host_push_integer(luau_host_state* state, int64_t value) { Int64Context c = {value}; return protect(native(state), oppushinteger, &c, 0); }
luau_host_status LUAU_HOST_CALL luau_host_push_number(luau_host_state* state, double value) { NumberContext c = {value}; return protect(native(state), oppushnumber, &c, 0); }
luau_host_status LUAU_HOST_CALL luau_host_push_vector(luau_host_state* state, float x, float y, float z) { VectorContext c = {x, y, z}; return protect(native(state), oppushvector, &c, 0); }

luau_host_status LUAU_HOST_CALL luau_host_push_string(luau_host_state* state, const uint8_t* value, uint64_t length)
{
    if (!state || (!value && length != 0) || length > uint64_t(std::numeric_limits<size_t>::max()))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    static const uint8_t empty = 0;
    StringContext c = {reinterpret_cast<const char*>(value ? value : &empty), size_t(length), 0, 0, nullptr};
    return protect(native(state), oppushstring, &c, 0);
}

luau_host_status LUAU_HOST_CALL luau_host_push_light_userdata(luau_host_state* state, void* value, int32_t tag)
{
    if (!state || tag < 0 || tag >= LUA_LUTAG_LIMIT)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    LightUserdataContext c = {value, tag};
    return protect(native(state), oppushlightuserdata, &c, 0);
}

luau_host_status LUAU_HOST_CALL luau_host_push_thread(luau_host_state* state, int32_t* isMainThread)
{
    IntContext c = {};
    luau_host_status status = protect(native(state), oppushthread, &c, 0);
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

    const luau_host_status status = protect(native(state), oppushcallback, &c, upvalueCount);
    if (ownerTransferred)
        *ownerTransferred = c.ownerTransferred ? 1 : 0;
    if (errorObject && status != LUAU_HOST_STATUS_OK)
        *errorObject = 1;
    return status;
}

luau_host_status LUAU_HOST_CALL luau_host_userdata_create(luau_host_state* state, uint64_t size, int32_t tag, void** output)
{
    if (!state || size > uint64_t(std::numeric_limits<size_t>::max()) || tag < 0 || tag >= LUA_UTAG_LIMIT)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    if (output) *output = nullptr;
    AllocationContext c = {size_t(size), tag, nullptr, nullptr};
    luau_host_status status = protect(native(state), opnewuserdata, &c, 0);
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
    luau_host_status status = protect(native(state), opnewuserdatadtor, &c, 0);
    if (status == LUAU_HOST_STATUS_OK && output) *output = c.result;
    return status;
}

luau_host_status LUAU_HOST_CALL luau_host_buffer_create(luau_host_state* state, uint64_t size, void** output)
{
    if (!state || size > uint64_t(std::numeric_limits<size_t>::max())) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    if (output) *output = nullptr;
    AllocationContext c = {size_t(size), 0, nullptr, nullptr};
    luau_host_status status = protect(native(state), opnewbuffer, &c, 0);
    if (status == LUAU_HOST_STATUS_OK && output) *output = c.result;
    return status;
}

luau_host_status LUAU_HOST_CALL luau_host_table_get(luau_host_state* state, int32_t index, int32_t* type)
{
    if (!state || !validordinaryindex(native(state), index)) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    IndexContext c = {index, 0};
    luau_host_status s = protect(native(state), opgettable, &c, 1);
    if (s == LUAU_HOST_STATUS_OK && type) *type = c.result;
    return s;
}

luau_host_status LUAU_HOST_CALL luau_host_table_set(luau_host_state* state, int32_t index)
{
    if (!state || !validordinaryindex(native(state), index)) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    IndexContext c = {index, 0};
    return protect(native(state), opsettable, &c, 2);
}

luau_host_status LUAU_HOST_CALL luau_host_table_raw_get(luau_host_state* state, int32_t index, int32_t* type)
{
    if (!state || !validtableindex(native(state), index)) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    IndexContext c = {index, 0};
    luau_host_status s = protect(native(state), oprawget, &c, 1);
    if (s == LUAU_HOST_STATUS_OK && type) *type = c.result;
    return s;
}

luau_host_status LUAU_HOST_CALL luau_host_table_raw_set(luau_host_state* state, int32_t index)
{
    if (!state || !validtableindex(native(state), index)) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    IndexContext c = {index, 0};
    return protect(native(state), oprawset, &c, 2);
}

luau_host_status LUAU_HOST_CALL luau_host_table_next(luau_host_state* state, int32_t index, int32_t* hasNext)
{
    if (!state || !validtableindex(native(state), index)) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    IndexContext c = {index, 0};
    luau_host_status s = protect(native(state), opnext, &c, 1);
    if (s == LUAU_HOST_STATUS_OK && hasNext) *hasNext = c.result;
    return s;
}

luau_host_status LUAU_HOST_CALL luau_host_table_create(luau_host_state* state, int32_t arraySize, int32_t recordSize)
{
    if (!state || arraySize < 0 || recordSize < 0) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    TableContext c = {arraySize, recordSize};
    return protect(native(state), opcreatetable, &c, 0);
}

luau_host_status LUAU_HOST_CALL luau_host_table_clear(luau_host_state* state, int32_t index)
{
    if (!state || !validtableindex(native(state), index)) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    IndexContext c = {index, 0};
    return protect(native(state), opcleartable, &c, 0);
}

luau_host_status LUAU_HOST_CALL luau_host_table_clone(luau_host_state* state, int32_t index)
{
    if (!state || !validtableindex(native(state), index)) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    IndexContext c = {index, 0};
    return protect(native(state), opclonetable, &c, 0);
}

luau_host_status LUAU_HOST_CALL luau_host_metatable_get(luau_host_state* state, int32_t index, int32_t* hasMetatable)
{
    if (!state || !validordinaryindex(native(state), index)) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    IndexContext c = {index, 0};
    luau_host_status s = protect(native(state), opgetmetatable, &c, 0);
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
    luau_host_status s = protect(native(state), opsetmetatable, &c, 1);
    if (s == LUAU_HOST_STATUS_OK && result) *result = c.result;
    return s;
}

luau_host_status LUAU_HOST_CALL luau_host_table_set_readonly(luau_host_state* state, int32_t index, int32_t enabled)
{
    if (!state || !validtableindex(native(state), index)) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    IntContext c = {enabled, index};
    return protect(native(state), opsetreadonly, &c, 0);
}

luau_host_status LUAU_HOST_CALL luau_host_global_get(luau_host_state* state, const uint8_t* key, int32_t* type)
{
    if (!state || !key) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    StringContext c = {reinterpret_cast<const char*>(key), 0, LUA_GLOBALSINDEX, 0, nullptr};
    luau_host_status s = protect(native(state), opgetfield, &c, 0);
    if (s == LUAU_HOST_STATUS_OK && type) *type = c.result;
    return s;
}

luau_host_status LUAU_HOST_CALL luau_host_global_set(luau_host_state* state, const uint8_t* key)
{
    if (!state || !key) return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    StringContext c = {reinterpret_cast<const char*>(key), 0, LUA_GLOBALSINDEX, 0, nullptr};
    return protect(native(state), opsetfield, &c, 1);
}

luau_host_status LUAU_HOST_CALL luau_host_global_push(luau_host_state* state) { return protect(native(state), opglobalpush, nullptr, 0); }
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
    luau_host_status s = protect(native(state), oprefcreate, &c, 0);
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
        if (s == LUAU_HOST_STATUS_SYSTEM_OUT_OF_MEMORY)
        {
            const luau_host_status errorStatus = protect(native(state), oppushnil, nullptr, 0);
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
    luau_host_status s = protect(native(state), oprefpush, &c, 0);
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
    IntContext c = {registryReference, 0};
    const luau_host_status status = protect(native(state), oprefrelease, &c, 0);
    if (status == LUAU_HOST_STATUS_OK)
        erasereference(native(state), reference, registryReference);
    return status;
}

luau_host_status LUAU_HOST_CALL luau_host_to_string(luau_host_state* state, int32_t index, const uint8_t** output, uint64_t* length)
{
    if (output) *output = nullptr;
    if (length) *length = 0;
    if (!state || !validordinaryindex(native(state), index))
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    StringContext c = {nullptr, 0, index, 0, nullptr};
    luau_host_status s = protect(native(state), optostring, &c, 0);
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
    StringContext c = {nullptr, 0, index, 0, nullptr};
    luau_host_status s = protect(native(state), opdisplaystring, &c, 0);
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
    LoadContext c = {
        reinterpret_cast<const char*>(chunkName),
        reinterpret_cast<const char*>(bytecode),
        size_t(bytecodeSize),
        environment,
        LUA_OK};
    luau_host_status outer = protect(native(state), opload, &c, 0);
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
    if (argumentCount >= top || lua_status(target) != LUA_OK)
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

    PCallContext c = {argumentCount, resultCount, errorFunction, LUA_OK};
    luau_host_status outer = protect(target, oppcall, &c, argumentCount + 1);
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
    return protect(native(state), opcollect, nullptr, 0);
}

luau_host_status LUAU_HOST_CALL luau_host_open_library(luau_host_state* state, luau_host_library library, int32_t* resultCount)
{
    if (!state || library < LUAU_HOST_LIBRARY_BASE || library > LUAU_HOST_LIBRARY_INTEGER)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;
    LibraryContext c = {library, 0};
    luau_host_status s = protect(native(state), opopenlibrary, &c, 0);
    if (s == LUAU_HOST_STATUS_OK && resultCount) *resultCount = c.result;
    return s;
}

luau_host_status LUAU_HOST_CALL luau_host_sandbox_root(luau_host_state* state) { return protect(native(state), opsandboxroot, nullptr, 0); }
luau_host_status LUAU_HOST_CALL luau_host_sandbox_thread(luau_host_state* state) { return protect(native(state), opsandboxthread, nullptr, 0); }

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
} // extern "C"
