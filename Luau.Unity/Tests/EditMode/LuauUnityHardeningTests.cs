using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Luau.Unity.Tests
{
    public sealed class LuauUnityHardeningTests
    {
        [Test]
        public void UnityFacadeExportsOnlyApprovedProductTypes()
        {
            var actual = typeof(LuauUnity).Assembly
                .GetExportedTypes()
                .Where(type => type.Namespace != null &&
                    type.Namespace.StartsWith("Luau.Unity", StringComparison.Ordinal))
                .Select(type => type.FullName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.That(actual, Is.EqualTo(new[]
            {
                "Luau.Unity.LuauAsset",
                "Luau.Unity.LuauScriptDispatchResult",
                "Luau.Unity.LuauScriptPhase",
                "Luau.Unity.LuauScriptPhaseFailureMode",
                "Luau.Unity.LuauScriptPhaseOptions",
                "Luau.Unity.LuauScriptRegistration",
                "Luau.Unity.LuauScriptScheduler",
                "Luau.Unity.LuauStateExtensions",
                "Luau.Unity.LuauUnity",
                "Luau.Unity.LuauUnityObjectGuard",
                "Luau.Unity.LuauUnityOptions",
                "Luau.Unity.LuauUnityValue",
            }));
        }

        [Test]
        public void OptionsDefaultToHardenedModSettings()
        {
            var options = new LuauUnityOptions();

            Assert.That(options.OpenStandardLibraries, Is.True);
            Assert.That(options.OpenDebugLibrary, Is.False);
            Assert.That(options.ModuleMap, Is.Null);
            Assert.That(options.SandboxRoot, Is.True);
            Assert.That(options.CaptureUnitySynchronizationContext, Is.True);
            Assert.That(
                options.MaxPrintArguments,
                Is.EqualTo(LuauUnityOptions.DefaultMaxPrintArguments));
            Assert.That(
                options.MaxPrintUtf8Bytes,
                Is.EqualTo(LuauUnityOptions.DefaultMaxPrintUtf8Bytes));
            Assert.That(
                options.MaxPrintMessagesPerSecond,
                Is.EqualTo(LuauUnityOptions.DefaultMaxPrintMessagesPerSecond));
            Assert.That(options.StateOptions, Is.SameAs(LuauStateOptions.Default));
            Assert.That(options.StateOptions.BytecodePolicy, Is.EqualTo(LuauBytecodePolicy.Reject));
            Assert.That(options.StateOptions.MemoryLimitBytes, Is.Not.Null);
            Assert.That(options.StateOptions.MaxSourceBytes, Is.Not.Null);
            Assert.That(options.StateOptions.MaxBytecodeBytes, Is.Not.Null);
            Assert.That(options.StateOptions.DefaultExecutionOptions.WallClockLimit, Is.Not.Null);
            Assert.That(options.StateOptions.DefaultExecutionOptions.InterruptCountLimit, Is.Not.Null);
            Assert.That(options.StateOptions.DefaultExecutionOptions.MaxResultCount, Is.Not.Null);
        }

        [Test]
        public void DefaultPrintBoundsArgumentsAndManagedUtf8Output()
        {
            var messages = new List<string>();
            using var root = LuauUnity.CreateState(new LuauUnityOptions
            {
                MaxPrintArguments = 2,
                MaxPrintUtf8Bytes = 12,
                Log = messages.Add,
            });
            using var script = root.CreateSandboxedThread();

            using var results = script.DoString(
                "local calls = 0; " +
                "local value = setmetatable({}, { " +
                "__tostring = function() calls += 1; return string.rep('🐺', 10000) end }); " +
                "print('one', 'two', value); " +
                "print(value); " +
                "return calls",
                "@unity/bounded-print.luau");

            Assert.That(results, Has.Length.EqualTo(1));
            Assert.That(results[0].Read<int>(), Is.EqualTo(1),
                "Values beyond the argument limit must not have their __tostring metamethod invoked.");
            Assert.That(messages, Has.Count.EqualTo(2));
            Assert.That(messages[0], Is.EqualTo("one\ttwo..."));
            Assert.That(messages[1], Is.EqualTo("🐺🐺..."));
            Assert.That(Encoding.UTF8.GetByteCount(messages[0]), Is.LessThanOrEqualTo(12));
            Assert.That(Encoding.UTF8.GetByteCount(messages[1]), Is.LessThanOrEqualTo(12));
        }

        [TestCase(0, 128, "maxArguments")]
        [TestCase(4, 0, "maxUtf8Bytes")]
        public void RegisterPrintRejectsNonPositiveLimits(
            int maxArguments,
            int maxUtf8Bytes,
            string parameterName)
        {
            using var root = LuauState.Create();

            var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
                LuauUnity.RegisterPrint(root, _ => { }, maxArguments, maxUtf8Bytes));

            Assert.That(exception, Is.Not.Null);
            Assert.That(exception.ParamName, Is.EqualTo(parameterName));
        }

        [Test]
        public void RegisterPrintRejectsNonPositiveRateLimit()
        {
            using var root = LuauState.Create();

            var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
                LuauUnity.RegisterPrint(root, _ => { }, 4, 128, 0));

            Assert.That(exception, Is.Not.Null);
            Assert.That(exception.ParamName, Is.EqualTo("maxMessagesPerSecond"));
        }

        [Test]
        public void ConfiguredPrintRateLimitDropsCallsBeforeFormattingArguments()
        {
            var messages = new List<string>();
            using var root = LuauUnity.CreateState(new LuauUnityOptions
            {
                MaxPrintMessagesPerSecond = 2,
                Log = messages.Add,
            });
            using var script = root.CreateSandboxedThread();

            using var results = script.DoString(
                "local calls = 0; " +
                "local value = setmetatable({}, { " +
                "__tostring = function() calls += 1; return 'formatted' end }); " +
                "print('one'); print('two'); print(value); return calls",
                "@unity/rate-limited-print.luau");

            Assert.That(messages, Is.EqualTo(new[] { "one", "two" }));
            Assert.That(results[0].Read<int>(), Is.Zero,
                "Suppressed print calls must not invoke argument metamethods.");
        }

        [Test]
        public void DefaultStateCapturesUnitySynchronizationContextAndPreservesAllOptions()
        {
            var currentContext = SynchronizationContext.Current;
            Assert.That(currentContext, Is.Not.Null,
                "The Unity test runner did not install its main-thread synchronization context.");
            const long memoryLimit = 32L * 1024 * 1024;
            const int sourceLimit = 512 * 1024;
            const int bytecodeLimit = 2 * 1024 * 1024;
            const int managedHandleLimit = 17;

            using var root = LuauUnity.CreateState(new LuauUnityOptions
            {
                StateOptions = new LuauStateOptions
                {
                    MemoryLimitBytes = memoryLimit,
                    MaxSourceBytes = sourceLimit,
                    MaxBytecodeBytes = bytecodeLimit,
                    MaxManagedHandleCount = managedHandleLimit,
                    BytecodePolicy = LuauBytecodePolicy.RequireValidator,
                    BytecodeValidator = AcceptArtifactValidator.Instance,
                    DefaultExecutionOptions = new LuauExecutionOptions
                    {
                        WallClockLimit = TimeSpan.FromSeconds(1),
                        InterruptCountLimit = 100,
                        MaxResultCount = 8,
                    },
                },
                Log = _ => { },
            });

            var executionOptions = root.Options.DefaultExecutionOptions;
            var scheduler = executionOptions.ContinuationScheduler
                as LuauSynchronizationContextScheduler;

            Assert.That(scheduler, Is.Not.Null);
            Assert.That(scheduler.SynchronizationContext, Is.SameAs(currentContext));
            Assert.That(scheduler.CheckAccess(), Is.True);
            Assert.That(executionOptions.WallClockLimit, Is.EqualTo(TimeSpan.FromSeconds(1)));
            Assert.That(executionOptions.InterruptCountLimit, Is.EqualTo(100));
            Assert.That(executionOptions.MaxResultCount, Is.EqualTo(8));
            Assert.That(root.Options.MemoryLimitBytes, Is.EqualTo(memoryLimit));
            Assert.That(root.Options.MaxSourceBytes, Is.EqualTo(sourceLimit));
            Assert.That(root.Options.MaxBytecodeBytes, Is.EqualTo(bytecodeLimit));
            Assert.That(root.Options.MaxManagedHandleCount, Is.EqualTo(managedHandleLimit));
            Assert.That(root.Options.BytecodePolicy, Is.EqualTo(LuauBytecodePolicy.RequireValidator));
            Assert.That(root.Options.BytecodeValidator, Is.SameAs(AcceptArtifactValidator.Instance));
        }

        [Test]
        public void DefaultStateOpensOnlySafeLibrariesAndSandboxesRoot()
        {
            using var root = LuauUnity.CreateState(new LuauUnityOptions
            {
                Log = _ => { },
            });
            using var script = root.CreateSandboxedThread();

            Assert.That(root.IsRootSandboxed, Is.True);
            Assert.That(
                root.Options.MemoryLimitBytes,
                Is.EqualTo(LuauStateOptions.Default.MemoryLimitBytes));
            Assert.That(
                root.Options.MaxSourceBytes,
                Is.EqualTo(LuauStateOptions.Default.MaxSourceBytes));
            Assert.That(
                root.Options.MaxBytecodeBytes,
                Is.EqualTo(LuauStateOptions.Default.MaxBytecodeBytes));
            Assert.That(
                root.Options.DefaultExecutionOptions.WallClockLimit,
                Is.EqualTo(LuauStateOptions.Default.DefaultExecutionOptions.WallClockLimit));
            Assert.That(
                root.Options.DefaultExecutionOptions.InterruptCountLimit,
                Is.EqualTo(LuauStateOptions.Default.DefaultExecutionOptions.InterruptCountLimit));
            Assert.That(
                root.Options.DefaultExecutionOptions.MaxResultCount,
                Is.EqualTo(LuauStateOptions.Default.DefaultExecutionOptions.MaxResultCount));

            using var results = script.DoString(
                "return " +
                "type(math) == 'table' " +
                "and type(table) == 'table' " +
                "and type(string) == 'table' " +
                "and type(coroutine) == 'table' " +
                "and type(bit32) == 'table' " +
                "and type(utf8) == 'table' " +
                "and type(buffer) == 'table' " +
                "and type(vector) == 'table' " +
                "and type(print) == 'function', " +
                "os == nil " +
                "and debug == nil " +
                "and require == nil " +
                "and getfenv == nil " +
                "and setfenv == nil",
                "@unity/default-facade.luau");

            Assert.That(results, Has.Length.EqualTo(2));
            Assert.That(results[0].Read<bool>(), Is.True, "A safe standard library was unavailable.");
            Assert.That(results[1].Read<bool>(), Is.True, "An unsafe or opt-in global was exposed.");
            Assert.Throws<InvalidOperationException>(() => root["lateHostApi"] = 1);
        }

        [Test]
        public void HostApisAreRegisteredBeforeSandboxAndCannotBeShadowed()
        {
            var configured = false;
            using var root = LuauUnity.CreateState(new LuauUnityOptions
            {
                ConfigureHostApis = state =>
                {
                    configured = true;
                    state["hostAnswer"] = 42;
                },
                Log = _ => { },
            });
            using var script = root.CreateSandboxedThread();

            using var results = script.DoString(
                "local ok = pcall(function() hostAnswer = 99 end); " +
                "return not ok, hostAnswer",
                "@unity/protected-host-api.luau");

            Assert.That(configured, Is.True);
            Assert.That(results, Has.Length.EqualTo(2));
            Assert.That(results[0].Read<bool>(), Is.True);
            Assert.That(results[1].Read<int>(), Is.EqualTo(42));
        }

        [Test]
        public void DefaultStateRejectsPersistentArtifactWithoutValidator()
        {
            using var root = LuauUnity.CreateState(new LuauUnityOptions
            {
                Log = _ => { },
            });

            var output = LuauCompiler.Compile(Encoding.UTF8.GetBytes("return 42"));
            var artifact = LuauBytecodeArtifact.Create(
                output,
                "unity-tests/default-rejection",
                "tests:first-party");

            var exception = Assert.Throws<LuauException>(() => root.ExecuteVerifiedBytecode(
                artifact,
                "@unity/untrusted-bytecode.luau"));

            Assert.That(exception, Is.Not.Null);
            Assert.That(exception.Message, Does.Contain("disabled").IgnoreCase);
            Assert.That(exception.ChunkName, Is.EqualTo("@unity/untrusted-bytecode.luau"));
        }

        [Test]
        public void LuauAssetExecutionAlwaysCompilesStoredUtf8Source()
        {
            var asset = ScriptableObject.CreateInstance<LuauAsset>();
            try
            {
                asset.name = "source-only-asset";
                var source = Encoding.UTF8.GetBytes("return 42");
                var serialized = new SerializedObject(asset);
                var bytes = serialized.FindProperty("bytes");
                bytes.arraySize = source.Length;
                for (var index = 0; index < source.Length; index++)
                {
                    bytes.GetArrayElementAtIndex(index).intValue = source[index];
                }

                serialized.ApplyModifiedPropertiesWithoutUndo();

                using var root = LuauUnity.CreateState(new LuauUnityOptions
                {
                    Log = _ => { },
                });
                using var script = root.CreateSandboxedThread();

                using var results = script.Execute(asset);
                Assert.That(results, Has.Length.EqualTo(1));
                Assert.That(results[0].Read<int>(), Is.EqualTo(42));
                Assert.That(serialized.FindProperty("isPrecompiled"), Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void PersistentAssetPolicyAndSizeChecksPrecedeArtifactReconstruction()
        {
            var asset = ScriptableObject.CreateInstance<LuauAsset>();
            try
            {
                asset.name = "fabricated-bytecode";
                var serialized = new SerializedObject(asset);
                serialized.FindProperty("contentKind").intValue = 1;
                var bytes = serialized.FindProperty("bytes");
                bytes.arraySize = 2;
                bytes.GetArrayElementAtIndex(0).intValue = 1;
                bytes.GetArrayElementAtIndex(1).intValue = 2;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                using (var rejecting = LuauState.Create())
                {
                    var exception = Assert.Throws<LuauException>(() => rejecting.Execute(asset));
                    Assert.That(exception.Message, Does.Contain("disabled").IgnoreCase);
                }

                using (var bounded = LuauState.Create(new LuauStateOptions
                {
                    BytecodePolicy = LuauBytecodePolicy.RequireValidator,
                    BytecodeValidator = AcceptArtifactValidator.Instance,
                    MaxBytecodeBytes = 1,
                }))
                {
                    var exception = Assert.Throws<LuauLoadLimitException>(() => bounded.Execute(asset));
                    Assert.That(exception.ActualBytes, Is.EqualTo(2));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void ModuleMapCopiesInputsAndCanonicalizesRequireIdentity()
        {
            var moduleSource = Encoding.UTF8.GetBytes("mark(); return {}");
            var modules = new Dictionary<string, byte[]>
            {
                ["folder/module.luau"] = moduleSource,
            };
            var aliases = new Dictionary<string, string>
            {
                ["mod"] = "folder",
            };
            var moduleMap = new LuauModuleMap(modules, aliases);

            moduleSource[0] = (byte)'x';
            modules.Clear();
            aliases["mod"] = "other";

            var executions = 0;
            using var root = LuauUnity.CreateState(new LuauUnityOptions
            {
                ModuleMap = moduleMap,
                ConfigureHostApis = state =>
                {
                    state["mark"] = state.CreateFunction("mark", _ => executions++);
                },
                Log = _ => { },
            });
            using var script = root.CreateSandboxedThread();

            using var results = script.DoString(
                "local a = require('folder/module'); " +
                "local b = require('./folder/module.luau'); " +
                "local c = require('/folder/module'); " +
                "local d = require('@mod/module'); " +
                "return a == b and b == c and c == d",
                "@unity/module-map.luau");

            Assert.That(results[0].Read<bool>(), Is.True);
            Assert.That(executions, Is.EqualTo(1));
        }

        [Test]
        public void ModuleMapRejectsNamespaceTraversalAndCanonicalDuplicates()
        {
            var traversal = Assert.Throws<ArgumentException>(() =>
                LuauModuleMap.CanonicalizeModuleId("../other-mod/secret"));
            Assert.That(traversal, Is.Not.Null);

            var undeclaredAlias = Assert.Throws<ArgumentException>(() =>
                LuauModuleMap.CanonicalizeModuleId("@other-mod/secret"));
            Assert.That(undeclaredAlias, Is.Not.Null);

            var duplicate = Assert.Throws<ArgumentException>(() =>
                new LuauModuleMap(new Dictionary<string, byte[]>
                {
                    ["module"] = Encoding.UTF8.GetBytes("return 1"),
                    ["./module.luau"] = Encoding.UTF8.GetBytes("return 2"),
                }));
            Assert.That(duplicate, Is.Not.Null);
        }

        [Test]
        public void ModuleMapCannotResolveOutsideItsExplicitNamespace()
        {
            var moduleMap = new LuauModuleMap(new Dictionary<string, byte[]>
            {
                ["allowed/module"] = Encoding.UTF8.GetBytes("return 1"),
            });
            using var root = LuauUnity.CreateState(new LuauUnityOptions
            {
                ModuleMap = moduleMap,
                Log = _ => { },
            });
            using var script = root.CreateSandboxedThread();

            var exception = Assert.Throws<LuauManagedCallbackException>(() =>
                script.DoString("return require('not-in-map')"));

            Assert.That(exception, Is.Not.Null);
            Assert.That(exception.Message, Does.Contain("not found").IgnoreCase);
        }

        sealed class AcceptArtifactValidator : ILuauBytecodeValidator
        {
            public static AcceptArtifactValidator Instance { get; } = new();

            public bool IsValid(
                LuauBytecodeArtifact artifact,
                ReadOnlySpan<byte> bytecode) => true;
        }
    }
}
