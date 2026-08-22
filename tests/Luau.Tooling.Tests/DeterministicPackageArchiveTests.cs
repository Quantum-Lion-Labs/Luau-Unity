using Luau.Tooling;

namespace Luau.Tooling.Tests;

public sealed class DeterministicPackageArchiveTests
{
    [Fact]
    public void Crc32MatchesStandardProbe()
    {
        Assert.Equal(0xcbf43926u, DeterministicPackageArchive.Crc32("123456789"u8));
    }
}
