using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Luau.Unity
{
    /// <summary>
    /// Package-owned serialized approval list for first-party bytecode. This
    /// type is intentionally internal; the Editor pipeline owns the asset.
    /// </summary>
    internal sealed class FirstPartyBytecodeManifest : ScriptableObject
    {
        internal const int CurrentSchemaVersion = 1;
        internal const string ResourcePath = "Luau.Unity/FirstPartyBytecodeManifest";

        [SerializeField] internal int schemaVersion = CurrentSchemaVersion;
        [SerializeField] internal string provenanceId = string.Empty;
        [SerializeField] internal FirstPartyBytecodeManifestEntry[] entries =
            Array.Empty<FirstPartyBytecodeManifestEntry>();
    }

    [Serializable]
    internal sealed class FirstPartyBytecodeManifestEntry
    {
        [SerializeField] internal int artifactSchemaVersion;
        [SerializeField] internal int bytecodeLength;
        [SerializeField] internal string sourceIdentity = string.Empty;
        [SerializeField] internal string sourceSha256 = string.Empty;
        [SerializeField] internal string bytecodeSha256 = string.Empty;
        [SerializeField] internal int optimizationLevel;
        [SerializeField] internal int debugLevel;
        [SerializeField] internal int typeInfoLevel;
        [SerializeField] internal int coverageLevel;
        [SerializeField] internal ulong upstreamRevisionHash;
        [SerializeField] internal ulong hostBuildFingerprint;
        [SerializeField] internal byte[] provenanceData = Array.Empty<byte>();
    }

    /// <summary>
    /// Immutable, Unity-object-free validator copied from a generated manifest
    /// on the main thread.
    /// </summary>
    internal sealed class FirstPartyBytecodeManifestValidator : ILuauBytecodeValidator
    {
        readonly string provenanceId;
        readonly Dictionary<string, ValidatedEntry> entries;

        internal FirstPartyBytecodeManifestValidator(FirstPartyBytecodeManifest manifest)
        {
            if (manifest == null)
            {
                throw new InvalidOperationException(
                    $"The generated first-party bytecode manifest was not found at Resources path " +
                    $"'{FirstPartyBytecodeManifest.ResourcePath}'.");
            }

            if (manifest.schemaVersion != FirstPartyBytecodeManifest.CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"The generated first-party bytecode manifest uses unsupported schema " +
                    $"{manifest.schemaVersion}; expected {FirstPartyBytecodeManifest.CurrentSchemaVersion}.");
            }

            ValidateIdentity(manifest.provenanceId, "manifest provenance ID");
            provenanceId = manifest.provenanceId;

            var serializedEntries = manifest.entries
                ?? throw new InvalidOperationException(
                    "The generated first-party bytecode manifest has a null entry collection.");
            entries = new Dictionary<string, ValidatedEntry>(
                serializedEntries.Length,
                StringComparer.Ordinal);

            string previousIdentity = null;
            for (var index = 0; index < serializedEntries.Length; index++)
            {
                var serialized = serializedEntries[index]
                    ?? throw Malformed(index, "entry", "is null");
                ValidateIdentity(serialized.sourceIdentity, $"entry {index} source identity");

                if (previousIdentity != null)
                {
                    var ordering = StringComparer.Ordinal.Compare(
                        previousIdentity,
                        serialized.sourceIdentity);
                    if (ordering == 0)
                    {
                        throw Malformed(
                            index,
                            nameof(serialized.sourceIdentity),
                            $"duplicates identity '{serialized.sourceIdentity}'");
                    }
                    if (ordering > 0)
                    {
                        throw Malformed(
                            index,
                            nameof(serialized.sourceIdentity),
                            "is not in ordinal source-identity order");
                    }
                }

                ValidateEntry(index, serialized);
                var entry = new ValidatedEntry(serialized);
                entries.Add(entry.SourceIdentity, entry);
                previousIdentity = entry.SourceIdentity;
            }
        }

        public bool IsValid(
            LuauBytecodeArtifact artifact,
            ReadOnlySpan<byte> bytecode)
        {
            if (artifact == null ||
                !entries.TryGetValue(artifact.SourceIdentity, out var entry))
            {
                return false;
            }

            var compileOptions = artifact.CompileOptions;
            if (artifact.SchemaVersion != entry.ArtifactSchemaVersion ||
                artifact.BytecodeLength != entry.BytecodeLength ||
                bytecode.Length != entry.BytecodeLength ||
                !string.Equals(artifact.SourceIdentity, entry.SourceIdentity, StringComparison.Ordinal) ||
                !string.Equals(artifact.SourceSha256, entry.SourceSha256, StringComparison.Ordinal) ||
                !string.Equals(artifact.BytecodeSha256, entry.BytecodeSha256, StringComparison.Ordinal) ||
                compileOptions.OptimizationLevel != entry.OptimizationLevel ||
                compileOptions.DebugLevel != entry.DebugLevel ||
                compileOptions.TypeInfoLevel != entry.TypeInfoLevel ||
                compileOptions.CoverageLevel != entry.CoverageLevel ||
                artifact.UpstreamRevisionHash != entry.UpstreamRevisionHash ||
                artifact.HostBuildFingerprint != entry.HostBuildFingerprint ||
                !string.Equals(artifact.ProvenanceId, provenanceId, StringComparison.Ordinal) ||
                !SequenceEqual(artifact.GetProvenanceData(), entry.ProvenanceData))
            {
                return false;
            }

            Span<byte> actualHash = stackalloc byte[32];
            using (var sha256 = SHA256.Create())
            {
                // Compute over the exact span presented to IsValid rather than
                // trusting either hash claim in the artifact envelope.
                if (!sha256.TryComputeHash(
                    bytecode,
                    actualHash,
                    out var written) ||
                    written != actualHash.Length)
                {
                    return false;
                }
            }

            return FixedTimeEquals(actualHash, entry.BytecodeHash);
        }

        static void ValidateEntry(
            int index,
            FirstPartyBytecodeManifestEntry entry)
        {
            if (entry.artifactSchemaVersion != LuauBytecodeArtifact.CurrentSchemaVersion)
            {
                throw Malformed(
                    index,
                    nameof(entry.artifactSchemaVersion),
                    $"uses unsupported schema {entry.artifactSchemaVersion}");
            }
            if (entry.bytecodeLength <= 0)
            {
                throw Malformed(
                    index,
                    nameof(entry.bytecodeLength),
                    "must be positive");
            }
            if (!IsCanonicalSha256(entry.sourceSha256))
            {
                throw Malformed(
                    index,
                    nameof(entry.sourceSha256),
                    "must be a lowercase 64-character SHA-256 value");
            }
            if (!IsCanonicalSha256(entry.bytecodeSha256))
            {
                throw Malformed(
                    index,
                    nameof(entry.bytecodeSha256),
                    "must be a lowercase 64-character SHA-256 value");
            }

            ValidateLevel(index, nameof(entry.optimizationLevel), entry.optimizationLevel, 2);
            ValidateLevel(index, nameof(entry.debugLevel), entry.debugLevel, 2);
            ValidateLevel(index, nameof(entry.typeInfoLevel), entry.typeInfoLevel, 1);
            ValidateLevel(index, nameof(entry.coverageLevel), entry.coverageLevel, 2);

            if (entry.provenanceData == null || entry.provenanceData.Length == 0)
            {
                throw Malformed(
                    index,
                    nameof(entry.provenanceData),
                    "must contain canonical provenance bytes");
            }
        }

        static void ValidateIdentity(string value, string description)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"The generated first-party bytecode manifest {description} is empty.");
            }

            try
            {
                _ = new UTF8Encoding(false, true).GetByteCount(value);
            }
            catch (EncoderFallbackException exception)
            {
                throw new InvalidOperationException(
                    $"The generated first-party bytecode manifest {description} is not valid Unicode text.",
                    exception);
            }
        }

        static void ValidateLevel(
            int index,
            string field,
            int value,
            int maximum)
        {
            if (value < 0 || value > maximum)
            {
                throw Malformed(
                    index,
                    field,
                    $"must be between 0 and {maximum}");
            }
        }

        static bool IsCanonicalSha256(string value)
        {
            if (value == null || value.Length != 64)
                return false;

            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if ((character < '0' || character > '9') &&
                    (character < 'a' || character > 'f'))
                {
                    return false;
                }
            }

            return true;
        }

        static byte[] ParseSha256(string value)
        {
            var bytes = new byte[32];
            for (var index = 0; index < bytes.Length; index++)
            {
                bytes[index] = (byte)((HexValue(value[index * 2]) << 4) |
                    HexValue(value[index * 2 + 1]));
            }
            return bytes;
        }

        static int HexValue(char character)
        {
            return character <= '9' ? character - '0' : character - 'a' + 10;
        }

        static bool SequenceEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;

            var difference = 0;
            for (var index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }
            return difference == 0;
        }

        static bool FixedTimeEquals(
            ReadOnlySpan<byte> left,
            ReadOnlySpan<byte> right)
        {
            if (left.Length != right.Length)
                return false;

            var difference = 0;
            for (var index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }
            return difference == 0;
        }

        static InvalidOperationException Malformed(
            int index,
            string field,
            string reason)
        {
            return new InvalidOperationException(
                $"The generated first-party bytecode manifest entry {index} field " +
                $"'{field}' {reason}.");
        }

        sealed class ValidatedEntry
        {
            internal ValidatedEntry(FirstPartyBytecodeManifestEntry source)
            {
                ArtifactSchemaVersion = source.artifactSchemaVersion;
                BytecodeLength = source.bytecodeLength;
                SourceIdentity = source.sourceIdentity;
                SourceSha256 = source.sourceSha256;
                BytecodeSha256 = source.bytecodeSha256;
                BytecodeHash = ParseSha256(source.bytecodeSha256);
                OptimizationLevel = source.optimizationLevel;
                DebugLevel = source.debugLevel;
                TypeInfoLevel = source.typeInfoLevel;
                CoverageLevel = source.coverageLevel;
                UpstreamRevisionHash = source.upstreamRevisionHash;
                HostBuildFingerprint = source.hostBuildFingerprint;
                ProvenanceData = (byte[])source.provenanceData.Clone();
            }

            internal int ArtifactSchemaVersion { get; }
            internal int BytecodeLength { get; }
            internal string SourceIdentity { get; }
            internal string SourceSha256 { get; }
            internal string BytecodeSha256 { get; }
            internal byte[] BytecodeHash { get; }
            internal int OptimizationLevel { get; }
            internal int DebugLevel { get; }
            internal int TypeInfoLevel { get; }
            internal int CoverageLevel { get; }
            internal ulong UpstreamRevisionHash { get; }
            internal ulong HostBuildFingerprint { get; }
            internal byte[] ProvenanceData { get; }
        }
    }

    /// <summary>
    /// Main-thread manifest loader plus thread-safe cache for state creation.
    /// Only the immutable validator escapes the loading phase.
    /// </summary>
    internal static class FirstPartyBytecodeManifestCache
    {
        static readonly object Gate = new object();
        static FirstPartyBytecodeManifestValidator validator;
        static Exception initializationFailure;
        static bool initialized;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void Reset()
        {
            lock (Gate)
            {
                validator = null;
                initializationFailure = null;
                initialized = false;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        internal static void Reload()
        {
            FirstPartyBytecodeManifest manifest;
            try
            {
                manifest = Resources.Load<FirstPartyBytecodeManifest>(
                    FirstPartyBytecodeManifest.ResourcePath);
            }
            catch (Exception exception)
            {
                Store(null, exception);
                return;
            }

            Reload(manifest);
        }

        /// <summary>
        /// Rebuilds the pure-managed snapshot from a main-thread-owned asset.
        /// Used by the Editor generator after it refreshes or deletes the
        /// generated manifest.
        /// </summary>
        internal static void Reload(FirstPartyBytecodeManifest manifest)
        {
            FirstPartyBytecodeManifestValidator loadedValidator = null;
            Exception failure = null;
            try
            {
                loadedValidator = new FirstPartyBytecodeManifestValidator(manifest);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Store(loadedValidator, failure);
        }

        internal static FirstPartyBytecodeManifestValidator GetValidatorOrThrow()
        {
            lock (Gate)
            {
                if (!initialized)
                {
                    throw new InvalidOperationException(
                        "The first-party bytecode manifest has not been preloaded. " +
                        "Create states after Unity runtime initialization has completed, or disable " +
                        $"{nameof(LuauUnityOptions.UseFirstPartyBytecode)}.");
                }

                if (initializationFailure != null)
                {
                    throw new InvalidOperationException(
                        "First-party bytecode is enabled, but the generated manifest is absent or malformed. " +
                        "Refresh it in Project Settings > Luau.Unity, or disable " +
                        $"{nameof(LuauUnityOptions.UseFirstPartyBytecode)}. " +
                        initializationFailure.Message,
                        initializationFailure);
                }

                return validator;
            }
        }

        static void Store(
            FirstPartyBytecodeManifestValidator loadedValidator,
            Exception failure)
        {
            lock (Gate)
            {
                validator = loadedValidator;
                initializationFailure = failure;
                initialized = true;
            }
        }
    }
}
