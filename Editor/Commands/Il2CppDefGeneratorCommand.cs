using HybridCLR.Editor.Link;
using HybridCLR.Editor.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace HybridCLR.Editor.Commands
{

    public static class Il2CppDefGeneratorCommand
    {

        [MenuItem("HybridCLR/Generate/Il2CppDef", priority = 104)]
        public static void GenerateIl2CppDef()
        {
            if (HybridCLRSettings.Instance.enableAotModeSelection &&
                !System.IO.File.Exists($"{SettingsUtil.LocalIl2CppDir}/libil2cpp/hybridclr/AotModeConfig.h"))
                throw new InvalidOperationException("AOT mode selection requires the matching HybridCLR and il2cpp_plus candidate runtimes.");
            var options = new Il2CppDef.Il2CppDefGenerator.Options()
            {
                UnityVersion = Application.unityVersion,
                HotUpdateAssemblies = SettingsUtil.HotUpdateAssemblyNamesIncludePreserved,
                EnableAotModeSelection = HybridCLRSettings.Instance.enableAotModeSelection,
                DeferredAssemblies = SettingsUtil.DheAotAssemblyNames,
                UnityVersionTemplateFile = $"{SettingsUtil.TemplatePathInPackage}/UnityVersion.h.tpl",
                UnityVersionOutputFile = $"{SettingsUtil.LocalIl2CppDir}/libil2cpp/hybridclr/generated/UnityVersion.h",
                AssemblyManifestTemplateFile = $"{SettingsUtil.TemplatePathInPackage}/AssemblyManifest.cpp.tpl",
                AssemblyManifestOutputFile = $"{SettingsUtil.LocalIl2CppDir}/libil2cpp/hybridclr/generated/AssemblyManifest.cpp",
            };

            var g = new Il2CppDef.Il2CppDefGenerator(options);
            g.Generate();
        }
    }
}
