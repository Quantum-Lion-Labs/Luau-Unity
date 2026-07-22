using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Luau.Unity.Editor
{
    internal sealed class LuauFirstPartyManifestStatus
    {
        internal LuauFirstPartyManifestStatus(
            int totalLuauAssets,
            int optedInAssets,
            int precompiledAssets,
            bool hasProvenanceId,
            bool isManifestCurrent,
            bool isEmptyManifest,
            bool manifestChanged,
            IEnumerable<string> errors)
        {
            TotalLuauAssets = totalLuauAssets;
            OptedInAssets = optedInAssets;
            PrecompiledAssets = precompiledAssets;
            HasProvenanceId = hasProvenanceId;
            IsManifestCurrent = isManifestCurrent;
            IsEmptyManifest = isEmptyManifest;
            ManifestChanged = manifestChanged;
            Errors = (errors ?? Array.Empty<string>())
                .Where(error => !string.IsNullOrWhiteSpace(error))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(error => error, StringComparer.Ordinal)
                .ToArray();
        }

        internal int TotalLuauAssets { get; }
        internal int OptedInAssets { get; }
        internal int PrecompiledAssets { get; }
        internal bool HasProvenanceId { get; }
        internal bool IsManifestCurrent { get; }
        internal bool IsEmptyManifest { get; }
        internal bool ManifestChanged { get; }
        internal IReadOnlyList<string> Errors { get; }
    }

    /// <summary>
    /// Builds the package-owned first-party allowlist from every opted-in Luau
    /// importer under Assets. The same implementation is used by lifecycle
    /// refreshes and the build gate so their admission rules cannot drift.
    /// </summary>
    internal static class LuauFirstPartyManifestGenerator
    {
        internal const string GeneratedAssetPath =
            "Assets/Generated/Luau.Unity/Resources/Luau.Unity/FirstPartyBytecodeManifest.asset";
        internal const string ResourceKey = FirstPartyBytecodeManifest.ResourcePath;
        internal const string SourceIdentityPrefix = "unity-asset-guid:";

        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        internal static LuauFirstPartyManifestStatus LastStatus { get; private set; }

        internal static LuauFirstPartyManifestStatus RefreshForCurrentPolicy()
        {
            if (LuauAssetImportSettings.ImportPolicy == LuauAssetImportPolicy.SourceOnly)
            {
                var errors = new List<string>();
                var changed = false;
                try
                {
                    changed = DeleteGeneratedManifest();
                }
                catch (Exception exception)
                {
                    errors.Add(
                        "Could not remove the package-owned generated manifest: " +
                        exception.Message);
                }

                // SourceOnly still owns this Resources key. An alternate asset
                // could otherwise preload a validator even after the package-
                // owned manifest was removed successfully.
                errors.AddRange(FindResourceKeyCollisions());

                GetImporterCounts(
                    out var total,
                    out var optedIn,
                    out var precompiled);
                LastStatus = new LuauFirstPartyManifestStatus(
                    total,
                    optedInAssets: optedIn,
                    precompiledAssets: precompiled,
                    hasProvenanceId: !string.IsNullOrWhiteSpace(
                        LuauAssetImportSettings.FirstPartyProvenanceId),
                    isManifestCurrent: errors.Count == 0 &&
                        AssetDatabase.LoadMainAssetAtPath(GeneratedAssetPath) == null,
                    isEmptyManifest: false,
                    manifestChanged: changed,
                    errors: errors);
                return LastStatus;
            }

            return Generate();
        }

        internal static LuauFirstPartyManifestStatus Generate()
        {
            // Fail closed while constructing the new snapshot. A prior valid
            // cache must not remain usable if scanning or generation aborts.
            FirstPartyBytecodeManifestCache.Reload(null);
            var errors = new List<string>();
            var entries = new List<FirstPartyBytecodeManifestEntry>();
            var provenanceId = LuauAssetImportSettings.FirstPartyProvenanceId;
            var hasProvenanceId = !string.IsNullOrWhiteSpace(provenanceId);
            var totalLuauAssets = 0;
            var optedInAssets = 0;
            var precompiledAssets = 0;

            if (!hasProvenanceId)
            {
                errors.Add(
                    "A first-party provenance ID is required. Configure it in " +
                    "Project Settings > Luau.Unity, then reimport Luau assets.");
            }

            foreach (var path in FindLuauAssetPaths())
            {
                var importer = AssetImporter.GetAtPath(path);
                var assets = AssetDatabase.LoadAllAssetsAtPath(path)
                    .OfType<LuauAsset>()
                    .ToArray();

                if (importer is LuauImporter luauImporter)
                {
                    totalLuauAssets++;
                    if (luauImporter.PrecompileRequested)
                        optedInAssets++;

                    if (assets.Length != 1)
                    {
                        errors.Add(
                            $"Luau importer '{path}' produced {assets.Length} Luau assets; " +
                            "reimport it with the current package importer.");
                        continue;
                    }

                    var asset = assets[0];
                    if (!IsKnownContentKind(asset, path, errors))
                        continue;

                    if (!luauImporter.PrecompileRequested)
                    {
                        if (!asset.IsSource)
                        {
                            errors.Add(
                                $"Luau asset '{path}' contains bytecode but its importer is not opted in. " +
                                "Reimport it or enable first-party precompile explicitly.");
                        }
                        continue;
                    }

                    if (asset.IsSource)
                    {
                        errors.Add(
                            $"Opted-in Luau asset '{path}' remained source after import. " +
                            "Fix its import or compilation error and reimport it before building.");
                        continue;
                    }

                    precompiledAssets++;
                    if (!hasProvenanceId)
                        continue;

                    try
                    {
                        entries.Add(CreateVerifiedEntry(path, asset, provenanceId));
                    }
                    catch (Exception exception)
                    {
                        errors.Add(
                            $"First-party artifact '{path}' is not canonical: {exception.Message} " +
                            "Reimport the asset with the current compiler and settings.");
                    }
                }
                else
                {
                    foreach (var asset in assets)
                    {
                        if (!IsKnownContentKind(asset, path, errors))
                            continue;
                        if (!asset.IsSource)
                        {
                            errors.Add(
                                $"Precompiled Luau asset '{path}' did not originate from LuauImporter. " +
                                "Only explicitly opted-in .luau assets can enter the generated manifest.");
                        }
                    }
                }
            }

            entries.Sort((left, right) =>
                StringComparer.Ordinal.Compare(left.sourceIdentity, right.sourceIdentity));
            for (var index = 1; index < entries.Count; index++)
            {
                if (string.Equals(
                    entries[index - 1].sourceIdentity,
                    entries[index].sourceIdentity,
                    StringComparison.Ordinal))
                {
                    errors.Add(
                        $"Duplicate first-party source identity '{entries[index].sourceIdentity}' " +
                        "was produced. Asset GUID identities must be unique.");
                }
            }

            errors.AddRange(FindResourceKeyCollisions());

            var changed = false;
            var current = false;
            if (errors.Count == 0)
            {
                try
                {
                    changed = WriteManifestIfChanged(provenanceId, entries.ToArray());
                    var reloaded = AssetDatabase.LoadAssetAtPath<FirstPartyBytecodeManifest>(
                        GeneratedAssetPath);
                    current = reloaded != null &&
                        ManifestMatches(reloaded, provenanceId, entries);
                    if (!current)
                    {
                        errors.Add(
                            $"The generated manifest could not be loaded from '{GeneratedAssetPath}'. " +
                            "Check that the package-owned generated path is writable, then refresh it.");
                    }
                    else
                    {
                        FirstPartyBytecodeManifestCache.Reload(reloaded);
                    }
                }
                catch (Exception exception)
                {
                    errors.Add(
                        $"Could not create or load the generated first-party manifest: {exception.Message}");
                }
            }

            if (errors.Count != 0)
            {
                try
                {
                    // Do not leave a previously generated allowlist available
                    // for the next domain reload after the project snapshot has
                    // failed validation.
                    changed |= DeleteGeneratedManifest();
                }
                catch (Exception exception)
                {
                    errors.Add(
                        "Could not invalidate the package-owned generated manifest: " +
                        exception.Message);
                }
            }

            LastStatus = new LuauFirstPartyManifestStatus(
                totalLuauAssets,
                optedInAssets,
                precompiledAssets,
                hasProvenanceId,
                isManifestCurrent: current && errors.Count == 0,
                isEmptyManifest: current && errors.Count == 0 && entries.Count == 0,
                manifestChanged: changed,
                errors: errors);
            return LastStatus;
        }

        internal static bool DeleteGeneratedManifest()
        {
            // Deletion is also a runtime-cache transition. Do this before the
            // filesystem operation so even a cleanup failure cannot leave an
            // earlier first-party allowlist active in the Editor.
            FirstPartyBytecodeManifestCache.Reload(null);
            var guid = AssetDatabase.AssetPathToGUID(GeneratedAssetPath);
            if (string.IsNullOrEmpty(guid))
                return false;

            var existing = AssetDatabase.LoadMainAssetAtPath(GeneratedAssetPath);
            if (!(existing is FirstPartyBytecodeManifest))
            {
                throw new InvalidOperationException(
                    $"'{GeneratedAssetPath}' is occupied by an asset that is not the " +
                    "package-owned first-party manifest; it was not deleted.");
            }

            if (!AssetDatabase.DeleteAsset(GeneratedAssetPath))
            {
                throw new InvalidOperationException(
                    $"Unity could not delete '{GeneratedAssetPath}'.");
            }

            return true;
        }

        internal static IReadOnlyList<string> FindResourceKeyCollisions()
        {
            var collisions = new List<string>();
            foreach (var path in AssetDatabase.GetAllAssetPaths())
            {
                if (AssetDatabase.IsValidFolder(path))
                    continue;
                if (string.Equals(path, GeneratedAssetPath, StringComparison.Ordinal))
                    continue;

                if (!TryGetResourcesKey(path, out var key) ||
                    !string.Equals(key, ResourceKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                collisions.Add(
                    $"Resource key '{ResourceKey}' is also provided by '{path}'. " +
                    "Remove or rename the colliding asset because Resources.Load would be ambiguous.");
            }

            return collisions
                .Distinct(StringComparer.Ordinal)
                .OrderBy(message => message, StringComparer.Ordinal)
                .ToArray();
        }

        internal static bool TryGetResourcesKey(string assetPath, out string key)
        {
            key = null;
            if (string.IsNullOrEmpty(assetPath))
                return false;

            var normalized = assetPath.Replace('\\', '/');
            var marker = "/Resources/";
            var markerIndex = normalized.LastIndexOf(
                marker,
                StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
                return false;

            var relative = normalized.Substring(markerIndex + marker.Length);
            if (relative.Length == 0)
                return false;

            var extensionIndex = relative.LastIndexOf('.');
            var slashIndex = relative.LastIndexOf('/');
            if (extensionIndex > slashIndex)
                relative = relative.Substring(0, extensionIndex);
            if (relative.Length == 0)
                return false;

            key = relative;
            return true;
        }

        internal static List<string> FindLuauImporterPaths()
        {
            return FindLuauAssetPaths()
                .Where(path => AssetImporter.GetAtPath(path) is LuauImporter)
                .ToList();
        }

        static void GetImporterCounts(
            out int total,
            out int optedIn,
            out int precompiled)
        {
            total = 0;
            optedIn = 0;
            precompiled = 0;
            foreach (var path in FindLuauImporterPaths())
            {
                total++;
                var importer = (LuauImporter)AssetImporter.GetAtPath(path);
                if (importer.PrecompileRequested)
                    optedIn++;

                var asset = AssetDatabase.LoadAssetAtPath<LuauAsset>(path);
                if (asset != null &&
                    asset.contentKind == LuauAssetContentKind.VerifiedBytecode)
                {
                    precompiled++;
                }
            }
        }

        static IEnumerable<string> FindLuauAssetPaths()
        {
            var importedAssetPaths = AssetDatabase.FindAssets(
                    "t:LuauAsset",
                    new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(IsProjectAssetPath);
            var luauFilePaths = AssetDatabase.GetAllAssetPaths()
                .Where(path => IsProjectAssetPath(path) &&
                    path.EndsWith(".luau", StringComparison.OrdinalIgnoreCase));

            return importedAssetPaths
                .Concat(luauFilePaths)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal);
        }

        static bool IsProjectAssetPath(string path)
        {
            return !string.IsNullOrEmpty(path) &&
                path.StartsWith("Assets/", StringComparison.Ordinal);
        }

        static bool IsKnownContentKind(
            LuauAsset asset,
            string path,
            ICollection<string> errors)
        {
            if (asset.contentKind == LuauAssetContentKind.Source ||
                asset.contentKind == LuauAssetContentKind.VerifiedBytecode)
            {
                return true;
            }

            errors.Add(
                $"Luau asset '{path}' has unsupported serialized content kind " +
                $"{(int)asset.contentKind}. Reimport it with the current package importer.");
            return false;
        }

        static FirstPartyBytecodeManifestEntry CreateVerifiedEntry(
            string path,
            LuauAsset asset,
            string expectedProvenanceId)
        {
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrWhiteSpace(guid))
                throw new InvalidOperationException("The asset has no stable Unity GUID.");

            var expectedIdentity = SourceIdentityPrefix + guid;
            var expectedProvenanceData = StrictUtf8.GetBytes(guid);
            if (!string.Equals(asset.sourceIdentity, expectedIdentity, StringComparison.Ordinal))
                throw new InvalidOperationException("Its source identity does not match its asset GUID.");
            if (!string.Equals(asset.provenanceId, expectedProvenanceId, StringComparison.Ordinal))
                throw new InvalidOperationException("Its provenance ID does not match Project Settings.");
            if (!ByteArraysEqual(asset.provenanceData, expectedProvenanceData))
                throw new InvalidOperationException("Its provenance data is not the exact UTF-8 asset GUID.");

            var defaultOptions = LuauCompileOptions.Default;
            if (asset.optimizationLevel != defaultOptions.OptimizationLevel ||
                asset.debugLevel != defaultOptions.DebugLevel ||
                asset.typeInfoLevel != defaultOptions.TypeInfoLevel ||
                asset.coverageLevel != defaultOptions.CoverageLevel)
            {
                throw new InvalidOperationException(
                    "Its compile levels do not match the current LuauImporter contract.");
            }

            // Construct from raw serialized fields every time. GetVerifiedBytecode
            // may hold a cached object created before an inspector/test mutation.
            var artifact = new LuauBytecodeArtifact(
                asset.artifactSchemaVersion,
                asset.bytes ?? Array.Empty<byte>(),
                new LuauCompileOptions
                {
                    OptimizationLevel = asset.optimizationLevel,
                    DebugLevel = asset.debugLevel,
                    TypeInfoLevel = asset.typeInfoLevel,
                    CoverageLevel = asset.coverageLevel,
                },
                asset.upstreamRevisionHash,
                asset.hostBuildFingerprint,
                asset.sourceIdentity,
                asset.sourceSha256,
                asset.bytecodeSha256,
                asset.provenanceId,
                asset.provenanceData ?? Array.Empty<byte>());

            var source = LuauImporter.ReadSourceBytes(
                path,
                LuauAssetImportSettings.MaxImportedSourceBytes);
            var sourceHash = ComputeSha256(source);
            if (!string.Equals(asset.sourceSha256, sourceHash, StringComparison.Ordinal))
                throw new InvalidOperationException("Its source hash does not match the current source bytes.");

            var compileResult = LuauUnity
                .CompileAssetSourceAsync(
                    source,
                    artifact.CompileOptions,
                    System.Threading.CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (compileResult.Kind != LuauCompileResultKind.Success)
            {
                throw new InvalidOperationException(
                    "Canonical recompilation failed: " +
                    LuauImporter.GetCompilationFailureMessage(compileResult));
            }

            var compilerOutput = compileResult.Output;
            var outputOptions = compilerOutput.CompileOptions;
            if (outputOptions.OptimizationLevel != artifact.CompileOptions.OptimizationLevel ||
                outputOptions.DebugLevel != artifact.CompileOptions.DebugLevel ||
                outputOptions.TypeInfoLevel != artifact.CompileOptions.TypeInfoLevel ||
                outputOptions.CoverageLevel != artifact.CompileOptions.CoverageLevel)
            {
                throw new InvalidOperationException(
                    "The compiler output did not preserve the requested compile levels.");
            }
            if (!string.Equals(compilerOutput.SourceSha256, sourceHash, StringComparison.Ordinal))
                throw new InvalidOperationException("The compiler reported an unexpected source hash.");
            if (!string.Equals(asset.bytecodeSha256, compilerOutput.BytecodeSha256, StringComparison.Ordinal) ||
                !ByteArraysEqual(asset.bytes, compilerOutput.ToBytecodeArray()))
            {
                throw new InvalidOperationException(
                    "Its payload is not the canonical compiler output for the current source.");
            }
            if (asset.upstreamRevisionHash != compilerOutput.UpstreamRevisionHash)
                throw new InvalidOperationException("Its upstream compiler revision is stale.");
            if (asset.hostBuildFingerprint != compilerOutput.HostBuildFingerprint)
                throw new InvalidOperationException("Its native host fingerprint is stale.");

            var sourceAfterCompilation = LuauImporter.ReadSourceBytes(
                path,
                LuauAssetImportSettings.MaxImportedSourceBytes);
            if (!ByteArraysEqual(source, sourceAfterCompilation))
            {
                throw new InvalidOperationException(
                    "Its source changed while the canonical artifact was being verified.");
            }

            return new FirstPartyBytecodeManifestEntry
            {
                artifactSchemaVersion = artifact.SchemaVersion,
                bytecodeLength = artifact.BytecodeLength,
                sourceIdentity = artifact.SourceIdentity,
                sourceSha256 = artifact.SourceSha256,
                bytecodeSha256 = artifact.BytecodeSha256,
                optimizationLevel = artifact.CompileOptions.OptimizationLevel,
                debugLevel = artifact.CompileOptions.DebugLevel,
                typeInfoLevel = artifact.CompileOptions.TypeInfoLevel,
                coverageLevel = artifact.CompileOptions.CoverageLevel,
                upstreamRevisionHash = artifact.UpstreamRevisionHash,
                hostBuildFingerprint = artifact.HostBuildFingerprint,
                provenanceData = artifact.GetProvenanceData(),
            };
        }

        static bool WriteManifestIfChanged(
            string provenanceId,
            FirstPartyBytecodeManifestEntry[] entries)
        {
            var existingMainAsset = AssetDatabase.LoadMainAssetAtPath(GeneratedAssetPath);
            var manifest = existingMainAsset as FirstPartyBytecodeManifest;
            if (existingMainAsset != null && manifest == null)
            {
                throw new InvalidOperationException(
                    $"'{GeneratedAssetPath}' is occupied by {existingMainAsset.GetType().Name}, " +
                    "not a Luau first-party manifest.");
            }

            if (manifest != null && ManifestMatches(manifest, provenanceId, entries))
                return false;

            EnsureGeneratedFolder();
            if (manifest == null)
            {
                manifest = ScriptableObject.CreateInstance<FirstPartyBytecodeManifest>();
                manifest.schemaVersion = FirstPartyBytecodeManifest.CurrentSchemaVersion;
                manifest.provenanceId = provenanceId;
                manifest.entries = CloneEntries(entries);
                try
                {
                    AssetDatabase.CreateAsset(manifest, GeneratedAssetPath);
                }
                catch
                {
                    UnityEngine.Object.DestroyImmediate(manifest);
                    throw;
                }
            }
            else
            {
                manifest.schemaVersion = FirstPartyBytecodeManifest.CurrentSchemaVersion;
                manifest.provenanceId = provenanceId;
                manifest.entries = CloneEntries(entries);
                EditorUtility.SetDirty(manifest);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(
                GeneratedAssetPath,
                ImportAssetOptions.ForceSynchronousImport |
                ImportAssetOptions.ForceUpdate);
            return true;
        }

        static void EnsureGeneratedFolder()
        {
            var folders = new[]
            {
                "Assets/Generated",
                "Assets/Generated/Luau.Unity",
                "Assets/Generated/Luau.Unity/Resources",
                "Assets/Generated/Luau.Unity/Resources/Luau.Unity",
            };
            var parent = "Assets";
            foreach (var folder in folders)
            {
                if (!AssetDatabase.IsValidFolder(folder))
                {
                    if (AssetDatabase.LoadMainAssetAtPath(folder) != null)
                    {
                        throw new InvalidOperationException(
                            $"The generated folder path '{folder}' is occupied by an asset.");
                    }

                    var name = folder.Substring(folder.LastIndexOf('/') + 1);
                    var guid = AssetDatabase.CreateFolder(parent, name);
                    if (string.IsNullOrEmpty(guid) || !AssetDatabase.IsValidFolder(folder))
                    {
                        throw new InvalidOperationException(
                            $"Unity could not create generated folder '{folder}'.");
                    }
                }
                parent = folder;
            }
        }

        static bool ManifestMatches(
            FirstPartyBytecodeManifest manifest,
            string provenanceId,
            IReadOnlyList<FirstPartyBytecodeManifestEntry> entries)
        {
            if (manifest == null ||
                manifest.schemaVersion != FirstPartyBytecodeManifest.CurrentSchemaVersion ||
                !string.Equals(manifest.provenanceId, provenanceId, StringComparison.Ordinal) ||
                manifest.entries == null ||
                manifest.entries.Length != entries.Count)
            {
                return false;
            }

            for (var index = 0; index < entries.Count; index++)
            {
                if (!EntryMatches(manifest.entries[index], entries[index]))
                    return false;
            }
            return true;
        }

        static bool EntryMatches(
            FirstPartyBytecodeManifestEntry left,
            FirstPartyBytecodeManifestEntry right)
        {
            return left != null && right != null &&
                left.artifactSchemaVersion == right.artifactSchemaVersion &&
                left.bytecodeLength == right.bytecodeLength &&
                string.Equals(left.sourceIdentity, right.sourceIdentity, StringComparison.Ordinal) &&
                string.Equals(left.sourceSha256, right.sourceSha256, StringComparison.Ordinal) &&
                string.Equals(left.bytecodeSha256, right.bytecodeSha256, StringComparison.Ordinal) &&
                left.optimizationLevel == right.optimizationLevel &&
                left.debugLevel == right.debugLevel &&
                left.typeInfoLevel == right.typeInfoLevel &&
                left.coverageLevel == right.coverageLevel &&
                left.upstreamRevisionHash == right.upstreamRevisionHash &&
                left.hostBuildFingerprint == right.hostBuildFingerprint &&
                ByteArraysEqual(left.provenanceData, right.provenanceData);
        }

        static FirstPartyBytecodeManifestEntry[] CloneEntries(
            IReadOnlyList<FirstPartyBytecodeManifestEntry> entries)
        {
            var clones = new FirstPartyBytecodeManifestEntry[entries.Count];
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                clones[index] = new FirstPartyBytecodeManifestEntry
                {
                    artifactSchemaVersion = entry.artifactSchemaVersion,
                    bytecodeLength = entry.bytecodeLength,
                    sourceIdentity = entry.sourceIdentity,
                    sourceSha256 = entry.sourceSha256,
                    bytecodeSha256 = entry.bytecodeSha256,
                    optimizationLevel = entry.optimizationLevel,
                    debugLevel = entry.debugLevel,
                    typeInfoLevel = entry.typeInfoLevel,
                    coverageLevel = entry.coverageLevel,
                    upstreamRevisionHash = entry.upstreamRevisionHash,
                    hostBuildFingerprint = entry.hostBuildFingerprint,
                    provenanceData = entry.provenanceData == null
                        ? Array.Empty<byte>()
                        : (byte[])entry.provenanceData.Clone(),
                };
            }
            return clones;
        }

        static string ComputeSha256(byte[] bytes)
        {
            byte[] hash;
            using (var algorithm = SHA256.Create())
                hash = algorithm.ComputeHash(bytes);

            var builder = new StringBuilder(hash.Length * 2);
            foreach (var value in hash)
                builder.Append(value.ToString("x2"));
            return builder.ToString();
        }

        static bool ByteArraysEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Length != right.Length)
                return false;
            for (var index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                    return false;
            }
            return true;
        }
    }
}
