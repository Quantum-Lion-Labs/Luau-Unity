using System.Runtime.InteropServices;
using Luau.Internal.Interop;
using static Luau.Internal.Interop.NativeMethods;

namespace Luau;

/// <summary>Creates generated managed-object capabilities for a Luau state.</summary>
public static class LuauObjectExtensions
{
    /// <summary>
    /// Creates an opaque per-state capability from a source-generated object
    /// binding. Only members marked with <see cref="LuauMemberAttribute"/> are
    /// reachable through the returned userdata.
    /// </summary>
    public static LuauObjectHandle CreateHandle<T>(this LuauState state, T target)
        where T : class, ILuauObjectCapability
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }
        if (target == null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        return state.CreateHandleCore(target, target.LuauObjectDescriptor);
    }
}

/// <summary>
/// Owns one official Luau VM root or a coroutine retained by that root. Root
/// disposal cancels active operations and invalidates every child and VM-backed
/// wrapper. Hosts must dispose root states deterministically.
/// </summary>
public partial class LuauState
{
    /// <summary>
    /// Creates a capability using an explicit immutable descriptor, whether
    /// source-generated or constructed manually by the host. Descriptor identity
    /// remains part of the authority, allowing intentionally narrower views over
    /// the same target without upgrading either handle.
    /// </summary>
    public LuauObjectHandle CreateHandle<T>(T target, LuauObjectDescriptor<T> descriptor)
        where T : class
    {
        if (target == null)
        {
            throw new ArgumentNullException(nameof(target));
        }
        if (descriptor == null)
        {
            throw new ArgumentNullException(nameof(descriptor));
        }

        return CreateHandleCore(target, descriptor);
    }

    internal unsafe LuauObjectHandle CreateHandleCore(object target, LuauObjectDescriptor descriptor)
    {
        ThrowIfDisposed();
        descriptor.ValidateTarget(target);

        var rootState = GetMainThread();
        var registry = context.ObjectRegistry;
        // Keep identity lookup/reservation and activation inside the root VM's
        // serialization boundary. A concurrent export cannot observe the
        // in-progress reservation and manufacture a second userdata identity.
        using var creationAccess = rootState.EnterNativeAccess();
        var token = registry.ReserveOrRetain(target, descriptor, out var retained);
        if (retained)
        {
            return new LuauObjectHandle(rootState, token);
        }

        var registrationActivated = false;
        var wrapperTransferred = false;
        try
        {
            var binding = registry.GetOrCreateBinding(
                descriptor,
                () => CreateObjectBinding(rootState, descriptor));
            var pointer = rootState.PointerUnsafe;
            var originalTop = luau_host_stack_get_top(pointer);
            var stackRestored = false;
            LuauObjectPayload* payload = null;
            try
            {
                var callbacks = new LuauHostCallbackTable
                {
                    struct_size = (uint)sizeof(LuauHostCallbackTable),
                    version = 1,
                    userdata_destructor = Marshal.GetFunctionPointerForDelegate(LuauObjectLifetime.Destructor),
                };

                void* rawPayload = null;
                LuauNativeProtection.Prepare(context);
                var createStatus = luau_host_userdata_create_with_destructor(
                    pointer,
                    (ulong)sizeof(LuauObjectPayload),
                    &callbacks,
                    &rawPayload);
                LuauNativeProtection.ThrowIfFailed(
                    rootState,
                    pointer,
                    createStatus,
                    "create a managed Luau object capability");

                payload = (LuauObjectPayload*)rawPayload;
                *payload = new LuauObjectPayload
                {
                    ContextId = token.ContextId,
                    Slot = token.Slot,
                    Generation = token.Generation,
                    // Publish magic last so a partially initialized token is inert.
                    Magic = LuauObjectPayload.ExpectedMagic,
                };

                var userdataIndex = luau_host_stack_abs_index(pointer, -1);
                rootState.PushTable(binding.Metatable);
                var metatableSet = 0;
                LuauNativeProtection.Prepare(context);
                var metatableStatus = luau_host_metatable_set(pointer, userdataIndex, &metatableSet);
                LuauNativeProtection.ThrowIfFailed(
                    rootState,
                    pointer,
                    metatableStatus,
                    "protect a managed Luau object capability");
                if (metatableSet == 0)
                {
                    throw new LuauException("The Luau VM rejected a managed capability metatable.");
                }

                var reference = LuauReferenceHelper.CreateReference(
                    rootState,
                    userdataIndex,
                    "retain a managed Luau object capability");
                registry.Activate(token, reference);
                registrationActivated = true;

                rootState.SetTop(originalTop);
                stackRestored = true;
                var handle = new LuauObjectHandle(rootState, token);
                wrapperTransferred = true;
                return handle;
            }
            finally
            {
                try
                {
                    if (!stackRestored)
                    {
                        if (payload != null)
                        {
                            payload->Magic = 0;
                        }
                        rootState.SetTop(originalTop);
                    }
                }
                finally
                {
                    GC.KeepAlive(LuauObjectLifetime.Destructor);
                }
            }
        }
        finally
        {
            if (!wrapperTransferred)
            {
                if (registrationActivated)
                {
                    registry.RollbackActivation(token, rootState);
                }
                else
                {
                    registry.CancelReservation(token);
                }
            }
        }
    }

    static LuauObjectBinding CreateObjectBinding(
        LuauState rootState,
        LuauObjectDescriptor descriptor)
    {
        var methodFunctions = new LuauFunction?[descriptor.MemberCount];
        LuauFunction? indexFunction = null;
        LuauFunction? newIndexFunction = null;
        LuauFunction? toStringFunction = null;
        LuauTable? metatable = null;
        try
        {
            for (var index = 0; index < descriptor.MemberCount; index++)
            {
                if (!descriptor.IsMethod(index))
                {
                    continue;
                }

                var memberIndex = index;
                var callbackName = $"{descriptor.TypeName}.{descriptor.GetMemberName(index)}";
                if (descriptor.IsAsyncMethod(index))
                {
                    methodFunctions[index] = rootState.CreateAsyncFunction(
                        callbackName,
                        async context =>
                        {
                            var target = context.State.ResolveObjectTarget(0, descriptor);
                            await descriptor.InvokeMethodAsync(memberIndex, target, context).ConfigureAwait(false);
                        });
                }
                else
                {
                    methodFunctions[index] = rootState.CreateFunction(
                        callbackName,
                        context =>
                        {
                            var target = context.State.ResolveObjectTarget(0, descriptor);
                            descriptor.InvokeMethod(memberIndex, target, context);
                        });
                }
            }

            indexFunction = rootState.CreateFunction(
                $"{descriptor.TypeName}.__index",
                context =>
                {
                    // Sandboxed Luau environments pre-resolve global index chains
                    // while bytecode is loaded. Capability members are dynamic, so
                    // leave the import constant nil and force GETIMPORT to dispatch
                    // through this metatable again when the script actually runs.
                    if (context.State.Context.IsLoadingBytecode)
                    {
                        return;
                    }

                    var name = context.Read<string>(1);
                    var memberIndex = descriptor.FindMember(name);
                    if (memberIndex < 0)
                    {
                        return;
                    }

                    var target = context.State.ResolveObjectTarget(0, descriptor);

                    if (descriptor.IsMethod(memberIndex))
                    {
                        context.Return(methodFunctions[memberIndex]!);
                    }
                    else
                    {
                        descriptor.ReadMember(memberIndex, target, context);
                    }
                });

            newIndexFunction = rootState.CreateFunction(
                $"{descriptor.TypeName}.__newindex",
                context =>
                {
                    var target = context.State.ResolveObjectTarget(0, descriptor);
                    var name = context.Read<string>(1);
                    var memberIndex = descriptor.FindMember(name);
                    if (memberIndex < 0)
                    {
                        throw new LuauException($"Cannot set unknown capability member '{name}'.");
                    }
                    if (descriptor.IsMethod(memberIndex))
                    {
                        throw new LuauException($"Cannot replace capability method '{name}'.");
                    }

                    descriptor.WriteMember(memberIndex, target, context);
                });

            toStringFunction = rootState.CreateFunction(
                $"{descriptor.TypeName}.__tostring",
                context =>
                {
                    _ = context.State.ResolveObjectTarget(0, descriptor);
                    context.Return(descriptor.TypeName);
                });

            metatable = rootState.CreateTable(0, 4);
            metatable["__index"] = indexFunction;
            metatable["__newindex"] = newIndexFunction;
            metatable["__tostring"] = toStringFunction;
            metatable["__metatable"] = "protected Luau object capability";
            metatable.SetReadOnly();

            return new LuauObjectBinding(
                metatable,
                methodFunctions,
                [indexFunction, newIndexFunction, toStringFunction]);
        }
        catch
        {
            metatable?.Dispose();
            foreach (var function in methodFunctions)
            {
                function?.Dispose();
            }
            indexFunction?.Dispose();
            newIndexFunction?.Dispose();
            toStringFunction?.Dispose();
            throw;
        }
    }

    internal object ResolveObjectTarget(int index, LuauObjectDescriptor descriptor)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        // Public callback argument indexes are zero-based; native stack slots
        // are one-based, matching LuauCallContext.Read<T>.
        if (!TryReadObjectToken(index + 1, out var token))
        {
            throw new InvalidOperationException(
                $"The value passed to '{descriptor.TypeName}' is not a managed Luau object capability.");
        }

        return context.ObjectRegistry.ResolveTarget(token, descriptor);
    }

    internal unsafe bool TryReadObjectToken(int index, out LuauObjectToken token)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        if ((LuauHostType)luau_host_type(l, index) != LuauHostType.Userdata ||
            luau_host_object_length(l, index) != sizeof(LuauObjectPayload))
        {
            token = default;
            return false;
        }

        var payload = (LuauObjectPayload*)luau_host_to_userdata(l, index);
        if (payload == null || payload->Magic != LuauObjectPayload.ExpectedMagic)
        {
            token = default;
            return false;
        }

        token = payload->Token;
        if (token.ContextId != context.ObjectRegistry.ContextId)
        {
            throw new InvalidOperationException("A managed Luau object capability cannot cross independent VMs.");
        }

        return true;
    }

    internal LuauObjectHandle RetainObjectHandleFromStack(
        int index,
        LuauObjectToken token,
        LuauCallFrame? borrowedFrame = null)
    {
        var rootState = GetMainThread();
        context.ObjectRegistry.RetainFromStack(
            token,
            () => LuauReferenceHelper.CreateReference(
                this,
                index,
                "retain a managed Luau object capability"));
        return new LuauObjectHandle(rootState, token, borrowedFrame);
    }
}
