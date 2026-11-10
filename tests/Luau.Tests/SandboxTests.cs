using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Luau.Tests;

public sealed class SandboxTests
{
    [Fact]
    public void RootSandboxRejectsMissingBaseLibraryBeforeFreezingState()
    {
        using var root = TestBudgets.CreateRoot();

        var exception = Assert.Throws<InvalidOperationException>(root.SandboxRoot);

        Assert.Contains("OpenBaseLibrary", exception.Message);
        Assert.False(root.IsRootSandboxed);

        root.OpenBaseLibrary();
        root.SandboxRoot();
        using var child = root.CreateSandboxedThread();

        Assert.Equal(3, Assert.Single(child.DoString("return 1 + 2")).Read<int>());
    }

    [Fact]
    public void RootSandboxRejectsReplacedGlobalEnvironmentBeforeFreezingState()
    {
        using var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        using var replacement = root.CreateTable();
        root["_G"] = replacement;

        var exception = Assert.Throws<InvalidOperationException>(root.SandboxRoot);

        Assert.Contains("OpenBaseLibrary", exception.Message);
        Assert.False(root.IsRootSandboxed);
        root.OpenBaseLibrary();
        root.SandboxRoot();
    }

    [Fact]
    public void SandboxedSiblingsHaveIsolatedGlobals()
    {
        using var root = CreateSandboxedRoot();
        using var first = root.CreateSandboxedThread();
        using var second = root.CreateSandboxedThread();

        var firstResults = first.DoString("scriptValue = 41; return scriptValue");
        var secondResults = second.DoString("return scriptValue == nil");
        var firstAgainResults = first.DoString("return scriptValue");

        Assert.Equal(41, Assert.Single(firstResults).Read<int>());
        Assert.True(Assert.Single(secondResults).Read<bool>());
        Assert.Equal(41, Assert.Single(firstAgainResults).Read<int>());
    }

    [Fact]
    public void SandboxedChildCanReadButCannotReplaceProtectedRootGlobal()
    {
        using var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        root["hostAnswer"] = 42;
        root.SandboxRoot();
        using var child = root.CreateSandboxedThread();

        Assert.Equal(42, Assert.Single(child.DoString("return hostAnswer")).Read<int>());

        var exception = Assert.Throws<LuauException>(
            () => child.DoString("hostAnswer = 99"));

        Assert.Contains("protected host global 'hostAnswer'", exception.Message);
        Assert.Equal(42, Assert.Single(child.DoString("return hostAnswer")).Read<int>());
    }

    [Fact]
    public void ProtectedRootAssignmentFailsInsidePcallWithoutChangingRootValue()
    {
        using var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        root["hostAnswer"] = 42;
        root.SandboxRoot();
        using var child = root.CreateSandboxedThread();

        var results = child.DoString(
            """
            local ok, message = pcall(function()
                hostAnswer = 99
            end)

            return ok, tostring(message), hostAnswer
            """);

        Assert.Equal(3, results.Length);
        Assert.False(results[0].Read<bool>());
        Assert.Contains("protected host global 'hostAnswer'", results[1].Read<string>());
        Assert.Equal(42, results[2].Read<int>());
    }

    [Fact]
    public void RootApiTablesAreReadOnlyFromSandboxedChildren()
    {
        using var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        using var hostApi = root.CreateTable();
        hostApi["answer"] = 42;
        root["hostApi"] = hostApi;
        root.SandboxRoot();
        using var child = root.CreateSandboxedThread();

        var results = child.DoString(
            """
            local ok, message = pcall(function()
                hostApi.answer = 99
            end)

            return ok, tostring(message), hostApi.answer
            """);

        Assert.Equal(3, results.Length);
        Assert.False(results[0].Read<bool>());
        Assert.Contains("readonly", results[1].Read<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(42, results[2].Read<int>());
    }

    [Fact]
    public void EnvironmentMutationFunctionsAreUnavailableInSandboxedChildren()
    {
        using var root = CreateSandboxedRoot();
        using var child = root.CreateSandboxedThread();

        var results = child.DoString("return getfenv == nil, setfenv == nil");

        Assert.Equal(2, results.Length);
        Assert.True(results[0].Read<bool>());
        Assert.True(results[1].Read<bool>());
    }

    [Fact]
    public void NewSandboxedChildGlobalsRemainWritable()
    {
        using var root = CreateSandboxedRoot();
        using var child = root.CreateSandboxedThread();

        var results = child.DoString(
            "scriptValue = 1; scriptValue = scriptValue + 1; return scriptValue");

        Assert.Equal(2, Assert.Single(results).Read<int>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SandboxedStatesCanBeDisposedInEitherOrder(bool disposeChildFirst)
    {
        var root = CreateSandboxedRoot();
        var child = root.CreateSandboxedThread();

        try
        {
            if (disposeChildFirst)
            {
                child.Dispose();

                Assert.True(child.IsDisposed);
                Assert.False(root.IsDisposed);
                Assert.Equal(7, Assert.Single(root.DoString("return 7")).Read<int>());

                root.Dispose();
            }
            else
            {
                root.Dispose();

                Assert.True(root.IsDisposed);
                Assert.True(child.IsDisposed);
                child.Dispose();
            }

            Assert.True(root.IsDisposed);
            Assert.True(child.IsDisposed);
        }
        finally
        {
            child.Dispose();
            root.Dispose();
        }
    }

    [Fact]
    public void RequireCacheIsSharedAcrossSandboxedSiblings()
    {
        using var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        var requirer = new CountingRequirer();
        root.OpenRequireLibrary(requirer);
        root.SandboxRoot();
        using var first = root.CreateSandboxedThread();
        using var second = root.CreateSandboxedThread();

        var firstResults = first.DoString("return require('shared-module')");
        var secondResults = second.DoString("return require('shared-module')");

        Assert.Equal(73, Assert.Single(firstResults).Read<int>());
        Assert.Equal(73, Assert.Single(secondResults).Read<int>());
        Assert.Equal(1, requirer.LoadCount);
    }

    [Fact]
    public void CachedModuleClosureDoesNotCaptureFirstSandboxedSiblingEnvironment()
    {
        using var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        var requirer = new PrivateGlobalClosureRequirer();
        root.OpenRequireLibrary(requirer);
        root.SandboxRoot();
        using var first = root.CreateSandboxedThread();
        using var second = root.CreateSandboxedThread();

        var firstResults = first.DoString(
            "privateGlobal = 'first-only'; local readPrivate = require('closure-module'); return readPrivate() == nil, privateGlobal");
        var secondResults = second.DoString(
            "local readPrivate = require('closure-module'); return readPrivate() == nil, privateGlobal == nil");

        Assert.Equal(2, firstResults.Length);
        Assert.True(firstResults[0].Read<bool>());
        Assert.Equal("first-only", firstResults[1].Read<string>());
        Assert.Equal(2, secondResults.Length);
        Assert.True(secondResults[0].Read<bool>());
        Assert.True(secondResults[1].Read<bool>());
        Assert.Equal(1, requirer.LoadCount);
    }

    [Theory]
    [InlineData("return")]
    [InlineData("return 1, 2")]
    public void RequireRejectsInvalidResultCountsWithoutCachingOrPoisoningTheRoot(string source)
    {
        using var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        var requirer = new SourceRequirer(source);
        root.OpenRequireLibrary(requirer);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var exception = Assert.Throws<LuauManagedCallbackException>(
                () => root.DoString("return require('invalid-result-module')"));

            var moduleFailure = Assert.IsType<LuauException>(exception.InnerException);
            Assert.Contains("exactly 1 value", moduleFailure.Message);
            Assert.Equal(42, Assert.Single(root.DoString("return 42")).Read<int>());
        }

        Assert.Equal(2, requirer.LoadCount);
    }

    [Fact]
    public void RequireRejectsCircularDependenciesAndCleansTheInProgressEntry()
    {
        using var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        var requirer = new CircularRequirer();
        root.OpenRequireLibrary(requirer);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var exception = Assert.Throws<LuauManagedCallbackException>(
                () => root.DoString("return require('cycle')"));

            var cycleFailure = Assert.IsType<LuauException>(exception.InnerException);
            Assert.Contains("Circular module dependency", cycleFailure.Message);
            Assert.Equal(42, Assert.Single(root.DoString("return 42")).Read<int>());
        }

        Assert.Equal(2, requirer.LoadCount);
    }

    [Fact]
    public void SandboxedContextRejectsLibraryRegistrationFromChild()
    {
        using var root = CreateSandboxedRoot();
        using var child = root.CreateSandboxedThread();
        var requirer = new CountingRequirer();
        var customLibrary = new TrackingLibrary();

        var osError = Assert.Throws<InvalidOperationException>(child.OpenOSLibrary);
        var debugError = Assert.Throws<InvalidOperationException>(child.OpenDebugLibrary);
        var requireError = Assert.Throws<InvalidOperationException>(
            () => child.OpenRequireLibrary(requirer));
        var customError = Assert.Throws<InvalidOperationException>(
            () => child.OpenLibrary(customLibrary));

        Assert.All(
            [osError, debugError, requireError, customError],
            error => Assert.Contains("root Luau state", error.Message));
        Assert.Equal(0, customLibrary.RegisterCount);

        var results = child.DoString(
            "return os == nil, debug == nil, require == nil");

        Assert.Equal(3, results.Length);
        Assert.All(results, result => Assert.True(result.Read<bool>()));
    }

    [Fact]
    public void PreexistingChildCannotRegisterAnyLibraryFamilyBeforeOrAfterRootSandboxing()
    {
        using var root = TestBudgets.CreateRoot();
        using var child = root.CreateThread();
        var requirer = new CountingRequirer();
        var customLibrary = new TrackingLibrary();

        AssertChildRegistrationRejected(child, requirer, customLibrary);
        Assert.Equal(0, customLibrary.RegisterCount);
        Assert.Equal(0, ConstructedTrackingLibrary.ConstructionCount);

        root.OpenBaseLibrary();
        root.SandboxRoot();

        AssertChildRegistrationRejected(child, requirer, customLibrary);
        Assert.Equal(0, customLibrary.RegisterCount);
        Assert.Equal(0, ConstructedTrackingLibrary.ConstructionCount);
    }

    static void AssertChildRegistrationRejected(
        LuauState child,
        LuauRequirer requirer,
        TrackingLibrary customLibrary)
    {
        ConstructedTrackingLibrary.ResetConstructionCount();
        Action[] registrations =
        [
            child.OpenBaseLibrary,
            child.OpenMathLibrary,
            child.OpenTableLibrary,
            child.OpenStringLibrary,
            child.OpenCoroutineLibrary,
            child.OpenBit32Library,
            child.OpenUtf8Library,
            child.OpenOSLibrary,
            child.OpenDebugLibrary,
            child.OpenBufferLibrary,
            child.OpenVectorLibrary,
            child.OpenIntegerLibrary,
            () => child.OpenRequireLibrary(requirer),
            () => child.OpenLibrary(customLibrary),
            () => child.OpenLibrary<ConstructedTrackingLibrary>(),
        ];

        foreach (var registration in registrations)
        {
            var exception = Assert.Throws<InvalidOperationException>(registration);
            Assert.Contains("root Luau state", exception.Message);
        }
    }

    static LuauState CreateSandboxedRoot()
    {
        var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        root.SandboxRoot();
        return root;
    }

    sealed class CountingRequirer : LuauRequirer
    {
        int loadCount;

        public int LoadCount => Volatile.Read(ref loadCount);

        protected override bool TryLoadModule(
            LuauState state,
            string fullPath,
            string requireArgument,
            out LuauValue result)
        {
            Interlocked.Increment(ref loadCount);
            result = LuauValue.FromNumber(73);
            return true;
        }

        protected override bool TryGetAliasPath(
            string alias,
            [NotNullWhen(true)] out string? path)
        {
            path = null;
            return false;
        }
    }

    sealed class PrivateGlobalClosureRequirer : LuauRequirer
    {
        int loadCount;

        public int LoadCount => Volatile.Read(ref loadCount);

        protected override bool TryLoadModule(
            LuauState state,
            string fullPath,
            string requireArgument,
            out LuauValue result)
        {
            Interlocked.Increment(ref loadCount);
            result = ExecuteModuleSource(
                state,
                requireArgument,
                "return function() return privateGlobal end"u8,
                "@closure-module"u8);
            return true;
        }

        protected override bool TryGetAliasPath(
            string alias,
            [NotNullWhen(true)] out string? path)
        {
            path = null;
            return false;
        }
    }

    sealed class SourceRequirer(string source) : LuauRequirer
    {
        readonly byte[] source = Encoding.UTF8.GetBytes(source);
        int loadCount;

        public int LoadCount => Volatile.Read(ref loadCount);

        protected override bool TryLoadModule(
            LuauState state,
            string fullPath,
            string requireArgument,
            out LuauValue result)
        {
            Interlocked.Increment(ref loadCount);
            result = ExecuteModuleSource(
                state,
                requireArgument,
                source,
                "@invalid-result-module"u8);
            return true;
        }

        protected override bool TryGetAliasPath(
            string alias,
            [NotNullWhen(true)] out string? path)
        {
            path = null;
            return false;
        }
    }

    sealed class CircularRequirer : LuauRequirer
    {
        int loadCount;

        public int LoadCount => Volatile.Read(ref loadCount);

        protected override bool TryLoadModule(
            LuauState state,
            string fullPath,
            string requireArgument,
            out LuauValue result)
        {
            Interlocked.Increment(ref loadCount);
            result = ExecuteModuleSource(
                state,
                requireArgument,
                "return require('cycle')"u8,
                "@cycle"u8);
            return true;
        }

        protected override bool TryGetAliasPath(
            string alias,
            [NotNullWhen(true)] out string? path)
        {
            path = null;
            return false;
        }
    }

    sealed class TrackingLibrary : ILuauLibrary
    {
        int registerCount;

        public int RegisterCount => Volatile.Read(ref registerCount);

        public void RegisterTo(LuauState state)
        {
            Interlocked.Increment(ref registerCount);
        }
    }

    public sealed class ConstructedTrackingLibrary : ILuauLibrary
    {
        static int constructionCount;

        public ConstructedTrackingLibrary()
        {
            Interlocked.Increment(ref constructionCount);
        }

        public static int ConstructionCount => Volatile.Read(ref constructionCount);

        public static void ResetConstructionCount()
        {
            Volatile.Write(ref constructionCount, 0);
        }

        public void RegisterTo(LuauState state)
        {
        }
    }
}
