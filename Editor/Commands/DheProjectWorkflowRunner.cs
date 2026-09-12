using System;
using System.Collections.Generic;
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
        /// <summary>Complete Base lifecycle; projects supply build/resource callbacks.</summary>
        public static DheProjectBaseResult BuildBase(DheProjectWorkflowAdapter adapter, DheProjectWorkflowOptions options)
        {
            RequireAdapter(adapter);
            var context = DheProjectWorkflowContext.Create(options);
            try
            {
                return BuildBaseCore(adapter, context);
            }
            finally
            {
                DheProjectBuildSupport.RestoreBuildIdentityTemplate(CreateIdentityOptions(adapter, context));
            }
        }

        private static DheProjectBaseResult BuildBaseCore(DheProjectWorkflowAdapter adapter, DheProjectWorkflowContext context)
        {
            Prepare(adapter, context);
            DheToolCommand.RunWithTimeout("preflight", new[]
            {
                "-SettingsFile", Path.Combine(adapter.ProjectRoot, "ProjectSettings/HybridCLRSettings.asset"),
                "-BaselineRoot", context.BaselineRoot, "-CurrentRoot", context.CurrentRoot,
                "-ProjectRoot", adapter.ProjectRoot,
                "-OutputRoot", Path.GetDirectoryName(context.ProjectPlanPath),
                "-RequireDheEqualsHotUpdate", "-RequireCompleteCoverage"
            }, 900000);
            string generatedPlan = Path.Combine(Path.GetDirectoryName(context.ProjectPlanPath), "dhe-project-plan.json");
            if (!string.Equals(generatedPlan, context.ProjectPlanPath, StringComparison.Ordinal))
            {
                if (!File.Exists(context.ProjectPlanPath)) File.Copy(generatedPlan, context.ProjectPlanPath);
                else if (!File.ReadAllBytes(generatedPlan).SequenceEqual(File.ReadAllBytes(context.ProjectPlanPath)))
                    throw new BuildFailedException("DHE requested project-plan path contains a different plan.");
            }
            StageRuntimePlan(adapter, context);
            BuildScriptsOnly(adapter, context);
            BuildFinalPlayer(adapter, context);
            return new DheProjectBaseResult
            {
                OutputRoot = context.OutputRoot,
                BuildIdentityPath = Path.Combine(context.OutputRoot, "build-identity.json"),
                NativeManifestPath = Path.Combine(context.OutputRoot, "native/dhe-native-manifest.json"),
                PlayerPath = ResolvePlayerOutput(adapter, context)
            };
        }

        public static void Prepare(DheProjectWorkflowAdapter adapter)
            => Prepare(adapter, DheProjectWorkflowContext.FromCommandLine(false));

        public static void Prepare(DheProjectWorkflowAdapter adapter, DheProjectWorkflowContext context)
        {
            RequireAdapter(adapter);
            RequireContext(context, false);
            context.EnsureTarget();
            // Keep the generated type shape stable between the initial AOT
            // snapshot and the final Player that embeds its snapshot hash.
            DheProjectBuildSupport.RestoreBuildIdentityTemplate(CreateIdentityOptions(adapter, context));
            string baselineSource = context.BaselineSourceRoot;
            DheProjectPrepareResult prepared = DheBuildPipeline.PrepareProjectArtifacts(
                new DheProjectPrepareOptions
                {
                    Target = context.Target,
                    Mode = context.Mode,
                    BaselineSourceRoot = string.IsNullOrWhiteSpace(baselineSource) ? null :
                        Path.GetFullPath(baselineSource),
                    BaselineOutputRoot = context.BaselineRoot,
                    CurrentOutputRoot = context.CurrentRoot,
                    Bootstrap = context.Bootstrap,
                    RequireDheEqualsHotUpdate = true,
                    BeforeCurrentGeneration = adapter.BeforeCurrentGeneration,
                    AfterCurrentGeneration = adapter.AfterCurrentGeneration,
                });
            EnsureAssemblyRoot(prepared.CurrentOutputRoot,
                prepared.HotUpdateAssemblyNames, "prepared Current output");
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
            => BuildScriptsOnly(adapter, DheProjectWorkflowContext.FromCommandLine(true));

        public static void BuildScriptsOnly(DheProjectWorkflowAdapter adapter, DheProjectWorkflowContext context)
        {
            RequireAdapter(adapter);
            RequireContext(context, true);
            context.EnsureTarget();
            BuildPlayer(adapter, context, BuildOptions.BuildScriptsOnly);
            DheProjectNativeOptions nativeOptions = CreateNativeOptions(adapter, context);
            DheNativeFinalizeResult result = DheBuildPipeline.FinalizeProjectNativeCode(
                DheProjectBuildSupport.CreateNativeFinalizeOptions(nativeOptions, false));
            DheProjectBuildSupport.WriteNativeEvidence(nativeOptions, result, false);
            DheProjectBuildSupport.StageBuildIdentity(CreateIdentityOptions(adapter, context), result);
            WritePlayerBuildEvidence(adapter, context, true);
        }

        public static void StageRuntimePlan(DheProjectWorkflowAdapter adapter)
            => StageRuntimePlan(adapter, DheProjectWorkflowContext.FromCommandLine(true));

        public static void StageRuntimePlan(DheProjectWorkflowAdapter adapter, DheProjectWorkflowContext context)
        {
            RequireAdapter(adapter);
            RequireContext(context, true);
            context.EnsureTarget();
            string runtimeAssetRoot = ResolveProjectPath(adapter.ProjectRoot,
                adapter.RuntimeAssetRoot);
            // Dependency callbacks must inspect the same frozen DLLs used by
            // the project plan, including externally compiled hotfix inputs.
            string currentAssemblyRoot = Path.GetFullPath(context.CurrentRoot);
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
                    AotMetadataAssemblyNames = context.AotMetadataAssemblyNames,
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
            => BuildFinalPlayer(adapter, DheProjectWorkflowContext.FromCommandLine(true));

        public static void BuildFinalPlayer(DheProjectWorkflowAdapter adapter, DheProjectWorkflowContext context)
        {
            RequireAdapter(adapter);
            RequireContext(context, true);
            context.EnsureTarget();
            try
            {
                DheProjectBuildSupport.ValidateStagedBuildIdentity(
                    CreateIdentityOptions(adapter, context));
                DheNativeFinalizeResult result = BuildPlayer(adapter, context, BuildOptions.None);
                bool nativeMatches = DheProjectBuildSupport.FinalNativeIdentityMatches(context.OutputRoot, result,
                    out string identityError);
                bool aotMatches = DheProjectBuildSupport.FinalAotAnalysisSnapshotMatches(
                    CreateIdentityOptions(adapter, context), out string aotError);
                if (!nativeMatches || !aotMatches)
                {
                    Debug.LogWarning("DHE native identity or AOT inputs changed during the final Player pass; " +
                        "settling the embedded identity and rebuilding once. " + identityError + " " + aotError);
                    DheProjectBuildSupport.StageBuildIdentity(CreateIdentityOptions(adapter, context),
                        result);
                    result = BuildPlayer(adapter, context, BuildOptions.None);
                }
                DheProjectBuildSupport.ValidateFinalNativeIdentity(context.OutputRoot, result);
                DheProjectBuildSupport.ValidateStagedBuildIdentity(
                    CreateIdentityOptions(adapter, context));
                DheProjectBuildSupport.WriteNativeEvidence(CreateNativeOptions(adapter, context),
                    result, true);
                WritePlayerBuildEvidence(adapter, context, false);
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
                AndroidArtifactLogPath = scriptsOnly ? null :
                    Path.Combine(context.OutputRoot, "native", "android-artifact.log"),
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
                PlayerOutputPath = ResolvePlayerOutput(adapter, context),
                // Every DHE Base must remain consumable by later
                // resource-only updates.  The native finalizer therefore
                // requires universal guards for both bootstrap and
                // subsequent Base rebuilds; bootstrap only controls the
                // baseline creation policy.
                GuardAllMethods = true,
                AdditionalGuardMvJsonPaths = adapter.AdditionalGuardMvJsonPaths,
                OrdinaryAotRoot = adapter.GuardOrdinaryAotMethods ? Path.GetFullPath(
                    SettingsUtil.GetAssembliesPostIl2CppStripDir(context.Target)) : null,
                OrdinaryGuardIdentityType = adapter.IdentityNamespace + "." + adapter.IdentityClassName,
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
                AotAssemblyRoot = Path.GetFullPath(
                    SettingsUtil.GetAssembliesPostIl2CppStripDir(context.Target)),
                ProjectPlanPath = context.ProjectPlanPath,
                Target = context.TargetName,
                EngineWorkflow = context.EngineWorkflow,
                Il2CppCodeGeneration = context.Il2CppCodeGeneration,
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
            DheToolCommand.Run("verify-installation", "-ProjectPath", adapter.ProjectRoot);
        }

        private static void RequireContext(DheProjectWorkflowContext context, bool requirePlan)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (requirePlan && string.IsNullOrWhiteSpace(context.ProjectPlanPath))
                throw new BuildFailedException("DHE stage requires the preflight project plan.");
            if (context.Mode == "Release") DheToolCommand.Run("verify-package", "-RequireRelease");
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

        private static void WritePlayerBuildEvidence(DheProjectWorkflowAdapter adapter,
            DheProjectWorkflowContext context, bool scriptsOnly)
        {
            WriteJson(Path.Combine(context.OutputRoot, "adapter", scriptsOnly ?
                "build-scripts-only.json" : "build-final-player.json"), new PlayerBuildEvidence
            {
                schemaVersion = 1, format = "hybridclr.dhe-adapter-player-build.json",
                generatedAtUtc = DateTimeOffset.UtcNow.ToString("O"), passed = true,
                scriptsOnly = scriptsOnly, target = context.TargetName,
                playerPath = ResolvePlayerOutput(adapter, context),
            });
        }

        [Serializable]
        private sealed class PlayerBuildEvidence
        {
            public int schemaVersion;
            public string format, generatedAtUtc, target, playerPath;
            public bool passed, scriptsOnly;
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
        public string Workflow = "dhe-opt5";
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
        public Action<string[]> BeforeCurrentGeneration;
        public Action<string[]> AfterCurrentGeneration;
        /// <summary>
        /// Optional authenticated MV JSONs for ordinary AOT guard coverage.
        /// These inputs affect native guards only and never hotfix loading.
        /// </summary>
        public string[] AdditionalGuardMvJsonPaths;
        /// <summary>Derive complete ordinary guards from the current final-build stripped input.</summary>
        public bool GuardOrdinaryAotMethods = true;
    }

    public sealed class DheProjectPlayerSmokeContext
    {
        public string PlayerPath;
        public string OutputRoot;
        public BuildTarget Target;
        public string TargetName;
        public DheNativeFinalizeResult NativeResult;
    }

    public sealed class DheProjectBaseResult
    {
        public string OutputRoot, BuildIdentityPath, NativeManifestPath, PlayerPath;
    }

    /// <summary>Explicit build inputs. Does not read command-line or environment state.</summary>
    public sealed class DheProjectWorkflowOptions
    {
        public BuildTarget Target;
        public string OutputRoot;
        public string BaselineRoot;
        public string CurrentRoot;
        public string BaselineSourceRoot;
        public string ProjectPlanPath;
        public string Mode = "Exploratory";
        public string EngineWorkflow = "Unity2022Fgs";
        public string Il2CppCodeGeneration = "OptimizeSize";
        public bool Bootstrap;
        public string PlayerOutputPath;
        public string[] AotMetadataAssemblyNames = Array.Empty<string>();
        /// <summary>Optional staging metadata arguments; copied when creating the context.</summary>
        public IDictionary<string, string> StageArguments;
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
        public string EngineWorkflow { get; private set; }
        public string Il2CppCodeGeneration { get; private set; }
        public string BaselineSourceRoot { get; private set; }
        public bool Bootstrap { get; private set; }
        public string[] AotMetadataAssemblyNames { get; private set; }
        private Dictionary<string, string> arguments;

        public static DheProjectWorkflowContext Create(DheProjectWorkflowOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.Target == BuildTarget.NoTarget || !Enum.IsDefined(typeof(BuildTarget), options.Target))
                throw new BuildFailedException("DHE build target must be explicit.");
            if (string.IsNullOrWhiteSpace(options.OutputRoot) || string.IsNullOrWhiteSpace(options.BaselineRoot))
                throw new BuildFailedException("DHE OutputRoot and BaselineRoot are required.");
            if (options.Mode != "Exploratory" && options.Mode != "Release")
                throw new BuildFailedException("DHE mode must be Exploratory or Release.");
            DheProjectBuildSupport.ValidateBuildConfiguration(options.EngineWorkflow, options.Il2CppCodeGeneration);
            var context = new DheProjectWorkflowContext
            {
                Target = options.Target, TargetName = options.Target.ToString(),
                OutputRoot = Path.GetFullPath(options.OutputRoot),
                BaselineRoot = Path.GetFullPath(options.BaselineRoot),
                CurrentRoot = Path.GetFullPath(options.CurrentRoot ?? Path.Combine(options.OutputRoot, "current")),
                ProjectPlanPath = Path.GetFullPath(options.ProjectPlanPath ?? Path.Combine(options.OutputRoot, "project-preflight/dhe-project-plan.json")),
                BaselineSourceRoot = string.IsNullOrWhiteSpace(options.BaselineSourceRoot) ? null : Path.GetFullPath(options.BaselineSourceRoot),
                Mode = options.Mode, EngineWorkflow = options.EngineWorkflow,
                Il2CppCodeGeneration = options.Il2CppCodeGeneration, Bootstrap = options.Bootstrap,
                AotMetadataAssemblyNames = (options.AotMetadataAssemblyNames ?? Array.Empty<string>()).ToArray(),
                arguments = options.StageArguments == null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) :
                    new Dictionary<string, string>(options.StageArguments, StringComparer.OrdinalIgnoreCase),
            };
            if (!string.IsNullOrWhiteSpace(options.PlayerOutputPath))
                context.arguments["-dhePlayerOutput"] = Path.GetFullPath(options.PlayerOutputPath);
            return context;
        }

        public static DheProjectWorkflowContext FromCommandLine(bool requireProjectPlan)
        {
            var context = Create(new DheProjectWorkflowOptions
            {
                Target = ParseTarget(RequireArgument("-dheTarget")),
                OutputRoot = Path.GetFullPath(RequireArgument("-dheOutputRoot")),
                BaselineRoot = Path.GetFullPath(RequireArgument("-dheBaselineRoot")),
                CurrentRoot = Path.GetFullPath(GetArgumentValue("-dheCurrentRoot") ??
                    Path.Combine(RequireArgument("-dheOutputRoot"), "current")),
                Mode = GetArgumentValue("-dheMode") ?? "Exploratory",
                EngineWorkflow = RequireArgument("-dheEngineWorkflow"),
                Il2CppCodeGeneration = RequireArgument("-dheIl2CppCodeGeneration"),
                BaselineSourceRoot = Environment.GetEnvironmentVariable("DHE_BASELINE_ROOT"),
                Bootstrap = IsTrue(GetArgumentValue("-dheBootstrap")),
                PlayerOutputPath = GetArgumentValue("-dhePlayerOutput"),
                AotMetadataAssemblyNames = ReadAotMetadataArgument(),
            });
            foreach (string name in new[] { "-dheAotMetadataFallbackRoot", "-dheAotMetadataFallbackManifest",
                "-dheAotMetadataExpectedTarget", "-dheAotMetadataExpectedStagedRuntimeSha256",
                "-dheAotMetadataExpectedRuntimeManifestSha256", "-dheAotMetadataExpectedPackageTreeSha256" })
                if (GetArgumentValue(name) is string value) context.arguments[name] = value;
            string projectPlan = GetArgumentValue("-dheProjectPlan");
            if (requireProjectPlan && string.IsNullOrWhiteSpace(projectPlan))
                throw new BuildFailedException("Missing required Unity argument: -dheProjectPlan");
            context.ProjectPlanPath = string.IsNullOrWhiteSpace(projectPlan) ? null :
                Path.GetFullPath(projectPlan);
            return context;
        }

        public string GetArgument(string name)
        {
            return arguments.TryGetValue(name, out string value) ? value ?? string.Empty : string.Empty;
        }

        public bool GetBooleanArgument(string name)
        {
            return IsTrue(GetArgument(name));
        }

        private static bool IsTrue(string value)
        {
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
            DheProjectBuildSupport.ApplyIl2CppCodeGeneration(Target,
                Il2CppCodeGeneration);
            EditorUserBuildSettings.buildScriptsOnly = false;
        }

        private static BuildTarget ParseTarget(string value)
        {
            if (Enum.TryParse(value, true, out BuildTarget target) && target != BuildTarget.NoTarget)
                return target;
            throw new BuildFailedException("Unsupported DHE build target: " + value);
        }

        private static string[] ReadAotMetadataArgument()
        {
            string value = GetArgumentValue("-dheAotMetadataAssemblies");
            if (value == null) return SettingsUtil.AOTAssemblyNames.ToArray();
            return string.Equals(value, "none", StringComparison.OrdinalIgnoreCase) ? Array.Empty<string>() :
                value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(name => name.Trim()).ToArray();
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
