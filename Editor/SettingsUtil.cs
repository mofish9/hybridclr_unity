using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using HybridCLR.Editor.Settings;


namespace HybridCLR.Editor
{
    public static class SettingsUtil
    {
        public static bool Enable
        { 
            get => HybridCLRSettings.Instance.enable;
            set 
            {
                HybridCLRSettings.Instance.enable = value;
                HybridCLRSettings.Save();
            }
        }

        public static string PackageName { get; } = "com.code-philosophy.hybridclr";

        /// <summary>
        /// Resolves the embedded package directory while preserving Unity's
        /// optional version suffix (for example @8.13.0).
        /// </summary>
        public static string PackagePathInProject
        {
            get
            {
                string packagesRoot = Path.Combine(ProjectDir, "Packages");
                string unversionedPath = Path.Combine(packagesRoot, PackageName);
                if (Directory.Exists(unversionedPath))
                {
                    return ("Packages/" + PackageName).Replace('\\', '/');
                }

                string[] versionedPaths = Directory.Exists(packagesRoot)
                    ? Directory.GetDirectories(packagesRoot, PackageName + "@*")
                    : Array.Empty<string>();
                if (versionedPaths.Length == 1)
                {
                    return ("Packages/" + Path.GetFileName(versionedPaths[0])).Replace('\\', '/');
                }
                if (versionedPaths.Length > 1)
                {
                    throw new InvalidOperationException(
                        "Multiple embedded HybridCLR package directories were found under " + packagesRoot + ".");
                }
                return ("Packages/" + PackageName).Replace('\\', '/');
            }
        }

        public static string HybridCLRDataPathInPackage => $"{PackagePathInProject}/Data~";

        public static string TemplatePathInPackage => $"{HybridCLRDataPathInPackage}/Templates";

        public static string ProjectDir { get; } = Directory.GetParent(Application.dataPath).ToString();

        public static string ScriptingAssembliesJsonFile { get; } = "ScriptingAssemblies.json";

        public static string HotUpdateDllsRootOutputDir => HybridCLRSettings.Instance.hotUpdateDllCompileOutputRootDir;

        public static string AssembliesPostIl2CppStripDir => HybridCLRSettings.Instance.strippedAOTDllOutputRootDir;

        public static string HybridCLRDataDir => $"{ProjectDir}/HybridCLRData";

        public static string LocalUnityDataDir => $"{HybridCLRDataDir}/LocalIl2CppData-{Application.platform}";

        public static string LocalIl2CppDir => $"{LocalUnityDataDir}/il2cpp";

        public static string GeneratedCppDir => $"{LocalIl2CppDir}/libil2cpp/hybridclr/generated";

        public static string Il2CppBuildCacheDir { get; } = $"{ProjectDir}/Library/Il2cppBuildCache";

        public static string GlobalgamemanagersBinFile { get; } = "globalgamemanagers";

        public static string Dataunity3dBinFile { get; } = "data.unity3d";

        public static string GetHotUpdateDllsOutputDirByTarget(BuildTarget target)
        {
            return $"{HotUpdateDllsRootOutputDir}/{target}";
        }

        public static string GetAssembliesPostIl2CppStripDir(BuildTarget target)
        {
            return $"{AssembliesPostIl2CppStripDir}/{target}";
        }

        class AssemblyDefinitionData
        {
            public string name;
        }

        public static List<string> HotUpdateAssemblyNamesExcludePreserved
        {
            get
            {
                var gs = HybridCLRSettings.Instance;
                var hotfixAssNames = (gs.hotUpdateAssemblyDefinitions ?? Array.Empty<AssemblyDefinitionAsset>()).Select(ad => JsonUtility.FromJson<AssemblyDefinitionData>(ad.text));

                var hotfixAssembles = new List<string>();
                foreach (var assName in hotfixAssNames)
                {
                    hotfixAssembles.Add(assName.name);
                }
                hotfixAssembles.AddRange(gs.hotUpdateAssemblies ?? Array.Empty<string>());
                return hotfixAssembles.ToList();
            }
        }

        public static List<string> DheAotAssemblyNames
        {
            get
            {
                // DHE is an explicit build feature. An empty list preserves
                // the legacy HybridCLR filtering behavior; project workflows
                // that opt into DHE must provide the exact intended assembly
                // set and validate it before building a Player.
                var configured = HybridCLRSettings.Instance.dheAotAssemblies;
                return configured == null ? new List<string>() : configured.ToList();
            }
        }

        public static List<string> HotUpdateAssemblyFilesExcludePreserved => HotUpdateAssemblyNamesExcludePreserved.Select(dll => dll + ".dll").ToList();


        public static List<string> HotUpdateAssemblyNamesIncludePreserved
        {
            get
            {
                List<string> allAsses = HotUpdateAssemblyNamesExcludePreserved;
                string[] preserveAssemblyNames = HybridCLRSettings.Instance.preserveHotUpdateAssemblies;
                if (preserveAssemblyNames != null && preserveAssemblyNames.Length > 0)
                {
                    foreach (var assemblyName in preserveAssemblyNames)
                    {
                        if (allAsses.Contains(assemblyName))
                        {
                            throw new Exception($"[HotUpdateAssemblyNamesIncludePreserved] assembly:'{assemblyName}' is duplicated");
                        }
                        allAsses.Add(assemblyName);
                    }
                }

                return allAsses;
            }
        }

        public static List<string> HotUpdateAssemblyFilesIncludePreserved => HotUpdateAssemblyNamesIncludePreserved.Select(ass => ass + ".dll").ToList();

        public static List<string> AOTAssemblyNames => HybridCLRSettings.Instance.patchAOTAssemblies.ToList();

        public static HybridCLRSettings HybridCLRSettings => HybridCLRSettings.Instance;
    }
}
