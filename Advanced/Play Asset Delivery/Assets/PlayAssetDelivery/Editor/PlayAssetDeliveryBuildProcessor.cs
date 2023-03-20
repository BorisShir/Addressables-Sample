using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Callbacks;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace AddressablesPlayAssetDelivery.Editor
{
    /// <summary>
    /// Moves custom asset pack data from their default build location <see cref="BuildScriptPlayAssetDelivery"/> to their correct player build data location.
    /// For an Android App Bundle, bundles assigned to a custom asset pack must be located in their {asset pack name}.androidpack directory in the Assets folder.
    /// The 'CustomAssetPacksData.json' file is also added to the built player's StreamingAssets file location.
    ///
    /// This script executes before the <see cref="AddressablesPlayerBuildProcessor"/> which moves all Addressables data to StreamingAssets.
    public class PlayAssetDeliveryBuildProcessor : BuildPlayerProcessor
    {
        /// <summary>
        /// Returns the player build processor callback order.
        /// </summary>
        public override int callbackOrder
        {
            get { return 0; }
        }

        /// <summary>
        /// Invoked before performing a Player build. Moves AssetBundles to their correct data location based on the build target platform.
        /// </summary>
        /// <param name="buildPlayerContext">Contains data related to the player.</param>
        public override void PrepareForBuild(BuildPlayerContext buildPlayerContext)
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android && EditorUserBuildSettings.buildAppBundle)
                MoveDataForAppBundleBuild(buildPlayerContext);
            else
                MoveDataToDefaultLocation();
        }

        static void MoveTextureCompressionData(BuildPlayerContext buildPlayerContext, string postfix)
        {
            // need to check that data for specific texture compression exist (player settings might change)
            var customAssetPackDataPath = Path.Combine($"{Addressables.StreamingAssetsSubFolder}{postfix}", CustomAssetPackUtility.kCustomAssetPackDataFilename);
            Debug.Log($"Adding {customAssetPackDataPath}");
            buildPlayerContext.AddAdditionalPathToStreamingAssets(Path.Combine(CustomAssetPackUtility.BuildRootDirectory, customAssetPackDataPath), customAssetPackDataPath);

            var buildProcessorDataPath = Path.Combine(CustomAssetPackUtility.BuildRootDirectory, $"{Addressables.StreamingAssetsSubFolder}{postfix}", CustomAssetPackUtility.kBuildProcessorDataFilename);
            Debug.Log($"Looking for {buildProcessorDataPath}");
            if (File.Exists(buildProcessorDataPath))
            {
                string contents = File.ReadAllText(buildProcessorDataPath);
                var data =  JsonUtility.FromJson<BuildProcessorData>(contents);

                foreach (BuildProcessorDataEntry entry in data.Entries)
                {
                    string assetsFolderPath = Path.Combine(CustomAssetPackUtility.PackContentRootDirectory, entry.AssetsSubfolderPath);
                    Debug.Log($"Moving from {entry.BundleBuildPath} to {assetsFolderPath}");
                    if (File.Exists(entry.BundleBuildPath))
                    {
                        if (string.IsNullOrEmpty(postfix))
                        {
                            File.Copy(entry.BundleBuildPath, assetsFolderPath);
                        }
                        else
                        {
                            string metaFilePath = AssetDatabase.GetTextMetaFilePathFromAssetPath(entry.BundleBuildPath);
                            File.Move(entry.BundleBuildPath, assetsFolderPath);
                            File.Delete(metaFilePath);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Move custom asset pack data from their build location to their App Bundle data location.
        /// </summary>
        /// <param name="buildPlayerContext">Contains data related to the player.</param>
        public static void MoveDataForAppBundleBuild(BuildPlayerContext buildPlayerContext)
        {
            try
            {
                AssetDatabase.StartAssetEditing();

                MoveTextureCompressionData(buildPlayerContext, "");
                if (TextureCompressionProcessor.EnabledTextureCompressionTargeting)
                {
                    foreach (var textureCompression in PlayerSettings.Android.textureCompressionFormats)
                    {
                        var postfix = TextureCompressionProcessor.TcfPostfix(textureCompression);
                        MoveTextureCompressionData(buildPlayerContext, postfix);
                        buildPlayerContext.AddAdditionalPathToStreamingAssets($"{Addressables.BuildPath}{postfix}", $"{Addressables.StreamingAssetsSubFolder}{postfix}");
                    }
                    buildPlayerContext.AddAdditionalPathToStreamingAssets($"{Addressables.BuildPath}{TextureCompressionProcessor.TcfPostfix(PlayerSettings.Android.textureCompressionFormats[0])}", Addressables.StreamingAssetsSubFolder);
                }
                else
                {
                    buildPlayerContext.AddAdditionalPathToStreamingAssets(Addressables.BuildPath, Addressables.StreamingAssetsSubFolder);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Exception occured when moving data for an app bundle build: {e.Message}.");
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
                    string contents = File.ReadAllText(CustomAssetPackUtility.BuildProcessorDataPath);
                    var data =  JsonUtility.FromJson<BuildProcessorData>(contents);

                    foreach (BuildProcessorDataEntry entry in data.Entries)
                    {
                        string assetsFolderPath = Path.Combine(CustomAssetPackUtility.PackContentRootDirectory, entry.AssetsSubfolderPath);
                        if (File.Exists(assetsFolderPath))
                        {
                            string metaFilePath = AssetDatabase.GetTextMetaFilePathFromAssetPath(assetsFolderPath);
                            File.Move(assetsFolderPath, entry.BundleBuildPath);
                            File.Delete(metaFilePath);
                        }
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
}
