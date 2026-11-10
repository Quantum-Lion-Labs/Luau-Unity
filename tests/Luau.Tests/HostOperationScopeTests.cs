namespace Luau.Tests;

public sealed class HostOperationScopeTests
{
    [Fact]
    public void HostOperationScopeEndsItsOperationWhenTheStackBoundaryCannotBeCreated()
    {
        // A root disposed by another thread right after a host operation began
        // used to strand that operation: the scope constructor threw before its
        // caller's using statement was assigned, so the deferred native close
        // never ran. Denying access only once an operation is active reproduces
        // that window deterministically.
        LuauState? root = null;
        var scheduler = new DenyOnceOperationActiveScheduler(() => root is { } r && r.Context.GetActiveOperation() != null);
        root = LuauState.Create(TestBudgets.StateOptions with
        {
            DefaultExecutionOptions = TestBudgets.Execution with { ContinuationScheduler = scheduler },
        });

        Assert.Throws<InvalidOperationException>(() =>
        {
            using var scope = new LuauDirectHostOperationScope(root);
        });

        Assert.Null(root.Context.GetActiveOperation());
        root["value"] = 42;
        Assert.Equal(42, root["value"].Read<int>());
        root.Dispose();
        Assert.Equal(1, root.Context.CloseCount);
        Assert.Equal(0, root.Context.ManagedCallbackCount);
    }

    sealed class DenyOnceOperationActiveScheduler(Func<bool> operationActive) : ILuauContinuationScheduler
    {
        bool denied;

        public bool CheckAccess()
        {
            if (denied || !operationActive())
            {
                return true;
            }

            denied = true;
            return false;
        }

        public void Post(Action continuation) => continuation();
    }
}
