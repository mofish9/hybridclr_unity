using System;
using System.IO;
using System.Linq;
using HybridCLR.Editor.Commands;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace __DHE_NAMESPACE__
{
    /// <summary>
    /// Project-owned DHE adapter. Cross-platform workflow and identity state
    /// are implemented by the HybridCLR package; customize only resource,
    /// signing, Player output, and smoke callbacks here.
    /// </summary>
    public static class DheWorkflowBuild
    {
        private const string RuntimeAssetRoot = "Assets/StreamingAssets/HybridCLR/DHE";

        public static void Prepare() => DheProjectWorkflowRunner.Prepare(CreateAdapter());

        public static void StageRuntimePlan() =>
            DheProjectWorkflowRunner.StageRuntimePlan(CreateAdapter());

        public static void BuildScriptsOnly() =>
            DheProjectWorkflowRunner.BuildScriptsOnly(CreateAdapter());

        public static void BuildFinalPlayer() =>
            DheProjectWorkflowRunner.BuildFinalPlayer(CreateAdapter());

        public static void BuildDheYooAsset()
        {
            throw new BuildFailedException(
                "Implement the project's resource build and write verified " +
                "adapter/resource-evidence.json before running the DHE Player workflow.");
        }

        private static DheProjectWorkflowAdapter CreateAdapter()
        {
            return new DheProjectWorkflowAdapter
            {
                ProjectRoot = ProjectRoot(),
                Workflow = "dhe-opt4",
                BuildIdentityAssetPath = "Assets/HybridCLRGenerated/DheBuildIdentity.cs",
                IdentityNamespace = "__DHE_IDENTITY_NAMESPACE__",
                IdentityClassName = "DheBuildIdentity",
                RuntimeAssetRoot = RuntimeAssetRoot,
                GetScenes = () => EditorBuildSettings.scenes.Where(scene => scene.enabled)
                    .Select(scene => scene.path).ToArray(),
                BuildPlayer = options => BuildPipeline.BuildPlayer(options),
                ResolvePlayerOutput = ResolvePlayerOutput,
            };
        }

        private static string ResolvePlayerOutput(BuildTarget target, string outputRoot)
        {
            string playerRoot = Path.Combine(outputRoot, "player");
            switch (target)
            {
                case BuildTarget.Android:
                    return Path.Combine(playerRoot, PlayerSettings.productName + ".apk");
                case BuildTarget.iOS:
                    return Path.Combine(playerRoot, PlayerSettings.productName + "-iOS");
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                    return Path.Combine(playerRoot, PlayerSettings.productName + ".exe");
                default:
                    throw new BuildFailedException("DHE adapter has no Player output for " + target + ".");
            }
        }

        private static string ProjectRoot() => Directory.GetParent(Application.dataPath).FullName;

        private static string Argument(string name)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, name);
            if (index < 0 || index + 1 >= arguments.Length)
                throw new BuildFailedException("Missing Unity argument: " + name);
            return arguments[index + 1];
        }
    }
}
