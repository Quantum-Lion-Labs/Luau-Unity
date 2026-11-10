using Luau.Internal.Interop;
using static Luau.Internal.Interop.NativeMethods;

namespace Luau;

/// <summary>
/// Stack work an operation does on entry, while it holds its own native
/// access: pushing invocation and resume arguments.
/// </summary>
internal static class LuauOperationStack
{
    // Entry pushes run right after BeginOperation, which reset the allocator
    // diagnostics, and run no Luau code, so they need no further reset; a
    // failed push throws before the next one. A failure is translated exactly
    // as the general push path translates it.
    internal static unsafe void PushArgument(LuauState state, LuauHostState* pointer, LuauValue value)
    {
        LuauHostStatus status;
        string operation;
        switch (value.Type)
        {
            case LuauType.Nil:
                status = luau_host_push_nil(pointer);
                operation = "push nil onto the Luau stack";
                break;
            case LuauType.Boolean:
                status = luau_host_push_boolean(pointer, value.BooleanPayload ? 1 : 0);
                operation = "push a boolean onto the Luau stack";
                break;
            case LuauType.Number:
                status = luau_host_push_number(pointer, value.NumberPayload);
                operation = "push a number onto the Luau stack";
                break;
            case LuauType.Integer:
                status = luau_host_push_integer(pointer, value.IntegerPayload);
                operation = "push a 64-bit integer onto the Luau stack";
                break;
            case LuauType.Vector:
                var vector = value.VectorPayload;
                status = luau_host_push_vector(pointer, vector.X, vector.Y, vector.Z);
                operation = "push a vector onto the Luau stack";
                break;
            default:
                // Strings and references keep the general path: it encodes
                // strings and validates wrapper ownership.
                state.Push(value);
                return;
        }

        if (status != LuauHostStatus.Ok)
        {
            LuauNativeProtection.ThrowIfFailed(state, pointer, status, operation);
        }
    }

    // The general push path checks for a hard stop after every push. Checking
    // once after the last push throws the same exception before any Luau code
    // runs, with the stack restored by the caller.
    internal static void ThrowIfStoppedBeforeEntry(ScriptOperation operation)
    {
        if (operation.GetHardStopException() is { } hardStop)
        {
            throw hardStop;
        }
    }
}
