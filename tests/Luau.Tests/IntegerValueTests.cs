namespace Luau.Tests;

public sealed class IntegerValueTests
{
    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(long.MaxValue)]
    public void SignedIntegerBoundariesRoundTripLosslessly(long expected)
    {
        using var state = LuauState.Create();
        state["value"] = expected;

        var actual = state["value"];
        Assert.Equal(LuauType.Integer, actual.Type);
        Assert.Equal(expected, actual.Read<long>());
    }

    [Fact]
    public void IntegerDoubleConversionIsExactUnlessLossIsExplicit()
    {
        const long exactValue = 9_007_199_254_740_992;
        const long inexactValue = 9_007_199_254_740_993;

        var exact = LuauValue.FromInteger(exactValue);
        var inexact = LuauValue.FromInteger(inexactValue);
        var maximum = LuauValue.FromInteger(long.MaxValue);

        Assert.True(exact.TryRead<double>(out var exactDouble));
        Assert.Equal((double)exactValue, exactDouble);
        Assert.False(inexact.TryRead<double>(out _));
        Assert.False(maximum.TryRead<double>(out _));
        Assert.Throws<InvalidOperationException>(() => inexact.Read<double>());
        Assert.Equal((double)inexactValue, inexact.ReadDoubleLossy());
    }

    [Fact]
    public void IntegralReadsAreRangeChecked()
    {
        Assert.Equal(byte.MaxValue, LuauValue.FromInteger(byte.MaxValue).Read<byte>());
        Assert.False(LuauValue.FromInteger((long)byte.MaxValue + 1).TryRead<byte>(out _));
        Assert.False(LuauValue.FromInteger(-1).TryRead<uint>(out _));
        Assert.False(LuauValue.FromInteger((long)int.MaxValue + 1).TryRead<int>(out _));
        Assert.Equal((ulong)long.MaxValue, LuauValue.FromInteger(long.MaxValue).Read<ulong>());

        Assert.False(LuauValue.FromNumber((double)long.MaxValue).TryRead<long>(out _));
        Assert.False(LuauValue.FromNumber(double.PositiveInfinity).TryRead<long>(out _));
        Assert.Throws<InvalidOperationException>(() => LuauValue.FromInteger(256).Read<byte>());
    }

    [Fact]
    public void ManagedIntegralCreationUsesNumbersUpTo32BitsAndIntegersFor64Bits()
    {
        Assert.Equal(LuauValue.FromNumber(42), LuauValue.CreateFrom(42));
        Assert.Equal(LuauValue.FromNumber(byte.MaxValue), LuauValue.CreateFrom(byte.MaxValue));
        Assert.Equal(LuauValue.FromNumber(sbyte.MinValue), LuauValue.CreateFrom(sbyte.MinValue));
        Assert.Equal(LuauValue.FromNumber(short.MinValue), LuauValue.CreateFrom(short.MinValue));
        Assert.Equal(LuauValue.FromNumber(ushort.MaxValue), LuauValue.CreateFrom(ushort.MaxValue));
        Assert.Equal(LuauValue.FromNumber(int.MinValue), LuauValue.CreateFrom(int.MinValue));
        Assert.Equal(LuauValue.FromNumber(uint.MaxValue), LuauValue.CreateFrom(uint.MaxValue));
        Assert.Equal(LuauType.Integer, LuauValue.CreateFrom(42L).Type);
        Assert.Equal(LuauType.Integer, LuauValue.CreateFrom(long.MinValue).Type);
        Assert.Equal(LuauType.Integer, LuauValue.CreateFrom(42UL).Type);
        Assert.Equal(LuauType.Number, LuauValue.CreateFrom(42d).Type);
        Assert.Throws<OverflowException>(() => LuauValue.CreateFrom(ulong.MaxValue));
    }

    [Fact]
    public void ImplicitIntegralConversionsUseNumbersUpTo32BitsAndIntegersFor64Bits()
    {
        Assert.Equal(LuauValue.FromNumber(7), (LuauValue)(byte)7);
        Assert.Equal(LuauValue.FromNumber(-7), (LuauValue)(sbyte)-7);
        Assert.Equal(LuauValue.FromNumber(-7), (LuauValue)(short)-7);
        Assert.Equal(LuauValue.FromNumber(7), (LuauValue)(ushort)7);
        Assert.Equal(LuauValue.FromNumber(-7), (LuauValue)(-7));
        Assert.Equal(LuauValue.FromNumber(uint.MaxValue), (LuauValue)uint.MaxValue);
        Assert.Equal(LuauValue.FromInteger(7), (LuauValue)7L);
        Assert.Equal(LuauValue.FromInteger(7), (LuauValue)7UL);
        Assert.IsType<double>(((LuauValue)7).Read<object>());
        Assert.IsType<long>(((LuauValue)7L).Read<object>());
    }

    [Fact]
    public void HostUInt32MaximumRoundTripsAsAnExactNumber()
    {
        using var state = LuauState.Create();
        state.OpenBaseLibrary();
        state["big"] = uint.MaxValue;

        var results = state.DoString(
            "return typeof(big), big + 1 == 4294967296, big",
            "@integer/host-uint32-maximum.luau");

        Assert.Equal("number", results[0].Read<string>());
        Assert.True(results[1].Read<bool>());
        Assert.Equal(uint.MaxValue, results[2].Read<uint>());
        Assert.False(results[2].TryRead<int>(out _));
    }

    [Fact]
    public void HostInt32ValuesBehaveLikeScriptNumericLiterals()
    {
        using var state = LuauState.Create();
        state.OpenBaseLibrary();
        state.OpenMathLibrary();
        using (var getAmmo = state.CreateFunction("getAmmo", context => context.Return(30)))
        {
            state["getAmmo"] = getAmmo;
        }
        state["count"] = 3;
        using var table = state.CreateTable();
        table[1] = "first";
        state["items"] = table;

        var results = state.DoString(
            """
            local lookup = { [30] = "found" }
            return typeof(getAmmo()), getAmmo() == 30, getAmmo() + 1, math.max(getAmmo(), 5),
                tonumber(getAmmo()), lookup[getAmmo()], count * 2, items[1], #items
            """,
            "@integer/host-int32-values.luau");

        Assert.Equal("number", results[0].Read<string>());
        Assert.True(results[1].Read<bool>());
        Assert.Equal(31, results[2].Read<int>());
        Assert.Equal(30, results[3].Read<int>());
        Assert.Equal(30, results[4].Read<int>());
        Assert.Equal("found", results[5].Read<string>());
        Assert.Equal(6, results[6].Read<int>());
        Assert.Equal("first", results[7].Read<string>());
        Assert.Equal(1, results[8].Read<int>());
    }

    [Fact]
    public void GeneratedInt32MembersBehaveLikeScriptNumericLiterals()
    {
        using var state = LuauState.Create();
        state.OpenBaseLibrary();
        state.OpenLibrary(new IntegerInteropLibrary());

        var results = state.DoString(
            """
            return typeof(interop.ammo()), interop.ammo() == 30, interop.ammo() + 1,
                interop.double(interop.ammo()), interop.limit == 8, typeof(interop.id())
            """,
            "@integer/generated-int32-values.luau");

        Assert.Equal("number", results[0].Read<string>());
        Assert.True(results[1].Read<bool>());
        Assert.Equal(31, results[2].Read<int>());
        Assert.Equal(60, results[3].Read<int>());
        Assert.True(results[4].Read<bool>());
        Assert.Equal("integer", results[5].Read<string>());
    }

    [Fact]
    public void IntegerAndNumberHaveDistinctManagedAndNativeTableKeySemantics()
    {
        var integer = LuauValue.FromInteger(1);
        var number = LuauValue.FromNumber(1);
        Assert.NotEqual(integer, number);

        var dictionary = new Dictionary<LuauValue, string>
        {
            [integer] = "integer",
            [number] = "number",
        };
        Assert.Equal(2, dictionary.Count);

        using var state = LuauState.Create();
        using var table = state.CreateTable();
        table.RawSet(integer, "integer");
        table.RawSet(number, "number");

        Assert.Equal("integer", table.RawGet(integer).Read<string>());
        Assert.Equal("number", table.RawGet(number).Read<string>());
    }

    [Fact]
    public void IntegerLibraryMakesScriptConversionsAndArithmeticExplicit()
    {
        using var state = LuauState.Create();
        state.OpenIntegerLibrary();
        state["lhs"] = 40L;
        state["rhs"] = 2L;

        var results = state.DoString(
            "return integer.add(lhs, rhs), integer.tonumber(lhs), class == nil",
            "@integer/explicit-operations.luau");

        Assert.Equal(LuauType.Integer, results[0].Type);
        Assert.Equal(42, results[0].Read<long>());
        Assert.Equal(LuauType.Number, results[1].Type);
        Assert.Equal(40, results[1].Read<double>());
        Assert.True(results[2].Read<bool>());
    }

    [Fact]
    public void SpanTableCreationRetainsLuauArraySemantics()
    {
        using var state = LuauState.Create();
        using var table = state.CreateTable(["first", "second"]);
        state["values"] = table;

        var results = state.DoString("return #values, values[1], values[2]");

        Assert.Equal(2, results[0].Read<int>());
        Assert.Equal("first", results[1].Read<string>());
        Assert.Equal("second", results[2].Read<string>());
    }

    [Fact]
    public void OrdinaryScriptNumericLiteralsKeepNumberBehavior()
    {
        using var state = LuauState.Create();

        var result = Assert.Single(state.DoString("return 42", "@integer/script-number.luau"));

        Assert.Equal(LuauType.Number, result.Type);
        Assert.Equal(42, result.Read<int>());
    }

    [Theory]
    [InlineData(12, "class")]
    [InlineData(13, "object")]
    public void UnsupportedUpstreamObjectKindsFailDeliberately(
        int nativeType,
        string expectedKind)
    {
        using var state = LuauState.Create();
        state.PushString("stack-sentinel");
        var originalTop = state.GetTop();

        var exception = Assert.Throws<LuauUnsupportedValueException>(
            () => state.ToValueForNativeTypeFixture(-1, nativeType));

        Assert.Equal(expectedKind, exception.ValueKind);
        Assert.Contains(expectedKind, exception.Message, StringComparison.Ordinal);
        Assert.Equal(originalTop, state.GetTop());
        Assert.Equal("stack-sentinel", state.ToString(-1));
    }
}

[LuauLibrary("interop")]
public sealed partial class IntegerInteropLibrary
{
    [LuauMember("ammo")]
    public static int Ammo() => 30;

    [LuauMember("double")]
    public static int Double(int value) => value * 2;

    [LuauMember("limit")]
    public int Limit { get; } = 8;

    [LuauMember("id")]
    public static long Id() => 9_007_199_254_740_993;
}
