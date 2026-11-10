using System.Reflection;

namespace Luau.Tests;

/// <summary>
/// Managed callback identifiers are process-wide and never reused. They were
/// 32-bit, so a process could register no callback after its 2,147,483,647th;
/// they are 64-bit now.
/// </summary>
public sealed class ManagedCallbackIdentifierTests
{
    static readonly FieldInfo NextIdentifier = typeof(LuauState).Assembly
        .GetType("Luau.LuauVmContext", throwOnError: true)!
        .GetField("nextGlobalManagedCallbackId", BindingFlags.NonPublic | BindingFlags.Static)!;

    // Moves the process counter forward to at least value. Identifiers are
    // never reused, so skipping some affects no other test.
    static void AdvanceTo(long value)
    {
        lock (NextIdentifier)
        {
            if ((long)NextIdentifier.GetValue(null)! < value)
            {
                NextIdentifier.SetValue(null, value);
            }
        }
    }

    [Fact]
    public void CallbacksRegisteredPastTheInt32RangeKeepWorking()
    {
        AdvanceTo(int.MaxValue - 2L);
        using var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        var baseline = root.Context.ManagedCallbackCount;

        var functions = new List<LuauFunction>();
        for (var index = 0; index < 4; index++)
        {
            var captured = index;
            functions.Add(root.CreateFunction(context => context.Return(captured * 10 + context.Read<int>(0))));
        }
        functions.Add(root.CreateAsyncFunction(async context =>
        {
            await Task.Yield();
            context.Return(context.Read<int>(0) + 100);
        }));
        Assert.True((long)NextIdentifier.GetValue(null)! > int.MaxValue);

        for (var index = 0; index < functions.Count; index++)
        {
            root[$"f{index}"] = LuauValue.FromFunction(functions[index]);
        }
        using (var results = root.DoString("return f0(1), f1(1), f2(1), f3(1)"))
        {
            Assert.Equal([1, 11, 21, 31], results.AsSpan().ToArray().Select(value => value.Read<int>()));
        }
        using (var results = root.DoStringAsync("return f4(1)").AsTask().GetAwaiter().GetResult())
        {
            Assert.Equal(101, results[0].Read<int>());
        }

        foreach (var function in functions)
        {
            function.Dispose();
        }
        for (var index = 0; index < functions.Count; index++)
        {
            root[$"f{index}"] = LuauValue.Nil;
        }
        root.CollectGarbage();
        Assert.Equal(baseline, root.Context.ManagedCallbackCount);
    }
}
