using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Callbacks;
using UnityEditor.Android;
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
    public class PlayAssetDeliveryBuildProcessor : BuildPlayerProcessor, IPostGenerateGradleAndroidProject
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
            buildPlayerContext.AddAdditionalPathToStreamingAssets(Path.Combine(CustomAssetPackUtility.BuildRootDirectory, customAssetPackDataPath), customAssetPackDataPath);

            var buildProcessorDataPath = Path.Combine(CustomAssetPackUtility.BuildRootDirectory, $"{Addressables.StreamingAssetsSubFolder}{postfix}", CustomAssetPackUtility.kBuildProcessorDataFilename);
            if (File.Exists(buildProcessorDataPath))
            {
                string contents = File.ReadAllText(buildProcessorDataPath);
                var data =  JsonUtility.FromJson<BuildProcessorData>(contents);

                foreach (BuildProcessorDataEntry entry in data.Entries)
                {
                    string assetsFolderPath = Path.Combine(CustomAssetPackUtility.PackContentRootDirectory, entry.AssetsSubfolderPath);
                    if (File.Exists(entry.BundleBuildPath))
                    {
                        if (TextureCompressionProcessor.EnabledTextureCompressionTargeting && string.IsNullOrEmpty(postfix))
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
                        var postfixDst = TextureCompressionProcessor.TcfPostfix(textureCompression);
                        var postfixSrc = textureCompression == PlayerSettings.Android.textureCompressionFormats[0] ? "" : postfixDst;
                        MoveTextureCompressionData(buildPlayerContext, postfixDst);
                        buildPlayerContext.AddAdditionalPathToStreamingAssets($"{Addressables.BuildPath}{postfixSrc}", $"{Addressables.StreamingAssetsSubFolder}{postfixDst}");
                    }
                }
                buildPlayerContext.AddAdditionalPathToStreamingAssets(Addressables.BuildPath, Addressables.StreamingAssetsSubFolder);
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
        // need to check this method
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

        const string kUnityAssetPackTextureCompressions = "UnityTextureCompressionsAssetPack";
        const string kUnityAssetPackStreamingAssets = "UnityStreamingAssetsPack";

        void MoveAddressablesData(string fromPath, string toPath, string postfix)
        {
            if (Directory.Exists($"{toPath}{postfix}"))
            {
                Directory.Delete($"{toPath}{postfix}", true);
            }
            Directory.Move($"{fromPath}{postfix}", $"{toPath}{postfix}");
        }

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            if (TextureCompressionProcessor.EnabledTextureCompressionTargeting)
            {
                // We get path to unityLibrary, move above
                var gradleProjectPath = Path.GetFullPath(Path.Combine(path, ".."));

                // Move all install-time addressables data to UnityAssetPackTextureCompressions as UnityAssetPackStreamingAssets might be not install-time
                var addressablesStreamingResourcesPath = Path.Combine(gradleProjectPath, kUnityAssetPackStreamingAssets, CustomAssetPackUtility.CustomAssetPacksAssetsPath);
                var addressablesResourcesPath = Path.Combine(gradleProjectPath, kUnityAssetPackTextureCompressions, CustomAssetPackUtility.CustomAssetPacksAssetsPath);

                MoveAddressablesData(addressablesStreamingResourcesPath, addressablesResourcesPath, "");
                foreach (var textureCompression in PlayerSettings.Android.textureCompressionFormats)
                {
                    MoveAddressablesData(addressablesStreamingResourcesPath, addressablesResourcesPath, TextureCompressionProcessor.TcfPostfix(textureCompression));
                }

                // Check if UnityAssetPackStreamingAssets is empty after moving data from it and if it is, then delete its mentions from gradle files
                var remain = Directory.GetFiles(Path.Combine(gradleProjectPath, kUnityAssetPackStreamingAssets), "*", SearchOption.AllDirectories);
                if (remain.Length == 1 && remain[0].EndsWith("build.gradle"))
                {
                    Directory.Delete(Path.Combine(gradleProjectPath, kUnityAssetPackStreamingAssets), true);
                    var settingsGradle = File.ReadAllText(Path.Combine(gradleProjectPath, "settings.gradle"));
                    File.WriteAllText(Path.Combine(gradleProjectPath, "settings.gradle"), settingsGradle.Replace("include ':UnityStreamingAssetsPack'", ""));
                    var launcherGradle = File.ReadAllText(Path.Combine(gradleProjectPath, "launcher/build.gradle"));
                    File.WriteAllText(Path.Combine(gradleProjectPath, "launcher/build.gradle"), launcherGradle.Replace(", \":UnityStreamingAssetsPack\"", ""));
                }
            }
            // Probably recheck asset pack sizes here
        }
    }
}
