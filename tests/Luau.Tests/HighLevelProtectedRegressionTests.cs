#pragma warning disable CS0618 // Deliberate regression coverage for transitional unsupported APIs.

using System.Text;

namespace Luau.Tests;

public sealed class HighLevelProtectedRegressionTests
{
    [Fact]
    public void InvalidStringIndexDoesNotConsumeAnUnrelatedStackValue()
    {
        using var state = TestBudgets.CreateRoot();
        state.PushInteger(42);

        Assert.Throws<InvalidOperationException>(() => state.ToString(2));

        Assert.Equal(1, state.GetTop());
        Assert.Equal(42, state.Pop().Read<int>());
    }

    [Fact]
    public void NonYieldableTostringMetamethodBudgetIsTypedAndVmRecovers()
    {
        const string chunkName = "@budgets/non-yieldable-tostring.luau";
        using var state = TestBudgets.CreateRoot();
        state.OpenBaseLibrary();

        var exception = Assert.Throws<LuauExecutionBudgetException>(() => state.DoString(
            "return tostring(setmetatable({}, { __tostring = function() while true do end end }))",
            chunkName,
            executionOptions: LuauExecutionOptions.Unbounded with { InterruptCountLimit = 1 }));

        Assert.Equal(LuauExecutionBudgetKind.InterruptCount, exception.BudgetKind);
        Assert.Equal(chunkName, exception.ChunkName);
        Assert.True(exception.ObservedInterruptCount > exception.InterruptCountLimit);
        Assert.Equal(42, Assert.Single(state.DoString("return 40 + 2")).Read<int>());
    }

    [Fact]
    public void CallbackPushStringQuotaFailureIsControlledAndVmRecovers()
    {
        const long memoryLimit = 1_048_576;
        const string chunkName = "@callbacks/push-string-quota.luau";
        using var state = LuauState.Create(TestBudgets.StateOptions with
        {
            MemoryLimitBytes = memoryLimit,
            BytecodePolicy = LuauBytecodePolicy.Reject,
        });
        state["pushHugeString"] = state.CreateFunction(
            "pushHugeString",
            context => context.Return(new string('x', 2_097_152)));

        var exception = Assert.Throws<LuauManagedCallbackException>(
            () => state.DoString("return pushHugeString()", chunkName));

        Assert.Equal(chunkName, exception.ChunkName);
        Assert.Equal("pushHugeString", exception.CallbackName);
        var memoryException = Assert.IsType<LuauMemoryLimitException>(exception.InnerException);
        Assert.Equal(memoryLimit, memoryException.LimitBytes);
        Assert.True(memoryException.AttemptedBytes > memoryLimit);
        var results = state.DoString("return 40 + 2", "@callbacks/recovered.luau");
        Assert.Single(results);
        Assert.Equal(42, results[0].Read<int>());
    }

    [Fact]
    public void ToDisplayStringReturnsExactUtf8MetamethodOutputAndPreservesInput()
    {
        const string expected = "héllø 🐺 東京";
        using var state = TestBudgets.CreateRoot();
        state.OpenBaseLibrary();
        using var value = Assert.Single(state.DoString(
            $$"""
            return setmetatable({}, {
                __tostring = function()
                    return "{{expected}}"
                end,
            })
            """,
            "@display/utf8.luau")).Read<LuauTable>();
        state["value"] = value;
        state["display"] = state.CreateFunction("display", context =>
        {
            var text = context.ToDisplayString(0, 1_024, out var wasTruncated);
            Assert.False(wasTruncated);
            context.Return(text);
        });

        Assert.Equal(expected, Assert.Single(state.DoString("return display(value)")).Read<string>());
    }

    [Fact]
    public void BoundedDisplayStringCapsManagedUtf8AndPreservesStack()
    {
        const string expected = "A🐺東京";
        using var state = TestBudgets.CreateRoot();
        state.OpenBaseLibrary();
        state.OpenStringLibrary();
        using var value = Assert.Single(state.DoString(
            $$"""
            return setmetatable({}, {
                __tostring = function()
                    return "{{expected}}" .. string.rep("🐺", 4096)
                end,
            })
            """,
            "@display/bounded-utf8.luau")).Read<LuauTable>();
        state["value"] = value;
        state["display"] = state.CreateFunction("display", context =>
        {
            var bounded = context.ToDisplayString(0, context.Read<int>(1), out var wasTruncated);
            context.Return(bounded);
            context.Return(wasTruncated);
        });

        var results = state.DoString(
            "local a, at = display(value, 5); local b, bt = display(value, 0); return a, at, b, bt");
        var bounded = results[0].Read<string>();
        var wasTruncated = results[1].Read<bool>();

        Assert.Equal("A🐺", bounded);
        Assert.True(wasTruncated);
        Assert.Equal(5, Encoding.UTF8.GetByteCount(bounded));
        Assert.Equal(string.Empty, results[2].Read<string>());
        Assert.True(results[3].Read<bool>());
    }

    [Fact]
    public void ThrowingDisplayMetamethodRestoresStackAndLeavesVmReusable()
    {
        using var state = TestBudgets.CreateRoot();
        state.OpenBaseLibrary();
        using var value = Assert.Single(state.DoString(
            """
            return setmetatable({}, {
                __tostring = function()
                    error("display boom")
                end,
            })
            """,
            "@display/throwing.luau")).Read<LuauTable>();
        state["value"] = value;
        state["display"] = state.CreateFunction("display", context =>
            context.Return(context.ToDisplayString(0, 1_024, out _)));

        var exception = Assert.Throws<LuauManagedCallbackException>(() =>
            state.DoString("return display(value)"));
        Assert.Contains("display boom", exception.InnerException!.Message, StringComparison.Ordinal);

        var results = state.DoString("return 21 * 2", "@display/recovered.luau");
        Assert.Single(results);
        Assert.Equal(42, results[0].Read<int>());
    }

    [Fact]
    public void ReferenceToStringDoesNotInvokeUntrustedMetamethods()
    {
        using var state = TestBudgets.CreateRoot();
        state.OpenBaseLibrary();
        using var value = Assert.Single(state.DoString(
            "return setmetatable({}, { __tostring = function() error('must not run') end })",
            "@display/reference-safe.luau")).Read<LuauTable>();

        var text = value.ToString();

        Assert.StartsWith("table: 0x", text, StringComparison.Ordinal);
        Assert.Equal(7, Assert.Single(state.DoString("return 7")).Read<int>());
    }

    [Fact]
    public void InternalPushNilGrowsTheNativeStackSafely()
    {
        using var state = TestBudgets.CreateRoot();

        for (var index = 0; index < 512; index++)
        {
            state.PushNil();
        }

        Assert.Equal(512, state.GetTop());
        state.SetTop(0);
        state.PushNumber(42);
        Assert.Equal(42, state.Pop().Read<int>());
        Assert.Equal(0, state.GetTop());
    }

}
