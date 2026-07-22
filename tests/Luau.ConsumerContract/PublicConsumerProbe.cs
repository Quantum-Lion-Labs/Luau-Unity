namespace Luau.ConsumerContract;

/// <summary>
/// Compiles as an ordinary consumer assembly with no InternalsVisibleTo grant.
/// These methods intentionally exist to make preview overload and extension
/// contracts fail the repository build if they stop being publicly usable.
/// </summary>
public static class PublicConsumerProbe
{
    public static (LuauResultScope Synchronous, ValueTask<LuauResultScope> Asynchronous) BindResumeScopes(
        LuauState coroutine,
        LuauValue[] arguments)
    {
        LuauResultScope synchronous = coroutine.Resume(arguments);
        ValueTask<LuauResultScope> asynchronous = coroutine.ResumeAsync(arguments);
        return (synchronous, asynchronous);
    }

    public static LuauCompileResult[] CreateEveryCompilationResult(
        LuauCompilerOutput output,
        LuauCompilationException diagnostic,
        Exception infrastructureFailure)
    {
        return
        [
            LuauCompileResult.Success(output),
            LuauCompileResult.Diagnostic(diagnostic),
            LuauCompileResult.Canceled(),
            LuauCompileResult.InfrastructureFailure(infrastructureFailure),
        ];
    }

    public static LuauObjectHandle CreateGeneratedCapability<T>(LuauState state, T target)
        where T : class, ILuauObjectCapability
    {
        return state.CreateHandle(target);
    }

    public static async ValueTask<int> RunScriptInstanceAsync(
        LuauState sandboxedRoot,
        CancellationToken cancellationToken = default)
    {
        using var instance = await LuauScriptInstance.CreateAsync(
            sandboxedRoot,
            "consumer-probe",
            static (thread, token) => thread.DoStringAsync(
                "return { value = function(input) return input end, tick = function() end }",
                "@consumer/script-instance.luau",
                cancellationToken: token),
            cancellationToken: cancellationToken);

        LuauScriptEntrypoint value = instance.GetRequiredEntrypoint("value");
        _ = instance.TryGetEntrypoint("tick", out LuauScriptEntrypoint? tick);

        using LuauResultScope allocated = value.Invoke(40);
        var destination = new LuauValue[1];
        _ = value.InvokeInto((LuauValue)41, destination);
        _ = await value.InvokeIntoAsync(
            (LuauValue)42,
            destination,
            cancellationToken);
        tick?.InvokeVoid();
        if (tick != null)
        {
            await tick.InvokeVoidAsync(cancellationToken);
        }

        return allocated[0].Read<int>() + destination[0].Read<int>();
    }
}

public sealed class PublicCompilationService : ILuauCompilationService
{
    public ValueTask<LuauCompileResult> CompileAsync(
        ReadOnlyMemory<byte> utf8Source,
        LuauCompileOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(LuauCompileResult.Canceled());
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class ExternalAssemblyContractTests
{
    [Fact]
    public void PublicContractProbeCompilesWithoutFriendAssemblyAccess()
    {
        Assert.NotNull(typeof(PublicConsumerProbe));
        Assert.NotNull(typeof(PublicCompilationService));
    }
}
