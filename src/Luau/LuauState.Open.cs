using Luau.Internal.Interop;
using static Luau.Internal.Interop.NativeMethods;

namespace Luau;

unsafe partial class LuauState
{
    /// <summary>
    /// Opens the base library on the root state before <see cref="SandboxRoot"/>.
    /// Child states and roots already sandboxed are rejected.
    /// </summary>
    public void OpenBaseLibrary()
    {
        OpenStandardLibrary(ProtectedStandardLibrary.Base, "base");
    }

    /// <summary>
    /// Opens the math library on the root state before <see cref="SandboxRoot"/>.
    /// Child states and roots already sandboxed are rejected.
    /// </summary>
    public void OpenMathLibrary()
    {
        OpenStandardLibrary(ProtectedStandardLibrary.Math, "math");
    }

    /// <summary>
    /// Opens the table library on the root state before <see cref="SandboxRoot"/>.
    /// Child states and roots already sandboxed are rejected.
    /// </summary>
    public void OpenTableLibrary()
    {
        OpenStandardLibrary(ProtectedStandardLibrary.Table, "table");
    }

    /// <summary>
    /// Opens the string library on the root state before <see cref="SandboxRoot"/>.
    /// Child states and roots already sandboxed are rejected.
    /// </summary>
    public void OpenStringLibrary()
    {
        OpenStandardLibrary(ProtectedStandardLibrary.String, "string");
    }

    /// <summary>
    /// Opens the coroutine library on the root state before <see cref="SandboxRoot"/>.
    /// Child states and roots already sandboxed are rejected.
    /// </summary>
    public void OpenCoroutineLibrary()
    {
        OpenStandardLibrary(ProtectedStandardLibrary.Coroutine, "coroutine");
    }

    /// <summary>
    /// Opens the bit32 library on the root state before <see cref="SandboxRoot"/>.
    /// Child states and roots already sandboxed are rejected.
    /// </summary>
    public void OpenBit32Library()
    {
        OpenStandardLibrary(ProtectedStandardLibrary.Bit32, "bit32");
    }

    /// <summary>
    /// Opens the UTF-8 library on the root state before <see cref="SandboxRoot"/>.
    /// Child states and roots already sandboxed are rejected.
    /// </summary>
    public void OpenUtf8Library()
    {
        OpenStandardLibrary(ProtectedStandardLibrary.Utf8, "utf8");
    }

    /// <summary>
    /// Opens the privileged OS library on the root state before
    /// <see cref="SandboxRoot"/>. Child states and roots already sandboxed are
    /// rejected. This capability must never be exposed to untrusted mods.
    /// </summary>
    public void OpenOSLibrary()
    {
        OpenStandardLibrary(ProtectedStandardLibrary.OS, "os");
    }

    /// <summary>
    /// Opens the privileged debug library on the root state before
    /// <see cref="SandboxRoot"/>. Child states and roots already sandboxed are
    /// rejected. This capability weakens script isolation and must never be
    /// exposed to untrusted mods.
    /// </summary>
    public void OpenDebugLibrary()
    {
        OpenStandardLibrary(ProtectedStandardLibrary.Debug, "debug");
    }

    /// <summary>
    /// Opens the buffer library on the root state before <see cref="SandboxRoot"/>.
    /// Child states and roots already sandboxed are rejected.
    /// </summary>
    public void OpenBufferLibrary()
    {
        OpenStandardLibrary(ProtectedStandardLibrary.Buffer, "buffer");
    }

    /// <summary>
    /// Opens the vector library on the root state before <see cref="SandboxRoot"/>.
    /// Child states and roots already sandboxed are rejected.
    /// </summary>
    public void OpenVectorLibrary()
    {
        OpenStandardLibrary(ProtectedStandardLibrary.Vector, "vector");
    }

    /// <summary>
    /// Opens upstream Luau's signed 64-bit integer library on the root state
    /// before <see cref="SandboxRoot"/>. Child states and roots already
    /// sandboxed are rejected. Integer values are deliberately distinct from
    /// ordinary Luau numbers; use <c>integer.tonumber</c> for an explicit lossy
    /// conversion in script.
    /// </summary>
    public void OpenIntegerLibrary()
    {
        OpenStandardLibrary(ProtectedStandardLibrary.Integer, "integer");
    }

    void OpenStandardLibrary(ProtectedStandardLibrary library, string name)
    {
        ThrowIfRootConfigurationUnavailable();
        using var access = EnterNativeAccess();
        var originalTop = luau_host_stack_get_top(l);

        try
        {
            var resultCount = 0;
            LuauNativeProtection.Prepare(context);
            var status = luau_host_open_library(l, (LuauHostLibrary)library, &resultCount);
            if (status == LuauHostStatus.ThreadNotRunnable)
            {
                // The root is suspended by a pending async callback.
                OpenLibraryOnThreadSharingGlobals((LuauHostLibrary)library, $"open the {name} library");
            }
            else
            {
                LuauNativeProtection.ThrowIfFailed(this, l, status, $"open the {name} library");
            }
        }
        finally
        {
            SetTop(originalTop);
        }
    }

    /// <summary>
    /// Registers managed <c>require</c> support on the root state before root
    /// sandboxing. Child states and roots already sandboxed are rejected.
    /// </summary>
    public void OpenRequireLibrary(LuauRequirer requirer)
    {
        ThrowIfRootConfigurationUnavailable();
        if (requirer == null)
        {
            throw new ArgumentNullException(nameof(requirer));
        }

        this["require"] = CreateFunction("require", call =>
        {
            var path = call.Read<string>(0);
            if (requirer.TryLoad(call.State, path, out var result))
            {
                call.Return(result);
                return;
            }

            throw new LuauException($"module '{path}' not found");
        });
    }

    /// <summary>
    /// Registers a generated host library on the root state before root
    /// sandboxing. Child states and roots already sandboxed are rejected.
    /// </summary>
    public void OpenLibrary<T>(T library)
        where T : ILuauLibrary
    {
        ThrowIfRootConfigurationUnavailable();
        if (library is null)
        {
            throw new ArgumentNullException(nameof(library));
        }
        library.RegisterTo(this);

        if (library is IDisposable disposable)
        {
            disposables.Add(disposable);
        }
    }

    /// <summary>
    /// Constructs and registers a generated host library on the root state
    /// before root sandboxing. Child states and roots already sandboxed are
    /// rejected.
    /// </summary>
    public void OpenLibrary<T>()
        where T : ILuauLibrary, new()
    {
        ThrowIfRootConfigurationUnavailable();
        OpenLibrary(new T());
    }

    void ThrowIfRootConfigurationUnavailable()
    {
        ThrowIfDisposed();
        if (!IsMainThread)
        {
            ThrowHelper.ThrowInvalidOperationException(
                "Libraries and host APIs can only be registered on the root Luau state.");
        }

        if (context.IsRootSandboxed)
        {
            ThrowHelper.ThrowInvalidOperationException(
                "Libraries and host APIs must be registered before SandboxRoot is applied.");
        }
    }

    enum ProtectedStandardLibrary
    {
        Base = 0,
        Coroutine = 1,
        Table = 2,
        OS = 3,
        String = 4,
        Bit32 = 5,
        Buffer = 6,
        Utf8 = 7,
        Math = 8,
        Debug = 9,
        Vector = 10,
        Integer = 11,
    }
}
