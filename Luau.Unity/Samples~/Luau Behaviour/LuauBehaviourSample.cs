using System;
using System.Threading;
using Luau;
using Luau.Unity;
using UnityEngine;

namespace Luau.Unity.Samples.LuauBehaviour
{
    /// <summary>Sample component backed by one sandboxed Luau script instance.</summary>
    public sealed class LuauBehaviourSample : MonoBehaviour
    {
        [SerializeField]
        LuauBehaviourRuntimeSample runtimeHost;

        [SerializeField]
        LuauAsset script;

        [SerializeField]
        int updateOrder;

        LuauScriptInstance instance;
        LuauScriptRegistration updateRegistration;
        int destroyed;
        bool failureLogged;

        async void Start()
        {
            if (runtimeHost == null || script == null)
            {
                DisableAfterFailure(new InvalidOperationException(
                    "Assign an explicit Luau Behaviour runtime host and Luau asset."));
                return;
            }

            LuauScriptInstance createdInstance = null;
            try
            {
                createdInstance = await runtimeHost.Root.CreateScriptInstanceAsync(
                    script,
                    ConfigureThread,
                    destroyCancellationToken);

                if (Volatile.Read(ref destroyed) != 0)
                {
                    createdInstance.Dispose();
                    return;
                }

                if (createdInstance.TryGetEntrypoint("start", out var start))
                {
                    await start.InvokeVoidAsync(destroyCancellationToken);
                }

                var update = createdInstance.GetRequiredEntrypoint("update");
                var registration = runtimeHost.Register(this, update, updateOrder);
                registration.IsEnabled = isActiveAndEnabled;

                instance = createdInstance;
                updateRegistration = registration;
                createdInstance = null;
            }
            catch (OperationCanceledException) when (destroyCancellationToken.IsCancellationRequested)
            {
                createdInstance?.Dispose();
            }
            catch (Exception exception)
            {
                createdInstance?.Dispose();
                DisableAfterFailure(exception);
            }
        }

        void ConfigureThread(LuauState thread)
        {
            using var self = runtimeHost.Root.CreateHandle(gameObject);
            thread["self"] = self;
        }

        void OnEnable()
        {
            if (updateRegistration != null && !updateRegistration.IsDisposed)
            {
                updateRegistration.IsEnabled = true;
            }
        }

        void OnDisable()
        {
            if (updateRegistration != null && !updateRegistration.IsDisposed)
            {
                updateRegistration.IsEnabled = false;
            }
        }

        internal void DisableAfterFailure(Exception exception)
        {
            if (!failureLogged)
            {
                failureLogged = true;
                Debug.LogError(
                    "Luau Behaviour '" + name + "' was disabled.\n" + exception,
                    this);
            }

            enabled = false;
        }

        void OnDestroy()
        {
            Interlocked.Exchange(ref destroyed, 1);
            try
            {
                if (instance != null &&
                    !instance.IsDisposed &&
                    instance.TryGetEntrypoint("destroy", out var destroy))
                {
                    destroy.InvokeVoid();
                }
            }
            catch (Exception exception)
            {
                if (!failureLogged)
                {
                    failureLogged = true;
                    Debug.LogWarning(
                        "Luau Behaviour '" + name + "' destroy hook failed.\n" + exception,
                        this);
                }
            }
            finally
            {
                if (runtimeHost != null)
                {
                    runtimeHost.Unregister(updateRegistration);
                }
                else
                {
                    updateRegistration?.Dispose();
                }
                updateRegistration = null;
                instance?.Dispose();
                instance = null;
            }
        }
    }
}
