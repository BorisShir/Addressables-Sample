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

        internal static bool UseCustomAssetPacks => EditorUserBuildSettings.buildAppBundle && (PlayerSettings.Android.splitApplicationBinary || EnabledTextureCompressionTargeting);

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

    // TODO rewrite
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

        Dictionary<AddressableAssetGroup, string> m_BuildPathRestore = new Dictionary<AddressableAssetGroup, string>();
        Dictionary<AddressableAssetGroup, string> m_LoadPathRestore = new Dictionary<AddressableAssetGroup, string>();

        void SetLocalBuildLoadPaths(IEnumerable<AddressableAssetGroup> groups)
        {
            foreach (var group in groups)
            {
                if (group.HasSchema<PlayAssetDeliverySchema>() && group.HasSchema<BundledAssetGroupSchema>())
                {
                    var schema = group.GetSchema<BundledAssetGroupSchema>();
                    if (schema.BuildPath.GetName(group.Settings) != AddressableAssetSettings.kLocalBuildPath)
                    {
                        m_BuildPathRestore[group] = schema.BuildPath.Id;
                        schema.BuildPath.SetVariableByName(group.Settings, AddressableAssetSettings.kLocalBuildPath);
                    }
                    if (schema.LoadPath.GetName(group.Settings) != AddressableAssetSettings.kLocalLoadPath)
                    {
                        m_LoadPathRestore[group] = schema.LoadPath.Id;
                        schema.LoadPath.SetVariableByName(group.Settings, AddressableAssetSettings.kLocalLoadPath);
                    }
                }
            }
        }

        void RestoreBuildLoadPaths()
        {
            foreach (var bp in m_BuildPathRestore)
            {
                var schema = bp.Key.GetSchema<BundledAssetGroupSchema>();
                schema.BuildPath.SetVariableById(bp.Key.Settings, bp.Value);
            }
            m_BuildPathRestore.Clear();
            foreach (var lp in m_LoadPathRestore)
            {
                var schema = lp.Key.GetSchema<BundledAssetGroupSchema>();
                schema.LoadPath.SetVariableById(lp.Key.Settings, lp.Value);
            }
            m_LoadPathRestore.Clear();
        }

        protected override TResult BuildDataImplementation<TResult>(AddressablesDataBuilderInput builderInput)
        {
            // Don't prepare content for asset packs if the build target isn't set to Android
            if (builderInput.Target != BuildTarget.Android)
            {
                Addressables.LogWarning("Build target is not set to Android. No custom asset pack config files will be created.");
                return base.BuildDataImplementation<TResult>(builderInput);
            }

            TResult result = AddressableAssetBuildResult.CreateResult<TResult>("", 0);
            m_AssetPacksNames.Clear();
            m_AssetPacksNames[""] = CustomAssetPackUtility.kAddressablesAssetPackName; // to avoid using this name for groups
            PlayAssetDeliveryBuildProcessor.MoveDataToDefaultLocation();

            SetLocalBuildLoadPaths(builderInput.AddressableSettings.groups);
            if (TextureCompressionProcessor.EnabledTextureCompressionTargeting)
            {
                CreateBuildOutputFolders(PlayerSettings.Android.textureCompressionFormats);
                TextureCompressionProcessor.Start();
                try
                {
                    while (TextureCompressionProcessor.Next())
                    {
                        AssetDatabase.Refresh();
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
                CreateBuildOutputFolders();
                result = base.BuildDataImplementation<TResult>(builderInput);
            }
            RestoreBuildLoadPaths();

            if (TextureCompressionProcessor.UseCustomAssetPacks)
            {
                PlayAssetDeliveryBuildProcessor.MoveDataForAppBundleBuild();
            }

            return result;
        }

        protected override TResult DoBuild<TResult>(AddressablesDataBuilderInput builderInput, AddressableAssetsBuildContext aaContext)
        {
            // Build AssetBundles
            TResult result = base.DoBuild<TResult>(builderInput, aaContext);
            if (builderInput.Target != BuildTarget.Android)
            {
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
                ClearCustomAssetPacksContent();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        void ClearCustomAssetPacksContent()
        {
            if (AssetDatabase.IsValidFolder(CustomAssetPackUtility.PackContentRootDirectory))
            {
                // Delete all bundle and json files in 'Assets/PlayAssetDelivery/Build/CustomAssetPackContent'
                var bundleFiles = Directory.EnumerateFiles(CustomAssetPackUtility.PackContentRootDirectory, "*.bundle", SearchOption.AllDirectories).ToList();
                foreach (var file in bundleFiles)
                {
                    AssetDatabase.DeleteAsset(file);
                }
                var jsonFiles = Directory.EnumerateFiles(CustomAssetPackUtility.PackContentRootDirectory, "*.json", SearchOption.AllDirectories).ToList();
                foreach (var file in jsonFiles)
                {
                    AssetDatabase.DeleteAsset(file);
                }
            }
        }

        void ClearJsonFiles()
        {
            var dirs = Directory.EnumerateDirectories(CustomAssetPackUtility.BuildRootDirectory, $"{Addressables.StreamingAssetsSubFolder}*");

            foreach (var dir in dirs)
            {
                // Delete "BuildProcessorData.json"
                var file = Path.Combine(dir, CustomAssetPackUtility.kBuildProcessorDataFilename);
                if (File.Exists(file))
                {
                    AssetDatabase.DeleteAsset(file);
                }

                // TODO do we need to delete directories as well?
            }

            // Delete "CustomAssetPacksData.json"
            if (File.Exists(CustomAssetPackUtility.CustomAssetPacksDataEditorPath))
            {
                AssetDatabase.DeleteAsset(CustomAssetPackUtility.CustomAssetPacksDataEditorPath);
            }
        }

        void CreateCustomAssetPacks(AddressableAssetSettings settings, CustomAssetPackSettings customAssetPackSettings, bool resetAssetPackSchemaData)
        {
            var assetPackToDataEntry = new Dictionary<string, CustomAssetPackDataEntry>();
            var bundleIdToEditorDataEntry = new Dictionary<string, BuildProcessorDataEntry>();
            var bundleIdToEditorDataEntryDefault = new Dictionary<string, BuildProcessorDataEntry>();

            var androidPackDir = CreateAssetPackDirectory(CustomAssetPackUtility.kAddressablesAssetPackName);
            CreateOrEditGradleFile(androidPackDir, CustomAssetPackUtility.kAddressablesAssetPackName, DeliveryType.InstallTime);

            foreach (AddressableAssetGroup group in settings.groups)
            {
                if (HasRequiredSchemas(settings, group))
                {
                    var assetPackSchema = group.GetSchema<PlayAssetDeliverySchema>();
                    DeliveryType deliveryType = DeliveryType.None;
                    var assetPackName = "";
                    if (assetPackSchema.IncludeInAssetPack)
                    {
                        List<CustomAssetPackEditorInfo> customAssetPacks = customAssetPackSettings.CustomAssetPacks;
                        // Reset schema data to match Custom Asset Pack Settings. This can occur when the CustomAssetPackSettings was deleted but the schema properties still use the old settings data.
                        if (resetAssetPackSchemaData || assetPackSchema.AssetPackIndex >= customAssetPacks.Count)
                        {
                            assetPackSchema.ResetAssetPackIndex();
                        }

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
                    // install-time addressable groups are all packed to AddressablesAssetPack
                    if (deliveryType == DeliveryType.InstallTime)
                    {
                        assetPackName = CustomAssetPackUtility.kAddressablesAssetPackName;
                    }

                    if (IsAssignedToCustomAssetPack(settings, group, assetPackSchema, deliveryType))
                    {
                        CreateConfigFiles(group, assetPackName, deliveryType, assetPackToDataEntry, bundleIdToEditorDataEntry, bundleIdToEditorDataEntryDefault);
                    }
                }
                else if (TextureCompressionProcessor.EnabledTextureCompressionTargeting && !TextureCompressionProcessor.IsLast)
                {
                    // delete non Play Asset Delivery bundles for non-default texture compression
                    foreach (var entry in group.entries)
                    {
                        var bundleFilePath = AddressablesRuntimeProperties.EvaluateString(entry.BundleFileId);
                        if (File.Exists(bundleFilePath))
                        {
                            File.Delete(bundleFilePath);
                        }
                    }
                }
            }

            var postfix = TextureCompressionProcessor.TcfPostfix();

            // Create the bundleIdToEditorDataEntry. It contains information for relocating custom asset pack bundles when building a player.
            SerializeBuildProcessorData(bundleIdToEditorDataEntry.Values.ToList(), postfix);

            // Create the CustomAssetPacksData.json file. It contains all custom asset pack information that can be used at runtime.
            SerializeCustomAssetPacksData(assetPackToDataEntry.Values.ToList(), postfix);

            if (TextureCompressionProcessor.EnabledTextureCompressionTargeting && TextureCompressionProcessor.IsLast)
            {
                // Create json files for the default variant.
                SerializeBuildProcessorData(bundleIdToEditorDataEntryDefault.Values.ToList(), "");
                SerializeCustomAssetPacksData(assetPackToDataEntry.Values.ToList(), "");
            }

            var addressablesLink = Path.Combine(Addressables.BuildPath, "AddressablesLink");
            if (!TextureCompressionProcessor.EnabledTextureCompressionTargeting || TextureCompressionProcessor.IsLast)
            {
                // move link.xml to Build folder
                var addressablesBuildLink = Path.Combine(CustomAssetPackUtility.BuildRootDirectory, "AddressablesLink");
                if (Directory.Exists(addressablesBuildLink))
                {
                    Directory.Delete(addressablesBuildLink, true);
                }
                Directory.Move(addressablesLink, addressablesBuildLink);
            }
            else
            {
                // we need only one link.xml file which would be the same for all texture compression variants
                Directory.Delete(addressablesLink, true);

                // moving generated files to the texture compression specific directory
                var tcfBuildPath = $"{Addressables.BuildPath}{postfix}";
                if (Directory.Exists(tcfBuildPath))
                {
                    Directory.Delete(tcfBuildPath, true);
                }
                Directory.Move(Addressables.BuildPath, tcfBuildPath);
            }
        }

        void CreateBuildOutputFolder(string postfix)
        {
            var folderWithPostfix = $"{Addressables.StreamingAssetsSubFolder}{postfix}";
            if (!AssetDatabase.IsValidFolder(Path.Combine(CustomAssetPackUtility.BuildRootDirectory, folderWithPostfix)))
            {
                AssetDatabase.CreateFolder(CustomAssetPackUtility.BuildRootDirectory, folderWithPostfix);
            }
        }

        void CreateBuildOutputFolders(TextureCompressionFormat[] textureCompressions = null)
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
                ClearCustomAssetPacksContent();

            CreateBuildOutputFolder("");
            if (textureCompressions != null)
            {
                foreach (var textureCompression in textureCompressions)
                {
                    CreateBuildOutputFolder(TextureCompressionProcessor.TcfPostfix(textureCompression));
                }
            }
        }

        bool BuildPathIncludedInStreamingAssets(string buildPath)
        {
            return buildPath.StartsWith(Addressables.BuildPath) || buildPath.StartsWith(Application.streamingAssetsPath);
        }

        static internal string ConstructAssetPackDirectoryName(string assetPackName)
        {
            return $"{assetPackName}.androidpack";
        }

        static internal string AddressableAssetPackAssetsPath(string postfix)
        {
            return Path.Combine(CustomAssetPackUtility.PackContentRootDirectory, ConstructAssetPackDirectoryName(CustomAssetPackUtility.kAddressablesAssetPackName), $"{CustomAssetPackUtility.CustomAssetPacksAssetsPath}{postfix}");
        }

        void CreateLastSubfolder(string assetPath, string folderName)
        {
            if (!AssetDatabase.IsValidFolder(Path.Combine(assetPath, folderName)))
            {
                AssetDatabase.CreateFolder(assetPath, folderName);
            }
            assetPath = Path.Combine(assetPath, folderName);
            if (!AssetDatabase.IsValidFolder(Path.Combine(assetPath, "Android")))
            {
                AssetDatabase.CreateFolder(assetPath, "Android");
            }
        }

        string CreateAssetPackDirectory(string assetPackName)
        {
            var folderName = ConstructAssetPackDirectoryName(assetPackName);
            var path = Path.Combine(CustomAssetPackUtility.PackContentRootDirectory, folderName).Replace("\\", "/");

            if (!AssetDatabase.IsValidFolder(path))
            {
                AssetDatabase.CreateFolder(CustomAssetPackUtility.PackContentRootDirectory, folderName);
            }

            var assetPath = path;
            var folders = CustomAssetPackUtility.CustomAssetPacksAssetsPath.Split('/');
            for (int i = 0; i < folders.Length - 1; ++i)
            {
                var newPath = Path.Combine(assetPath, folders[i]);
                if (!AssetDatabase.IsValidFolder(newPath))
                {
                    AssetDatabase.CreateFolder(assetPath, folders[i]);
                }
                assetPath = newPath;
            }
            if (TextureCompressionProcessor.IsLast)
            {
                CreateLastSubfolder(assetPath, folders[^1]);
            }
            CreateLastSubfolder(assetPath, $"{folders[^1]}{TextureCompressionProcessor.TcfPostfix()}");
            return path;
        }

        bool HasRequiredSchemas(AddressableAssetSettings settings, AddressableAssetGroup group)
        {
            var hasBundledSchema = group.HasSchema<BundledAssetGroupSchema>();
            var hasPADSchema = group.HasSchema<PlayAssetDeliverySchema>();

            if (!hasBundledSchema && !hasPADSchema)
            {
                return false;
            }
            if (!hasBundledSchema && hasPADSchema)
            {
                Addressables.LogWarning($"Group '{group.name}' has a '{typeof(PlayAssetDeliverySchema).Name}' but not a '{typeof(BundledAssetGroupSchema).Name}'. " +
                    $"It does not contain any bundled content to be assigned to an asset pack.");
                return false;
            }
            if (hasBundledSchema && !hasPADSchema)
            {
                var bundledSchema = group.GetSchema<BundledAssetGroupSchema>();
                var buildPath = bundledSchema.BuildPath.GetValue(settings);
                if (BuildPathIncludedInStreamingAssets(buildPath))
                {
                    // check what happens if TC is on
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
                var buildPath = bundledSchema.BuildPath.GetValue(settings);
                if (BuildPathIncludedInStreamingAssets(buildPath))
                {
                    // need to check what happens with TC targeting in this case
                    Addressables.LogWarning($"Group '{group.name}' has Delivery Type set to 'None' and it is not included to any custom asset packs, but its build path '{buildPath}' will be included in StreamingAssets at build time. The group will be assigned to the streaming assets pack.");
                }
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

                // Store the bundle's build path and its corresponding .androidpack folder location
                var bundlePackDir = ConstructAssetPackDirectoryName(assetPackName);
                var assetsFolderPath = Path.Combine(bundlePackDir, $"{CustomAssetPackUtility.CustomAssetPacksAssetsPath}{TextureCompressionProcessor.TcfPostfix()}", "Android", bundleFileName);
                bundleIdToEditorDataEntry.Add(entry.BundleFileId, new BuildProcessorDataEntry(bundleBuildPath, assetsFolderPath));
                if (TextureCompressionProcessor.IsLast)
                {
                    assetsFolderPath = Path.Combine(bundlePackDir, CustomAssetPackUtility.CustomAssetPacksAssetsPath, "Android", bundleFileName);
                    bundleIdToEditorDataEntryDefault.Add(entry.BundleFileId, new BuildProcessorDataEntry(bundleBuildPath, assetsFolderPath));
                }
            }
        }

        void CreateOrEditGradleFile(string androidPackDir, string assetPackName, DeliveryType deliveryType)
        {
            if (deliveryType == DeliveryType.None)
            {
                // TODO check if this condition is required
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
            var deliveryTypeString = CustomAssetPackUtility.DeliveryTypeToGradleString(deliveryType);
            var buildFilePath = Path.Combine(androidPackDir, "build.gradle");
            var content = $"apply plugin: 'com.android.asset-pack'\n\nassetPack {{\n\tpackName = \"{assetPackName}\"\n\tdynamicDelivery {{\n\t\tdeliveryType = \"{deliveryTypeString}\"\n\t}}\n}}";
            File.WriteAllText(buildFilePath, content);
#endif
        }

        void SerializeBuildProcessorData(List<BuildProcessorDataEntry> entries, string postfix)
        {
            var customPackEditorData = new BuildProcessorData(entries);
            var contents = JsonUtility.ToJson(customPackEditorData);
            var jsonPath = Path.Combine(CustomAssetPackUtility.BuildRootDirectory, $"{Addressables.StreamingAssetsSubFolder}{postfix}", CustomAssetPackUtility.kBuildProcessorDataFilename);
            File.WriteAllText(jsonPath, contents);
        }

        void SerializeCustomAssetPacksData(List<CustomAssetPackDataEntry> entries, string postfix)
        {
            var customPackData = new CustomAssetPackData(entries);
            var contents = JsonUtility.ToJson(customPackData);
            if (postfix != "" || TextureCompressionProcessor.UseCustomAssetPacks)
            {
                // no need to write variant for the default texture compression if we are not using custom asset packs
                var jsonPath = Path.Combine(AddressableAssetPackAssetsPath(postfix), CustomAssetPackUtility.kCustomAssetPackDataFilename);
                File.WriteAllText(jsonPath, contents);
            }
            if (postfix == "")
            {
                // need to write default variant outside of the custom asset packs to be used for Editor Play Mode
                File.WriteAllText(CustomAssetPackUtility.CustomAssetPacksDataEditorPath, contents);
            }
        }
    }
}
