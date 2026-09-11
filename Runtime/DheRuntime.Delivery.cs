using System;
using System.Linq;
using System.Threading;

namespace HybridCLR
{
    public static partial class DheRuntime
    {
        private static DheDelivery preparedDelivery;
        static partial void ResetPreparedDelivery() => Volatile.Write(ref preparedDelivery, null);

        public static bool TryPrepareDelivery(IDheRuntimeAssetProvider deliveryProvider,
            IDheRuntimeAssetProvider embeddedBaseProvider, DheRuntimeIdentity buildIdentity,
            string manifestAssetPath, string expectedManifestSha256, out DheDelivery delivery, out string error)
        {
            DheDelivery result = null;
            bool accepted = Configure((out string message) =>
            {
                message = string.Empty;
                try
                {
                    // Capture caller-owned identity fields before retaining the plan.
                    buildIdentity = UnityEngine.JsonUtility.FromJson<DheRuntimeIdentity>(UnityEngine.JsonUtility.ToJson(buildIdentity));
                    var provider = new DheDelivery.DeliveryProvider(deliveryProvider, embeddedBaseProvider,
                        buildIdentity, manifestAssetPath, expectedManifestSha256);
                    string root = provider.Manifest.runtimeAssetRoot;
                    ResetCore();
                    if (!InitializeFromResourceUpdateCore(provider, buildIdentity,
                            root + "dhe-resource-update.json", out message, root)) return false;
                    string[] names = Artifacts.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
                    string[] paths = names.Select(name => Artifacts[name].CurrentAssetPath).ToArray();
                    if (paths.Any(string.IsNullOrEmpty)) throw new InvalidOperationException("Delivery Current paths are missing.");
                    result = new DheDelivery(provider, expectedManifestSha256, names, paths);
                    Volatile.Write(ref preparedDelivery, result);
                    return true;
                }
                catch (Exception exception) { message = exception.Message; return false; }
            }, out error);
            delivery = accepted ? result : null;
            return accepted;
        }

        internal static bool IsActiveDelivery(DheDelivery delivery) =>
            ReferenceEquals(Volatile.Read(ref preparedDelivery), delivery) && LoadState == DheLoadState.Ready;

        internal static bool LoadPreparedDelivery(DheDelivery delivery, out LoadImageErrorCode code, out string error)
        {
            byte[][] dlls;
            try
            {
                if (!ReferenceEquals(Volatile.Read(ref preparedDelivery), delivery))
                    throw new InvalidOperationException("DHE delivery was reset or superseded.");
                delivery.Provider.VerifyAssets();
                dlls = delivery.CurrentPaths.Select(delivery.Provider.LoadBytes).ToArray();
            }
            catch (Exception exception)
            {
                code = LoadImageErrorCode.DHE_MV_CURRENT_HASH_MISMATCH; error = exception.Message; return false;
            }
            return ExecuteLoad(delivery.AssemblyNames, dlls,
                (out LoadImageErrorCode result, out string message) =>
                {
                    if (!ReferenceEquals(Volatile.Read(ref preparedDelivery), delivery))
                    {
                        result = LoadImageErrorCode.DHE_MV_CURRENT_HASH_MISMATCH;
                        message = "DHE delivery was reset or superseded."; return false;
                    }
                    if (!LoadCurrentAssemblyImagesCore(delivery.AssemblyNames, dlls, out result, out message)) return false;
                    // Match the established workflow: Current registration first,
                    // then any planned supplemental AOT metadata, before exposing assets.
                    return LoadAotMetadataImages(delivery.Provider, HomologousImageMode.SuperSet, out result, out message);
                }, out code, out error);
        }
    }
}
