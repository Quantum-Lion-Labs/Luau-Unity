using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Luau.Unity.Editor
{
    [ScriptedImporter(3, "luau")]
    public sealed class LuauImporter : ScriptedImporter
    {
        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        internal static System.Action<string> ImportErrorObserverForTests { get; set; }

        [SerializeField]
        bool precompile;

        // The imported LuauAsset can contain source even when this setting is
        // enabled (for example, after a compiler diagnostic). Build validation
        // must inspect both values so a failed opt-in never silently ships as
        // source in a first-party build.
        internal bool PrecompileRequested => precompile;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            LuauCompilerIdentityDependency.DependsOn(ctx);
            var asset = ScriptableObject.CreateInstance<LuauAsset>();

            byte[] source;
            string text;
            try
            {
                source = ReadSourceBytes(
                    ctx.assetPath,
                    LuauAssetImportSettings.MaxImportedSourceBytes);
                text = DecodeSource(source);
            }
            catch (IOException exception)
            {
                LogImportError(ctx, exception.Message);
                asset.SetSource(string.Empty, System.Array.Empty<byte>());
                AddAsset(ctx, asset);
                return;
            }
            catch (System.UnauthorizedAccessException exception)
            {
                LogImportError(ctx, exception.Message);
                asset.SetSource(string.Empty, System.Array.Empty<byte>());
                AddAsset(ctx, asset);
                return;
            }
            catch (DecoderFallbackException exception)
            {
                LogImportError(
                    ctx,
                    $"Luau source '{ctx.assetPath}' is not valid UTF-8: {exception.Message}");
                asset.SetSource(string.Empty, System.Array.Empty<byte>());
                AddAsset(ctx, asset);
                return;
            }

            LuauCompileResult compileResult;
            try
            {
                // Import is synchronous, but compilation still enters the one
                // package-owned worker lane. That lane admits source and bounds
                // native output before publishing a compiler capability.
                compileResult = LuauUnity
                    .CompileAssetSourceAsync(
                        source,
                        LuauCompileOptions.Default,
                        System.Threading.CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }
            catch (System.Exception exception)
            {
                LogImportError(ctx, exception.Message);
                asset.SetSource(text, source);
                AddAsset(ctx, asset);
                return;
            }

            if (compileResult.Kind != LuauCompileResultKind.Success)
            {
                LogImportError(ctx, GetCompilationFailureMessage(compileResult));
                asset.SetSource(text, source);
                AddAsset(ctx, asset);
                return;
            }

            var compilerOutput = compileResult.Output;
            LuauCompilerIdentityDependency.ScheduleRegistration(compilerOutput);
            var allowPrecompile =
                LuauAssetImportSettings.ImportPolicy ==
                    LuauAssetImportPolicy.AllowFirstPartyPrecompile &&
                precompile;

            if (allowPrecompile)
            {
                ImportPrecompiled(ctx, asset, text, source, compilerOutput);
            }
            else
            {
                asset.SetSource(text, source);
            }

            AddAsset(ctx, asset);
        }

        internal static string GetCompilationFailureMessage(LuauCompileResult result)
        {
            switch (result.Kind)
            {
                case LuauCompileResultKind.Diagnostic:
                    return result.CompilationDiagnostic.Message;
                case LuauCompileResultKind.Canceled:
                    return "Luau asset compilation was canceled before output was published.";
                case LuauCompileResultKind.InfrastructureFailure:
                    return result.InfrastructureException.Message;
                default:
                    return "Luau asset compilation returned an unknown fail-closed outcome.";
            }
        }

        static void LogImportError(AssetImportContext ctx, string message)
        {
            var observer = ImportErrorObserverForTests;
            if (observer != null)
            {
                observer(message);
                return;
            }

            ctx.LogImportError(message);
        }

        static void AddAsset(AssetImportContext ctx, LuauAsset asset)
        {
            ctx.AddObjectToAsset("Main", asset);
            ctx.SetMainObject(asset);
        }

        static void ImportPrecompiled(
            AssetImportContext ctx,
            LuauAsset asset,
            string sourceText,
            byte[] sourceBytes,
            LuauCompilerOutput compilerOutput)
        {
            var provenanceId = LuauAssetImportSettings.FirstPartyProvenanceId;
            var assetGuid = AssetDatabase.AssetPathToGUID(ctx.assetPath);
            if (string.IsNullOrWhiteSpace(provenanceId) || string.IsNullOrEmpty(assetGuid))
            {
                LogImportError(
                    ctx,
                    "First-party precompile requires a project provenance ID and stable asset GUID. " +
                    "The importer stored source instead.");
                asset.SetSource(sourceText, sourceBytes);
                return;
            }

            var artifact = LuauBytecodeArtifact.Create(
                compilerOutput,
                "unity-asset-guid:" + assetGuid,
                provenanceId,
                Encoding.UTF8.GetBytes(assetGuid));
            asset.SetVerifiedBytecode(sourceText, artifact);
        }

        internal static byte[] ReadSourceBytes(string path, int maxSourceBytes)
        {
            if (string.IsNullOrEmpty(path))
                throw new System.ArgumentException("A source path is required.", nameof(path));
            if (maxSourceBytes <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(maxSourceBytes));

            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: false))
            {
                var declaredLength = stream.Length;
                if (declaredLength > maxSourceBytes)
                {
                    throw new IOException(
                        $"Luau source '{path}' is {declaredLength} bytes; the project importer limit is " +
                        $"{maxSourceBytes} bytes.");
                }

                var source = new byte[(int)declaredLength];
                var offset = 0;
                while (offset < source.Length)
                {
                    var read = stream.Read(source, offset, source.Length - offset);
                    if (read == 0)
                    {
                        throw new IOException(
                            $"Luau source '{path}' changed while it was being imported.");
                    }
                    offset += read;
                }

                if (stream.ReadByte() != -1)
                {
                    throw new IOException(
                        $"Luau source '{path}' changed while it was being imported.");
                }

                return source;
            }
        }

        internal static string DecodeSource(byte[] source)
        {
            if (source == null)
                throw new System.ArgumentNullException(nameof(source));
            return StrictUtf8.GetString(source);
        }
    }
}
