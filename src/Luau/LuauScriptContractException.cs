namespace Luau;

/// <summary>
/// Reports that a script instance did not satisfy its initialization or
/// exported-entrypoint contract.
/// </summary>
public sealed class LuauScriptContractException : LuauException
{
    /// <summary>
    /// Initializes a script contract failure.
    /// </summary>
    /// <param name="instanceName">The host-provided script instance name.</param>
    /// <param name="entrypointName">
    /// The requested export name, or <see langword="null"/> for an initialization failure.
    /// </param>
    /// <param name="message">The failure detail, before the diagnostic label is applied.</param>
    /// <param name="innerException">The exception that caused this failure, when available.</param>
    public LuauScriptContractException(
        string instanceName,
        string? entrypointName,
        string message,
        Exception? innerException = null)
        : base(
            LuauDiagnosticMessages.WithChunk(
                message ?? throw new ArgumentNullException(nameof(message)),
                CreateOperationLabel(instanceName, entrypointName)),
            CreateOperationLabel(instanceName, entrypointName),
            innerException)
    {
        InstanceName = ValidateInstanceName(instanceName);
        EntrypointName = entrypointName;
    }

    /// <summary>Gets the host-provided script instance name.</summary>
    public string InstanceName { get; }

    /// <summary>
    /// Gets the requested export name, or <see langword="null"/> for an
    /// initialization failure.
    /// </summary>
    public string? EntrypointName { get; }

    static string CreateOperationLabel(string instanceName, string? entrypointName)
    {
        instanceName = ValidateInstanceName(instanceName);
        return entrypointName == null ? instanceName : $"{instanceName}:{entrypointName}";
    }

    static string ValidateInstanceName(string instanceName)
    {
        if (instanceName == null)
        {
            throw new ArgumentNullException(nameof(instanceName));
        }
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            throw new ArgumentException(
                "A script instance name must contain a non-whitespace character.",
                nameof(instanceName));
        }

        return instanceName;
    }
}
