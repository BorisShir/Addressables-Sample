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
    ///
    /// This script executes before the <see cref="AddressablesPlayerBuildProcessor"/> which moves all Addressables data to StreamingAssets.
    public class PlayAssetDeliveryBuildProcessor : BuildPlayerProcessor, IPostprocessBuildWithReport
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
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                return;
            }
            // need to check that Addressables are built for Android
            if (EditorUserBuildSettings.buildAppBundle && (PlayerSettings.Android.splitApplicationBinary || TextureCompressionProcessor.EnabledTextureCompressionTargeting))
            {
                MoveDataForAppBundleBuild(true);
                BuildScriptPlayAssetDelivery.BuildingPlayer = true;
            }
            else
            {
                MoveDataToDefaultLocation();
            }
        }

        static void MoveTextureCompressionData(string postfix)
        {
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
                            File.Move(entry.BundleBuildPath, assetsFolderPath);
                            string metaFilePath = AssetDatabase.GetTextMetaFilePathFromAssetPath(entry.BundleBuildPath);
                            if (File.Exists(metaFilePath))
                            {
                                // metafile might exist only if BuildPath is not default "LocalBuildPath"
                                // and points to something inside Assets folder
                                File.Delete(metaFilePath);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Move custom asset pack data from their build location to their App Bundle data location.
        /// </summary>
        public static void MoveDataForAppBundleBuild(bool moveInstallTimeData)
        {
            try
            {
                AssetDatabase.StartAssetEditing();
                MoveTextureCompressionData("");
                if (TextureCompressionProcessor.EnabledTextureCompressionTargeting)
                {
                    foreach (var textureCompression in PlayerSettings.Android.textureCompressionFormats)
                    {
                        var postfix = TextureCompressionProcessor.TcfPostfix(textureCompression);
                        MoveTextureCompressionData(postfix);
                        var targetPath = Path.Combine(CustomAssetPackUtility.PackContentRootDirectory, "AddressablesAssetPack.androidpack", $"{CustomAssetPackUtility.CustomAssetPacksAssetsPath}{postfix}");
                        if (Directory.Exists($"{Addressables.BuildPath}{postfix}"))
                        {
                            Directory.Move($"{Addressables.BuildPath}{postfix}", targetPath);
                        }
                        else if (!Directory.Exists(targetPath))
                        {
                            FileUtil.CopyFileOrDirectory(Addressables.BuildPath, targetPath);
                        }
                        if (!File.Exists(Path.Combine(targetPath, CustomAssetPackUtility.kCustomAssetPackDataFilename)))
                            File.Copy(Path.Combine(CustomAssetPackUtility.BuildRootDirectory, $"{Addressables.StreamingAssetsSubFolder}{postfix}", CustomAssetPackUtility.kCustomAssetPackDataFilename),
                                Path.Combine(targetPath, CustomAssetPackUtility.kCustomAssetPackDataFilename));
                    }
                }
                if (moveInstallTimeData)
                {
                    var targetPath = Path.Combine(CustomAssetPackUtility.PackContentRootDirectory, "AddressablesAssetPack.androidpack", CustomAssetPackUtility.CustomAssetPacksAssetsPath);
                    if (!Directory.Exists(targetPath))
                    {
                        Directory.Move(Addressables.BuildPath, targetPath);
                    }
                    if (!File.Exists(Path.Combine(targetPath, CustomAssetPackUtility.kCustomAssetPackDataFilename)))
                        File.Copy(Path.Combine(CustomAssetPackUtility.BuildRootDirectory, Addressables.StreamingAssetsSubFolder, CustomAssetPackUtility.kCustomAssetPackDataFilename),
                            Path.Combine(targetPath, CustomAssetPackUtility.kCustomAssetPackDataFilename));
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
                        if (File.Exists(assetsFolderPath) && Directory.Exists(Path.GetDirectoryName(entry.BundleBuildPath)))
                        {
                            if (File.Exists(entry.BundleBuildPath))
                            {
                                File.Delete(entry.BundleBuildPath);
                            }
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

        [PostProcessBuildAttribute(1)]
        public static void OnPostprocessBuild(BuildTarget target, string pathToBuiltProject)
        {
            Debug.Log($"Build completed {pathToBuiltProject}");
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            Debug.Log("OnPostprocessBuild");
        }
    }

    /// <summary>
    /// Moves all install-time Addressables data and Addressables json files to StreamingAssets.
    /// This script executes after the <see cref="AddressablesPlayerBuildProcessor"/> which moves other Addressables data to StreamingAssets.
    public class PlayAssetDeliverySecondBuildProcessor : BuildPlayerProcessor, IPostGenerateGradleAndroidProject
    {
        /// <summary>
        /// Returns the player build processor callback order.
        /// </summary>
        public override int callbackOrder
        {
            get { return 2; }
        }

        /// <summary>
        /// Invoked before performing a Player build. Moves AssetBundles to their correct data location based on the build target platform.
        /// </summary>
        /// <param name="buildPlayerContext">Contains data related to the player.</param>
        public override void PrepareForBuild(BuildPlayerContext buildPlayerContext)
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android && EditorUserBuildSettings.buildAppBundle && 
                (PlayerSettings.Android.splitApplicationBinary || TextureCompressionProcessor.EnabledTextureCompressionTargeting))
            {
                //MoveInstallTimeDataToStreamingAssets(buildPlayerContext);
            }
            BuildScriptPlayAssetDelivery.BuildingPlayer = false;
        }

        static void MoveInstallTimeDataToStreamingAssets(BuildPlayerContext buildPlayerContext)
        {
            var customAssetPackDataPath = Path.Combine(Addressables.StreamingAssetsSubFolder, CustomAssetPackUtility.kCustomAssetPackDataFilename);
            buildPlayerContext.AddAdditionalPathToStreamingAssets(Path.Combine(CustomAssetPackUtility.BuildRootDirectory, customAssetPackDataPath), customAssetPackDataPath);

            if (TextureCompressionProcessor.EnabledTextureCompressionTargeting)
            {
                foreach (var textureCompression in PlayerSettings.Android.textureCompressionFormats)
                {
                    var postfixDst = TextureCompressionProcessor.TcfPostfix(textureCompression);
                    var postfixSrc = textureCompression == PlayerSettings.Android.textureCompressionFormats[0] ? "" : postfixDst;
                    // need to check that data for specific texture compression exist (player settings might change)
                    customAssetPackDataPath = Path.Combine($"{Addressables.StreamingAssetsSubFolder}{postfixDst}", CustomAssetPackUtility.kCustomAssetPackDataFilename);
                    buildPlayerContext.AddAdditionalPathToStreamingAssets(Path.Combine(CustomAssetPackUtility.BuildRootDirectory, customAssetPackDataPath), customAssetPackDataPath);
                    //buildPlayerContext.AddAdditionalPathToStreamingAssets($"{Addressables.BuildPath}{postfixSrc}", $"{Addressables.StreamingAssetsSubFolder}{postfixDst}");
                }
            }
        }

        // probably create separate class here
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

        void CheckStreamingAssetsPack(string gradleProjectPath)
        {
            // Check if UnityAssetPackStreamingAssets is empty after moving data from it and if it is, then delete its mentions from gradle files
            var streamingAssetPack = Path.Combine(gradleProjectPath, kUnityAssetPackStreamingAssets);
            foreach (var file in Directory.GetFiles(streamingAssetPack, "*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith("build.gradle") && !Path.GetRelativePath(streamingAssetPack, file).StartsWith("build"))
                {
                    return;
                }
            }
            Directory.Delete(Path.Combine(gradleProjectPath, kUnityAssetPackStreamingAssets), true);
            var settingsGradle = File.ReadAllText(Path.Combine(gradleProjectPath, "settings.gradle"));
            File.WriteAllText(Path.Combine(gradleProjectPath, "settings.gradle"), settingsGradle.Replace("include ':UnityStreamingAssetsPack'", ""));
            var launcherGradle = File.ReadAllText(Path.Combine(gradleProjectPath, "launcher/build.gradle"));
            File.WriteAllText(Path.Combine(gradleProjectPath, "launcher/build.gradle"), launcherGradle.Replace(", \":UnityStreamingAssetsPack\"", ""));
        }

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            /*if (!EditorUserBuildSettings.buildAppBundle)
            {
                return;
            }
            // We get path to unityLibrary, move above
            var gradleProjectPath = Path.GetFullPath(Path.Combine(path, ".."));
            var assetPacksUpdated = false;
            if (TextureCompressionProcessor.EnabledTextureCompressionTargeting)
            {
                // Move all install-time addressables data to UnityAssetPackTextureCompressions as UnityAssetPackStreamingAssets might be not install-time
                var addressablesStreamingResourcesPath = Path.Combine(gradleProjectPath, kUnityAssetPackStreamingAssets, CustomAssetPackUtility.CustomAssetPacksAssetsPath);
                var addressablesResourcesPath = Path.Combine(gradleProjectPath, kUnityAssetPackTextureCompressions, CustomAssetPackUtility.CustomAssetPacksAssetsPath);

                MoveAddressablesData(addressablesStreamingResourcesPath, addressablesResourcesPath, "");
                foreach (var textureCompression in PlayerSettings.Android.textureCompressionFormats)
                {
                    MoveAddressablesData(addressablesStreamingResourcesPath, addressablesResourcesPath, TextureCompressionProcessor.TcfPostfix(textureCompression));
                }

                CheckStreamingAssetsPack(gradleProjectPath);
                assetPacksUpdated = true;
            }
            else if (PlayerSettings.Android.splitApplicationBinary)
            {
                // Move all install-time addressables data to unityLibrary from UnityAssetPackStreamingAssets
                // If there is no UnityAssetPackStreamingAssets, this means that all data are packed to UnityDataAssetPack
                // and it's must be install-time (need to check)
                assetPacksUpdated = true;
            }
            if (assetPacksUpdated)
            {
                // Recheck asset pack sizes here
            }*/
        }
    }
}
