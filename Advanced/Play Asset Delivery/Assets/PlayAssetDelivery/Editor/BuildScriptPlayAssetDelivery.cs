using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.Initialization;

namespace AddressablesPlayAssetDelivery.Editor
{
    /// <summary>
    /// In addition to the Default Build Script behavior (building AssetBundles), this script assigns Android bundled content to "install-time" or "on-demand" custom asset packs
    /// specified in <see cref="CustomAssetPackSettings"/>.
    ///
    /// We will create the config files necessary for creating an asset pack (see https://docs.unity3d.com/Manual/play-asset-delivery.html#custom-asset-packs).
    /// The files are:
    /// * An {asset pack name}.androidpack folder located in 'Assets/PlayAssetDelivery/Build/CustomAssetPackContent'
    /// * A 'build.gradle' file for each .androidpack folder. If this file is missing, Unity will assume that the asset pack uses "on-demand" delivery.
    ///
    /// Additionally we generate some files to store build and runtime data that are located in in 'Assets/PlayAssetDelivery/Build':
    /// * Create a 'BuildProcessorData.json' file to store the build paths and .androidpack paths for bundles that should be assigned to custom asset packs.
    /// At build time this will be used by the <see cref="PlayAssetDeliveryBuildProcessor"/> to relocate bundles to their corresponding .androidpack paths.
    /// * Create a 'CustomAssetPacksData.json' file to store custom asset pack information to be used at runtime. See <see cref="PlayAssetDeliveryInitialization"/>.
    ///
    /// We assign any content marked for "install-time" delivery to the generated asset packs. In most cases the asset pack containing streaming assets will use "install-time" delivery,
    /// but in large projects it may use "fast-follow" delivery instead. For more information see https://docs.unity3d.com/Manual/play-asset-delivery.html#generated-asset-packs.
    ///
    /// Because <see cref="AddressablesPlayerBuildProcessor"/> moves all Addressables.BuildPath content to the streaming assets path, any content in that directory
    /// will be included in the generated asset packs even if they are not marked for "install-time" delivery.
    /// </summary>
    [CreateAssetMenu(fileName = "BuildScriptPlayAssetDelivery.asset", menuName = "Addressables/Custom Build/Play Asset Delivery")]
    public class BuildScriptPlayAssetDelivery : BuildScriptPackedMode
    {
        /// <inheritdoc/>
        public override string Name
        {
            get { return "Play Asset Delivery"; }
        }

        internal static string TcfPostfix(MobileTextureSubtarget subtarget)
        {
            return subtarget switch
            {
                MobileTextureSubtarget.ETC => "#tcf_etc1",
                MobileTextureSubtarget.ETC2 => "#tcf_etc2",
                MobileTextureSubtarget.ASTC => "#tcf_astc",
                MobileTextureSubtarget.PVRTC => "#tcf_pvrtc",
                MobileTextureSubtarget.DXT => "#tcf_dxt1"
            };
        }

        internal static MobileTextureSubtarget ConvertToMobileTextureSubtarget(TextureCompressionFormat compression)
        {
            return compression switch
            {
                TextureCompressionFormat.ETC => MobileTextureSubtarget.ETC,
                TextureCompressionFormat.ETC2 => MobileTextureSubtarget.ETC2,
                TextureCompressionFormat.ASTC => MobileTextureSubtarget.ASTC,
                TextureCompressionFormat.PVRTC => MobileTextureSubtarget.PVRTC,
                TextureCompressionFormat.DXTC => MobileTextureSubtarget.DXT,
                TextureCompressionFormat.DXTC_RGTC => MobileTextureSubtarget.DXT
            };
        }

        protected override TResult BuildDataImplementation<TResult>(AddressablesDataBuilderInput builderInput)
        {
            var textureCompressions = PlayerSettings.Android.textureCompressionFormats;
            CreateBuildOutputFolders(textureCompressions);
            TResult result = default(TResult);
            if (textureCompressions.Length > 1 && builderInput.Target == BuildTarget.Android)
            {
                var storedSubtarget = EditorUserBuildSettings.androidBuildSubtarget;
                for (int i = textureCompressions.Length - 1; i >= 0; --i)
                {
                    EditorUserBuildSettings.androidBuildSubtarget = ConvertToMobileTextureSubtarget(textureCompressions[i]);
                    AssetDatabase.Refresh();
                    result = base.BuildDataImplementation<TResult>(builderInput);
                }
            }
            else
            {
                result = base.BuildDataImplementation<TResult>(builderInput);                
            }
            return result;
        }

        protected override TResult DoBuild<TResult>(AddressablesDataBuilderInput builderInput, AddressableAssetsBuildContext aaContext)
        {
            UnityEngine.Debug.Log("Do Build");
            // Build AssetBundles
            TResult result = base.DoBuild<TResult>(builderInput, aaContext);

            // Don't prepare content for asset packs if the build target isn't set to Android
            if (builderInput.Target != BuildTarget.Android)
            {
                Addressables.LogWarning("Build target is not set to Android. No custom asset pack config files will be created.");
                return result;
            }

            var resetAssetPackSchemaData = !CustomAssetPackSettings.SettingsExists;
            var customAssetPackSettings = CustomAssetPackSettings.GetSettings(true);

            CreateCustomAssetPacks(aaContext.Settings, customAssetPackSettings, resetAssetPackSchemaData);
            return result;
        }

        /// <inheritdoc/>
        public override void ClearCachedData()
        {
            base.ClearCachedData();
            try
            {
                ClearJsonFiles();
                ClearBundlesInAssetsFolder();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        void ClearBundlesInAssetsFolder()
        {
            if (AssetDatabase.IsValidFolder(CustomAssetPackUtility.PackContentRootDirectory))
            {
                // Delete all bundle files in 'Assets/PlayAssetDelivery/Build/CustomAssetPackContent'
                List<string> bundleFiles = Directory.EnumerateFiles(CustomAssetPackUtility.PackContentRootDirectory, "*.bundle", SearchOption.AllDirectories).ToList();
                foreach (string file in bundleFiles)
                    AssetDatabase.DeleteAsset(file);
            }
        }

        void ClearJsonFiles()
        {
            // Delete "CustomAssetPacksData.json"
            if (File.Exists(CustomAssetPackUtility.CustomAssetPacksDataEditorPath))
                AssetDatabase.DeleteAsset(CustomAssetPackUtility.CustomAssetPacksDataEditorPath);
            if (File.Exists(CustomAssetPackUtility.CustomAssetPacksDataRuntimePath))
            {
                File.Delete(CustomAssetPackUtility.CustomAssetPacksDataRuntimePath);
                File.Delete(CustomAssetPackUtility.CustomAssetPacksDataRuntimePath + ".meta");
                CustomAssetPackUtility.DeleteDirectory(Application.streamingAssetsPath, true);
            }

            // Delete "BuildProcessorData.json"
            if (File.Exists(CustomAssetPackUtility.BuildProcessorDataPath))
                AssetDatabase.DeleteAsset(CustomAssetPackUtility.BuildProcessorDataPath);
        }

        void CreateCustomAssetPacks(AddressableAssetSettings settings, CustomAssetPackSettings customAssetPackSettings, bool resetAssetPackSchemaData)
        {
            List<CustomAssetPackEditorInfo> customAssetPacks = customAssetPackSettings.CustomAssetPacks;
            var assetPackToDataEntry = new Dictionary<string, CustomAssetPackDataEntry>();
            var bundleIdToEditorDataEntry = new Dictionary<string, BuildProcessorDataEntry>();

            foreach (AddressableAssetGroup group in settings.groups)
            {
                if (HasRequiredSchemas(settings, group))
                {
                    var assetPackSchema = group.GetSchema<PlayAssetDeliverySchema>();
                    // Reset schema data to match Custom Asset Pack Settings. This can occur when the CustomAssetPackSettings was deleted but the schema properties still use the old settings data.
                    if (resetAssetPackSchemaData || assetPackSchema.AssetPackIndex >= customAssetPacks.Count)
                        assetPackSchema.ResetAssetPackIndex();

                    CustomAssetPackEditorInfo assetPack = customAssetPacks[assetPackSchema.AssetPackIndex];
                    if (IsAssignedToCustomAssetPack(settings, group, assetPackSchema, assetPack))
                        CreateConfigFiles(group, assetPack.AssetPackName, assetPack.DeliveryType, assetPackToDataEntry, bundleIdToEditorDataEntry);
                }
            }

            // Create the bundleIdToEditorDataEntry. It contains information for relocating custom asset pack bundles when building a player.
            SerializeBuildProcessorData(bundleIdToEditorDataEntry.Values.ToList());

            // Create the CustomAssetPacksData.json file. It contains all custom asset pack information that can be used at runtime.
            SerializeCustomAssetPacksData(assetPackToDataEntry.Values.ToList());
        }

        void CreateBuildOutputFolders(TextureCompressionFormat[] textureCompressions)
        {
            // Create the 'Assets/PlayAssetDelivery/Build' directory
            if (!AssetDatabase.IsValidFolder(CustomAssetPackUtility.BuildRootDirectory))
                AssetDatabase.CreateFolder(CustomAssetPackUtility.RootDirectory, CustomAssetPackUtility.kBuildFolderName);
            else
                ClearJsonFiles();

            // Create the 'Assets/PlayAssetDelivery/Build/CustomAssetPackContent' directory
            if (!AssetDatabase.IsValidFolder(CustomAssetPackUtility.PackContentRootDirectory))
                AssetDatabase.CreateFolder(CustomAssetPackUtility.BuildRootDirectory, CustomAssetPackUtility.kPackContentFolderName);
            else
                ClearBundlesInAssetsFolder();

            foreach (var textureCompression in textureCompressions)
            {
                var postfix = TcfPostfix(ConvertToMobileTextureSubtarget(textureCompression));
                if (!AssetDatabase.IsValidFolder($"{CustomAssetPackUtility.BuildRootDirectory}/{Addressables.StreamingAssetsSubFolder}{postfix}"))
                    AssetDatabase.CreateFolder(CustomAssetPackUtility.BuildRootDirectory, $"{Addressables.StreamingAssetsSubFolder}{postfix}");
            }
        }

        bool BuildPathIncludedInStreamingAssets(string buildPath)
        {
            return buildPath.StartsWith(Addressables.BuildPath) || buildPath.StartsWith(Application.streamingAssetsPath);
        }

        string ConstructAssetPackDirectoryName(string assetPackName)
        {
            return $"{assetPackName}.androidpack";
        }

        string CreateAssetPackDirectory(string assetPackName)
        {
            string folderName = ConstructAssetPackDirectoryName(assetPackName);
            string path = Path.Combine(CustomAssetPackUtility.PackContentRootDirectory, folderName).Replace("\\", "/");

            if (!AssetDatabase.IsValidFolder(path))
                AssetDatabase.CreateFolder(CustomAssetPackUtility.PackContentRootDirectory, folderName);

            var assetNames = CustomAssetPackUtility.CustomAssetPacksAssetsPath.Split('/');
            var assetPath = path;
            assetNames[assetNames.Length - 1] += TcfPostfix(EditorUserBuildSettings.androidBuildSubtarget);
            foreach (var assetName in assetNames)
            {
                var newPath = Path.Combine(assetPath, assetName);
                if (!AssetDatabase.IsValidFolder(newPath))
                    AssetDatabase.CreateFolder(assetPath, assetName);
                assetPath = newPath;
            }
            return path;
        }

        bool HasRequiredSchemas(AddressableAssetSettings settings, AddressableAssetGroup group)
        {
            bool hasBundledSchema = group.HasSchema<BundledAssetGroupSchema>();
            bool hasPADSchema = group.HasSchema<PlayAssetDeliverySchema>();

            if (!hasBundledSchema && !hasPADSchema)
                return false;
            if (!hasBundledSchema && hasPADSchema)
            {
                Addressables.LogWarning($"Group '{group.name}' has a '{typeof(PlayAssetDeliverySchema).Name}' but not a '{typeof(BundledAssetGroupSchema).Name}'. " +
                    $"It does not contain any bundled content to be assigned to an asset pack.");
                return false;
            }
            if (hasBundledSchema && !hasPADSchema)
            {
                var bundledSchema = group.GetSchema<BundledAssetGroupSchema>();
                string buildPath = bundledSchema.BuildPath.GetValue(settings);
                if (BuildPathIncludedInStreamingAssets(buildPath))
                {
                    Addressables.Log($"Group '{group.name}' does not have a '{typeof(PlayAssetDeliverySchema).Name}' but its build path '{buildPath}' will be included in StreamingAssets at build time. " +
                        $"The group will be assigned to the generated asset packs unless its build path is changed.");
                }
                return false;
            }
            return true;
        }

        bool IsAssignedToCustomAssetPack(AddressableAssetSettings settings, AddressableAssetGroup group, PlayAssetDeliverySchema schema, CustomAssetPackEditorInfo assetPack)
        {
            if (!schema.IncludeInAssetPack)
            {
                var bundledSchema = group.GetSchema<BundledAssetGroupSchema>();
                string buildPath = bundledSchema.BuildPath.GetValue(settings);
                if (BuildPathIncludedInStreamingAssets(buildPath))
                {
                    Addressables.LogWarning($"Group '{group.name}' has 'Include In Asset Pack' disabled, but its build path '{buildPath}' will be included in StreamingAssets at build time. " +
                        $"The group will be assigned to the streaming assets pack.");
                }
                return false;
            }
            // Here we are skipping install-time addressables
            //if (assetPack.DeliveryType == DeliveryType.InstallTime)
            //    return false;

            return true;
        }

        void CreateConfigFiles(AddressableAssetGroup group, string assetPackName, DeliveryType deliveryType, Dictionary<string, CustomAssetPackDataEntry> assetPackToDataEntry, Dictionary<string, BuildProcessorDataEntry> bundleIdToEditorDataEntry)
        {
            foreach (AddressableAssetEntry entry in group.entries)
            {
                if (bundleIdToEditorDataEntry.ContainsKey(entry.BundleFileId))
                    continue;

                var bundleBuildPath = AddressablesRuntimeProperties.EvaluateString(entry.BundleFileId).Replace("\\", "/");
                var bundleName = Path.GetFileNameWithoutExtension(bundleBuildPath);
                Debug.Log($"Create config files {entry.BundleFileId} {bundleBuildPath} {bundleName}");

                var bundleFileName = Path.GetFileName(bundleBuildPath);
                var postfix = TcfPostfix(EditorUserBuildSettings.androidBuildSubtarget);
                var bundleFileFolder = $"{Path.GetDirectoryName(bundleBuildPath)}{postfix}";
                Directory.CreateDirectory(bundleFileFolder);
                File.Move(bundleBuildPath, Path.Combine(bundleFileFolder, bundleFileName));

                if (!assetPackToDataEntry.ContainsKey(assetPackName))
                {
                    // Create .androidpack directory and gradle file for the asset pack
                    assetPackToDataEntry[assetPackName] = new CustomAssetPackDataEntry(assetPackName, deliveryType, new List<string>() { bundleName });
                    string androidPackDir = CreateAssetPackDirectory(assetPackName);
                    CreateOrEditGradleFile(androidPackDir, assetPackName, deliveryType);
                }
                else
                {
                    // Otherwise just save the bundle to asset pack data
                    assetPackToDataEntry[assetPackName].AssetBundles.Add(bundleName);
                }

                // Store the bundle's build path and its corresponding .androidpack folder location
                var bundlePackDir = ConstructAssetPackDirectoryName(assetPackName);
                var assetsFolderPath = Path.Combine(bundlePackDir, $"{CustomAssetPackUtility.CustomAssetPacksAssetsPath}{postfix}", Path.GetFileName(bundleBuildPath));
                bundleIdToEditorDataEntry.Add(entry.BundleFileId, new BuildProcessorDataEntry(Path.Combine(bundleFileFolder, bundleFileName), assetsFolderPath));
            }
        }

        void CreateOrEditGradleFile(string androidPackDir, string assetPackName, DeliveryType deliveryType)
        {
            if (deliveryType == DeliveryType.None)
            {
                Addressables.Log($"Asset pack '{assetPackName}' has its delivery type set to 'None'. " +
                    $"No gradle file will be created for this asset pack. Unity assumes that any custom asset packs with no gradle file use on-demand delivery.");
                return;
            }

            // Warn about other gradle files in the .androidpack directory
            List<string> gradleFiles = Directory.EnumerateFiles(androidPackDir, "*.gradle").Where(x => Path.GetFileName(x) != "build.gradle").ToList();
            if (gradleFiles.Count > 0)
            {
                Addressables.LogWarning($"Custom asset pack at '{androidPackDir}' contains {gradleFiles.Count} files with .gradle extension which will be ignored. " +
                    $"Only the 'build.gradle' file will be included in the Android App Bundle.");
            }

#if UNITY_ANDROID
            // Create or edit the 'build.gradle' file in the .androidpack directory
            string deliveryTypeString = CustomAssetPackUtility.DeliveryTypeToGradleString(deliveryType);
            string buildFilePath = Path.Combine(androidPackDir, "build.gradle");
            string content = $"apply plugin: 'com.android.asset-pack'\n\nassetPack {{\n\tpackName = \"{assetPackName}\"\n\tdynamicDelivery {{\n\t\tdeliveryType = \"{deliveryTypeString}\"\n\t}}\n}}";
            File.WriteAllText(buildFilePath, content);
#endif            
        }

        void SerializeBuildProcessorData(List<BuildProcessorDataEntry> entries)
        {
            var customPackEditorData = new BuildProcessorData(entries);
            string contents = JsonUtility.ToJson(customPackEditorData);
            var postfix = TcfPostfix(EditorUserBuildSettings.androidBuildSubtarget);
            var jsonPath = Path.Combine(CustomAssetPackUtility.BuildRootDirectory, $"{Addressables.StreamingAssetsSubFolder}{postfix}", CustomAssetPackUtility.kBuildProcessorDataFilename);
            File.WriteAllText(jsonPath, contents);
        }

        void SerializeCustomAssetPacksData(List<CustomAssetPackDataEntry> entries)
        {
            var customPackData = new CustomAssetPackData(entries);
            string contents = JsonUtility.ToJson(customPackData);
            var postfix = TcfPostfix(EditorUserBuildSettings.androidBuildSubtarget);
            var jsonPath = Path.Combine(CustomAssetPackUtility.BuildRootDirectory, $"{Addressables.StreamingAssetsSubFolder}{postfix}", CustomAssetPackUtility.kCustomAssetPackDataFilename);
            File.WriteAllText(jsonPath, contents);
        }
    }
}
