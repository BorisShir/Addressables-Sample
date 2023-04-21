using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Callbacks;
using UnityEditor.Android;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace AddressablesPlayAssetDelivery.Editor
{
    /// <summary>
    /// Moves custom asset pack data from their default build location <see cref="BuildScriptPlayAssetDelivery"/> to their correct player build data location.
    /// For an Android App Bundle, bundles assigned to a custom asset pack must be located in their {asset pack name}.androidpack directory in the Assets folder.
    ///
    /// This script executes before the <see cref="AddressablesPlayerBuildProcessor"/> which moves all Addressables data to StreamingAssets.
    public class PlayAssetDeliveryBuildProcessor : BuildPlayerProcessor
    {
        /// <summary>
        /// Returns the player build processor callback order.
        /// </summary>
        public override int callbackOrder => 0;

        static internal bool BuildingPlayer { get; set; } = false;

        static internal int DataBuilderIndex { get; set; } = 0;

        T CreateScriptAsset<T>(string subfolder) where T : ScriptableObject
        {
            var script = ScriptableObject.CreateInstance<T>();
            var path = Path.Combine(AddressableAssetSettingsDefaultObject.Settings.ConfigFolder, subfolder);
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            path = Path.Combine(path, $"{typeof(T).Name}.asset");
            if (!File.Exists(path))
            {
                AssetDatabase.CreateAsset(script, path);
            }
            return AssetDatabase.LoadAssetAtPath<T>(path);
        }

        /// <summary>
        /// Invoked before performing a Player build. Moves AssetBundles to their correct data location based on the build target platform.
        /// </summary>
        /// <param name="buildPlayerContext">Contains data related to the player.</param>
        public override void PrepareForBuild(BuildPlayerContext buildPlayerContext)
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                return;
            }

            // need to check that Addressables are built for Android
            if (TextureCompressionProcessor.UseCustomAssetPacks)
            {
                if (AddressableAssetSettingsDefaultObject.Settings.InitializationObjects.FindIndex(i => i is PlayAssetDeliveryInitializationSettings) == -1)
                {
                    // add PlayAssetDeliveryInitialization script asset if required
                    AddressableAssetSettingsDefaultObject.Settings.AddInitializationObject(CreateScriptAsset<PlayAssetDeliveryInitializationSettings>("InitObjects"));
                }
                DataBuilderIndex = AddressableAssetSettingsDefaultObject.Settings.ActivePlayerDataBuilderIndex;
                var padBuildScriptIndex = AddressableAssetSettingsDefaultObject.Settings.DataBuilders.FindIndex(b => b is BuildScriptPlayAssetDelivery);
                if (padBuildScriptIndex == -1)
                {
                    // add BuildScriptPlayAssetDelivery script asset if required
                    // TODO if this script asset is not added, then this script is not available for manual build from Addressables Groups window
                    // TODO consider moving this code to where it can be called automatically once package is installed
                    AddressableAssetSettingsDefaultObject.Settings.AddDataBuilder(CreateScriptAsset<BuildScriptPlayAssetDelivery>("DataBuilders"));
                    padBuildScriptIndex = AddressableAssetSettingsDefaultObject.Settings.DataBuilders.Count - 1;
                }
                AddressableAssetSettingsDefaultObject.Settings.ActivePlayerDataBuilderIndex = padBuildScriptIndex;
                BuildingPlayer = true;
                MoveDataForAppBundleBuild();
            }
            else
            {
                MoveDataToDefaultLocation();
            }
        }

        static bool MoveBundlesToCustomAssetPacks(string postfix)
        {
            var buildProcessorDataPath = Path.Combine(CustomAssetPackUtility.BuildRootDirectory, $"{Addressables.StreamingAssetsSubFolder}{postfix}", CustomAssetPackUtility.kBuildProcessorDataFilename);
            if (!File.Exists(buildProcessorDataPath))
            {
                return false;
            }
            var contents = File.ReadAllText(buildProcessorDataPath);
            var data = JsonUtility.FromJson<BuildProcessorData>(contents);

            foreach (BuildProcessorDataEntry entry in data.Entries)
            {
                if (!File.Exists(entry.BundleBuildPath))
                {
                    // already moved
                    continue;
                }
                var assetsFolderPath = Path.Combine(CustomAssetPackUtility.PackContentRootDirectory, entry.AssetsSubfolderPath);
                if (File.Exists(assetsFolderPath))
                {
                    File.Delete(assetsFolderPath);
                }
                if (TextureCompressionProcessor.EnabledTextureCompressionTargeting && string.IsNullOrEmpty(postfix))
                {
                    File.Copy(entry.BundleBuildPath, assetsFolderPath);
                }
                else
                {
                    var metaFilePath = AssetDatabase.GetTextMetaFilePathFromAssetPath(entry.BundleBuildPath);
                    File.Move(entry.BundleBuildPath, assetsFolderPath);
                    if (File.Exists(metaFilePath))
                    {
                        // metafile might exist only if BuildPath is not default "LocalBuildPath"
                        // and points to something inside Assets folder
                        File.Delete(metaFilePath);
                    }
                }
            }
            return true;
        }

        static readonly string[] SharedFilesMasks =
        {
            "*unitybuiltinshaders*.bundle",
            "*monoscripts*.bundle",
            "settings.json",
            "catalog.json",
            "catalog.bin"
        };

        static void CopyOrMoveSharedFiles(string from, string to, bool copy)
        {
            foreach (var mask in SharedFilesMasks)
            {
                var files = Directory.EnumerateFiles(from, mask, SearchOption.AllDirectories).ToList();
                foreach (var f in files)
                {
                    var dest = Path.Combine(to, Path.GetRelativePath(from, f));
                    if (copy)
                    {
                        File.Copy(f, dest, true);
                    }
                    else
                    {
                        var metaFilePath = AssetDatabase.GetTextMetaFilePathFromAssetPath(f);
                        File.Move(f, dest);
                        if (File.Exists(metaFilePath))
                        {
                            File.Delete(metaFilePath);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Move custom asset pack data from their build location to their App Bundle data location.
        /// </summary>
        public static void MoveDataForAppBundleBuild()
        {
            try
            {
                AssetDatabase.StartAssetEditing();
                if (!MoveBundlesToCustomAssetPacks(""))
                {
                    // Addressables are not built for PAD yet
                    return;
                }
                var addressablesAssetPackFolder = BuildScriptPlayAssetDelivery.ConstructAssetPackDirectoryName(CustomAssetPackUtility.kAddressablesAssetPackName);

                if (TextureCompressionProcessor.EnabledTextureCompressionTargeting)
                {
                    foreach (var textureCompression in PlayerSettings.Android.textureCompressionFormats)
                    {
                        var postfix = TextureCompressionProcessor.TcfPostfix(textureCompression);
                        MoveBundlesToCustomAssetPacks(postfix);
                        var targetPath = Path.Combine(CustomAssetPackUtility.PackContentRootDirectory, addressablesAssetPackFolder, $"{CustomAssetPackUtility.CustomAssetPacksAssetsPath}{postfix}");
                        var sourcePath = $"{Addressables.BuildPath}{postfix}";
                        var copyFiles = false;
                        if (!Directory.Exists(sourcePath))
                        {
                            // using default texture compression variant
                            sourcePath = Addressables.BuildPath;
                            copyFiles = true;
                        }
                        CopyOrMoveSharedFiles(sourcePath, targetPath, copyFiles);
                    }
                }

                var jsonPath = Path.Combine(BuildScriptPlayAssetDelivery.AddressableAssetPackAssetsPath(""), CustomAssetPackUtility.kCustomAssetPackDataFilename);
                if (!File.Exists(jsonPath))
                {
                    File.Copy(CustomAssetPackUtility.CustomAssetPacksDataEditorPath, jsonPath);
                }

                if (BuildingPlayer)
                {
                    var targetPath = Path.Combine(CustomAssetPackUtility.PackContentRootDirectory, addressablesAssetPackFolder, CustomAssetPackUtility.CustomAssetPacksAssetsPath);
                    CopyOrMoveSharedFiles(Addressables.BuildPath, targetPath, false);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Exception occured when moving data for an app bundle build: {e.Message} at {e.StackTrace}.");
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
        }

        public static void MoveDataAfterAppBundleBuild()
        {
            try
            {
                AssetDatabase.StartAssetEditing();
                var sourcePath = BuildScriptPlayAssetDelivery.AddressableAssetPackAssetsPath("");
                CopyOrMoveSharedFiles(sourcePath, Addressables.BuildPath, false);
            }
            catch (Exception e)
            {
                Debug.LogError($"Exception occured when moving data after an app bundle build: {e.Message} at {e.StackTrace}.");
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
        }

        /// <summary>
        /// Move custom asset pack data from their App Bundle data location to to their build location.
        /// </summary>
        public static void MoveDataToDefaultLocation()
        {
            try
            {
                AssetDatabase.StartAssetEditing();
                if (File.Exists(CustomAssetPackUtility.BuildProcessorDataPath))
                {
                    var contents = File.ReadAllText(CustomAssetPackUtility.BuildProcessorDataPath);
                    var data = JsonUtility.FromJson<BuildProcessorData>(contents);

                    foreach (BuildProcessorDataEntry entry in data.Entries)
                    {
                        var assetsFolderPath = Path.Combine(CustomAssetPackUtility.PackContentRootDirectory, entry.AssetsSubfolderPath);
                        if (File.Exists(assetsFolderPath) && Directory.Exists(Path.GetDirectoryName(entry.BundleBuildPath)))
                        {
                            // TODO probably we need to keep existing file and just delete file from custom asset pack
                            if (File.Exists(entry.BundleBuildPath))
                            {
                                File.Delete(entry.BundleBuildPath);
                            }
                            var metaFilePath = AssetDatabase.GetTextMetaFilePathFromAssetPath(assetsFolderPath);
                            File.Move(assetsFolderPath, entry.BundleBuildPath);
                            if (File.Exists(metaFilePath))
                            {
                                File.Delete(metaFilePath);
                            }
                        }
                    }
                }

                var jsonPath = Path.Combine(BuildScriptPlayAssetDelivery.AddressableAssetPackAssetsPath(""), CustomAssetPackUtility.kCustomAssetPackDataFilename);
                if (File.Exists(jsonPath))
                {
                    var metaFilePath = AssetDatabase.GetTextMetaFilePathFromAssetPath(jsonPath);
                    File.Delete(jsonPath);
                    if (File.Exists(metaFilePath))
                    {
                        File.Delete(metaFilePath);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Exception occured when moving data for a player build: {e.Message}.");
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
        }
    }

    /// <summary>
    /// TODO write summary
    /// This script executes after the <see cref="AddressablesPlayerBuildProcessor"/> which moves other Addressables data to StreamingAssets.
    public class PlayAssetDeliverySecondBuildProcessor : BuildPlayerProcessor, IPostGenerateGradleAndroidProject
    {
        /// <summary>
        /// Returns the player build processor callback order.
        /// </summary>
        public override int callbackOrder => 2;

        /// <summary>
        /// Invoked before performing a Player build. Moves AssetBundles to their correct data location based on the build target platform.
        /// </summary>
        /// <param name="buildPlayerContext">Contains data related to the player.</param>
        public override void PrepareForBuild(BuildPlayerContext buildPlayerContext)
        {
            // TODO probably need to check here that bundles exist for all TC variants
            PlayAssetDeliveryBuildProcessor.BuildingPlayer = false;
            AddressableAssetSettingsDefaultObject.Settings.ActivePlayerDataBuilderIndex = PlayAssetDeliveryBuildProcessor.DataBuilderIndex;

            // Add TC files which remain in Addressables.BuildPath (groups with delivery type set to "None") to Streaming Assets
            if (TextureCompressionProcessor.EnabledTextureCompressionTargeting)
            {
                foreach (var textureCompression in PlayerSettings.Android.textureCompressionFormats)
                {
                    var postfix = TextureCompressionProcessor.TcfPostfix(textureCompression);
                    var sourcePath = $"{Addressables.BuildPath}{postfix}";
                    if (!Directory.Exists(sourcePath))
                    {
                        // using default texture compression variant
                        sourcePath = Addressables.BuildPath;
                    }
                    buildPlayerContext.AddAdditionalPathToStreamingAssets(sourcePath, $"{Addressables.StreamingAssetsSubFolder}{postfix}");
                }
            }
        }

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            if (!EditorUserBuildSettings.buildAppBundle)
            {
                return;
            }

            // TODO what happens if there are errors during build process, how can we move data back?
            PlayAssetDeliveryBuildProcessor.MoveDataAfterAppBundleBuild();

            // TODO Recheck asset pack sizes here
        }
    }
}
