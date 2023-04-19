using System;
using System.ComponentModel;
using System.IO;
using System.Collections.Generic;
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
    public class PlayAssetDeliveryAssetBundleProvider : AssetBundleProvider
    {
        Dictionary<string, ProvideHandle> m_ProviderInterfaces = new Dictionary<string, ProvideHandle>();
        public override void Provide(ProvideHandle providerInterface)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            LoadFromAssetPack(providerInterface);
#else
            base.Provide(providerInterface);
#endif
        }

        void LoadFromAssetPack(ProvideHandle providerInterface)
        {
            string bundleName = Path.GetFileNameWithoutExtension(providerInterface.Location.InternalId.Replace("\\", "/"));
            if (!PlayAssetDeliveryRuntimeData.Instance.BundleNameToAssetPack.ContainsKey(bundleName))
            {
                // Bundle is either assigned to the generated asset packs, or not assigned to any asset pack
                base.Provide(providerInterface);
            }
            else
            {
                var assetPackName = PlayAssetDeliveryRuntimeData.Instance.BundleNameToAssetPack[bundleName].AssetPackName;
                // Bundle is assigned to the already downloaded asset pack
                if (PlayAssetDeliveryRuntimeData.Instance.AssetPackNameToDownloadPath.ContainsKey(assetPackName))
                {
                    // Asset pack is already downloaded
                    base.Provide(providerInterface);
                }
                // Bundle is assigned to install-time AddressablesAssetPack
                else if (assetPackName == CustomAssetPackUtility.kAddressablesAssetPackName)
                {
                    PlayAssetDeliveryRuntimeData.Instance.AssetPackNameToDownloadPath.Add(CustomAssetPackUtility.kAddressablesAssetPackName, Application.streamingAssetsPath);
                    base.Provide(providerInterface);
                }
                else
                {
                    // Download the asset pack
                    DownloadRemoteAssetPack(providerInterface, assetPackName);
                }
            }
        }

        public override void Release(IResourceLocation location, object asset)
        {
            base.Release(location, asset);
            m_ProviderInterfaces.Clear();
        }

        void DownloadRemoteAssetPack(ProvideHandle providerInterface, string assetPackName)
        {
            // Note that most methods in the AndroidAssetPacks class are either direct wrappers of java APIs in Google's PlayCore plugin,
            // or depend on values that the PlayCore API returns. If the PlayCore plugin is missing, calling these methods will throw an InvalidOperationException exception.
            try
            {
                m_ProviderInterfaces[assetPackName] = providerInterface;
                AndroidAssetPacks.DownloadAssetPackAsync(new string[] { assetPackName }, CheckDownloadStatus);
            }
            catch (InvalidOperationException ioe)
            {
                Debug.LogError($"Cannot retrieve state for asset pack '{assetPackName}'. PlayCore Plugin is not installed: {ioe.Message}");
                m_ProviderInterfaces.Remove(assetPackName);
                providerInterface.Complete(this, false, new Exception("exception"));
            }
        }

        void CheckDownloadStatus(AndroidAssetPackInfo info)
        {
            var providerInterface = m_ProviderInterfaces[info.name];
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
                    m_ProviderInterfaces.Remove(info.name);
                    base.Provide(providerInterface);
                }
                else
                    message = $"Downloaded asset pack '{info.name}' but cannot locate it on device.";
            }

            if (!string.IsNullOrEmpty(message))
            {
                m_ProviderInterfaces.Remove(info.name);
                Debug.LogError(message);
                // probably need less general exception
                providerInterface.Complete(this, false, new Exception("exception"));
            }
        }

        void OnRequestToUseMobileDataComplete(AndroidAssetPackUseMobileDataRequestResult result)
        {
            if (!result.allowed)
            {
                Debug.LogError("Request to use mobile data was denied.");
                foreach (var p in m_ProviderInterfaces)
                {
                    // probably need less general exception
                    p.Value.Complete(this, false, new Exception("exception"));
                }
                m_ProviderInterfaces.Clear();
            }
        }
    }
}
