using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Luau.Unity.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Luau.Unity.Tests
{
    public sealed class LuauImporterPolicyTests
    {
        const string AssetPath = "Assets/__LuauImporterPolicyTests__.luau";

        LuauAssetImportPolicy originalPolicy;
        string originalProvenanceId;
        int originalMaxSourceBytes;
        readonly List<string> importErrors = new List<string>();

        [SetUp]
        public void SetUp()
        {
            originalPolicy = LuauAssetImportSettings.ImportPolicy;
            originalProvenanceId = LuauAssetImportSettings.FirstPartyProvenanceId;
            originalMaxSourceBytes = LuauAssetImportSettings.MaxImportedSourceBytes;
            importErrors.Clear();
            LuauImporter.ImportErrorObserverForTests = importErrors.Add;
            AssetDatabase.DeleteAsset(AssetPath);
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(AssetPath);
            LuauAssetImportSettings.SetFirstPartyProvenanceIdForTests(originalProvenanceId);
            LuauAssetImportSettings.SetImportPolicyForTests(originalPolicy);
            LuauAssetImportSettings.SetMaxImportedSourceBytesForTests(originalMaxSourceBytes);
            LuauImporter.ImportErrorObserverForTests = null;
        }

        [Test]
        public void CheckedInProjectPolicyDefaultsToSourceOnly()
        {
            Assert.That(LuauAssetImportSettings.ImportPolicy,
                Is.EqualTo(LuauAssetImportPolicy.SourceOnly));
            Assert.That(
                LuauAssetImportSettings.MaxImportedSourceBytes,
                Is.EqualTo(LuauAssetImportSettings.DefaultMaxImportedSourceBytes));
        }

        [Test]
        public void SourceAdmissionAcceptsExactLimitAndRejectsOneOverBeforeReading()
        {
            var exact = Encoding.UTF8.GetBytes("return 1234");
            File.WriteAllBytes(AssetPath, exact);

            Assert.That(
                LuauImporter.ReadSourceBytes(AssetPath, exact.Length),
                Is.EqualTo(exact));
            Assert.Throws<IOException>(() =>
                LuauImporter.ReadSourceBytes(AssetPath, exact.Length - 1));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                LuauAssetImportSettings.SetMaxImportedSourceBytesForTests(0));
        }

        [Test]
        public void StrictUtf8AcceptsBomAndEmptySourceAndRejectsInvalidBytes()
        {
            var bomSource = new byte[]
            {
                0xef, 0xbb, 0xbf,
                (byte)'r', (byte)'e', (byte)'t', (byte)'u', (byte)'r', (byte)'n', (byte)' ', (byte)'1',
            };

            Assert.That(LuauImporter.DecodeSource(bomSource), Is.EqualTo("\ufeffreturn 1"));
            Assert.That(LuauImporter.DecodeSource(Array.Empty<byte>()), Is.Empty);
            Assert.Throws<DecoderFallbackException>(() =>
                LuauImporter.DecodeSource(new byte[] { 0xc3, 0x28 }));
        }

        [Test]
        public void SourceOnlyImporterPreservesTheExactAdmittedUtf8Bytes()
        {
            var source = Encoding.UTF8.GetBytes("return '\u00e9' -- exact UTF-8");
            LuauAssetImportSettings.SetImportPolicyForTests(LuauAssetImportPolicy.SourceOnly);
            LuauAssetImportSettings.SetMaxImportedSourceBytesForTests(source.Length);
            File.WriteAllBytes(AssetPath, source);

            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);

            var asset = AssetDatabase.LoadAssetAtPath<LuauAsset>(AssetPath);
            Assert.That(asset, Is.Not.Null);
            Assert.That(asset.IsPrecompiled, Is.False);
            Assert.That(asset.AsSpan().ToArray(), Is.EqualTo(source));
        }

        [Test]
        public void ImporterRejectsOneByteOverTheConfiguredLimit()
        {
            var source = Encoding.UTF8.GetBytes("return 1234");
            LuauAssetImportSettings.SetMaxImportedSourceBytesForTests(source.Length - 1);
            File.WriteAllBytes(AssetPath, source);
            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);

            var asset = AssetDatabase.LoadAssetAtPath<LuauAsset>(AssetPath);
            AssertImportError(new Regex(
                "project importer limit is",
                RegexOptions.CultureInvariant));
            Assert.That(asset, Is.Not.Null);
            Assert.That(asset.AsSpan().Length, Is.Zero);
        }

        [Test]
        public void ImporterPreservesBomAndEmptySourceBytes()
        {
            var bomSource = new byte[]
            {
                0xef, 0xbb, 0xbf,
                (byte)'r', (byte)'e', (byte)'t', (byte)'u', (byte)'r', (byte)'n', (byte)' ', (byte)'1',
            };
            LuauAssetImportSettings.SetMaxImportedSourceBytesForTests(bomSource.Length);
            File.WriteAllBytes(AssetPath, bomSource);
            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);

            var bomAsset = AssetDatabase.LoadAssetAtPath<LuauAsset>(AssetPath);
            Assert.That(bomAsset.AsSpan().ToArray(), Is.EqualTo(bomSource));

            File.WriteAllBytes(AssetPath, Array.Empty<byte>());
            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);

            var emptyAsset = AssetDatabase.LoadAssetAtPath<LuauAsset>(AssetPath);
            Assert.That(emptyAsset.AsSpan().Length, Is.Zero);
        }

        [Test]
        public void ImporterRejectsInvalidUtf8WithoutPersistingReplacementText()
        {
            var invalid = new byte[] { 0xc3, 0x28 };
            LuauAssetImportSettings.SetMaxImportedSourceBytesForTests(invalid.Length);
            File.WriteAllBytes(AssetPath, invalid);
            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);

            var asset = AssetDatabase.LoadAssetAtPath<LuauAsset>(AssetPath);
            AssertImportError(new Regex(
                "not valid UTF-8",
                RegexOptions.CultureInvariant));
            Assert.That(asset, Is.Not.Null);
            Assert.That(asset.AsSpan().Length, Is.Zero);
        }

        [Test]
        public void CompileFailurePreservesTheExactAdmittedSource()
        {
            var source = Encoding.UTF8.GetBytes("local broken = )");
            var diagnostic = Assert.Throws<LuauCompilationException>(() =>
                LuauCompiler.Compile(source));
            LuauAssetImportSettings.SetMaxImportedSourceBytesForTests(source.Length);
            File.WriteAllBytes(AssetPath, source);
            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);

            var asset = AssetDatabase.LoadAssetAtPath<LuauAsset>(AssetPath);
            AssertImportError(new Regex(
                Regex.Escape(diagnostic.Message),
                RegexOptions.CultureInvariant));
            Assert.That(asset, Is.Not.Null);
            Assert.That(asset.IsPrecompiled, Is.False);
            Assert.That(asset.AsSpan().ToArray(), Is.EqualTo(source));
        }

        [Test]
        public void ImporterUsesBoundedSharedLaneAndPreservesSourceOnOutputLimit()
        {
            var source = Encoding.UTF8.GetBytes("return 42");
            var providerCalls = 0;
            var outputLimit = new LuauCompilationLimitException(
                LuauCompilationLimitKind.BytecodeBytesPerResult,
                actual: 4097,
                limit: 4096);
            using var providerOverride = LuauUnity.OverrideAssetCompilationProviderForTests(
                (admittedSource, options, cancellationToken) =>
                {
                    Interlocked.Increment(ref providerCalls);
                    Assert.That(admittedSource.ToArray(), Is.EqualTo(source));
                    Assert.That(options, Is.EqualTo(LuauCompileOptions.Default));
                    Assert.That(cancellationToken, Is.EqualTo(CancellationToken.None));
                    return new ValueTask<LuauCompileResult>(
                        LuauCompileResult.InfrastructureFailure(outputLimit));
                });
            LuauAssetImportSettings.SetImportPolicyForTests(LuauAssetImportPolicy.SourceOnly);
            LuauAssetImportSettings.SetMaxImportedSourceBytesForTests(source.Length);
            File.WriteAllBytes(AssetPath, source);
            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);

            var asset = AssetDatabase.LoadAssetAtPath<LuauAsset>(AssetPath);
            AssertImportError(new Regex(
                Regex.Escape(outputLimit.Message),
                RegexOptions.CultureInvariant));
            Assert.That(providerCalls, Is.EqualTo(1));
            Assert.That(asset, Is.Not.Null);
            Assert.That(asset.IsPrecompiled, Is.False);
            Assert.That(asset.AsSpan().ToArray(), Is.EqualTo(source));
        }

        [Test]
        public void CompilerIdentityTracksCoverageInstrumentation()
        {
            var source = Encoding.UTF8.GetBytes("return 42");
            var defaultOutput = LuauCompiler.Compile(source, LuauCompileOptions.Default);
            var coverageOutput = LuauCompiler.Compile(
                source,
                LuauCompileOptions.Default with
                {
                    CoverageLevel = 2,
                });

            Assert.That(defaultOutput.CompileOptions.CoverageLevel, Is.Zero);
            Assert.That(coverageOutput.CompileOptions.CoverageLevel, Is.EqualTo(2));
            Assert.That(
                coverageOutput.BytecodeSha256,
                Is.Not.EqualTo(defaultOutput.BytecodeSha256));
            Assert.That(
                LuauCompilerIdentityDependency.ComputeHash(coverageOutput),
                Is.Not.EqualTo(LuauCompilerIdentityDependency.ComputeHash(defaultOutput)));
        }

        [Test]
        public void SourceOnlyReimportReplacesPrecompiledContentAndValidatorInspectsContent()
        {
            LuauAssetImportSettings.SetFirstPartyProvenanceIdForTests("tests:first-party");
            LuauAssetImportSettings.SetImportPolicyForTests(
                LuauAssetImportPolicy.AllowFirstPartyPrecompile);

            File.WriteAllText(AssetPath, "return 42");
            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);

            var importer = (LuauImporter)AssetImporter.GetAtPath(AssetPath);
            var serializedImporter = new SerializedObject(importer);
            serializedImporter.FindProperty("precompile").boolValue = true;
            serializedImporter.ApplyModifiedPropertiesWithoutUndo();
            importer.SaveAndReimport();

            var precompiled = AssetDatabase.LoadAssetAtPath<LuauAsset>(AssetPath);
            var assetGuid = AssetDatabase.AssetPathToGUID(AssetPath);
            Assert.That(precompiled.IsPrecompiled, Is.True);
            Assert.Throws<InvalidOperationException>(() => precompiled.AsMemory());
            Assert.That(
                new SerializedObject(precompiled).FindProperty("sourceIdentity").stringValue,
                Is.EqualTo("unity-asset-guid:" + assetGuid));
            Assert.That(
                Encoding.UTF8.GetString(
                    ReadByteArray(new SerializedObject(precompiled), "provenanceData")),
                Is.EqualTo(assetGuid));

            using (var state = LuauState.Create(TestBudgets.StateOptions with
            {
                BytecodePolicy = LuauBytecodePolicy.RequireValidator,
                BytecodeValidator = new AssetGuidValidator(
                    "tests:first-party",
                    assetGuid),
                MaxBytecodeBytes = LuauStateOptions.Default.MaxBytecodeBytes,
            }))
            {
                using var results = state.Execute(precompiled);
                Assert.That(results[0].Read<int>(), Is.EqualTo(42));
            }
            Assert.Throws<InvalidOperationException>(() =>
                LuauSourceOnlyAssetValidator.ValidateSourceOnly(new[] { AssetPath }));

            LuauAssetImportSettings.SetImportPolicyForTests(LuauAssetImportPolicy.SourceOnly);
            importer.SaveAndReimport();

            var sourceOnly = AssetDatabase.LoadAssetAtPath<LuauAsset>(AssetPath);
            Assert.That(sourceOnly.IsPrecompiled, Is.False);
            Assert.That(Encoding.UTF8.GetString(sourceOnly.AsSpan()), Is.EqualTo("return 42"));
            Assert.DoesNotThrow(() =>
                LuauSourceOnlyAssetValidator.ValidateSourceOnly(new[] { AssetPath }));

            var tampered = new SerializedObject(sourceOnly);
            tampered.FindProperty("contentKind").intValue = 99;
            tampered.ApplyModifiedPropertiesWithoutUndo();
            Assert.Throws<InvalidOperationException>(() => sourceOnly.AsMemory());
            Assert.Throws<InvalidOperationException>(() =>
            {
                _ = sourceOnly.IsPrecompiled;
            });
            Assert.Throws<InvalidOperationException>(() =>
                LuauSourceOnlyAssetValidator.ValidateSourceOnly(new[] { AssetPath }));
        }

        [Test]
        public void LegacyPrecompiledAssetWithoutSourceIdentityRequiresReimport()
        {
            var output = LuauCompiler.Compile(Encoding.UTF8.GetBytes("return 42"));
            var artifact = LuauBytecodeArtifact.Create(
                output,
                "unity-tests/legacy-source",
                "tests:first-party");
            var asset = ScriptableObject.CreateInstance<LuauAsset>();
            try
            {
                asset.SetVerifiedBytecode("return 42", artifact);
                asset.sourceIdentity = string.Empty;

                var exception = Assert.Throws<InvalidOperationException>(() =>
                    asset.GetVerifiedBytecode());
                Assert.That(exception.Message, Does.Contain("source identity"));
                Assert.That(exception.Message, Does.Contain("Reimport"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        static byte[] ReadByteArray(SerializedObject serializedObject, string propertyName)
        {
            var property = serializedObject.FindProperty(propertyName);
            var bytes = new byte[property.arraySize];
            for (var index = 0; index < bytes.Length; index++)
                bytes[index] = (byte)property.GetArrayElementAtIndex(index).intValue;
            return bytes;
        }

        void AssertImportError(Regex expected)
        {
            Assert.That(importErrors, Has.Count.EqualTo(1));
            Assert.That(expected.IsMatch(importErrors[0]), Is.True, importErrors[0]);
            importErrors.Clear();
        }

        sealed class AssetGuidValidator : ILuauBytecodeValidator
        {
            readonly string provenanceId;
            readonly string assetGuid;

            public AssetGuidValidator(string provenanceId, string assetGuid)
            {
                this.provenanceId = provenanceId;
                this.assetGuid = assetGuid;
            }

            public bool IsValid(
                LuauBytecodeArtifact artifact,
                ReadOnlySpan<byte> bytecode)
            {
                return artifact.SourceIdentity == "unity-asset-guid:" + assetGuid &&
                    artifact.ProvenanceId == provenanceId &&
                    bytecode.Length == artifact.BytecodeLength &&
                    Encoding.UTF8.GetString(artifact.GetProvenanceData()) == assetGuid;
            }
        }
    }
}
