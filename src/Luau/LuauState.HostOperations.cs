namespace Luau;

public unsafe partial class LuauState
{
    internal void CollectGarbage()
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        using var operation = new LuauDirectHostOperationScope(this);
        LuauNativeProtection.Prepare(context);
        var status = Luau.Internal.Interop.NativeMethods.luau_host_collect(l);
        LuauNativeProtection.ThrowIfFailed(this, l, status, "run a full garbage collection");
        operation.Complete("A direct garbage collection cannot yield or suspend the Luau thread.");
    }

    internal LuauOperationLease BeginHostOperationIfNeeded()
    {
        return BeginOperationIfNeeded(ScriptOperationMode.DirectHostOperation, chunkName: null);
    }

    internal LuauOperationLease BeginNestedOperationIfNeeded(string? chunkName)
    {
        return BeginOperationIfNeeded(ScriptOperationMode.NestedProtectedCall, chunkName);
    }

    LuauOperationLease BeginOperationIfNeeded(ScriptOperationMode mode, string? chunkName)
    {
        ThrowIfDisposed();
        var active = context.GetActiveOperation();
        if (active != null)
        {
            return new LuauOperationLease(
                active,
                ownedOperation: null,
                initialThreadStatus: null,
                mode);
        }

        var initialThreadStatus = GetNativeOperationStatus();
        var created = BeginOperation(
            chunkName: null,
            options: null,
            cancellationToken: default,
            isAsync: false,
            mode);
        return new LuauOperationLease(created, created, initialThreadStatus, mode);
    }
}

internal readonly ref struct LuauOperationLease
{
    readonly ScriptOperation? ownedOperation;
    readonly Luau.Internal.Interop.LuauHostStatus? initialThreadStatus;

    internal LuauOperationLease(
        ScriptOperation operation,
        ScriptOperation? ownedOperation,
        Luau.Internal.Interop.LuauHostStatus? initialThreadStatus,
        ScriptOperationMode mode)
    {
        Operation = operation;
        this.ownedOperation = ownedOperation;
        this.initialThreadStatus = initialThreadStatus;
        Mode = mode;
    }

    internal ScriptOperation Operation { get; }
    internal ScriptOperationMode Mode { get; }

    internal bool IsOwnedOperationSuspended =>
        ownedOperation != null &&
        initialThreadStatus == Luau.Internal.Interop.LuauHostStatus.Ok &&
        ownedOperation.State.GetNativeOperationStatus() != Luau.Internal.Interop.LuauHostStatus.Ok;

    internal void AbortSuspendedOperation()
    {
        if (ownedOperation == null)
        {
            return;
        }

        ScriptRunner.AbortHostOperation(ownedOperation);
    }

    public void Dispose()
    {
        ownedOperation?.Dispose();
    }
}

internal ref struct LuauDirectHostOperationScope
{
    LuauOperationLease operation;
    LuauStackBoundary stack;
    bool suspensionHandled;
    bool disposed;

    internal LuauDirectHostOperationScope(LuauState state)
    {
        operation = state.BeginHostOperationIfNeeded();
        try
        {
            stack = new LuauStackBoundary(state);
        }
        catch
        {
            // The caller's using statement is never assigned when the
            // constructor throws, for example when another thread disposes
            // the root right after the operation began. End the operation
            // here, or a deferred native close would never run.
            operation.Dispose();
            throw;
        }

        suspensionHandled = false;
        disposed = false;
    }

    internal ScriptOperation Operation => operation.Operation;

    internal void Complete(string yieldedMessage)
    {
        if (operation.IsOwnedOperationSuspended)
        {
            AbortSuspendedOperation();
            throw new LuauException(yieldedMessage);
        }

        stack.Complete();
    }

    internal void CompleteAndRestore(string yieldedMessage)
    {
        if (operation.IsOwnedOperationSuspended)
        {
            AbortSuspendedOperation();
            throw new LuauException(yieldedMessage);
        }

        stack.Restore();
    }

    void AbortSuspendedOperation()
    {
        if (suspensionHandled)
        {
            return;
        }

        suspensionHandled = true;
        stack.Abandon();
        operation.AbortSuspendedOperation();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            if (operation.IsOwnedOperationSuspended)
            {
                AbortSuspendedOperation();
            }
            else
            {
                stack.Dispose();
            }
        }
        finally
        {
            operation.Dispose();
            disposed = true;
        }
    }
}
