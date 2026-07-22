using System;
using System.Threading;
using System.Threading.Tasks;

namespace Luau.Unity
{
    public static partial class LuauStateExtensions
    {
        /// <summary>
        /// Creates a sandboxed script instance from a Unity asset through the
        /// package-owned bounded compilation lane.
        /// </summary>
        /// <param name="root">The caller-owned root that will own the instance thread.</param>
        /// <param name="asset">The source or verified-bytecode asset to load.</param>
        /// <param name="configureThread">
        /// An optional capability configuration callback run on the new sandboxed
        /// thread before the asset executes.
        /// </param>
        /// <param name="cancellationToken">Cancels compilation or initialization.</param>
        public static ValueTask<LuauScriptInstance> CreateScriptInstanceAsync(
            this LuauState root,
            LuauAsset asset,
            Action<LuauState> configureThread = null,
            CancellationToken cancellationToken = default)
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }
            if (asset == null)
            {
                throw new ArgumentNullException(nameof(asset));
            }

            var assetName = asset.name;
            return LuauScriptInstance.CreateAsync(
                root,
                assetName,
                (thread, token) => thread.ExecuteAsync(asset, token),
                configureThread,
                cancellationToken);
        }
    }
}
