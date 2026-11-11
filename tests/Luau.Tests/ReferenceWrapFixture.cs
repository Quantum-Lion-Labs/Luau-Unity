using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Luau.Tests;

/// <summary>
/// Runs managed scenarios against the reference-wrap fixture, a test-only build
/// of the native host that can position the process reference-token allocator
/// (<c>native/luau-host/tests/luau_host_reference_wrap_fixture.h</c>).
/// </summary>
/// <remarks>
/// <para>
/// The fixture is a second copy of the host, so the runtime that uses it must
/// be a second copy too: one load context holds this assembly, <c>Luau</c>, and
/// <c>Luau.Interop</c> again and resolves <c>luau_host</c> to the fixture. The
/// rest of the suite keeps using the host it was built against, and the
/// fixture's allocator moves only for the scenarios here, which run one at a
/// time.
/// </para>
/// <para>
/// <c>LUAU_HOST_REFERENCE_WRAP_FIXTURE</c> names the fixture; when it is set
/// and the file is missing, every scenario fails. Without it the tests look
/// for the <c>linux-x64</c> or <c>windows-x64</c> development build and are
/// skipped when there is none, as in a run against the committed plugins
/// alone.
/// </para>
/// </remarks>
internal static class ReferenceWrapFixture
{
    internal const string EnvironmentVariable = "LUAU_HOST_REFERENCE_WRAP_FIXTURE";

    static readonly object Gate = new();
    static readonly Lazy<(string? Path, string? SkipReason)> Location = new(Locate);
    static ReferenceWrapLoadContext? context;

    internal static string? SkipReason => Location.Value.SkipReason;

    internal static void Run(string scenario)
    {
        var path = Location.Value.Path ??
            throw new InvalidOperationException(Location.Value.SkipReason);

        lock (Gate)
        {
            context ??= new ReferenceWrapLoadContext(AppContext.BaseDirectory, path);
            var assembly = context.LoadFromAssemblyName(typeof(ReferenceWrapFixture).Assembly.GetName());
            var type = assembly.GetType(typeof(ReferenceTokenWrapScenarios).FullName!, throwOnError: true)!;
            var method = type.GetMethod(scenario, BindingFlags.Static | BindingFlags.NonPublic) ??
                throw new MissingMethodException(type.FullName, scenario);
            try
            {
                method.Invoke(null, null);
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            }
        }
    }

    static (string? Path, string? SkipReason) Locate()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrEmpty(configured))
        {
            var full = System.IO.Path.GetFullPath(configured);
            return File.Exists(full)
                ? (full, null)
                : (null, $"{EnvironmentVariable} names '{full}', which does not exist.");
        }

        var root = FindRepositoryRoot();
        if (root != null)
        {
            foreach (var candidate in DevelopmentCandidates(root))
            {
                if (File.Exists(candidate))
                {
                    return (candidate, null);
                }
            }
        }

        return (null,
            "The reference-wrap fixture is built with the native tests of the linux-x64 or " +
            $"windows-x64 preset; build one, or set {EnvironmentVariable}.");
    }

    static IEnumerable<string> DevelopmentCandidates(string root)
    {
        var build = System.IO.Path.Combine(root, "native", "luau-host", "out", "build");
        if (OperatingSystem.IsLinux())
        {
            yield return System.IO.Path.Combine(
                build, "linux-x64", "reference-wrap-fixture", "libluau_host_reference_wrap.so");
        }
        else if (OperatingSystem.IsWindows())
        {
            yield return System.IO.Path.Combine(
                build, "windows-x64", "reference-wrap-fixture", "Release", "luau_host_reference_wrap.dll");
        }
    }

    static string? FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory != null;
            directory = directory.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "Luau.slnx")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    sealed class ReferenceWrapLoadContext : AssemblyLoadContext
    {
        static readonly HashSet<string> Isolated = new(StringComparer.Ordinal)
        {
            "Luau",
            "Luau.Interop",
            "Luau.Tests",
        };

        readonly string baseDirectory;
        readonly string fixturePath;

        internal ReferenceWrapLoadContext(string baseDirectory, string fixturePath)
            : base("Luau reference-wrap fixture")
        {
            this.baseDirectory = baseDirectory;
            this.fixturePath = fixturePath;
        }

        protected override Assembly? Load(AssemblyName assemblyName) =>
            assemblyName.Name != null && Isolated.Contains(assemblyName.Name)
                ? LoadFromAssemblyPath(System.IO.Path.Combine(baseDirectory, assemblyName.Name + ".dll"))
                : null;

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
            unmanagedDllName == "luau_host"
                ? LoadUnmanagedDllFromPath(fixturePath)
                : IntPtr.Zero;
    }
}

/// <summary>A fact that needs the reference-wrap fixture.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class ReferenceWrapFactAttribute : FactAttribute
{
    public ReferenceWrapFactAttribute()
    {
        // A configured fixture that is missing fails in Run instead.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ReferenceWrapFixture.EnvironmentVariable)) &&
            ReferenceWrapFixture.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>
/// The fixture's test-only exports. They resolve to the fixture only inside its
/// load context; anywhere else the call fails with a missing entry point.
/// </summary>
internal static class ReferenceWrapNative
{
    [DllImport("luau_host", EntryPoint = "luau_host_test_reference_tokens_seek", ExactSpelling = true)]
    internal static extern void Seek(int next);

    [DllImport("luau_host", EntryPoint = "luau_host_test_reference_tokens_live", ExactSpelling = true)]
    internal static extern ulong Live();

    internal static void RequireFixture()
    {
        if (ReferenceEquals(
                AssemblyLoadContext.GetLoadContext(typeof(ReferenceWrapNative).Assembly),
                AssemblyLoadContext.Default))
        {
            throw new InvalidOperationException(
                "Reference-wrap scenarios run only through ReferenceWrapFixture.Run.");
        }
    }
}
