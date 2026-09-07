using System.IO;
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
            DheLinkerPreservation.Write(data.inputDirectory, SettingsUtil.DheAotAssemblyNames, output,
                SettingsUtil.HybridCLRSettings.dhePreserveAotAssemblies);
            Debug.Log("[HybridCLR DHE] Preserved Base assemblies and resolved external types: " + output);
            return output;
        }
    }
}
