using System;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace Luau.Unity.Tests
{
    public sealed class LuauFirstPartyManifestTests
    {
        [TearDown]
        public void TearDown()
        {
            FirstPartyBytecodeManifestCache.Reset();
        }

        [Test]
        public void FullyMatchingArtifactAndExactPayloadAreAccepted()
        {
            using var fixture = new ManifestFixture();
            var validator = new FirstPartyBytecodeManifestValidator(fixture.Manifest);

            Assert.That(validator.IsValid(fixture.Artifact, fixture.Bytecode), Is.True);
        }

        [TestCase("artifactSchemaVersion")]
        [TestCase("bytecodeLength")]
        [TestCase("sourceIdentity")]
        [TestCase("sourceSha256")]
        [TestCase("bytecodeSha256")]
        [TestCase("optimizationLevel")]
        [TestCase("debugLevel")]
        [TestCase("typeInfoLevel")]
        [TestCase("coverageLevel")]
        [TestCase("upstreamRevisionHash")]
        [TestCase("hostBuildFingerprint")]
        [TestCase("provenanceId")]
        [TestCase("provenanceData")]
        public void EverySecurityRelevantManifestFieldIsFailClosed(string field)
        {
            using var fixture = new ManifestFixture();
            var entry = fixture.Manifest.entries[0];

            switch (field)
            {
                case "artifactSchemaVersion":
                    entry.artifactSchemaVersion++;
                    Assert.Throws<InvalidOperationException>(() =>
                        new FirstPartyBytecodeManifestValidator(fixture.Manifest));
                    return;
                case "bytecodeLength":
                    entry.bytecodeLength++;
                    break;
                case "sourceIdentity":
                    entry.sourceIdentity += "/different";
                    break;
                case "sourceSha256":
                    entry.sourceSha256 = DifferentHash(entry.sourceSha256);
                    break;
                case "bytecodeSha256":
                    entry.bytecodeSha256 = DifferentHash(entry.bytecodeSha256);
                    break;
                case "optimizationLevel":
                    entry.optimizationLevel = NextLevel(entry.optimizationLevel, 2);
                    break;
                case "debugLevel":
                    entry.debugLevel = NextLevel(entry.debugLevel, 2);
                    break;
                case "typeInfoLevel":
                    entry.typeInfoLevel = NextLevel(entry.typeInfoLevel, 1);
                    break;
                case "coverageLevel":
                    entry.coverageLevel = NextLevel(entry.coverageLevel, 2);
                    break;
                case "upstreamRevisionHash":
                    entry.upstreamRevisionHash++;
                    break;
                case "hostBuildFingerprint":
                    entry.hostBuildFingerprint++;
                    break;
                case "provenanceId":
                    fixture.Manifest.provenanceId += "/different";
                    break;
                case "provenanceData":
                    entry.provenanceData = (byte[])entry.provenanceData.Clone();
                    entry.provenanceData[0] ^= 0x01;
                    break;
                default:
                    Assert.Fail("Unknown manifest field: " + field);
                    break;
            }

            var validator = new FirstPartyBytecodeManifestValidator(fixture.Manifest);
            Assert.That(
                validator.IsValid(fixture.Artifact, fixture.Bytecode),
                Is.False,
                field + " was not authenticated.");
        }

        [Test]
        public void ExactPayloadTamperingAndLengthMismatchAreRejected()
        {
            using var fixture = new ManifestFixture();
            var validator = new FirstPartyBytecodeManifestValidator(fixture.Manifest);
            var tampered = (byte[])fixture.Bytecode.Clone();
            tampered[tampered.Length / 2] ^= 0x01;

            Assert.That(validator.IsValid(fixture.Artifact, tampered), Is.False);
            Assert.That(
                validator.IsValid(
                    fixture.Artifact,
                    new ReadOnlySpan<byte>(fixture.Bytecode, 0, fixture.Bytecode.Length - 1)),
                Is.False);
        }

        [Test]
        public void MissingAndUnknownEntriesAreRejected()
        {
            using var fixture = new ManifestFixture();
            fixture.Manifest.entries = Array.Empty<FirstPartyBytecodeManifestEntry>();
            var emptyValidator = new FirstPartyBytecodeManifestValidator(fixture.Manifest);

            Assert.That(emptyValidator.IsValid(fixture.Artifact, fixture.Bytecode), Is.False);

            fixture.Manifest.entries = new[] { ManifestFixture.CreateEntry(fixture.Artifact) };
            var validator = new FirstPartyBytecodeManifestValidator(fixture.Manifest);
            var other = LuauBytecodeArtifact.Create(
                fixture.Output,
                "unity-asset-guid:ffffffffffffffffffffffffffffffff",
                fixture.Artifact.ProvenanceId,
                Encoding.UTF8.GetBytes("ffffffffffffffffffffffffffffffff"));

            Assert.That(validator.IsValid(other, fixture.Bytecode), Is.False);
        }

        [Test]
        public void DuplicateAndNonOrdinalEntriesAreRejectedDuringInitialization()
        {
            using var fixture = new ManifestFixture();
            var first = ManifestFixture.CreateEntry(fixture.Artifact);
            var duplicate = ManifestFixture.CreateEntry(fixture.Artifact);
            fixture.Manifest.entries = new[] { first, duplicate };

            Assert.Throws<InvalidOperationException>(() =>
                new FirstPartyBytecodeManifestValidator(fixture.Manifest));

            first.sourceIdentity = "unity-asset-guid:z";
            duplicate.sourceIdentity = "unity-asset-guid:a";
            fixture.Manifest.entries = new[] { first, duplicate };
            Assert.Throws<InvalidOperationException>(() =>
                new FirstPartyBytecodeManifestValidator(fixture.Manifest));
        }

        [Test]
        public void OrdinalIdentityLookupIsCaseSensitive()
        {
            using var fixture = new ManifestFixture();
            var upper = LuauBytecodeArtifact.Create(
                fixture.Output,
                "unity-asset-guid:A",
                fixture.Artifact.ProvenanceId,
                Encoding.UTF8.GetBytes("A"));
            var lower = LuauBytecodeArtifact.Create(
                fixture.Output,
                "unity-asset-guid:a",
                fixture.Artifact.ProvenanceId,
                Encoding.UTF8.GetBytes("a"));
            fixture.Manifest.entries = new[]
            {
                ManifestFixture.CreateEntry(lower),
                ManifestFixture.CreateEntry(upper),
            }
                .OrderBy(entry => entry.sourceIdentity, StringComparer.Ordinal)
                .ToArray();

            var validator = new FirstPartyBytecodeManifestValidator(fixture.Manifest);

            Assert.That(validator.IsValid(upper, fixture.Bytecode), Is.True);
            Assert.That(validator.IsValid(lower, fixture.Bytecode), Is.True);
        }

        [TestCase("manifestSchema")]
        [TestCase("nullEntries")]
        [TestCase("nullEntry")]
        [TestCase("emptyProvenanceId")]
        [TestCase("invalidUnicodeProvenanceId")]
        [TestCase("emptySourceIdentity")]
        [TestCase("invalidUnicodeSourceIdentity")]
        [TestCase("zeroBytecodeLength")]
        [TestCase("malformedSourceHash")]
        [TestCase("uppercaseBytecodeHash")]
        [TestCase("optimizationBelowRange")]
        [TestCase("optimizationAboveRange")]
        [TestCase("debugBelowRange")]
        [TestCase("debugAboveRange")]
        [TestCase("typeInfoBelowRange")]
        [TestCase("typeInfoAboveRange")]
        [TestCase("coverageBelowRange")]
        [TestCase("coverageAboveRange")]
        [TestCase("emptyProvenanceData")]
        public void MalformedManifestsAreRejectedDuringInitialization(string malformedField)
        {
            using var fixture = new ManifestFixture();
            var entry = fixture.Manifest.entries[0];

            switch (malformedField)
            {
                case "manifestSchema":
                    fixture.Manifest.schemaVersion++;
                    break;
                case "nullEntries":
                    fixture.Manifest.entries = null;
                    break;
                case "nullEntry":
                    fixture.Manifest.entries[0] = null;
                    break;
                case "emptyProvenanceId":
                    fixture.Manifest.provenanceId = string.Empty;
                    break;
                case "invalidUnicodeProvenanceId":
                    fixture.Manifest.provenanceId = "\uD800";
                    break;
                case "emptySourceIdentity":
                    entry.sourceIdentity = string.Empty;
                    break;
                case "invalidUnicodeSourceIdentity":
                    entry.sourceIdentity = "\uD800";
                    break;
                case "zeroBytecodeLength":
                    entry.bytecodeLength = 0;
                    break;
                case "malformedSourceHash":
                    entry.sourceSha256 = "1234";
                    break;
                case "uppercaseBytecodeHash":
                    entry.bytecodeSha256 = "A" + entry.bytecodeSha256.Substring(1);
                    break;
                case "optimizationBelowRange":
                    entry.optimizationLevel = -1;
                    break;
                case "optimizationAboveRange":
                    entry.optimizationLevel = 3;
                    break;
                case "debugBelowRange":
                    entry.debugLevel = -1;
                    break;
                case "debugAboveRange":
                    entry.debugLevel = 3;
                    break;
                case "typeInfoBelowRange":
                    entry.typeInfoLevel = -1;
                    break;
                case "typeInfoAboveRange":
                    entry.typeInfoLevel = 2;
                    break;
                case "coverageBelowRange":
                    entry.coverageLevel = -1;
                    break;
                case "coverageAboveRange":
                    entry.coverageLevel = 3;
                    break;
                case "emptyProvenanceData":
                    entry.provenanceData = Array.Empty<byte>();
                    break;
                default:
                    Assert.Fail("Unknown malformed field: " + malformedField);
                    break;
            }

            Assert.Throws<InvalidOperationException>(() =>
                new FirstPartyBytecodeManifestValidator(fixture.Manifest));
        }

        [Test]
        public void NullManifestIsRejectedDuringInitialization()
        {
            Assert.Throws<InvalidOperationException>(() =>
                new FirstPartyBytecodeManifestValidator(null));
        }

        [Test]
        public void ValidatorSnapshotDoesNotRetainMutableManifestData()
        {
            using var fixture = new ManifestFixture();
            var entry = fixture.Manifest.entries[0];
            var validator = new FirstPartyBytecodeManifestValidator(fixture.Manifest);

            fixture.Manifest.provenanceId = "changed";
            entry.sourceIdentity = "changed";
            entry.provenanceData[0] ^= 0x01;
            fixture.Manifest.entries = Array.Empty<FirstPartyBytecodeManifestEntry>();

            Assert.That(validator.IsValid(fixture.Artifact, fixture.Bytecode), Is.True);
        }

        [Test]
        public void GeneratedOptionPreservesEveryCallerLimitAndSchedulerSetting()
        {
            using var fixture = new ManifestFixture();
            FirstPartyBytecodeManifestCache.Reload(fixture.Manifest);
            var scheduler = new InlineScheduler();
            var execution = new LuauExecutionOptions
            {
                WallClockLimit = TimeSpan.FromSeconds(2),
                InterruptCountLimit = 1234,
                MaxResultCount = 7,
                ContinuationScheduler = scheduler,
            };
            var supplied = new LuauStateOptions
            {
                MemoryLimitBytes = 32L * 1024 * 1024,
                MaxSourceBytes = 512 * 1024,
                MaxBytecodeBytes = 2 * 1024 * 1024,
                MaxDiagnosticBytes = 64 * 1024,
                MaxDecodedStringBytes = 1024 * 1024,
                MaxDecodedBytesPerOperation = 4L * 1024 * 1024,
                MaxCachedModuleCount = 31,
                MaxModuleDependencyDepth = 8,
                MaxManagedHandleCount = 17,
                BytecodePolicy = LuauBytecodePolicy.Reject,
                DefaultExecutionOptions = execution,
            };

            using var root = LuauUnity.CreateState(new LuauUnityOptions
            {
                UseFirstPartyBytecode = true,
                StateOptions = supplied,
                CaptureUnitySynchronizationContext = false,
                Log = _ => { },
            });

            Assert.That(root.Options, Is.Not.SameAs(supplied));
            Assert.That(root.Options.MemoryLimitBytes, Is.EqualTo(supplied.MemoryLimitBytes));
            Assert.That(root.Options.MaxSourceBytes, Is.EqualTo(supplied.MaxSourceBytes));
            Assert.That(root.Options.MaxBytecodeBytes, Is.EqualTo(supplied.MaxBytecodeBytes));
            Assert.That(root.Options.MaxDiagnosticBytes, Is.EqualTo(supplied.MaxDiagnosticBytes));
            Assert.That(root.Options.MaxDecodedStringBytes, Is.EqualTo(supplied.MaxDecodedStringBytes));
            Assert.That(
                root.Options.MaxDecodedBytesPerOperation,
                Is.EqualTo(supplied.MaxDecodedBytesPerOperation));
            Assert.That(root.Options.MaxCachedModuleCount, Is.EqualTo(supplied.MaxCachedModuleCount));
            Assert.That(
                root.Options.MaxModuleDependencyDepth,
                Is.EqualTo(supplied.MaxModuleDependencyDepth));
            Assert.That(root.Options.MaxManagedHandleCount, Is.EqualTo(supplied.MaxManagedHandleCount));
            Assert.That(root.Options.DefaultExecutionOptions, Is.EqualTo(execution));
            Assert.That(
                root.Options.DefaultExecutionOptions.ContinuationScheduler,
                Is.SameAs(scheduler));
            Assert.That(root.Options.BytecodePolicy, Is.EqualTo(LuauBytecodePolicy.RequireValidator));
            Assert.That(
                root.Options.BytecodeValidator,
                Is.TypeOf<FirstPartyBytecodeManifestValidator>());
            Assert.That(supplied.BytecodePolicy, Is.EqualTo(LuauBytecodePolicy.Reject));
            Assert.That(supplied.BytecodeValidator, Is.Null);
        }

        [Test]
        public void GeneratedOptionRejectsCustomValidatorConflictBeforeManifestLookup()
        {
            FirstPartyBytecodeManifestCache.Reload(null);
            var policyException = Assert.Throws<InvalidOperationException>(() =>
                LuauUnity.CreateState(new LuauUnityOptions
                {
                    UseFirstPartyBytecode = true,
                    CaptureUnitySynchronizationContext = false,
                    StateOptions = new LuauStateOptions
                    {
                        BytecodePolicy = LuauBytecodePolicy.RequireValidator,
                        BytecodeValidator = AcceptAllValidator.Instance,
                    },
                }));
            var validatorException = Assert.Throws<InvalidOperationException>(() =>
                LuauUnity.CreateState(new LuauUnityOptions
                {
                    UseFirstPartyBytecode = true,
                    CaptureUnitySynchronizationContext = false,
                    StateOptions = new LuauStateOptions
                    {
                        BytecodePolicy = LuauBytecodePolicy.Reject,
                        BytecodeValidator = AcceptAllValidator.Instance,
                    },
                }));

            Assert.That(policyException.Message, Does.Contain("cannot be combined"));
            Assert.That(validatorException.Message, Does.Contain("cannot be combined"));
        }

        [Test]
        public void GeneratedOptionRejectsMissingManifestBeforeCreatingAState()
        {
            FirstPartyBytecodeManifestCache.Reload(null);
            var exception = Assert.Throws<InvalidOperationException>(() =>
                LuauUnity.CreateState(new LuauUnityOptions
                {
                    UseFirstPartyBytecode = true,
                    CaptureUnitySynchronizationContext = false,
                    StateOptions = new LuauStateOptions
                    {
                        BytecodePolicy = LuauBytecodePolicy.Reject,
                    },
                }));

            Assert.That(exception.Message, Does.Contain("manifest").IgnoreCase);
            Assert.That(exception.Message, Does.Contain("absent or malformed").IgnoreCase);
        }

        [Test]
        public void SourceAssetStillUsesCompilationWhenGeneratedValidationIsEnabled()
        {
            using var fixture = new ManifestFixture();
            FirstPartyBytecodeManifestCache.Reload(fixture.Manifest);
            var asset = ScriptableObject.CreateInstance<LuauAsset>();
            try
            {
                asset.SetSource("return 42", Encoding.UTF8.GetBytes("return 42"));
                using var root = LuauUnity.CreateState(new LuauUnityOptions
                {
                    UseFirstPartyBytecode = true,
                    CaptureUnitySynchronizationContext = false,
                    Log = _ => { },
                });
                using var results = root.Execute(asset);

                Assert.That(asset.IsPrecompiled, Is.False);
                Assert.That(results, Has.Length.EqualTo(1));
                Assert.That(results[0].Read<int>(), Is.EqualTo(42));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        static int NextLevel(int current, int maximum)
        {
            return (current + 1) % (maximum + 1);
        }

        static string DifferentHash(string hash)
        {
            return (hash[0] == '0' ? "1" : "0") + hash.Substring(1);
        }

        sealed class InlineScheduler : ILuauContinuationScheduler
        {
            public bool CheckAccess() => true;
            public void Post(Action continuation) => continuation();
        }

        sealed class AcceptAllValidator : ILuauBytecodeValidator
        {
            internal static AcceptAllValidator Instance { get; } = new AcceptAllValidator();

            public bool IsValid(
                LuauBytecodeArtifact artifact,
                ReadOnlySpan<byte> bytecode) => true;
        }

        sealed class ManifestFixture : IDisposable
        {
            const string ProvenanceId = "tests:first-party/v1";
            const string SourceIdentity =
                "unity-asset-guid:0123456789abcdef0123456789abcdef";

            internal ManifestFixture()
            {
                Output = LuauCompiler.Compile(
                    Encoding.UTF8.GetBytes("return 2718"),
                    new LuauCompileOptions
                    {
                        OptimizationLevel = 2,
                        DebugLevel = 1,
                        TypeInfoLevel = 1,
                        CoverageLevel = 1,
                    });
                Artifact = LuauBytecodeArtifact.Create(
                    Output,
                    SourceIdentity,
                    ProvenanceId,
                    Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef"));
                Bytecode = Artifact.ToBytecodeArray();
                Manifest = ScriptableObject.CreateInstance<FirstPartyBytecodeManifest>();
                Manifest.schemaVersion = FirstPartyBytecodeManifest.CurrentSchemaVersion;
                Manifest.provenanceId = ProvenanceId;
                Manifest.entries = new[] { CreateEntry(Artifact) };
            }

            internal LuauCompilerOutput Output { get; }
            internal LuauBytecodeArtifact Artifact { get; }
            internal byte[] Bytecode { get; }
            internal FirstPartyBytecodeManifest Manifest { get; }

            internal static FirstPartyBytecodeManifestEntry CreateEntry(
                LuauBytecodeArtifact artifact)
            {
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

            public void Dispose()
            {
                UnityEngine.Object.DestroyImmediate(Manifest);
            }
        }
    }
}
