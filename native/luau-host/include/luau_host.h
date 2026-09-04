#ifndef LUAU_HOST_H
#define LUAU_HOST_H

#include <stdint.h>

#if defined(_WIN32)
#define LUAU_HOST_CALL __cdecl
#if defined(LUAU_HOST_BUILDING_LIBRARY)
#define LUAU_HOST_API __declspec(dllexport)
#else
#define LUAU_HOST_API __declspec(dllimport)
#endif
#elif defined(__GNUC__) || defined(__clang__)
#define LUAU_HOST_CALL
#define LUAU_HOST_API __attribute__((visibility("default")))
#else
#define LUAU_HOST_CALL
#define LUAU_HOST_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* The managed side treats this as an opaque pointer-sized handle. */
typedef struct luau_host_state luau_host_state;

/* Every non-null handle and pointer passed to this ABI must still be valid for
 * the documented call. Handles that share a root VM are externally serialized:
 * no host call may run concurrently with another call on that root, including
 * state_close or interrupt_uninstall versus execution/callback dispatch.
 * Synchronous stack access from a managed function is permitted;
 * reverse callbacks must not close a root, reset an active thread, or reenter a
 * lifecycle operation. Child/thread handles are borrowed: the corresponding
 * thread value must remain reachable from a VM stack, table, or registry
 * reference, and the root must remain open, for every use of the handle. */

/* INVALID_ARGUMENT reports a rejected precondition and is stack-neutral unless
 * a function explicitly says otherwise. A protected operation contains Luau
 * errors and native allocation failure; its comment defines the resulting
 * stack boundary and whether one topmost error object is available. No-fail
 * observers never allocate or invoke Luau code and return a neutral sentinel
 * for an invalid ordinary stack index. */

typedef int32_t luau_host_status;
enum
{
    LUAU_HOST_STATUS_OK = 0,
    LUAU_HOST_STATUS_LUA_ERROR = 1,
    LUAU_HOST_STATUS_MEMORY_QUOTA = 2,
    LUAU_HOST_STATUS_SYSTEM_OUT_OF_MEMORY = 3,
    LUAU_HOST_STATUS_CANCELED = 4,
    LUAU_HOST_STATUS_INVALID_ARGUMENT = 5,
    LUAU_HOST_STATUS_COMPILER_ERROR = 6,
    LUAU_HOST_STATUS_TERMINAL_RESET = 7,
    LUAU_HOST_STATUS_UNSUPPORTED = 8,
    LUAU_HOST_STATUS_YIELDED = 9,
    LUAU_HOST_STATUS_BREAK = 10,
    LUAU_HOST_STATUS_RESOURCE_EXHAUSTED = 11,
};

typedef int32_t luau_host_allocator_failure;
enum
{
    LUAU_HOST_ALLOCATOR_FAILURE_NONE = 0,
    LUAU_HOST_ALLOCATOR_FAILURE_QUOTA = 1,
    LUAU_HOST_ALLOCATOR_FAILURE_SYSTEM = 2,
};

typedef int32_t luau_host_interrupt_kind;
enum
{
    LUAU_HOST_INTERRUPT_EXECUTION = -1,
    LUAU_HOST_INTERRUPT_GC = 0,
};

typedef int32_t luau_host_library;
enum
{
    LUAU_HOST_LIBRARY_BASE = 0,
    LUAU_HOST_LIBRARY_COROUTINE = 1,
    LUAU_HOST_LIBRARY_TABLE = 2,
    LUAU_HOST_LIBRARY_OS = 3,
    LUAU_HOST_LIBRARY_STRING = 4,
    LUAU_HOST_LIBRARY_BIT32 = 5,
    LUAU_HOST_LIBRARY_BUFFER = 6,
    LUAU_HOST_LIBRARY_UTF8 = 7,
    LUAU_HOST_LIBRARY_MATH = 8,
    LUAU_HOST_LIBRARY_DEBUG = 9,
    LUAU_HOST_LIBRARY_VECTOR = 10,
    LUAU_HOST_LIBRARY_INTEGER = 11,
};

enum
{
    LUAU_HOST_ABI_MAGIC = 0x4841554cU,
    LUAU_HOST_ABI_MAJOR = 2,
    LUAU_HOST_ABI_MINOR = 0,
    LUAU_HOST_CALLBACK_TABLE_VERSION = 1,
    LUAU_HOST_COMPILE_OPTIONS_VERSION = 1,
    LUAU_HOST_STATE_OPTIONS_VERSION = 1,
    LUAU_HOST_MULTIPLE_RESULTS = -1,
    LUAU_HOST_CALLBACK_YIELD = -1,
    LUAU_HOST_CALLBACK_ERROR = -2,
};

enum
{
    LUAU_HOST_FEATURE_SELF_DESCRIPTION = 1U << 0,
    LUAU_HOST_FEATURE_PROTECTED_OPERATIONS = 1U << 1,
    LUAU_HOST_FEATURE_HOST_BUFFER = 1U << 2,
    LUAU_HOST_FEATURE_TRACKED_ALLOCATOR = 1U << 3,
    LUAU_HOST_FEATURE_MANAGED_CALLBACKS = 1U << 4,
    LUAU_HOST_FEATURE_INTERRUPT = 1U << 5,
    LUAU_HOST_FEATURE_TERMINAL_RESET = 1U << 6,
    LUAU_HOST_FEATURE_INTEGER_VALUES = 1U << 7,
    LUAU_HOST_FEATURE_SANDBOX = 1U << 8,
    LUAU_HOST_FEATURE_OPAQUE_REFERENCE_TOKENS = 1U << 9,
    LUAU_HOST_FEATURE_DIRECT_CALLBACK_IDENTITY = 1U << 10,
    LUAU_HOST_FEATURE_OBSERVATION_ONLY_GC_INTERRUPT = 1U << 11,
    LUAU_HOST_FEATURE_COROUTINE_SUSPENSION = 1U << 12,
};

enum
{
    LUAU_HOST_STATE_OPTION_TRACK_MEMORY = 1U << 0,
};

/* Managed functions return a count of topmost stack values
 * in [0, stack_get_top(state)], LUAU_HOST_CALLBACK_YIELD only after calling host
 * yield (or entering the native break state), or LUAU_HOST_CALLBACK_ERROR after
 * appending a callback failure value above the entry arguments. For callback
 * error, the host immediately raises the topmost value as the Luau error. The
 * host converts every other result to a contained generic Lua error before Luau
 * can consume it. Interrupt polls receive
 * EXECUTION or GC, never an upstream collector phase. Reverse callbacks and the
 * code/delegates backing them must remain callable for their documented lifetime
 * and must never unwind across this C ABI. An invalid result/unwind is converted
 * to LUA_ERROR with exactly one generic pinned error object. */
typedef int32_t(LUAU_HOST_CALL* luau_host_managed_function)(luau_host_state* state);
typedef int32_t(LUAU_HOST_CALL* luau_host_interrupt_poll)(luau_host_state* state, luau_host_interrupt_kind kind);
typedef void(LUAU_HOST_CALL* luau_host_userdata_destructor)(void* userdata);

#pragma pack(push, 8)

typedef struct luau_host_compile_options
{
    uint32_t struct_size;
    uint16_t version;
    uint16_t reserved0;
    int32_t optimization_level;
    int32_t debug_level;
    int32_t type_info_level;
    int32_t coverage_level;
    uint32_t flags;
    uint32_t reserved1;
} luau_host_compile_options;

typedef struct luau_host_callback_table
{
    uint32_t struct_size;
    uint16_t version;
    uint16_t reserved0;
    void* userdata;
    uint64_t registration_id;
    luau_host_managed_function managed_function;
    luau_host_interrupt_poll interrupt_poll;
    luau_host_userdata_destructor userdata_destructor;
} luau_host_callback_table;

typedef struct luau_host_state_options
{
    uint32_t struct_size;
    uint16_t version;
    uint16_t flags;
    uint64_t memory_limit_bytes;
} luau_host_state_options;

typedef struct luau_host_memory_info
{
    uint32_t struct_size;
    int32_t failure;
    uint64_t current_bytes;
    uint64_t peak_bytes;
    uint64_t limit_bytes;
    uint64_t last_attempted_bytes;
    uint8_t tracked;
    uint8_t reserved[7];
} luau_host_memory_info;

typedef struct luau_host_buffer
{
    uint8_t* data;
    uint64_t size;
} luau_host_buffer;

typedef struct luau_host_abi_info
{
    uint32_t struct_size;
    uint32_t magic;
    uint16_t abi_major;
    uint16_t abi_minor;
    uint32_t feature_flags;
    uint8_t pointer_size;
    uint8_t size_t_size;
    uint8_t little_endian;
    uint8_t reserved0;
    uint32_t compile_options_size;
    uint32_t callback_table_size;
    uint32_t state_options_size;
    uint32_t memory_info_size;
    uint32_t buffer_size;
    int32_t type_nil;
    int32_t type_boolean;
    int32_t type_lightuserdata;
    int32_t type_number;
    int32_t type_integer;
    int32_t type_vector;
    int32_t type_string;
    int32_t type_table;
    int32_t type_function;
    int32_t type_userdata;
    int32_t type_thread;
    int32_t type_buffer;
    int32_t type_class;
    int32_t type_object;
    uint64_t upstream_revision_hash;
    uint64_t host_build_fingerprint;
} luau_host_abi_info;

#pragma pack(pop)

/* Optional compile/state/callback records are caller-owned and borrowed only
 * for the call. When present, struct_size must be at least the corresponding
 * host record size, version must equal the constant above, and every reserved
 * field/unsupported flag must be zero. Future larger records are accepted only
 * when their known prefix obeys this version's rules. */

/* No-fail, allocation-free. Writes at most caller_size bytes. The fixed
 * struct_size/magic prefix is required; smaller buffers are rejected. */
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_get_abi_info(
    uint32_t caller_size,
    luau_host_abi_info* output);

/* Protected compiler operation; no VM stack is involved. source is a borrowed
 * byte span and may contain NUL. output must enter as {NULL,0}; otherwise it is
 * rejected unchanged. On OK, output owns one host buffer, including when Luau
 * encoded a source-language diagnostic in that buffer. On failure an initially
 * empty output remains empty. C++ exceptions and allocation failures are
 * translated to COMPILER_ERROR or SYSTEM_OUT_OF_MEMORY. */
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_compile(
    const uint8_t* source,
    uint64_t source_size,
    const luau_host_compile_options* options,
    luau_host_buffer* output);

/* No-fail ownership operation; no VM stack is involved. Frees a buffer returned
 * by this host library and clears it. Empty/already-cleared buffers are accepted;
 * any other pointer is outside the contract. */
LUAU_HOST_API void LUAU_HOST_CALL luau_host_buffer_free(luau_host_buffer* buffer);

/* Protected root creation. No stack exists on entry. output is required and is
 * set null before creation. On success it receives one owned root handle. When
 * failure_info is non-null, the caller must initialize struct_size to at least
 * the fixed 8-byte prefix; the host preserves that caller size and writes no
 * more than it. Failure returns null plus allocation diagnostics when available.
 * No reverse callback is retained or invoked after failed creation. */
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_state_create(
    const luau_host_state_options* options,
    luau_host_state** output,
    luau_host_memory_info* failure_info);

/* Root-only, stack-destroying close. No-fail for a valid root; uninstalls its
 * interrupt, invokes remaining userdata destructors, and invalidates every
 * stack value, borrowed pointer, registry reference, and child handle. Exactly
 * one call is permitted. Destructors must not reenter this root. */
LUAU_HOST_API void LUAU_HOST_CALL luau_host_state_close(luau_host_state* root);

/* No-fail observers/control for the root-owned allocator; stack-neutral.
 * memory_info is caller-sized: initialize struct_size to at least the fixed
 * 8-byte prefix. The host preserves it and writes at most that many bytes.
 * current_bytes and peak_bytes charge requested payload bytes still physically
 * retained by the host allocator. A failed shrinking realloc remains charged at
 * its prior size until a later successful realloc or free; platform allocator
 * metadata and capacity rounding are outside these counters.
 * reset_failure clears allocator failure telemetry and sticky cancellation. */
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_memory_get(
    luau_host_state* state,
    luau_host_memory_info* output);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_memory_reset_failure(luau_host_state* state);

/* No-fail lifecycle observers; stack-neutral. main_thread returns the borrowed
 * root handle. thread_status maps native status and sticky interruption to a
 * stable host status. */
LUAU_HOST_API luau_host_state* LUAU_HOST_CALL luau_host_main_thread(luau_host_state* state);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_thread_status(luau_host_state* state);

/* Protected child creation. output is required and initialized null. Success
 * pushes one thread value on parent (+1) and returns the same borrowed child
 * handle. Non-argument failure restores the entry top then appends one error
 * (+1); the child is not returned. Keep the pushed thread or another VM
 * reference reachable for as long as the child handle is used. */
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_thread_create(
    luau_host_state* parent,
    luau_host_state** output);

/* Terminal protected operation; no caller values are preserved. Success closes
 * active frames, clears the thread stack, and marks it reset. Any failure may
 * leave call-frame state unusable and returns TERMINAL_RESET with no safe error
 * object; managed code must poison the complete root. */
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_thread_reset(luau_host_state* state);

/* No-fail, allocation-free, stack-neutral observers. abs_index maps an ordinary
 * relative/absolute index, get_top returns the value count, type returns the
 * handshake type tag, type_name returns a process-lifetime static UTF-8 name,
 * object_length does not invoke metamethods and reports the
 * caller-requested payload size for destructor userdata. is_yieldable observes
 * only the current thread. Invalid indices return 0/null/a neutral tag. */
LUAU_HOST_API int32_t LUAU_HOST_CALL luau_host_stack_abs_index(luau_host_state* state, int32_t index);
LUAU_HOST_API int32_t LUAU_HOST_CALL luau_host_stack_get_top(luau_host_state* state);
LUAU_HOST_API int32_t LUAU_HOST_CALL luau_host_type(luau_host_state* state, int32_t index);
LUAU_HOST_API const uint8_t* LUAU_HOST_CALL luau_host_type_name(luau_host_state* state, int32_t type);
LUAU_HOST_API int32_t LUAU_HOST_CALL luau_host_object_length(luau_host_state* state, int32_t index);
LUAU_HOST_API int32_t LUAU_HOST_CALL luau_host_is_yieldable(luau_host_state* state);

/* Protected stack mutations; pseudo-indices are rejected. Success effects:
 * set_top establishes exactly index values; insert moves top to index (top
 * unchanged); remove deletes index (-1); replace moves top into index (-1);
 * move between distinct same-root threads pops count from from and appends them
 * to to (from==to is neutral); check is neutral and writes its boolean result.
 * On non-argument failure, set_top/insert/remove/check and move restore their
 * entry stacks then append one error to the executing/source thread; replace
 * consumes its top input and appends one error, so its entry count is retained. */
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_stack_set_top(luau_host_state* state, int32_t index);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_stack_insert(luau_host_state* state, int32_t index);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_stack_remove(luau_host_state* state, int32_t index);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_stack_replace(luau_host_state* state, int32_t index);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_stack_move(
    luau_host_state* from,
    luau_host_state* to,
    int32_t count);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_stack_check(
    luau_host_state* state,
    int32_t size,
    int32_t* result);

/* No-fail, stack-neutral typed observers; none invokes metamethods. number and
 * 32-bit conversions may accept numeric strings. Signed/unsigned 32-bit reads
 * truncate finite numeric values toward zero and set is_integer=0 for values
 * whose truncated result is out of range; unsigned also rejects negatives.
 * integer64 accepts only the lossless integer value kind. String view never
 * coerces. A vector pointer is valid only until the next call on that VM and
 * must be copied immediately. String/userdata/buffer pointers remain borrowed
 * only while the exact value is reachable and its root is open; any call that
 * can mutate the stack or collect may end that lifetime. to_pointer is an
 * identity token, not caller-owned storage. */
LUAU_HOST_API int32_t LUAU_HOST_CALL luau_host_to_boolean(luau_host_state* state, int32_t index);
LUAU_HOST_API double LUAU_HOST_CALL luau_host_to_number(luau_host_state* state, int32_t index, int32_t* is_number);
LUAU_HOST_API int32_t LUAU_HOST_CALL luau_host_to_integer32(luau_host_state* state, int32_t index, int32_t* is_integer);
LUAU_HOST_API uint32_t LUAU_HOST_CALL luau_host_to_unsigned32(luau_host_state* state, int32_t index, int32_t* is_integer);
LUAU_HOST_API int64_t LUAU_HOST_CALL luau_host_to_integer64(luau_host_state* state, int32_t index, int32_t* is_integer);
LUAU_HOST_API const float* LUAU_HOST_CALL luau_host_to_vector(luau_host_state* state, int32_t index);
LUAU_HOST_API const uint8_t* LUAU_HOST_CALL luau_host_to_string_view(
    luau_host_state* state,
    int32_t index,
    uint64_t* length);
LUAU_HOST_API void* LUAU_HOST_CALL luau_host_to_light_userdata(luau_host_state* state, int32_t index);
LUAU_HOST_API void* LUAU_HOST_CALL luau_host_to_userdata(luau_host_state* state, int32_t index);
LUAU_HOST_API luau_host_state* LUAU_HOST_CALL luau_host_to_thread(luau_host_state* state, int32_t index);
LUAU_HOST_API void* LUAU_HOST_CALL luau_host_to_buffer(
    luau_host_state* state,
    int32_t index,
    uint64_t* length);
LUAU_HOST_API const void* LUAU_HOST_CALL luau_host_to_pointer(luau_host_state* state, int32_t index);

/* No-fail, allocation-free, stack-neutral managed-callback observer. Valid
 * only synchronously inside a host-created managed function. Returns the
 * callback table's copied nonzero registration_id, or zero outside that frame. */
LUAU_HOST_API uint64_t LUAU_HOST_CALL luau_host_callback_registration_id(luau_host_state* state);

/* Protected pushes. Success appends exactly one value; push_value duplicates
 * an existing ordinary index. push_string borrows an explicit byte span and
 * permits embedded NUL. On non-argument failure the entry top is restored and
 * exactly one error is appended (+1). */
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_push_value(luau_host_state* state, int32_t index);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_push_nil(luau_host_state* state);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_push_boolean(luau_host_state* state, int32_t value);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_push_integer(luau_host_state* state, int64_t value);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_push_number(luau_host_state* state, double value);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_push_vector(
    luau_host_state* state,
    float x,
    float y,
    float z);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_push_string(
    luau_host_state* state,
    const uint8_t* value,
    uint64_t length);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_push_light_userdata(
    luau_host_state* state,
    void* value,
    int32_t tag);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_push_thread(
    luau_host_state* state,
    int32_t* is_main_thread);

/* Protected callback closure creation. Consumes upvalue_count values and
 * pushes one closure. debug_name is borrowed for this call and copied by the
 * host. registration_id must be nonzero and is copied into hidden metadata;
 * callers can recover it only from callback_registration_id while executing
 * that closure. All callbacks support at most 254 caller upvalues because the
 * host adds one hidden metadata/lifetime upvalue. managed_function code must remain
 * callable until closure collection or root close. When userdata and
 * userdata_destructor are both set, the host retains that owner for the same
 * lifetime and invokes the destructor exactly once with callback-table userdata
 * as its argument. Destructors are GC/close notifications: they may only
 * release or enqueue that opaque token and must not call this host, block,
 * allocate substantially, access Unity APIs, or unwind. owner_transferred is
 * set once native userdata owns it, even
 * if later closure allocation fails; otherwise the caller remains responsible.
 * The table and debug-name bytes are copied, not borrowed. Success consumes
 * upvalue_count values and pushes one closure (net 1-upvalue_count). On protected
 * failure those values are consumed and one error is appended at that boundary;
 * error_object is one. Precondition failure is neutral and leaves it zero. */
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_push_callback(
    luau_host_state* state,
    const luau_host_callback_table* callbacks,
    const uint8_t* debug_name,
    uint64_t debug_name_size,
    int32_t upvalue_count,
    int32_t* owner_transferred,
    int32_t* error_object);

/* Protected allocations. Success appends one userdata/buffer value (+1) and
 * returns its borrowed payload pointer. The destructor form copies only the
 * callback function pointer; when the value is collected/root closes, it calls
 * userdata_destructor with the newly allocated payload pointer (callback-table
 * userdata is ignored). Destructor code must remain callable for that lifetime
 * and may only release or enqueue the opaque token stored in that payload; it
 * must not call this host, block, allocate substantially, access Unity APIs,
 * unwind, or reenter lifecycle. Non-argument failure restores entry
 * top and appends exactly one error (+1); output is null. */
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_userdata_create(
    luau_host_state* state,
    uint64_t size,
    int32_t tag,
    void** output);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_userdata_create_with_destructor(
    luau_host_state* state,
    uint64_t size,
    const luau_host_callback_table* callbacks,
    void** output);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_buffer_create(
    luau_host_state* state,
    uint64_t size,
    void** output);

/* Protected table operations with exact success effects:
 * - get/raw_get consume top key and push value (top unchanged), writing its type;
 * - set/raw_set consume top key and value (-2);
 * - next consumes top key; when has_next=1 it pushes next key+value (net +1),
 *   otherwise it pushes nothing (net -1);
 * - create and clone append one table (+1); clear and set_readonly are neutral;
 * - metatable_get appends one table only when has_metatable=1 (otherwise neutral);
 * - metatable_set consumes one top nil/table (-1) and writes its integer result.
 * On protected failure each operation first consumes the inputs listed above,
 * then appends exactly one error. Precondition failure consumes nothing. */
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_table_get(
    luau_host_state* state,
    int32_t index,
    int32_t* type);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_table_set(luau_host_state* state, int32_t index);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_table_raw_get(
    luau_host_state* state,
    int32_t index,
    int32_t* type);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_table_raw_set(luau_host_state* state, int32_t index);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_table_next(
    luau_host_state* state,
    int32_t index,
    int32_t* has_next);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_table_create(
    luau_host_state* state,
    int32_t array_size,
    int32_t record_size);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_table_clear(luau_host_state* state, int32_t index);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_table_clone(luau_host_state* state, int32_t index);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_metatable_get(
    luau_host_state* state,
    int32_t index,
    int32_t* has_metatable);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_metatable_set(
    luau_host_state* state,
    int32_t index,
    int32_t* result);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_table_set_readonly(
    luau_host_state* state,
    int32_t index,
    int32_t enabled);

/* Explicit global operations avoid pseudo-indices. key is a borrowed,
 * NUL-terminated UTF-8 byte string valid for the call; embedded NUL terminates
 * the key. Success: get/global_push append one value (+1), set consumes one top
 * value (-1), and is_global is a no-fail stack-neutral observer. On protected
 * failure get/push restore entry top then append one error; set consumes its
 * input then appends one error (entry count retained). */
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_global_get(
    luau_host_state* state,
    const uint8_t* key,
    int32_t* type);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_global_set(
    luau_host_state* state,
    const uint8_t* key);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_global_push(luau_host_state* state);
LUAU_HOST_API int32_t LUAU_HOST_CALL luau_host_is_global(luau_host_state* state, int32_t index);

/* Root-registry references use opaque positive process-unique int32 tokens.
 * reference output is required. create is stack-neutral and retains a copy of
 * index. Tokens increase monotonically and are never reused in the process.
 * Once all positive int32 values have been issued, create returns
 * RESOURCE_EXHAUSTED and leaves reference at -1. push appends the retained value
 * (+1); release is stack-neutral and invalidates the token. References are
 * root-scoped, must be released at most once, and become invalid at root close.
 * Validation is an O(1) live-token lookup and never scans the VM registry free
 * list. Protected failure appends exactly one error without consuming caller
 * values; precondition/stale-token rejection is neutral. */
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_reference_create(
    luau_host_state* state,
    int32_t index,
    int32_t* reference);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_reference_push(
    luau_host_state* state,
    int32_t reference,
    int32_t* type);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_reference_release(
    luau_host_state* state,
    int32_t reference);

/* Protected string conversion. to_string may coerce the indexed value in place
 * but is top-neutral. to_display_string appends one display string (+1).
 * Both return a borrowed UTF-8 byte view to the resulting string; copy it before
 * the next mutating/collecting VM call. Non-argument failure restores entry top
 * then appends exactly one error (+1), with null/zero output. */
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_to_string(
    luau_host_state* state,
    int32_t index,
    const uint8_t** output,
    uint64_t* length);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_to_display_string(
    luau_host_state* state,
    int32_t index,
    const uint8_t** output,
    uint64_t* length);

/* Protected bytecode load. chunk_name is a borrowed, NUL-terminated UTF-8 byte
 * string valid for the call (embedded NUL terminates it); bytecode is a nonempty
 * explicit borrowed span. Empty input is rejected before entering the upstream
 * loader. Luau bytecode has no general verifier: callers must only pass output
 * produced in-process by luau_host_compile or authenticated against the exact
 * host build fingerprint. environment is zero or an ordinary table index. On
 * outer OK,
 * exactly one value is appended: a function with load_status=OK, or one load
 * error with load_status=LUA_ERROR. On outer non-argument failure, entry top is
 * restored and exactly one error is appended; load_status is not written. */
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_load(
    luau_host_state* state,
    const uint8_t* chunk_name,
    const uint8_t* bytecode,
    uint64_t bytecode_size,
    int32_t environment,
    luau_host_status* load_status);

/* Protected execution; raw lua_Status is never exposed. result_count accepts a
 * nonnegative exact count or LUAU_HOST_MULTIPLE_RESULTS. pcall consumes the
 * function and argument_count values, then appends exactly result_count results
 * (or all results), or exactly one top error on LUA_ERROR/allocation/cancel.
 * resume consumes the initial function/argument values or prior yielded frame;
 * OK leaves returned values, YIELDED leaves script yield values, BREAK
 * preserves the suspended frame (see resume_target), and LUA_ERROR
 * leaves exactly one top error. resume_error consumes one top error input and
 * continues the suspended handler with the same outcome contract. After any
 * resume error/cancel, reset before reuse. Invalid arguments are stack-neutral.
 * yield is no-fail and valid only synchronously in a yieldable managed callback:
 * it protects the top result_count values, sets yielded state, and returns -1;
 * invalid use is neutral and returns 0. */
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_pcall(
    luau_host_state* state,
    int32_t argument_count,
    int32_t result_count,
    int32_t error_function);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_resume(
    luau_host_state* state,
    luau_host_state* from,
    int32_t argument_count);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_resume_error(
    luau_host_state* state,
    luau_host_state* from);
LUAU_HOST_API int32_t LUAU_HOST_CALL luau_host_yield(luau_host_state* state, int32_t result_count);

/* Host-only suspension preserves callback arguments and propagates through
 * coroutine.resume/wrap without exposing a script yield. No-fail; valid only
 * in a yieldable managed callback; returns -1 on suspension, otherwise 0.
 * resume_target is a no-fail borrowed observer of the innermost suspended
 * thread (or state itself). Push callback results/error on that thread, then
 * resume/resume_error the original outer state. Those operations resume the
 * child and its waiting ancestors until completion, script yield, or another
 * host suspension. Resetting the outer state also resets waiting children. */
LUAU_HOST_API int32_t LUAU_HOST_CALL luau_host_suspend(luau_host_state* state);
LUAU_HOST_API luau_host_state* LUAU_HOST_CALL luau_host_resume_target(luau_host_state* state);

/* Protected full collection. This is the only exposed collector control.
 * Success is stack-neutral. Failure restores the entry top then appends one
 * error (+1). GC interrupt notifications are observation-only regardless of
 * their poll return value and can never yield, throw, or cancel collection. */
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_collect(luau_host_state* state);

/* Protected library and sandbox operations. open_library accepts only the
 * reviewed enum and appends exactly result_count values (the approved libraries
 * currently return one table). sandbox_root freezes libraries/globals and is top-neutral;
 * sandbox_thread replaces that thread's global table and is top-neutral.
 * Non-argument failure restores entry top then appends exactly one error (+1). */
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_open_library(
    luau_host_state* state,
    luau_host_library library,
    int32_t* result_count);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_sandbox_root(luau_host_state* state);
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_sandbox_thread(luau_host_state* state);

/* Interrupt callback lifecycle. Install is stack-neutral and copies the poll
 * pointer. Under the root-serialization contract above, uninstall prevents
 * future entries and drains callbacks that already entered the host gate before
 * returning without busy-waiting. The poll code/delegate must stay callable
 * from successful install through completed uninstall/root close. Polls are
 * stack-neutral unless they call documented callback-safe APIs. For EXECUTION,
 * zero continues and nonzero requests host suspension when possible or a sticky CANCELED
 * hard stop otherwise. GC notifications are observation-only: their return
 * value is ignored and they cannot alter VM control flow. Each root owns its poll pointer, so
 * independent roots may install different functions concurrently. Uninstall must
 * not be called by the poll itself. Poll callbacks must never unwind or reenter
 * lifecycle. */
LUAU_HOST_API luau_host_status LUAU_HOST_CALL luau_host_interrupt_install(
    luau_host_state* state,
    const luau_host_callback_table* callbacks);
LUAU_HOST_API void LUAU_HOST_CALL luau_host_interrupt_uninstall(luau_host_state* state);

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* LUAU_HOST_H */
