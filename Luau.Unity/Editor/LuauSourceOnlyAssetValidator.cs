using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Luau.Unity.Editor
{
    /// <summary>
    /// Enforces the source-only package boundary by inspecting imported asset
    /// content, independent of serialized importer options.
    /// </summary>
    public static class LuauSourceOnlyAssetValidator
    {
        public static IReadOnlyList<string> FindNonSourceAssets(
            IEnumerable<string> assetPaths)
        {
            if (assetPaths == null)
                throw new ArgumentNullException(nameof(assetPaths));

            return assetPaths
                .Where(path => !string.IsNullOrEmpty(path))
                .Distinct(StringComparer.Ordinal)
                .Where(path => AssetDatabase.LoadAllAssetsAtPath(path)
                    .OfType<LuauAsset>()
                    .Any(asset => !asset.IsSource))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }

        public static void ValidateSourceOnly(IEnumerable<string> assetPaths)
        {
            var invalid = FindNonSourceAssets(assetPaths);
            if (invalid.Count != 0)
            {
                throw new InvalidOperationException(
                    "Source-only Luau validation rejected non-source assets: " +
                    string.Join(", ", invalid));
            }
        }

        public static void ValidateProject()
        {
            ValidateSourceOnly(FindAllLuauAssetPaths());
        }

        internal static IEnumerable<string> FindAllLuauAssetPaths()
        {
            return AssetDatabase.FindAssets("t:LuauAsset")
                .Select(AssetDatabase.GUIDToAssetPath);
        }
    }

    /// <summary>
    /// Establishes the package's Luau content snapshot before Unity collects
    /// player content. Source-only cleanup and first-party generation live in
    /// one early preprocessor so neither policy can bypass the other.
    /// </summary>
    internal sealed class LuauSourceOnlyBuildPreprocessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            try
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                if (LuauAssetImportSettings.ImportPolicy ==
                    LuauAssetImportPolicy.SourceOnly)
                {
                    var sourceOnlyStatus =
                        LuauFirstPartyManifestGenerator.RefreshForCurrentPolicy();
                    if (sourceOnlyStatus.Errors.Count != 0)
                    {
                        throw new InvalidOperationException(
                            "Source-only Luau generated-content cleanup failed:\n- " +
                            string.Join("\n- ", sourceOnlyStatus.Errors));
                    }
                    LuauSourceOnlyAssetValidator.ValidateProject();
                    return;
                }

                var status = LuauFirstPartyManifestGenerator.Generate();
                if (status.Errors.Count != 0)
                {
                    throw new InvalidOperationException(
                        "First-party Luau manifest validation failed:\n- " +
                        string.Join("\n- ", status.Errors));
                }

                var manifest = AssetDatabase.LoadAssetAtPath<FirstPartyBytecodeManifest>(
                    LuauFirstPartyManifestGenerator.GeneratedAssetPath);
                if (manifest == null)
                {
                    throw new InvalidOperationException(
                        "The generated first-party Luau manifest could not be reloaded before " +
                        "player content collection.");
                }

                // Rebuild the pure-managed runtime snapshot from the exact
                // ScriptableObject that Unity is about to include.
                FirstPartyBytecodeManifestCache.Reload(manifest);
            }
            catch (Exception exception)
            {
                throw new BuildFailedException(exception.Message);
            }
        }
    }
}
