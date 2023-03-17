using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.Initialization;

namespace AddressablesPlayAssetDelivery.Editor
{
    internal static class TextureCompressionProcessor
    {
        internal static bool EnabledTextureCompressionTargeting => PlayerSettings.Android.textureCompressionFormats.Length > 1 &&
            EditorUserBuildSettings.overrideTextureCompression != OverrideTextureCompression.ForceUncompressed;

        private static MobileTextureSubtarget m_StoredTextureCompressionFormat;
        private static int m_Current;

        internal static void Start()
        {
            m_StoredTextureCompressionFormat = EditorUserBuildSettings.androidBuildSubtarget;
            m_Current = PlayerSettings.Android.textureCompressionFormats.Length;
        }

        internal static bool Next()
        {
            if (m_Current > 0)
            {
                --m_Current;
                EditorUserBuildSettings.androidBuildSubtarget = ConvertToMobileTextureSubtarget(PlayerSettings.Android.textureCompressionFormats[m_Current]);
                return true;
            }
            else
            {
                EditorUserBuildSettings.androidBuildSubtarget = m_StoredTextureCompressionFormat;
                return false;
            }
        }

        internal static bool IsLast => m_Current == 0;

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

        internal static string TcfPostfix(TextureCompressionFormat compression)
        {
            return TcfPostfix(ConvertToMobileTextureSubtarget(compression));
        }

        internal static string TcfPostfix()
        {
            return TcfPostfix(EditorUserBuildSettings.androidBuildSubtarget);
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
    }

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

        protected override TResult BuildDataImplementation<TResult>(AddressablesDataBuilderInput builderInput)
        {
            CreateBuildOutputFolders(PlayerSettings.Android.textureCompressionFormats);
            TResult result = default(TResult);
            if (TextureCompressionProcessor.EnabledTextureCompressionTargeting && builderInput.Target == BuildTarget.Android)
            {
                TextureCompressionProcessor.Start();
                while (TextureCompressionProcessor.Next())
                {
                    AssetDatabase.Refresh();
                    // find out what TResult might be, how we can detect that some step failed?
                    // don't forget to restore EditorUserBuildSettings.androidBuildSubtarget on early exit
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
            // Modify this to clear ALL json files (default and for different texture compressions)
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
            var bundleIdToEditorDataEntryDefault = new Dictionary<string, BuildProcessorDataEntry>();

            Directory.Move(Addressables.BuildPath, $"{Addressables.BuildPath}{TextureCompressionProcessor.TcfPostfix()}");

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
                        CreateConfigFiles(group, assetPack.AssetPackName, assetPack.DeliveryType, assetPackToDataEntry, bundleIdToEditorDataEntry, bundleIdToEditorDataEntryDefault);
                }
            }

            // Create the bundleIdToEditorDataEntry. It contains information for relocating custom asset pack bundles when building a player.
            SerializeBuildProcessorData(bundleIdToEditorDataEntry.Values.ToList(), false);

            // Create the CustomAssetPacksData.json file. It contains all custom asset pack information that can be used at runtime.
            SerializeCustomAssetPacksData(assetPackToDataEntry.Values.ToList(), false);

            // Create json files for the default variant.
            if (TextureCompressionProcessor.IsLast)
            {
                SerializeBuildProcessorData(bundleIdToEditorDataEntryDefault.Values.ToList(), true);
                SerializeCustomAssetPacksData(assetPackToDataEntry.Values.ToList(), true);
                // find out better place for this file, also re-check whole logic, why there is error message if file is deleted
                File.Copy($"{Addressables.BuildPath}{TextureCompressionProcessor.TcfPostfix()}/AddressablesLink/link.xml", "Assets");
            }

        }

        // do we really need to pass textureCompressions array here?
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

            if (!AssetDatabase.IsValidFolder($"{CustomAssetPackUtility.BuildRootDirectory}/{Addressables.StreamingAssetsSubFolder}"))
                AssetDatabase.CreateFolder(CustomAssetPackUtility.BuildRootDirectory, Addressables.StreamingAssetsSubFolder);
            foreach (var textureCompression in textureCompressions)
            {
                var postfix = TextureCompressionProcessor.TcfPostfix(textureCompression);
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
            for (int i = 0; i < assetNames.Length - 1; ++i)
            {
                var newPath = Path.Combine(assetPath, assetNames[i]);
                if (!AssetDatabase.IsValidFolder(newPath))
                    AssetDatabase.CreateFolder(assetPath, assetNames[i]);
                assetPath = newPath;
            }
            var lastAssetName = assetNames.Last();
            if (TextureCompressionProcessor.IsLast)
            {
                if (!AssetDatabase.IsValidFolder(Path.Combine(assetPath, lastAssetName)))
                    AssetDatabase.CreateFolder(assetPath, lastAssetName);
            }
            lastAssetName += TextureCompressionProcessor.TcfPostfix();
            if (!AssetDatabase.IsValidFolder(Path.Combine(assetPath, lastAssetName)))
                AssetDatabase.CreateFolder(assetPath, lastAssetName);
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

        void CreateConfigFiles(AddressableAssetGroup group, string assetPackName, DeliveryType deliveryType, Dictionary<string, CustomAssetPackDataEntry> assetPackToDataEntry, Dictionary<string, BuildProcessorDataEntry> bundleIdToEditorDataEntry, Dictionary<string, BuildProcessorDataEntry> bundleIdToEditorDataEntryDefault)
        {
            foreach (AddressableAssetEntry entry in group.entries)
            {
                if (bundleIdToEditorDataEntry.ContainsKey(entry.BundleFileId))
                    continue;

                var bundleBuildPath = AddressablesRuntimeProperties.EvaluateString(entry.BundleFileId);
                // need to make sure that replace is working correctly, not depending on directory delimiter
                bundleBuildPath = bundleBuildPath.Replace(Addressables.BuildPath, $"{Addressables.BuildPath}{TextureCompressionProcessor.TcfPostfix()}").Replace("\\", "/");
                var bundleName = Path.GetFileNameWithoutExtension(bundleBuildPath);
                Debug.Log($"Create config files {entry.BundleFileId} {bundleBuildPath} {bundleName}");

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
                var postfix = TextureCompressionProcessor.TcfPostfix();
                var assetsFolderPath = Path.Combine(bundlePackDir, $"{CustomAssetPackUtility.CustomAssetPacksAssetsPath}{postfix}", Path.GetFileName(bundleBuildPath));
                bundleIdToEditorDataEntry.Add(entry.BundleFileId, new BuildProcessorDataEntry(bundleBuildPath, assetsFolderPath));
                if (TextureCompressionProcessor.IsLast)
                {
                    assetsFolderPath = Path.Combine(bundlePackDir, $"{CustomAssetPackUtility.CustomAssetPacksAssetsPath}", Path.GetFileName(bundleBuildPath));
                    bundleIdToEditorDataEntryDefault.Add(entry.BundleFileId, new BuildProcessorDataEntry(bundleBuildPath, assetsFolderPath));
                }
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

        void SerializeBuildProcessorData(List<BuildProcessorDataEntry> entries, bool isDefault)
        {
            var customPackEditorData = new BuildProcessorData(entries);
            string contents = JsonUtility.ToJson(customPackEditorData);
            var folderWithPostfix = Addressables.StreamingAssetsSubFolder;
            if (!isDefault)
            {
                folderWithPostfix += TextureCompressionProcessor.TcfPostfix();
            }
            var jsonPath = Path.Combine(CustomAssetPackUtility.BuildRootDirectory, folderWithPostfix, CustomAssetPackUtility.kBuildProcessorDataFilename);
            File.WriteAllText(jsonPath, contents);
        }

        void SerializeCustomAssetPacksData(List<CustomAssetPackDataEntry> entries, bool isDefault)
        {
            var customPackData = new CustomAssetPackData(entries);
            string contents = JsonUtility.ToJson(customPackData);
            var folderWithPostfix = Addressables.StreamingAssetsSubFolder;
            if (!isDefault)
            {
                folderWithPostfix += TextureCompressionProcessor.TcfPostfix();
            }
            var jsonPath = Path.Combine(CustomAssetPackUtility.BuildRootDirectory, folderWithPostfix, CustomAssetPackUtility.kCustomAssetPackDataFilename);
            File.WriteAllText(jsonPath, contents);
        }
    }
}
