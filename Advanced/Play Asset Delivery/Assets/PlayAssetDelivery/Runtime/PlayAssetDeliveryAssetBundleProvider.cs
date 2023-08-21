using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace AddressablesPlayAssetDelivery
{
    /// <summary>
    /// Ensures that the asset pack containing the AssetBundle is installed/downloaded before attemping to load the bundle.
    /// </summary>
    [DisplayName("Play Asset Delivery Provider")]
    public class PlayAssetDeliveryAssetBundleProvider : ResourceProviderBase
    {
        // parts of the code are copied from AssetBundleProvider, this is required to avoid calling Transform func when caching asset bundles
        // otherwise Transform method returns different results before asset pack is installed and after

        private static Dictionary<string, AssetBundleUnloadOperation> m_UnloadingBundles = new Dictionary<string, AssetBundleUnloadOperation>();
        /// <summary>
        /// Stores async operations that unload the requested AssetBundles.
        /// </summary>
        protected internal static Dictionary<string, AssetBundleUnloadOperation> UnloadingBundles
        {
            get { return m_UnloadingBundles; }
            internal set { m_UnloadingBundles = value; }
        }

        internal static int UnloadingAssetBundleCount => m_UnloadingBundles.Count;
        internal static int AssetBundleCount => AssetBundle.GetAllLoadedAssetBundles().Count() - UnloadingAssetBundleCount;
        internal static void WaitForAllUnloadingBundlesToComplete()
        {
            if (UnloadingAssetBundleCount > 0)
            {
                var bundles = m_UnloadingBundles.Values.ToArray();
                foreach (var b in bundles)
                    b.WaitForCompletion();
            }
        }

        ProvideHandle m_ProviderInterface;
        public override void Provide(ProvideHandle providerInterface)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            m_ProviderInterface = providerInterface;
            LoadFromAssetPack(providerInterface);
#else
            BaseProvide(providerInterface);
#endif
        }

        void BaseProvide(ProvideHandle providerInterface)
        {
            if (m_UnloadingBundles.TryGetValue(providerInterface.Location.InternalId, out var unloadOp))
            {
                if (unloadOp.isDone)
                    unloadOp = null;
            }
            new AssetBundleResource().Start(providerInterface, unloadOp);
        }

        /// <inheritdoc/>
        public override Type GetDefaultType(IResourceLocation location)
        {
            return typeof(IAssetBundleResource);
        }

        void LoadFromAssetPack(ProvideHandle providerInterface)
        {
            string bundleName = Path.GetFileNameWithoutExtension(providerInterface.Location.InternalId.Replace("\\", "/"));
            if (!PlayAssetDeliveryRuntimeData.Instance.BundleNameToAssetPack.ContainsKey(bundleName))
            {
                // Bundle is either assigned to the generated asset packs, or not assigned to any asset pack
                BaseProvide(providerInterface);
            }
            else
            {
                // Bundle is assigned to a custom fast-follow or on-demand asset pack
                string assetPackName = PlayAssetDeliveryRuntimeData.Instance.BundleNameToAssetPack[bundleName].AssetPackName;
                if (PlayAssetDeliveryRuntimeData.Instance.AssetPackNameToDownloadPath.ContainsKey(assetPackName))
                {
                    // Asset pack is already downloaded
                    BaseProvide(providerInterface);
                }
                else
                {
                    // Download the asset pack
                    DownloadRemoteAssetPack(assetPackName);
                }
            }
        }

        public override void Release(IResourceLocation location, object asset)
        {
            m_ProviderInterface = default;

            if (location == null)
                throw new ArgumentNullException("location");
            if (asset == null)
            {
                Debug.LogWarningFormat("Releasing null asset bundle from location {0}.  This is an indication that the bundle failed to load.", location);
                return;
            }

            var bundle = asset as AssetBundleResource;
            if (bundle != null)
            {
                if (bundle.Unload(out var unloadOp))
                {
                    m_UnloadingBundles.Add(location.InternalId, unloadOp);
                    unloadOp.completed += op => m_UnloadingBundles.Remove(location.InternalId);
                }
                return;
            }
        }

        void DownloadRemoteAssetPack(string assetPackName)
        {
            // Note that most methods in the AndroidAssetPacks class are either direct wrappers of java APIs in Google's PlayCore plugin,
            // or depend on values that the PlayCore API returns. If the PlayCore plugin is missing, calling these methods will throw an InvalidOperationException exception.
            try
            {
                AndroidAssetPacks.DownloadAssetPackAsync(new string[] { assetPackName }, CheckDownloadStatus);
            }
            catch (InvalidOperationException ioe)
            {
                Debug.LogError($"Cannot retrieve state for asset pack '{assetPackName}'. PlayCore Plugin is not installed: {ioe.Message}");
                m_ProviderInterface.Complete(this, false, new Exception("exception"));
            }
        }

        void CheckDownloadStatus(AndroidAssetPackInfo info)
        {
            string message = "";
            if (info.status == AndroidAssetPackStatus.Failed)
                message = $"Failed to retrieve the state of asset pack '{info.name}'.";
            else if (info.status == AndroidAssetPackStatus.Unknown)
                message = $"Asset pack '{info.name}' is unavailable for this application. This can occur if the app was not installed through Google Play.";
            else if (info.status == AndroidAssetPackStatus.Canceled)
                message = $"Cancelled asset pack download request '{info.name}'.";
            else if (info.status == AndroidAssetPackStatus.WaitingForWifi)
                AndroidAssetPacks.RequestToUseMobileDataAsync(OnRequestToUseMobileDataComplete);
            else if (info.status == AndroidAssetPackStatus.Completed)
            {
                string assetPackPath = AndroidAssetPacks.GetAssetPackPath(info.name);

                if (!string.IsNullOrEmpty(assetPackPath))
                {
                    // Asset pack was located on device. Proceed with loading the bundle.
                    PlayAssetDeliveryRuntimeData.Instance.AssetPackNameToDownloadPath.Add(info.name, assetPackPath);
                    BaseProvide(m_ProviderInterface);
                }
                else
                    message = $"Downloaded asset pack '{info.name}' but cannot locate it on device.";
            }

            if (!string.IsNullOrEmpty(message))
            {
                Debug.LogError(message);
                m_ProviderInterface.Complete(this, false, new Exception("exception"));
            }
        }

        void OnRequestToUseMobileDataComplete(AndroidAssetPackUseMobileDataRequestResult result)
        {
            if (!result.allowed)
            {
                Debug.LogError("Request to use mobile data was denied.");
                m_ProviderInterface.Complete(this, false, new Exception("exception"));
            }
        }
    }
}
