using System.Text;

namespace Luau.Tests;

public sealed class NonYieldableCoroutineTests
{
    [Theory]
    [InlineData("metamethod", false)]
    [InlineData("metamethod", true)]
    [InlineData("sort", false)]
    [InlineData("sort", true)]
    [InlineData("gsub", false)]
    [InlineData("gsub", true)]
    [InlineData("require", false)]
    [InlineData("require", true)]
    public async Task RejectedParentSuspensionDoesNotCorruptLaterInvocations(string boundary, bool asyncCallback)
    {
        using var root = LuauState.Create();
        root.OpenBaseLibrary();
        root.OpenCoroutineLibrary();
        root.OpenTableLibrary();
        root.OpenStringLibrary();
        var callbackInvoked = false;
        using var host = asyncCallback
            ? root.CreateAsyncFunction("host", _ =>
            {
                callbackInvoked = true;
                return default;
            })
            : root.CreateFunction("host", _ => throw new InvalidOperationException("managed failure"));
        root["host"] = host;
        using var functionOwner = root.DoString("return function(a, b) return a + b end");
        var function = functionOwner.Read<LuauFunction>(0);
        var body = boundary switch
        {
            "metamethod" => "local proxy = setmetatable({}, {__index = function() return coroutine.resume(co) end}); return proxy.value",
            "sort" => "table.sort({2, 1}, function(a, b) coroutine.resume(co); return a < b end)",
            "gsub" => "return string.gsub('x', '.', function() return coroutine.resume(co) end)",
            "require" => "return coroutine.resume(co)",
            _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
        };
        var source = "local co = coroutine.create(function() return host(42) end); " +
            "local ok, err = pcall(function() " + body + " end); ";
        if (boundary == "require")
        {
            root.OpenRequireLibrary(new LuauModuleMap(new Dictionary<string, byte[]>
            {
                ["module"] = Encoding.UTF8.GetBytes(source + "return {ok = ok, err = tostring(err)}"),
            }));
            source = "local result = require('module'); return result.ok, result.err";
        }
        else
        {
            source += "return ok, tostring(err)";
        }

        // The break cannot reach the runner, so the callback cannot complete.
        // Report the managed failure and reset the operation even if Lua
        // catches the boundary error along the way.
        var failure = asyncCallback
            ? await Assert.ThrowsAsync<LuauManagedCallbackException>(async () =>
            {
                using var rejected = await root.DoStringAsync(source).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            })
            : Assert.Throws<LuauManagedCallbackException>(() =>
            {
                using var rejected = root.DoString(source);
            });
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.False(callbackInvoked);

        using (var result = function.Invoke(new LuauValue[] { 1d, 2d }))
        {
            Assert.Equal(3, Assert.Single(result).Read<int>());
        }
        // The child is no longer reachable from Lua. Collection must not leave
        // a dangling native continuation pointer for another execution to walk.
        root.CollectGarbage();
        using var afterCollection = function.Invoke(new LuauValue[] { 4d, 5d });
        Assert.Equal(9, Assert.Single(afterCollection).Read<int>());
    }
}
