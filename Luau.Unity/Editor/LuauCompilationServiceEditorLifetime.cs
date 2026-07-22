using System;
using UnityEditor;
using UnityEngine;

namespace Luau.Unity.Editor
{
    [InitializeOnLoad]
    internal static class LuauCompilationServiceEditorLifetime
    {
        static LuauCompilationServiceEditorLifetime()
        {
            AssemblyReloadEvents.beforeAssemblyReload += DrainForAssemblyReload;
            EditorApplication.quitting += DrainForEditorQuit;
        }

        internal static void DrainForAssemblyReload()
        {
            Drain("assembly reload");
        }

        static void DrainForEditorQuit()
        {
            Drain("Editor shutdown");
        }

        static void Drain(string reason)
        {
            try
            {
                LuauUnity.DisposeTrackedRoots();
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "Tracked Luau roots could not be disposed for " + reason + ".\n" +
                    exception);
            }

            try
            {
                LuauUnity.DrainCompilationServiceAsync(exception =>
                    Debug.LogWarning(
                        "Luau background compilation is still draining for " + reason + ".\n" +
                        exception))
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "The shared Luau compilation service could not be drained for " + reason + ".\n" +
                    exception);
            }
        }
    }
}
