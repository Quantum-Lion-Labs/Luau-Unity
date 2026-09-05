using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Luau.Unity.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Luau.Unity.Tests
{
    public sealed class LuauFirstPartyManifestGeneratorTests
    {
        const string TestRoot = "Assets/__LuauFirstPartyManifestGeneratorTests__";
        const string GeneratedParent = "Assets/Generated";
        const string GeneratedRoot = "Assets/Generated/Luau.Unity";
        const string ProvenanceId = "tests:generated-manifest/v1";

        readonly List<string> importErrors = new List<string>();
        readonly List<ImporterSnapshot> importerSnapshots = new List<ImporterSnapshot>();
        LuauAssetImportPolicy originalPolicy;
        string originalProvenanceId;
        bool generatedParentExisted;
        bool generatedRootExisted;
        byte[] originalManifestBytes;
        byte[] originalManifestMetaBytes;
        bool restoreCompleted;

        [SetUp]
        public void SetUp()
        {
            originalPolicy = LuauAssetImportSettings.ImportPolicy;
            originalProvenanceId = LuauAssetImportSettings.FirstPartyProvenanceId;
            generatedParentExisted = AssetDatabase.IsValidFolder(GeneratedParent);
            generatedRootExisted = AssetDatabase.IsValidFolder(GeneratedRoot);
            originalManifestBytes = ReadFileIfPresent(
                ToAbsolutePath(LuauFirstPartyManifestGenerator.GeneratedAssetPath));
            originalManifestMetaBytes = ReadFileIfPresent(
                ToAbsolutePath(LuauFirstPartyManifestGenerator.GeneratedAssetPath) + ".meta");
            restoreCompleted = false;

            try
            {
                AssetDatabase.DeleteAsset(TestRoot);
                LuauImporter.ImportErrorObserverForTests = importErrors.Add;
                LuauAssetImportSettings.SetFirstPartyProvenanceIdForTests(ProvenanceId);
                LuauAssetImportSettings.SetImportPolicyForTests(
                    LuauAssetImportPolicy.AllowFirstPartyPrecompile);
                CaptureAndDisableExistingOptIns();
                LuauAssetImportSettings.ReimportLuauAssets();
                LuauFirstPartyManifestRefresh.RefreshNow(logErrors: false);
                LuauFirstPartyManifestGenerator.DeleteGeneratedManifest();
                importErrors.Clear();
            }
            catch
            {
                try
                {
                    RestoreProjectState();
                }
                catch (Exception cleanupException)
                {
                    Console.Error.WriteLine(
                        "Luau manifest test setup cleanup also failed: " + cleanupException);
                }
                throw;
            }
        }

        [TearDown]
        public void TearDown()
        {
            RestoreProjectState();
        }

        void RestoreProjectState()
        {
            if (restoreCompleted)
                return;

            try
            {
                AssetDatabase.DeleteAsset(TestRoot);
                LuauAssetImportSettings.SetFirstPartyProvenanceIdForTests(originalProvenanceId);
                LuauAssetImportSettings.SetImportPolicyForTests(originalPolicy);
                RestoreExistingImporterOptIns();
                LuauAssetImportSettings.ReimportLuauAssets();

                // Consume any delayed refresh scheduled by importing/deleting the
                // temporary .luau files before restoring the exact generated asset.
                LuauFirstPartyManifestRefresh.RefreshNow(logErrors: false);
                RestoreGeneratedAssets();
                restoreCompleted = true;
            }
            finally
            {
                LuauImporter.ImportErrorObserverForTests = null;
                if (restoreCompleted)
                    importerSnapshots.Clear();
            }
        }

        [Test]
        public void GenerationCoversAllAssetsAndIsOrdinalDeterministicAndNoOp()
        {
            var firstPath = TestRoot + "/Unreferenced/Zeta.luau";
            var secondPath = TestRoot + "/Resources/Alpha.luau";
            var first = CreatePrecompiledAsset(firstPath, "return 1");
            var second = CreatePrecompiledAsset(secondPath, "return 2");
            var expectedIdentities = new[]
            {
                first.sourceIdentity,
                second.sourceIdentity,
            }.OrderBy(value => value, StringComparer.Ordinal).ToArray();

            var generated = LuauFirstPartyManifestGenerator.Generate();
            var manifest = LoadGeneratedManifest();
            var firstSerialization = EditorJsonUtility.ToJson(manifest, prettyPrint: false);
            var actualIdentities = manifest.entries
                .Where(entry => expectedIdentities.Contains(entry.sourceIdentity))
                .Select(entry => entry.sourceIdentity)
                .ToArray();

            Assert.That(generated.Errors, Is.Empty);
            Assert.That(generated.IsManifestCurrent, Is.True);
            Assert.That(generated.ManifestChanged, Is.True);
            Assert.That(generated.OptedInAssets, Is.GreaterThanOrEqualTo(2));
            Assert.That(generated.PrecompiledAssets, Is.GreaterThanOrEqualTo(2));
            Assert.That(actualIdentities, Is.EqualTo(expectedIdentities));
            Assert.That(
                manifest.entries.Select(entry => entry.sourceIdentity),
                Is.Ordered.Using<string>(StringComparer.Ordinal));

            var noOp = LuauFirstPartyManifestGenerator.Generate();
            var secondSerialization = EditorJsonUtility.ToJson(
                LoadGeneratedManifest(),
                prettyPrint: false);

            Assert.That(noOp.Errors, Is.Empty);
            Assert.That(noOp.IsManifestCurrent, Is.True);
            Assert.That(noOp.ManifestChanged, Is.False);
            Assert.That(secondSerialization, Is.EqualTo(firstSerialization));
            Assert.That(LuauFirstPartyManifestGenerator.LastStatus, Is.SameAs(noOp));
        }

        [Test]
        public void ValidProvenanceProducesACurrentZeroEntryManifest()
        {
            var status = LuauFirstPartyManifestGenerator.Generate();
            var manifest = LoadGeneratedManifest();

            Assert.That(status.Errors, Is.Empty);
            Assert.That(status.HasProvenanceId, Is.True);
            Assert.That(status.IsManifestCurrent, Is.True);
            Assert.That(status.IsEmptyManifest, Is.True);
            Assert.That(manifest.provenanceId, Is.EqualTo(ProvenanceId));
            Assert.That(manifest.entries, Is.Empty);
        }

        [TestCase(LuauAssetImportPolicy.SourceOnly)]
        [TestCase(LuauAssetImportPolicy.AllowFirstPartyPrecompile)]
        public void UnexpectedRefreshFailureReplacesSuccessAndClearsValidator(
            LuauAssetImportPolicy policy)
        {
            var previous = LuauFirstPartyManifestRefresh.RefreshNow(logErrors: false);
            Assert.That(previous.IsManifestCurrent, Is.True);
            Assert.That(FirstPartyBytecodeManifestCache.GetValidatorOrThrow(), Is.Not.Null);
            LuauAssetImportSettings.SetImportPolicyForTests(policy);

            var failed = LuauFirstPartyManifestRefresh.RefreshNowCore(
                logErrors: false,
                refresh: () => throw new IOException("Asset enumeration failed."));

            Assert.That(failed, Is.Not.SameAs(previous));
            Assert.That(LuauFirstPartyManifestGenerator.LastStatus, Is.SameAs(failed));
            Assert.That(failed.IsManifestCurrent, Is.False);
            Assert.That(failed.HasAssetSnapshot, Is.False);
            Assert.That(failed.Errors.Single(), Does.Contain("Asset enumeration failed."));
            Assert.Throws<InvalidOperationException>(() =>
                FirstPartyBytecodeManifestCache.GetValidatorOrThrow());

            var recovered = LuauFirstPartyManifestRefresh.RefreshNow(logErrors: false);
            Assert.That(recovered.IsManifestCurrent, Is.True);
            Assert.That(recovered.HasAssetSnapshot, Is.True);
            Assert.That(recovered.Errors, Is.Empty);
        }

        [Test]
        public void SourceOnlyBuildDeletesThePackageOwnedManifest()
        {
            Assert.That(LuauFirstPartyManifestGenerator.Generate().Errors, Is.Empty);
            Assert.That(
                AssetDatabase.LoadMainAssetAtPath(
                    LuauFirstPartyManifestGenerator.GeneratedAssetPath),
                Is.Not.Null);
            LuauAssetImportSettings.SetImportPolicyForTests(
                LuauAssetImportPolicy.SourceOnly);

            Assert.DoesNotThrow(() =>
                new LuauSourceOnlyBuildPreprocessor().OnPreprocessBuild(null));

            Assert.That(
                AssetDatabase.LoadMainAssetAtPath(
                    LuauFirstPartyManifestGenerator.GeneratedAssetPath),
                Is.Null);
            Assert.That(LuauFirstPartyManifestGenerator.LastStatus.IsManifestCurrent, Is.True);
        }

        [Test]
        public void FirstPartyBuildFailsClosedWhenProvenanceIsMissing()
        {
            LuauAssetImportSettings.SetFirstPartyProvenanceIdForTests(string.Empty);

            var exception = Assert.Throws<BuildFailedException>(() =>
                new LuauSourceOnlyBuildPreprocessor().OnPreprocessBuild(null));

            Assert.That(exception.Message, Does.Contain("provenance ID").IgnoreCase);
            Assert.That(LuauFirstPartyManifestGenerator.LastStatus.HasProvenanceId, Is.False);
            Assert.That(LuauFirstPartyManifestGenerator.LastStatus.IsManifestCurrent, Is.False);
        }

        [Test]
        public void ResourceKeyCollisionIsReportedBeforeManifestCreation()
        {
            var collisionPath = TestRoot +
                "/Resources/Luau.Unity/FirstPartyBytecodeManifest.txt";
            WriteAndImport(collisionPath, "collision");

            var status = LuauFirstPartyManifestGenerator.Generate();

            Assert.That(status.Errors, Has.Some.Contains("Resource key"));
            Assert.That(status.Errors, Has.Some.Contains(collisionPath));
            Assert.That(status.IsManifestCurrent, Is.False);
            Assert.That(
                AssetDatabase.LoadMainAssetAtPath(
                    LuauFirstPartyManifestGenerator.GeneratedAssetPath),
                Is.Null);
        }

        [Test]
        public void SourceOnlyBuildRejectsAlternateManifestResourceKey()
        {
            Assert.That(LuauFirstPartyManifestGenerator.Generate().Errors, Is.Empty);
            var collisionPath = TestRoot +
                "/Resources/Luau.Unity/FirstPartyBytecodeManifest.txt";
            WriteAndImport(collisionPath, "collision");
            LuauAssetImportSettings.SetImportPolicyForTests(
                LuauAssetImportPolicy.SourceOnly);

            var exception = Assert.Throws<BuildFailedException>(() =>
                new LuauSourceOnlyBuildPreprocessor().OnPreprocessBuild(null));

            Assert.That(exception.Message, Does.Contain("Resource key"));
            Assert.That(
                AssetDatabase.LoadMainAssetAtPath(
                    LuauFirstPartyManifestGenerator.GeneratedAssetPath),
                Is.Null);
            Assert.That(AssetDatabase.LoadMainAssetAtPath(collisionPath), Is.Not.Null);
        }

        [Test]
        public void SourceChangedWithoutReimportIsRejectedAsStale()
        {
            var path = TestRoot + "/Stale.luau";
            CreatePrecompiledAsset(path, "return 10");
            File.WriteAllText(ToAbsolutePath(path), "return 11", new UTF8Encoding(false));

            var status = LuauFirstPartyManifestGenerator.Generate();

            Assert.That(status.Errors, Has.Some.Contains(path));
            Assert.That(status.Errors, Has.Some.Contains("source hash"));
            Assert.That(status.IsManifestCurrent, Is.False);
        }

        [Test]
        public void OptedInCompilationFallbackCannotSilentlyEnterManifest()
        {
            var path = TestRoot + "/Broken.luau";
            var asset = CreatePrecompiledAsset(path, "local broken = )", expectFallback: true);

            var status = LuauFirstPartyManifestGenerator.Generate();

            Assert.That(asset.IsPrecompiled, Is.False);
            Assert.That(importErrors, Is.Not.Empty);
            Assert.That(status.Errors, Has.Some.Contains(path));
            Assert.That(status.Errors, Has.Some.Contains("remained source"));
            Assert.That(status.OptedInAssets, Is.GreaterThanOrEqualTo(1));
            Assert.That(status.IsManifestCurrent, Is.False);
        }

        [TestCase("payload")]
        [TestCase("contentKind")]
        [TestCase("provenance")]
        public void MalformedSerializedArtifactIsRejected(string corruption)
        {
            var path = TestRoot + "/Malformed.luau";
            var asset = CreatePrecompiledAsset(path, "return 99");
            switch (corruption)
            {
                case "payload":
                    asset.bytes[asset.bytes.Length - 1] ^= 0x01;
                    break;
                case "contentKind":
                    asset.contentKind = (LuauAssetContentKind)99;
                    break;
                case "provenance":
                    asset.provenanceData[0] ^= 0x01;
                    break;
                default:
                    Assert.Fail("Unknown corruption: " + corruption);
                    break;
            }
            EditorUtility.SetDirty(asset);

            var status = LuauFirstPartyManifestGenerator.Generate();

            Assert.That(status.Errors, Has.Some.Contains(path));
            Assert.That(status.IsManifestCurrent, Is.False);
        }

        [Test]
        public void PrecompiledAssetFromAnotherImporterIsRejected()
        {
            EnsureFolder(TestRoot);
            var output = LuauCompiler.Compile(Encoding.UTF8.GetBytes("return 7"));
            var artifact = LuauBytecodeArtifact.Create(
                output,
                "unity-asset-guid:not-an-imported-luau-file",
                ProvenanceId,
                Encoding.UTF8.GetBytes("not-an-imported-luau-file"));
            var asset = ScriptableObject.CreateInstance<LuauAsset>();
            asset.SetVerifiedBytecode("return 7", artifact);
            var path = TestRoot + "/Foreign.asset";
            AssetDatabase.CreateAsset(asset, path);

            var status = LuauFirstPartyManifestGenerator.Generate();

            Assert.That(status.Errors, Has.Some.Contains(path));
            Assert.That(status.Errors, Has.Some.Contains("did not originate from LuauImporter"));
        }

        [Test]
        public void OccupiedGeneratedAssetPathFailsWithoutOverwritingTheAsset()
        {
            EnsureFolder(Path.GetDirectoryName(
                LuauFirstPartyManifestGenerator.GeneratedAssetPath).Replace('\\', '/'));
            var occupant = new TextAsset("occupied");
            AssetDatabase.CreateAsset(
                occupant,
                LuauFirstPartyManifestGenerator.GeneratedAssetPath);

            var status = LuauFirstPartyManifestGenerator.Generate();

            Assert.That(status.Errors, Has.Some.Contains("occupied"));
            Assert.That(
                AssetDatabase.LoadAssetAtPath<TextAsset>(
                    LuauFirstPartyManifestGenerator.GeneratedAssetPath),
                Is.Not.Null);
        }

        LuauAsset CreatePrecompiledAsset(
            string path,
            string source,
            bool expectFallback = false)
        {
            WriteAndImport(path, source);
            var importer = AssetImporter.GetAtPath(path);
            Assert.That(importer, Is.TypeOf<LuauImporter>());
            var serialized = new SerializedObject(importer);
            var precompile = serialized.FindProperty("precompile");
            Assert.That(precompile, Is.Not.Null);
            precompile.boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            importer.SaveAndReimport();

            var asset = AssetDatabase.LoadAssetAtPath<LuauAsset>(path);
            Assert.That(asset, Is.Not.Null);
            Assert.That(asset.IsPrecompiled, Is.EqualTo(!expectFallback));
            return asset;
        }

        static void WriteAndImport(string path, string contents)
        {
            var folder = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(folder);
            File.WriteAllText(ToAbsolutePath(path), contents, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }

        static void EnsureFolder(string assetFolder)
        {
            var normalized = assetFolder.Replace('\\', '/').TrimEnd('/');
            var parts = normalized.Split('/');
            var current = parts[0];
            for (var index = 1; index < parts.Length; index++)
            {
                var next = current + "/" + parts[index];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    var guid = AssetDatabase.CreateFolder(current, parts[index]);
                    Assert.That(guid, Is.Not.Empty, "Could not create test folder " + next);
                }
                current = next;
            }
        }

        static FirstPartyBytecodeManifest LoadGeneratedManifest()
        {
            var manifest = AssetDatabase.LoadAssetAtPath<FirstPartyBytecodeManifest>(
                LuauFirstPartyManifestGenerator.GeneratedAssetPath);
            Assert.That(manifest, Is.Not.Null);
            return manifest;
        }

        void CaptureAndDisableExistingOptIns()
        {
            importerSnapshots.Clear();
            foreach (var guid in AssetDatabase.FindAssets("t:LuauAsset", new[] { "Assets" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) ||
                    path.StartsWith(TestRoot + "/", StringComparison.Ordinal) ||
                    !(AssetImporter.GetAtPath(path) is LuauImporter importer))
                {
                    continue;
                }

                var snapshot = new ImporterSnapshot(
                    path,
                    importer.PrecompileRequested,
                    ReadFileIfPresent(ToAbsolutePath(path) + ".meta"));
                importerSnapshots.Add(snapshot);
                if (snapshot.PrecompileRequested)
                    SetImporterPrecompile(importer, false);
            }
        }

        void RestoreExistingImporterOptIns()
        {
            foreach (var snapshot in importerSnapshots)
            {
                if (AssetImporter.GetAtPath(snapshot.Path) is LuauImporter importer &&
                    importer.PrecompileRequested != snapshot.PrecompileRequested)
                {
                    SetImporterPrecompile(importer, snapshot.PrecompileRequested);
                }

                if (snapshot.MetaBytes != null)
                {
                    File.WriteAllBytes(ToAbsolutePath(snapshot.Path) + ".meta", snapshot.MetaBytes);
                    AssetDatabase.ImportAsset(
                        snapshot.Path,
                        ImportAssetOptions.ForceSynchronousImport |
                        ImportAssetOptions.ForceUpdate);
                }
            }
        }

        static void SetImporterPrecompile(LuauImporter importer, bool enabled)
        {
            var serialized = new SerializedObject(importer);
            var precompile = serialized.FindProperty("precompile");
            Assert.That(precompile, Is.Not.Null);
            precompile.boolValue = enabled;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            importer.SaveAndReimport();
        }

        void RestoreGeneratedAssets()
        {
            AssetDatabase.DeleteAsset(LuauFirstPartyManifestGenerator.GeneratedAssetPath);

            if (!generatedRootExisted)
                AssetDatabase.DeleteAsset(GeneratedRoot);
            if (!generatedParentExisted &&
                AssetDatabase.IsValidFolder(GeneratedParent) &&
                !AssetDatabase.GetAllAssetPaths().Any(path =>
                    path.StartsWith(GeneratedParent + "/", StringComparison.Ordinal)))
            {
                AssetDatabase.DeleteAsset(GeneratedParent);
            }

            if (originalManifestBytes != null)
            {
                var absolutePath = ToAbsolutePath(
                    LuauFirstPartyManifestGenerator.GeneratedAssetPath);
                Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
                File.WriteAllBytes(absolutePath, originalManifestBytes);
                if (originalManifestMetaBytes != null)
                    File.WriteAllBytes(absolutePath + ".meta", originalManifestMetaBytes);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }

            var restored = AssetDatabase.LoadAssetAtPath<FirstPartyBytecodeManifest>(
                LuauFirstPartyManifestGenerator.GeneratedAssetPath);
            FirstPartyBytecodeManifestCache.Reload(restored);
        }

        static byte[] ReadFileIfPresent(string path)
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        static string ToAbsolutePath(string assetPath)
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
        }

        sealed class ImporterSnapshot
        {
            internal ImporterSnapshot(
                string path,
                bool precompileRequested,
                byte[] metaBytes)
            {
                Path = path;
                PrecompileRequested = precompileRequested;
                MetaBytes = metaBytes;
            }

            internal string Path { get; }
            internal bool PrecompileRequested { get; }
            internal byte[] MetaBytes { get; }
        }
    }
}
