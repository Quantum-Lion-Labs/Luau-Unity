namespace Luau;

/// <summary>
/// Describes the managed lifecycle state of a child Luau coroutine.
/// Root states do not have a coroutine lifecycle status.
/// </summary>
/// <remarks>
/// A child that has yielded, or a coroutine created by script whose function
/// has not started, accepts only <c>Resume</c> until it finishes. Starting a
/// new call on it, for example with <c>DoString</c> or <c>ExecuteCompilerOutput</c>,
/// throws <see cref="System.InvalidOperationException"/> and leaves it
/// unchanged. A freshly created host thread and a <see cref="Dead"/> child
/// accept new calls.
/// </remarks>
public enum LuauThreadStatus : byte
{
    /// <summary>The child is freshly created or suspended at a yield.</summary>
    Suspended = 0,

    /// <summary>The child is currently executing through a managed operation.</summary>
    Running = 1,

    /// <summary>The child completed, failed, or was reset after a hard stop.</summary>
    Dead = 2,
}
