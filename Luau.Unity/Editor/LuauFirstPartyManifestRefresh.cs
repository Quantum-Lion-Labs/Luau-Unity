using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Luau.Unity.Editor
{
    /// <summary>
    /// Coalesces import/startup activity into one post-import manifest refresh.
    /// The generator itself remains synchronous so builds and recovery buttons
    /// can establish a definitive snapshot before continuing.
    /// </summary>
    [InitializeOnLoad]
    internal static class LuauFirstPartyManifestRefresh
    {
        static bool scheduled;
        static bool running;
        static bool refreshAgain;

        static LuauFirstPartyManifestRefresh()
        {
            Schedule();
        }

        internal static void Schedule()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return;

            if (running)
            {
                refreshAgain = true;
                return;
            }
            if (scheduled)
                return;

            scheduled = true;
            EditorApplication.delayCall -= RunScheduled;
            EditorApplication.delayCall += RunScheduled;
        }

        internal static LuauFirstPartyManifestStatus RefreshNow(bool logErrors)
        {
            return RefreshNowCore(logErrors, LuauFirstPartyManifestGenerator.RefreshForCurrentPolicy);
        }

        internal static LuauFirstPartyManifestStatus RefreshNowCore(
            bool logErrors,
            Func<LuauFirstPartyManifestStatus> refresh)
        {
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return LuauFirstPartyManifestGenerator.LastStatus;

            if (running)
            {
                refreshAgain = true;
                return LuauFirstPartyManifestGenerator.LastStatus;
            }

            scheduled = false;
            EditorApplication.delayCall -= RunScheduled;
            running = true;
            try
            {
                var status = refresh();
                if (logErrors && status.Errors.Count != 0)
                {
                    Debug.LogError(
                        "Luau.Unity could not refresh the generated first-party manifest:\n" +
                        string.Join("\n", status.Errors.Select(error => "- " + error)));
                }
                return status;
            }
            catch (Exception exception)
            {
                var status = LuauFirstPartyManifestGenerator.RecordRefreshFailure(exception);
                if (logErrors)
                {
                    Debug.LogError(
                        "Luau.Unity could not refresh the generated first-party manifest: " +
                        exception.Message);
                }
                return status;
            }
            finally
            {
                running = false;
                if (refreshAgain)
                {
                    refreshAgain = false;
                    Schedule();
                }
            }
        }

        static void RunScheduled()
        {
            scheduled = false;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                Schedule();
                return;
            }

            RefreshNow(logErrors: false);
        }
    }

    internal sealed class LuauFirstPartyManifestAssetPostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (ContainsLuau(importedAssets) ||
                ContainsLuau(deletedAssets) ||
                ContainsLuau(movedAssets) ||
                ContainsLuau(movedFromAssetPaths))
            {
                LuauFirstPartyManifestRefresh.Schedule();
            }
        }

        static bool ContainsLuau(string[] paths)
        {
            return paths != null && paths.Any(path =>
                !string.IsNullOrEmpty(path) &&
                path.EndsWith(".luau", StringComparison.OrdinalIgnoreCase));
        }
    }
}
