using System.Text;

namespace Luau.Tests;

public sealed class ManagedHostRegressionTests
{
    [Fact]
    public void HostMemoryByteCountsSaturateForManagedDiagnostics()
    {
        Assert.Equal(long.MaxValue, LuauVmContext.ToDiagnosticByteCount(ulong.MaxValue));
        Assert.Equal(123, LuauVmContext.ToDiagnosticByteCount(123));
    }

    [Fact]
    public void QuotaLimitedRootCreationFailureIsTypedAndDoesNotPoisonLaterCreation()
    {
        var exception = Assert.Throws<LuauMemoryLimitException>(() => LuauState.Create(
            new LuauStateOptions
            {
                MemoryLimitBytes = 1,
                BytecodePolicy = LuauBytecodePolicy.Reject,
            }));

        Assert.Equal(1, exception.LimitBytes);
        Assert.Equal(1, exception.Usage.LimitBytes);
        Assert.True(exception.AttemptedBytes > exception.LimitBytes);

        using var recovered = TestBudgets.CreateRoot();
        Assert.Equal(42, Assert.Single(recovered.DoString("return 42")).Read<int>());
    }

    [Fact]
    public void StandaloneCompilerOwnedBuffersRemainValidAcrossRepeatedCompileAndLoad()
    {
        LuauCompilerOutput? output = null;
        for (var iteration = 0; iteration < 64; iteration++)
        {
            output = LuauCompiler.Compile(Encoding.UTF8.GetBytes($"return {iteration}"));
            Assert.True(output.BytecodeLength > 0);
        }

        using var state = TestBudgets.CreateRoot();
        var results = state.ExecuteCompilerOutput(
            output!,
            "@luau-host/compiler-buffer.luau");

        Assert.Equal(63, Assert.Single(results).Read<int>());
    }
}
