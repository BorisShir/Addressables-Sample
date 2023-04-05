using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
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
            if ((--m_Current) < 0)
            {
                return false;
            }
            EditorUserBuildSettings.androidBuildSubtarget = ConvertToMobileTextureSubtarget(PlayerSettings.Android.textureCompressionFormats[m_Current]);
            return true;
        }

        internal static void Finish()
        {
            EditorUserBuildSettings.androidBuildSubtarget = m_StoredTextureCompressionFormat;
        }

        internal static bool IsLast => EnabledTextureCompressionTargeting && m_Current == 0;

        internal static string TcfPostfix(MobileTextureSubtarget subtarget)
        {
            return subtarget switch
            {
                MobileTextureSubtarget.ETC => "#tcf_etc1",
                MobileTextureSubtarget.ETC2 => "#tcf_etc2",
                MobileTextureSubtarget.ASTC => "#tcf_astc",
                MobileTextureSubtarget.PVRTC => "#tcf_pvrtc",
                MobileTextureSubtarget.DXT => "#tcf_dxt1",
                _ => throw new ArgumentException($"{subtarget} is not supported by TCFT")
            };
        }

        internal static string TcfPostfix(TextureCompressionFormat compression)
        {
            return TcfPostfix(ConvertToMobileTextureSubtarget(compression));
        }

        internal static string TcfPostfix()
        {
            return EnabledTextureCompressionTargeting ? TcfPostfix(EditorUserBuildSettings.androidBuildSubtarget) : "";
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
                TextureCompressionFormat.DXTC_RGTC => MobileTextureSubtarget.DXT,
                _ => throw new ArgumentException($"{compression} is not supported by TCFT")
            };
        }
    }

    /// <summary>
    /// In addition to the Default Build Script behavior (building AssetBundles), this script assigns Android bundled content to "install-time" or "on-demand" custom asset packs
    /// specified in <see cref="CustomAssetPackSettings"/>.
    ///
    /// We will create the config files necessary for creating an asset pack (see https://docs.unity3d.com/Manual/play-asset-delivery.html#custom-asset-packs).
    /// The files are:
    /// * An {asset pack name}.androidpack folder located in 'Assets/PlayAssetDelivery/Build/CustomAssetPackContent/src/main/assets/aa/Android'
    /// * A 'build.gradle' file for each .androidpack folder. If this file is missing, Unity will assume that the asset pack uses "on-demand" delivery.
    ///
    /// Additionally we generate some files to store build and runtime data that are located in in 'Assets/PlayAssetDelivery/Build/aa':
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

        Dictionary<string, string> m_AssetPacksNames = new Dictionary<string, string>();

        void AddResult<TResult>(ref TResult combined, TResult result) where TResult : IDataBuilderResult
        {
            combined.Duration += result.Duration;
            combined.LocationCount += result.LocationCount;
            combined.OutputPath = result.OutputPath;
            if (!string.IsNullOrEmpty(result.Error))
            {
                if (string.IsNullOrEmpty(combined.Error))
                {
                    combined.Error = result.Error;
                }
                else
                {
                    combined.Error += $"\n{result.Error}";
                }
            }
            if (result.FileRegistry != null)
            {
                if (combined.FileRegistry == null)
                {
                    combined.FileRegistry = combined.FileRegistry;
                }
                else
                {
                    foreach (var f in result.FileRegistry.GetFilePaths())
                    {
                        combined.FileRegistry.AddFile(f);
                    }
                }
            }
        }

        /*protected override string ProcessGroup(AddressableAssetGroup assetGroup, AddressableAssetsBuildContext aaContext)
        {
            if (assetGroup.HasSchema<BundledAssetGroupSchema>() && assetGroup.GetSchema<BundledAssetGroupSchema>().IncludeInBuild)
            {
                var schema = assetGroup.GetSchema<BundledAssetGroupSchema>() as BundledAssetGroupSchema;
                Debug.Log($"BuildPath {schema.BuildPath.GetValue(assetGroup.Settings)}");
                schema.BuildPath.SetVariableByName(assetGroup.Settings, "Local.BuildPath");
                schema.LoadPath.SetVariableByName(assetGroup.Settings, "Local.LoadPath");
            }
            return base.ProcessGroup(assetGroup, aaContext);
        }*/

        protected override TResult BuildDataImplementation<TResult>(AddressablesDataBuilderInput builderInput)
        {
            TResult result = AddressableAssetBuildResult.CreateResult<TResult>("", 0);
            m_AssetPacksNames.Clear();
            if (builderInput.Target == BuildTarget.Android)
            {
                CreateBuildOutputFolders();
            }
            if (TextureCompressionProcessor.EnabledTextureCompressionTargeting && builderInput.Target == BuildTarget.Android)
            {
                TextureCompressionProcessor.Start();
                try
                {
                    while (TextureCompressionProcessor.Next())
                    {
                        AssetDatabase.Refresh();
                        // consider trying to change builderInput so result is generated in the correct catalog
                        AddResult(ref result, base.BuildDataImplementation<TResult>(builderInput));
                    }
                }
                finally
                {
                    TextureCompressionProcessor.Finish();
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
                ClearTextureTargetCompressedBundles();
                ClearAssetPacksInAssetsFolder();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        void ClearAssetPacksInAssetsFolder()
        {
            if (AssetDatabase.IsValidFolder(CustomAssetPackUtility.PackContentRootDirectory))
            {
                // Delete all asset pack folders in 'Assets/PlayAssetDelivery/Build'
                foreach (var dir in Directory.EnumerateDirectories(CustomAssetPackUtility.PackContentRootDirectory, "*.androidpack"))
                {
                    AssetDatabase.DeleteAsset(dir);
                }
            }
        }

        void ClearTextureTargetCompressedBundles()
        {
            // TODO: Delete all Library\com.unity.addressables\aa\Android#tcf_* folders
        }

        void ClearJsonFiles()
        {
            // TODO: Delete "BuildProcessorData.json" files
        }

        void CreateCustomAssetPacks(AddressableAssetSettings settings, CustomAssetPackSettings customAssetPackSettings, bool resetAssetPackSchemaData)
        {
            var assetPackToDataEntry = new Dictionary<string, CustomAssetPackDataEntry>();
            var bundleIdToEditorDataEntry = new Dictionary<string, BuildProcessorDataEntry>();
            var bundleIdToEditorDataEntryDefault = new Dictionary<string, BuildProcessorDataEntry>();

            foreach (AddressableAssetGroup group in settings.groups)
            {
                if (HasRequiredSchemas(settings, group))
                {
                    var assetPackSchema = group.GetSchema<PlayAssetDeliverySchema>();
                    DeliveryType deliveryType = DeliveryType.None;
                    string assetPackName = "";
                    if (assetPackSchema.IncludeInAssetPack)
                    {
                        List<CustomAssetPackEditorInfo> customAssetPacks = customAssetPackSettings.CustomAssetPacks;
                        // Reset schema data to match Custom Asset Pack Settings. This can occur when the CustomAssetPackSettings was deleted but the schema properties still use the old settings data.
                        if (resetAssetPackSchemaData || assetPackSchema.AssetPackIndex >= customAssetPacks.Count)
                            assetPackSchema.ResetAssetPackIndex();

                        var assetPack = customAssetPacks[assetPackSchema.AssetPackIndex];
                        deliveryType = assetPack.DeliveryType;
                        assetPackName = assetPack.AssetPackName;
                    }
                    else
                    {
                        deliveryType = assetPackSchema.AssetPackDeliveryType;
                        if (!m_AssetPacksNames.TryGetValue(group.Name, out assetPackName))
                        {
                            assetPackName = Regex.Replace(group.Name, "[^A-Za-z0-9_-]", "");
                            if (assetPackName.Length == 0 || !char.IsLetter(assetPackName[0]))
                            {
                                assetPackName = "Group" + assetPackName;
                            }
                            assetPackName = customAssetPackSettings.GenerateUniqueName(assetPackName, m_AssetPacksNames.Values);
                            m_AssetPacksNames[group.Name] = assetPackName;
                        }
                    }

                    if (IsAssignedToCustomAssetPack(settings, group, assetPackSchema, deliveryType))
                    {
                        CreateConfigFiles(group, assetPackName, deliveryType, assetPackToDataEntry, bundleIdToEditorDataEntry, bundleIdToEditorDataEntryDefault);
                    }
                }
            }

            var postfix = TextureCompressionProcessor.TcfPostfix();

            // Create the BuildProcessorData.json file. It contains information for relocating custom asset pack bundles when building a player.
            SerializeBuildProcessorData(bundleIdToEditorDataEntry.Values.ToList(), postfix);

            // Create the CustomAssetPacksData.json file. It contains all custom asset pack information that can be used at runtime.
            SerializeCustomAssetPacksData(assetPackToDataEntry.Values.ToList(), "");

            if (TextureCompressionProcessor.EnabledTextureCompressionTargeting)
            {
                if (TextureCompressionProcessor.IsLast)
                {
                    // Create the BuildProcessorData.json file for the default variant.
                    SerializeBuildProcessorData(bundleIdToEditorDataEntryDefault.Values.ToList(), "");
                }
                else
                {
                    // we need to keep only one link.xml file because it would be the same for all texture compression variants
                    Directory.Delete(Path.Combine(Addressables.BuildPath, "AddressablesLink"), true);

                    var tcfBuildPath = $"{Addressables.BuildPath}{postfix}";
                    if (Directory.Exists(tcfBuildPath))
                    {
                        // probably can be removed after proper cleaning is implemented
                        Directory.Delete(tcfBuildPath, true);
                    }
                    Directory.Move(Addressables.BuildPath, tcfBuildPath);
                }
            }
        }

        void CreateBuildOutputFolders()
        {
            // TODO: is it required?
            ClearJsonFiles();

            // Create the 'Assets/PlayAssetDelivery/Build' directory
            if (!AssetDatabase.IsValidFolder(CustomAssetPackUtility.BuildRootDirectory))
            {
                AssetDatabase.CreateFolder(CustomAssetPackUtility.RootDirectory, CustomAssetPackUtility.kBuildFolderName);
            }
            else
            {
                ClearAssetPacksInAssetsFolder();
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

            var assetPath = path;
            foreach (var assetName in CustomAssetPackUtility.CustomAssetPacksAssetsPath.Split('/'))
            {
                var newPath = Path.Combine(assetPath, assetName);
                if (!AssetDatabase.IsValidFolder(newPath))
                    AssetDatabase.CreateFolder(assetPath, assetName);
                assetPath = newPath;
            }
            var tcfSubfolder = "Android";
            if (TextureCompressionProcessor.IsLast)
            {
                if (!AssetDatabase.IsValidFolder(Path.Combine(assetPath, tcfSubfolder)))
                    AssetDatabase.CreateFolder(assetPath, tcfSubfolder);
                File.WriteAllText(Path.Combine(assetPath, tcfSubfolder, "stub.txt"), "");
            }
            tcfSubfolder += TextureCompressionProcessor.TcfPostfix();
            if (!AssetDatabase.IsValidFolder(Path.Combine(assetPath, tcfSubfolder)))
                AssetDatabase.CreateFolder(assetPath, tcfSubfolder);
            File.WriteAllText(Path.Combine(assetPath, tcfSubfolder, "stub.txt"), "");
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

        bool IsAssignedToCustomAssetPack(AddressableAssetSettings settings, AddressableAssetGroup group, PlayAssetDeliverySchema schema, DeliveryType deliveryType)
        {
            if (!schema.IncludeInAssetPack && schema.AssetPackDeliveryType == DeliveryType.None)
            {
                var bundledSchema = group.GetSchema<BundledAssetGroupSchema>();
                string buildPath = bundledSchema.BuildPath.GetValue(settings);
                if (BuildPathIncludedInStreamingAssets(buildPath))
                {
                    // need to check what happens with TC targeting in this case
                    Addressables.LogWarning($"Group '{group.name}' has Delivery Type set to 'None' and it is not included to any custom asset packs, but its build path '{buildPath}' will be included in StreamingAssets at build time. The group will be assigned to the streaming assets pack.");
                }
                return false;
            }
            // Comment if we want to create separate asset pack(s) for install-time addressables
            if (deliveryType == DeliveryType.InstallTime)
            {
                return false;
            }

            return true;
        }

        void CreateConfigFiles(AddressableAssetGroup group, string assetPackName, DeliveryType deliveryType, Dictionary<string, CustomAssetPackDataEntry> assetPackToDataEntry, Dictionary<string, BuildProcessorDataEntry> bundleIdToEditorDataEntry, Dictionary<string, BuildProcessorDataEntry> bundleIdToEditorDataEntryDefault)
        {
            foreach (var entry in group.entries)
            {
                if (bundleIdToEditorDataEntry.ContainsKey(entry.BundleFileId))
                    continue;

                var bundleBuildPath = AddressablesRuntimeProperties.EvaluateString(entry.BundleFileId).Replace("\\", "/");               
                var bundleFileName = Path.GetFileName(bundleBuildPath);
                var bundleName = Path.GetFileNameWithoutExtension(bundleBuildPath);
                var postfix = TextureCompressionProcessor.IsLast ? "" : TextureCompressionProcessor.TcfPostfix();
                bundleBuildPath = Path.Combine($"{Addressables.BuildPath}{postfix}", Path.GetRelativePath(Addressables.BuildPath, bundleBuildPath));

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

                // Store the bundle's path in Gradle project and its corresponding .androidpack folder location
                var assetsTargetPath = Path.Combine(assetPackName, CustomAssetPackUtility.CustomAssetPacksAssetsPath, $"Android{TextureCompressionProcessor.TcfPostfix()}", bundleFileName);
                var assetsSourcePath = Path.Combine("UnityStreamingAssetsPack", $"{CustomAssetPackUtility.CustomAssetPacksAssetsPath}{TextureCompressionProcessor.TcfPostfix()}", "Android", bundleFileName);
                bundleIdToEditorDataEntry.Add(entry.BundleFileId, new BuildProcessorDataEntry(assetsSourcePath, assetsTargetPath));
                if (TextureCompressionProcessor.IsLast)
                {
                    assetsTargetPath = Path.Combine(assetPackName, CustomAssetPackUtility.CustomAssetPacksAssetsPath, "Android", bundleFileName);
                    assetsSourcePath = Path.Combine("UnityStreamingAssetsPack", CustomAssetPackUtility.CustomAssetPacksAssetsPath, "Android", bundleFileName);
                    bundleIdToEditorDataEntryDefault.Add(entry.BundleFileId, new BuildProcessorDataEntry(assetsSourcePath, assetsTargetPath));
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

        void SerializeBuildProcessorData(List<BuildProcessorDataEntry> entries, string postfix)
        {
            var path = Path.Combine(Path.GetDirectoryName(Addressables.BuildPath), "BuildProcessorData");
            Directory.CreateDirectory(path);
            var customPackEditorData = new BuildProcessorData(entries);
            string contents = JsonUtility.ToJson(customPackEditorData);
            var jsonPath = Path.Combine(path, $"BuildProcessorData{postfix}.json");
            File.WriteAllText(jsonPath, contents);
        }

        void SerializeCustomAssetPacksData(List<CustomAssetPackDataEntry> entries, string postfix)
        {
            var customPackData = new CustomAssetPackData(entries);
            string contents = JsonUtility.ToJson(customPackData);
            var jsonPath = Path.Combine($"{Addressables.BuildPath}{postfix}", CustomAssetPackUtility.kCustomAssetPackDataFilename);
            File.WriteAllText(jsonPath, contents);
        }
    }
}
