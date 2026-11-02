using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Luau;

/// <summary>
/// Represents one managed Luau value. Primitive values are self-contained;
/// reference values borrow the lifetime of their contained wrapper and do not
/// retain it when this struct is copied or converted.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct LuauValue : IEquatable<LuauValue>
{
    [StructLayout(LayoutKind.Explicit)]
    struct ValueUnion
    {
        [FieldOffset(0)] public bool BooleanValue;
        [FieldOffset(0)] public double NumberValue;
        [FieldOffset(0)] public long IntegerValue;
        [FieldOffset(0)] public IntPtr PointerValue;
        [FieldOffset(0)] public Vector3 VectorValue;
    }

    /// <summary>Represents the singleton Luau <c>nil</c> value.</summary>
    public static readonly LuauValue Nil = default;

    /// <summary>
    /// Converts a supported managed value without requiring a Luau state.
    /// Reference values retain their existing ownership and VM association.
    /// </summary>
    /// <remarks>
    /// Integral types up to 32 bits become Luau numbers, so scripts can compare
    /// them with numeric literals and use them as array indices. <see cref="long"/>
    /// and <see cref="ulong"/> become distinct Luau integers to stay lossless.
    /// </remarks>
    public static LuauValue CreateFrom<T>(T? value)
    {
        if (value == null) return Nil;

        if (value is LuauValue luauValue) return luauValue;
        if (value is bool boolean) return FromBoolean(boolean);
        if (value is string text) return FromString(text);
        if (value is Vector3 vector) return FromVector(vector);
        if (value is LuauFunction function) return FromFunction(function);
        if (value is LuauTable table) return FromTable(table);
        if (value is LuauBuffer buffer) return FromBuffer(buffer);
        if (value is LuauState state) return FromThread(state);
        if (value is LuauUserData userData) return FromUserData(userData);
        if (value is LuauObjectHandle objectHandle) return FromObjectHandle(objectHandle);

        // 32-bit and smaller integral types are exactly representable as a
        // double, so they become ordinary Luau numbers that compare, index,
        // and do arithmetic with numeric literals. Only 64-bit types use the
        // distinct Luau integer type, where a double would lose precision.
        if (value is byte unsignedByte) return FromNumber(unsignedByte);
        if (value is sbyte signedByte) return FromNumber(signedByte);
        if (value is short signedShort) return FromNumber(signedShort);
        if (value is ushort unsignedShort) return FromNumber(unsignedShort);
        if (value is int signedInteger) return FromNumber(signedInteger);
        if (value is uint unsignedInteger) return FromNumber(unsignedInteger);
        if (value is long signedLong) return FromInteger(signedLong);
        if (value is ulong unsignedLong) return FromInteger(checked((long)unsignedLong));
        if (value is float single) return FromNumber(single);
        if (value is double number) return FromNumber(number);

        throw new ArgumentException(
            $"Cannot convert {typeof(T).Name} to {nameof(LuauValue)}.",
            nameof(value));
    }

    /// <summary>Creates a Luau floating-point number.</summary>
    public static LuauValue FromNumber(double value)
    {
        return new(LuauType.Number, new() { NumberValue = value }, null);
    }

    /// <summary>Creates a distinct signed 64-bit Luau integer.</summary>
    public static LuauValue FromInteger(long value)
    {
        return new(LuauType.Integer, new() { IntegerValue = value }, null);
    }

    /// <summary>Creates a Luau Boolean value.</summary>
    public static LuauValue FromBoolean(bool value)
    {
        return new(LuauType.Boolean, new() { BooleanValue = value }, null);
    }

    /// <summary>Creates a Luau string value from a non-null managed string.</summary>
    public static LuauValue FromString(string value)
    {
        return new(LuauType.String, default, value);
    }

    internal static LuauValue FromLightUserData(IntPtr value)
    {
        return new(LuauType.LightUserData, new() { PointerValue = value }, null);
    }

    /// <summary>Wraps existing userdata without retaining or transferring its ownership.</summary>
    public static LuauValue FromUserData(LuauUserData value)
    {
        return new(LuauType.UserData, default, value);
    }

    /// <summary>Creates an opaque userdata value from a managed object capability.</summary>
    public static LuauValue FromObjectHandle(LuauObjectHandle value)
    {
        return new(
            LuauType.UserData,
            default,
            value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>Creates a Luau three-component vector value.</summary>
    public static LuauValue FromVector(Vector3 value)
    {
        return new(LuauType.Vector, new() { VectorValue = value }, null);
    }

    /// <summary>Wraps an existing table without retaining or transferring its ownership.</summary>
    public static LuauValue FromTable(LuauTable value)
    {
        return new(LuauType.Table, default, value);
    }

    /// <summary>Wraps an existing function without retaining or transferring its ownership.</summary>
    public static LuauValue FromFunction(LuauFunction value)
    {
        return new(LuauType.Function, default, value);
    }

    /// <summary>Wraps an existing root or coroutine without retaining or transferring its ownership.</summary>
    public static LuauValue FromThread(LuauState value)
    {
        return new(LuauType.Thread, default, value);
    }

    /// <summary>Wraps an existing buffer without retaining or transferring its ownership.</summary>
    public static LuauValue FromBuffer(LuauBuffer value)
    {
        return new(LuauType.Buffer, default, value);
    }

    readonly LuauType type;
    readonly ValueUnion value;
    readonly object? reference;
    readonly bool disposeThreadIfUnpublished;

    internal bool ContainsManagedReferenceWrapper => reference is LuauTable or
        LuauFunction or
        LuauBuffer or
        LuauState or
        LuauUserData or
        LuauObjectHandle;

    /// <summary>Gets the Luau VM type of this value.</summary>
    public LuauType Type => type;

    internal IntPtr LightUserDataPointer => type == LuauType.LightUserData
        ? value.PointerValue
        : throw new InvalidOperationException($"Cannot read {type} as light userdata.");

    LuauValue(
        LuauType type,
        ValueUnion value,
        object? reference,
        bool disposeThreadIfUnpublished = false)
    {
        this.type = type;
        this.value = value;
        this.reference = reference;
        this.disposeThreadIfUnpublished = disposeThreadIfUnpublished;
    }

    internal static LuauValue FromStackThread(LuauState value, bool wasCreated) =>
        new(LuauType.Thread, default, value, disposeThreadIfUnpublished: wasCreated);

    /// <summary>
    /// Returns the managed or Luau textual representation. Reference-valued
    /// instances require their contained wrapper and root state to remain live.
    /// </summary>
    public unsafe override string ToString()
    {
        return type switch
        {
            LuauType.Nil => "nil",
            LuauType.Boolean => value.BooleanValue ? "true" : "false",
            LuauType.LightUserData => $"lightuserdata: 0x{value.PointerValue:X}",
            LuauType.Number => value.NumberValue.ToString(),
            LuauType.Integer => value.IntegerValue.ToString(),
            LuauType.Vector => VectorToString(value.VectorValue),
            LuauType.String => ((string)reference!).ToString(),
            LuauType.Table => ((LuauTable)reference!).ToString(),
            LuauType.Function => ((LuauFunction)reference!).ToString()!,
            LuauType.UserData => reference switch
            {
                LuauObjectHandle handle => handle.ToString(),
                LuauUserData userData => userData.ToString(),
                _ => "userdata",
            },
            LuauType.Thread => ((LuauState)reference!).ToString()!,
            LuauType.Buffer => ((LuauBuffer)reference!).ToString()!,
            _ => "",
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static string VectorToString(Vector3 vector)
    {
        return $"{vector.X}, {vector.Y}, {vector.Z}";
    }

    /// <summary>Gets whether this value is Luau <c>nil</c>.</summary>
    public bool IsNil => Type == LuauType.Nil;

    /// <summary>
    /// Creates an independently disposable owner when this value contains a
    /// table, function, buffer, userdata, or object-handle reference. Primitive
    /// values are returned unchanged. A cached thread wrapper cannot be
    /// independently retained and is rejected.
    /// </summary>
    public LuauValue Retain()
    {
        return Type switch
        {
            LuauType.Table => FromTable(Read<LuauTable>().Retain()),
            LuauType.Function => FromFunction(Read<LuauFunction>().Retain()),
            LuauType.Buffer => FromBuffer(Read<LuauBuffer>().Retain()),
            LuauType.UserData when reference is LuauObjectHandle handle =>
                FromObjectHandle(handle.Retain()),
            LuauType.UserData => FromUserData(Read<LuauUserData>().Retain()),
            LuauType.Thread => throw new InvalidOperationException(
                "A cached Luau thread wrapper cannot be independently retained."),
            _ => this,
        };
    }

    internal void DisposeOwnedReference()
    {
        switch (reference)
        {
            case LuauTable table:
                table.Dispose();
                break;
            case LuauFunction function:
                function.Dispose();
                break;
            case LuauBuffer buffer:
                buffer.Dispose();
                break;
            case LuauUserData userData:
                userData.Dispose();
                break;
            case LuauObjectHandle objectHandle:
                objectHandle.Dispose();
                break;
        }
    }

    internal void DisposeUnpublishedReference()
    {
        DisposeOwnedReference();
        if (disposeThreadIfUnpublished && reference is LuauState { IsMainThread: false } thread)
        {
            thread.Dispose();
        }
    }

    /// <summary>Reads this value as the requested managed representation.</summary>
    /// <remarks>
    /// Reading a Luau script closure as <see cref="LuauFunction"/> produces a
    /// host-invokable function. Managed callback capabilities created by
    /// <see cref="LuauState.CreateFunction(Action{LuauCallContext})"/> remain
    /// callable only from Luau.
    /// </remarks>
    public T Read<T>()
    {
        if (TryRead<T>(out var result)) return result;
        throw new InvalidOperationException($"Cannot convert {Type} to {typeof(T).Name}");
    }

    /// <summary>
    /// Reads a numeric value as <see cref="double"/>, explicitly allowing a
    /// 64-bit integer to lose precision.
    /// </summary>
    public double ReadDoubleLossy()
    {
        return Type switch
        {
            LuauType.Number => value.NumberValue,
            LuauType.Integer => value.IntegerValue,
            _ => throw new InvalidOperationException($"Cannot convert {Type} to Double"),
        };
    }

    /// <summary>
    /// Attempts a checked conversion to <typeparamref name="T"/> without
    /// retaining reference wrappers or changing their ownership.
    /// </summary>
    /// <typeparam name="T">The requested supported managed representation.</typeparam>
    /// <param name="result">Receives the converted value when conversion succeeds.</param>
    /// <returns><see langword="true"/> when the conversion is valid and lossless; otherwise <see langword="false"/>.</returns>
    public bool TryRead<T>(out T result)
    {
        if (typeof(T) == typeof(LuauValue))
        {
            return Assign(this, out result);
        }

        switch (Type)
        {
            case LuauType.Nil:
                if (default(T) is null)
                {
                    result = default!;
                    return true;
                }
                break;
            case LuauType.Boolean:
                if (typeof(T) == typeof(bool))
                {
                    return Assign(value.BooleanValue, out result);
                }
                if (typeof(T) == typeof(object))
                {
                    return Assign((object)value.BooleanValue, out result);
                }
                break;
            case LuauType.UserData:
                if (typeof(T) == typeof(LuauObjectHandle) && reference is LuauObjectHandle objectHandle)
                {
                    return Assign(objectHandle, out result);
                }
                if (typeof(T) == typeof(LuauUserData))
                {
                    if (reference is LuauUserData userData)
                    {
                        return Assign(userData, out result);
                    }
                    break;
                }
                if (typeof(T) == typeof(object))
                {
                    return Assign(reference!, out result);
                }
                break;
            case LuauType.LightUserData:
                break;
            case LuauType.Number:
                var number = value.NumberValue;
                if (typeof(T) == typeof(double))
                {
                    return Assign(number, out result);
                }
                if (typeof(T) == typeof(float))
                {
                    return Assign((float)number, out result);
                }
                if (typeof(T) == typeof(byte) && MathEx.IsInteger(number) && number >= byte.MinValue && number <= byte.MaxValue)
                {
                    return Assign((byte)number, out result);
                }
                if (typeof(T) == typeof(sbyte) && MathEx.IsInteger(number) && number >= sbyte.MinValue && number <= sbyte.MaxValue)
                {
                    return Assign((sbyte)number, out result);
                }
                if (typeof(T) == typeof(short) && MathEx.IsInteger(number) && number >= short.MinValue && number <= short.MaxValue)
                {
                    return Assign((short)number, out result);
                }
                if (typeof(T) == typeof(ushort) && MathEx.IsInteger(number) && number >= ushort.MinValue && number <= ushort.MaxValue)
                {
                    return Assign((ushort)number, out result);
                }
                if (typeof(T) == typeof(int) && MathEx.IsInteger(number) && number >= int.MinValue && number <= int.MaxValue)
                {
                    return Assign((int)number, out result);
                }
                if (typeof(T) == typeof(long) && MathEx.IsInt64(number))
                {
                    return Assign((long)number, out result);
                }
                if (typeof(T) == typeof(uint) && MathEx.IsInteger(number) && number >= uint.MinValue && number <= uint.MaxValue)
                {
                    return Assign((uint)number, out result);
                }
                if (typeof(T) == typeof(ulong) && MathEx.IsUInt64(number))
                {
                    return Assign((ulong)number, out result);
                }
                if (typeof(T) == typeof(object))
                {
                    return Assign((object)number, out result);
                }
                break;
            case LuauType.Integer:
                var integer = value.IntegerValue;
                if (typeof(T) == typeof(long))
                {
                    return Assign(integer, out result);
                }
                if (typeof(T) == typeof(byte) && integer >= byte.MinValue && integer <= byte.MaxValue)
                {
                    return Assign((byte)integer, out result);
                }
                if (typeof(T) == typeof(sbyte) && integer >= sbyte.MinValue && integer <= sbyte.MaxValue)
                {
                    return Assign((sbyte)integer, out result);
                }
                if (typeof(T) == typeof(short) && integer >= short.MinValue && integer <= short.MaxValue)
                {
                    return Assign((short)integer, out result);
                }
                if (typeof(T) == typeof(ushort) && integer >= ushort.MinValue && integer <= ushort.MaxValue)
                {
                    return Assign((ushort)integer, out result);
                }
                if (typeof(T) == typeof(int) && integer >= int.MinValue && integer <= int.MaxValue)
                {
                    return Assign((int)integer, out result);
                }
                if (typeof(T) == typeof(uint) && integer >= uint.MinValue && integer <= uint.MaxValue)
                {
                    return Assign((uint)integer, out result);
                }
                if (typeof(T) == typeof(ulong) && integer >= 0)
                {
                    return Assign((ulong)integer, out result);
                }
                if (typeof(T) == typeof(double) && MathEx.TryConvertToDoubleExact(integer, out var exactDouble))
                {
                    return Assign(exactDouble, out result);
                }
                if (typeof(T) == typeof(float) && MathEx.TryConvertToSingleExact(integer, out var exactSingle))
                {
                    return Assign(exactSingle, out result);
                }
                if (typeof(T) == typeof(object))
                {
                    return Assign((object)integer, out result);
                }
                break;
            case LuauType.Vector:
                if (typeof(T) == typeof(Vector3))
                {
                    return Assign(value.VectorValue, out result);
                }
                if (typeof(T) == typeof(object))
                {
                    return Assign((object)value.VectorValue, out result);
                }
                break;
            case LuauType.String:
                if (typeof(T) == typeof(string))
                {
                    return Assign((string)reference!, out result);
                }
                if (typeof(T) == typeof(object))
                {
                    return Assign((object)(string)reference!, out result);
                }
                break;
            case LuauType.Table:
                if (typeof(T) == typeof(LuauTable))
                {
                    return Assign((LuauTable)reference!, out result);
                }
                if (typeof(T) == typeof(object))
                {
                    return Assign((object)(LuauTable)reference!, out result);
                }
                break;
            case LuauType.Function:
                if (typeof(T) == typeof(LuauFunction))
                {
                    return Assign((LuauFunction)reference!, out result);
                }
                if (typeof(T) == typeof(object))
                {
                    return Assign((object)(LuauFunction)reference!, out result);
                }
                break;
            case LuauType.Thread:
                if (typeof(T) == typeof(LuauState))
                {
                    return Assign((LuauState)reference!, out result);
                }
                if (typeof(T) == typeof(object))
                {
                    return Assign((object)(LuauState)reference!, out result);
                }
                break;
            case LuauType.Buffer:
                if (typeof(T) == typeof(LuauBuffer))
                {
                    return Assign((LuauBuffer)reference!, out result);
                }
                if (typeof(T) == typeof(object))
                {
                    return Assign((object)(LuauBuffer)reference!, out result);
                }
                break;
        }

        result = default!;
        return false;
    }

    static bool Assign<T>(object value, out T result)
    {
        result = (T)value;
        return true;
    }

    /// <summary>
    /// Tests primitive values by value and VM-backed references by managed
    /// wrapper identity. This does not invoke Luau equality metamethods.
    /// </summary>
    public bool Equals(LuauValue other)
    {
        if (type != other.type) return false;

        return type switch
        {
            LuauType.Nil => true,
            LuauType.Boolean => value.BooleanValue == other.value.BooleanValue,
            LuauType.LightUserData => value.PointerValue == other.value.PointerValue,
            LuauType.UserData => ReferenceEquals(reference, other.reference),
            LuauType.Number => value.NumberValue == other.value.NumberValue,
            LuauType.Integer => value.IntegerValue == other.value.IntegerValue,
            LuauType.Vector => value.VectorValue == other.value.VectorValue,
            LuauType.String => ((string)reference!).Equals((string)other.reference!),
            _ => reference == other.reference,
        };
    }

    /// <summary>Tests whether <paramref name="obj"/> is an equal <see cref="LuauValue"/>.</summary>
    public override bool Equals([NotNullWhen(true)] object? obj)
    {
        return obj is LuauValue other && Equals(other);
    }

    /// <summary>Returns a hash consistent with managed value-or-wrapper-identity equality.</summary>
    public override int GetHashCode()
    {
        return type switch
        {
            LuauType.Nil => HashCode.Combine(type),
            LuauType.Boolean => HashCode.Combine(type, value.BooleanValue),
            LuauType.LightUserData => HashCode.Combine(type, value.PointerValue),
            LuauType.Number => HashCode.Combine(type, value.NumberValue),
            LuauType.Integer => HashCode.Combine(type, value.IntegerValue),
            LuauType.Vector => HashCode.Combine(type, value.VectorValue),
            LuauType.String => HashCode.Combine(type, (string)reference!),
            _ => HashCode.Combine(type, RuntimeHelpers.GetHashCode(reference!)),
        };
    }

    /// <summary>Tests two values using managed <see cref="Equals(LuauValue)"/> semantics.</summary>
    public static bool operator ==(LuauValue left, LuauValue right)
    {
        return left.Equals(right);
    }

    /// <summary>Tests two values for inequality using managed equality semantics.</summary>
    public static bool operator !=(LuauValue left, LuauValue right)
    {
        return !(left == right);
    }

    /// <summary>Converts a double to a Luau floating-point number.</summary>
    public static implicit operator LuauValue(double value) => FromNumber(value);
    /// <summary>Converts a byte to an exactly equal Luau number.</summary>
    public static implicit operator LuauValue(byte value) => FromNumber(value);
    /// <summary>Converts a signed byte to an exactly equal Luau number.</summary>
    public static implicit operator LuauValue(sbyte value) => FromNumber(value);
    /// <summary>Converts a signed 16-bit value to an exactly equal Luau number.</summary>
    public static implicit operator LuauValue(short value) => FromNumber(value);
    /// <summary>Converts an unsigned 16-bit value to an exactly equal Luau number.</summary>
    public static implicit operator LuauValue(ushort value) => FromNumber(value);
    /// <summary>Converts a signed 32-bit value to an exactly equal Luau number.</summary>
    public static implicit operator LuauValue(int value) => FromNumber(value);
    /// <summary>Converts an unsigned 32-bit value to an exactly equal Luau number.</summary>
    public static implicit operator LuauValue(uint value) => FromNumber(value);
    /// <summary>Converts a signed 64-bit value to a distinct Luau integer.</summary>
    public static implicit operator LuauValue(long value) => FromInteger(value);
    /// <summary>Converts an unsigned 64-bit value to a Luau integer, rejecting values above <see cref="long.MaxValue"/>.</summary>
    public static implicit operator LuauValue(ulong value) => FromInteger(checked((long)value));
    /// <summary>Converts a Boolean to a Luau Boolean value.</summary>
    public static implicit operator LuauValue(bool value) => FromBoolean(value);
    /// <summary>Converts a non-null managed string to a Luau string value.</summary>
    public static implicit operator LuauValue(string value) => FromString(value);
    /// <summary>Converts a three-component vector to a Luau vector value.</summary>
    public static implicit operator LuauValue(Vector3 value) => FromVector(value);
    /// <summary>Wraps a table without retaining or transferring its ownership.</summary>
    public static implicit operator LuauValue(LuauTable value) => FromTable(value);
    /// <summary>Wraps a function without retaining or transferring its ownership.</summary>
    public static implicit operator LuauValue(LuauFunction value) => FromFunction(value);
    /// <summary>Wraps a root or coroutine without retaining or transferring its ownership.</summary>
    public static implicit operator LuauValue(LuauState value) => FromThread(value);
    /// <summary>Wraps a buffer without retaining or transferring its ownership.</summary>
    public static implicit operator LuauValue(LuauBuffer value) => FromBuffer(value);
    /// <summary>Wraps userdata without retaining or transferring its ownership.</summary>
    public static implicit operator LuauValue(LuauUserData value) => FromUserData(value);
    /// <summary>Wraps an object capability without retaining or transferring its ownership.</summary>
    public static implicit operator LuauValue(LuauObjectHandle value) => FromObjectHandle(value);
}
