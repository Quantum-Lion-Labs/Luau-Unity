#include "luau_host.h"

#include <stddef.h>
#include <string.h>

#if !defined(LUAU_HOST_INVALID_ABI_CASE)
#error LUAU_HOST_INVALID_ABI_CASE must select one malformed ABI fixture
#endif

#define LUAU_HOST_INVALID_ABI_WRONG_MAGIC 1
#define LUAU_HOST_INVALID_ABI_WRONG_MAJOR 2
#define LUAU_HOST_INVALID_ABI_MISSING_REQUIRED_FEATURE 3
#define LUAU_HOST_INVALID_ABI_WRONG_POINTER_SIZE 4
#define LUAU_HOST_INVALID_ABI_WRONG_COMPILE_OPTIONS_SIZE 5
#define LUAU_HOST_INVALID_ABI_WRONG_CALLBACK_TABLE_SIZE 6
#define LUAU_HOST_INVALID_ABI_SHIFTED_TAGS 7
#define LUAU_HOST_INVALID_ABI_TRUNCATED_RECORD 8

_Static_assert(sizeof(luau_host_compile_options) == 32, "Unexpected compile-options fixture layout");
_Static_assert(sizeof(luau_host_callback_table) == (sizeof(void*) == 8 ? 48 : 40), "Unexpected callback-table fixture layout");
_Static_assert(sizeof(luau_host_state_options) == 16, "Unexpected state-options fixture layout");
_Static_assert(sizeof(luau_host_memory_info) == 48, "Unexpected memory-info fixture layout");
_Static_assert(sizeof(luau_host_buffer) == 16, "Unexpected buffer fixture layout");
_Static_assert(sizeof(luau_host_abi_info) == 112, "Unexpected ABI fixture layout");

static volatile uint32_t abi_query_count;
static volatile uint32_t compile_count;
static volatile uint32_t state_create_count;

static uint32_t fixture_feature_flags(void)
{
    return LUAU_HOST_FEATURE_SELF_DESCRIPTION |
        LUAU_HOST_FEATURE_PROTECTED_OPERATIONS |
        LUAU_HOST_FEATURE_HOST_BUFFER |
        LUAU_HOST_FEATURE_TRACKED_ALLOCATOR |
        LUAU_HOST_FEATURE_MANAGED_CALLBACKS |
        LUAU_HOST_FEATURE_INTERRUPT |
        LUAU_HOST_FEATURE_TERMINAL_RESET |
        LUAU_HOST_FEATURE_INTEGER_VALUES |
        LUAU_HOST_FEATURE_SANDBOX |
        LUAU_HOST_FEATURE_COROUTINE_SUSPENSION;
}

static luau_host_abi_info fixture_abi_info(void)
{
    const uint16_t endian_probe = 1;
    luau_host_abi_info value = {0};

    value.struct_size = (uint32_t)sizeof(value);
    value.magic = LUAU_HOST_ABI_MAGIC;
    value.abi_major = LUAU_HOST_ABI_MAJOR;
    value.abi_minor = LUAU_HOST_ABI_MINOR;
    value.feature_flags = fixture_feature_flags();
    value.pointer_size = (uint8_t)sizeof(void*);
    value.size_t_size = (uint8_t)sizeof(size_t);
    value.little_endian = *(const uint8_t*)&endian_probe;
    value.compile_options_size = (uint32_t)sizeof(luau_host_compile_options);
    value.callback_table_size = (uint32_t)sizeof(luau_host_callback_table);
    value.state_options_size = (uint32_t)sizeof(luau_host_state_options);
    value.memory_info_size = (uint32_t)sizeof(luau_host_memory_info);
    value.buffer_size = (uint32_t)sizeof(luau_host_buffer);
    value.type_nil = 0;
    value.type_boolean = 1;
    value.type_lightuserdata = 2;
    value.type_number = 3;
    value.type_integer = 4;
    value.type_vector = 5;
    value.type_string = 6;
    value.type_table = 7;
    value.type_function = 8;
    value.type_userdata = 9;
    value.type_thread = 10;
    value.type_buffer = 11;
    value.type_class = 12;
    value.type_object = 13;
    value.upstream_revision_hash = UINT64_C(0xc45f010aabf167ac);
    value.host_build_fingerprint = UINT64_C(0xb400000000000001) + LUAU_HOST_INVALID_ABI_CASE;

#if LUAU_HOST_INVALID_ABI_CASE == LUAU_HOST_INVALID_ABI_WRONG_MAGIC
    value.magic ^= 1U;
#elif LUAU_HOST_INVALID_ABI_CASE == LUAU_HOST_INVALID_ABI_WRONG_MAJOR
    value.abi_major = (uint16_t)(value.abi_major + 1U);
#elif LUAU_HOST_INVALID_ABI_CASE == LUAU_HOST_INVALID_ABI_MISSING_REQUIRED_FEATURE
    value.feature_flags &= ~LUAU_HOST_FEATURE_HOST_BUFFER;
#elif LUAU_HOST_INVALID_ABI_CASE == LUAU_HOST_INVALID_ABI_WRONG_POINTER_SIZE
    value.pointer_size = (uint8_t)(sizeof(void*) == 8 ? 4 : 8);
#elif LUAU_HOST_INVALID_ABI_CASE == LUAU_HOST_INVALID_ABI_WRONG_COMPILE_OPTIONS_SIZE
    value.compile_options_size += 1U;
#elif LUAU_HOST_INVALID_ABI_CASE == LUAU_HOST_INVALID_ABI_WRONG_CALLBACK_TABLE_SIZE
    value.callback_table_size += 1U;
#elif LUAU_HOST_INVALID_ABI_CASE == LUAU_HOST_INVALID_ABI_SHIFTED_TAGS
    value.type_nil += 1;
    value.type_boolean += 1;
    value.type_lightuserdata += 1;
    value.type_number += 1;
    value.type_integer += 1;
    value.type_vector += 1;
    value.type_string += 1;
    value.type_table += 1;
    value.type_function += 1;
    value.type_userdata += 1;
    value.type_thread += 1;
    value.type_buffer += 1;
    value.type_class += 1;
    value.type_object += 1;
#elif LUAU_HOST_INVALID_ABI_CASE == LUAU_HOST_INVALID_ABI_TRUNCATED_RECORD
    value.struct_size = (uint32_t)offsetof(luau_host_abi_info, host_build_fingerprint);
#else
#error Unknown LUAU_HOST_INVALID_ABI_CASE
#endif

    return value;
}

luau_host_status LUAU_HOST_CALL luau_host_get_abi_info(
    uint32_t caller_size,
    luau_host_abi_info* output)
{
    luau_host_abi_info value;
    uint32_t bytes_to_write;
    const uint32_t fixed_prefix_size = (uint32_t)offsetof(luau_host_abi_info, compile_options_size);

    abi_query_count++;
    if (output == NULL)
        return LUAU_HOST_STATUS_INVALID_ARGUMENT;

    value = fixture_abi_info();
    bytes_to_write = caller_size < value.struct_size ? caller_size : value.struct_size;
    if (bytes_to_write != 0)
        memcpy(output, &value, bytes_to_write);

    return caller_size < fixed_prefix_size ? LUAU_HOST_STATUS_INVALID_ARGUMENT : LUAU_HOST_STATUS_OK;
}

luau_host_status LUAU_HOST_CALL luau_host_compile(
    const uint8_t* source,
    uint64_t source_size,
    const luau_host_compile_options* options,
    luau_host_buffer* output)
{
    (void)source;
    (void)source_size;
    (void)options;

    compile_count++;
    if (output != NULL)
    {
        output->data = NULL;
        output->size = 0;
    }
    return LUAU_HOST_STATUS_INVALID_ARGUMENT;
}

void LUAU_HOST_CALL luau_host_buffer_free(luau_host_buffer* buffer)
{
    if (buffer != NULL)
    {
        buffer->data = NULL;
        buffer->size = 0;
    }
}

luau_host_status LUAU_HOST_CALL luau_host_state_create(
    const luau_host_state_options* options,
    luau_host_state** output,
    luau_host_memory_info* failure_info)
{
    (void)options;

    state_create_count++;
    if (output != NULL)
        *output = NULL;
    if (failure_info != NULL)
    {
        memset(failure_info, 0, sizeof(*failure_info));
        failure_info->struct_size = (uint32_t)sizeof(*failure_info);
    }
    return LUAU_HOST_STATUS_INVALID_ARGUMENT;
}

LUAU_HOST_API uint32_t LUAU_HOST_CALL luau_host_fixture_get_abi_query_count(void)
{
    return abi_query_count;
}

LUAU_HOST_API uint32_t LUAU_HOST_CALL luau_host_fixture_get_compile_count(void)
{
    return compile_count;
}

LUAU_HOST_API uint32_t LUAU_HOST_CALL luau_host_fixture_get_state_create_count(void)
{
    return state_create_count;
}
