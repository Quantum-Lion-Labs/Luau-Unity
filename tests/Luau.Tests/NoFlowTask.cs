using System.Runtime.CompilerServices;

namespace Luau.Tests;

/// <summary>
/// A task-like type whose async method builder does not carry the
/// <see cref="ExecutionContext"/> across an await, as UniTask's builder does
/// not. Code after an await in an <c>async NoFlowTask</c> method runs in the
/// context of whatever resumes it, so <see cref="AsyncLocal{T}"/> values its
/// caller set are gone there.
/// </summary>
/// <remarks>
/// Awaiting a <see cref="NoFlowTask"/> is ordinary: its awaiter is a
/// <see cref="TaskAwaiter"/>, so the awaiting method keeps its own context.
/// Only the code inside the non-flowing method loses it.
/// </remarks>
[AsyncMethodBuilder(typeof(NoFlowTaskMethodBuilder))]
public sealed class NoFlowTask
{
    internal readonly TaskCompletionSource Source = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskAwaiter GetAwaiter() => Source.Task.GetAwaiter();

    /// <summary>The completion as a task, for tests that wait on it.</summary>
    public Task AsTask() => Source.Task;
}

public struct NoFlowTaskMethodBuilder
{
    NoFlowTask task;

    public static NoFlowTaskMethodBuilder Create() => new() { task = new NoFlowTask() };

    public NoFlowTask Task => task;

    // Runs the first segment in the caller's context, as UniTask's builder
    // does, without restoring it afterwards.
    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine => stateMachine.MoveNext();

    public void SetStateMachine(IAsyncStateMachine stateMachine)
    {
    }

    public void SetResult() => task.Source.SetResult();

    public void SetException(Exception exception) => task.Source.SetException(exception);

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        // Only awaiters without UnsafeOnCompleted come here; the awaiter
        // decides what OnCompleted carries.
        IAsyncStateMachine boxed = stateMachine;
        awaiter.OnCompleted(boxed.MoveNext);
    }

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        IAsyncStateMachine boxed = stateMachine;
        // No ExecutionContext capture: the continuation runs in the context
        // of whoever completes the awaiter.
        awaiter.UnsafeOnCompleted(boxed.MoveNext);
    }
}

/// <summary>
/// An awaitable that runs its continuation inline, on the thread and in the
/// execution context of the code that calls <see cref="Set"/>, as UniTask's
/// completion sources do. Awaited from a <see cref="NoFlowTask"/> method, the
/// continuation therefore carries whatever context the completer has.
/// </summary>
public sealed class InlineSignal : ICriticalNotifyCompletion
{
    Action? continuation;
    int set;

    public InlineSignal GetAwaiter() => this;

    public bool IsCompleted => Volatile.Read(ref set) != 0;

    public void GetResult()
    {
    }

    public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);

    public void UnsafeOnCompleted(Action continuation)
    {
        Volatile.Write(ref this.continuation, continuation);
        if (Volatile.Read(ref set) != 0)
        {
            Interlocked.Exchange(ref this.continuation, null)?.Invoke();
        }
    }

    public void Set()
    {
        Volatile.Write(ref set, 1);
        Interlocked.Exchange(ref continuation, null)?.Invoke();
    }
}
