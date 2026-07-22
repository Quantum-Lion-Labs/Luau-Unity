namespace Luau;

/// <summary>
/// Loads and executes one script on its owned sandboxed thread.
/// </summary>
/// <param name="thread">The sandboxed child thread owned by the script instance.</param>
/// <param name="cancellationToken">Cancellation requested by the creating host.</param>
/// <returns>
/// A result scope containing the script's returned values. A valid script
/// instance returns exactly one table.
/// </returns>
public delegate ValueTask<LuauResultScope> LuauScriptLoader(
    LuauState thread,
    CancellationToken cancellationToken);
