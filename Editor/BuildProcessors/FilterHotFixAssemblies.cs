using HybridCLR.Editor.Meta;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace HybridCLR.Editor.BuildProcessors
{
    /// <summary>
    /// 将热更新dll从Build过程中过滤，防止打包到主工程中
    /// </summary>
    internal class FilterHotFixAssemblies : IFilterBuildAssemblies
    {
        private const string DheAotBaselineRootEnvironmentVariable = "HYBRIDCLR_DHE_AOT_BASELINE_ROOT";

        public int callbackOrder => 0;

        public string[] OnFilterAssemblies(BuildOptions buildOptions, string[] assemblies)
        {
            if (!SettingsUtil.Enable)
            {
                Debug.Log($"[FilterHotFixAssemblies] disabled");
                return assemblies;
            }
            List<string> allHotUpdateDllNames = SettingsUtil.HotUpdateAssemblyNamesExcludePreserved;
            List<string> dheAotDllNames = SettingsUtil.DheAotAssemblyNames;
            var allHotUpdateDllSet = new HashSet<string>(allHotUpdateDllNames, StringComparer.Ordinal);
            var dheAotDllSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dheAotDll in dheAotDllNames)
            {
                if (string.IsNullOrWhiteSpace(dheAotDll))
                {
                    throw new BuildFailedException("DHE AOT assembly name can't be empty");
                }
                if (!dheAotDllSet.Add(dheAotDll))
                {
                    throw new BuildFailedException($"DHE AOT assembly:{dheAotDll} is duplicated");
                }
                if (!allHotUpdateDllSet.Contains(dheAotDll))
                {
                    throw new BuildFailedException($"DHE AOT assembly:{dheAotDll} must also be listed in hotUpdateAssemblies");
                }
            }

            // 检查是否重复填写
            var hotUpdateDllSet = new HashSet<string>();
            foreach(var hotUpdateDll in allHotUpdateDllNames)
            {
                if (string.IsNullOrWhiteSpace(hotUpdateDll))
                {
                    throw new BuildFailedException($"hot update assembly name cann't be empty");
                }
                if (!hotUpdateDllSet.Add(hotUpdateDll))
                {
                    throw new BuildFailedException($"hot update assembly:{hotUpdateDll} is duplicated");
                }
            }

            var assResolver = MetaUtil.CreateHotUpdateAssemblyResolver(EditorUserBuildSettings.activeBuildTarget, allHotUpdateDllNames);
            // 检查是否填写了正确的dll名称
            foreach (var hotUpdateDllName in allHotUpdateDllNames)
            {
                if (assemblies.Select(Path.GetFileNameWithoutExtension).All(ass => ass != hotUpdateDllName) 
                    && string.IsNullOrEmpty(assResolver.ResolveAssembly(hotUpdateDllName, false)))
                {
                    throw new BuildFailedException($"hot update assembly:{hotUpdateDllName} doesn't exist");
                }
            }

            // Ordinary hot-update DLLs stay external. DHE assemblies are a
            // deliberate exception: their baseline image must be compiled
            // into the player while the current image is loaded at runtime.
            var assembliesToFilter = new HashSet<string>(
                allHotUpdateDllNames.Where(name => !dheAotDllSet.Contains(name)),
                StringComparer.Ordinal);

            // DHE assemblies are AOT inputs, but their AOT image must be the
            // previously shipped baseline rather than the current hot-update
            // compilation. The project workflow sets this process-scoped root
            // immediately before BuildPipeline.BuildPlayer. Keeping the
            // substitution here makes it work for every Unity build entry
            // point while leaving ordinary hot-update filtering unchanged.
            string baselineRoot = Environment.GetEnvironmentVariable(
                DheAotBaselineRootEnvironmentVariable);
            bool useBaseline = !string.IsNullOrWhiteSpace(baselineRoot);
            if (useBaseline)
            {
                baselineRoot = Path.GetFullPath(baselineRoot).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!Directory.Exists(baselineRoot))
                {
                    throw new BuildFailedException(
                        "DHE AOT baseline root was not found: " + baselineRoot);
                }
            }

            var result = new List<string>();
            foreach (string assemblyPath in assemblies)
            {
                string assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
                if (assembliesToFilter.Contains(assemblyName))
                {
                    Debug.Log($"[FilterHotFixAssemblies] filter assembly:{assemblyName}");
                    continue;
                }
                if (useBaseline && dheAotDllSet.Contains(assemblyName))
                {
                    // All current DHE paths are replaced below in a stable
                    // name-ordered pass. This also handles accidental duplicate
                    // current/plugin paths without leaking one into the Player.
                    continue;
                }
                result.Add(assemblyPath);
            }

            if (useBaseline)
            {
                foreach (string assemblyName in dheAotDllNames)
                {
                    string baselinePath = Path.GetFullPath(Path.Combine(
                        baselineRoot, assemblyName + ".dll"));
                    string baselinePrefix = baselineRoot + Path.DirectorySeparatorChar;
                    if (!baselinePath.StartsWith(baselinePrefix,
                            StringComparison.OrdinalIgnoreCase) ||
                        !File.Exists(baselinePath))
                    {
                        throw new BuildFailedException(
                            "DHE AOT baseline assembly was not found: " + baselinePath);
                    }
                    result.Add(baselinePath);
                    Debug.Log($"[FilterHotFixAssemblies] use DHE baseline assembly:{baselinePath}");
                }
            }
            else
            {
                foreach (string assemblyPath in assemblies)
                {
                    string assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
                    if (dheAotDllSet.Contains(assemblyName) &&
                        !result.Contains(assemblyPath))
                    {
                        result.Add(assemblyPath);
                    }
                }
            }
            return result.ToArray();
        }
    }
}
