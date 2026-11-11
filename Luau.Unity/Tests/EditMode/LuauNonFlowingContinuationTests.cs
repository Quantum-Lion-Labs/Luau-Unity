using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Luau.Unity.Tests
{
    /// <summary>
    /// UniTask's async method builder does not carry the
    /// <see cref="ExecutionContext"/> across an await, so code after an await
    /// in an <c>async UniTask</c> helper of an asynchronous host callback runs
    /// without the operation's ambient marker. The state's continuation
    /// scheduler, which <see cref="LuauUnity.CreateState"/> captures from the
    /// Editor's main thread, is what lets the operation accept that code. The
    /// test's own builder behaves as UniTask's does, so the package takes no
    /// UniTask dependency.
    /// </summary>
    public sealed class LuauNonFlowingContinuationTests
    {
        static readonly AsyncLocal<string> FlowProbe = new AsyncLocal<string>();

        sealed class Observation
        {
            public bool LostContext;
            public int ThreadId;
        }

        static async NoFlowTask LoadInto(LuauTable table, Observation observation)
        {
            // Resumes from the Editor loop's queue, in the main thread's own
            // context rather than the callback's.
            await Task.Yield();
            observation.LostContext = FlowProbe.Value == null;
            observation.ThreadId = Environment.CurrentManagedThreadId;
            table["loaded"] = true;
        }

        [Test]
        public async Task AsyncCallbackAwaitingANonFlowingHelperCompletesOnADefaultState()
        {
            var ownerThreadId = Environment.CurrentManagedThreadId;
            using var root = LuauUnity.CreateState(new LuauUnityOptions
            {
                // The helper resumes through the Editor loop, which the wall
                // clock counts.
                StateOptions = TestBudgets.StateOptions,
            });
            Assert.That(
                root.Options.DefaultExecutionOptions.ContinuationScheduler,
                Is.InstanceOf<LuauSynchronizationContextScheduler>());
            using var table = root.CreateTable();
            root["table"] = table;
            var observation = new Observation();
            using var load = root.CreateAsyncFunction("load", async context =>
            {
                FlowProbe.Value = "callback";
                await LoadInto(table, observation);
                context.Return(1);
            });
            root["load"] = load;

            using (var results = await root.DoStringAsync("return load(), table.loaded"))
            {
                Assert.That(observation.LostContext, Is.True, "The helper kept the callback's execution context.");
                Assert.That(observation.ThreadId, Is.EqualTo(ownerThreadId));
                Assert.That(results, Has.Length.EqualTo(2));
                Assert.That(results[0].Read<int>(), Is.EqualTo(1));
                Assert.That(results[1].Read<bool>(), Is.True);
            }

            using var after = root.DoString("return 2");
            Assert.That(after[0].Read<int>(), Is.EqualTo(2));
        }

        [Test]
        public async Task WithoutASchedulerTheSameHelperIsRefused()
        {
            using var root = LuauUnity.CreateState(new LuauUnityOptions
            {
                StateOptions = TestBudgets.StateOptions,
                CaptureUnitySynchronizationContext = false,
            });
            Assert.That(root.Options.DefaultExecutionOptions.ContinuationScheduler, Is.Null);
            using var table = root.CreateTable();
            var observation = new Observation();
            using var load = root.CreateAsyncFunction("load", async context =>
            {
                FlowProbe.Value = "callback";
                await LoadInto(table, observation);
                context.Return(1);
            });
            root["load"] = load;

            LuauManagedCallbackException exception = null;
            try
            {
                using var results = await root.DoStringAsync("return load()");
            }
            catch (LuauManagedCallbackException caught)
            {
                exception = caught;
            }

            Assert.That(observation.LostContext, Is.True, "The helper kept the callback's execution context.");
            Assert.That(exception, Is.Not.Null);
            Assert.That(exception.CallbackName, Is.EqualTo("load"));
            Assert.That(exception.InnerException, Is.InstanceOf<InvalidOperationException>());
            Assert.That(
                exception.InnerException.Message,
                Is.EqualTo("The Luau VM is executing on another operation."));

            using var after = root.DoString("return 2");
            Assert.That(after[0].Read<int>(), Is.EqualTo(2));
        }
    }

    /// <summary>
    /// A task-like type whose builder, like UniTask's, neither captures the
    /// <see cref="ExecutionContext"/> at an await nor restores it after the
    /// first synchronous segment.
    /// </summary>
    [AsyncMethodBuilder(typeof(NoFlowTaskMethodBuilder))]
    sealed class NoFlowTask
    {
        internal readonly TaskCompletionSource<bool> Source =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskAwaiter<bool> GetAwaiter()
        {
            return Source.Task.GetAwaiter();
        }
    }

    struct NoFlowTaskMethodBuilder
    {
        NoFlowTask task;

        public static NoFlowTaskMethodBuilder Create()
        {
            return new NoFlowTaskMethodBuilder { task = new NoFlowTask() };
        }

        public NoFlowTask Task => task;

        public void Start<TStateMachine>(ref TStateMachine stateMachine)
            where TStateMachine : IAsyncStateMachine
        {
            stateMachine.MoveNext();
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine)
        {
        }

        public void SetResult()
        {
            task.Source.SetResult(true);
        }

        public void SetException(Exception exception)
        {
            task.Source.SetException(exception);
        }

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            IAsyncStateMachine boxed = stateMachine;
            awaiter.OnCompleted(boxed.MoveNext);
        }

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            IAsyncStateMachine boxed = stateMachine;
            // No ExecutionContext capture: the continuation runs in the
            // context of whoever completes the awaiter.
            awaiter.UnsafeOnCompleted(boxed.MoveNext);
        }
    }
}
