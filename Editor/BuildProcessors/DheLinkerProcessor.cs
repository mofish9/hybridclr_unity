using System.IO;
using System.Linq;
using HybridCLR.Editor.Link;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;
using UnityEngine;

namespace HybridCLR.Editor.BuildProcessors
{
    public sealed class DheLinkerProcessor : IUnityLinkerProcessor
    {
        public int callbackOrder => 0;

        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            if (!SettingsUtil.Enable || SettingsUtil.DheAotAssemblyNames.Count == 0) return null;
            string output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library",
                "HybridCLR", "DHE", data.target.ToString(), "link.xml"));
            // Bee invokes this while constructing its graph, before it copies
            // DLLs into data.inputDirectory. Match its PlayerBuildConfig inputs.
#if UNITY_2022_2_OR_NEWER
            BuildFile[] files = report.GetFiles();
#else
            BuildFile[] files = report.files;
#endif
            string[] inputs = files.Where(file => file.role == "ManagedLibrary" ||
                file.role == "DependentManagedLibrary" || file.role == "ManagedEngineAPI")
                .Select(file => file.path).GroupBy(Path.GetFileName).Select(group => group.First()).ToArray();
            DheLinkerPreservation.Write(inputs, SettingsUtil.DheAotAssemblyNames, output,
                SettingsUtil.HybridCLRSettings.dhePreserveAotAssemblies);
            Debug.Log("[HybridCLR DHE] Preserved Base assemblies and resolved external types: " + output);
            return output;
        }
    }
}
