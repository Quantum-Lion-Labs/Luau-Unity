using System;
using System.Collections;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Luau;
using UnityEngine;

namespace Luau.Unity.Verification
{
    /// <summary>
    /// Runs a minimal native-plugin and sandbox smoke test inside a player.
    /// The pass/fail markers are intentionally stable for log-based CI checks.
    /// </summary>
    [AddComponentMenu("Luau/Verification/Luau Player Smoke")]
    [DisallowMultipleComponent]
    public sealed class LuauPlayerSmoke : MonoBehaviour
    {
        const int BackgroundCompilationStressIterationsPerLane = 1000;

        public const string BackgroundAssetResourceName = "__LuauPlayerSmokeBackground";
        public const string BackgroundSource =
            "capability.Value = 40; " +
            "capability:Increment(2); " +
            "capability.Position = vector.create(1, 2, 3); " +
            "return smokeHost.assertUnityThread(), hostManualAddOne(314), " +
            "capability.Value, capability.Hidden == nil";
        public const string FirstPartyAssetResourceName = "__LuauPlayerSmokeFirstParty";
        public const string FirstPartySource = "return 2718";
        public const string FirstPartyProvenanceId = "tests:player-smoke/v1";
        public const string PassedMarker = "LUAU_PLAYER_SMOKE_PASS";
        public const string FailedMarker = "LUAU_PLAYER_SMOKE_FAIL";

        [SerializeField] bool quitOnCompletion = true;

        public bool QuitOnCompletion
        {
            get => quitOnCompletion;
            set => quitOnCompletion = value;
        }

        IEnumerator Start()
        {
            var smoke = RunSmokeAsync();
            while (!smoke.IsCompleted)
            {
                yield return null;
            }

            var exitCode = smoke.GetAwaiter().GetResult() ? 0 : 1;
            if (quitOnCompletion)
            {
                yield return QuitAfterLogFlush(exitCode);
            }
        }

        public static async Task<bool> RunSmokeAsync()
        {
            try
            {
                var unityThreadId = System.Environment.CurrentManagedThreadId;
                using var root = LuauUnity.CreateState(new LuauUnityOptions
                {
                    UseFirstPartyBytecode = true,
                    StateOptions = new LuauStateOptions
                    {
                        MemoryLimitBytes = 32 * 1024 * 1024,
                        DefaultExecutionOptions = LuauExecutionOptions.Default with
                        {
                            // Player startup and first-use async dispatch can pause
                            // beyond the ordinary 250 ms host default on mobile VR.
                            WallClockLimit = TimeSpan.FromSeconds(5),
                        },
                        BytecodePolicy = LuauBytecodePolicy.Reject,
                    },
                    ConfigureHostApis = state =>
                    {
                        state["hostAnswer"] = 42L;
                        state.OpenLibrary(new LuauPlayerSmokeHost(unityThreadId));
                        state["hostManualAddOne"] = state.CreateFunction(
                            "hostManualAddOne",
                            context => context.Return(context.Read<long>(0) + 1));
                    },
                    Log = message => Debug.Log("[Luau] " + message),
                });
                using var first = root.CreateSandboxedThread();
                using var second = root.CreateSandboxedThread();

                using var firstResult = first.DoString(
                    "scriptLocal = 123; " +
                    "local protected = not pcall(function() hostAnswer = 99 end); " +
                    "return math.floor(41.9) == 41 " +
                    "and protected " +
                    "and os == nil " +
                    "and debug == nil " +
                    "and require == nil " +
                    "and getfenv == nil " +
                    "and setfenv == nil, " +
                    "hostAnswer, " +
                    "hostManualAddOne(41), " +
                    "smokeHost.addOne(41)",
                    "@unity/player-smoke-first.luau");
                using var secondResult = second.DoString(
                    "return scriptLocal == nil, hostAnswer",
                    "@unity/player-smoke-second.luau");
                using var asyncResult = await first.DoStringAsync(
                    "local answer = smokeHost.asyncAnswer(); return answer, smokeHost.assertUnityThread()",
                    "@unity/player-smoke-async.luau");
                var capabilityObject = new GameObject("Luau Player Smoke Capability");
                var capability = capabilityObject.AddComponent<LuauPlayerSmokeCapability>();
                LuauResultScope compiledResult = null;
                LuauResultScope firstPartyResult = null;
                try
                {
                    using var capabilityHandle = root.CreateHandle(capability);
                    first["capability"] = capabilityHandle;
                    var validSource = Encoding.UTF8.GetBytes(BackgroundSource);
                    var backgroundAsset = Resources.Load<LuauAsset>(BackgroundAssetResourceName);
                    if (backgroundAsset == null)
                    {
                        throw new LuauException(
                            "The importer-produced background smoke asset was not included in the player.");
                    }
                    try
                    {
                        var invalidSource = Encoding.UTF8.GetBytes("local broken = )");
                        var nearLimitSource = new byte[1024 * 1024 - 32];
                        for (var index = 0; index < nearLimitSource.Length; index++)
                        {
                            nearLimitSource[index] = (byte)' ';
                        }
                        Buffer.BlockCopy(
                            validSource,
                            0,
                            nearLimitSource,
                            nearLimitSource.Length - validSource.Length,
                            validSource.Length);

                        _ = await Task.Run(async () => await StressCompilationServiceAsync(
                                validSource,
                                invalidSource,
                                nearLimitSource)
                            .ConfigureAwait(false));
                        compiledResult = await first.ExecuteAsync(backgroundAsset);
                    }
                    finally
                    {
                        Resources.UnloadAsset(backgroundAsset);
                    }

                    var firstPartyAsset = Resources.Load<LuauAsset>(FirstPartyAssetResourceName);
                    if (firstPartyAsset == null)
                    {
                        throw new LuauException(
                            "The importer-produced first-party smoke asset was not included in the player.");
                    }
                    try
                    {
                        if (!firstPartyAsset.IsPrecompiled)
                        {
                            throw new LuauException(
                                "The first-party smoke asset did not contain precompiled bytecode.");
                        }

                        firstPartyResult = await first.ExecuteAsync(firstPartyAsset);
                    }
                    finally
                    {
                        Resources.UnloadAsset(firstPartyAsset);
                    }

                    if (compiledResult.Length != 4 ||
                        !compiledResult[0].Read<bool>() ||
                        compiledResult[1].Read<int>() != 315 ||
                        compiledResult[2].Read<int>() != 42 ||
                        !compiledResult[3].Read<bool>() ||
                        capability.Value != 42 ||
                        capability.Position != new Vector3(1, 2, 3))
                    {
                        throw new LuauException(
                            "Background compilation, source asset execution, or generated capability dispatch failed.");
                    }

                    if (firstPartyResult.Length != 1 ||
                        firstPartyResult[0].Read<int>() != 2718)
                    {
                        throw new LuauException(
                            "Generated-manifest first-party bytecode execution failed.");
                    }

                    UnityEngine.Object.Destroy(capabilityObject);
                    for (var frame = 0; frame < 3 && capability != null; frame++)
                    {
                        await Task.Yield();
                    }
                    if (capability != null)
                    {
                        throw new LuauException(
                            "The player smoke capability did not enter Unity's destroyed-object state.");
                    }

                    try
                    {
                        using var unexpectedResult = first.DoString(
                            "return capability.Value",
                            "@unity/player-smoke-destroyed-capability.luau");
                        throw new LuauException(
                            "A destroyed Unity capability remained accessible to Luau.");
                    }
                    catch (LuauManagedCallbackException exception)
                        when (exception.InnerException is MissingReferenceException)
                    {
                    }
                }
                finally
                {
                    firstPartyResult?.Dispose();
                    compiledResult?.Dispose();
                    if (capabilityObject != null)
                    {
                        UnityEngine.Object.Destroy(capabilityObject);
                    }
                }

                if (firstResult.Length != 4 ||
                    !firstResult[0].Read<bool>() ||
                    firstResult[1].Read<long>() != 42L ||
                    firstResult[2].Read<int>() != 42 ||
                    firstResult[3].Read<long>() != 42L)
                {
                    throw new LuauException("The primary sandbox smoke script returned an unexpected result.");
                }

                if (secondResult.Length != 2 ||
                    !secondResult[0].Read<bool>() ||
                    secondResult[1].Read<long>() != 42L)
                {
                    throw new LuauException("Sandboxed script globals leaked between sibling threads.");
                }

                if (asyncResult.Length != 2 ||
                    asyncResult[0].Read<int>() != 77 ||
                    !asyncResult[1].Read<bool>())
                {
                    throw new LuauException(
                        "The asynchronous managed callback or Unity-thread-affinity smoke test failed.");
                }

                if (!root.MemoryUsage.IsLimited || root.MemoryUsage.PeakBytes <= 0)
                {
                    throw new LuauException("The tracked native allocator did not report usage.");
                }

                Debug.Log(
                    PassedMarker + " platform=" + Application.platform +
                    " backgroundCompiles=" +
                    BackgroundCompilationStressIterationsPerLane);
                return true;
            }
            catch (System.Exception exception)
            {
                Debug.LogError(FailedMarker + "\n" + exception);
                return false;
            }
        }

        static IEnumerator QuitAfterLogFlush(int exitCode)
        {
            yield return null;
            Application.Quit(exitCode);
        }

        static async Task<LuauCompilerOutput> StressCompilationServiceAsync(
            byte[] validSource,
            byte[] invalidSource,
            byte[] nearLimitSource)
        {
            string validBytecodeHash = null;
            string nearLimitBytecodeHash = null;
            string diagnostic = null;
            LuauCompilerOutput executableOutput = null;

            for (var index = 0;
                index < BackgroundCompilationStressIterationsPerLane;
                index++)
            {
                var expectDiagnostic = (index & 1) != 0;
                var useNearLimitSource = !expectDiagnostic && index % 127 == 0;
                var source = expectDiagnostic
                    ? invalidSource
                    : useNearLimitSource ? nearLimitSource : validSource;
                var compilation = await LuauUnity.CompileAsync(source).ConfigureAwait(false);

                if (expectDiagnostic)
                {
                    if (compilation.Kind != LuauCompileResultKind.Diagnostic ||
                        compilation.CompilationDiagnostic == null)
                    {
                        throw new LuauException(
                            "Invalid source did not produce a compilation diagnostic during concurrent player stress.");
                    }

                    diagnostic = diagnostic ?? compilation.CompilationDiagnostic.Message;
                    if (!string.Equals(
                            compilation.CompilationDiagnostic.Message,
                            diagnostic,
                            StringComparison.Ordinal))
                    {
                        throw new LuauException(
                            "Repeated concurrent invalid-source compilation produced non-deterministic diagnostics.");
                    }

                    continue;
                }

                if (compilation.Kind != LuauCompileResultKind.Success ||
                    compilation.Output == null)
                {
                    throw new LuauException(
                        "Valid source failed during concurrent background compilation player stress.");
                }

                executableOutput = executableOutput ?? compilation.Output;
                var expectedHash = useNearLimitSource
                    ? nearLimitBytecodeHash
                    : validBytecodeHash;
                expectedHash = expectedHash ?? compilation.Output.BytecodeSha256;
                if (!string.Equals(
                        compilation.Output.BytecodeSha256,
                        expectedHash,
                        StringComparison.Ordinal))
                {
                    throw new LuauException(
                        "Repeated concurrent background compilation produced non-deterministic bytecode.");
                }

                if (useNearLimitSource)
                {
                    nearLimitBytecodeHash = expectedHash;
                }
                else
                {
                    validBytecodeHash = expectedHash;
                }
            }

            if (executableOutput == null ||
                validBytecodeHash == null ||
                nearLimitBytecodeHash == null ||
                diagnostic == null)
            {
                throw new LuauException(
                    "Concurrent background compilation stress did not exercise every input category.");
            }

            return executableOutput;
        }
    }

    [LuauLibrary("smokeHost")]
    internal sealed partial class LuauPlayerSmokeHost
    {
        readonly int unityThreadId;

        public LuauPlayerSmokeHost(int unityThreadId)
        {
            this.unityThreadId = unityThreadId;
        }

        [LuauMember("addOne")]
        public static long AddOne(long value)
        {
            return value + 1;
        }

        [LuauMember("asyncAnswer")]
        public async ValueTask<int> AsyncAnswer(CancellationToken cancellationToken)
        {
            await Task.Delay(1, cancellationToken);
            AssertUnityThread("The asynchronous host callback left the Unity main thread.");
            return 77;
        }

        [LuauMember("assertUnityThread")]
        public bool AssertUnityThread()
        {
            AssertUnityThread("Luau resumed off the Unity main thread after an asynchronous callback.");
            return true;
        }

        void AssertUnityThread(string message)
        {
            if (System.Environment.CurrentManagedThreadId != unityThreadId)
            {
                throw new InvalidOperationException(message);
            }
        }
    }

    [LuauLibrary("PlayerSmokeCapability", Exposure = LuauLibraryExposure.Capability)]
    internal sealed partial class LuauPlayerSmokeCapability : MonoBehaviour
    {
        [LuauMember]
        public int Value { get; set; }

        [LuauMember]
        public Vector3 Position { get; set; }

        public int Hidden => 99;

        [LuauMember]
        public void Increment(int amount)
        {
            Value += amount;
        }
    }
}
