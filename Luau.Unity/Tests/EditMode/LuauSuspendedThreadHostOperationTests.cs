using System;
using NUnit.Framework;

namespace Luau.Unity.Tests
{
    /// <summary>
    /// A host publishes per-frame state to a script's thread whatever the
    /// script is doing, and an async host function formats its arguments
    /// while its thread is suspended. Both used to run Luau code (the
    /// sandbox's write guard, a __tostring) on a thread that could not run it,
    /// and the next resume of that thread crashed the Editor.
    /// </summary>
    public sealed class LuauSuspendedThreadHostOperationTests
    {
        static LuauState CreateRoot()
        {
            var root = LuauState.Create(new LuauStateOptions
            {
                // Explicit and generous: nothing here depends on the default
                // wall-clock budget.
                DefaultExecutionOptions = LuauExecutionOptions.Unbounded with
                {
                    WallClockLimit = TimeSpan.FromMinutes(2),
                },
            });
            root.OpenBaseLibrary();
            root.OpenCoroutineLibrary();
            return root;
        }

        [Test]
        public void GlobalWriteAndReadOnAYieldedSandboxedThreadThenResume()
        {
            using var root = CreateRoot();
            root.SandboxRoot();
            using var thread = root.CreateSandboxedThread();
            using (var yielded = thread.DoString("coroutine.yield(1) return 'A', score"))
            {
                Assert.That(yielded[0].Read<int>(), Is.EqualTo(1));
            }

            var top = thread.GetTop();
            thread["score"] = 5;
            Assert.That(thread["score"].Read<int>(), Is.EqualTo(5));

            Assert.That(thread.GetStatus(), Is.EqualTo(LuauThreadStatus.Suspended));
            Assert.That(thread.GetTop(), Is.EqualTo(top));
            using (var resumed = thread.Resume())
            {
                Assert.That(resumed.Length, Is.EqualTo(2));
                Assert.That(resumed[0].Read<string>(), Is.EqualTo("A"));
                Assert.That(resumed[1].Read<int>(), Is.EqualTo(5));
            }

            using var rootResult = root.DoString("return 1");
            Assert.That(rootResult[0].Read<int>(), Is.EqualTo(1));
        }

        [Test]
        public void AsyncCallbackFormatsAnArgumentWithToString()
        {
            using var root = CreateRoot();
            string formatted = null;
            using var log = root.CreateAsyncFunction("log", context =>
            {
                formatted = context.ToDisplayString(0, 256, out _);
                context.Return(formatted);
                return default;
            });
            root["log"] = log;
            using var thread = root.CreateThread();

            // The callback completes synchronously and the state has no
            // continuation scheduler, so this never needs the main thread.
            using (var results = thread.DoStringAsync(
                "local value = setmetatable({}, { __tostring = function() return 'formatted by script' end }) " +
                "return log(value), 'after'").AsTask().GetAwaiter().GetResult())
            {
                Assert.That(results[0].Read<string>(), Is.EqualTo("formatted by script"));
                Assert.That(results[1].Read<string>(), Is.EqualTo("after"));
            }

            Assert.That(formatted, Is.EqualTo("formatted by script"));
            using var rootResult = root.DoString("return 1");
            Assert.That(rootResult[0].Read<int>(), Is.EqualTo(1));
        }
    }
}
