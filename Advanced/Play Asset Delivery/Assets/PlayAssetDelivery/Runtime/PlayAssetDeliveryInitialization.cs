using System;
using System.IO;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Android;
using UnityEngine.Networking;
using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.ResourceManagement.Util;

namespace AddressablesPlayAssetDelivery
{
    /// <summary>
    /// IInitializableObject that configures Addressables for loading content from asset packs.
    /// </summary>
    [Serializable]
    public class PlayAssetDeliveryInitialization : IInitializableObject
    {
        public bool Initialize(string id, string data)
        {
            return true;
        }

        /// <summary>
        /// Determines whether warnings should be logged during initialization.
        /// </summary>
        /// <param name="data">The JSON serialized <see cref="PlayAssetDeliveryInitializationData"/> object</param>
        /// <returns>True to log warnings, otherwise returns false. Default value is true.</returns>
        public bool LogWarnings(string data)
        {
            var initializeData = JsonUtility.FromJson<PlayAssetDeliveryInitializationData>(data);
            if (initializeData != null)
            {
                return initializeData.LogWarnings;
            }
            return true;
        }

        /// <inheritdoc/>
        public virtual AsyncOperationHandle<bool> InitializeAsync(ResourceManager rm, string id, string data)
        {
            var op = new PlayAssetDeliveryInitializeOperation();
            return op.Start(rm, LogWarnings(data));
        }
    }

    /// <summary>
    /// Configures Addressables for loading content from asset packs
    /// </summary>
    public class PlayAssetDeliveryInitializeOperation : AsyncOperationBase<bool>
    {
        ResourceManager m_RM;
        bool m_LogWarnings = false;

        bool m_IsDone = false; // AsyncOperationBase.IsDone is internal
        bool m_HasExecuted = false;  // AsyncOperationBase.HasExecuted is internal

        public AsyncOperationHandle<bool> Start(ResourceManager rm, bool logWarnings)
        {
            m_RM = rm;
            m_LogWarnings = logWarnings;
            return m_RM.StartOperation(this, default);
        }

        protected override bool InvokeWaitForCompletion()
        {
            if (!m_HasExecuted)
            {
                Execute();
            }
            return m_IsDone;
        }

        void CompleteOverride(string warningMsg)
        {
            if (m_LogWarnings && warningMsg != null)
            {
                Debug.LogWarning($"{warningMsg} Default internal id locations will be used instead.");
            }
            Complete(true, true, "");
            m_IsDone = true;
        }

        protected override void Execute()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            DownloadCustomAssetPacksData();
#elif UNITY_ANDROID && UNITY_EDITOR
            LoadFromEditorData();
#else
            CompleteOverride(null);
#endif
            m_HasExecuted = true;
        }

        void LoadFromEditorData()
        {
            if (File.Exists(CustomAssetPackUtility.CustomAssetPacksDataEditorPath))
            {
                InitializeBundleToAssetPackMap(File.ReadAllText(CustomAssetPackUtility.CustomAssetPacksDataEditorPath));
            }
            else if (File.Exists(CustomAssetPackUtility.CustomAssetPacksDataRuntimePath))
            {
                InitializeBundleToAssetPackMap(File.ReadAllText(CustomAssetPackUtility.CustomAssetPacksDataRuntimePath));
            }

            Addressables.ResourceManager.InternalIdTransformFunc = EditorTransformFunc;
            CompleteOverride(null);
        }

        void DownloadCustomAssetPacksData()
        {
            // CustomAssetPacksDataRuntimePath file is always in install-tim AddressablesAssetPack (if split binary is on),
            // or in the main APK (if split binary is off). So there is no need to check for core asset packs status before accessing it.
            var www = UnityWebRequest.Get(CustomAssetPackUtility.CustomAssetPacksDataRuntimePath);
            www.SendWebRequest().completed += (op) =>
            {
                var www = (op as UnityWebRequestAsyncOperation).webRequest;
                if (www.result != UnityWebRequest.Result.Success)
                {
                    CompleteOverride($"Could not load '{CustomAssetPackUtility.kCustomAssetPackDataFilename}' : {www.error}.");
                }
                else
                {
                    InitializeBundleToAssetPackMap(www.downloadHandler.text);
                    Addressables.ResourceManager.InternalIdTransformFunc = AppBundleTransformFunc;
                    CompleteOverride(null);
                }
            };
        }

        void InitializeBundleToAssetPackMap(string contents)
        {
            CustomAssetPackData customPackData =  JsonUtility.FromJson<CustomAssetPackData>(contents);
            foreach (CustomAssetPackDataEntry entry in customPackData.Entries)
            {
                foreach (var bundle in entry.AssetBundles)
                {
                    PlayAssetDeliveryRuntimeData.Instance.BundleNameToAssetPack.Add(bundle, entry);
                }
            }
        }

        string AppBundleTransformFunc(IResourceLocation location)
        {
            if (location.ResourceType == typeof(IAssetBundleResource))
            {
                var bundleName = Path.GetFileNameWithoutExtension(location.InternalId.Replace("\\", "/"));
                if (PlayAssetDeliveryRuntimeData.Instance.BundleNameToAssetPack.ContainsKey(bundleName))
                {
                    var assetPackName = PlayAssetDeliveryRuntimeData.Instance.BundleNameToAssetPack[bundleName].AssetPackName;
                    if (PlayAssetDeliveryRuntimeData.Instance.AssetPackNameToDownloadPath.ContainsKey(assetPackName))
                    {
                        // Load bundle that was assigned to a custom fast-follow or on-demand asset pack.
                        // PlayAssetDeliveryBundleProvider.Provider previously saved the asset pack path.
                        var ret = Path.Combine(PlayAssetDeliveryRuntimeData.Instance.AssetPackNameToDownloadPath[assetPackName], Addressables.StreamingAssetsSubFolder, "Android", Path.GetFileName(location.InternalId.Replace("\\", "/")));
                        return ret;
                    }
                }
            }
            // Load resource from the default location. The generated asset packs contain streaming assets.
            return location.InternalId;
        }

        string EditorTransformFunc(IResourceLocation location)
        {
            if (location.ResourceType == typeof(IAssetBundleResource))
            {
                var bundleName = Path.GetFileNameWithoutExtension(location.InternalId.Replace("\\", "/"));
                if (PlayAssetDeliveryRuntimeData.Instance.BundleNameToAssetPack.ContainsKey(bundleName))
                {
                    var assetPackName = PlayAssetDeliveryRuntimeData.Instance.BundleNameToAssetPack[bundleName].AssetPackName;
                    var androidPackFolder = $"{CustomAssetPackUtility.PackContentRootDirectory}/{assetPackName}.androidpack";
                    var bundlePath = Path.Combine(androidPackFolder, CustomAssetPackUtility.CustomAssetPacksAssetsPath, "Android", Path.GetFileName(location.InternalId.Replace("\\", "/")));
                    if (File.Exists(bundlePath))
                    {
                        // Load bundle from the 'Assets/PlayAssetDelivery/Build/CustomAssetPackContent' folder.
                        // The PlayAssetDeliveryBuildProcessor moves bundles assigned to "fast-follow" or "on-demand" asset packs to this location
                        // as result of a previous App Bundle build.
                        return bundlePath;
                    }
                }
            }
            // Load resource from the default location.
            return location.InternalId;
        }
    }

    /// <summary>
    /// Contains settings for <see cref="PlayAssetDeliveryInitialization"/>.
    /// </summary>
    [Serializable]
    public class PlayAssetDeliveryInitializationData
    {
        [SerializeField]
        bool m_LogWarnings = true;
        /// <summary>
        /// Enable recompression of asset bundles into LZ4 format as they are saved to the cache.  This sets the Caching.compressionEnabled value.
        /// </summary>
        public bool LogWarnings { get { return m_LogWarnings; } set { m_LogWarnings = value; } }
    }
}
