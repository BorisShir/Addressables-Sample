using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;
#if UNITY_EDITOR && UNITY_ANDROID
using Unity.Android.Types;
#endif

namespace AddressablesPlayAssetDelivery
{
    /// <summary>
    /// Serializable representation of 'Unity.Android.Types.AndroidAssetPackDeliveryType'.
    /// </summary>
    public enum DeliveryType
    {
        /// <summary>
        /// No delivery type specified.
        /// </summary>
        None = 0,

        /// <summary>
        /// Content is downloaded when the app is installed.
        /// </summary>
        InstallTime = 1,

        /// <summary>
        /// Content is downloaded automatically as soon as the the app is installed.
        /// </summary>
        FastFollow = 2,

        /// <summary>
        /// Content is downloaded while the app is running.
        /// </summary>
        OnDemand = 3
    }

    public class CustomAssetPackUtility
    {
        public const string kBuildFolderName = "Build";
        public const string kPackContentFolderName = "CustomAssetPackContent";

        public const string kBuildProcessorDataFilename = "BuildProcessorData.json";
        public const string kCustomAssetPackDataFilename = "CustomAssetPacksData.json";

        public const string kAddressablesAssetPackName = "AddressablesAssetPack";

        public static string RootDirectory => $"Assets/PlayAssetDelivery";

        public static string BuildRootDirectory => $"{RootDirectory}/{kBuildFolderName}";

        public static string PackContentRootDirectory => $"{BuildRootDirectory}/{kPackContentFolderName}";

        public static string BuildProcessorDataPath => Path.Combine(BuildRootDirectory, Addressables.StreamingAssetsSubFolder, kBuildProcessorDataFilename);

        public static string CustomAssetPacksDataEditorPath => Path.Combine(BuildRootDirectory, Addressables.StreamingAssetsSubFolder, kCustomAssetPackDataFilename);

        public static string CustomAssetPacksDataRuntimePath => Path.Combine(Application.streamingAssetsPath, Addressables.StreamingAssetsSubFolder, kCustomAssetPackDataFilename);

        public static string CustomAssetPacksAssetsPath => $"src/main/assets/{Addressables.StreamingAssetsSubFolder}";

#if UNITY_EDITOR && UNITY_ANDROID
        static readonly Dictionary<DeliveryType, AndroidAssetPackDeliveryType> k_DeliveryTypeToGradleString = new Dictionary<DeliveryType, AndroidAssetPackDeliveryType>()
        {
            { DeliveryType.InstallTime, AndroidAssetPackDeliveryType.InstallTime },
            { DeliveryType.FastFollow, AndroidAssetPackDeliveryType.FastFollow },
            { DeliveryType.OnDemand, AndroidAssetPackDeliveryType.OnDemand },
        };

        public static string DeliveryTypeToGradleString(DeliveryType deliveryType)
        {
            return k_DeliveryTypeToGradleString[deliveryType].Name;
        }
#endif
    }
}
