namespace Luau.Tests;

public sealed class FunctionInvocationOutputTests
{
    [Fact]
    public void InvokeVoidSupportsZeroSingleAndSpanArguments()
    {
        using var root = LuauState.Create();
        var calls = new List<int[]>();
        using var capture = root.CreateFunction(context =>
        {
            var values = new int[context.ArgumentCount];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = context.Read<int>(index);
            }
            calls.Add(values);
        });
        root["capture"] = capture;
        using var functionOwner = root.DoString(
            "return function(...) capture(...) end");
        var function = functionOwner.Read<LuauFunction>(0);

        function.InvokeVoid();
        function.InvokeVoid(7);
        function.InvokeVoid(new LuauValue[] { 11, 13, 17 });

        Assert.Collection(
            calls,
            values => Assert.Empty(values),
            values => Assert.Equal([7], values),
            values => Assert.Equal([11, 13, 17], values));
    }

    [Fact]
    public async Task InvokeVoidRejectsAsyncCallbackSynchronouslyAndSupportsItAsynchronously()
    {
        using var root = LuauState.Create();
        var observed = 0;
        using var asyncCapture = root.CreateAsyncFunction(async context =>
        {
            await Task.Yield();
            observed = context.Read<int>(0);
        });
        root["asyncCapture"] = asyncCapture;
        using var functionOwner = root.DoString(
            "return function(value) asyncCapture(value) end");
        var function = functionOwner.Read<LuauFunction>(0);

        var exception = Assert.Throws<LuauManagedCallbackException>(
            () => function.InvokeVoid(41));

        Assert.Contains("asynchronous managed callback", exception.Message, StringComparison.OrdinalIgnoreCase);
        await function.InvokeVoidAsync(42);
        Assert.Equal(42, observed);
        Assert.Equal(9, Assert.Single(root.DoString("return 9")).Read<int>());
    }

    [Fact]
    public void InvokeVoidDiscardsUnexpectedResultsReportsLabelAndLeavesVmReusable()
    {
        using var root = LuauState.Create();
        using var functionOwner = root.DoString(
            "return function() return {}, 42 end");
        var function = functionOwner.Read<LuauFunction>(0);
        var disposableCount = root.RegisteredDisposableCount;

        var unlabeled = Assert.Throws<LuauResultLimitException>(() => function.InvokeVoid());
        var labeled = Assert.Throws<LuauResultLimitException>(
            () => function.InvokeVoidLabeled("match:update"));

        Assert.Equal(2, unlabeled.ActualCount);
        Assert.Equal(0, unlabeled.Limit);
        Assert.Null(unlabeled.ChunkName);
        Assert.Equal("match:update", labeled.ChunkName);
        Assert.Contains("match:update", labeled.Message, StringComparison.Ordinal);
        Assert.Equal(disposableCount, root.RegisteredDisposableCount);
        Assert.Equal(42, Assert.Single(root.DoString("return 40 + 2")).Read<int>());
    }

    [Fact]
    public void InvokeVoidReportsTheZeroResultContractAboveTheStateDefaultLimit()
    {
        using var root = LuauState.Create();
        var returnedValues = string.Join(", ", Enumerable.Repeat("1", 65));
        using var functionOwner = root.DoString(
            $"return function() return {returnedValues} end");
        var function = functionOwner.Read<LuauFunction>(0);

        var exception = Assert.Throws<LuauResultLimitException>(
            () => function.InvokeVoidLabeled("match:update"));

        Assert.Equal(65, exception.ActualCount);
        Assert.Equal(0, exception.Limit);
        Assert.Equal("match:update", exception.ChunkName);
        Assert.Equal(42, Assert.Single(root.DoString("return 40 + 2")).Read<int>());
    }

    [Fact]
    public async Task InvokeIntoSyncAndAsyncTransferResultOwnershipToTheDestination()
    {
        using var root = LuauState.Create();
        using var synchronousOwner = root.DoString(
            "return function(value) return value + value, { answer = value } end");
        var synchronous = synchronousOwner.Read<LuauFunction>(0);
        var syncDestination = new LuauValue[2];

        Assert.Equal(2, synchronous.InvokeInto(
            new LuauValue[] { 21d },
            syncDestination));
        Assert.Equal(42, syncDestination[0].Read<int>());
        var syncTable = syncDestination[1].Read<LuauTable>();
        Assert.Equal(21, syncTable["answer"].Read<int>());

        using var asyncDouble = root.CreateAsyncFunction(async context =>
        {
            await Task.Yield();
            context.Return(context.Read<double>(0) * 2);
        });
        root["asyncDouble"] = asyncDouble;
        using var asynchronousOwner = root.DoString(
            "return function(value) return asyncDouble(value), { answer = value } end");
        var asynchronous = asynchronousOwner.Read<LuauFunction>(0);
        var asyncDestination = new LuauValue[2];

        Assert.Equal(2, await asynchronous.InvokeIntoAsync(
            new LuauValue[] { 22d },
            asyncDestination));
        Assert.Equal(44, asyncDestination[0].Read<int>());
        var asyncTable = asyncDestination[1].Read<LuauTable>();
        Assert.Equal(22, asyncTable["answer"].Read<int>());

        syncTable.Dispose();
        syncDestination[1] = default;
        asyncTable.Dispose();
        asyncDestination[1] = default;
    }

    [Fact]
    public async Task InvokeIntoRejectsShortDestinationsAndCancellationWithoutMutation()
    {
        using var root = LuauState.Create();
        using var functionOwner = root.DoString(
            "return function() return 1, 2 end");
        var function = functionOwner.Read<LuauFunction>(0);
        var synchronous = new LuauValue[] { 91 };
        var asynchronous = new LuauValue[] { 92 };

        var syncFailure = Assert.Throws<ArgumentException>(
            () => function.InvokeInto(synchronous));
        var asyncFailure = await Assert.ThrowsAsync<ArgumentException>(
            async () => await function.InvokeIntoAsync(asynchronous));

        Assert.Equal("destination", syncFailure.ParamName);
        Assert.Equal("destination", asyncFailure.ParamName);
        Assert.Equal(91, synchronous[0].Read<int>());
        Assert.Equal(92, asynchronous[0].Read<int>());

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceledDestination = new LuauValue[] { 93, 94 };
        var canceled = await Assert.ThrowsAsync<LuauExecutionCanceledException>(
            async () => await function.InvokeIntoAsync(
                canceledDestination,
                cancellation.Token));

        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.Equal([93, 94], canceledDestination.Select(value => value.Read<int>()));
        Assert.Equal(7, Assert.Single(root.DoString("return 7")).Read<int>());
    }

    [Fact]
    public async Task InvokeIntoRollsBackPartiallyWrittenReferenceResults()
    {
        using var root = LuauState.Create(new LuauStateOptions
        {
            MaxDecodedStringBytes = 3,
            MaxDecodedBytesPerOperation = 16,
        });
        using var functionOwner = root.DoString(
            "return function() return 'over', {} end");
        var function = functionOwner.Read<LuauFunction>(0);
        var releasedBefore = root.Context.ReleasedReferenceCount;
        var synchronous = new LuauValue[2];
        var asynchronous = new LuauValue[2];

        Assert.Throws<LuauDecodedResultLimitException>(
            () => function.InvokeInto(synchronous));
        Assert.All(synchronous, value => Assert.True(value.IsNil));
        Assert.Equal(releasedBefore + 1, root.Context.ReleasedReferenceCount);

        await Assert.ThrowsAsync<LuauDecodedResultLimitException>(
            async () => await function.InvokeIntoAsync(asynchronous));
        Assert.All(asynchronous, value => Assert.True(value.IsNil));
        Assert.Equal(releasedBefore + 2, root.Context.ReleasedReferenceCount);
        Assert.Equal(8, Assert.Single(root.DoString("return 8")).Read<int>());
    }

    [Fact]
    public async Task InvokeIntoRejectsLiveReferenceSlotsAndPreservesTheirOwners()
    {
        using var root = LuauState.Create();
        using var existing = root.CreateTable();
        existing["answer"] = 41;
        using var functionOwner = root.DoString(
            "return function() return {} end");
        var function = functionOwner.Read<LuauFunction>(0);
        var destination = new[] { LuauValue.FromTable(existing) };

        var synchronous = Assert.Throws<ArgumentException>(
            () => function.InvokeInto(destination));
        var asynchronous = await Assert.ThrowsAsync<ArgumentException>(
            async () => await function.InvokeIntoAsync(destination));

        Assert.Equal("destination", synchronous.ParamName);
        Assert.Equal("destination", asynchronous.ParamName);
        Assert.Same(existing, destination[0].Read<LuauTable>());
        Assert.Equal(41, existing["answer"].Read<int>());
        destination[0] = default;
    }
}
