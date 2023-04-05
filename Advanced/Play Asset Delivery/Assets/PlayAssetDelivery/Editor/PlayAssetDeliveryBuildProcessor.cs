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
            // making this 2 helps to execute PrepareForBuild after Addressables are built, but causes problems with building project
            get { return 0; }
        }

        /// <summary>
        /// Invoked before performing a Player build. Moves AssetBundles to their correct data location based on the build target platform.
        /// </summary>
        /// <param name="buildPlayerContext">Contains data related to the player.</param>
        public override void PrepareForBuild(BuildPlayerContext buildPlayerContext)
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android && EditorUserBuildSettings.buildAppBundle && TextureCompressionProcessor.EnabledTextureCompressionTargeting)
            {
                foreach (var textureCompression in PlayerSettings.Android.textureCompressionFormats)
                {
                    var postfixDst = TextureCompressionProcessor.TcfPostfix(textureCompression);
                    var postfixSrc = textureCompression == PlayerSettings.Android.textureCompressionFormats[0] ? "" : postfixDst;
                    buildPlayerContext.AddAdditionalPathToStreamingAssets($"{Addressables.BuildPath}{postfixSrc}", $"{Addressables.StreamingAssetsSubFolder}{postfixDst}");
                }
            }
        }

        static void MoveTextureCompressionData(string projectPath, string postfix)
        {
            var buildProcessorDataPath = Path.Combine(Path.GetDirectoryName(Addressables.BuildPath), "BuildProcessorData", $"BuildProcessorData{postfix}.json");
            if (File.Exists(buildProcessorDataPath))
            {
                string contents = File.ReadAllText(buildProcessorDataPath);
                var data =  JsonUtility.FromJson<BuildProcessorData>(contents);

                foreach (BuildProcessorDataEntry entry in data.Entries)
                {
                    string assetsTargetPath = Path.Combine(projectPath, entry.AssetsSubfolderPath);
                    string assetsSourcePath = Path.Combine(projectPath, entry.BundleBuildPath);

                    if (File.Exists(assetsSourcePath))
                    {
                        Debug.Log($"Moving from {assetsSourcePath} to {assetsTargetPath}");
                        File.Move(assetsSourcePath, assetsTargetPath);
                    }
                }
            }
        }

        const string kUnityAssetPackTextureCompressions = "UnityTextureCompressionsAssetPack";
        const string kUnityAssetPackStreamingAssets = "UnityStreamingAssetsPack";

        void MoveInstallTimeData(string fromPath, string toPath, string postfix)
        {
            if (Directory.Exists($"{toPath}{postfix}"))
            {
                Directory.Delete($"{toPath}{postfix}", true);
            }
            Directory.Move($"{fromPath}{postfix}", $"{toPath}{postfix}");
        }

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android || !EditorUserBuildSettings.buildAppBundle)
            {
                return;
            }

            // We get path to unityLibrary, move above
            var gradleProjectPath = Path.GetFullPath(Path.Combine(path, ".."));

            // Clean all asset pack folders created from 'Assets/PlayAssetDelivery/Build'
            foreach (var dir in Directory.EnumerateDirectories(CustomAssetPackUtility.PackContentRootDirectory, "*.androidpack"))
            {
                var packName = Path.GetFileName(dir)[0..^12];
            }

            MoveTextureCompressionData(gradleProjectPath, "");
            if (TextureCompressionProcessor.EnabledTextureCompressionTargeting)
            {
                foreach (var textureCompression in PlayerSettings.Android.textureCompressionFormats)
                {
                    var postfix = TextureCompressionProcessor.TcfPostfix(textureCompression);
                    MoveTextureCompressionData(gradleProjectPath, postfix);
                }

                // Move all install-time addressables data to UnityAssetPackTextureCompressions as UnityAssetPackStreamingAssets might be not install-time
                var addressablesStreamingResourcesPath = Path.Combine(gradleProjectPath, kUnityAssetPackStreamingAssets, CustomAssetPackUtility.CustomAssetPacksAssetsPath);
                var addressablesResourcesPath = Path.Combine(gradleProjectPath, kUnityAssetPackTextureCompressions, CustomAssetPackUtility.CustomAssetPacksAssetsPath);

                MoveInstallTimeData(addressablesStreamingResourcesPath, addressablesResourcesPath, "");
                foreach (var textureCompression in PlayerSettings.Android.textureCompressionFormats)
                {
                    MoveInstallTimeData(addressablesStreamingResourcesPath, addressablesResourcesPath, TextureCompressionProcessor.TcfPostfix(textureCompression));
                }

                // Check if UnityAssetPackStreamingAssets is empty after moving data from it and if it is, then delete its mentions from gradle files
                var remain = Directory.GetFiles(Path.Combine(gradleProjectPath, kUnityAssetPackStreamingAssets), "*", SearchOption.AllDirectories);
                if (remain.Length == 1 && remain[0].EndsWith("build.gradle"))
                {
                    // only build.gradle file remain
                    Directory.Delete(Path.Combine(gradleProjectPath, kUnityAssetPackStreamingAssets), true);
                    var settingsGradle = File.ReadAllText(Path.Combine(gradleProjectPath, "settings.gradle"));
                    File.WriteAllText(Path.Combine(gradleProjectPath, "settings.gradle"), settingsGradle.Replace("include ':UnityStreamingAssetsPack'", ""));
                    var launcherGradle = File.ReadAllText(Path.Combine(gradleProjectPath, "launcher/build.gradle"));
                    File.WriteAllText(Path.Combine(gradleProjectPath, "launcher/build.gradle"), launcherGradle.Replace(", \":UnityStreamingAssetsPack\"", ""));
                }
            }
            else
            {
                // check where are our data UnityAssetPackStreamingAssets or UnityDataAssetPack
                // check if this pack is install-time, if yes - OK
                // if not - move our data to UnityLibrary (dev wants them to be install-time)
                // check if pack is empty after moving
            }
            // Try to remove stub files

            // recheck asset pack sizes here
        }
    }
}
