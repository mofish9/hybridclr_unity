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
#if UNITY_2022_2_OR_NEWER
            // Newer Bee invokes this before populating staging. Match the
            // PlayerBuildConfig input list, including framework dependencies.
            string[] inputs = report.GetFiles().Where(file => file.role == "ManagedLibrary" ||
                file.role == "DependentManagedLibrary" || file.role == "ManagedEngineAPI")
                .Select(file => file.path).GroupBy(Path.GetFileName).Select(group => group.First()).ToArray();
#else
            // Unity 2021 supplies the prepared inputs here; its BuildReport
            // has not recorded the complete managed input set at this point.
            string[] inputs = Directory.GetFiles(data.inputDirectory, "*.dll");
#endif
            DheLinkerPreservation.Write(inputs, SettingsUtil.DheAotAssemblyNames, output,
                SettingsUtil.HybridCLRSettings.dhePreserveAotAssemblies);
            Debug.Log("[HybridCLR DHE] Preserved Base assemblies and resolved external types: " + output);
            return output;
        }
    }
}
