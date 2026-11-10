namespace Luau.Tests;

/// <summary>
/// Managed behavior at the end of the reference-token range, against the
/// reference-wrap fixture (see <see cref="ReferenceWrapFixture"/>).
/// </summary>
public sealed class ReferenceTokenWrapTests
{
    [ReferenceWrapFact]
    public void ReferenceCreationAfterTheLastToken() =>
        ReferenceWrapFixture.Run(nameof(ReferenceTokenWrapScenarios.ReferenceCreationAfterTheLastToken));
}
