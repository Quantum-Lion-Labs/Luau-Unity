namespace Luau;

/// <summary>
/// Implemented by types whose Luau-visible object surface is generated from
/// <see cref="LuauLibraryAttribute"/> and <see cref="LuauMemberAttribute"/>.
/// </summary>
public interface ILuauObjectCapability
{
    /// <summary>Gets the generated descriptor for this object's capability surface.</summary>
    LuauObjectDescriptor LuauObjectDescriptor { get; }
}

/// <summary>
/// Describes one explicit, immutable member surface that can be attached to an
/// opaque <see cref="LuauObjectHandle"/>. Source-generated bindings and hosts
/// constructing descriptors manually use the same capability model. Descriptor
/// identity is part of the authority: two descriptor instances never silently
/// upgrade one another even when they target the same managed object.
/// </summary>
public abstract class LuauObjectDescriptor
{
    internal LuauObjectDescriptor(string typeName)
    {
        if (typeName == null)
        {
            throw new ArgumentNullException(nameof(typeName));
        }
        if (typeName.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("A Luau capability type name cannot contain a NUL character.", nameof(typeName));
        }

        TypeName = typeName;
    }

    /// <summary>Gets the diagnostic type name exposed by this descriptor.</summary>
    public string TypeName { get; }

    internal abstract int MemberCount { get; }
    internal abstract string GetMemberName(int index);
    internal abstract bool IsMethod(int index);
    internal abstract bool IsAsyncMethod(int index);
    internal abstract int FindMember(string name);
    internal abstract void ValidateTarget(object target);
    internal abstract void ReadMember(int index, object target, LuauCallContext context);
    internal abstract void WriteMember(int index, object target, LuauCallContext context);
    internal abstract void InvokeMethod(int index, object target, LuauCallContext context);
    internal abstract ValueTask InvokeMethodAsync(int index, object target, LuauCallContext context);
}

/// <summary>
/// A reflection-free descriptor for one managed reference type. Source-generated
/// bindings or host-authored policies construct this type once and reuse it for
/// every instance sharing that authority.
/// </summary>
/// <typeparam name="T">The exact managed target type.</typeparam>
public sealed class LuauObjectDescriptor<T> : LuauObjectDescriptor
    where T : class
{
    readonly Action<T>? validateTarget;
    readonly LuauObjectMember<T>[] members;

    /// <summary>Creates an immutable capability descriptor.</summary>
    /// <param name="typeName">The diagnostic Luau type name.</param>
    /// <param name="validateTarget">
    /// An optional AOT-safe liveness/thread-affinity validator invoked before
    /// every member access. Unity bindings use this to reject destroyed objects.
    /// </param>
    /// <param name="members">The explicitly allowed member surface.</param>
    public LuauObjectDescriptor(
        string typeName,
        Action<T>? validateTarget,
        LuauObjectMember<T>[] members)
        : base(typeName)
    {
        this.validateTarget = validateTarget;
        if (members == null)
        {
            throw new ArgumentNullException(nameof(members));
        }

        this.members = (LuauObjectMember<T>[])members.Clone();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < this.members.Length; index++)
        {
            var member = this.members[index]
                ?? throw new ArgumentException("Capability member arrays cannot contain null entries.", nameof(members));
            if (!names.Add(member.Name))
            {
                throw new ArgumentException(
                    $"Capability member name '{member.Name}' is declared more than once.",
                    nameof(members));
            }
        }
    }

    internal override int MemberCount => members.Length;

    internal override string GetMemberName(int index) => members[index].Name;
    internal override bool IsMethod(int index) => members[index].Kind != LuauObjectMemberKind.Property;
    internal override bool IsAsyncMethod(int index) => members[index].Kind == LuauObjectMemberKind.AsyncMethod;

    internal override int FindMember(string name)
    {
        for (var index = 0; index < members.Length; index++)
        {
            if (string.Equals(members[index].Name, name, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    internal override void ValidateTarget(object target)
    {
        if (target is not T typedTarget)
        {
            throw new InvalidOperationException(
                $"The managed target is not valid for the '{TypeName}' Luau capability descriptor.");
        }

        validateTarget?.Invoke(typedTarget);
    }

    internal override void ReadMember(int index, object target, LuauCallContext context)
    {
        var getter = members[index].Getter
            ?? throw new LuauException($"Cannot read write-only capability member '{members[index].Name}'.");
        getter((T)target, context);
    }

    internal override void WriteMember(int index, object target, LuauCallContext context)
    {
        var setter = members[index].Setter
            ?? throw new LuauException($"Cannot set readonly capability member '{members[index].Name}'.");
        setter((T)target, context);
    }

    internal override void InvokeMethod(int index, object target, LuauCallContext context)
    {
        var method = members[index].MethodCallback
            ?? throw new InvalidOperationException("The capability member is not a synchronous method.");
        method((T)target, context);
    }

    internal override ValueTask InvokeMethodAsync(int index, object target, LuauCallContext context)
    {
        var method = members[index].AsyncMethodCallback
            ?? throw new InvalidOperationException("The capability member is not an asynchronous method.");
        return method((T)target, context);
    }
}

/// <summary>
/// Immutable dispatch data for one explicitly exposed member. Source-generated
/// bindings and manually constructed descriptors use the same reflection-free
/// member representation.
/// </summary>
public sealed class LuauObjectMember<T>
    where T : class
{
    LuauObjectMember(
        string name,
        LuauObjectMemberKind kind,
        Action<T, LuauCallContext>? getter,
        Action<T, LuauCallContext>? setter,
        Action<T, LuauCallContext>? method,
        Func<T, LuauCallContext, ValueTask>? asyncMethod)
    {
        if (name == null)
        {
            throw new ArgumentNullException(nameof(name));
        }
        if (name.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("A Luau capability member name cannot contain a NUL character.", nameof(name));
        }

        Name = name;
        Kind = kind;
        Getter = getter;
        Setter = setter;
        MethodCallback = method;
        AsyncMethodCallback = asyncMethod;
    }

    /// <summary>Gets the Luau-visible member name.</summary>
    public string Name { get; }

    internal LuauObjectMemberKind Kind { get; }
    internal Action<T, LuauCallContext>? Getter { get; }
    internal Action<T, LuauCallContext>? Setter { get; }
    internal Action<T, LuauCallContext>? MethodCallback { get; }
    internal Func<T, LuauCallContext, ValueTask>? AsyncMethodCallback { get; }

    /// <summary>Creates a readable, writable, or read/write property binding.</summary>
    public static LuauObjectMember<T> Property(
        string name,
        Action<T, LuauCallContext>? getter,
        Action<T, LuauCallContext>? setter)
    {
        if (getter == null && setter == null)
        {
            throw new ArgumentException("A capability property must be readable, writable, or both.", nameof(getter));
        }

        return new LuauObjectMember<T>(
            name,
            LuauObjectMemberKind.Property,
            getter,
            setter,
            method: null,
            asyncMethod: null);
    }

    /// <summary>Creates a synchronous method binding.</summary>
    public static LuauObjectMember<T> Method(
        string name,
        Action<T, LuauCallContext> method)
    {
        return new LuauObjectMember<T>(
            name,
            LuauObjectMemberKind.Method,
            getter: null,
            setter: null,
            method ?? throw new ArgumentNullException(nameof(method)),
            asyncMethod: null);
    }

    /// <summary>Creates an asynchronous method binding.</summary>
    public static LuauObjectMember<T> AsyncMethod(
        string name,
        Func<T, LuauCallContext, ValueTask> method)
    {
        return new LuauObjectMember<T>(
            name,
            LuauObjectMemberKind.AsyncMethod,
            getter: null,
            setter: null,
            method: null,
            method ?? throw new ArgumentNullException(nameof(method)));
    }
}

internal enum LuauObjectMemberKind
{
    Property,
    Method,
    AsyncMethod,
}
