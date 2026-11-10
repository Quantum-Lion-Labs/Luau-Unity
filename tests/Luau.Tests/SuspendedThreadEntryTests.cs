using System.Diagnostics.CodeAnalysis;

namespace Luau.Tests;

/// <summary>
/// A suspended thread has a pending continuation. Starting a new call on it
/// used to resume that continuation instead and drop the new chunk, so the
/// script, which decides when to yield, decided what the host's next call did.
/// </summary>
public sealed class SuspendedThreadEntryTests
{
    static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    const string NewChunk = "mark() return 'B'";
    const string NewChunkName = "@entry/new.luau";
    static readonly byte[] NewChunkUtf8 = "mark() return 'B'"u8.ToArray();
    static readonly byte[] NewChunkNameUtf8 = "@entry/new.luau"u8.ToArray();

    public enum SuspendedKind
    {
        HostThreadYielded,
        ScriptCoroutineYielded,
        ScriptCoroutineNotStarted,
    }

    static readonly Dictionary<string, Func<Fixture, Task>> NewCallEntryPoints = new()
    {
        ["DoString(char)"] = fixture => Sync(() =>
            fixture.Thread.DoString(NewChunk.AsSpan(), NewChunkName.AsSpan()).Dispose()),
        ["DoString(utf8)"] = fixture => Sync(() =>
            fixture.Thread.DoString(NewChunkUtf8.AsSpan(), NewChunkNameUtf8.AsSpan()).Dispose()),
        ["DoStringInto(char)"] = fixture => Sync(() =>
            fixture.Thread.DoStringInto(NewChunk.AsSpan(), new LuauValue[4], NewChunkName.AsSpan())),
        ["DoStringInto(utf8)"] = fixture => Sync(() =>
            fixture.Thread.DoStringInto(NewChunkUtf8.AsSpan(), new LuauValue[4], NewChunkNameUtf8.AsSpan())),
        ["DoStringAsync(string)"] = async fixture =>
            (await fixture.Thread.DoStringAsync(NewChunk, NewChunkName)).Dispose(),
        ["DoStringAsync(char memory)"] = async fixture =>
            (await fixture.Thread.DoStringAsync(NewChunk.AsMemory(), NewChunkName.AsMemory())).Dispose(),
        ["DoStringAsync(utf8 memory)"] = async fixture =>
            (await fixture.Thread.DoStringAsync(
                (ReadOnlyMemory<byte>)NewChunkUtf8,
                (ReadOnlyMemory<byte>)NewChunkNameUtf8)).Dispose(),
        ["DoStringIntoAsync(string)"] = async fixture =>
            await fixture.Thread.DoStringIntoAsync(NewChunk, new LuauValue[4], NewChunkName),
        ["DoStringIntoAsync(char memory)"] = async fixture =>
            await fixture.Thread.DoStringIntoAsync(NewChunk.AsMemory(), new LuauValue[4], NewChunkName.AsMemory()),
        ["DoStringIntoAsync(utf8 memory)"] = async fixture =>
            await fixture.Thread.DoStringIntoAsync(
                (ReadOnlyMemory<byte>)NewChunkUtf8,
                new LuauValue[4],
                (ReadOnlyMemory<byte>)NewChunkNameUtf8),
        ["ExecuteCompilerOutput"] = fixture => Sync(() =>
            fixture.Thread.ExecuteCompilerOutput(fixture.Output, NewChunkName).Dispose()),
        ["ExecuteCompilerOutputInto"] = fixture => Sync(() =>
            fixture.Thread.ExecuteCompilerOutputInto(fixture.Output, new LuauValue[4], NewChunkName)),
        ["ExecuteCompilerOutputAsync"] = async fixture =>
            (await fixture.Thread.ExecuteCompilerOutputAsync(fixture.Output, NewChunkName.AsMemory())).Dispose(),
        ["ExecuteCompilerOutputIntoAsync"] = async fixture =>
            await fixture.Thread.ExecuteCompilerOutputIntoAsync(
                fixture.Output,
                new LuauValue[4],
                NewChunkName.AsMemory()),
        ["ExecuteVerifiedBytecode"] = fixture => Sync(() =>
            fixture.Thread.ExecuteVerifiedBytecode(fixture.Artifact, NewChunkName).Dispose()),
        ["ExecuteVerifiedBytecodeInto"] = fixture => Sync(() =>
            fixture.Thread.ExecuteVerifiedBytecodeInto(fixture.Artifact, new LuauValue[4], NewChunkName)),
        ["ExecuteVerifiedBytecodeAsync"] = async fixture =>
            (await fixture.Thread.ExecuteVerifiedBytecodeAsync(fixture.Artifact, NewChunkName.AsMemory())).Dispose(),
        ["ExecuteVerifiedBytecodeIntoAsync"] = async fixture =>
            await fixture.Thread.ExecuteVerifiedBytecodeIntoAsync(
                fixture.Artifact,
                new LuauValue[4],
                NewChunkName.AsMemory()),
    };

    public static TheoryData<string, SuspendedKind> EntryPointsAndSuspendedKinds()
    {
        var data = new TheoryData<string, SuspendedKind>();
        foreach (var entryPoint in NewCallEntryPoints.Keys)
        {
            foreach (var kind in Enum.GetValues<SuspendedKind>())
            {
                data.Add(entryPoint, kind);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EntryPointsAndSuspendedKinds))]
    public async Task NewCallOnSuspendedThreadIsRejectedAndTheOriginalContinuationStillResumes(
        string entryPoint,
        SuspendedKind kind)
    {
        using var fixture = Fixture.Create(kind);
        var topBefore = fixture.Thread.GetTop();
        Assert.Equal(LuauThreadStatus.Suspended, fixture.Thread.GetStatus());
        Assert.Equal("suspended", fixture.ScriptStatus());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewCallEntryPoints[entryPoint](fixture));

        Assert.Contains("suspended", exception.Message, StringComparison.Ordinal);
        Assert.Contains(NewChunkName, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Marks);
        Assert.Equal(LuauThreadStatus.Suspended, fixture.Thread.GetStatus());
        Assert.Equal(topBefore, fixture.Thread.GetTop());
        Assert.Equal("suspended", fixture.ScriptStatus());

        using (var resumed = fixture.Thread.Resume())
        {
            Assert.Equal("A", Assert.Single(resumed).Read<string>());
        }

        Assert.Equal(LuauThreadStatus.Dead, fixture.Thread.GetStatus());
        Assert.Equal("dead", fixture.ScriptStatus());
        Assert.Equal(0, fixture.Marks);

        // The finished thread and its root are reusable.
        await NewCallEntryPoints[entryPoint](fixture);
        Assert.Equal(1, fixture.Marks);
        using var rootResult = fixture.Root.DoString("return 1");
        Assert.Equal(1, Assert.Single(rootResult).Read<int>());
    }

    [Fact]
    public void ReportedReproductionRunsTheNewChunkOnlyAfterTheThreadFinishes()
    {
        using var root = LuauState.Create();
        root.OpenBaseLibrary();
        root.OpenCoroutineLibrary();
        using var thread = root.CreateThread();

        using (var first = thread.DoString("coroutine.yield(1) return 'A'"))
        {
            Assert.Equal(1, Assert.Single(first).Read<int>());
        }

        var exception = Assert.Throws<InvalidOperationException>(() => thread.DoString("return 'B'"));
        Assert.Contains("suspended", exception.Message, StringComparison.Ordinal);
        Assert.Equal(LuauThreadStatus.Suspended, thread.GetStatus());

        using (var resumed = thread.Resume())
        {
            Assert.Equal("A", Assert.Single(resumed).Read<string>());
        }

        using var second = thread.DoString("return 'B'");
        Assert.Equal("B", Assert.Single(second).Read<string>());
        Assert.Equal(LuauThreadStatus.Dead, thread.GetStatus());
    }

    [Fact]
    public void ScriptCanStillResumeItsCoroutineAfterAHostCallIsRejected()
    {
        using var fixture = Fixture.Create(SuspendedKind.ScriptCoroutineYielded);

        Assert.Throws<InvalidOperationException>(() => fixture.Thread.DoString(NewChunk));

        using var resumed = fixture.Root.DoString(
            "local ok, value = coroutine.resume(co) return ok, value, coroutine.status(co)");
        Assert.True(resumed[0].Read<bool>());
        Assert.Equal("A", resumed[1].Read<string>());
        Assert.Equal("dead", resumed[2].Read<string>());
        Assert.Equal(0, fixture.Marks);
    }

    [Fact]
    public void ResumeArgumentsReachTheOriginalContinuationAfterARejection()
    {
        using var root = LuauState.Create();
        root.OpenBaseLibrary();
        root.OpenCoroutineLibrary();
        using var thread = root.CreateThread();
        using (thread.DoString("local value = coroutine.yield() return value * 2"))
        {
        }

        Assert.Throws<InvalidOperationException>(() => thread.DoString("return 0"));

        using var resumed = thread.Resume(new LuauValue[] { 21d });
        Assert.Equal(42, Assert.Single(resumed).Read<int>());
    }

    [Fact]
    public void FinishedAndFailedThreadsStillAcceptNewCalls()
    {
        using var fixture = Fixture.Create(SuspendedKind.HostThreadYielded);
        using (fixture.Thread.Resume())
        {
        }

        using var fresh = fixture.Root.CreateThread();
        RunNewChunk(fresh);

        // Dead after finishing a resumed continuation.
        RunNewChunk(fixture.Thread);
        Assert.Equal(LuauThreadStatus.Dead, fixture.Thread.GetStatus());

        // Dead after a failed call, which resets the thread.
        using var failed = fixture.Root.CreateThread();
        Assert.Throws<LuauException>(() => failed.DoString("error('expected failure')"));
        Assert.Equal(LuauThreadStatus.Dead, failed.GetStatus());
        RunNewChunk(failed);

        // Dead after a failed resume of a yielded continuation.
        using var failedResume = fixture.Root.CreateThread();
        using (failedResume.DoString("coroutine.yield() error('expected failure')"))
        {
        }

        Assert.Throws<LuauException>(() => failedResume.Resume());
        Assert.Equal(LuauThreadStatus.Dead, failedResume.GetStatus());
        RunNewChunk(failedResume);

        // A script coroutine that finished normally has nothing pending.
        using var finishedResults = fixture.Root.DoString(
            "local finished = coroutine.create(function() return 1 end) " +
            "assert(coroutine.resume(finished)) return finished");
        var finished = finishedResults.Read<LuauState>(0);
        using (finished)
        {
            RunNewChunk(finished);
        }

        Assert.Equal(5, fixture.Marks);

        void RunNewChunk(LuauState thread)
        {
            using var results = thread.DoString(NewChunk, NewChunkName);
            Assert.Equal("B", Assert.Single(results).Read<string>());
        }
    }

    [Fact]
    public void ScriptCoroutineThatDiedWithAnErrorFailsTheNextHostCallAndIsThenReusable()
    {
        // Unchanged behavior: the host call reaches the VM, which refuses to
        // resume a coroutine that died with an error; the failure resets the
        // thread, so the following call runs.
        using var fixture = Fixture.Create(SuspendedKind.HostThreadYielded);
        using var erroredResults = fixture.Root.DoString(
            "local errored = coroutine.create(function() error('boom') end) " +
            "assert(not coroutine.resume(errored)) return errored");
        var errored = erroredResults.Read<LuauState>(0);
        using (errored)
        {
            var failure = Assert.Throws<LuauException>(() => errored.DoString(NewChunk, NewChunkName));
            Assert.IsType<LuauException>(failure);
            Assert.Contains("non-suspended coroutine", failure.Message, StringComparison.Ordinal);
            Assert.Equal(0, fixture.Marks);

            using var results = errored.DoString(NewChunk, NewChunkName);
            Assert.Equal("B", Assert.Single(results).Read<string>());
            Assert.Equal(1, fixture.Marks);
        }
    }

    [Fact]
    public async Task ThreadSuspendedByAPendingAsyncCallbackIsReportedAsExecuting()
    {
        using var root = LuauState.Create(LuauStateOptions.UnboundedResources);
        root.OpenBaseLibrary();
        root.OpenCoroutineLibrary();
        using var thread = root.CreateThread();
        using var other = root.CreateThread();
        var entered = NewSignal();
        var release = NewSignal();
        Exception? fromCallback = null;
        using var pending = root.CreateAsyncFunction(async context =>
        {
            // The VM has suspended this thread for the callback; the
            // callback's own flow may use the state but not start a call.
            fromCallback = Record.Exception(() => context.State.DoString("return 'B'").Dispose());
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        });
        root["pending"] = pending;

        var running = thread.DoStringAsync("pending() return 'A'", "@entry/pending.luau").AsTask();
        try
        {
            await entered.Task.WaitAsync(TestTimeout);

            var callbackFailure = Assert.IsType<InvalidOperationException>(fromCallback);
            Assert.Contains("already executing", callbackFailure.Message, StringComparison.Ordinal);

            AssertExecuting(Assert.Throws<InvalidOperationException>(() => thread.DoString("return 'B'")));
            AssertExecuting(await Assert.ThrowsAsync<InvalidOperationException>(
                () => thread.DoStringAsync("return 'B'").AsTask()));
            AssertExecuting(Assert.Throws<InvalidOperationException>(() => other.DoString("return 'B'")));
        }
        finally
        {
            release.TrySetResult();
        }

        using (var result = await running.WaitAsync(TestTimeout))
        {
            Assert.Equal("A", Assert.Single(result).Read<string>());
        }

        using var next = thread.DoString("return 'B'");
        Assert.Equal("B", Assert.Single(next).Read<string>());

        static void AssertExecuting(InvalidOperationException exception)
        {
            Assert.Contains("executing", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("suspended", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FunctionInvocationRunsOnTheRootWhileAChildIsSuspended()
    {
        using var fixture = Fixture.Create(SuspendedKind.HostThreadYielded);
        using var thread = fixture.Root.CreateThread();
        using var yielded = thread.DoString(
            "coroutine.yield(function() mark() return 'F' end, function() mark() end) return 'A'");
        var function = yielded.Read<LuauFunction>(0);
        var voidFunction = yielded.Read<LuauFunction>(1);
        Assert.Equal(LuauThreadStatus.Suspended, thread.GetStatus());

        using (var invoked = function.Invoke())
        {
            Assert.Equal("F", Assert.Single(invoked).Read<string>());
        }

        voidFunction.InvokeVoid();
        Assert.Equal(2, fixture.Marks);
        Assert.Equal(LuauThreadStatus.Suspended, thread.GetStatus());

        using var resumed = thread.Resume();
        Assert.Equal("A", Assert.Single(resumed).Read<string>());
    }

    [Fact]
    public async Task ScriptInstanceLoaderCannotStartASecondCallOnItsSuspendedThread()
    {
        using var root = LuauState.Create(LuauStateOptions.UnboundedResources);
        root.OpenBaseLibrary();
        root.OpenCoroutineLibrary();
        root.SandboxRoot();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await LuauScriptInstance.CreateAsync(
                root,
                "yielding-loader",
                async (thread, cancellationToken) =>
                {
                    using (await thread.DoStringAsync(
                        "coroutine.yield() return {}",
                        cancellationToken: cancellationToken))
                    {
                    }

                    return await thread.DoStringAsync(
                        "return {}",
                        "@entry/second.luau",
                        cancellationToken: cancellationToken);
                }));

        Assert.Contains("suspended", exception.Message, StringComparison.Ordinal);
        using var rootResult = root.DoString("return 1");
        Assert.Equal(1, Assert.Single(rootResult).Read<int>());
    }

    [Fact]
    public void SandboxThreadRejectsASuspendedThreadWithoutChangingIt()
    {
        using var root = LuauState.Create(LuauStateOptions.UnboundedResources);
        root.OpenBaseLibrary();
        root.OpenCoroutineLibrary();
        root.SandboxRoot();
        using var thread = root.CreateThread();
        using (thread.DoString("coroutine.yield() return 'A'"))
        {
        }

        var exception = Assert.Throws<InvalidOperationException>(() => thread.SandboxThread());

        Assert.Contains("suspended", exception.Message, StringComparison.Ordinal);
        using (var resumed = thread.Resume())
        {
            Assert.Equal("A", Assert.Single(resumed).Read<string>());
        }

        // The rejected call installed no thread sandbox: the thread still
        // shares the read-only root globals until it is sandboxed.
        Assert.Throws<LuauException>(() => thread.DoString("written = true"));
        thread.SandboxThread();
        using var written = thread.DoString("written = true return written");
        Assert.True(Assert.Single(written).Read<bool>());
    }

    [Fact]
    public void ModuleExecutionRejectsASuspendedThreadInsideAnActiveOperation()
    {
        using var root = LuauState.Create(LuauStateOptions.UnboundedResources);
        root.OpenBaseLibrary();
        root.OpenCoroutineLibrary();
        using var thread = root.CreateThread();
        using (thread.DoString("coroutine.yield() return 'A'"))
        {
        }

        root.OpenRequireLibrary(new CapturedThreadRequirer(thread));

        var failure = Assert.Throws<LuauManagedCallbackException>(
            () => root.DoString("return require('module')"));

        var cause = Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.Contains("suspended", cause.Message, StringComparison.Ordinal);
        Assert.Equal(LuauThreadStatus.Suspended, thread.GetStatus());
        using var resumed = thread.Resume();
        Assert.Equal("A", Assert.Single(resumed).Read<string>());
    }

    static Task Sync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    sealed class Counter
    {
        public int Value;
    }

    sealed class Fixture : IDisposable
    {
        readonly LuauFunction mark;
        readonly Counter marks;

        Fixture(LuauState root, LuauState thread, LuauFunction mark, Counter marks)
        {
            Root = root;
            Thread = thread;
            this.mark = mark;
            this.marks = marks;
            Output = LuauCompiler.Compile(NewChunkUtf8);
            Artifact = LuauBytecodeArtifact.Create(Output, NewChunkName, "tests/suspended-thread");
        }

        public LuauState Root { get; }
        public LuauState Thread { get; }
        public LuauCompilerOutput Output { get; }
        public LuauBytecodeArtifact Artifact { get; }
        public int Marks => Volatile.Read(ref marks.Value);

        public static Fixture Create(SuspendedKind kind)
        {
            var root = LuauState.Create(LuauStateOptions.UnboundedResources with
            {
                BytecodePolicy = LuauBytecodePolicy.RequireValidator,
                BytecodeValidator = new AcceptingValidator(),
            });
            LuauFunction? mark = null;
            LuauState? thread = null;
            try
            {
                root.OpenBaseLibrary();
                root.OpenCoroutineLibrary();
                var marks = new Counter();
                mark = root.CreateFunction(_ => Interlocked.Increment(ref marks.Value));
                root["mark"] = mark;
                thread = CreateSuspendedThread(root, kind);
                return new Fixture(root, thread, mark, marks);
            }
            catch
            {
                thread?.Dispose();
                mark?.Dispose();
                root.Dispose();
                throw;
            }
        }

        static LuauState CreateSuspendedThread(LuauState root, SuspendedKind kind)
        {
            switch (kind)
            {
                case SuspendedKind.HostThreadYielded:
                    var thread = root.CreateThread();
                    using (var first = thread.DoString("coroutine.yield(1) return 'A'"))
                    {
                        Assert.Equal(1, Assert.Single(first).Read<int>());
                    }

                    root["co"] = thread;
                    return thread;
                case SuspendedKind.ScriptCoroutineYielded:
                    using (var created = root.DoString(
                        "co = coroutine.create(function() coroutine.yield(1) return 'A' end) " +
                        "assert(coroutine.resume(co)) return co"))
                    {
                        return created.Read<LuauState>(0);
                    }
                case SuspendedKind.ScriptCoroutineNotStarted:
                    using (var created = root.DoString(
                        "co = coroutine.create(function() return 'A' end) return co"))
                    {
                        return created.Read<LuauState>(0);
                    }
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        public string ScriptStatus()
        {
            using var status = Root.DoString("return coroutine.status(co)");
            return Assert.Single(status).Read<string>();
        }

        public void Dispose()
        {
            Thread.Dispose();
            mark.Dispose();
            Root.Dispose();
        }
    }

    sealed class AcceptingValidator : ILuauBytecodeValidator
    {
        public bool IsValid(LuauBytecodeArtifact artifact, ReadOnlySpan<byte> bytecode) => true;
    }

    sealed class CapturedThreadRequirer(LuauState target) : LuauRequirer
    {
        protected override bool TryLoadModule(
            LuauState state,
            string fullPath,
            string requireArgument,
            out LuauValue result)
        {
            result = ExecuteModuleSource(target, requireArgument, "return 'module'"u8);
            return true;
        }

        protected override bool TryGetAliasPath(string alias, [NotNullWhen(true)] out string? path)
        {
            path = null;
            return false;
        }
    }
}
