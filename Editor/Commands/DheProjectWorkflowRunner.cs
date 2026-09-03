using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace HybridCLR.Editor.Commands
{
    /// <summary>
    /// Unity-side workflow stages shared by every DHE project. A project keeps
    /// only its Player/resource/smoke callbacks in Assets.
    /// </summary>
    public static class DheProjectWorkflowRunner
    {
        public static void Prepare(DheProjectWorkflowAdapter adapter)
        {
            RequireAdapter(adapter);
            DheProjectWorkflowContext context = DheProjectWorkflowContext.FromCommandLine(false);
            context.EnsureTarget();
            string baselineSource = Environment.GetEnvironmentVariable("DHE_BASELINE_ROOT");
            DheProjectPrepareResult prepared = DheBuildPipeline.PrepareProjectArtifacts(
                new DheProjectPrepareOptions
                {
                    Target = context.Target,
                    Mode = context.Mode,
                    BaselineSourceRoot = string.IsNullOrWhiteSpace(baselineSource) ? null :
                        Path.GetFullPath(baselineSource),
                    BaselineOutputRoot = context.BaselineRoot,
                    CurrentOutputRoot = context.CurrentRoot,
                    Bootstrap = context.GetBooleanArgument("-dheBootstrap"),
                    RequireDheEqualsHotUpdate = true,
                });
            EnsureAssemblyRoot(SettingsUtil.GetHotUpdateDllsOutputDirByTarget(context.Target),
                prepared.HotUpdateAssemblyNames, "hot-update output");
            WriteJson(Path.Combine(context.OutputRoot, "adapter", "prepare.json"),
                new PrepareEvidence
                {
                    schemaVersion = 1,
                    format = "hybridclr.dhe-project-adapter-prepare.json",
                    generatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                    passed = true,
                    toolchainContractVersion = 1,
                    target = context.TargetName,
                    mode = context.Mode,
                    pathSemantics = "workspace-absolute-v1",
                    projectPath = adapter.ProjectRoot,
                    settingsFile = Path.Combine(adapter.ProjectRoot, "ProjectSettings",
                        "HybridCLRSettings.asset"),
                    baselineRoot = context.BaselineRoot,
                    currentRoot = context.CurrentRoot,
                    baselineSourceRoot = prepared.BaselineSourceRoot,
                    currentSourceRoot = prepared.CurrentSourceRoot,
                    runtimeAssemblySourceRoot = prepared.CurrentSourceRoot,
                    baselineGeneratedFromCurrent = prepared.BaselineGeneratedFromCurrent,
                    aotAssemblies = prepared.DheAotAssemblyNames,
                    hotUpdateAssemblies = prepared.HotUpdateAssemblyNames,
                });
            Debug.Log("DHE package workflow Prepare passed.");
        }

        public static void BuildScriptsOnly(DheProjectWorkflowAdapter adapter)
        {
            RequireAdapter(adapter);
            DheProjectWorkflowContext context = DheProjectWorkflowContext.FromCommandLine(true);
            context.EnsureTarget();
            BuildPlayer(adapter, context, BuildOptions.BuildScriptsOnly);
            DheProjectNativeOptions nativeOptions = CreateNativeOptions(adapter, context);
            DheNativeFinalizeResult result = DheBuildPipeline.FinalizeProjectNativeCode(
                DheProjectBuildSupport.CreateNativeFinalizeOptions(nativeOptions, false));
            DheProjectBuildSupport.WriteNativeEvidence(nativeOptions, result, false);
            DheProjectBuildSupport.StageBuildIdentity(CreateIdentityOptions(adapter, context), result);
        }

        public static void StageRuntimePlan(DheProjectWorkflowAdapter adapter)
        {
            RequireAdapter(adapter);
            DheProjectWorkflowContext context = DheProjectWorkflowContext.FromCommandLine(true);
            context.EnsureTarget();
            string runtimeAssetRoot = ResolveProjectPath(adapter.ProjectRoot,
                adapter.RuntimeAssetRoot);
            string currentAssemblyRoot = Path.GetFullPath(
                SettingsUtil.GetHotUpdateDllsOutputDirByTarget(context.Target));
            string fallbackRoot = context.GetArgument("-dheAotMetadataFallbackRoot");
            string fallbackManifest = context.GetArgument("-dheAotMetadataFallbackManifest");
            DheRuntimePlanResult result = DheBuildPipeline.StageRuntimePlan(
                new DheRuntimePlanOptions
                {
                    Target = context.Target,
                    ProjectRoot = adapter.ProjectRoot,
                    ProjectPlanPath = context.ProjectPlanPath,
                    RuntimeAssetRoot = runtimeAssetRoot,
                    OutputRoot = context.OutputRoot,
                    StrippedAotRoot = Path.GetFullPath(
                        SettingsUtil.GetAssembliesPostIl2CppStripDir(context.Target)),
                    AotMetadataFallbackRoot = string.IsNullOrWhiteSpace(fallbackRoot) ? null :
                        Path.GetFullPath(fallbackRoot),
                    AotMetadataFallbackManifestPath = string.IsNullOrWhiteSpace(fallbackManifest) ?
                        null : Path.GetFullPath(fallbackManifest),
                    AotMetadataFallbackExpectedTarget =
                        context.GetArgument("-dheAotMetadataExpectedTarget"),
                    AotMetadataFallbackExpectedStagedRuntimeSha256 =
                        context.GetArgument("-dheAotMetadataExpectedStagedRuntimeSha256"),
                    AotMetadataFallbackExpectedRuntimeManifestSha256 =
                        context.GetArgument("-dheAotMetadataExpectedRuntimeManifestSha256"),
                    AotMetadataFallbackExpectedPackageTreeSha256 =
                        context.GetArgument("-dheAotMetadataExpectedPackageTreeSha256"),
                    AotMetadataAssemblyNames = SettingsUtil.AOTAssemblyNames.ToArray(),
                    HotfixAssemblyNames = SettingsUtil.HotUpdateAssemblyNamesExcludePreserved.ToArray(),
                    RuntimeAssetPathResolver = path => ToProjectAssetPath(adapter.ProjectRoot, path),
                    CurrentAssemblyTransform = adapter.CurrentAssemblyTransform,
                    HotfixLoadOrderResolver = adapter.HotfixLoadOrderResolver == null ? null :
                        files => adapter.HotfixLoadOrderResolver(currentAssemblyRoot, files),
                    DependencyMapWriter = adapter.DependencyMapWriter == null ? null :
                        destination => adapter.DependencyMapWriter(currentAssemblyRoot, destination),
                });
            adapter.StageAdditionalRuntimeAssets?.Invoke(result.RuntimeAssetRoot);
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate |
                ImportAssetOptions.ForceSynchronousImport);
            Debug.Log("DHE package workflow runtime plan staged: " +
                result.AssemblyNames.Length + " assemblies; handoff=" + result.HandoffRoot);
        }

        public static void BuildFinalPlayer(DheProjectWorkflowAdapter adapter)
        {
            RequireAdapter(adapter);
            DheProjectWorkflowContext context = DheProjectWorkflowContext.FromCommandLine(true);
            context.EnsureTarget();
            DheProjectBuildSupport.ValidateStagedBuildIdentity(
                CreateIdentityOptions(adapter, context));
            try
            {
                DheNativeFinalizeResult result = BuildPlayer(adapter, context, BuildOptions.None);
                if (!DheProjectBuildSupport.FinalNativeIdentityMatches(context.OutputRoot, result,
                    out string identityError))
                {
                    Debug.LogWarning("DHE native identity changed during the final Player pass; " +
                        "settling the embedded identity and rebuilding once. " + identityError);
                    DheProjectBuildSupport.StageBuildIdentity(CreateIdentityOptions(adapter, context),
                        result);
                    result = BuildPlayer(adapter, context, BuildOptions.None);
                }
                DheProjectBuildSupport.ValidateFinalNativeIdentity(context.OutputRoot, result);
                DheProjectBuildSupport.WriteNativeEvidence(CreateNativeOptions(adapter, context),
                    result, true);
                adapter.RunPlayerSmoke?.Invoke(new DheProjectPlayerSmokeContext
                {
                    PlayerPath = ResolvePlayerOutput(adapter, context),
                    OutputRoot = context.OutputRoot,
                    Target = context.Target,
                    TargetName = context.TargetName,
                    NativeResult = result,
                });
            }
            finally
            {
                DheProjectBuildSupport.RestoreBuildIdentityTemplate(
                    CreateIdentityOptions(adapter, context));
            }
        }

        private static DheNativeFinalizeResult BuildPlayer(DheProjectWorkflowAdapter adapter,
            DheProjectWorkflowContext context, BuildOptions buildOptions)
        {
            DheBuildPipeline.ValidateAssemblyScope(true, out _, out string[] dheAssemblies);
            EnsureAssemblyRoot(context.BaselineRoot, dheAssemblies,
                "stripped AOT baseline for Player");
            bool scriptsOnly = (buildOptions & BuildOptions.BuildScriptsOnly) != 0;
            DheNativeFinalizeResult nativeResult = null;
            DheBuildPipeline.BuildPlayer(new DhePlayerBuildOptions
            {
                OutputPath = ResolvePlayerOutput(adapter, context),
                BaselineAotRoot = context.BaselineRoot,
                Target = context.Target,
                BuildOptions = buildOptions,
                CleanBuild = scriptsOnly,
                Scenes = adapter.GetScenes(),
                BuildPlayerCallback = adapter.BuildPlayer,
                NativeFinalizeOptions = scriptsOnly ? null :
                    DheProjectBuildSupport.CreateNativeFinalizeOptions(
                        CreateNativeOptions(adapter, context), true),
                NativeFinalizeResultCallback = result => nativeResult = result,
            });
            return nativeResult;
        }

        private static string ResolvePlayerOutput(DheProjectWorkflowAdapter adapter,
            DheProjectWorkflowContext context)
        {
            string configured = context.GetArgument("-dhePlayerOutput");
            return !string.IsNullOrWhiteSpace(configured) ? Path.GetFullPath(configured) :
                Path.GetFullPath(adapter.ResolvePlayerOutput(context.Target, context.OutputRoot));
        }

        private static DheProjectNativeOptions CreateNativeOptions(DheProjectWorkflowAdapter adapter,
            DheProjectWorkflowContext context)
        {
            return new DheProjectNativeOptions
            {
                ProjectRoot = adapter.ProjectRoot,
                ProjectPlanPath = context.ProjectPlanPath,
                OutputRoot = context.OutputRoot,
                Target = context.TargetName,
                GuardAllMethods = context.GetBooleanArgument("-dheBootstrap"),
            };
        }

        private static DheProjectIdentityOptions CreateIdentityOptions(
            DheProjectWorkflowAdapter adapter, DheProjectWorkflowContext context)
        {
            return new DheProjectIdentityOptions
            {
                ProjectRoot = adapter.ProjectRoot,
                OutputRoot = context.OutputRoot,
                BaselineRoot = context.BaselineRoot,
                ProjectPlanPath = context.ProjectPlanPath,
                Target = context.TargetName,
                Workflow = adapter.Workflow,
                BuildIdentityAssetPath = adapter.BuildIdentityAssetPath,
                RuntimePlanPath = Path.Combine(adapter.RuntimeAssetRoot,
                    "DheRuntimePlan.json"),
                BaseMetaVersionAssetRoot = Path.Combine(adapter.RuntimeAssetRoot,
                    "BaseMetaVersion"),
                IdentityNamespace = adapter.IdentityNamespace,
                IdentityClassName = adapter.IdentityClassName,
            };
        }

        private static void RequireAdapter(DheProjectWorkflowAdapter adapter)
        {
            if (adapter == null) throw new ArgumentNullException(nameof(adapter));
            if (string.IsNullOrWhiteSpace(adapter.ProjectRoot) || adapter.GetScenes == null ||
                adapter.BuildPlayer == null || adapter.ResolvePlayerOutput == null ||
                string.IsNullOrWhiteSpace(adapter.RuntimeAssetRoot) ||
                string.IsNullOrWhiteSpace(adapter.BuildIdentityAssetPath) ||
                string.IsNullOrWhiteSpace(adapter.IdentityNamespace) ||
                string.IsNullOrWhiteSpace(adapter.IdentityClassName))
                throw new BuildFailedException("DHE project workflow adapter is incomplete.");
        }

        private static void EnsureAssemblyRoot(string root, string[] names, string description)
        {
            string fullRoot = Path.GetFullPath(root);
            if (!Directory.Exists(fullRoot))
                throw new DirectoryNotFoundException("DHE " + description + " was not found: " +
                    fullRoot);
            foreach (string name in names ?? Array.Empty<string>())
            {
                string path = Path.Combine(fullRoot, name + ".dll");
                if (!File.Exists(path)) throw new FileNotFoundException(
                    "DHE " + description + " assembly was not found", path);
            }
        }

        private static string ResolveProjectPath(string projectRoot, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new BuildFailedException("DHE project adapter runtime asset root is empty.");
            return Path.GetFullPath(Path.IsPathRooted(path) ? path :
                Path.Combine(projectRoot, path));
        }

        private static string ToProjectAssetPath(string projectRoot, string path)
        {
            string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string resolved = Path.GetFullPath(path);
            if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException("DHE runtime asset is outside the project: " + resolved);
            string relative = resolved.Substring(root.Length).Replace('\\', '/');
            if (!relative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException("DHE runtime asset is outside Assets: " + resolved);
            return relative;
        }

        private static void WriteJson(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(value, true), new UTF8Encoding(false));
        }

        [Serializable]
        private sealed class PrepareEvidence
        {
            public int schemaVersion;
            public string format;
            public string generatedAtUtc;
            public bool passed;
            public int toolchainContractVersion;
            public string target;
            public string mode;
            public string pathSemantics;
            public string projectPath;
            public string settingsFile;
            public string baselineRoot;
            public string currentRoot;
            public string baselineSourceRoot;
            public string currentSourceRoot;
            public string runtimeAssemblySourceRoot;
            public bool baselineGeneratedFromCurrent;
            public string[] aotAssemblies;
            public string[] hotUpdateAssemblies;
        }
    }

    public sealed class DheProjectWorkflowAdapter
    {
        public string ProjectRoot;
        public string Workflow = "dhe-opt4";
        public string BuildIdentityAssetPath;
        public string IdentityNamespace;
        public string IdentityClassName = "DheBuildIdentity";
        public string RuntimeAssetRoot;
        public Func<string[]> GetScenes;
        public Func<BuildPlayerOptions, BuildReport> BuildPlayer;
        public Func<BuildTarget, string, string> ResolvePlayerOutput;
        public Action<DheProjectPlayerSmokeContext> RunPlayerSmoke;
        public Func<string, byte[], byte[]> CurrentAssemblyTransform;
        public Func<string, string[], string[]> HotfixLoadOrderResolver;
        public Action<string, string> DependencyMapWriter;
        public Action<string> StageAdditionalRuntimeAssets;
    }

    public sealed class DheProjectPlayerSmokeContext
    {
        public string PlayerPath;
        public string OutputRoot;
        public BuildTarget Target;
        public string TargetName;
        public DheNativeFinalizeResult NativeResult;
    }

    public sealed class DheProjectWorkflowContext
    {
        public BuildTarget Target { get; private set; }
        public string TargetName { get; private set; }
        public string OutputRoot { get; private set; }
        public string BaselineRoot { get; private set; }
        public string CurrentRoot { get; private set; }
        public string ProjectPlanPath { get; private set; }
        public string Mode { get; private set; }

        public static DheProjectWorkflowContext FromCommandLine(bool requireProjectPlan)
        {
            var context = new DheProjectWorkflowContext
            {
                TargetName = RequireArgument("-dheTarget"),
                OutputRoot = Path.GetFullPath(RequireArgument("-dheOutputRoot")),
                BaselineRoot = Path.GetFullPath(RequireArgument("-dheBaselineRoot")),
                CurrentRoot = Path.GetFullPath(GetArgumentValue("-dheCurrentRoot") ??
                    Path.Combine(RequireArgument("-dheOutputRoot"), "current")),
                Mode = GetArgumentValue("-dheMode") ?? "Exploratory",
            };
            context.Target = ParseTarget(context.TargetName);
            string projectPlan = GetArgumentValue("-dheProjectPlan");
            if (requireProjectPlan && string.IsNullOrWhiteSpace(projectPlan))
                throw new BuildFailedException("Missing required Unity argument: -dheProjectPlan");
            context.ProjectPlanPath = string.IsNullOrWhiteSpace(projectPlan) ? null :
                Path.GetFullPath(projectPlan);
            return context;
        }

        public string GetArgument(string name)
        {
            return GetArgumentValue(name) ?? string.Empty;
        }

        public bool GetBooleanArgument(string name)
        {
            string value = GetArgument(name);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        public void EnsureTarget()
        {
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(Target);
            if (EditorUserBuildSettings.activeBuildTarget != Target &&
                !EditorUserBuildSettings.SwitchActiveBuildTarget(group, Target))
                throw new BuildFailedException("Unable to switch active build target to " + Target);
            if (group == BuildTargetGroup.Standalone)
            {
                EditorUserBuildSettings.selectedStandaloneTarget = Target;
                EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;
                var method = typeof(EditorUserBuildSettings).GetMethod("SetSelectedSubtargetFor",
                    System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.NonPublic);
                if (method == null) throw new BuildFailedException(
                    "Unity does not expose the selected standalone subtarget API required by SBP.");
                method.Invoke(null, new object[] { Target, (int)StandaloneBuildSubtarget.Player });
            }
            EditorUserBuildSettings.buildScriptsOnly = false;
        }

        private static BuildTarget ParseTarget(string value)
        {
            if (Enum.TryParse(value, true, out BuildTarget target) && target != BuildTarget.NoTarget)
                return target;
            throw new BuildFailedException("Unsupported DHE build target: " + value);
        }

        private static string RequireArgument(string name)
        {
            string value = GetArgumentValue(name);
            if (string.IsNullOrWhiteSpace(value)) throw new BuildFailedException(
                "Missing required Unity argument: " + name);
            return value;
        }

        private static string GetArgumentValue(string name)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index < arguments.Length - 1; index++)
            {
                if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                    return arguments[index + 1];
            }
            return null;
        }
    }
}
