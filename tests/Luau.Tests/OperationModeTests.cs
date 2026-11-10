namespace Luau.Tests;

public sealed class OperationModeTests
{
    [Fact]
    public void OwnedOperationBoundariesDeclareTheirExecutionMode()
    {
        using var state = TestBudgets.CreateRoot();

        using (var nested = state.BeginNestedOperationIfNeeded("@operation/nested.luau"))
        {
            Assert.Equal(ScriptOperationMode.NestedProtectedCall, nested.Mode);
            Assert.Equal(ScriptOperationMode.NestedProtectedCall, nested.Operation.Mode);
        }

        using (var direct = state.BeginHostOperationIfNeeded())
        {
            Assert.Equal(ScriptOperationMode.DirectHostOperation, direct.Mode);
            Assert.Equal(ScriptOperationMode.DirectHostOperation, direct.Operation.Mode);
        }
    }

    [Fact]
    public void ScriptExecutionUsesTheTopLevelResumeMode()
    {
        using var state = TestBudgets.CreateRoot();
        ScriptOperationMode? observed = null;
        using var callback = state.CreateFunction("observeMode", context =>
        {
            observed = context.State.Context.GetActiveOperation()?.Mode;
            context.Return(true);
        });
        state["observeMode"] = callback;

        Assert.True(Assert.Single(state.DoString("return observeMode()")).Read<bool>());
        Assert.Equal(ScriptOperationMode.TopLevelResume, observed);
    }
}
