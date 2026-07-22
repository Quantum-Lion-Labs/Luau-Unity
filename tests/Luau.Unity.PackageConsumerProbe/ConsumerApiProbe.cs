using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Luau;
using Luau.Unity;

namespace Luau.Unity.PackageConsumerProbe
{
    internal static class ConsumerApiProbe
    {
        public static LuauUnityOptions CreateOptions(Action<LuauState> configureHostApis)
        {
            return new LuauUnityOptions
            {
                CaptureUnitySynchronizationContext = false,
                ModuleMap = new LuauModuleMap(new Dictionary<string, byte[]>()),
                ConfigureHostApis = configureHostApis,
                Log = _ => { },
            };
        }

        public static ValueTask<LuauModuleBundle> CompileModulesAsync(
            LuauModuleMap moduleMap,
            CancellationToken cancellationToken)
        {
            return LuauUnity.CompileModuleBundleAsync(
                moduleMap,
                cancellationToken: cancellationToken);
        }

        public static async ValueTask<LuauScriptInstance> CreateBehaviourInstanceAsync(
            LuauState root,
            LuauAsset asset,
            Action<LuauState> configureThread,
            CancellationToken cancellationToken)
        {
            var instance = await root.CreateScriptInstanceAsync(
                asset,
                configureThread,
                cancellationToken);
            instance.TryGetEntrypoint("start", out _);
            instance.GetRequiredEntrypoint("update");
            return instance;
        }

        public static LuauScriptDispatchResult DispatchBehaviourUpdate(
            LuauState root,
            LuauScriptEntrypoint update,
            LuauValue deltaTime)
        {
            using var scheduler = new LuauScriptScheduler(root);
            var phase = scheduler.CreatePhase(
                "consumer-update",
                new LuauScriptPhaseOptions
                {
                    InvocationOptions = LuauExecutionOptions.Default with
                    {
                        WallClockLimit = TimeSpan.FromMilliseconds(2),
                        InterruptCountLimit = 10_000,
                        MaxResultCount = 0,
                    },
                    AggregateWallClockBudget = TimeSpan.FromMilliseconds(4),
                    FailureMode = LuauScriptPhaseFailureMode.DisableAndContinue,
                    FailureCallback = (_, __) => { },
                });
            using var registration = phase.Register(update, order: 10);
            registration.IsEnabled = true;
            return phase.Dispatch(deltaTime);
        }
    }
}
