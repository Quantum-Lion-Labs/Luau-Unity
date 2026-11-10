namespace Luau.Tests;

/// <summary>
/// Scenarios that run inside the reference-wrap fixture's load context, one at
/// a time; see <see cref="ReferenceWrapFixture"/>. Each positions the process
/// allocator itself and leaves no reference it created alive.
/// </summary>
internal static class ReferenceTokenWrapScenarios
{
    internal static int TokenOf(object wrapper)
    {
        using var access = ((ILuauReference)wrapper).AcquireReference();
        return access.Reference;
    }

    static LuauState CreateRoot()
    {
        var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        return root;
    }

    internal static void ReferenceCreationAfterTheLastToken()
    {
        ReferenceWrapNative.RequireFixture();
        var baseline = ReferenceWrapNative.Live();
        using var first = CreateRoot();
        using var second = CreateRoot();
        using var reader = first.CreateFunction(context => context.Read<LuauTable>(0));
        first["readTable"] = LuauValue.FromFunction(reader);
        var early = first.CreateTable();
        early["value"] = LuauValue.FromNumber(7);

        ReferenceWrapNative.Seek(int.MaxValue - 1);
        var penultimate = first.CreateTable();
        var last = second.CreateTable();
        Assert.Equal(int.MaxValue - 1, TokenOf(penultimate));
        Assert.Equal(int.MaxValue, TokenOf(last));

        // Every root, including a new one, fails to create a reference from
        // here on: results, callback arguments, and creation alike.
        Assert.Throws<LuauReferenceLimitException>(() => first.CreateTable());
        Assert.Throws<LuauReferenceLimitException>(() => second.DoString("return {}"));
        using (var third = CreateRoot())
        {
            Assert.Throws<LuauReferenceLimitException>(() => third.CreateTable());
        }
        var callbackFailure = Assert.Throws<LuauManagedCallbackException>(
            () => first.DoString("readTable({})"));
        Assert.Contains("cannot be read as LuauTable", callbackFailure.Message);
        Assert.Equal(0, first.GetTop());

        // References that are live keep working, and values without one too.
        Assert.Equal(7, early["value"].Read<int>());
        using (var results = first.DoString("return 1 + 1"))
        {
            Assert.Equal(2, results[0].Read<int>());
        }

        // Releasing every reference gives nothing back.
        early.Dispose();
        penultimate.Dispose();
        last.Dispose();
        Assert.Equal(baseline, ReferenceWrapNative.Live());
        Assert.Throws<LuauReferenceLimitException>(() => first.CreateTable());
    }
}
