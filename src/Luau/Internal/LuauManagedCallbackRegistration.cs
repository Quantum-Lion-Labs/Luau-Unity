using Luau.Internal.Interop;
using System.Runtime.InteropServices;

namespace Luau;

internal interface ILuauManagedCallbackFunction
{
    int RegistrationId { get; }
    LuauHostManagedFunction Callback { get; }
}

internal sealed class LuauManagedCallbackRegistration
{
    internal LuauManagedCallbackRegistration(
        LuauVmContext owner,
        int id,
        string? name,
        Func<LuauState, CancellationToken, int> callback)
    {
        Owner = owner;
        Id = id;
        Name = name;
        SynchronousCallback = callback;
        nativeReleaseHandle = GCHandle.Alloc(this, GCHandleType.WeakTrackResurrection);
    }

    internal LuauManagedCallbackRegistration(
        LuauVmContext owner,
        int id,
        string? name,
        Func<LuauState, CancellationToken, ValueTask<int>> callback)
    {
        Owner = owner;
        Id = id;
        Name = name;
        AsynchronousCallback = callback;
        nativeReleaseHandle = GCHandle.Alloc(this, GCHandleType.WeakTrackResurrection);
    }

    readonly LuauVmContext Owner;
    // The VM owns registrations. A native release token must not root the VM
    // through callback closures or capability bindings. Track resurrection so
    // destructors can still queue releases while the root finalizer closes it.
    GCHandle nativeReleaseHandle;
    int nativeReleaseHandleDisposed;
    int pendingNativeReleaseCount;
    int nativeReleaseQueued;
    LuauManagedCallbackRegistration? nextNativeRelease;

    internal int Id { get; }
    internal string? Name { get; }
    internal Func<LuauState, CancellationToken, int>? SynchronousCallback { get; }
    internal Func<LuauState, CancellationToken, ValueTask<int>>? AsynchronousCallback { get; }
    internal bool IsAsync => AsynchronousCallback != null;

    internal IntPtr NativeReleaseToken => GCHandle.ToIntPtr(nativeReleaseHandle);

    internal bool HasPendingNativeReleases =>
        Volatile.Read(ref pendingNativeReleaseCount) != 0 ||
        Volatile.Read(ref nativeReleaseQueued) != 0;

    internal void QueueNativeRelease()
    {
        Interlocked.Increment(ref pendingNativeReleaseCount);
        QueueIfNeeded();
    }

    internal LuauManagedCallbackRegistration? TakeNextNativeRelease()
    {
        return Interlocked.Exchange(ref nextNativeRelease, null);
    }

    internal void SetNextNativeRelease(LuauManagedCallbackRegistration? next)
    {
        Volatile.Write(ref nextNativeRelease, next);
    }

    internal int DrainNativeReleaseCount()
    {
        var count = Interlocked.Exchange(ref pendingNativeReleaseCount, 0);
        Volatile.Write(ref nativeReleaseQueued, 0);

        // A destructor may have incremented the counter after the exchange but
        // before the queued flag was cleared. Re-check so that release cannot
        // be stranded until an unrelated VM operation happens to run.
        if (Volatile.Read(ref pendingNativeReleaseCount) != 0)
        {
            QueueIfNeeded();
        }

        return count;
    }

    internal void DisposeNativeReleaseToken()
    {
        if (Interlocked.Exchange(ref nativeReleaseHandleDisposed, 1) == 0)
        {
            nativeReleaseHandle.Free();
        }
    }

    void QueueIfNeeded()
    {
        if (Interlocked.CompareExchange(ref nativeReleaseQueued, 1, 0) == 0)
        {
            Owner.EnqueueManagedCallbackNativeRelease(this);
        }
    }
}

internal static unsafe class LuauManagedCallbackLifetime
{
    static readonly LuauHostUserdataDestructor destructor = Destroy;

    internal static LuauHostUserdataDestructor Destructor => destructor;

    internal static void QueueRelease(IntPtr userdata)
    {
        if (userdata == IntPtr.Zero)
        {
            return;
        }

        var handle = GCHandle.FromIntPtr(userdata);
        if (handle.Target is LuauManagedCallbackRegistration registration)
        {
            registration.QueueNativeRelease();
        }
    }

    [AOT.MonoPInvokeCallback(typeof(LuauHostUserdataDestructor))]
    static void Destroy(void* userdata)
    {
        try
        {
            if (userdata != null)
            {
                QueueRelease((IntPtr)userdata);
            }
        }
        catch
        {
            // Native GC/finalization callbacks must never unwind into Luau.
        }
    }
}
