using System;
using System.IO;
using System.Reflection;
using System.Text;
using Luau.Unity.Verification;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Luau.Unity.Editor
{
    /// <summary>
    /// Builds a disposable smoke scene without changing PlayerSettings or the
    /// project's configured scene list. The requested target must already be
    /// active and configured for IL2CPP.
    /// </summary>
    public static class LuauPlayerSmokeBuild
    {
        const string OutputArgument = "-luauSmokeOutput";
        const string GeneratedParentAssetPath = "Assets/Generated";
        const string GeneratedRootAssetPath = "Assets/Generated/Luau.Unity";
        const string GeneratedManifestAssetPath =
            "Assets/Generated/Luau.Unity/Resources/Luau.Unity/FirstPartyBytecodeManifest.asset";

        [MenuItem("Luau/Verification/Build Windows x64 IL2CPP Smoke Player")]
        public static void BuildWindows64Il2Cpp()
        {
            Build(
                BuildTarget.StandaloneWindows64,
                Path.Combine("Builds", "LuauSmoke", "Windows", "LuauSmoke.exe"));
        }

        [MenuItem("Luau/Verification/Build Android ARM64 IL2CPP Smoke Player")]
        public static void BuildAndroidArm64Il2Cpp()
        {
            BuildAndroid(
                AndroidArchitecture.ARM64,
                Path.Combine("Builds", "LuauSmoke", "Android-arm64", "LuauSmoke.apk"));
        }

        [MenuItem("Luau/Verification/Build Android x64 IL2CPP Smoke Player")]
        public static void BuildAndroidX64Il2Cpp()
        {
            BuildAndroid(
                AndroidArchitecture.X86_64,
                Path.Combine("Builds", "LuauSmoke", "Android-x64", "LuauSmoke.apk"));
        }

        static void BuildAndroid(AndroidArchitecture architecture, string defaultOutput)
        {
            var previousArchitecture = PlayerSettings.Android.targetArchitectures;
            try
            {
                PlayerSettings.Android.targetArchitectures = architecture;
                Build(BuildTarget.Android, defaultOutput, architecture);
            }
            finally
            {
                PlayerSettings.Android.targetArchitectures = previousArchitecture;
            }
        }

        static void Build(
            BuildTarget target,
            string defaultOutput,
            AndroidArchitecture? requiredAndroidArchitecture = null)
        {
            ValidateTarget(target, requiredAndroidArchitecture);

            var output = GetOutputPath(defaultOutput);
            var outputDirectory = Path.GetDirectoryName(output);
            if (string.IsNullOrEmpty(outputDirectory))
            {
                throw new BuildFailedException("The Luau smoke output path has no parent directory.");
            }

            Directory.CreateDirectory(outputDirectory);

            var previousPolicy = LuauAssetImportSettings.ImportPolicy;
            var previousProvenanceId = LuauAssetImportSettings.FirstPartyProvenanceId;
            var generatedBackup = new GeneratedAssetsBackup();
            string temporaryFolder = null;
            Scene smokeScene = default;
            try
            {
                SetTemporaryImportSettings(
                    LuauAssetImportPolicy.AllowFirstPartyPrecompile,
                    LuauPlayerSmoke.FirstPartyProvenanceId);

                var temporaryFolderName = "__LuauPlayerSmoke_" + Guid.NewGuid().ToString("N");
                var temporaryFolderGuid = AssetDatabase.CreateFolder("Assets", temporaryFolderName);
                temporaryFolder = AssetDatabase.GUIDToAssetPath(temporaryFolderGuid);
                if (string.IsNullOrEmpty(temporaryFolder))
                {
                    throw new BuildFailedException(
                        "Unable to create a temporary Luau smoke scene folder.");
                }

                var resourcesFolder = AssetDatabase.CreateFolder(
                    temporaryFolder,
                    "Resources");
                resourcesFolder = AssetDatabase.GUIDToAssetPath(resourcesFolder);
                if (string.IsNullOrEmpty(resourcesFolder))
                {
                    throw new BuildFailedException(
                        "Unable to create the temporary Luau smoke Resources folder.");
                }

                var backgroundAssetPath = resourcesFolder + "/" +
                    LuauPlayerSmoke.BackgroundAssetResourceName + ".luau";
                var absoluteBackgroundAssetPath = Path.GetFullPath(
                    Path.Combine(Application.dataPath, "..", backgroundAssetPath));
                File.WriteAllText(
                    absoluteBackgroundAssetPath,
                    LuauPlayerSmoke.BackgroundSource,
                    new UTF8Encoding(false));
                AssetDatabase.ImportAsset(
                    backgroundAssetPath,
                    ImportAssetOptions.ForceSynchronousImport);
                if (AssetDatabase.LoadAssetAtPath<LuauAsset>(backgroundAssetPath) == null)
                {
                    throw new BuildFailedException(
                        "The temporary Luau background smoke source was not imported as a LuauAsset.");
                }

                var firstPartyAssetPath = resourcesFolder + "/" +
                    LuauPlayerSmoke.FirstPartyAssetResourceName + ".luau";
                var absoluteFirstPartyAssetPath = Path.GetFullPath(
                    Path.Combine(Application.dataPath, "..", firstPartyAssetPath));
                File.WriteAllText(
                    absoluteFirstPartyAssetPath,
                    LuauPlayerSmoke.FirstPartySource,
                    new UTF8Encoding(false));
                AssetDatabase.ImportAsset(
                    firstPartyAssetPath,
                    ImportAssetOptions.ForceSynchronousImport);

                var firstPartyImporter = AssetImporter.GetAtPath(firstPartyAssetPath);
                if (firstPartyImporter == null)
                {
                    throw new BuildFailedException(
                        "The temporary first-party Luau smoke importer was unavailable.");
                }

                var serializedImporter = new SerializedObject(firstPartyImporter);
                var precompile = serializedImporter.FindProperty("precompile");
                if (precompile == null)
                {
                    throw new BuildFailedException(
                        "The Luau importer did not expose its serialized precompile opt-in.");
                }

                precompile.boolValue = true;
                serializedImporter.ApplyModifiedPropertiesWithoutUndo();
                firstPartyImporter.SaveAndReimport();

                var firstPartyAsset =
                    AssetDatabase.LoadAssetAtPath<LuauAsset>(firstPartyAssetPath);
                if (firstPartyAsset == null || !firstPartyAsset.IsPrecompiled)
                {
                    throw new BuildFailedException(
                        "The temporary first-party Luau smoke source was not precompiled.");
                }

                var activeScene = SceneManager.GetActiveScene();
                var sceneMode = activeScene.IsValid() &&
                                string.IsNullOrEmpty(activeScene.path) &&
                                !activeScene.isDirty
                    ? NewSceneMode.Single
                    : NewSceneMode.Additive;
                smokeScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, sceneMode);
                var smokeObject = new GameObject("Luau Player Smoke");
                SceneManager.MoveGameObjectToScene(smokeObject, smokeScene);
                smokeObject.AddComponent<LuauPlayerSmoke>().QuitOnCompletion = true;

                var scenePath = temporaryFolder + "/LuauPlayerSmoke.unity";
                if (!EditorSceneManager.SaveScene(smokeScene, scenePath, false))
                {
                    throw new BuildFailedException("Unable to save the temporary Luau smoke scene.");
                }

                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { scenePath },
                    locationPathName = output,
                    target = target,
                    options = BuildOptions.Development,
                });

                if (report.summary.result != BuildResult.Succeeded)
                {
                    throw new BuildFailedException(
                        "Luau smoke build failed with " + report.summary.totalErrors + " error(s).");
                }

                if (AssetDatabase.LoadMainAssetAtPath(GeneratedManifestAssetPath) == null)
                {
                    throw new BuildFailedException(
                        "The first-party Luau manifest was not generated during the smoke build.");
                }

                Debug.Log("Luau smoke player built at " + report.summary.outputPath);
            }
            finally
            {
                try
                {
                    if (smokeScene.IsValid() && smokeScene.isLoaded)
                    {
                        if (SceneManager.sceneCount == 1)
                        {
                            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                        }
                        else
                        {
                            EditorSceneManager.CloseScene(smokeScene, true);
                        }
                    }

                    if (!string.IsNullOrEmpty(temporaryFolder))
                    {
                        AssetDatabase.DeleteAsset(temporaryFolder);
                    }
                }
                finally
                {
                    try
                    {
                        SetTemporaryImportSettings(previousPolicy, previousProvenanceId);
                    }
                    finally
                    {
                        try
                        {
                            FlushScheduledManifestRefresh();
                        }
                        finally
                        {
                            generatedBackup.Restore();
                        }
                    }
                }
            }
        }

        static void SetTemporaryImportSettings(
            LuauAssetImportPolicy policy,
            string provenanceId)
        {
            InvokeSettingsTestHook("SetFirstPartyProvenanceIdForTests", provenanceId);
            InvokeSettingsTestHook("SetImportPolicyForTests", policy);
            InvokeSettingsTestHook("ReimportLuauAssets");
        }

        static void InvokeSettingsTestHook(string methodName, params object[] arguments)
        {
            var method = typeof(LuauAssetImportSettings).GetMethod(
                methodName,
                BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
            {
                throw new BuildFailedException(
                    "The Luau smoke build could not find settings hook " + methodName + ".");
            }

            method.Invoke(null, arguments);
        }

        static void FlushScheduledManifestRefresh()
        {
            var refreshType = typeof(LuauAssetImportSettings).Assembly.GetType(
                "Luau.Unity.Editor.LuauFirstPartyManifestRefresh",
                throwOnError: false);
            var method = refreshType?.GetMethod(
                "RefreshNow",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
            {
                throw new BuildFailedException(
                    "The Luau smoke build could not flush the scheduled manifest refresh.");
            }

            // Deleting and reimporting the temporary .luau assets queues a
            // delayed refresh. Consume it under the restored settings before
            // restoring exact generated bytes, or the next Editor tick could
            // overwrite the backup.
            method.Invoke(null, new object[] { false });
        }

        sealed class GeneratedAssetsBackup
        {
            readonly string generatedParentPath;
            readonly string generatedRootPath;
            readonly string backupPath;
            readonly bool generatedParentExisted;
            readonly bool generatedRootExisted;

            public GeneratedAssetsBackup()
            {
                generatedParentPath = ToAbsoluteProjectPath(GeneratedParentAssetPath);
                generatedRootPath = ToAbsoluteProjectPath(GeneratedRootAssetPath);
                generatedParentExisted = Directory.Exists(generatedParentPath);
                generatedRootExisted = Directory.Exists(generatedRootPath);
                backupPath = Path.Combine(
                    Path.GetTempPath(),
                    "LuauPlayerSmokeGenerated_" + Guid.NewGuid().ToString("N"));

                if (generatedRootExisted)
                {
                    FileUtil.CopyFileOrDirectory(generatedRootPath, backupPath);
                    if (File.Exists(generatedRootPath + ".meta"))
                    {
                        FileUtil.CopyFileOrDirectory(
                            generatedRootPath + ".meta",
                            backupPath + ".meta");
                    }
                }
            }

            public void Restore()
            {
                try
                {
                    AssetDatabase.DeleteAsset(GeneratedRootAssetPath);
                    FileUtil.DeleteFileOrDirectory(generatedRootPath);
                    FileUtil.DeleteFileOrDirectory(generatedRootPath + ".meta");

                    if (generatedRootExisted)
                    {
                        FileUtil.CopyFileOrDirectory(backupPath, generatedRootPath);
                        if (File.Exists(backupPath + ".meta"))
                        {
                            FileUtil.CopyFileOrDirectory(
                                backupPath + ".meta",
                                generatedRootPath + ".meta");
                        }
                    }

                    if (!generatedParentExisted &&
                        Directory.Exists(generatedParentPath) &&
                        Directory.GetFileSystemEntries(generatedParentPath).Length == 0)
                    {
                        AssetDatabase.DeleteAsset(GeneratedParentAssetPath);
                    }

                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                }
                finally
                {
                    FileUtil.DeleteFileOrDirectory(backupPath);
                    FileUtil.DeleteFileOrDirectory(backupPath + ".meta");
                }
            }

            static string ToAbsoluteProjectPath(string assetPath)
            {
                return Path.GetFullPath(
                    Path.Combine(Application.dataPath, "..", assetPath));
            }
        }

        static void ValidateTarget(
            BuildTarget target,
            AndroidArchitecture? requiredAndroidArchitecture)
        {
            if (EditorUserBuildSettings.activeBuildTarget != target)
            {
                throw new BuildFailedException(
                    "The active build target is " + EditorUserBuildSettings.activeBuildTarget +
                    ", but the Luau smoke build requested " + target +
                    ". Start Unity with the matching -buildTarget argument first.");
            }

            var group = BuildPipeline.GetBuildTargetGroup(target);
            var namedTarget = NamedBuildTarget.FromBuildTargetGroup(group);
            if (PlayerSettings.GetScriptingBackend(namedTarget) != ScriptingImplementation.IL2CPP)
            {
                throw new BuildFailedException(
                    "The " + target + " player must already be configured to use IL2CPP.");
            }

            if (target == BuildTarget.Android && requiredAndroidArchitecture.HasValue &&
                PlayerSettings.Android.targetArchitectures != requiredAndroidArchitecture.Value)
            {
                throw new BuildFailedException(
                    "The Android smoke player must target exactly " + requiredAndroidArchitecture.Value + ".");
            }
        }

        static string GetOutputPath(string defaultOutput)
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var i = 0; i < arguments.Length; i++)
            {
                if (string.Equals(arguments[i], OutputArgument, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= arguments.Length || string.IsNullOrWhiteSpace(arguments[i + 1]))
                    {
                        throw new BuildFailedException(OutputArgument + " requires a path value.");
                    }

                    return Path.GetFullPath(arguments[i + 1]);
                }

                var prefix = OutputArgument + "=";
                if (arguments[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var value = arguments[i].Substring(prefix.Length);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        throw new BuildFailedException(OutputArgument + " requires a path value.");
                    }

                    return Path.GetFullPath(value);
                }
            }

            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", defaultOutput));
        }
    }
}
