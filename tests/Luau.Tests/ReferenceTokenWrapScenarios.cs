namespace Luau.Tests;

/// <summary>
/// Scenarios that run inside the reference-wrap fixture's load context, one at
/// a time; see <see cref="ReferenceWrapFixture"/>. Each positions the process
/// allocator itself and leaves no reference it created alive.
/// </summary>
internal static class ReferenceTokenWrapScenarios
{
    const int Maximum = int.MaxValue;

    sealed class Target
    {
        public double Value;
    }

    static readonly LuauObjectDescriptor<Target> Descriptor = new(
        "WrapTarget",
        validateTarget: null,
        [
            LuauObjectMember<Target>.Property(
                "value",
                (target, context) => context.Return(target.Value),
                (target, context) => target.Value = context.Read<double>(2)),
        ]);

    // Values of each kind that scripts make; each carries a marker that
    // identifies it.
    const string MakeSource = """
        function make(kind, marker)
            if kind == "table" then
                return { marker = marker }
            elseif kind == "function" then
                return function() return marker end
            elseif kind == "userdata" then
                local value = newproxy(true)
                getmetatable(value).__index = function() return marker end
                return value
            elseif kind == "buffer" then
                local value = buffer.create(8)
                buffer.writef64(value, 0, marker)
                return value
            else
                return coroutine.create(function() return marker end)
            end
        end
        function markerOf(value)
            local kind = type(value)
            if kind == "table" then return value.marker end
            if kind == "function" then return value() end
            if kind == "userdata" then return value.marker end
            if kind == "buffer" then return buffer.readf64(value, 0) end
            return nil
        end
        """;

    static readonly string[] ScriptKinds = ["table", "function", "userdata", "buffer", "thread"];

    internal static int TokenOf(object wrapper)
    {
        using var access = ((ILuauReference)wrapper).AcquireReference();
        return access.Reference;
    }

    static int TokenOf(LuauValue value) =>
        TokenOf(value.TryRead<LuauState>(out var thread) ? thread : value.Read<object>());

    static LuauState CreateRoot()
    {
        var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        root.OpenBufferLibrary();
        root.OpenCoroutineLibrary();
        return root;
    }

    static void DisposeAll(IEnumerable<IDisposable> values)
    {
        foreach (var value in values)
        {
            value.Dispose();
        }
    }

    // A value made by a script, owned by the caller.
    static LuauValue Make(LuauState root, string kind, double marker)
    {
        var destination = new LuauValue[1];
        Assert.Equal(1, root.DoStringInto($"return make('{kind}', {marker})", destination));
        return destination[0];
    }

    static double MarkerOf(LuauState root, LuauValue value)
    {
        if (value.TryRead<LuauState>(out var thread))
        {
            using var resumed = thread.Resume(ReadOnlySpan<LuauValue>.Empty);
            return resumed[0].Read<double>();
        }

        using var markerOf = root["markerOf"].Read<LuauFunction>();
        using var results = markerOf.Invoke([value]);
        return results[0].Read<double>();
    }

    static IDisposable Owner(LuauValue value) =>
        value.TryRead<LuauState>(out var thread) ? thread : (IDisposable)value.Read<object>();

    internal static void ReferenceCreationAfterTheLastToken()
    {
        ReferenceWrapNative.RequireFixture();
        var baseline = ReferenceWrapNative.Live();
        using var first = CreateRoot();
        using var second = CreateRoot();
        using var reader = first.CreateFunction(context =>
            context.Return(context.Read<LuauTable>(0)["value"].Read<double>()));
        first["readTable"] = LuauValue.FromFunction(reader);
        var early = first.CreateTable();
        early["value"] = LuauValue.FromNumber(7);
        var earlyToken = TokenOf(early);

        ReferenceWrapNative.Seek(Maximum - 1);
        var penultimate = first.CreateTable();
        var last = second.CreateTable();
        Assert.Equal(Maximum - 1, TokenOf(penultimate));
        Assert.Equal(Maximum, TokenOf(last));

        // Every root, including a new one, keeps creating references after
        // the last value, from the bottom of the range, passing live values:
        // creation, results, and callback arguments alike.
        var wrapped = first.CreateTable();
        Assert.InRange(TokenOf(wrapped), 1, 1000);
        Assert.NotEqual(earlyToken, TokenOf(wrapped));
        using (var results = second.DoString("return { value = 2 }"))
        {
            Assert.Equal(2, results[0].Read<LuauTable>()["value"].Read<int>());
        }
        using (var third = CreateRoot())
        using (var table = third.CreateTable())
        {
            Assert.InRange(TokenOf(table), 1, 1000);
        }
        using (var results = first.DoString("return readTable({ value = 3 })"))
        {
            Assert.Equal(3, results[0].Read<int>());
        }

        Assert.Equal(7, early["value"].Read<int>());
        DisposeAll([early, penultimate, last, wrapped]);
        Assert.Equal(baseline, ReferenceWrapNative.Live());
        using var afterRelease = first.CreateTable();
    }

    // Every kind of reference, made by scripts and by the host, on three roots
    // while the cursor wraps, keeps naming its own value until it is released.
    internal static void EveryReferenceKindAcrossTheWrap()
    {
        ReferenceWrapNative.RequireFixture();
        var baseline = ReferenceWrapNative.Live();
        var roots = new[] { CreateRoot(), CreateRoot(), CreateRoot() };
        var held = new List<(LuauState Root, double Marker, LuauValue Value)>();
        var handles = new List<(LuauState Root, Target Target, LuauObjectHandle Handle)>();
        try
        {
            foreach (var root in roots)
            {
                root.DoString(MakeSource).Dispose();
            }

            ReferenceWrapNative.Seek(Maximum - 9);
            var marker = 0d;
            for (var round = 0; round < 2; round++)
            {
                foreach (var root in roots)
                {
                    foreach (var kind in ScriptKinds)
                    {
                        marker++;
                        held.Add((root, marker, Make(root, kind, marker)));
                    }

                    marker++;
                    var table = root.CreateTable();
                    table["marker"] = LuauValue.FromNumber(marker);
                    held.Add((root, marker, LuauValue.FromTable(table)));

                    marker++;
                    held.Add((root, marker, LuauValue.FromBuffer(root.CreateBuffer(BitConverter.GetBytes(marker)))));

                    marker++;
                    var target = new Target { Value = marker };
                    handles.Add((root, target, root.CreateHandle(target, Descriptor)));
                }
            }

            var tokens = held.Select(entry => TokenOf(entry.Value))
                .Concat(handles.Select(entry => TokenOf(entry.Handle)))
                .ToList();
            Assert.Equal(tokens.Count, tokens.Distinct().Count());
            Assert.Contains(tokens, token => token >= Maximum - 9);
            Assert.Contains(tokens, token => token < 1000);

            // Release every other value, then check that the rest still name
            // their own values.
            for (var index = held.Count - 1; index >= 0; index -= 2)
            {
                Owner(held[index].Value).Dispose();
                held.RemoveAt(index);
            }
            foreach (var (root, expected, value) in held)
            {
                Assert.Equal(expected, MarkerOf(root, value));
            }
            foreach (var (root, target, handle) in handles)
            {
                root["handle"] = LuauValue.FromObjectHandle(handle);
                using var results = root.DoString("handle.value = handle.value + 0.5 return handle.value");
                Assert.Equal(target.Value, results[0].Read<double>());
                root["handle"] = LuauValue.Nil;
            }
        }
        finally
        {
            foreach (var entry in held)
            {
                Owner(entry.Value).Dispose();
            }
            foreach (var entry in handles)
            {
                entry.Handle.Dispose();
            }
            DisposeAll(roots);
        }

        Assert.Equal(baseline, ReferenceWrapNative.Live());
    }

    // Values the process holds for a long time sit at the bottom of the
    // range; after the wrap the cursor passes them, and none is handed out
    // again while it is held.
    internal static void LiveValuesArePassedAfterTheWrap()
    {
        ReferenceWrapNative.RequireFixture();
        var baseline = ReferenceWrapNative.Live();
        var roots = new[] { CreateRoot(), CreateRoot() };
        var tables = new List<(LuauTable Table, double Marker)>();
        try
        {
            ReferenceWrapNative.Seek(1);
            for (var index = 0; index < 32; index++)
            {
                var table = roots[index % 2].CreateTable();
                table["marker"] = LuauValue.FromNumber(index);
                tables.Add((table, index));
            }
            var longLived = tables.Select(entry => TokenOf(entry.Table)).ToHashSet();
            Assert.Equal(32, longLived.Count);
            Assert.All(longLived, token => Assert.InRange(token, 1, 1000));

            ReferenceWrapNative.Seek(Maximum - 3);
            for (var index = 32; index < 96; index++)
            {
                var root = roots[index % 2];
                using var results = root.DoString($"return {{ marker = {index} }}");
                var table = results[0].Read<LuauTable>().Retain();
                tables.Add((table, index));
                Assert.DoesNotContain(TokenOf(table), longLived);
            }

            var all = tables.Select(entry => TokenOf(entry.Table)).ToList();
            Assert.Equal(all.Count, all.Distinct().Count());
            Assert.Contains(all, token => token >= Maximum - 3);
            foreach (var (table, marker) in tables)
            {
                Assert.Equal(marker, table["marker"].Read<double>());
            }
        }
        finally
        {
            DisposeAll(tables.Select(entry => entry.Table));
            DisposeAll(roots);
        }

        Assert.Equal(baseline, ReferenceWrapNative.Live());
    }

    // Each owner of references keeps working when its references were
    // issued on both sides of the wrap: the root, child and sandboxed
    // threads, script instances and their entrypoints, the module cache,
    // callback arguments and results, releases deferred during an operation,
    // and the host operation thread.
    internal static void EveryOwnerAcrossTheWrap()
    {
        ReferenceWrapNative.RequireFixture();
        var baseline = ReferenceWrapNative.Live();
        var modules = new LuauModuleMap(new Dictionary<string, byte[]>
        {
            ["counter"] = "local count = 0 return { next = function() count += 1 return count end }"u8.ToArray(),
        });
        var root = CreateRoot();
        var other = CreateRoot();
        LuauScriptInstance? before = null;
        LuauScriptInstance? after = null;
        LuauState? child = null;
        LuauState? yielded = null;
        try
        {
            var deferredToken = 0;
            var issuedDuringOperation = 0;
            using var deferring = root.CreateFunction(context =>
            {
                // Released during the operation, so the release is deferred
                // and the value stays live until the operation ends.
                var table = context.State.CreateTable();
                deferredToken = TokenOf(table);
                table.Dispose();
                ReferenceWrapNative.Seek(deferredToken);
                using var next = context.State.CreateTable();
                issuedDuringOperation = TokenOf(next);
            });
            using var sum = root.CreateFunction(context =>
            {
                var total = 0d;
                for (var index = 0; index < context.ArgumentCount; index++)
                {
                    total += context.Read<LuauTable>(index)["value"].Read<double>();
                }
                using var result = context.State.CreateTable();
                result["value"] = LuauValue.FromNumber(total);
                context.Return(result);
            });
            using var threadStatus = root.CreateFunction(context =>
                context.Return(context.Read<LuauState>(0).GetStatus().ToString()));
            root["deferring"] = LuauValue.FromFunction(deferring);
            root["sum"] = LuauValue.FromFunction(sum);
            root["threadStatus"] = LuauValue.FromFunction(threadStatus);
            root.OpenRequireLibrary(modules);
            root.SandboxRoot();

            ReferenceWrapNative.Seek(Maximum - 2);
            before = CreateInstance(root, "before");
            using (var results = root.DoString("return require('counter').next()"))
            {
                Assert.Equal(1, results[0].Read<int>());
            }
            child = root.CreateThread();
            // Sandboxed, so that it has globals of its own to write.
            yielded = root.CreateSandboxedThread();
            yielded.DoString("coroutine.yield()").Dispose();
            Assert.Equal(LuauThreadStatus.Suspended, yielded.GetStatus());

            // The wrap has happened by now.
            using var sandboxed = root.CreateSandboxedThread();
            after = CreateInstance(root, "after");
            using var otherTable = other.CreateTable();
            Assert.InRange(TokenOf(sandboxed), 1, 1000);

            foreach (var instance in new[] { before, after })
            {
                instance.GetRequiredEntrypoint("update").InvokeVoid(LuauValue.FromNumber(1));
                using var results = instance.GetRequiredEntrypoint("total").Invoke();
                Assert.Equal(1, results[0].Read<double>());
            }

            using (var results = root.DoString(
                "return require('counter').next(), rawequal(require('counter'), require('counter'))"))
            {
                Assert.Equal(2, results[0].Read<int>());
                Assert.True(results[1].Read<bool>());
            }

            using (var results = child.DoString("return sum({ value = 1 }, { value = 2 }, { value = 4 }).value"))
            {
                Assert.Equal(7, results[0].Read<double>());
            }
            using (var results = sandboxed.DoString("return threadStatus(coroutine.create(function() end))"))
            {
                Assert.Equal(nameof(LuauThreadStatus.Suspended), results[0].Read<string>());
            }

            // A global write on a yielded thread runs on the host operation
            // thread, which the root creates and references on first use.
            yielded["fromHost"] = LuauValue.FromNumber(5);
            using (var table = root.CreateTable())
            {
                table["value"] = LuauValue.FromNumber(6);
                yielded["table"] = LuauValue.FromTable(table);
            }
            yielded.Resume(ReadOnlySpan<LuauValue>.Empty).Dispose();
            using (var results = yielded.DoString("return fromHost + table.value"))
            {
                Assert.Equal(11, results[0].Read<double>());
            }

            root.DoString("deferring()").Dispose();
            Assert.NotEqual(deferredToken, issuedDuringOperation);
            ReferenceWrapNative.Seek(deferredToken);
            using (var table = root.CreateTable())
            {
                Assert.Equal(deferredToken, TokenOf(table));
            }
        }
        finally
        {
            before?.Dispose();
            after?.Dispose();
            child?.Dispose();
            yielded?.Dispose();
            root.Dispose();
            other.Dispose();
        }

        Assert.Equal(baseline, ReferenceWrapNative.Live());
    }

    static LuauScriptInstance CreateInstance(LuauState root, string name) =>
        LuauScriptInstance.CreateAsync(
            root,
            name,
            static (thread, _) => ValueTask.FromResult(thread.DoString("""
                local state = { total = 0 }
                return {
                    update = function(dt) state.total += dt end,
                    total = function() return state.total end,
                }
                """)))
            .AsTask().GetAwaiter().GetResult();

    // A handle keeps its identity across the wrap: another wrapper of the
    // same target while one is live shares its token, scripts see one
    // userdata, and a handle read back from a script after every wrapper was
    // released resolves to the same target, on its own root only.
    internal static void HandlesKeepTheirIdentityAcrossTheWrap()
    {
        ReferenceWrapNative.RequireFixture();
        var baseline = ReferenceWrapNative.Live();
        var first = CreateRoot();
        var second = CreateRoot();
        try
        {
            using var check = first.DoString("return function(a, b) return rawequal(a, b) end")[0]
                .Read<LuauFunction>().Retain();
            var target = new Target { Value = 1 };
            // The first handle of a descriptor on a root also creates its
            // binding, which holds references of its own.
            first.CreateHandle(new Target(), Descriptor).Dispose();
            second.CreateHandle(new Target(), Descriptor).Dispose();

            ReferenceWrapNative.Seek(Maximum);
            var original = first.CreateHandle(target, Descriptor);
            Assert.Equal(Maximum, TokenOf(original));
            first["saved"] = LuauValue.FromObjectHandle(original);
            var onSecond = second.CreateHandle(target, Descriptor);
            Assert.InRange(TokenOf(onSecond), 1, 1000);

            using (var again = first.CreateHandle(target, Descriptor))
            using (var retained = original.Retain())
            {
                Assert.Equal(Maximum, TokenOf(again));
                Assert.Equal(Maximum, TokenOf(retained));
                using var same = check.Invoke([LuauValue.FromObjectHandle(again), LuauValue.FromObjectHandle(original)]);
                Assert.True(same[0].Read<bool>());
            }

            // Every wrapper released: the token goes, the userdata stays in
            // the script, and reading it back issues a token after the wrap.
            original.Dispose();
            var readBack = first["saved"].Read<LuauObjectHandle>();
            Assert.InRange(TokenOf(readBack), 1, 1000);
            first["handle"] = LuauValue.FromObjectHandle(readBack);
            using (var results = first.DoString("handle.value = 3 return rawequal(handle, saved), saved.value"))
            {
                Assert.True(results[0].Read<bool>());
                Assert.Equal(3, results[1].Read<double>());
            }
            Assert.Equal(3, target.Value);

            // The second root's handle for the same target is its own.
            second["handle"] = LuauValue.FromObjectHandle(onSecond);
            using (var results = second.DoString("handle.value = 4 return handle.value"))
            {
                Assert.Equal(4, results[0].Read<double>());
            }
            Assert.Throws<InvalidOperationException>(() => second["foreign"] = LuauValue.FromObjectHandle(readBack));

            readBack.Dispose();
            onSecond.Dispose();
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }

        Assert.Equal(baseline, ReferenceWrapNative.Live());
    }

    // Closing a root with live references frees their values; another root is
    // issued the same values when the cursor reaches them, and the closed
    // root's wrappers stay disposed.
    internal static void ClosingARootFreesItsValues()
    {
        ReferenceWrapNative.RequireFixture();
        var baseline = ReferenceWrapNative.Live();
        using var survivor = CreateRoot();
        var closing = CreateRoot();
        closing.DoString(MakeSource).Dispose();

        ReferenceWrapNative.Seek(Maximum - 2);
        var closed = new List<LuauValue>();
        foreach (var kind in ScriptKinds)
        {
            closed.Add(Make(closing, kind, 1));
        }
        var closedTokens = closed.Select(TokenOf).ToList();
        Assert.Contains(closedTokens, token => token >= Maximum - 2);
        Assert.Contains(closedTokens, token => token < 1000);

        closing.Dispose();
        Assert.Equal(baseline, ReferenceWrapNative.Live());
        Assert.Throws<ObjectDisposedException>(() => closed[0].Read<LuauTable>()["marker"]);

        ReferenceWrapNative.Seek(Maximum - 2);
        var reissued = new List<LuauTable>();
        try
        {
            for (var index = 0; index < closedTokens.Count; index++)
            {
                var table = survivor.CreateTable();
                table["marker"] = LuauValue.FromNumber(index);
                reissued.Add(table);
            }
            Assert.Equal(closedTokens, reissued.Select(table => TokenOf(table)).ToList());

            // The closed root's wrappers neither read nor release anything.
            foreach (var value in closed)
            {
                Owner(value).Dispose();
            }
            Assert.Equal(baseline + (ulong)reissued.Count, ReferenceWrapNative.Live());
            for (var index = 0; index < reissued.Count; index++)
            {
                Assert.Equal(index, reissued[index]["marker"].Read<int>());
            }
        }
        finally
        {
            DisposeAll(reissued);
        }

        Assert.Equal(baseline, ReferenceWrapNative.Live());
    }

    // A released token is rejected before and after the wrap until its value
    // is issued again, and then names only the new owner's reference.
    internal static void StaleTokensAreRejectedUntilReissued()
    {
        ReferenceWrapNative.RequireFixture();
        var baseline = ReferenceWrapNative.Live();
        using var first = CreateRoot();
        using var second = CreateRoot();

        ReferenceWrapNative.Seek(Maximum - 1);
        var stale = first.CreateTable();
        var staleToken = TokenOf(stale);
        stale.Dispose();
        Assert.ThrowsAny<Exception>(() => LuauReferenceHelper.PushReference(first, staleToken, "push a stale token"));
        Assert.Equal(0, first.GetTop());

        var through = new List<LuauTable>();
        for (var index = 0; index < 4; index++)
        {
            through.Add(second.CreateTable());
        }
        Assert.DoesNotContain(staleToken, through.Select(table => TokenOf(table)));
        Assert.Contains(through, table => TokenOf(table) < staleToken);
        Assert.ThrowsAny<Exception>(() => LuauReferenceHelper.PushReference(first, staleToken, "push a stale token"));
        Assert.ThrowsAny<Exception>(() => LuauReferenceHelper.PushReference(second, staleToken, "push a stale token"));

        ReferenceWrapNative.Seek(staleToken);
        using var reissued = second.CreateTable();
        reissued["marker"] = LuauValue.FromNumber(9);
        Assert.Equal(staleToken, TokenOf(reissued));
        Assert.ThrowsAny<Exception>(() => LuauReferenceHelper.PushReference(first, staleToken, "push a stale token"));
        Assert.Equal(0, first.GetTop());
        Assert.Equal(9, reissued["marker"].Read<int>());

        DisposeAll(through);
        reissued.Dispose();
        Assert.Equal(baseline, ReferenceWrapNative.Live());
    }
}
