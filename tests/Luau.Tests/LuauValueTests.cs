namespace Luau.Tests;

public sealed class LuauValueTests
{
    [Fact]
    public void NilReadsAsNormalManagedNull()
    {
        Assert.True(LuauValue.Nil.TryRead<object>(out var value));
        Assert.Null(value);
        Assert.Null(LuauValue.Nil.Read<string>());
    }

    [Fact]
    public void UserdataEqualityAndHashingUseWrapperIdentity()
    {
        using var state = TestBudgets.CreateRoot();
        using var first = new LuauUserData(state, -1);
        using var second = new LuauUserData(state, -1);
        var firstValue = LuauValue.FromUserData(first);
        var sameValue = LuauValue.FromUserData(first);
        var secondValue = LuauValue.FromUserData(second);

        Assert.Equal(firstValue, sameValue);
        Assert.Equal(firstValue.GetHashCode(), sameValue.GetHashCode());
        Assert.NotEqual(firstValue, secondValue);
    }

    [Fact]
    public void ArbitraryStructsAreNotConvertedToUserdata()
    {
        using var state = TestBudgets.CreateRoot();

        Assert.Throws<ArgumentException>(() => LuauValue.CreateFrom(new UnsupportedStruct(1)));
    }

    readonly record struct UnsupportedStruct(int Value);
}
