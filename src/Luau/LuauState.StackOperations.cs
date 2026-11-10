using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Luau.Internal.Interop;
using static Luau.Internal.Interop.NativeMethods;

namespace Luau;

public unsafe partial class LuauState
{
    internal void Call(
        int numOfargs,
        int numOfresults,
        LuauExecutionOptions? executionOptions = null)
    {
        ThrowIfDisposed();
        if (numOfargs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numOfargs));
        }

        if (numOfresults < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(numOfresults));
        }

        using var operation = BeginOperation(
            chunkName: null,
            options: executionOptions,
            cancellationToken: default,
            isAsync: false);
        var baseTop = GetTop() - numOfargs - 1;
        if (baseTop < 0)
        {
            throw new InvalidOperationException("The Luau stack does not contain a function and the requested arguments.");
        }

        var actualResults = ScriptRunner.RunToStack(operation, numOfargs);

        if (numOfresults < 0 || actualResults == numOfresults)
        {
            return;
        }

        if (actualResults > numOfresults)
        {
            SetTop(baseTop + numOfresults);
            return;
        }

        for (var i = actualResults; i < numOfresults; i++)
        {
            PushNil();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetTop()
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        return luau_host_stack_get_top(l);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetTop(int top)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        var currentTop = luau_host_stack_get_top(l);
        if (top < -currentTop - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(top), "The relative stack top is below the base of the Luau stack.");
        }

        LuauNativeProtection.Prepare(context);
        var status = luau_host_stack_set_top(l, top);
        LuauNativeProtection.ThrowIfFailed(this, l, status, "set the stack top");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetAbsIndex(int index)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        return luau_host_stack_abs_index(l, index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Insert(LuauValue value, int index)
    {
        using var access = EnterNativeAccess();
        Push(value);
        LuauNativeProtection.Prepare(context);
        var status = luau_host_stack_insert(l, index);
        LuauNativeProtection.ThrowIfFailed(this, l, status, "insert a stack value");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Insert(int index)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        LuauNativeProtection.Prepare(context);
        var status = luau_host_stack_insert(l, index);
        LuauNativeProtection.ThrowIfFailed(this, l, status, "insert a stack value");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Replace(LuauValue value, int index)
    {
        using var access = EnterNativeAccess();
        Push(value);
        LuauNativeProtection.Prepare(context);
        var status = luau_host_stack_replace(l, index);
        LuauNativeProtection.ThrowIfFailed(this, l, status, "replace a stack value");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Replace(int index)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        LuauNativeProtection.Prepare(context);
        var status = luau_host_stack_replace(l, index);
        LuauNativeProtection.ThrowIfFailed(this, l, status, "replace a stack value");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Remove(int index)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        LuauNativeProtection.Prepare(context);
        var status = luau_host_stack_remove(l, index);
        LuauNativeProtection.ThrowIfFailed(this, l, status, "remove a stack value");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CheckStack(int size)
    {
        ThrowIfDisposed();
        if (size < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        using var access = EnterNativeAccess();
        LuauNativeProtection.ReserveSlots(this, l, size, $"grow the Luau stack by {size} slots");
    }

    internal unsafe LuauType GetLuauType(int index)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();

        var type = luau_host_type(l, index);

        return MapNativeType((LuauHostType)type);
    }

    internal static LuauType MapNativeType(LuauHostType luauType)
    {
        switch (luauType)
        {
            case LuauHostType.Nil: return LuauType.Nil;
            case LuauHostType.Boolean: return LuauType.Boolean;
            case LuauHostType.LightUserdata: return LuauType.LightUserData;
            case LuauHostType.Number: return LuauType.Number;
            case LuauHostType.Integer: return LuauType.Integer;
            case LuauHostType.Vector: return LuauType.Vector;
            case LuauHostType.String: return LuauType.String;
            case LuauHostType.Table: return LuauType.Table;
            case LuauHostType.Function: return LuauType.Function;
            case LuauHostType.Userdata: return LuauType.UserData;
            case LuauHostType.Thread: return LuauType.Thread;
            case LuauHostType.Buffer: return LuauType.Buffer;
            case LuauHostType.Class:
            case LuauHostType.Object:
                ThrowHelper.ThrowUnsupportedValue(luauType);
                break;
        }

        ThrowHelper.ThrowTypeIsNotSupported(luauType);
        return default; // dummy
    }

    internal unsafe LuauValue ToValue(int index)
    {
        return ToValue(index, borrowedFrame: null);
    }

    internal unsafe LuauValue ToValue(int index, LuauCallFrame? borrowedFrame)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();

        return ToValueCore(index, (LuauHostType)luau_host_type(l, index), borrowedFrame);
    }

    /// <summary>
    /// Internal fixture for exercising native tag policy without enabling
    /// upstream class/object libraries in production states.
    /// </summary>
    internal LuauValue ToValueForNativeTypeFixture(int index, int nativeType)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();

        return ToValueCore(index, (LuauHostType)nativeType, borrowedFrame: null);
    }

    LuauValue ToValueCore(
        int index,
        LuauHostType luauType,
        LuauCallFrame? borrowedFrame)
    {
        switch (luauType)
        {
            case LuauHostType.Nil:
                return LuauValue.Nil;
            case LuauHostType.Boolean:
                return LuauValue.FromBoolean(luau_host_to_boolean(l, index) == 1);
            case LuauHostType.LightUserdata:
#pragma warning disable CS0618 // Transitional internal light-userdata protocol.
                return LuauValue.FromLightUserData((IntPtr)luau_host_to_light_userdata(l, index));
#pragma warning restore CS0618
            case LuauHostType.Number:
                return LuauValue.FromNumber(luau_host_to_number(l, index, null));
            case LuauHostType.Integer:
                var isInteger = 0;
                var integer = luau_host_to_integer64(l, index, &isInteger);
                if (isInteger != 1)
                {
                    ThrowHelper.ThrowInvalidOperationException($"The value at {index} is not a 64-bit integer");
                }
                return LuauValue.FromInteger(integer);
            case LuauHostType.Vector:
                var vecPtr = luau_host_to_vector(l, index);
                return LuauValue.FromVector(new(vecPtr[0], vecPtr[1], vecPtr[2]));
            case LuauHostType.String:
                return LuauValue.FromString(ReadStackString(index, "read a Luau string"));
            case LuauHostType.Table:
                var table = new LuauTable(
                    this,
                    LuauReferenceHelper.CreateReference(this, index, "retain a Luau table"),
                    borrowedFrame);
                return LuauValue.FromTable(table);
            case LuauHostType.Function:
                var function = new LuauScriptFunction(
                    this,
                    LuauReferenceHelper.CreateReference(this, index, "retain a Luau function"),
                    borrowedFrame);
                return LuauValue.FromFunction(function);
            case LuauHostType.Userdata:
                if (TryReadObjectToken(index, out var objectToken))
                {
                    return LuauValue.FromObjectHandle(
                        RetainObjectHandleFromStack(index, objectToken, borrowedFrame));
                }
                var userData = new LuauUserData(
                    this,
                    LuauReferenceHelper.CreateReference(this, index, "retain Luau userdata"),
                    borrowedFrame);
                return LuauValue.FromUserData(userData);
            case LuauHostType.Thread:
                var thread = context.GetOrCreateThread(
                    l,
                    luau_host_to_thread(l, index),
                    index,
                    out var createdThread);
                return LuauValue.FromStackThread(thread, createdThread);
            case LuauHostType.Buffer:
                var buffer = new LuauBuffer(
                    this,
                    LuauReferenceHelper.CreateReference(this, index, "retain a Luau buffer"),
                    borrowedFrame);
                return LuauValue.FromBuffer(buffer);
            case LuauHostType.Class:
            case LuauHostType.Object:
                ThrowHelper.ThrowUnsupportedValue(luauType);
                break;
        }

        ThrowHelper.ThrowTypeIsNotSupported(luauType);
        return default; // dummy
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool ToBoolean(int index)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        return luau_host_to_boolean(l, index) == 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal double ToNumber(int index)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();

        if ((LuauHostType)luau_host_type(l, index) == LuauHostType.Integer)
        {
            int isInteger;
            var integer = luau_host_to_integer64(l, index, &isInteger);
            if (isInteger != 1)
            {
                ThrowHelper.ThrowInvalidOperationException($"The value at {index} is not a number or integer");
            }

            if (!MathEx.TryConvertToDoubleExact(integer, out var exact))
            {
                ThrowHelper.ThrowInvalidOperationException(
                    $"The integer at {index} cannot be represented exactly as a double. Use {nameof(ToNumberLossy)} for an explicit lossy conversion.");
            }

            return exact;
        }

        int isNum;
        var result = luau_host_to_number(l, index, &isNum);

        if (isNum != 1)
        {
            ThrowHelper.ThrowInvalidOperationException($"The value at {index} is not a number");
        }

        return result;
    }

    /// <summary>
    /// Converts a native number or 64-bit integer to <see cref="double"/>,
    /// explicitly allowing integer precision loss.
    /// </summary>
    internal double ToNumberLossy(int index)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();

        if ((LuauHostType)luau_host_type(l, index) == LuauHostType.Integer)
        {
            int isInteger;
            var integer = luau_host_to_integer64(l, index, &isInteger);
            if (isInteger != 1)
            {
                ThrowHelper.ThrowInvalidOperationException($"The value at {index} is not a number or integer");
            }

            return integer;
        }

        int isNum;
        var result = luau_host_to_number(l, index, &isNum);
        if (isNum != 1)
        {
            ThrowHelper.ThrowInvalidOperationException($"The value at {index} is not a number or integer");
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int ToInteger(int index)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();

        if ((LuauHostType)luau_host_type(l, index) == LuauHostType.Integer)
        {
            int isInteger;
            var integer = luau_host_to_integer64(l, index, &isInteger);
            if (isInteger != 1 || integer < int.MinValue || integer > int.MaxValue)
            {
                ThrowHelper.ThrowInvalidOperationException($"The value at {index} is outside the Int32 range");
            }

            return (int)integer;
        }

        int isNum;
        var result = luau_host_to_integer32(l, index, &isNum);

        if (isNum != 1)
        {
            ThrowHelper.ThrowInvalidOperationException($"The value at {index} is not a integer");
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long ToInteger64(int index)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();

        if ((LuauHostType)luau_host_type(l, index) == LuauHostType.Integer)
        {
            int isInteger;
            var integer = luau_host_to_integer64(l, index, &isInteger);
            if (isInteger != 1)
            {
                ThrowHelper.ThrowInvalidOperationException($"The value at {index} is not a 64-bit integer");
            }

            return integer;
        }

        int isNum;
        var number = luau_host_to_number(l, index, &isNum);
        if (isNum != 1 || !MathEx.IsInt64(number))
        {
            ThrowHelper.ThrowInvalidOperationException($"The value at {index} is not an exact 64-bit integer");
        }

        return (long)number;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint ToUnsigned(int index)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();

        if ((LuauHostType)luau_host_type(l, index) == LuauHostType.Integer)
        {
            int isInteger;
            var integer = luau_host_to_integer64(l, index, &isInteger);
            if (isInteger != 1 || integer < uint.MinValue || integer > uint.MaxValue)
            {
                ThrowHelper.ThrowInvalidOperationException($"The value at {index} is outside the UInt32 range");
            }

            return (uint)integer;
        }

        int isNum;
        var result = luau_host_to_unsigned32(l, index, &isNum);

        if (isNum != 1)
        {
            ThrowHelper.ThrowInvalidOperationException($"The value at {index} is not an unsigned integer");
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Vector3 ToVector(int index)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();

        var ptr = luau_host_to_vector(l, index);
        return new(ptr[0], ptr[1], ptr[2]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal string ToString(int index)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        return ReadStackString(index, "convert a Luau value to a string");
    }

    string ReadStackString(int index, string operation)
    {
        byte* text = null;
        ulong length = 0;

        LuauNativeProtection.Prepare(context);
        var status = luau_host_to_string(l, index, &text, &length);
        LuauNativeProtection.ThrowIfFailed(this, l, status, operation);

        if (text == null)
        {
            throw new InvalidOperationException($"The value at {index} cannot be converted to a string.");
        }

        ReserveDecodedString(length);

        return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(text, checked((int)length)));
    }

    void ReserveDecodedString(ulong length)
    {
        if (Options.MaxDecodedStringBytes is { } stringLimit && length > (ulong)stringLimit)
        {
            throw new LuauDecodedResultLimitException(
                context.GetActiveOperation()?.ChunkName,
                LuauDecodedResultLimitKind.StringBytes,
                LuauVmContext.ToDiagnosticByteCount(length),
                stringLimit);
        }
        if (length > int.MaxValue)
        {
            throw new LuauDecodedResultLimitException(
                context.GetActiveOperation()?.ChunkName,
                LuauDecodedResultLimitKind.StringBytes,
                LuauVmContext.ToDiagnosticByteCount(length),
                int.MaxValue);
        }

        context.GetActiveOperation()?.ReserveDecodedBytes(length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal LuauTable ToTable(int index)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        return new LuauTable(
            this,
            LuauReferenceHelper.CreateReference(this, index, "retain a Luau table"));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal LuauFunction ToFunction(int index)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        return new LuauScriptFunction(
            this,
            LuauReferenceHelper.CreateReference(this, index, "retain a Luau function"));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal LuauState ToThread(int index)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        return context.GetOrCreateThread(l, luau_host_to_thread(l, index), index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal LuauBuffer ToBuffer(int index)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        return new LuauBuffer(
            this,
            LuauReferenceHelper.CreateReference(this, index, "retain a Luau buffer"));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal LuauUserData ToUserData(int index)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        return new LuauUserData(
            this,
            LuauReferenceHelper.CreateReference(this, index, "retain Luau userdata"));
    }

    internal void Push(LuauValue value)
    {
        switch (value.Type)
        {
            case LuauType.Nil:
                PushNil();
                break;
            case LuauType.Boolean:
                PushBoolean(value.Read<bool>());
                break;
            case LuauType.LightUserData:
                PushLightUserData((void*)value.LightUserDataPointer);
                break;
            case LuauType.Number:
                PushNumber(value.Read<double>());
                break;
            case LuauType.Integer:
                PushInteger(value.Read<long>());
                break;
            case LuauType.Vector:
                PushVector(value.Read<Vector3>());
                break;
            case LuauType.String:
                PushString(value.Read<string>());
                break;
            case LuauType.Table:
                PushTable(value.Read<LuauTable>());
                break;
            case LuauType.Function:
                PushFunction(value.Read<LuauFunction>());
                break;
            case LuauType.UserData:
                if (value.TryRead<LuauObjectHandle>(out var objectHandle))
                {
                    PushObjectHandle(objectHandle);
                }
                else
                {
                    PushUserData(value.Read<LuauUserData>());
                }
                break;
            case LuauType.Thread:
                PushThread(value.Read<LuauState>());
                break;
            case LuauType.Buffer:
                PushBuffer(value.Read<LuauBuffer>());
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PushNil()
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        LuauNativeProtection.Prepare(context);
        var status = luau_host_push_nil(l);
        LuauNativeProtection.ThrowIfFailed(this, l, status, "push nil onto the Luau stack");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PushBoolean(bool value)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        LuauNativeProtection.Prepare(context);
        var status = luau_host_push_boolean(l, value ? 1 : 0);
        LuauNativeProtection.ThrowIfFailed(this, l, status, "push a boolean onto the Luau stack");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PushLightUserData(void* value)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        LuauNativeProtection.Prepare(context);
        var status = luau_host_push_light_userdata(l, value, 0);
        LuauNativeProtection.ThrowIfFailed(this, l, status, "push light userdata onto the Luau stack");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PushInteger(long value)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        LuauNativeProtection.Prepare(context);
        var status = luau_host_push_integer(l, value);
        LuauNativeProtection.ThrowIfFailed(this, l, status, "push a 64-bit integer onto the Luau stack");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PushNumber(double value)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        LuauNativeProtection.Prepare(context);
        var status = luau_host_push_number(l, value);
        LuauNativeProtection.ThrowIfFailed(this, l, status, "push a number onto the Luau stack");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PushVector(Vector3 value)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        LuauNativeProtection.Prepare(context);
        var status = luau_host_push_vector(l, value.X, value.Y, value.Z);
        LuauNativeProtection.ThrowIfFailed(this, l, status, "push a vector onto the Luau stack");
    }

    internal void PushString(string value)
    {
        ThrowIfDisposed();
        if (value == null) throw new ArgumentNullException(nameof(value));

        using var utf8 = new Utf8BufferScope(value.AsSpan());
        using var access = EnterNativeAccess();
        fixed (byte* stringPtr = utf8.Bytes)
        {
            LuauNativeProtection.Prepare(context);
            var status = luau_host_push_string(l, stringPtr, (ulong)utf8.Length);
            LuauNativeProtection.ThrowIfFailed(this, l, status, "push a string onto the Luau stack");
        }
    }

    internal void PushString(ReadOnlySpan<byte> utf8Value)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        fixed (byte* stringPtr = utf8Value)
        {
            LuauNativeProtection.Prepare(context);
            var status = luau_host_push_string(l, stringPtr, (ulong)utf8Value.Length);
            LuauNativeProtection.ThrowIfFailed(this, l, status, "push a string onto the Luau stack");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PushTable(LuauTable value)
    {
        PushReference(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PushThread(LuauState value)
    {
        PushReference(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PushBuffer(LuauBuffer value)
    {
        PushReference(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PushUserData(LuauUserData value)
    {
        PushReference(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PushObjectHandle(LuauObjectHandle value)
    {
        PushReference(value);
    }

    internal void PushFunction(LuauFunction value)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        using var functionAccess = value.AcquireForPush();
        if (value is LuauScriptFunction scriptFunc)
        {
            PushReference(scriptFunc);
        }
        else
        {
            if (value is not ILuauManagedCallbackFunction managedCallback)
            {
                throw new InvalidOperationException("Only runtime-managed Luau callbacks can be pushed.");
            }

            if (!ReferenceEquals(functionAccess.State.Context, context))
            {
                throw new InvalidOperationException("Cannot push a managed callback from another Luau VM.");
            }

            var registrationId = managedCallback.RegistrationId;
            if (registrationId == 0)
            {
                ThrowHelper.ThrowObjectDisposedException(nameof(LuauFunction));
            }

            var nativeReleaseToken = context.AddManagedCallbackNativeReference(registrationId);
            var callbackTable = new LuauHostCallbackTable
            {
                struct_size = (uint)sizeof(LuauHostCallbackTable),
                version = 1,
                userdata = nativeReleaseToken,
                registration_id = checked((ulong)registrationId),
                managed_function = Marshal.GetFunctionPointerForDelegate(managedCallback.Callback),
                userdata_destructor = Marshal.GetFunctionPointerForDelegate(
                    LuauManagedCallbackLifetime.Destructor),
            };
            var ownerTransferred = 0;
            var errorObject = 0;
            LuauNativeProtection.Prepare(context);
            var closureStatus = luau_host_push_callback(
                    l,
                    &callbackTable,
                    null,
                    0,
                    0,
                    &ownerTransferred,
                    &errorObject);
            if (closureStatus != LuauHostStatus.Ok && ownerTransferred == 0)
            {
                LuauManagedCallbackLifetime.QueueRelease(nativeReleaseToken);
            }
            LuauNativeProtection.ThrowIfFailed(
                this,
                l,
                closureStatus,
                "create a managed callback closure");
            GC.KeepAlive(managedCallback.Callback);
            GC.KeepAlive(LuauManagedCallbackLifetime.Destructor);
        }
    }

    void PushReference<T>(T value)
        where T : ILuauReference
    {
        ThrowIfDisposed();
        using var referenceAccess = value.AcquireReference();
        if (!ReferenceEquals(referenceAccess.State.Context, context))
        {
            throw new InvalidOperationException("Cannot push a reference from another Luau VM.");
        }

        LuauReferenceHelper.PushReference(
            this,
            referenceAccess.Reference,
            "push a managed Luau reference");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal LuauValue Pop()
    {
        using var access = EnterNativeAccess();
        var value = ToValue(-1);
        try
        {
            SetTop(-2);
            return value;
        }
        catch
        {
            value.DisposeUnpublishedReference();
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Pop(int n)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        if (n < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(n));
        }
        SetTop(-n - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Pop(int n, Span<LuauValue> destination)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();

        if (destination.Length < n)
        {
            ThrowHelper.ThrowArgumentException(nameof(destination), "Destination is too short");
        }

        var top = luau_host_stack_get_top(l);
        var converted = 0;
        try
        {
            for (int i = 0; i < n; i++)
            {
                destination[i] = ToValue(top - i);
                converted++;
            }

            SetTop(-n - 1);
        }
        catch
        {
            for (var index = converted - 1; index >= 0; index--)
            {
                destination[index].DisposeUnpublishedReference();
                destination[index] = default;
            }
            throw;
        }
    }

    internal void XMove(LuauState destination, int n)
    {
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        destination.ThrowIfDisposed();
        if (!ReferenceEquals(context, destination.context))
        {
            throw new InvalidOperationException("Cannot move values between independent Luau VMs.");
        }

        var from = l;
        var to = destination.l;
        if (n < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(n));
        }
        if (luau_host_stack_get_top(from) < n)
        {
            throw new InvalidOperationException("The source Luau stack does not contain enough values to move.");
        }

        LuauNativeProtection.Prepare(context);
        var status = luau_host_stack_move(from, to, n);
        LuauNativeProtection.ThrowIfFailed(this, from, status, "move stack values");
    }
}
