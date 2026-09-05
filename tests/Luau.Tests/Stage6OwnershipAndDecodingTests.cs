using System.Text;
using Luau.Internal;

namespace Luau.Tests;

public sealed class Stage6OwnershipAndDecodingTests
{
    [Fact]
    public void DecodedStringLimitAcceptsExactBytesAndRejectsOneOverBeforeRecovery()
    {
        using var exactState = LuauState.Create(new LuauStateOptions
        {
            MaxDecodedStringBytes = 4,
            MaxDecodedBytesPerOperation = 16,
        });
        using (var exact = exactState.DoString("return 'test'", "@decode/exact.luau"))
        {
            Assert.Equal("test", exact.Read<string>(0));
        }

        using var limitedState = LuauState.Create(new LuauStateOptions
        {
            MaxDecodedStringBytes = 3,
            MaxDecodedBytesPerOperation = 16,
        });
        var exception = Assert.Throws<LuauDecodedResultLimitException>(
            () => limitedState.DoString("return 'test'", "@decode/over.luau"));

        Assert.Equal(LuauDecodedResultLimitKind.StringBytes, exception.LimitKind);
        Assert.Equal(4, exception.ActualBytes);
        Assert.Equal(3, exception.LimitBytes);
        Assert.Equal("@decode/over.luau", exception.ChunkName);
        Assert.Equal(42, Assert.Single(limitedState.DoString("return 40 + 2")).Read<int>());
    }

    [Fact]
    public void AggregateDecodedLimitRejectsManyIndividuallyAdmittedStrings()
    {
        using var state = LuauState.Create(new LuauStateOptions
        {
            MaxDecodedStringBytes = 4,
            MaxDecodedBytesPerOperation = 7,
        });

        var exception = Assert.Throws<LuauDecodedResultLimitException>(
            () => state.DoString("return 'abcd', 'efgh'", "@decode/aggregate.luau"));

        Assert.Equal(LuauDecodedResultLimitKind.OperationBytes, exception.LimitKind);
        Assert.Equal(8, exception.ActualBytes);
        Assert.Equal(7, exception.LimitBytes);
        Assert.Equal(3, Assert.Single(state.DoString("return 3")).Read<int>());
    }

    [Fact]
    public void ExplicitDisplayLimitIsCappedByRootAndChargedToAggregateBudget()
    {
        using var cappedState = LuauState.Create(new LuauStateOptions
        {
            MaxDecodedStringBytes = 3,
            MaxDecodedBytesPerOperation = 32,
        });
        using var display = cappedState.CreateFunction("display", context =>
        {
            var formatted = context.ToDisplayString(0, int.MaxValue, out var truncated);
            context.Return(formatted);
            context.Return(truncated);
        });
        cappedState["display"] = display;
        using (var result = cappedState.DoString("return display('test')"))
        {
            Assert.Equal("tes", result.Read<string>(0));
            Assert.True(result.Read<bool>(1));
        }

        using var aggregateState = LuauState.Create(new LuauStateOptions
        {
            MaxDecodedStringBytes = 4,
            MaxDecodedBytesPerOperation = 7,
        });
        using var twice = aggregateState.CreateFunction("displayTwice", context =>
        {
            _ = context.ToDisplayString(0, int.MaxValue, out _);
            _ = context.ToDisplayString(1, int.MaxValue, out _);
            context.Return(true);
        });
        aggregateState["displayTwice"] = twice;

        var callbackFailure = Assert.Throws<LuauManagedCallbackException>(
            () => aggregateState.DoString("return displayTwice('abcd', 'efgh')"));
        var limit = Assert.IsType<LuauDecodedResultLimitException>(callbackFailure.InnerException);
        Assert.Equal(LuauDecodedResultLimitKind.OperationBytes, limit.LimitKind);
        Assert.Equal(8, limit.ActualBytes);
        Assert.Equal(7, limit.LimitBytes);
    }

    [Fact]
    public void GlobalsRawTableReadsAndEnumerationUseDecodedOperationBoundaries()
    {
        using var state = LuauState.Create(new LuauStateOptions
        {
            MaxDecodedStringBytes = 8,
            MaxDecodedBytesPerOperation = 3,
        });
        state["globalValue"] = "test";
        var globalFailure = Assert.Throws<LuauDecodedResultLimitException>(
            () => _ = state["globalValue"]);
        Assert.Equal(LuauDecodedResultLimitKind.OperationBytes, globalFailure.LimitKind);

        using var table = state.CreateTable();
        table.RawSet("key", "test");
        var rawFailure = Assert.Throws<LuauDecodedResultLimitException>(
            () => table.RawGet("key"));
        Assert.Equal(LuauDecodedResultLimitKind.OperationBytes, rawFailure.LimitKind);

        using var enumerator = table.GetEnumerator();
        var enumerationFailure = Assert.Throws<LuauDecodedResultLimitException>(
            () => enumerator.MoveNext());
        Assert.Equal(LuauDecodedResultLimitKind.OperationBytes, enumerationFailure.LimitKind);
        Assert.Equal(11, Assert.Single(state.DoString("return 11")).Read<int>());
    }

    [Fact]
    public void IntoConversionFailureDisposesAlreadyWrittenReferenceResults()
    {
        using var state = LuauState.Create(new LuauStateOptions
        {
            MaxDecodedStringBytes = 3,
            MaxDecodedBytesPerOperation = 16,
        });
        var destination = new LuauValue[2];
        var releasedBefore = state.Context.ReleasedReferenceCount;

        Assert.Throws<LuauDecodedResultLimitException>(
            () => state.DoStringInto("return 'over', {}", destination));

        Assert.All(destination, value => Assert.True(value.IsNil));
        Assert.Equal(releasedBefore + 1, state.Context.ReleasedReferenceCount);
        Assert.Equal(7, Assert.Single(state.DoString("return 7")).Read<int>());
    }

    [Fact]
    public async Task IntoDestinationsRejectLiveReferenceOverwriteAndPreserveExistingOwner()
    {
        using var state = LuauState.Create();
        using var existing = state.CreateTable();
        existing["answer"] = 41;
        var destination = new LuauValue[2];

        // Only slots that will receive results are constrained; unused caller
        // capacity is neither inspected nor mutated.
        destination[1] = LuauValue.FromTable(existing);
        Assert.Equal(1, state.DoStringInto("return 42", destination));
        Assert.Equal(42, destination[0].Read<int>());
        Assert.Equal(41, existing["answer"].Read<int>());

        destination[0] = LuauValue.FromTable(existing);
        var synchronous = Assert.Throws<ArgumentException>(
            () => state.DoStringInto("return {}", destination));
        Assert.Equal("destination", synchronous.ParamName);
        Assert.Equal(41, existing["answer"].Read<int>());

        var asynchronous = await Assert.ThrowsAsync<ArgumentException>(
            async () => await state.DoStringIntoAsync("return 9", destination));
        Assert.Equal("destination", asynchronous.ParamName);
        Assert.Equal(41, existing["answer"].Read<int>());

        destination[0] = default;
        Assert.Equal(1, await state.DoStringIntoAsync("return { answer = 43 }", destination));
        var replacement = destination[0].Read<LuauTable>();
        Assert.Equal(43, replacement["answer"].Read<int>());
        replacement.Dispose();
        destination[0] = default;
    }

    [Fact]
    public async Task IntoDestinationsRejectCachedThreadWrapperOverwrite()
    {
        using var state = LuauState.Create();
        state.OpenCoroutineLibrary();
        using var created = state.DoString(
            "return coroutine.create(function() return 1 end)");
        var child = created.Read<LuauState>(0);
        var destination = new[] { LuauValue.FromThread(child) };

        Assert.Throws<ArgumentException>(() => state.DoStringInto("return 2", destination));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await state.DoStringIntoAsync("return 3", destination));
        Assert.False(child.IsDisposed);

        destination[0] = default;
        child.Dispose();
        Assert.Equal(1, state.Context.CachedStateCount);
    }

    [Fact]
    public async Task PartialConversionReleasesOnlyNewUnpublishedThreadWrappers()
    {
        using var state = LuauState.Create(new LuauStateOptions
        {
            MaxDecodedStringBytes = 3,
            MaxDecodedBytesPerOperation = 16,
        });
        state.OpenCoroutineLibrary();
        var releasedBefore = state.Context.ReleasedReferenceCount;
        var destination = new LuauValue[2];
        const string source = "return 'over', coroutine.create(function() end)";

        Assert.Throws<LuauDecodedResultLimitException>(() => state.DoString(source));
        Assert.Equal(1, state.Context.CachedStateCount);
        Assert.Equal(releasedBefore + 1, state.Context.ReleasedReferenceCount);

        await Assert.ThrowsAsync<LuauDecodedResultLimitException>(
            async () => await state.DoStringAsync(source));
        Assert.Equal(1, state.Context.CachedStateCount);
        Assert.Equal(releasedBefore + 2, state.Context.ReleasedReferenceCount);

        Assert.Throws<LuauDecodedResultLimitException>(
            () => state.DoStringInto(source, destination));
        Assert.All(destination, value => Assert.True(value.IsNil));
        Assert.Equal(1, state.Context.CachedStateCount);
        Assert.Equal(releasedBefore + 3, state.Context.ReleasedReferenceCount);

        await Assert.ThrowsAsync<LuauDecodedResultLimitException>(
            async () => await state.DoStringIntoAsync(source, destination));
        Assert.All(destination, value => Assert.True(value.IsNil));
        Assert.Equal(1, state.Context.CachedStateCount);
        Assert.Equal(releasedBefore + 4, state.Context.ReleasedReferenceCount);
    }

    [Fact]
    public void ResultScopeOwnsReferencesAndRetainCreatesIndependentOwner()
    {
        using var state = LuauState.Create();
        var results = state.DoString("return { answer = 42 }, 'primitive'");
        var table = results.Read<LuauTable>(0);
        var primitive = results[1];
        using var retained = table.Retain();

        Assert.False(table.IsBorrowed);
        Assert.False(table.IsDisposed);
        Assert.Equal(42, table["answer"].Read<int>());
        results.Dispose();

        Assert.True(table.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => _ = results.Count);
        Assert.Throws<ObjectDisposedException>(() => _ = table["answer"]);
        Assert.Equal("primitive", primitive.Read<string>());
        Assert.Equal(42, retained["answer"].Read<int>());
    }

    [Fact]
    public void ResultScopeCarvesOutCallerOwnedCachedThreadWrappers()
    {
        using var root = LuauState.Create();
        root.OpenCoroutineLibrary();
        var results = root.DoString(
            "return coroutine.create(function() return 42 end)");
        var child = results.Read<LuauState>(0);

        Assert.Equal(2, root.Context.CachedStateCount);
        results.Dispose();
        Assert.False(child.IsDisposed);
        Assert.Throws<InvalidOperationException>(() => LuauValue.FromThread(child).Retain());
        using (var resumed = child.Resume())
        {
            Assert.Equal(42, Assert.Single(resumed).Read<int>());
        }

        child.Dispose();
        Assert.Equal(1, root.Context.CachedStateCount);
    }

    [Fact]
    public void RepeatedThreadReadsShareOneCachedWrapperUntilExplicitRelease()
    {
        using var root = LuauState.Create();
        root.OpenCoroutineLibrary();
        using var results = root.DoString(
            "return { child = coroutine.create(function() return 7 end) }");
        var table = results.Read<LuauTable>(0);
        var first = table["child"].Read<LuauState>();
        var second = table.RawGet("child").Read<LuauState>();

        Assert.Same(first, second);
        Assert.Equal(2, root.Context.CachedStateCount);
        first.Dispose();
        Assert.True(second.IsDisposed);

        var replacement = table["child"].Read<LuauState>();
        Assert.NotSame(first, replacement);
        Assert.False(replacement.IsDisposed);
        replacement.Dispose();
        Assert.Equal(1, root.Context.CachedStateCount);
    }

    [Fact]
    public void TableEnumeratorDoesNotOwnSharedThreadKeysOrValues()
    {
        using var root = LuauState.Create();
        root.OpenCoroutineLibrary();
        var results = root.DoString(
            "local key = coroutine.create(function() end); " +
            "local value = coroutine.create(function() end); " +
            "return { [key] = value }, key, value");
        var table = results.Read<LuauTable>(0);
        var key = results.Read<LuauState>(1);
        var value = results.Read<LuauState>(2);
        var enumerator = table.GetEnumerator();

        Assert.True(enumerator.MoveNext());
        Assert.Same(key, enumerator.Current.Key.Read<LuauState>());
        Assert.Same(value, enumerator.Current.Value.Read<LuauState>());
        enumerator.Dispose();
        Assert.False(key.IsDisposed);
        Assert.False(value.IsDisposed);

        results.Dispose();
        Assert.False(key.IsDisposed);
        Assert.False(value.IsDisposed);
        key.Dispose();
        value.Dispose();
        Assert.Equal(1, root.Context.CachedStateCount);
    }

    [Fact]
    public void CallbackReferencesAreBorrowedAndRetainOutlivesTheFrame()
    {
        using var state = LuauState.Create();
        LuauTable? borrowed = null;
        LuauTable? retained = null;
        using var callback = state.CreateFunction("inspect", context =>
        {
            borrowed = context.Read<LuauTable>(0);
            Assert.True(borrowed.IsBorrowed);
            retained = borrowed.Retain();
            context.Return(borrowed["answer"]);
        });
        state["inspect"] = callback;

        using (var result = state.DoString("return inspect({ answer = 42 })"))
        {
            Assert.Equal(42, result.Read<int>(0));
        }

        Assert.NotNull(borrowed);
        Assert.NotNull(retained);
        Assert.True(borrowed!.IsBorrowed);
        Assert.True(borrowed.IsDisposed);
        Assert.Throws<InvalidOperationException>(() => _ = borrowed["answer"]);
        Assert.False(retained!.IsBorrowed);
        Assert.Equal(42, retained["answer"].Read<int>());
        retained.Dispose();
    }

    [Fact]
    public void MixedCallbackReferenceKindsShareBorrowedExpiryAndRetainModel()
    {
        using var state = LuauState.Create();
        state.OpenBaseLibrary();
        using var sourceBuffer = state.CreateBuffer([1, 2, 3, 4]);
        using var sourceHandle = state.CreateHandle(new GeneratedCapabilityTarget { Value = 42 });
        state["hostBuffer"] = sourceBuffer;
        state["hostHandle"] = sourceHandle;

        LuauFunction? borrowedFunction = null;
        LuauBuffer? borrowedBuffer = null;
        LuauObjectHandle? borrowedHandle = null;
        LuauUserData? borrowedUserData = null;
        LuauFunction? retainedFunction = null;
        LuauBuffer? retainedBuffer = null;
        LuauObjectHandle? retainedHandle = null;
        LuauUserData? retainedUserData = null;
        using var callback = state.CreateFunction("inspectMixed", context =>
        {
            borrowedFunction = context.Read<LuauFunction>(0);
            borrowedBuffer = context.Read<LuauBuffer>(1);
            borrowedHandle = context.Read<LuauObjectHandle>(2);
            borrowedUserData = context.Read<LuauUserData>(3);
            Assert.True(borrowedFunction.IsBorrowed);
            Assert.True(borrowedBuffer.IsBorrowed);
            Assert.True(borrowedHandle.IsBorrowed);
            Assert.True(borrowedUserData.IsBorrowed);
            retainedFunction = borrowedFunction.Retain();
            retainedBuffer = borrowedBuffer.Retain();
            retainedHandle = borrowedHandle.Retain();
            retainedUserData = borrowedUserData.Retain();
            context.Return(true);
        });
        state["inspectMixed"] = callback;

        using (var result = state.DoString(
            "return inspectMixed(function() return 42 end, hostBuffer, hostHandle, newproxy(true))"))
        {
            Assert.True(result.Read<bool>(0));
        }

        Assert.True(borrowedFunction!.IsDisposed);
        Assert.True(borrowedBuffer!.IsDisposed);
        Assert.True(borrowedHandle!.IsDisposed);
        Assert.True(borrowedUserData!.IsDisposed);
        Assert.Throws<InvalidOperationException>(() => borrowedFunction.Invoke());
        Assert.Throws<InvalidOperationException>(() => _ = borrowedBuffer.Length);
        Assert.Throws<InvalidOperationException>(() => _ = borrowedHandle.State);
        Assert.Throws<InvalidOperationException>(() => _ = borrowedUserData.Size);

        using (var invocation = retainedFunction!.Invoke())
        {
            Assert.Equal(42, invocation.Read<int>(0));
        }
        Assert.Equal([1, 2, 3, 4], retainedBuffer!.ToArray());
        Assert.Same(state, retainedHandle!.State);
        Assert.True(retainedUserData!.Size >= 0);

        retainedFunction.Dispose();
        retainedBuffer.Dispose();
        retainedHandle.Dispose();
        retainedUserData.Dispose();
    }

    [Fact]
    public void GeneratedBindingsUseTheSameBorrowedReferenceModel()
    {
        using var state = LuauState.Create();
        var probe = new GeneratedOwnershipProbe();
        state.OpenLibrary(probe);

        using (var result = state.DoString("return ownershipProbe.inspect({ answer = 42 })"))
        {
            Assert.True(result.Read<bool>(0));
        }

        Assert.NotNull(probe.Captured);
        Assert.True(probe.Captured!.IsBorrowed);
        Assert.True(probe.Captured.IsDisposed);
        Assert.Throws<InvalidOperationException>(() => _ = probe.Captured["answer"]);
        Assert.NotNull(probe.Retained);
        Assert.False(probe.Retained!.IsBorrowed);
        Assert.Equal(42, probe.Retained["answer"].Read<int>());
        probe.Retained.Dispose();
    }

    [Fact]
    public void GeneratedReferenceReturnsDoNotTransferOrDisposeLibraryOwnership()
    {
        using var state = LuauState.Create();
        using var shared = state.CreateTable();
        shared["answer"] = 42;
        var probe = new GeneratedOwnershipProbe { Shared = shared };
        state.OpenLibrary(probe);

        using (var result = state.DoString(
            "return ownershipProbe.shared.answer, ownershipProbe.getShared().answer"))
        {
            Assert.Equal(42, result.Read<int>(0));
            Assert.Equal(42, result.Read<int>(1));
        }

        Assert.Same(shared, probe.Shared);
        Assert.False(shared.IsDisposed);
        Assert.Equal(42, shared["answer"].Read<int>());
    }

    [Fact]
    public void ManualAndGeneratedThreadArgumentsUseTheSharedCachedWrapper()
    {
        using var state = LuauState.Create();
        state.OpenCoroutineLibrary();
        var probe = new GeneratedOwnershipProbe();
        state.OpenLibrary(probe);
        LuauState? manualThread = null;
        using var inspect = state.CreateFunction("inspectThread", context =>
        {
            manualThread = context.Read<LuauState>(0);
            context.Return(true);
        });
        state["inspectThread"] = inspect;
        var results = state.DoString(
            "local child = coroutine.create(function() return 5 end); " +
            "return inspectThread(child), ownershipProbe.inspectThread(child), child");
        var returnedThread = results.Read<LuauState>(2);

        Assert.True(results.Read<bool>(0));
        Assert.True(results.Read<bool>(1));
        Assert.Same(returnedThread, manualThread);
        Assert.Same(returnedThread, probe.CapturedThread);
        results.Dispose();
        Assert.False(returnedThread.IsDisposed);
        returnedThread.Dispose();
    }

    [Fact]
    public void HostileBorrowedReferenceArgumentsAreReleasedAtEveryCallbackBoundary()
    {
        using var state = LuauState.Create();
        using var callback = state.CreateFunction("consume", context =>
        {
            var table = context.Read<LuauTable>(0);
            context.Return(table["value"]);
        });
        state["consume"] = callback;
        var releasedBefore = state.Context.ReleasedReferenceCount;

        using var results = state.DoString(
            "local total = 0; for index = 1, 1000 do " +
            "total += consume({ value = index }) end; return total");

        Assert.Equal(500500, results.Read<int>(0));
        Assert.Equal(releasedBefore + 1000, state.Context.ReleasedReferenceCount);
    }

    [Fact]
    public unsafe void DiagnosticDecoderTruncatesAtUtf8BoundaryAndContainsInvalidBytes()
    {
        byte[] utf8 = [0x61, 0xf0, 0x9f, 0x98, 0x80, 0x62];
        fixed (byte* pointer = utf8)
        {
            var decoded = BoundedUtf8Decoder.Decode(pointer, (ulong)utf8.Length, 4, out var truncated);
            Assert.Equal("a", decoded);
            Assert.True(truncated);
        }

        byte[] invalid = [0xff, 0xfe, 0x61];
        fixed (byte* pointer = invalid)
        {
            var decoded = BoundedUtf8Decoder.DecodeDiagnostic(pointer, (ulong)invalid.Length, 3);
            Assert.Equal("\uFFFD", decoded);
            Assert.Equal(3, Encoding.UTF8.GetByteCount(decoded));
        }
    }

    [Fact]
    public void NewHardeningDefaultsAreFiniteCloneableAndExplicitlyValidated()
    {
        var defaults = LuauStateOptions.Default;
        Assert.NotNull(defaults.MaxDecodedStringBytes);
        Assert.NotNull(defaults.MaxDecodedBytesPerOperation);
        Assert.NotNull(defaults.MaxCachedModuleCount);
        Assert.Equal(16L * 1024 * 1024, defaults.MaxCachedModuleBytes);
        Assert.NotNull(defaults.MaxModuleDependencyDepth);
        Assert.True(defaults.MaxDiagnosticBytes > 0);

        var unbounded = LuauStateOptions.UnboundedResources;
        Assert.Null(unbounded.MaxDecodedStringBytes);
        Assert.Null(unbounded.MaxDecodedBytesPerOperation);
        Assert.Null(unbounded.MaxCachedModuleCount);
        Assert.Null(unbounded.MaxCachedModuleBytes);
        Assert.Null(unbounded.MaxModuleDependencyDepth);
        Assert.True(unbounded.MaxDiagnosticBytes > 0);

        var original = new LuauStateOptions
        {
            MaxDecodedStringBytes = 123,
            MaxDecodedBytesPerOperation = 456,
            MaxDiagnosticBytes = 78,
            MaxCachedModuleCount = 9,
            MaxCachedModuleBytes = 1024,
            MaxModuleDependencyDepth = 10,
        };
        var snapshot = original.Snapshot();
        Assert.NotSame(original, snapshot);
        Assert.Equal(original, snapshot);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LuauStateOptions { MaxDecodedStringBytes = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LuauStateOptions { MaxDecodedBytesPerOperation = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LuauStateOptions { MaxDiagnosticBytes = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LuauStateOptions { MaxCachedModuleCount = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LuauStateOptions { MaxCachedModuleBytes = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LuauStateOptions { MaxCachedModuleBytes = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LuauStateOptions { MaxModuleDependencyDepth = 0 });
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(3, 0, 0, 0)]
    [InlineData(0, -1, 0, 0)]
    [InlineData(0, 3, 0, 0)]
    [InlineData(0, 0, -1, 0)]
    [InlineData(0, 0, 2, 0)]
    [InlineData(0, 0, 0, -1)]
    [InlineData(0, 0, 0, 3)]
    public void CompilerOptionRangesRejectUnsupportedValues(
        int optimization,
        int debug,
        int typeInfo,
        int coverage)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuauCompileOptions
        {
            OptimizationLevel = optimization,
            DebugLevel = debug,
            TypeInfoLevel = typeInfo,
            CoverageLevel = coverage,
        });
    }
}

[LuauLibrary("ownershipProbe")]
public sealed partial class GeneratedOwnershipProbe
{
    public LuauTable? Captured { get; private set; }
    public LuauTable? Retained { get; private set; }

    public LuauState? CapturedThread { get; private set; }

    [LuauMember("shared")]
    public LuauTable Shared { get; set; } = null!;

    [LuauMember("inspect")]
    public bool Inspect(LuauTable table)
    {
        Captured = table;
        Retained = table.Retain();
        return table.IsBorrowed;
    }

    [LuauMember("getShared")]
    public LuauTable GetShared() => Shared;

    [LuauMember("inspectThread")]
    public bool InspectThread(LuauState thread)
    {
        CapturedThread = thread;
        return !thread.IsMainThread;
    }
}
