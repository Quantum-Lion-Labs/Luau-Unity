
using System;
using System.Runtime.InteropServices;

namespace Luau.Internal.Interop
{
    internal static unsafe class NativeMethods
    {
        internal const string __DllName =
#if (UNITY_IOS || UNITY_WEBGL) && !UNITY_EDITOR
            "__Internal";
#else
            "luau_host";
#endif

        const CallingConvention Call = CallingConvention.Cdecl;

        [DllImport(__DllName, EntryPoint = "luau_host_get_abi_info", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_get_abi_info(uint callerSize, LuauHostAbiInfo* output);

        [DllImport(__DllName, EntryPoint = "luau_host_compile", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_compile(byte* source, ulong sourceSize, LuauHostCompileOptions* options, LuauHostBuffer* output);

        [DllImport(__DllName, EntryPoint = "luau_host_buffer_free", CallingConvention = Call, ExactSpelling = true)]
        internal static extern void luau_host_buffer_free(LuauHostBuffer* buffer);

        [DllImport(__DllName, EntryPoint = "luau_host_state_create", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_state_create(LuauHostStateOptions* options, LuauHostState** output, LuauHostMemoryInfo* failureInfo);

        [DllImport(__DllName, EntryPoint = "luau_host_state_close", CallingConvention = Call, ExactSpelling = true)]
        internal static extern void luau_host_state_close(LuauHostState* root);

        [DllImport(__DllName, EntryPoint = "luau_host_memory_get", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_memory_get(LuauHostState* state, LuauHostMemoryInfo* output);

        [DllImport(__DllName, EntryPoint = "luau_host_memory_reset_failure", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_memory_reset_failure(LuauHostState* state);

        [DllImport(__DllName, EntryPoint = "luau_host_main_thread", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostState* luau_host_main_thread(LuauHostState* state);

        [DllImport(__DllName, EntryPoint = "luau_host_thread_status", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_thread_status(LuauHostState* state);

        [DllImport(__DllName, EntryPoint = "luau_host_thread_create", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_thread_create(LuauHostState* parent, LuauHostState** output);

        [DllImport(__DllName, EntryPoint = "luau_host_thread_reset", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_thread_reset(LuauHostState* state);

        [DllImport(__DllName, EntryPoint = "luau_host_stack_abs_index", CallingConvention = Call, ExactSpelling = true)]
        internal static extern int luau_host_stack_abs_index(LuauHostState* state, int index);

        [DllImport(__DllName, EntryPoint = "luau_host_stack_get_top", CallingConvention = Call, ExactSpelling = true)]
        internal static extern int luau_host_stack_get_top(LuauHostState* state);

        [DllImport(__DllName, EntryPoint = "luau_host_type", CallingConvention = Call, ExactSpelling = true)]
        internal static extern int luau_host_type(LuauHostState* state, int index);

        [DllImport(__DllName, EntryPoint = "luau_host_type_name", CallingConvention = Call, ExactSpelling = true)]
        internal static extern byte* luau_host_type_name(LuauHostState* state, int type);

        [DllImport(__DllName, EntryPoint = "luau_host_object_length", CallingConvention = Call, ExactSpelling = true)]
        internal static extern int luau_host_object_length(LuauHostState* state, int index);

        [DllImport(__DllName, EntryPoint = "luau_host_is_yieldable", CallingConvention = Call, ExactSpelling = true)]
        internal static extern int luau_host_is_yieldable(LuauHostState* state);

        [DllImport(__DllName, EntryPoint = "luau_host_stack_set_top", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_stack_set_top(LuauHostState* state, int index);

        [DllImport(__DllName, EntryPoint = "luau_host_stack_insert", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_stack_insert(LuauHostState* state, int index);

        [DllImport(__DllName, EntryPoint = "luau_host_stack_remove", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_stack_remove(LuauHostState* state, int index);

        [DllImport(__DllName, EntryPoint = "luau_host_stack_replace", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_stack_replace(LuauHostState* state, int index);

        [DllImport(__DllName, EntryPoint = "luau_host_stack_move", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_stack_move(LuauHostState* from, LuauHostState* to, int count);

        [DllImport(__DllName, EntryPoint = "luau_host_stack_check", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_stack_check(LuauHostState* state, int size, int* result);

        [DllImport(__DllName, EntryPoint = "luau_host_to_boolean", CallingConvention = Call, ExactSpelling = true)]
        internal static extern int luau_host_to_boolean(LuauHostState* state, int index);

        [DllImport(__DllName, EntryPoint = "luau_host_to_number", CallingConvention = Call, ExactSpelling = true)]
        internal static extern double luau_host_to_number(LuauHostState* state, int index, int* isNumber);

        [DllImport(__DllName, EntryPoint = "luau_host_to_integer32", CallingConvention = Call, ExactSpelling = true)]
        internal static extern int luau_host_to_integer32(LuauHostState* state, int index, int* isInteger);

        [DllImport(__DllName, EntryPoint = "luau_host_to_unsigned32", CallingConvention = Call, ExactSpelling = true)]
        internal static extern uint luau_host_to_unsigned32(LuauHostState* state, int index, int* isInteger);

        [DllImport(__DllName, EntryPoint = "luau_host_to_integer64", CallingConvention = Call, ExactSpelling = true)]
        internal static extern long luau_host_to_integer64(LuauHostState* state, int index, int* isInteger);

        [DllImport(__DllName, EntryPoint = "luau_host_to_vector", CallingConvention = Call, ExactSpelling = true)]
        internal static extern float* luau_host_to_vector(LuauHostState* state, int index);

        [DllImport(__DllName, EntryPoint = "luau_host_to_string_view", CallingConvention = Call, ExactSpelling = true)]
        internal static extern byte* luau_host_to_string_view(LuauHostState* state, int index, ulong* length);

        [DllImport(__DllName, EntryPoint = "luau_host_to_light_userdata", CallingConvention = Call, ExactSpelling = true)]
        internal static extern void* luau_host_to_light_userdata(LuauHostState* state, int index);

        [DllImport(__DllName, EntryPoint = "luau_host_to_userdata", CallingConvention = Call, ExactSpelling = true)]
        internal static extern void* luau_host_to_userdata(LuauHostState* state, int index);

        [DllImport(__DllName, EntryPoint = "luau_host_to_thread", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostState* luau_host_to_thread(LuauHostState* state, int index);

        [DllImport(__DllName, EntryPoint = "luau_host_to_buffer", CallingConvention = Call, ExactSpelling = true)]
        internal static extern void* luau_host_to_buffer(LuauHostState* state, int index, ulong* length);

        [DllImport(__DllName, EntryPoint = "luau_host_to_pointer", CallingConvention = Call, ExactSpelling = true)]
        internal static extern void* luau_host_to_pointer(LuauHostState* state, int index);

        [DllImport(__DllName, EntryPoint = "luau_host_callback_registration_id", CallingConvention = Call, ExactSpelling = true)]
        internal static extern ulong luau_host_callback_registration_id(LuauHostState* state);

        [DllImport(__DllName, EntryPoint = "luau_host_push_value", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_push_value(LuauHostState* state, int index);

        [DllImport(__DllName, EntryPoint = "luau_host_push_nil", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_push_nil(LuauHostState* state);

        [DllImport(__DllName, EntryPoint = "luau_host_push_boolean", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_push_boolean(LuauHostState* state, int value);

        [DllImport(__DllName, EntryPoint = "luau_host_push_integer", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_push_integer(LuauHostState* state, long value);

        [DllImport(__DllName, EntryPoint = "luau_host_push_number", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_push_number(LuauHostState* state, double value);

        [DllImport(__DllName, EntryPoint = "luau_host_push_vector", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_push_vector(LuauHostState* state, float x, float y, float z);

        [DllImport(__DllName, EntryPoint = "luau_host_push_string", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_push_string(LuauHostState* state, byte* value, ulong length);

        [DllImport(__DllName, EntryPoint = "luau_host_push_light_userdata", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_push_light_userdata(LuauHostState* state, void* value, int tag);

        [DllImport(__DllName, EntryPoint = "luau_host_push_thread", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_push_thread(LuauHostState* state, int* isMainThread);

        [DllImport(__DllName, EntryPoint = "luau_host_push_callback", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_push_callback(LuauHostState* state, LuauHostCallbackTable* callbacks, byte* debugName, ulong debugNameSize, int upvalueCount, int* ownerTransferred, int* errorObject);

        [DllImport(__DllName, EntryPoint = "luau_host_userdata_create", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_userdata_create(LuauHostState* state, ulong size, int tag, void** output);

        [DllImport(__DllName, EntryPoint = "luau_host_userdata_create_with_destructor", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_userdata_create_with_destructor(LuauHostState* state, ulong size, LuauHostCallbackTable* callbacks, void** output);

        [DllImport(__DllName, EntryPoint = "luau_host_buffer_create", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_buffer_create(LuauHostState* state, ulong size, void** output);

        [DllImport(__DllName, EntryPoint = "luau_host_table_get", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_table_get(LuauHostState* state, int index, int* type);

        [DllImport(__DllName, EntryPoint = "luau_host_table_set", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_table_set(LuauHostState* state, int index);

        [DllImport(__DllName, EntryPoint = "luau_host_table_raw_get", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_table_raw_get(LuauHostState* state, int index, int* type);

        [DllImport(__DllName, EntryPoint = "luau_host_table_raw_set", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_table_raw_set(LuauHostState* state, int index);

        [DllImport(__DllName, EntryPoint = "luau_host_table_next", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_table_next(LuauHostState* state, int index, int* hasNext);

        [DllImport(__DllName, EntryPoint = "luau_host_table_create", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_table_create(LuauHostState* state, int arraySize, int recordSize);

        [DllImport(__DllName, EntryPoint = "luau_host_table_clear", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_table_clear(LuauHostState* state, int index);

        [DllImport(__DllName, EntryPoint = "luau_host_table_clone", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_table_clone(LuauHostState* state, int index);

        [DllImport(__DllName, EntryPoint = "luau_host_metatable_get", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_metatable_get(LuauHostState* state, int index, int* hasMetatable);

        [DllImport(__DllName, EntryPoint = "luau_host_metatable_set", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_metatable_set(LuauHostState* state, int index, int* result);

        [DllImport(__DllName, EntryPoint = "luau_host_table_set_readonly", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_table_set_readonly(LuauHostState* state, int index, int enabled);

        [DllImport(__DllName, EntryPoint = "luau_host_global_get", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_global_get(LuauHostState* state, byte* key, int* type);

        [DllImport(__DllName, EntryPoint = "luau_host_global_set", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_global_set(LuauHostState* state, byte* key);

        [DllImport(__DllName, EntryPoint = "luau_host_global_push", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_global_push(LuauHostState* state);

        [DllImport(__DllName, EntryPoint = "luau_host_is_global", CallingConvention = Call, ExactSpelling = true)]
        internal static extern int luau_host_is_global(LuauHostState* state, int index);

        [DllImport(__DllName, EntryPoint = "luau_host_reference_create", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_reference_create(LuauHostState* state, int index, int* reference);

        [DllImport(__DllName, EntryPoint = "luau_host_reference_push", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_reference_push(LuauHostState* state, int reference, int* type);

        [DllImport(__DllName, EntryPoint = "luau_host_reference_release", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_reference_release(LuauHostState* state, int reference);

        [DllImport(__DllName, EntryPoint = "luau_host_to_string", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_to_string(LuauHostState* state, int index, byte** output, ulong* length);

        [DllImport(__DllName, EntryPoint = "luau_host_to_display_string", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_to_display_string(LuauHostState* state, int index, byte** output, ulong* length);

        [DllImport(__DllName, EntryPoint = "luau_host_load", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_load(LuauHostState* state, byte* chunkName, byte* bytecode, ulong bytecodeSize, int environment, LuauHostStatus* loadStatus);

        [DllImport(__DllName, EntryPoint = "luau_host_pcall", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_pcall(LuauHostState* state, int argumentCount, int resultCount, int errorFunction);

        [DllImport(__DllName, EntryPoint = "luau_host_resume", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_resume(LuauHostState* state, LuauHostState* from, int argumentCount);

        [DllImport(__DllName, EntryPoint = "luau_host_resume_error", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_resume_error(LuauHostState* state, LuauHostState* from);

        [DllImport(__DllName, EntryPoint = "luau_host_yield", CallingConvention = Call, ExactSpelling = true)]
        internal static extern int luau_host_yield(LuauHostState* state, int resultCount);

        [DllImport(__DllName, EntryPoint = "luau_host_suspend", CallingConvention = Call, ExactSpelling = true)]
        internal static extern int luau_host_suspend(LuauHostState* state);

        [DllImport(__DllName, EntryPoint = "luau_host_resume_target", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostState* luau_host_resume_target(LuauHostState* state);

        [DllImport(__DllName, EntryPoint = "luau_host_collect", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_collect(LuauHostState* state);

        [DllImport(__DllName, EntryPoint = "luau_host_open_library", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_open_library(LuauHostState* state, LuauHostLibrary library, int* resultCount);

        [DllImport(__DllName, EntryPoint = "luau_host_sandbox_root", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_sandbox_root(LuauHostState* state);

        [DllImport(__DllName, EntryPoint = "luau_host_sandbox_thread", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_sandbox_thread(LuauHostState* state);

        [DllImport(__DllName, EntryPoint = "luau_host_interrupt_install", CallingConvention = Call, ExactSpelling = true)]
        internal static extern LuauHostStatus luau_host_interrupt_install(LuauHostState* state, LuauHostCallbackTable* callbacks);

        [DllImport(__DllName, EntryPoint = "luau_host_interrupt_uninstall", CallingConvention = Call, ExactSpelling = true)]
        internal static extern void luau_host_interrupt_uninstall(LuauHostState* state);
    }

}
