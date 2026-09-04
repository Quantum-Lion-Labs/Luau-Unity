using System.Runtime.CompilerServices;

namespace Luau.Tests;

public sealed class CallbackRegressionTests
{
    [Fact]
    public void SandboxedGeneratedPropertiesAreReadAtExecutionTime()
    {
        using var root = LuauState.Create();
        root.OpenBaseLibrary();
        var library = new CallbackRegressionLibrary();
        root.OpenLibrary(library);
        root.SandboxRoot();
        using var child = root.CreateSandboxedThread();
        using var result = child.DoString("regression.value = 7; regression.field = 8; return regression.value, regression.field");
        Assert.Equal(7, result[0].Read<int>());
        Assert.Equal(1, library.GetterCalls);
        Assert.Equal(8, result[1].Read<int>());
        library.Value = 19;
        using var next = child.DoString("return regression.value");
        Assert.Equal(19, next[0].Read<int>());
        Assert.Equal(2, library.GetterCalls);
    }

    [Theory]
    [InlineData("return host(42)")]
    [InlineData("local co = coroutine.create(function() return host(42) end); local ok, value = coroutine.resume(co); assert(ok); assert(coroutine.status(co) == 'dead'); return value")]
    [InlineData("return coroutine.wrap(function() return host(42) end)()")]
    [InlineData("return coroutine.wrap(function() local co = coroutine.create(function() return host(host(41)) end); local ok, value = coroutine.resume(co); assert(ok); assert(coroutine.status(co) == 'dead'); return value end)()")]
    public async Task AsyncCallbacksKeepTheirCoroutineArgumentsAndResults(string source)
    {
        using var root = CreateRoot();
        using var host = root.CreateAsyncFunction("host", async context =>
        {
            Assert.Equal(1, context.ArgumentCount);
            await Task.Yield();
            context.Return(context.Read<int>(0) + 1);
        });
        root["host"] = host;
        using var result = await root.DoStringAsync(source).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(43, Assert.Single(result).Read<int>());
    }

    [Fact]
    public async Task ScriptYieldsStillReachCoroutineResumeAfterHostSuspension()
    {
        using var root = CreateRoot();
        using var host = root.CreateAsyncFunction("host", async context =>
        {
            await Task.Yield();
            context.Return(context.Read<int>(0) + 1);
        });
        root["host"] = host;
        using var result = await root.DoStringAsync("""
            local co = coroutine.create(function()
                local value = coroutine.yield(host(41))
                return host(value)
            end)
            local ok, first = coroutine.resume(co)
            assert(ok and coroutine.status(co) == 'suspended')
            local ok2, second = coroutine.resume(co, first)
            return ok2, first, second, coroutine.status(co)
            """).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result[0].Read<bool>());
        Assert.Equal(42, result[1].Read<int>());
        Assert.Equal(43, result[2].Read<int>());
        Assert.Equal("dead", result[3].Read<string>());
    }

    [Theory]
    [InlineData("local co = coroutine.create(function() return host() end); return coroutine.resume(co)")]
    [InlineData("return pcall(coroutine.wrap(function() return host() end))")]
    [InlineData("local co = coroutine.create(function() return pcall(host) end); local ok, caught, failure = coroutine.resume(co); assert(ok); return caught, failure")]
    public async Task NestedCallbackErrorsReachTheCorrectHandler(string source)
    {
        using var root = CreateRoot();
        using var host = root.CreateAsyncFunction("host", async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("expected callback failure");
        });
        root["host"] = host;
        using var result = await root.DoStringAsync(source).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(result[0].Read<bool>());
        Assert.Equal(2, result.Length);
        using var recovered = root.DoString("return 17");
        Assert.Equal(17, recovered[0].Read<int>());
    }

    [Fact]
    public async Task NestedWrapFailurePreservesTheManagedException()
    {
        using var root = CreateRoot();
        var failure = new InvalidOperationException("expected callback failure");
        using var host = root.CreateAsyncFunction("host", async _ =>
        {
            await Task.Yield();
            throw failure;
        });
        root["host"] = host;
        var exception = await Assert.ThrowsAsync<LuauManagedCallbackException>(() => root.DoStringAsync(
            "return coroutine.wrap(function() return coroutine.wrap(function() return host() end)() end)()")
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Same(failure, exception.InnerException);
    }

    [Fact]
    public async Task CancellationResetsTheWholeSuspendedCoroutineChain()
    {
        using var root = CreateRoot();
        using var cancellation = new CancellationTokenSource();
        using var host = root.CreateAsyncFunction("host", async context =>
        {
            await Task.Yield();
            cancellation.Cancel();
            context.CancellationToken.ThrowIfCancellationRequested();
        });
        root["host"] = host;
        await Assert.ThrowsAsync<LuauExecutionCanceledException>(() => root.DoStringAsync("""
            co = coroutine.create(function() return host(42) end)
            return coroutine.wrap(function() return coroutine.resume(co) end)()
            """, cancellationToken: cancellation.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        using var result = root.DoString("return coroutine.status(co), 17");
        Assert.Equal("dead", result[0].Read<string>());
        Assert.Equal(17, result[1].Read<int>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbandonedBindingsDoNotRootTheVm(bool capability)
    {
        var (root, context) = AbandonBinding(capability);
        for (var i = 0; i < 12 && (root.IsAlive || context.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(root.IsAlive);
        Assert.False(context.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static (WeakReference Root, WeakReference Context) AbandonBinding(bool capability)
    {
        var root = LuauState.Create();
        var library = new CallbackRegressionLibrary();
        if (capability)
        {
            var descriptor = new LuauObjectDescriptor<CallbackRegressionLibrary>("regression", null,
                new[] { LuauObjectMember<CallbackRegressionLibrary>.Property("value", (target, call) => call.Return(target.Value), null) });
            using var handle = root.CreateHandle(library, descriptor);
            root["target"] = handle;
        }
        else
        {
            root.OpenLibrary(library);
        }
        return (new WeakReference(root), new WeakReference(root.Context));
    }

    static LuauState CreateRoot()
    {
        var root = LuauState.Create();
        root.OpenBaseLibrary();
        root.OpenCoroutineLibrary();
        return root;
    }
}

[LuauLibrary("regression")]
sealed partial class CallbackRegressionLibrary
{
    int value = 10;
    public int GetterCalls;
    [LuauMember("field")] public int Field = 10;
    [LuauMember("value")]
    public int Value { get { GetterCalls++; return value; } set { this.value = value; } }
    [LuauMember("noop")] public void Noop() { }
}
