using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Luau.Unity
{
    internal delegate ValueTask<LuauCompileResult> LuauAssetCompilationProvider(
        ReadOnlyMemory<byte> utf8Source,
        LuauCompileOptions options,
        CancellationToken cancellationToken);

    public static partial class LuauUnity
    {
        internal static readonly TimeSpan CompilationServiceDrainTimeout =
            TimeSpan.FromSeconds(5);

        static readonly object CompilationServiceGate = new object();
        static readonly object AssetCompilationProviderGate = new object();
        static LuauThreadedCompilationService compilationService;
        static bool compilationServiceStopping;
        static LuauAssetCompilationProvider assetCompilationProvider = CompileAsync;

        internal static ValueTask<LuauCompileResult> CompileAssetSourceAsync(
            ReadOnlyMemory<byte> utf8Source,
            LuauCompileOptions options,
            CancellationToken cancellationToken)
        {
            LuauAssetCompilationProvider provider;
            lock (AssetCompilationProviderGate)
            {
                provider = assetCompilationProvider;
            }

            return provider(utf8Source, options, cancellationToken);
        }

        internal static IDisposable OverrideAssetCompilationProviderForTests(
            LuauAssetCompilationProvider provider)
        {
            if (provider == null)
            {
                throw new ArgumentNullException(nameof(provider));
            }

            lock (AssetCompilationProviderGate)
            {
                var previous = assetCompilationProvider;
                assetCompilationProvider = provider;
                return new AssetCompilationProviderOverride(previous, provider);
            }
        }

        /// <summary>
        /// Compiles UTF-8 Luau source on the package-owned, bounded background
        /// compilation lane. The lane is shared by all callers and is drained
        /// automatically during Unity shutdown and Editor assembly reload.
        /// </summary>
        public static ValueTask<LuauCompileResult> CompileAsync(
            ReadOnlyMemory<byte> utf8Source,
            LuauCompileOptions options = null,
            CancellationToken cancellationToken = default)
        {
            lock (CompilationServiceGate)
            {
                if (compilationServiceStopping)
                {
                    throw new ObjectDisposedException(
                        nameof(LuauUnity),
                        "The shared Luau compilation service is shutting down.");
                }

                if (compilationService == null)
                {
                    compilationService = new LuauThreadedCompilationService(
                        GetRecommendedCompilationOptions());
                }

                // CompileAsync performs admission and takes its owned source
                // snapshot synchronously. Holding the lifecycle lock makes
                // admission linear with package shutdown.
                return compilationService.CompileAsync(
                    utf8Source,
                    options,
                    cancellationToken);
            }
        }

        /// <summary>
        /// Compiles an immutable source module map into an all-or-nothing,
        /// same-process bundle through Unity's shared bounded compilation lane.
        /// The returned bundle is a resolver capability, not persistent trusted
        /// bytecode; install it explicitly with <see cref="LuauState.OpenRequireLibrary"/>.
        /// </summary>
        public static ValueTask<LuauModuleBundle> CompileModuleBundleAsync(
            LuauModuleMap moduleMap,
            LuauCompileOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (moduleMap == null)
            {
                throw new ArgumentNullException(nameof(moduleMap));
            }

            return moduleMap.CompileModuleBundleAsync(
                SharedModuleCompilationService.Instance,
                options,
                cancellationToken);
        }

        /// <summary>
        /// Creates the recommended finite compilation policy for the current
        /// maintained Unity platform. Use this when constructing an advanced,
        /// caller-owned <see cref="LuauThreadedCompilationService"/>.
        /// </summary>
        public static LuauThreadedCompilationOptions GetRecommendedCompilationOptions()
        {
#if UNITY_EDITOR_WIN
            return GetRecommendedCompilationOptions(RuntimePlatform.WindowsEditor);
#elif UNITY_STANDALONE_WIN
            return GetRecommendedCompilationOptions(RuntimePlatform.WindowsPlayer);
#elif UNITY_ANDROID
            return GetRecommendedCompilationOptions(RuntimePlatform.Android);
#elif UNITY_EDITOR_LINUX
            return GetRecommendedCompilationOptions(RuntimePlatform.LinuxEditor);
#elif UNITY_STANDALONE_LINUX
            return GetRecommendedCompilationOptions(RuntimePlatform.LinuxPlayer);
#else
            throw new PlatformNotSupportedException(
                "Background Luau compilation is maintained for Windows x64 and Android ARM64/x64, " +
                "with Linux x64 enabled for development validation; " +
                "the current Unity target is not maintained.");
#endif
        }

        internal static LuauThreadedCompilationOptions GetRecommendedCompilationOptions(
            RuntimePlatform platform)
        {
            var windows = platform == RuntimePlatform.WindowsEditor ||
                platform == RuntimePlatform.WindowsPlayer;
            var linux = platform == RuntimePlatform.LinuxEditor ||
                platform == RuntimePlatform.LinuxPlayer;
            if (!windows && !linux && platform != RuntimePlatform.Android)
            {
                throw new PlatformNotSupportedException(
                    "Background Luau compilation is maintained for Windows x64 and Android ARM64/x64, " +
                    "with Linux x64 enabled for development validation; " +
                    "the current Unity platform is " + platform + ".");
            }

            return new LuauThreadedCompilationOptions
            {
                WorkerCount = 1,
                MaxQueuedRequestCount = windows || linux ? 32 : 16,
                MaxQueuedSourceBytes = windows || linux ? 8L * 1024 * 1024 : 4L * 1024 * 1024,
                MaxSourceBytesPerRequest = 1024 * 1024,
                MaxBytecodeBytesPerResult = 4 * 1024 * 1024,
                ShutdownTimeout = CompilationServiceDrainTimeout,
            };
        }

        internal static async Task DrainCompilationServiceAsync(
            Action<LuauCompilationShutdownException> timeoutObserver = null)
        {
            LuauThreadedCompilationService service;
            lock (CompilationServiceGate)
            {
                compilationServiceStopping = true;
                service = compilationService;
            }

            if (service == null)
            {
                return;
            }

            // A native compile cannot be aborted safely. A lifecycle drain may
            // report finite progress intervals, but it must not allow Unity to
            // unload the managed assembly until the worker has actually exited.
            while (true)
            {
                try
                {
                    await service.DisposeAsync().ConfigureAwait(false);
                    lock (CompilationServiceGate)
                    {
                        if (ReferenceEquals(compilationService, service))
                        {
                            compilationService = null;
                        }
                    }
                    return;
                }
                catch (LuauCompilationShutdownException exception)
                {
                    timeoutObserver?.Invoke(exception);
                }
            }
        }

#if UNITY_EDITOR
        internal static void ResetCompilationServiceAfterDrainForTests()
        {
            lock (CompilationServiceGate)
            {
                if (!compilationServiceStopping || compilationService != null)
                {
                    throw new InvalidOperationException(
                        "The shared Luau compilation service can only be reset after a completed drain.");
                }

                compilationServiceStopping = false;
            }
        }
#endif

        sealed class AssetCompilationProviderOverride : IDisposable
        {
            readonly LuauAssetCompilationProvider previous;
            readonly LuauAssetCompilationProvider installed;
            int disposed;

            internal AssetCompilationProviderOverride(
                LuauAssetCompilationProvider previous,
                LuauAssetCompilationProvider installed)
            {
                this.previous = previous;
                this.installed = installed;
            }

            public void Dispose()
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    return;
                }

                lock (AssetCompilationProviderGate)
                {
                    if (disposed != 0)
                    {
                        return;
                    }
                    if (!ReferenceEquals(assetCompilationProvider, installed))
                    {
                        throw new InvalidOperationException(
                            "Luau asset compilation provider overrides must be disposed in reverse order.");
                    }

                    assetCompilationProvider = previous;
                    Volatile.Write(ref disposed, 1);
                }
            }
        }

        sealed class SharedModuleCompilationService : ILuauCompilationService
        {
            internal static SharedModuleCompilationService Instance { get; } =
                new SharedModuleCompilationService();

            SharedModuleCompilationService()
            {
            }

            public ValueTask<LuauCompileResult> CompileAsync(
                ReadOnlyMemory<byte> utf8Source,
                LuauCompileOptions options = null,
                CancellationToken cancellationToken = default)
            {
                return CompileAssetSourceAsync(utf8Source, options, cancellationToken);
            }

            public ValueTask DisposeAsync()
            {
                // This adapter never owns the package-wide service lifetime.
                return default;
            }
        }

#if !UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void RegisterCompilationServicePlayerLifetime()
        {
            Application.quitting -= DrainCompilationServiceForPlayerQuit;
            Application.quitting += DrainCompilationServiceForPlayerQuit;
        }

        static void DrainCompilationServiceForPlayerQuit()
        {
            try
            {
                DisposeTrackedRoots();
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "Tracked Luau roots could not be disposed during player shutdown.\n" +
                    exception);
            }

            try
            {
                DrainCompilationServiceAsync(exception =>
                    Debug.LogWarning(
                        "Luau background compilation is still draining during player shutdown.\n" +
                        exception))
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "The shared Luau compilation service could not be drained during player shutdown.\n" +
                    exception);
            }
        }
#endif
    }
}
