using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HybridCLR.Editor.BuildProcessors
{
    // Deferred components may be created after loading Current. What cannot be
    // deferred is a script identity serialized into the Base Player itself.
    internal sealed class AotModeAssetValidator : IProcessSceneWithReport, IPreprocessBuildWithReport
    {
        public int callbackOrder => 10;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (!Enabled) return;
            Validate(EditorUtility.CollectDependencies(scene.GetRootGameObjects()), scene.path);
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (!Enabled) return;
            Validate(EditorUtility.CollectDependencies(PlayerSettings.GetPreloadedAssets()), "Player preloaded assets");
            foreach (string path in AssetDatabase.GetAllAssetPaths())
                if (path.IndexOf("/Resources/", StringComparison.OrdinalIgnoreCase) >= 0 && !AssetDatabase.IsValidFolder(path))
                    Validate(EditorUtility.CollectDependencies(AssetDatabase.LoadAllAssetsAtPath(path)), path);
        }

        static bool Enabled => Settings.HybridCLRSettings.Instance.enable &&
            Settings.HybridCLRSettings.Instance.enableAotModeSelection;

        static void Validate(UnityEngine.Object[] objects, string source)
        {
            var deferred = new HashSet<string>(SettingsUtil.DheAotAssemblyNames, StringComparer.OrdinalIgnoreCase);
            foreach (var value in objects)
            {
                if (value == null) continue;
                if (deferred.Contains(value.GetType().Assembly.GetName().Name))
                    throw new BuildFailedException($"AOT mode selection: '{source}' serializes deferred hotfix object '{value.name}'. Keep Base assets in ordinary AOT, and load hotfix assets after selecting and loading Current.");
                // SerializeReference can store a hotfix object behind an AOT
                // field typed as object/interface, without a static reference.
                if (!(value is MonoBehaviour) && !(value is ScriptableObject)) continue;
                using (var serialized = new SerializedObject(value))
                {
                    var property = serialized.GetIterator();
                    while (property.Next(true))
                        if (property.propertyType == SerializedPropertyType.ManagedReference)
                        {
                            string fullName = property.managedReferenceFullTypename ?? string.Empty;
                            int separator = fullName.IndexOf(' ');
                            if (separator > 0 && deferred.Contains(fullName.Substring(0, separator)))
                                throw new BuildFailedException($"AOT mode selection: '{source}' contains deferred managed reference '{fullName}'. Load this asset after Current.");
                        }
                }
            }
        }
    }
}
