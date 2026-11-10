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

    [ReferenceWrapFact]
    public void EveryReferenceKindAcrossTheWrap() =>
        ReferenceWrapFixture.Run(nameof(ReferenceTokenWrapScenarios.EveryReferenceKindAcrossTheWrap));

    [ReferenceWrapFact]
    public void LiveValuesArePassedAfterTheWrap() =>
        ReferenceWrapFixture.Run(nameof(ReferenceTokenWrapScenarios.LiveValuesArePassedAfterTheWrap));

    [ReferenceWrapFact]
    public void EveryOwnerAcrossTheWrap() =>
        ReferenceWrapFixture.Run(nameof(ReferenceTokenWrapScenarios.EveryOwnerAcrossTheWrap));

    [ReferenceWrapFact]
    public void HandlesKeepTheirIdentityAcrossTheWrap() =>
        ReferenceWrapFixture.Run(nameof(ReferenceTokenWrapScenarios.HandlesKeepTheirIdentityAcrossTheWrap));

    [ReferenceWrapFact]
    public void ClosingARootFreesItsValues() =>
        ReferenceWrapFixture.Run(nameof(ReferenceTokenWrapScenarios.ClosingARootFreesItsValues));

    [ReferenceWrapFact]
    public void StaleTokensAreRejectedUntilReissued() =>
        ReferenceWrapFixture.Run(nameof(ReferenceTokenWrapScenarios.StaleTokensAreRejectedUntilReissued));
}
