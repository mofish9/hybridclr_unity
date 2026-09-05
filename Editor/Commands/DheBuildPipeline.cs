using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace HybridCLR.Editor.Commands
{
    /// <summary>
    /// Project-independent DHE build primitives. A project adapter supplies
    /// asset/package policies through callbacks; the package owns the file and
    /// player-build contract shared by every Unity project.
    /// </summary>
    public static class DheBuildPipeline
    {
        private const string BaselineEnvironmentVariable = "HYBRIDCLR_DHE_AOT_BASELINE_ROOT";
        private const string BuildPhaseEnvironmentVariable = "HYBRIDCLR_DHE_BUILD_PHASE";
        private const string NativeGuardHashContract = "guard-block-set-v1";
		private const string NativeRuntimeProtocol = "dhe-runtime-protocol-v1";
		private const string NativeRuntimeContract = "dhe-runtime-v2";
        private static readonly string[] NativeRuntimeCapabilities =
        {
            "aot-guard-v1",
            "stable-method-identity-v1",
            "single-current-multibase-v1",
            "resource-update-plan-integrity-v1",
            "resource-update-aot-metadata-path-v1",
            "resource-update-aot-metadata-set-selection-v1",
            "atomic-multi-assembly-registration-v1",
			"supplemental-existing-type-instance-fields-v1",
            "supplemental-existing-type-static-fields-v1",
			"supplemental-existing-type-methods-v1",
			"removed-existing-type-methods-v1",
			"existing-type-method-signature-replacement-v1",
			"removed-existing-type-fields-v1",
			"removed-types-v1",
			"logical-existing-type-properties-events-v1",
			"logical-existing-member-custom-attributes-v1",
            "supplemental-nested-types-v1",
            "supplemental-top-level-types-v1",
        };
        private const string NativeGuardBeginPrefix = "HYBRIDCLR_DHE_GUARD_BEGIN_V1:";
        private const string NativeGuardEndPrefix = "HYBRIDCLR_DHE_GUARD_END_V1:";
        internal const string CurrentGenerationBuildPhase = "current-generation";
        internal const string FinalPlayerBuildPhase = "final-player";
        internal const string CurrentGenerationScriptingDefine =
            "HYBRIDCLR_DHE_CURRENT_GENERATION";
        internal const string BasePlayerScriptingDefine =
            "HYBRIDCLR_DHE_BASE_PLAYER";

        internal static bool IsCurrentGenerationBuild =>
            string.Equals(Environment.GetEnvironmentVariable(BuildPhaseEnvironmentVariable),
                CurrentGenerationBuildPhase, StringComparison.OrdinalIgnoreCase);
        private const string DllExtension = ".dll";
        private const string DllBytesExtension = ".dll.bytes";
        private static readonly Dictionary<string, byte[]> BaselineAssemblyBackups =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        public static string[] GetHotUpdateAssemblyNames()
        {
            return NormalizeAssemblyNames(SettingsUtil.HotUpdateAssemblyNamesExcludePreserved);
        }

        public static string[] GetDheAotAssemblyNames()
        {
            return NormalizeAssemblyNames(SettingsUtil.DheAotAssemblyNames);
        }

        /// <summary>
        /// Removes only the generated DHE payload from a project's hot-update
        /// asset directory. Legacy hotfix/AOT metadata remains intact so a
        /// normal base-package build can explicitly opt out of DHE without
        /// inheriting a plan from an earlier DHE build.
        /// </summary>
        public static void ClearDheRuntimePlanAssets(string runtimeAssetRoot)
        {
            if (string.IsNullOrWhiteSpace(runtimeAssetRoot))
                throw new BuildFailedException("DHE runtime asset root is empty.");
            string root = Path.GetFullPath(runtimeAssetRoot).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string assetsRoot = Path.GetFullPath(Application.dataPath).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
#if UNITY_EDITOR_WIN
            const StringComparison pathComparison = StringComparison.OrdinalIgnoreCase;
#else
            const StringComparison pathComparison = StringComparison.Ordinal;
#endif
            if (string.Equals(root, assetsRoot, pathComparison) ||
                !root.StartsWith(assetsRoot + Path.DirectorySeparatorChar, pathComparison))
                throw new BuildFailedException(
                    "DHE runtime asset root must be a child of the project Assets directory: " +
                    root);
            if (!Directory.Exists(root))
            {
                return;
            }
            ClearDhePayloadFiles(root);
            ClearBaseMetaVersionFiles(Path.Combine(root, "BaseMetaVersion"));
        }

        public static void ValidateAssemblyScope(bool requireExactMatch,
            out string[] hotUpdateAssemblies, out string[] dheAotAssemblies)
        {
            hotUpdateAssemblies = GetHotUpdateAssemblyNames();
            dheAotAssemblies = GetDheAotAssemblyNames();
            if (hotUpdateAssemblies.Length == 0)
            {
                throw new BuildFailedException("HybridCLR hotUpdateAssemblies is empty.");
            }

            EnsureUnique(hotUpdateAssemblies, "hotUpdateAssemblies");
            EnsureUnique(dheAotAssemblies, "dheAotAssemblies");
            string[] configuredHotUpdateAssemblies = hotUpdateAssemblies;
            if (dheAotAssemblies.Any(name => !configuredHotUpdateAssemblies.Contains(name,
                    StringComparer.OrdinalIgnoreCase)))
            {
                throw new BuildFailedException(
                    "Every DHE AOT assembly must also be listed in hotUpdateAssemblies.");
            }
            if (requireExactMatch &&
                (hotUpdateAssemblies.Except(dheAotAssemblies, StringComparer.OrdinalIgnoreCase).Any() ||
                 dheAotAssemblies.Except(hotUpdateAssemblies, StringComparer.OrdinalIgnoreCase).Any()))
            {
                throw new BuildFailedException(
                    "DHE release coverage requires dheAotAssemblies to exactly match hotUpdateAssemblies.");
            }
        }

        /// <summary>
        /// Generates the current stripped-AOT image and stages the complete
        /// baseline/current assembly sets consumed by the host-side MV step.
        /// All filesystem and release-policy inputs are explicit so this step
        /// is shared by projects without inheriting a demo directory layout.
        /// </summary>
        public static DheProjectPrepareResult PrepareProjectArtifacts(
            DheProjectPrepareOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            ValidateAssemblyScope(options.RequireDheEqualsHotUpdate,
                out string[] hotUpdateAssemblies, out string[] dheAotAssemblies);
            try
            {
                if (options.BeforeCurrentGeneration != null)
                    options.BeforeCurrentGeneration(dheAotAssemblies);
                GenerateCurrentArtifacts(options.Target);
            }
            finally
            {
                if (options.AfterCurrentGeneration != null)
                    options.AfterCurrentGeneration(dheAotAssemblies);
            }

            string currentSourceRoot = string.IsNullOrWhiteSpace(options.CurrentAotRoot)
                ? Path.GetFullPath(SettingsUtil.GetAssembliesPostIl2CppStripDir(options.Target))
                : RequireDirectory(options.CurrentAotRoot, "DHE current stripped-AOT root");
            string currentOutputRoot = RequireOutputDirectory(options.CurrentOutputRoot,
                "DHE current output root");
            string baselineOutputRoot = RequireOutputDirectory(options.BaselineOutputRoot,
                "DHE baseline output root");
            string baselineSourceRoot = string.IsNullOrWhiteSpace(options.BaselineSourceRoot)
                ? currentSourceRoot
                : RequireDirectory(options.BaselineSourceRoot, "DHE baseline source root");
            bool baselineGeneratedFromCurrent = string.Equals(baselineSourceRoot,
                currentSourceRoot, StringComparison.OrdinalIgnoreCase);
            if (options.Bootstrap && !baselineGeneratedFromCurrent)
            {
                throw new BuildFailedException(
                    "DHE bootstrap must freeze the generated current stripped-AOT set as its Base.");
            }
            if (string.Equals(options.Mode, "Release", StringComparison.OrdinalIgnoreCase) &&
                baselineGeneratedFromCurrent && !options.Bootstrap)
            {
                throw new BuildFailedException(
                    "DHE Release preparation requires a previous stripped-AOT baseline root.");
            }

            CopyAssemblySet(currentSourceRoot, currentOutputRoot, dheAotAssemblies,
                "current stripped-AOT");
            CopyAssemblySet(baselineSourceRoot, baselineOutputRoot, dheAotAssemblies,
                "baseline stripped-AOT");
            return new DheProjectPrepareResult
            {
                Target = options.Target,
                Mode = string.IsNullOrWhiteSpace(options.Mode) ? "Exploratory" : options.Mode,
                CurrentSourceRoot = currentSourceRoot,
                CurrentOutputRoot = currentOutputRoot,
                BaselineSourceRoot = baselineSourceRoot,
                BaselineOutputRoot = baselineOutputRoot,
                BaselineGeneratedFromCurrent = baselineGeneratedFromCurrent,
                HotUpdateAssemblyNames = hotUpdateAssemblies,
                DheAotAssemblyNames = dheAotAssemblies,
            };
        }

        /// <summary>
        /// Generates the current stripped-AOT artifacts used as the right-hand
        /// side of a DHE diff. This is deliberately separate from BuildPlayer:
        /// a current-generation pass must retain the current DHE assemblies,
        /// while the final Player pass must inject the previous baseline.
        /// </summary>
        public static void GenerateCurrentArtifacts(BuildTarget target)
        {
            string[] dheAssemblies = GetDheAotAssemblyNames();
            if (dheAssemblies.Length == 0)
            {
                throw new BuildFailedException(
                    "DHE current artifact generation requires at least one dheAotAssemblies entry.");
            }

            EnsureActiveBuildTarget(target);
            // Unity 2021's Bee profiler writes Library/Bee/buildreport.json
            // before guaranteeing that the parent directory exists.
            Directory.CreateDirectory(Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", "Library", "Bee")));
            string previousPhase = Environment.GetEnvironmentVariable(
                BuildPhaseEnvironmentVariable);
            string previousBaseline = Environment.GetEnvironmentVariable(
                BaselineEnvironmentVariable);
            Environment.SetEnvironmentVariable(BuildPhaseEnvironmentVariable,
                CurrentGenerationBuildPhase);
            // A caller may have inherited a final-player baseline binding. Do
            // not allow that process state to silently turn current artifacts
            // into another copy of the previous release.
            Environment.SetEnvironmentVariable(BaselineEnvironmentVariable, null);
            try
            {
                PrebuildCommand.GenerateAll();
            }
            finally
            {
                Environment.SetEnvironmentVariable(BuildPhaseEnvironmentVariable, previousPhase);
                Environment.SetEnvironmentVariable(BaselineEnvironmentVariable, previousBaseline);
            }
        }

        public static DheRuntimePlanResult StageRuntimePlan(DheRuntimePlanOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            if (options.Target != BuildTarget.NoTarget)
            {
                EnsureActiveBuildTarget(options.Target);
            }
            string projectRoot = RequireDirectory(options.ProjectRoot, "DHE project root");
            string planPath = RequireFile(options.ProjectPlanPath, "DHE project plan");
            string assetRoot = ResolveProjectPath(projectRoot, options.RuntimeAssetRoot);
            string outputRoot = ResolveProjectPath(projectRoot, options.OutputRoot);
            string currentAssetRoot = RequireProjectChild(projectRoot, assetRoot,
                "DHE runtime asset root");
            string baseMetaVersionRoot = RequireProjectChild(projectRoot,
                ResolveProjectPath(projectRoot, string.IsNullOrWhiteSpace(options.BaseMetaVersionAssetRoot)
                    ? Path.Combine(currentAssetRoot, "BaseMetaVersion")
                    : options.BaseMetaVersionAssetRoot), "DHE Base MetaVersion asset root");
            Directory.CreateDirectory(currentAssetRoot);
            Directory.CreateDirectory(baseMetaVersionRoot);
            Directory.CreateDirectory(outputRoot);

            DheProjectPlan plan = JsonUtility.FromJson<DheProjectPlan>(File.ReadAllText(planPath));
            if (plan == null || plan.schemaVersion != 1 ||
                !string.Equals(plan.format, "hybridclr.dhe-project-plan.json", StringComparison.Ordinal) ||
                !plan.complete || plan.assemblies == null || plan.assemblies.Length == 0)
            {
                throw new BuildFailedException(
                    "DHE project plan must be a complete hybridclr.dhe-project-plan.json document: " + planPath);
            }

            string planDirectory = Path.GetDirectoryName(planPath);

            // Remove only DHE payloads from the previous plan. Other hotfix
            // bytes in the same directory are owned by the project adapter.
            ClearRuntimeAssets(currentAssetRoot);
            ClearBaseMetaVersionFiles(baseMetaVersionRoot);
            string handoffRoot = Path.Combine(outputRoot, "runtime-plan");
            if (Directory.Exists(handoffRoot))
            {
                Directory.Delete(handoffRoot, true);
            }
            Directory.CreateDirectory(handoffRoot);

            List<DheRuntimePlanAssembly> runtimeRecords = new List<DheRuntimePlanAssembly>();
            List<DheRuntimePlanHandoffAssembly> handoffRecords =
                new List<DheRuntimePlanHandoffAssembly>();
            List<DheRuntimePlanAotMetadata> handoffAotMetadata =
                new List<DheRuntimePlanAotMetadata>();
            List<KeyValuePair<string, byte[]>> baselineSet =
                new List<KeyValuePair<string, byte[]>>();
            List<KeyValuePair<string, byte[]>> currentSet =
                new List<KeyValuePair<string, byte[]>>();
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DheProjectAssembly assembly in plan.assemblies)
            {
                string assemblyName = NormalizeAssemblyName(assembly == null ? null : assembly.assemblyName);
                if (string.IsNullOrWhiteSpace(assemblyName) || !names.Add(assemblyName))
                {
                    throw new BuildFailedException("DHE project plan contains an empty or duplicate assembly.");
                }
                if (!string.Equals(assembly?.status, "compatible", StringComparison.Ordinal))
                {
                    throw new BuildFailedException("DHE project plan assembly is not compatible: " + assemblyName);
                }

                string currentPath = RequireFile(ResolvePlanReference(planDirectory, assembly.current),
                    assemblyName + " current assembly");
                string baselinePath = RequireFile(ResolvePlanReference(planDirectory, assembly.baseline),
                    assemblyName + " baseline assembly");
                string baseMetaVersionPath = RequireFile(ResolvePlanReference(planDirectory,
                    assembly.baseMetaVersionBytes), assemblyName + " Base MetaVersion binary");
                string currentMetaVersionPath = RequireFile(ResolvePlanReference(planDirectory,
                    assembly.currentMetaVersionBytes), assemblyName + " current MetaVersion binary");
                byte[] currentBytes = File.ReadAllBytes(currentPath);
                byte[] baseMetaVersionBytes = File.ReadAllBytes(baseMetaVersionPath);
                byte[] currentMetaVersionBytes = File.ReadAllBytes(currentMetaVersionPath);
                if (options.CurrentAssemblyTransform != null)
                {
                    currentBytes = options.CurrentAssemblyTransform(assemblyName, currentBytes) ??
                        throw new BuildFailedException("DHE current assembly transform returned null: " + assemblyName);
                }

                string dllFileName = assemblyName + DllBytesExtension;
                string mvFileName = assemblyName + ".mv.bytes";
                string baseMvFileName = assemblyName + ".mv.bytes";
                string snapshotFileName = assemblyName + ".aot-snapshot.bytes";
                File.WriteAllBytes(Path.Combine(currentAssetRoot, dllFileName), currentBytes);
                byte[] baselineBytes = File.ReadAllBytes(baselinePath);
                baselineSet.Add(new KeyValuePair<string, byte[]>(assemblyName, baselineBytes));
                currentSet.Add(new KeyValuePair<string, byte[]>(assemblyName, currentBytes));
                byte[] snapshotBytes = Sha256(baselineBytes);
                File.WriteAllBytes(Path.Combine(currentAssetRoot, mvFileName), currentMetaVersionBytes);
                File.WriteAllBytes(Path.Combine(baseMetaVersionRoot, baseMvFileName), baseMetaVersionBytes);
                File.WriteAllBytes(Path.Combine(currentAssetRoot, snapshotFileName), snapshotBytes);

                runtimeRecords.Add(new DheRuntimePlanAssembly
                {
                    assemblyName = assemblyName,
                    current = ResolveRuntimeAssetPath(options, projectRoot,
                        Path.Combine(currentAssetRoot, dllFileName)),
                    currentMetaVersion = ResolveRuntimeAssetPath(options, projectRoot,
                        Path.Combine(currentAssetRoot, mvFileName)),
                    baseMetaVersion = ResolveRuntimeAssetPath(options, projectRoot,
                        Path.Combine(baseMetaVersionRoot, baseMvFileName)),
                    currentSha256 = Sha256Hex(currentBytes),
                    currentMetaVersionSha256 = Sha256Hex(currentMetaVersionBytes),
                });

                string handoffCurrent = assemblyName + ".current.dll";
                string handoffBaseline = assemblyName + ".baseline.dll";
                string handoffBaseMv = assemblyName + ".base.mv.bytes";
                string handoffCurrentMv = assemblyName + ".current.mv.bytes";
                string handoffSnapshot = assemblyName + ".snapshot.bytes";
                File.WriteAllBytes(Path.Combine(handoffRoot, handoffCurrent), currentBytes);
                File.Copy(baselinePath, Path.Combine(handoffRoot, handoffBaseline), true);
                File.WriteAllBytes(Path.Combine(handoffRoot, handoffBaseMv), baseMetaVersionBytes);
                File.WriteAllBytes(Path.Combine(handoffRoot, handoffCurrentMv), currentMetaVersionBytes);
                File.Copy(Path.Combine(currentAssetRoot, snapshotFileName),
                    Path.Combine(handoffRoot, handoffSnapshot), true);
                handoffRecords.Add(new DheRuntimePlanHandoffAssembly
                {
                    assemblyName = assemblyName,
                    current = handoffCurrent,
                    baseline = handoffBaseline,
                    baseMetaVersion = handoffBaseMv,
                    currentMetaVersion = handoffCurrentMv,
                    snapshot = handoffSnapshot,
                    baselineSha256 = Sha256Hex(baselineBytes),
                    currentSha256 = Sha256Hex(currentBytes),
                    baseMetaVersionSha256 = Sha256Hex(baseMetaVersionBytes),
                    currentMetaVersionSha256 = Sha256Hex(currentMetaVersionBytes),
                    snapshotSha256 = Sha256Hex(snapshotBytes),
                });
            }

            string[] hotfixAssemblyNames = NormalizeAssemblyNames(options.HotfixAssemblyNames);
            if (hotfixAssemblyNames.Length == 0)
            {
                hotfixAssemblyNames = names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            EnsureUnique(hotfixAssemblyNames, "hotfixAssemblyNames");
            HashSet<string> hotfixNameSet = new HashSet<string>(hotfixAssemblyNames,
                StringComparer.OrdinalIgnoreCase);
            foreach (string dheName in names)
            {
                if (!hotfixNameSet.Contains(dheName))
                {
                    throw new BuildFailedException(
                        "Every DHE assembly must also be present in hotfixAssemblyNames: " + dheName);
                }
            }

            string[] loadList = hotfixAssemblyNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => name + DllExtension).ToArray();
            if (options.HotfixLoadOrderResolver != null)
            {
                loadList = options.HotfixLoadOrderResolver(loadList) ??
                    throw new BuildFailedException("DHE hotfix load order resolver returned null.");
            }
            File.WriteAllText(Path.Combine(currentAssetRoot, "HotfixFileList.txt"),
                SerializeStringArray(loadList), System.Text.Encoding.UTF8);
            if (options.DependencyMapWriter != null)
            {
                options.DependencyMapWriter(currentAssetRoot);
            }

            string[] aotAssemblies = NormalizeAssemblyNames(options.AotMetadataAssemblyNames);
            EnsureUnique(aotAssemblies, "AotMetadataAssemblyNames");
            string strippedAotRoot = aotAssemblies.Length == 0
                ? string.Empty : RequireDirectory(options.StrippedAotRoot, "DHE stripped AOT root");
            string fallbackAotRoot = string.IsNullOrWhiteSpace(options.AotMetadataFallbackRoot)
                ? string.Empty : RequireDirectory(options.AotMetadataFallbackRoot,
                    "DHE fallback AOT metadata root");
            if (!string.IsNullOrWhiteSpace(options.AotMetadataFallbackManifestPath) &&
                string.IsNullOrWhiteSpace(fallbackAotRoot))
            {
                throw new BuildFailedException(
                    "DHE AOT metadata fallback manifest requires AotMetadataFallbackRoot.");
            }
            DheAotMetadataManifest fallbackManifest = null;
            string fallbackManifestSha256 = string.Empty;
            if (!string.IsNullOrWhiteSpace(fallbackAotRoot))
            {
                fallbackManifest = ValidateAotMetadataFallback(options, fallbackAotRoot,
                    NormalizeAssemblyNames(aotAssemblies));
                fallbackManifestSha256 = Sha256Hex(File.ReadAllBytes(
                    RequireFile(options.AotMetadataFallbackManifestPath,
                        "DHE AOT metadata fallback manifest")));
            }
            List<DheRuntimePlanAotMetadata> aotMetadata = new List<DheRuntimePlanAotMetadata>();
            List<KeyValuePair<string, byte[]>> aotMetadataSet =
                new List<KeyValuePair<string, byte[]>>();
            foreach (string name in NormalizeAssemblyNames(aotAssemblies))
            {
                string source = Path.Combine(strippedAotRoot, name + DllExtension);
                string sourceKind = "current-stripped";
                if (!File.Exists(source) && !string.IsNullOrWhiteSpace(fallbackAotRoot))
                {
                    source = Path.Combine(fallbackAotRoot, name + DllExtension);
                    sourceKind = "fallback-root";
                }
                source = RequireFile(source, name + " AOT metadata");
                byte[] sourceBytes = File.ReadAllBytes(source);
                File.WriteAllBytes(Path.Combine(currentAssetRoot, name + ".bytes"), sourceBytes);
                aotMetadataSet.Add(new KeyValuePair<string, byte[]>(name, sourceBytes));
                aotMetadata.Add(new DheRuntimePlanAotMetadata
                {
                    assemblyName = name,
                    sourceKind = sourceKind,
                    sha256 = Sha256Hex(sourceBytes),
                    manifestSha256 = fallbackManifest == null ? string.Empty : fallbackManifestSha256,
                    path = ResolveRuntimeAssetPath(options, projectRoot,
                        Path.Combine(currentAssetRoot, name + ".bytes")),
                });
            }
            foreach (DheRuntimePlanAotMetadata metadata in aotMetadata)
            {
                string name = NormalizeAssemblyName(metadata.assemblyName);
                string handoffFileName = name + ".aot-metadata.bytes";
                File.Copy(Path.Combine(currentAssetRoot, name + ".bytes"),
                    Path.Combine(handoffRoot, handoffFileName), true);
                handoffAotMetadata.Add(new DheRuntimePlanAotMetadata
                {
                    assemblyName = name,
                    sourceKind = metadata.sourceKind,
                    sha256 = metadata.sha256,
                    manifestSha256 = metadata.manifestSha256,
                    path = handoffFileName,
                });
            }
            File.WriteAllText(Path.Combine(currentAssetRoot, "AotFileList.txt"),
                SerializeStringArray(NormalizeAssemblyNames(aotAssemblies)),
                System.Text.Encoding.UTF8);

            DheRuntimePlanDocument runtimePlan = new DheRuntimePlanDocument
            {
                schemaVersion = 1,
                format = "hybridclr.dhe-runtime-asset-plan.json",
                currentAssemblySetSha256 = Sha256NamedByteSet(currentSet),
				runtimeAssetRoot = ResolveRuntimeAssetPath(options, projectRoot,
					currentAssetRoot).TrimEnd('/') + "/",
                baseMetaVersionAssetRoot = ResolveRuntimeAssetPath(options, projectRoot,
                    baseMetaVersionRoot).TrimEnd('/') + "/",
                selection = "embedded-base-metaversion",
                aotMetadataSetId = Sha256NamedByteSet(aotMetadataSet),
                aotMetadata = aotMetadata.ToArray(),
                aotMetadataSets = Array.Empty<DheRuntimePlanAotMetadataSet>(),
                baseSelections = Array.Empty<DheRuntimePlanBaseSelection>(),
                aotMetadataManifestSha256 = fallbackManifest == null ? string.Empty : fallbackManifestSha256,
                assemblies = runtimeRecords.ToArray(),
            };
            File.WriteAllText(Path.Combine(currentAssetRoot, "DheRuntimePlan.json"),
                JsonUtility.ToJson(runtimePlan, true), new System.Text.UTF8Encoding(false));
            DheRuntimePlanHandoffDocument handoffPlan = new DheRuntimePlanHandoffDocument
            {
                schemaVersion = 1,
                format = "hybridclr.dhe-runtime-handoff-plan.json",
                baseAssemblySetSha256 = Sha256NamedByteSet(baselineSet),
                selection = "embedded-base-metaversion",
                aotMetadataManifestSha256 = fallbackManifest == null ? string.Empty : fallbackManifestSha256,
                aotMetadata = handoffAotMetadata.ToArray(),
                assemblies = handoffRecords.ToArray(),
            };
            string handoffPlanPath = Path.Combine(handoffRoot, "dhe-runtime-plan.json");
            File.WriteAllText(handoffPlanPath, JsonUtility.ToJson(handoffPlan, true),
                new System.Text.UTF8Encoding(false));
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            return new DheRuntimePlanResult
            {
                ProjectPlanPath = planPath,
                RuntimeAssetRoot = currentAssetRoot,
                RuntimePlanPath = Path.Combine(currentAssetRoot, "DheRuntimePlan.json"),
                HandoffRoot = handoffRoot,
                HandoffPlanPath = handoffPlanPath,
                AssemblyNames = names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
            };
        }

        public static BuildReport BuildPlayer(DhePlayerBuildOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            string outputPath = Path.GetFullPath(options.OutputPath);
            string baselineRoot = RequireDirectory(options.BaselineAotRoot, "DHE AOT baseline root");
            if (options.Scenes == null || options.Scenes.Length == 0)
            {
                throw new BuildFailedException("DHE Player build requires at least one scene.");
            }

            BuildTargetGroup group = EnsureActiveBuildTarget(options.Target);
            if (options.CleanBuild)
            {
                ClearPlayerOutput(outputPath, options.Target);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            string previousBaseline = Environment.GetEnvironmentVariable(BaselineEnvironmentVariable);
            string previousPhase = Environment.GetEnvironmentVariable(BuildPhaseEnvironmentVariable);
            Environment.SetEnvironmentVariable(BaselineEnvironmentVariable, baselineRoot);
            Environment.SetEnvironmentVariable(BuildPhaseEnvironmentVariable, FinalPlayerBuildPhase);
            try
            {
                BuildPlayerOptions buildOptions = new BuildPlayerOptions
                {
                    scenes = options.Scenes,
                    locationPathName = outputPath,
                    target = options.Target,
                    targetGroup = group,
#if UNITY_2021_2_OR_NEWER
                    extraScriptingDefines = new[] { BasePlayerScriptingDefine },
#endif
                    // Tuanjie 1.10 (Unity 2022.3 lineage) exposes
                    // CleanBuildCache rather than Unity's newer CleanBuild
                    // flag.  This is the portable cache-invalidation flag
                    // supported by the target editor and used by the
                    // existing HybridCLR strip-AOT path as well.
                    options = options.BuildOptions |
                        (options.CleanBuild ? BuildOptions.CleanBuildCache : BuildOptions.None),
                };
                // The package owns the baseline binding and result contract,
                // while a project may wrap the actual Unity build with its
                // resource scopes, signing settings, or platform options.
                // Keeping this callback at the package boundary avoids making
                // the generic DHE workflow depend on a project's build
                // framework (YooAsset, Addressables, or a custom builder).
                BuildReport report = options.BuildPlayerCallback == null
                    ? BuildPipeline.BuildPlayer(buildOptions)
                    : options.BuildPlayerCallback(buildOptions);
                if (report == null)
                {
                    throw new BuildFailedException("DHE Player build callback returned no BuildReport.");
                }
                if (report.summary.result != BuildResult.Succeeded)
                {
                    throw new BuildFailedException("DHE Player build failed: " + report.summary.result);
                }
                if (options.NativeFinalizeOptions != null)
                {
                    options.NativeFinalizeOptions.RebuildPlayer = true;
                    DheNativeFinalizeResult nativeResult = FinalizeProjectNativeCode(
                        options.NativeFinalizeOptions);
                    options.NativeFinalizeResultCallback?.Invoke(nativeResult);
                }
                // Baseline assembly inputs must remain bound while the
                // generated-C++ finalizer re-evaluates Bee. Restoring them
                // first invalidates UnityLinker/IL2CPP and lets the frontend
                // overwrite freshly injected guards.
                options.GeneratedCppFinalizeCallback?.Invoke(report);
                return report;
            }
            finally
            {
                try
                {
                    RestoreBaselineAssemblyInputs();
                }
                finally
                {
                    Environment.SetEnvironmentVariable(BaselineEnvironmentVariable, previousBaseline);
                    Environment.SetEnvironmentVariable(BuildPhaseEnvironmentVariable, previousPhase);
                }
            }
        }

        /// <summary>
        /// Adds method-level DHE dispatch guards to the C++ generated by a
        /// scripts-only Player pass. This replaces the old shell transformer
        /// and deliberately stays at the package boundary: a project adapter
        /// supplies MV documents and the target editor's generated-C++ root.
        /// </summary>
        public static DheNativeGuardResult InjectGeneratedGuards(DheNativeGuardOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (!options.GuardAllMethods)
                throw new BuildFailedException(
                    "DHE Base Players require universal guards for resource-only updates.");
            string generatedRoot = RequireDirectory(options.GeneratedCppRoot, "DHE generated C++ root");
            string[] mvPaths = (options.MvJsonPaths ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (mvPaths.Length == 0) throw new BuildFailedException("DHE guard injection requires at least one MV JSON.");

            List<DheGuardMethod> requested = new List<DheGuardMethod>();
            int changedRequested = 0;
            int changedUnsupported = 0;
            HashSet<string> requestedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string mvPath in mvPaths)
            {
                RequireFile(mvPath, "DHE MV JSON");
                DheMvDocument mv = ReadMvDocument(mvPath);
                if (mv == null || string.IsNullOrWhiteSpace(mv.assemblyName) ||
                    mv.methods == null || mv.methods.Any(method => method == null ||
                        string.IsNullOrWhiteSpace(method.identity) ||
                        !IsSha256(method.stableId)) ||
                    requestedAssemblies.Contains(mv.assemblyName))
                    throw new BuildFailedException("DHE MV JSON is invalid or duplicated: " + mvPath);
                requestedAssemblies.Add(mv.assemblyName);
                foreach (DheMvMethod method in mv.methods.Where(item => item != null &&
                    item.token != 0 &&
                    (item.flags & 8u) != 0 &&
                    !item.isAbstract && !item.isPInvoke))
                {
                    requested.Add(new DheGuardMethod
                    {
                        AssemblyName = mv.assemblyName,
                        ManagedId = method.identity,
                        StableMethodIdSha256 = method.stableId,
                        MethodToken = method.token,
                        MethodName = method.name,
                        DeclaringType = method.declaringType,
                        ReturnType = method.returnType,
                        ManagedParameterTypes = method.parameterTypes ?? Array.Empty<string>(),
                        IsStatic = method.isStatic,
                        HasThis = method.hasThis || !method.isStatic,
                        DeclaringTypeIsValueType = method.declaringTypeIsValueType,
                        GenericParameterCount = checked((uint)method.genericParameterCount),
                        DeclaringTypeGenericParameterCount = checked((uint)method.declaringTypeGenericParameterCount),
                        IsChanged = false,
                    });
                }
            }
            // A release with no changed method bodies is a valid DHE no-op.
            // Emit an empty, auditable native manifest and let the final
            // Player build continue without injecting a guard.
            Dictionary<string, List<DheCppDefinition>> definitions = requested.Count == 0
                ? new Dictionary<string, List<DheCppDefinition>>(StringComparer.OrdinalIgnoreCase)
                : IndexCppDefinitions(generatedRoot);
            Dictionary<string, List<string>> generatedFunctions = requested.Count == 0
                ? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                : IndexGeneratedMethodFunctions(generatedRoot, requested);
            List<DheNativeManifestMethod> manifestMethods = new List<DheNativeManifestMethod>();
            Dictionary<string, DheNativeManifestIssue> unsupported =
                new Dictionary<string, DheNativeManifestIssue>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, DheNativeManifestIssue> interpreterOnly =
                new Dictionary<string, DheNativeManifestIssue>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> nativeOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<DheNativeManifestMethod>> methodsByFile =
                new Dictionary<string, List<DheNativeManifestMethod>>(StringComparer.OrdinalIgnoreCase);
            foreach (DheGuardMethod method in requested)
            {
                string methodKey = GetManagedMethodKey(method.AssemblyName, method.MethodToken);
                List<DheCppDefinition> matches = new List<DheCppDefinition>();
                if (generatedFunctions.TryGetValue(methodKey, out List<string> functionNames))
                {
                    foreach (string functionName in functionNames)
                    {
                        if (!definitions.TryGetValue(functionName, out List<DheCppDefinition> exactDefinitions) ||
                            exactDefinitions.Count != 1)
                        {
                            throw new BuildFailedException("DHE IL2CPP method index resolved '" + functionName +
                                "' for " + method.AssemblyName + "/" + method.MethodToken +
                                ", but found " + (exactDefinitions == null ? 0 : exactDefinitions.Count) +
                                " generated definitions.");
                        }
                        DheCppDefinition definition = exactDefinitions[0];
                        if (!string.IsNullOrWhiteSpace(definition.ManagedSignature) &&
                            !ManagedSignatureMatches(method, definition.ManagedSignature))
                        {
                            throw new BuildFailedException("DHE IL2CPP method index conflicts with the managed " +
                                "signature comment for '" + functionName + "'. Assembly='" +
                                method.AssemblyName + "', token=" + method.MethodToken + ", managedId='" +
                                method.ManagedId + "', comment='" + definition.ManagedSignature + "'.");
                        }
                        matches.Add(definition);
                    }
                }
                bool generic = method.GenericParameterCount > 0 || method.DeclaringTypeGenericParameterCount > 0;
                if (matches.Count == 0)
                {
                    if (options.GuardAllMethods)
                    {
                        AddNativeManifestIssue(interpreterOnly, method, "no generated native entry");
                        continue;
                    }
                    throw new BuildFailedException("Expected " + (generic ? "one or more" : "one") +
                        " generated definition for '" + method.DeclaringType + "::" + method.MethodName +
                        "', found " + matches.Count + ".");
                }
                if (!generic && matches.Count != 1)
                    throw new BuildFailedException("Expected one generated definition for '" +
                        method.DeclaringType + "::" + method.MethodName + "', found " + matches.Count + ".");
                if (matches.Count > 4096)
                    throw new BuildFailedException("DHE generated definition fan-out is unexpectedly large for '" +
                        method.DeclaringType + "::" + method.MethodName + "': " + matches.Count + ".");
                foreach (DheCppDefinition definition in matches)
                {
                    DheNativeManifestMethod resolved = ResolveNativeMethod(method, definition);
                    try
                    {
                        _ = CreateGuard(resolved);
                    }
                    catch (Exception exception)
                    {
                        bool firstIssue = AddNativeManifestIssue(unsupported, method, exception.Message);
                        if (firstIssue && method.IsChanged) changedUnsupported++;
                        continue;
                    }
                    string nativeIdentity = Path.GetFullPath(definition.File) + "\n" + definition.FunctionName;
                    string managedIdentity = method.AssemblyName + "/" + method.StableMethodIdSha256;
                    if (nativeOwners.TryGetValue(nativeIdentity, out string existingOwner) &&
                        !string.Equals(existingOwner, managedIdentity, StringComparison.Ordinal))
                    {
                        throw new BuildFailedException("DHE native entry maps to multiple managed methods: " +
                            definition.FunctionName + " => " + existingOwner + ", " + managedIdentity + ".");
                    }
                    nativeOwners[nativeIdentity] = managedIdentity;
                    manifestMethods.Add(resolved);
                    if (!methodsByFile.TryGetValue(definition.File, out List<DheNativeManifestMethod> fileMethods))
                    {
                        fileMethods = new List<DheNativeManifestMethod>();
                        methodsByFile.Add(definition.File, fileMethods);
                    }
                    fileMethods.Add(resolved);
                }
            }
            if (options.RequireCompleteCoverage && unsupported.Count > 0)
                throw new BuildFailedException("DHE C++ injection has unsupported methods: " + string.Join("; ",
                    unsupported.Values.Select(issue => issue.id + ": " + string.Join(", ", issue.reasons))));
            if (options.GuardAllMethods && requested.Count > 0 && manifestMethods.Count == 0)
                throw new BuildFailedException(
                    "DHE universal guard generation resolved zero native entries; the Base Player would not support resource-only updates.");

            int transformed = 0;
            foreach (KeyValuePair<string, List<DheNativeManifestMethod>> pair in methodsByFile)
            {
                string source = File.ReadAllText(pair.Key, Encoding.UTF8);
                Dictionary<string, string> existingBlocks = IndexExistingGuardBlocks(source);
                List<Tuple<int, string>> insertions = new List<Tuple<int, string>>();
                foreach (DheNativeManifestMethod method in pair.Value)
                {
                    string beginMarker = GetGuardMarker(NativeGuardBeginPrefix, method);
                    if (existingBlocks.TryGetValue(beginMarker, out string actual))
                    {
                        string expected = NormalizeGuardBlock(CreateGuard(method));
                        if (!string.Equals(actual, expected, StringComparison.Ordinal))
                        {
                            throw new BuildFailedException("Existing DHE guard block differs from the expected " +
                                "generated block: " + method.functionName + "/" + method.methodToken);
                        }
                        continue;
                    }
                    if (method.bodyStart <= 0 || method.bodyStart > source.Length ||
                        source[method.bodyStart - 1] != '{')
                        throw new BuildFailedException("Could not find a definition for DHE function '" + method.functionName + "'.");
                    if (!Regex.IsMatch(method.nativeSignature, @"\bconst\s+RuntimeMethod\s*\*\s*method\b"))
                        throw new BuildFailedException("DHE function has no RuntimeMethod parameter: " + method.functionName);
                    insertions.Add(Tuple.Create(method.bodyStart, "\r\n" + CreateGuard(method)));
                }
                if (insertions.Count > 0) source = ApplyInsertions(source, insertions);
                transformed += insertions.Count;
                if (insertions.Count > 0 && !Regex.IsMatch(source, @"(?m)^#include\s+""hybridclr/DheRuntime\.h"""))
                    source = "#include \"hybridclr/DheRuntime.h\"\r\n#include \"hybridclr/Il2CppCompatibleDef.h\"\r\n" + source;
                if (insertions.Count > 0) File.WriteAllText(pair.Key, source, new UTF8Encoding(false));
            }

            string manifestPath = Path.GetFullPath(options.OutputManifestPath ??
                Path.Combine(generatedRoot, "dhe-native-manifest.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath));
            DheNativeManifestDocument manifest = new DheNativeManifestDocument
            {
                schemaVersion = 1,
                resolverVersion = 3,
                abiContract = "il2cpp-generated-cpp-signature-v2",
                guardHashContract = NativeGuardHashContract,
				runtimeProtocol = NativeRuntimeProtocol,
                runtimeContract = NativeRuntimeContract,
                runtimeCapabilities = NativeRuntimeCapabilities,
                generatedCppRoot = generatedRoot,
                guardMode = "universal",
                changedMethodCount = changedRequested,
                supportedChangedMethodCount = changedRequested - changedUnsupported,
                unsupportedChangedMethodCount = changedUnsupported,
                guardedMethodCount = requested.Count,
                supportedGuardedMethodCount = requested.Count - interpreterOnly.Count - unsupported.Count,
                unsupportedGuardedMethodCount = unsupported.Count,
                interpreterOnlyMethodCount = interpreterOnly.Count,
                nativeEntryCount = manifestMethods.Count,
                methods = manifestMethods.ToArray(),
                interpreterOnlyMethods = interpreterOnly.Values.ToArray(),
                unsupportedChangedMethods = unsupported.Values.Where(issue => issue.isChanged).ToArray(),
            };
            File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest, true), new UTF8Encoding(false));
            string[] generatedCppPaths = manifestMethods.Select(method => Path.GetFullPath(method.sourceFile))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            return new DheNativeGuardResult
            {
                ManifestPath = manifestPath,
                RuntimeProtocol = NativeRuntimeProtocol,
                RuntimeContract = NativeRuntimeContract,
                RuntimeCapabilities = NativeRuntimeCapabilities,
                RequestedMethodCount = requested.Count,
                TransformedMethodCount = transformed,
                NativeEntryCount = manifestMethods.Count,
                UnsupportedMethodCount = unsupported.Count,
                GuardMode = "universal",
                GuardedMethodCount = requested.Count,
                SupportedGuardedMethodCount = requested.Count - interpreterOnly.Count - unsupported.Count,
                UnsupportedGuardedMethodCount = unsupported.Count,
                InterpreterOnlyMethodCount = interpreterOnly.Count,
                UnsupportedChangedMethodCount = changedUnsupported,
                GeneratedCppPaths = generatedCppPaths,
                NativeManifestSha256 = Sha256Hex(File.ReadAllBytes(manifestPath)),
                NativeGuardSourceSha256 = Sha256GuardBlockSet(manifestMethods, generatedRoot),
            };
        }

        /// <summary>
        /// Resolves a complete project plan against the current IL2CPP output,
        /// injects every supported guard and optionally re-evaluates the Player
        /// Bee graph. This is the project-independent native finalization step;
        /// adapters supply paths and consume the structured result only.
        /// </summary>
        public static DheNativeFinalizeResult FinalizeProjectNativeCode(
            DheNativeFinalizeOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            string projectRoot = RequireDirectory(options.ProjectRoot, "DHE project root");
            string planPath = RequireFile(options.ProjectPlanPath, "DHE project plan");
            DheProjectPlan plan = JsonUtility.FromJson<DheProjectPlan>(File.ReadAllText(planPath));
            if (plan == null || plan.schemaVersion != 1 || !plan.complete ||
                plan.assemblies == null || plan.assemblies.Length == 0)
            {
                throw new BuildFailedException("DHE native finalization requires a complete project plan: " +
                    planPath);
            }
            string planDirectory = Path.GetDirectoryName(planPath);
            string[] assemblyNames = plan.assemblies.Select(assembly =>
                    NormalizeAssemblyName(assembly == null ? null : assembly.assemblyName))
                .ToArray();
            if (assemblyNames.Any(string.IsNullOrWhiteSpace) ||
                assemblyNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != assemblyNames.Length)
            {
                throw new BuildFailedException("DHE project plan contains an empty or duplicate assembly.");
            }
            string[] mvPaths = plan.assemblies.Select(assembly => RequireFile(
                    ResolvePlanReference(planDirectory, assembly.baseMetaVersionJson),
                    NormalizeAssemblyName(assembly.assemblyName) + " Base MetaVersion JSON"))
                .ToArray();
            string generatedCppRoot = string.IsNullOrWhiteSpace(options.GeneratedCppRoot)
                ? FindGeneratedCppRoot(projectRoot, assemblyNames)
                : RequireDirectory(options.GeneratedCppRoot, "DHE generated C++ root");
            string manifestPath = string.IsNullOrWhiteSpace(options.OutputManifestPath)
                ? Path.Combine(projectRoot, "Library", "DHE", "dhe-native-manifest.json")
                : Path.GetFullPath(options.OutputManifestPath);
            DheNativeGuardResult guard = InjectGeneratedGuards(new DheNativeGuardOptions
            {
                MvJsonPaths = mvPaths,
                GeneratedCppRoot = generatedCppRoot,
                OutputManifestPath = manifestPath,
                RequireCompleteCoverage = options.RequireCompleteCoverage,
                GuardAllMethods = options.GuardAllMethods,
            });
            DheBeeRebuildResult rebuild = null;
            if (options.RebuildPlayer)
            {
                rebuild = RebuildPlayerFromGeneratedCpp(new DheBeeRebuildOptions
                {
                    ProjectRoot = projectRoot,
                    GeneratedCppRoot = generatedCppRoot,
                    LogPath = options.BeeLogPath,
                    MaxAttempts = options.BeeMaxAttempts,
                    TimeoutSeconds = options.BeeTimeoutSeconds,
                });
            }
            return new DheNativeFinalizeResult
            {
                ProjectPlanPath = planPath,
                GeneratedCppRoot = generatedCppRoot,
                AssemblyNames = assemblyNames,
                GuardResult = guard,
                BeeRebuildResult = rebuild,
            };
        }

        public static string FindGeneratedCppRoot(string projectRoot,
            IEnumerable<string> assemblyNames)
        {
            projectRoot = RequireDirectory(projectRoot, "DHE project root");
            string beeArtifactsRoot = RequireDirectory(Path.Combine(projectRoot, "Library", "Bee", "artifacts"),
                "DHE Bee artifacts root");
            string[] normalizedNames = NormalizeAssemblyNames(assemblyNames);
            if (normalizedNames.Length == 0)
                throw new BuildFailedException("DHE generated-C++ lookup requires an assembly name.");
            Regex ownerPattern = new Regex("^(?:" + string.Join("|", normalizedNames.Select(Regex.Escape)) +
                @")(?:__\d+)?\.cpp$", RegexOptions.IgnoreCase);
            string[] roots = Directory.GetFiles(beeArtifactsRoot, "*.cpp", SearchOption.AllDirectories)
                .Where(path => ownerPattern.IsMatch(Path.GetFileName(path)))
                .Select(Path.GetDirectoryName).Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(Directory.GetLastWriteTimeUtc).ToArray();
            if (roots.Length == 0)
                throw new BuildFailedException("DHE generated C++ root was not produced for: " +
                    string.Join(", ", normalizedNames));
            return Path.GetFullPath(roots[0]);
        }

        /// <summary>
        /// Re-evaluates the Player Bee graph after DHE guards have been added
        /// to the final generated C++ snapshot. BuildPipeline may regenerate
        /// IL2CPP output even after a scripts-only pass, so this native-only
        /// step binds the shipped Player to the audited guard source. The
        /// editor-owned executable is invoked directly without a shell.
        /// </summary>
        public static DheBeeRebuildResult RebuildPlayerFromGeneratedCpp(
            DheBeeRebuildOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            string projectRoot = RequireDirectory(options.ProjectRoot, "DHE project root");
            string generatedRoot = RequireDirectory(options.GeneratedCppRoot,
                "DHE generated C++ root");
            string beeRoot = RequireDirectory(Path.Combine(projectRoot, "Library", "Bee"),
                "Unity Bee root");
            string expectedGeneratedPrefix = Path.GetFullPath(
                Path.Combine(beeRoot, "artifacts")).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!generatedRoot.StartsWith(expectedGeneratedPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new BuildFailedException(
                    "DHE generated C++ root must be owned by the current Bee graph: " +
                    generatedRoot);
            }

            string[] dagPaths = Directory.GetFiles(beeRoot, "Player*.dag",
                SearchOption.TopDirectoryOnly);
            if (dagPaths.Length == 0)
            {
                throw new BuildFailedException(
                    "Unity did not produce a Player Bee DAG under: " + beeRoot);
            }
            string dagPath = dagPaths.OrderByDescending(File.GetLastWriteTimeUtc).First();
            string beeBackendPath = ResolveBeeBackendPath();
            int maxAttempts = options.MaxAttempts <= 0 ? 3 : options.MaxAttempts;
            int timeoutSeconds = options.TimeoutSeconds <= 0 ? 600 : options.TimeoutSeconds;
            string logPath = Path.GetFullPath(string.IsNullOrWhiteSpace(options.LogPath)
                ? Path.Combine(projectRoot, "Library", "DheBeeRebuild.log")
                : options.LogPath);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));

            StringBuilder log = new StringBuilder();
            int exitCode = -1;
            int attempts = 0;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                attempts = attempt;
                System.Diagnostics.ProcessStartInfo start =
                    new System.Diagnostics.ProcessStartInfo(beeBackendPath)
                    {
                        WorkingDirectory = projectRoot,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    };
                start.ArgumentList.Add("-C");
                start.ArgumentList.Add(projectRoot);
                start.ArgumentList.Add("-R");
                start.ArgumentList.Add(dagPath);
                start.ArgumentList.Add("Player");
                using (System.Diagnostics.Process process =
                       System.Diagnostics.Process.Start(start))
                {
                    if (process == null)
                        throw new BuildFailedException("Unable to start Unity Bee backend.");
                    Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                    Task<string> stderr = process.StandardError.ReadToEndAsync();
                    bool exited = process.WaitForExit(checked(timeoutSeconds * 1000));
                    if (!exited)
                    {
                        try
                        {
                            process.Kill();
                            process.WaitForExit();
                        }
                        catch { }
                    }
                    Task.WaitAll(stdout, stderr);
                    log.AppendLine("attempt=" + attempt);
                    log.Append(stdout.Result);
                    log.Append(stderr.Result);
                    if (!exited)
                    {
                        File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(false));
                        throw new BuildFailedException(
                            "Unity Bee rebuild timed out after " + timeoutSeconds +
                            " seconds. See: " + logPath);
                    }
                    exitCode = process.ExitCode;
                }
                if (exitCode == 0) break;
                // Bee uses 4 to request graph re-evaluation after an input
                // changes. A bounded retry is the editor's normal contract.
                if (exitCode != 4) break;
            }
            File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(false));
            if (exitCode != 0)
            {
                throw new BuildFailedException("Unity Bee failed to rebuild the DHE Player " +
                    "(exit code " + exitCode + "). See: " + logPath);
            }
            return new DheBeeRebuildResult
            {
                BeeBackendPath = beeBackendPath,
                DagPath = dagPath,
                LogPath = logPath,
                Attempts = attempts,
                ExitCode = exitCode,
            };
        }

        private static string ResolveBeeBackendPath()
        {
            string executableName = Application.platform == RuntimePlatform.WindowsEditor
                ? "bee_backend.exe"
                : "bee_backend";
            string editorDirectory = Path.GetDirectoryName(EditorApplication.applicationPath);
            string contentsPath = EditorApplication.applicationContentsPath;
            string[] candidates = new[]
            {
                Path.Combine(contentsPath, executableName),
                Path.Combine(editorDirectory, "Data", executableName),
                Path.Combine(editorDirectory, executableName),
                Path.GetFullPath(Path.Combine(editorDirectory, "..", executableName)),
            }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string resolved = candidates.FirstOrDefault(File.Exists);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                throw new BuildFailedException("Unity Bee backend was not found. Checked: " +
                    string.Join(", ", candidates));
            }
            return resolved;
        }

        /// <summary>
        /// Unity's IFilterBuildAssemblies callback is only allowed to remove
        /// entries. Final DHE builds still need the baseline bytes at the
        /// original input paths, so replace those files in place immediately
        /// before the callback returns and restore them after BuildPlayer.
        /// </summary>
        internal static void PrepareBaselineAssemblyInputs(string[] assemblies,
            string baselineRoot, IEnumerable<string> assemblyNames)
        {
            if (assemblies == null)
            {
                throw new ArgumentNullException(nameof(assemblies));
            }
            string projectRoot = Path.GetFullPath(SettingsUtil.ProjectDir).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string projectPrefix = projectRoot + Path.DirectorySeparatorChar;
            string resolvedBaselineRoot = Path.GetFullPath(baselineRoot ?? string.Empty).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            try
            {
                foreach (string rawName in assemblyNames ?? Array.Empty<string>())
                {
                    string assemblyName = NormalizeAssemblyName(rawName);
                    if (string.IsNullOrWhiteSpace(assemblyName))
                    {
                        throw new BuildFailedException("DHE baseline assembly name is empty.");
                    }
                    string baselinePath = Path.Combine(resolvedBaselineRoot,
                        assemblyName + DllExtension);
                    if (!File.Exists(baselinePath))
                    {
                        throw new BuildFailedException(
                            "DHE AOT baseline assembly was not found: " + baselinePath);
                    }

                    string[] inputPaths = assemblies
                        .Where(path => string.Equals(
                            NormalizeAssemblyName(Path.GetFileNameWithoutExtension(path)),
                            assemblyName, StringComparison.OrdinalIgnoreCase))
                        .Select(Path.GetFullPath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (inputPaths.Length == 0)
                    {
                        throw new BuildFailedException(
                            "DHE baseline assembly has no Unity build input path: " +
                            assemblyName);
                    }

                    byte[] baselineBytes = File.ReadAllBytes(baselinePath);
                    foreach (string inputPath in inputPaths)
                    {
                        if (!inputPath.StartsWith(projectPrefix,
                                StringComparison.OrdinalIgnoreCase) ||
                            !File.Exists(inputPath))
                        {
                            throw new BuildFailedException(
                                "DHE baseline replacement requires a project-owned input path: " +
                                inputPath);
                        }
                        if (!BaselineAssemblyBackups.ContainsKey(inputPath))
                        {
                            BaselineAssemblyBackups.Add(inputPath, File.ReadAllBytes(inputPath));
                        }
                        File.WriteAllBytes(inputPath, baselineBytes);
                        Debug.Log("[DHE] use baseline bytes at Unity input: " + inputPath);
                    }
                }
            }
            catch
            {
                RestoreBaselineAssemblyInputs();
                throw;
            }
        }

        internal static void RestoreBaselineAssemblyInputs()
        {
            if (BaselineAssemblyBackups.Count == 0)
            {
                return;
            }
            Exception restoreFailure = null;
            foreach (KeyValuePair<string, byte[]> backup in BaselineAssemblyBackups)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup.Key));
                    File.WriteAllBytes(backup.Key, backup.Value);
                }
                catch (Exception exception)
                {
                    restoreFailure = restoreFailure ?? exception;
                }
            }
            BaselineAssemblyBackups.Clear();
            if (restoreFailure != null)
            {
                throw new BuildFailedException(
                    "Failed to restore Unity DHE assembly inputs: " + restoreFailure.Message);
            }
        }

        private static BuildTargetGroup EnsureActiveBuildTarget(BuildTarget target)
        {
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
            if (EditorUserBuildSettings.activeBuildTarget != target &&
                !EditorUserBuildSettings.SwitchActiveBuildTarget(group, target))
            {
                throw new BuildFailedException("Unable to switch active build target to " + target);
            }
            return group;
        }

        private static void ClearRuntimeAssets(string root)
        {
            ClearDhePayloadFiles(root);
        }

        private static void ClearDhePayloadFiles(string root)
        {
            // A DHE sidecar is identifiable by its MV or snapshot companion.
            // Use both the existing plan and sidecar names so removed DHE
            // assemblies are cleaned without touching legacy hotfix payloads.
            HashSet<string> generatedAssemblies = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            string planPath = Path.Combine(root, "DheRuntimePlan.json");
            if (File.Exists(planPath))
            {
                try
                {
                    DheRuntimePlanDocument previousPlan = JsonUtility.FromJson<DheRuntimePlanDocument>(
                        File.ReadAllText(planPath));
                    foreach (DheRuntimePlanAssembly record in previousPlan?.assemblies ??
                        Array.Empty<DheRuntimePlanAssembly>())
                    {
                        string name = NormalizeAssemblyName(record?.assemblyName);
                        if (!string.IsNullOrWhiteSpace(name)) generatedAssemblies.Add(name);
                    }
                }
                catch (Exception exception)
                {
                    throw new BuildFailedException(
                        "Previous DHE runtime plan is invalid: " + planPath + " (" +
                        exception.Message + ")");
                }
            }

            foreach (string path in Directory.GetFiles(root, "*.mv.bytes", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(path);
                generatedAssemblies.Add(fileName.Substring(0,
                    fileName.Length - ".mv.bytes".Length));
            }
            foreach (string path in Directory.GetFiles(root, "*.mv2.bytes", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(path);
                generatedAssemblies.Add(fileName.Substring(0,
                    fileName.Length - ".mv2.bytes".Length));
            }
            foreach (string path in Directory.GetFiles(root, "*.aot-snapshot.bytes",
                SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(path);
                generatedAssemblies.Add(fileName.Substring(0,
                    fileName.Length - ".aot-snapshot.bytes".Length));
            }

            foreach (string name in generatedAssemblies)
            {
                foreach (string suffix in new[] { ".dll.bytes", ".baseline.dll.bytes",
                             ".mv.bytes", ".mv2.bytes", ".aot-snapshot.bytes" })
                {
                    string path = Path.Combine(root, name + suffix);
                    if (File.Exists(path)) File.Delete(path);
                    if (File.Exists(path + ".meta")) File.Delete(path + ".meta");
                }
            }
            // The lower-case name belonged to the retired experimental
            // runner. Remove it as well so an ignored asset from an earlier
            // checkout cannot shadow the canonical plan in a Player build.
            foreach (string fileName in new[] { "DheRuntimePlan.json", "dhe-runtime-plan.json",
                "dhe-resource-update.json", "dhe-resource-update-validation.json", "DheSmokeConfig.json" })
            {
                string path = Path.Combine(root, fileName);
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".meta")) File.Delete(path + ".meta");
            }
            string resourcePayloadRoot = Path.Combine(root, "payload");
            if (Directory.Exists(resourcePayloadRoot)) Directory.Delete(resourcePayloadRoot, true);
            if (File.Exists(resourcePayloadRoot + ".meta")) File.Delete(resourcePayloadRoot + ".meta");
        }

        private static void ClearBaseMetaVersionFiles(string root)
        {
            if (!Directory.Exists(root))
            {
                return;
            }
            foreach (string pattern in new[] { "*.mv.bytes", "*.mv2.bytes" })
            {
                foreach (string path in Directory.GetFiles(root, pattern,
                             SearchOption.TopDirectoryOnly))
                {
                    File.Delete(path);
                    if (File.Exists(path + ".meta")) File.Delete(path + ".meta");
                }
            }
        }

        private static void ClearPlayerOutput(string outputPath, BuildTarget target)
        {
            string full = Path.GetFullPath(outputPath);
            string parent = Path.GetDirectoryName(full);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent,
                    Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException("DHE Player output path is too broad to clean: " + full);

            if (Directory.Exists(full)) Directory.Delete(full, true);
            if (File.Exists(full)) File.Delete(full);
            if (target == BuildTarget.StandaloneWindows || target == BuildTarget.StandaloneWindows64)
            {
                string data = Path.Combine(parent, Path.GetFileNameWithoutExtension(full) + "_Data");
                if (Directory.Exists(data)) Directory.Delete(data, true);
            }
        }

        private static string ReadTextWithoutBom(string path)
        {
            return File.ReadAllText(path, new UTF8Encoding(false)).TrimStart('\uFEFF');
        }

        // Unity's JsonUtility rejects null numeric values in the full MV
        // method list (added/removed methods carry currentToken=null). The
        // host's MV document is still JSON, so use a small structural scanner
        // here rather than a shell parser or a fragile line-based match.
        private static DheMvDocument ReadMvDocument(string path)
        {
            string json = ReadTextWithoutBom(path);
            DheMvDocument result = new DheMvDocument
            {
                assemblyName = ReadJsonString(json, "assemblyName"),
                methods = Array.Empty<DheMvMethod>(),
            };
            int methodsKey = json.IndexOf("\"methods\"", StringComparison.Ordinal);
            int arrayStart = methodsKey < 0 ? -1 : json.IndexOf('[', methodsKey);
            if (arrayStart < 0) return result;
            List<DheMvMethod> methods = new List<DheMvMethod>();
            foreach (string objectText in ExtractJsonObjects(json, arrayStart))
            {
                methods.Add(new DheMvMethod
                {
                    identity = ReadJsonString(objectText, "identity"),
                    stableId = ReadJsonString(objectText, "stableId"),
                    name = ReadJsonString(objectText, "name"),
                    token = checked((uint)ReadJsonInt(objectText, "token")),
                    flags = checked((uint)ReadJsonInt(objectText, "flags")),
                    declaringType = ReadJsonString(objectText, "declaringType"),
                    returnType = ReadJsonString(objectText, "returnType"),
                    parameterTypes = ReadJsonStringArray(objectText, "parameterTypes"),
                    isStatic = ReadJsonBool(objectText, "isStatic"),
                    isAbstract = ReadJsonBool(objectText, "isAbstract"),
                    isPInvoke = ReadJsonBool(objectText, "isPInvoke"),
                    hasThis = ReadJsonBool(objectText, "hasThis"),
                    declaringTypeIsValueType = ReadJsonBool(objectText, "declaringTypeIsValueType"),
                    genericParameterCount = ReadJsonInt(objectText, "genericParameterCount"),
                    declaringTypeGenericParameterCount = ReadJsonInt(objectText, "declaringTypeGenericParameterCount"),
                });
            }
            result.methods = methods.ToArray();
            return result;
        }

        private static IEnumerable<string> ExtractJsonObjects(string json, int arrayStart)
        {
            bool quoted = false;
            bool escaped = false;
            int depth = 0;
            int objectStart = -1;
            for (int index = arrayStart + 1; index < json.Length; index++)
            {
                char character = json[index];
                if (quoted)
                {
                    if (escaped) escaped = false;
                    else if (character == '\\') escaped = true;
                    else if (character == '"') quoted = false;
                    continue;
                }
                if (character == '"') { quoted = true; continue; }
                if (character == '{')
                {
                    if (depth == 0) objectStart = index;
                    depth++;
                }
                else if (character == '}' && depth > 0)
                {
                    depth--;
                    if (depth == 0 && objectStart >= 0)
                    {
                        yield return json.Substring(objectStart, index - objectStart + 1);
                        objectStart = -1;
                    }
                }
                else if (character == ']' && depth == 0) yield break;
            }
        }

        private static string ReadJsonString(string json, string property)
        {
            Match match = Regex.Match(json,
                "\\\"" + Regex.Escape(property) + "\\\"\\s*:\\s*\\\"(?<value>(?:\\\\.|[^\\\"\\\\])*)\\\"",
                RegexOptions.Singleline);
            return match.Success ? UnescapeJsonString(match.Groups["value"].Value) : string.Empty;
        }

        private static int ReadJsonInt(string json, string property)
        {
            Match match = Regex.Match(json,
                "\\\"" + Regex.Escape(property) + "\\\"\\s*:\\s*(?<value>-?[0-9]+)");
            return match.Success && int.TryParse(match.Groups["value"].Value, out int value) ? value : 0;
        }

        private static bool ReadJsonBool(string json, string property)
        {
            Match match = Regex.Match(json,
                "\\\"" + Regex.Escape(property) + "\\\"\\s*:\\s*(?<value>true|false)");
            return match.Success && string.Equals(match.Groups["value"].Value, "true", StringComparison.Ordinal);
        }

        private static string[] ReadJsonStringArray(string json, string property)
        {
            Match match = Regex.Match(json,
                "\\\"" + Regex.Escape(property) + "\\\"\\s*:\\s*\\[",
                RegexOptions.Singleline);
            if (!match.Success) return Array.Empty<string>();
            List<string> values = new List<string>();
            int cursor = match.Index + match.Length;
            while (cursor < json.Length)
            {
                while (cursor < json.Length && char.IsWhiteSpace(json[cursor])) cursor++;
                if (cursor < json.Length && json[cursor] == ']') return values.ToArray();
                if (cursor >= json.Length || json[cursor] != '"')
                    throw new BuildFailedException("DHE JSON property '" + property +
                        "' is not an array of strings.");

                int valueStart = ++cursor;
                bool escaped = false;
                while (cursor < json.Length)
                {
                    char character = json[cursor];
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        break;
                    }
                    cursor++;
                }
                if (cursor >= json.Length)
                    throw new BuildFailedException("DHE JSON property '" + property +
                        "' contains an unterminated string.");
                values.Add(UnescapeJsonString(json.Substring(valueStart, cursor - valueStart)));
                cursor++;
                while (cursor < json.Length && char.IsWhiteSpace(json[cursor])) cursor++;
                if (cursor < json.Length && json[cursor] == ']') return values.ToArray();
                if (cursor >= json.Length || json[cursor] != ',')
                    throw new BuildFailedException("DHE JSON property '" + property +
                        "' has an invalid string array separator.");
                cursor++;
            }
            throw new BuildFailedException("DHE JSON property '" + property +
                "' contains an unterminated string array.");
        }

        private static string UnescapeJsonString(string value)
        {
            StringBuilder result = new StringBuilder(value.Length);
            for (int index = 0; index < value.Length; index++)
            {
                if (value[index] != '\\' || index + 1 >= value.Length)
                {
                    result.Append(value[index]);
                    continue;
                }
                char escape = value[++index];
                switch (escape)
                {
                    case '"': result.Append('"'); break;
                    case '\\': result.Append('\\'); break;
                    case '/': result.Append('/'); break;
                    case 'b': result.Append('\b'); break;
                    case 'f': result.Append('\f'); break;
                    case 'n': result.Append('\n'); break;
                    case 'r': result.Append('\r'); break;
                    case 't': result.Append('\t'); break;
                    case 'u':
                        if (index + 4 >= value.Length) throw new BuildFailedException("Invalid JSON unicode escape.");
                        string hex = value.Substring(index + 1, 4);
                        result.Append((char)Convert.ToInt32(hex, 16));
                        index += 4;
                        break;
                    default: result.Append(escape); break;
                }
            }
            return result.ToString();
        }

        private static Dictionary<string, List<DheCppDefinition>> IndexCppDefinitions(string root)
        {
            Regex pattern = new Regex(
                @"^(?<signature>[^\r\n{};]*\b(?<function>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<parameters>[^\r\n{};]*)\)\s*)$",
                RegexOptions.Compiled);
            Dictionary<string, List<DheCppDefinition>> result =
                new Dictionary<string, List<DheCppDefinition>>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(root, "*.cpp", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file, Encoding.UTF8);
                int lineStart = 0;
                while (lineStart < text.Length)
                {
                    int newline = text.IndexOf('\n', lineStart);
                    int lineEnd = newline < 0 ? text.Length : newline;
                    if (lineEnd > lineStart && text[lineEnd - 1] == '\r') lineEnd--;
                    int lineLength = lineEnd - lineStart;
                    int brace = lineLength == 0 ? -1 : text.IndexOf('{', lineStart, lineLength);
                    int signatureEnd = brace >= 0 ? brace : lineEnd;
                    int signatureLength = signatureEnd - lineStart;
                    if (brace < 0 && newline >= 0)
                    {
                        int next = newline + 1;
                        while (next < text.Length && (text[next] == ' ' || text[next] == '\t' || text[next] == '\r')) next++;
                        if (next < text.Length && text[next] == '{') brace = next;
                    }
                    string line = signatureLength > 0 && brace >= 0 &&
                        text.IndexOf("_m", lineStart, signatureLength, StringComparison.Ordinal) >= 0 &&
                        text.IndexOf('(', lineStart, signatureLength) >= 0
                            ? text.Substring(lineStart, signatureLength)
                            : string.Empty;
                    Match match = line.Length == 0 ? Match.Empty : pattern.Match(line);
                    if (!match.Success)
                    {
                        if (newline < 0) break;
                        lineStart = newline + 1;
                        continue;
                    }
                    string function = match.Groups["function"].Value;
                    if (!result.TryGetValue(function, out List<DheCppDefinition> definitions))
                    {
                        definitions = new List<DheCppDefinition>();
                        result.Add(function, definitions);
                    }
                    definitions.Add(new DheCppDefinition
                    {
                        File = Path.GetFullPath(file),
                        Signature = match.Groups["signature"].Value,
                        FunctionName = function,
                        ParametersText = match.Groups["parameters"].Value,
                        BodyStart = brace + 1,
                        ManagedSignature = ReadPreviousManagedSignature(text, lineStart),
                    });
                    if (newline < 0) break;
                    lineStart = newline + 1;
                }
            }
            return result;
        }

        private static Dictionary<string, List<string>> IndexGeneratedMethodFunctions(string root,
            IReadOnlyCollection<DheGuardMethod> requestedMethods)
        {
            List<DheCodeGenModule> modules = ReadCodeGenModules(root);
            Dictionary<string, DheCodeGenModule> requestedModules =
                new Dictionary<string, DheCodeGenModule>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> requestedAssemblies = new HashSet<string>(
                requestedMethods.Select(method => NormalizeAssemblyName(method.AssemblyName)),
                StringComparer.OrdinalIgnoreCase);
            foreach (DheCodeGenModule module in modules)
            {
                string assemblyName = NormalizeAssemblyName(module.AssemblyName);
                if (!requestedAssemblies.Contains(assemblyName)) continue;
                if (requestedModules.ContainsKey(assemblyName))
                    throw new BuildFailedException("DHE generated C++ contains duplicate codegen modules for '" +
                        assemblyName + "'.");
                requestedModules.Add(assemblyName, module);
            }
            foreach (string assemblyName in requestedAssemblies)
            {
                if (!requestedModules.ContainsKey(assemblyName))
                    throw new BuildFailedException("DHE generated C++ has no codegen module for '" +
                        assemblyName + "'.");
            }

            Dictionary<string, List<string>> result = new Dictionary<string, List<string>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (DheGuardMethod method in requestedMethods)
            {
                string key = GetManagedMethodKey(method.AssemblyName, method.MethodToken);
                if (!result.ContainsKey(key)) result.Add(key, new List<string>());
                if ((method.MethodToken & 0xff000000u) != 0x06000000u)
                    throw new BuildFailedException("DHE method token is not a MethodDef token: " +
                        method.AssemblyName + "/" + method.MethodToken + ".");
                uint rid = method.MethodToken & 0x00ffffffu;
                DheCodeGenModule module = requestedModules[NormalizeAssemblyName(method.AssemblyName)];
                if (rid == 0 || rid > module.MethodPointers.Length)
                    throw new BuildFailedException("DHE method token is outside the IL2CPP codegen module: " +
                        method.AssemblyName + "/" + method.MethodToken + ".");
                AddGeneratedFunction(result[key], module.MethodPointers[rid - 1]);
            }

            DheGuardMethod[] genericMethods = requestedMethods.Where(method =>
                method.GenericParameterCount > 0 || method.DeclaringTypeGenericParameterCount > 0).ToArray();
            if (genericMethods.Length > 0)
                AddGeneratedGenericMethodFunctions(root, modules, genericMethods, result);
            foreach (List<string> functions in result.Values)
                functions.Sort(StringComparer.Ordinal);
            return result;
        }

        private static List<DheCodeGenModule> ReadCodeGenModules(string root)
        {
            Regex arrayPattern = new Regex(
                @"static\s+Il2CppMethodPointer\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\[\s*(?<count>\d+)\s*\]\s*=\s*\{(?<body>.*?)\};",
                RegexOptions.Singleline | RegexOptions.Compiled);
            Regex modulePattern = new Regex(
                @"const\s+Il2CppCodeGenModule\s+[A-Za-z_][A-Za-z0-9_]*\s*=\s*\{\s*\""(?<assembly>[^\""\r\n]+)\""\s*,\s*(?<count>\d+)\s*,\s*(?<array>[A-Za-z_][A-Za-z0-9_]*|NULL)\s*,",
                RegexOptions.Singleline | RegexOptions.Compiled);
            List<DheCodeGenModule> modules = new List<DheCodeGenModule>();
            HashSet<string> moduleAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] files = Directory.GetFiles(root, "*_CodeGen.c", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(root, "*_CodeGen.cpp", SearchOption.AllDirectories))
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            foreach (string file in files)
            {
                string source = File.ReadAllText(file, Encoding.UTF8);
                Dictionary<string, string[]> arrays = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (Match match in arrayPattern.Matches(source))
                {
                    string name = match.Groups["name"].Value;
                    int count = ParseGeneratedCount(match.Groups["count"].Value, file);
                    if (arrays.ContainsKey(name))
                        throw new BuildFailedException("DHE codegen pointer array is duplicated in " + file +
                            ": " + name + ".");
                    arrays.Add(name, ParseFunctionPointerInitializer(match.Groups["body"].Value, count, file));
                }
                foreach (Match match in modulePattern.Matches(source))
                {
                    string assemblyName = match.Groups["assembly"].Value;
                    string normalizedAssemblyName = NormalizeAssemblyName(assemblyName);
                    if (!moduleAssemblies.Add(normalizedAssemblyName))
                        throw new BuildFailedException("DHE generated C++ contains duplicate codegen modules for '" +
                            normalizedAssemblyName + "'.");
                    int count = ParseGeneratedCount(match.Groups["count"].Value, file);
                    string arrayName = match.Groups["array"].Value;
                    string[] pointers;
                    if (count == 0 && string.Equals(arrayName, "NULL", StringComparison.Ordinal))
                    {
                        pointers = Array.Empty<string>();
                    }
                    else if (!arrays.TryGetValue(arrayName, out pointers) || pointers.Length != count)
                    {
                        throw new BuildFailedException("DHE codegen module pointer table is missing or has the " +
                            "wrong size in " + file + ": " + assemblyName + ".");
                    }
                    modules.Add(new DheCodeGenModule
                    {
                        AssemblyName = assemblyName,
                        MethodPointers = pointers,
                    });
                }
            }
            if (modules.Count == 0)
                throw new BuildFailedException("DHE generated C++ contains no Il2CppCodeGenModule definitions.");
            return modules;
        }

        private static void AddGeneratedGenericMethodFunctions(string root,
            IReadOnlyCollection<DheCodeGenModule> modules, IReadOnlyCollection<DheGuardMethod> genericMethods,
            Dictionary<string, List<string>> functions)
        {
            Dictionary<int, string> globalMethods = IndexRequestedGlobalMethods(root, modules, genericMethods);
            string definitionsPath = RequireGeneratedCppFile(root, "Il2CppGenericMethodDefinitions.c");
            string tablePath = RequireGeneratedCppFile(root, "Il2CppGenericMethodTable.c");
            string pointersPath = RequireGeneratedCppFile(root, "Il2CppGenericMethodPointerTable.c");

            string definitionsSource = File.ReadAllText(definitionsPath, Encoding.UTF8);
            Match definitions = Regex.Match(definitionsSource,
                @"g_Il2CppMethodSpecTable\s*\[\s*(?<count>\d+)\s*\]\s*=\s*\{(?<body>.*?)\};",
                RegexOptions.Singleline);
            if (!definitions.Success)
                throw new BuildFailedException("DHE cannot parse the IL2CPP generic method spec table.");
            int definitionCount = ParseGeneratedCount(definitions.Groups["count"].Value, definitionsPath);
            MatchCollection definitionRows = Regex.Matches(definitions.Groups["body"].Value,
                @"\{\s*(?<method>-?\d+)\s*,\s*-?\d+\s*,\s*-?\d+\s*\}");
            if (definitionRows.Count != definitionCount)
                throw new BuildFailedException("DHE IL2CPP generic method spec count does not match its table.");
            int[] methodDefinitionIndices = definitionRows.Cast<Match>()
                .Select(match => int.Parse(match.Groups["method"].Value, CultureInfo.InvariantCulture)).ToArray();

            string pointerSource = File.ReadAllText(pointersPath, Encoding.UTF8);
            Match pointerTable = Regex.Match(pointerSource,
                @"g_Il2CppGenericMethodPointers\s*\[\s*(?<count>\d+)\s*\]\s*=\s*\{(?<body>.*?)\};",
                RegexOptions.Singleline);
            if (!pointerTable.Success)
                throw new BuildFailedException("DHE cannot parse the IL2CPP generic method pointer table.");
            int pointerCount = ParseGeneratedCount(pointerTable.Groups["count"].Value, pointersPath);
            string[] pointers = ParseFunctionPointerInitializer(pointerTable.Groups["body"].Value,
                pointerCount, pointersPath);

            string tableSource = File.ReadAllText(tablePath, Encoding.UTF8);
            Match functionTable = Regex.Match(tableSource,
                @"g_Il2CppGenericMethodFunctions\s*\[\s*(?<count>\d+)\s*\]\s*=\s*\{(?<body>.*?)\};",
                RegexOptions.Singleline);
            if (!functionTable.Success)
                throw new BuildFailedException("DHE cannot parse the IL2CPP generic method function table.");
            int functionCount = ParseGeneratedCount(functionTable.Groups["count"].Value, tablePath);
            MatchCollection functionRows = Regex.Matches(functionTable.Groups["body"].Value,
                @"\{\s*(?<spec>-?\d+)\s*,\s*(?<pointer>-?\d+)\s*,\s*-?\d+\s*,\s*-?\d+\s*\}");
            if (functionRows.Count != functionCount)
                throw new BuildFailedException("DHE IL2CPP generic method function count does not match its table.");
            foreach (Match row in functionRows)
            {
                int specIndex = int.Parse(row.Groups["spec"].Value, CultureInfo.InvariantCulture);
                int pointerIndex = int.Parse(row.Groups["pointer"].Value, CultureInfo.InvariantCulture);
                if (specIndex < 0 || specIndex >= methodDefinitionIndices.Length)
                    throw new BuildFailedException("DHE IL2CPP generic method spec index is out of range: " +
                        specIndex + ".");
                int methodDefinitionIndex = methodDefinitionIndices[specIndex];
                if (!globalMethods.TryGetValue(methodDefinitionIndex, out string methodKey)) continue;
                if (pointerIndex < 0 || pointerIndex >= pointers.Length)
                    throw new BuildFailedException("DHE IL2CPP generic method pointer index is out of range: " +
                        pointerIndex + ".");
                AddGeneratedFunction(functions[methodKey], pointers[pointerIndex]);
            }
        }

        private static Dictionary<int, string> IndexRequestedGlobalMethods(string root,
            IReadOnlyCollection<DheCodeGenModule> modules, IReadOnlyCollection<DheGuardMethod> requestedMethods)
        {
            DirectoryInfo cppRoot = new DirectoryInfo(root);
            if (cppRoot.Parent == null)
                throw new BuildFailedException("DHE generated C++ root has no IL2CPP output parent.");
            string[] metadataPaths = Directory.GetFiles(cppRoot.Parent.FullName, "global-metadata.dat",
                    SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (metadataPaths.Length == 0)
                throw new BuildFailedException("DHE IL2CPP output has no global-metadata.dat.");
            byte[] metadata = File.ReadAllBytes(metadataPaths[0]);
            string metadataHash = Sha256Hex(metadata);
            foreach (string path in metadataPaths.Skip(1))
            {
                if (!string.Equals(metadataHash, Sha256Hex(File.ReadAllBytes(path)),
                        StringComparison.OrdinalIgnoreCase))
                    throw new BuildFailedException("DHE IL2CPP output contains different global-metadata.dat files.");
            }
            if (metadata.Length < 176 || unchecked((uint)ReadMetadataInt32(metadata, 0)) != 0xfab11bafu)
                throw new BuildFailedException("DHE global-metadata.dat header is invalid.");
            int metadataVersion = ReadMetadataInt32(metadata, 4);
            if (metadataVersion != 31)
                throw new BuildFailedException("DHE global metadata version is unsupported: " +
                    metadataVersion + ".");

            int stringsOffset = ReadMetadataInt32(metadata, 24);
            int stringsSize = ReadMetadataInt32(metadata, 28);
            int methodsOffset = ReadMetadataInt32(metadata, 48);
            int methodsSize = ReadMetadataInt32(metadata, 52);
            int imagesOffset = ReadMetadataInt32(metadata, 168);
            int imagesSize = ReadMetadataInt32(metadata, 172);
            ValidateMetadataRange(metadata, stringsOffset, stringsSize, "strings");
            ValidateMetadataRange(metadata, methodsOffset, methodsSize, "methods");
            ValidateMetadataRange(metadata, imagesOffset, imagesSize, "images");

            int methodCount = modules.Sum(module => module.MethodPointers.Length);
            if (methodCount <= 0 || methodsSize % methodCount != 0)
                throw new BuildFailedException("DHE global metadata method table does not match codegen modules.");
            int methodRecordSize = methodsSize / methodCount;
            if (methodRecordSize != 32 && methodRecordSize != 36)
                throw new BuildFailedException("DHE global metadata method record size is unsupported: " +
                    methodRecordSize + ".");
            if (modules.Count <= 0 || imagesSize % modules.Count != 0)
                throw new BuildFailedException("DHE global metadata image table does not match codegen modules.");
            int imageRecordSize = imagesSize / modules.Count;
            if (imageRecordSize != 40)
                throw new BuildFailedException("DHE global metadata image record size is unsupported: " +
                    imageRecordSize + ".");

            Dictionary<string, DheMetadataImage> requestedImages =
                new Dictionary<string, DheMetadataImage>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> requestedAssemblies = new HashSet<string>(
                requestedMethods.Select(method => NormalizeAssemblyName(method.AssemblyName)),
                StringComparer.OrdinalIgnoreCase);
            for (int imageIndex = 0; imageIndex < modules.Count; imageIndex++)
            {
                int record = checked(imagesOffset + imageIndex * imageRecordSize);
                int nameIndex = ReadMetadataInt32(metadata, record);
                string assemblyName = NormalizeAssemblyName(ReadMetadataString(metadata, stringsOffset,
                    stringsSize, nameIndex));
                if (!requestedAssemblies.Contains(assemblyName)) continue;
                if (requestedImages.ContainsKey(assemblyName))
                    throw new BuildFailedException("DHE global metadata contains duplicate images for '" +
                        assemblyName + "'.");
                requestedImages.Add(assemblyName, new DheMetadataImage
                {
                    AssemblyName = assemblyName,
                    TypeStart = ReadMetadataInt32(metadata, record + 8),
                    TypeCount = ReadMetadataInt32(metadata, record + 12),
                });
            }
            foreach (string assemblyName in requestedAssemblies)
            {
                if (!requestedImages.ContainsKey(assemblyName))
                    throw new BuildFailedException("DHE global metadata has no image for '" + assemblyName + "'.");
            }

            Dictionary<string, DheGuardMethod> requestedByKey = requestedMethods.ToDictionary(
                method => GetManagedMethodKey(method.AssemblyName, method.MethodToken),
                method => method, StringComparer.OrdinalIgnoreCase);
            Dictionary<int, string> result = new Dictionary<int, string>();
            int tokenOffset = methodRecordSize == 36 ? 24 : 20;
            for (int methodIndex = 0; methodIndex < methodCount; methodIndex++)
            {
                int record = checked(methodsOffset + methodIndex * methodRecordSize);
                int declaringType = ReadMetadataInt32(metadata, record + 4);
                uint token = unchecked((uint)ReadMetadataInt32(metadata, record + tokenOffset));
                foreach (DheMetadataImage image in requestedImages.Values)
                {
                    if (declaringType < image.TypeStart || declaringType >= image.TypeStart + image.TypeCount)
                        continue;
                    string key = GetManagedMethodKey(image.AssemblyName, token);
                    if (!requestedByKey.ContainsKey(key)) break;
                    if (result.ContainsKey(methodIndex))
                        throw new BuildFailedException("DHE global method definition maps to multiple requested methods: " +
                            methodIndex + ".");
                    result.Add(methodIndex, key);
                    break;
                }
            }
            HashSet<string> resolved = new HashSet<string>(result.Values, StringComparer.OrdinalIgnoreCase);
            foreach (string key in requestedByKey.Keys)
            {
                if (!resolved.Contains(key))
                    throw new BuildFailedException("DHE cannot find the requested generic MethodDef in global metadata: " +
                        key + ".");
            }
            return result;
        }

        private static string[] ParseFunctionPointerInitializer(string body, int expectedCount, string sourcePath)
        {
            string withoutComments = Regex.Replace(body, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            withoutComments = Regex.Replace(withoutComments, @"//[^\r\n]*", string.Empty);
            string[] entries = withoutComments.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Trim()).Where(entry => entry.Length > 0).ToArray();
            if (entries.Length != expectedCount)
                throw new BuildFailedException("DHE function pointer initializer count is wrong in " + sourcePath +
                    ": expected " + expectedCount + ", found " + entries.Length + ".");
            Regex pointerPattern = new Regex(
                @"^(?:(?:\(\s*Il2CppMethodPointer\s*\)\s*)?&\s*)?(?<name>[A-Za-z_][A-Za-z0-9_]*)$",
                RegexOptions.Compiled);
            string[] result = new string[entries.Length];
            for (int index = 0; index < entries.Length; index++)
            {
                if (string.Equals(entries[index], "NULL", StringComparison.Ordinal)) continue;
                Match pointer = pointerPattern.Match(entries[index]);
                if (!pointer.Success)
                    throw new BuildFailedException("DHE cannot parse function pointer '" + entries[index] +
                        "' in " + sourcePath + ".");
                result[index] = pointer.Groups["name"].Value;
            }
            return result;
        }

        private static string RequireGeneratedCppFile(string root, string fileName)
        {
            string[] matches = Directory.GetFiles(root, fileName, SearchOption.AllDirectories);
            if (matches.Length != 1)
                throw new BuildFailedException("DHE expected one generated " + fileName + ", found " +
                    matches.Length + ".");
            return matches[0];
        }

        private static int ParseGeneratedCount(string value, string sourcePath)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result) || result < 0)
                throw new BuildFailedException("DHE generated table count is invalid in " + sourcePath + ".");
            return result;
        }

        private static void AddGeneratedFunction(List<string> functions, string functionName)
        {
            if (string.IsNullOrWhiteSpace(functionName) ||
                functionName.EndsWith("AdjustorThunk", StringComparison.Ordinal)) return;
            if (!functions.Contains(functionName, StringComparer.Ordinal)) functions.Add(functionName);
        }

        private static string GetManagedMethodKey(string assemblyName, uint methodToken)
        {
            return NormalizeAssemblyName(assemblyName) + "/" +
                methodToken.ToString(CultureInfo.InvariantCulture);
        }

        private static int ReadMetadataInt32(byte[] metadata, int offset)
        {
            if (offset < 0 || offset > metadata.Length - 4)
                throw new BuildFailedException("DHE global metadata integer offset is outside the file: " + offset + ".");
            return metadata[offset] | metadata[offset + 1] << 8 | metadata[offset + 2] << 16 |
                metadata[offset + 3] << 24;
        }

        private static void ValidateMetadataRange(byte[] metadata, int offset, int size, string name)
        {
            if (offset < 0 || size < 0 || offset > metadata.Length - size)
                throw new BuildFailedException("DHE global metadata " + name + " range is outside the file.");
        }

        private static string ReadMetadataString(byte[] metadata, int stringsOffset, int stringsSize,
            int stringIndex)
        {
            if (stringIndex < 0 || stringIndex >= stringsSize)
                throw new BuildFailedException("DHE global metadata string index is outside the string table: " +
                    stringIndex + ".");
            int start = checked(stringsOffset + stringIndex);
            int limit = checked(stringsOffset + stringsSize);
            int end = start;
            while (end < limit && metadata[end] != 0) end++;
            if (end == limit)
                throw new BuildFailedException("DHE global metadata string is not null terminated.");
            return Encoding.UTF8.GetString(metadata, start, end - start);
        }

        private static string ReadPreviousManagedSignature(string source, int lineStart)
        {
            int end = lineStart - 1;
            while (end >= 0 && (source[end] == '\r' || source[end] == '\n')) end--;
            if (end < 0) return string.Empty;
            int start = source.LastIndexOf('\n', end);
            start = start < 0 ? 0 : start + 1;
            string line = source.Substring(start, end - start + 1).Trim();
            return line.StartsWith("// ", StringComparison.Ordinal) ? line.Substring(3).Trim() : string.Empty;
        }

        private static bool AddNativeManifestIssue(
            Dictionary<string, DheNativeManifestIssue> issues, DheGuardMethod method, string reason)
        {
            string key = method.AssemblyName + "/" + method.StableMethodIdSha256;
            if (issues.TryGetValue(key, out DheNativeManifestIssue issue))
            {
                if (!issue.reasons.Contains(reason, StringComparer.Ordinal))
                    issue.reasons = issue.reasons.Concat(new[] { reason }).ToArray();
                return false;
            }
            issues.Add(key, new DheNativeManifestIssue
            {
                id = key,
                name = method.MethodName,
                assemblyName = method.AssemblyName,
                declaringType = method.DeclaringType,
                methodToken = method.MethodToken,
                managedId = method.ManagedId,
                stableMethodIdSha256 = method.StableMethodIdSha256,
                reasons = new[] { reason },
                isChanged = method.IsChanged,
            });
            return true;
        }

        private static bool ManagedSignatureMatches(DheGuardMethod method, string signature)
        {
            if (string.IsNullOrWhiteSpace(signature)) return false;
            string declaringType = method.DeclaringType ?? string.Empty;
            string typeMarker = method.DeclaringTypeGenericParameterCount > 0
                ? declaringType + "<"
                : declaringType + "::";
            if (signature.IndexOf(typeMarker, StringComparison.Ordinal) < 0) return false;
            string methodMarker = "::" + method.MethodName;
            int methodStart = signature.IndexOf(methodMarker, StringComparison.Ordinal);
            if (methodStart < 0) return false;
            int cursor = methodStart + methodMarker.Length;
            if (method.GenericParameterCount > 0)
            {
                if (cursor >= signature.Length || signature[cursor] != '<' ||
                    !TryReadDelimitedItemCount(signature, cursor, '<', '>',
                        out int genericCount, out cursor) ||
                    genericCount != method.GenericParameterCount)
                {
                    return false;
                }
            }
            if (cursor >= signature.Length || signature[cursor] != '(' ||
                !TryReadDelimitedItemCount(signature, cursor, '(', ')',
                    out int parameterCount, out _) ||
                parameterCount != method.ManagedParameterTypes.Length)
            {
                return false;
            }
            return true;
        }

        private static bool TryReadDelimitedItemCount(string text, int openIndex,
            char open, char close, out int itemCount, out int nextIndex)
        {
            itemCount = 0;
            nextIndex = openIndex;
            if (openIndex < 0 || openIndex >= text.Length || text[openIndex] != open)
                return false;
            Stack<char> delimiters = new Stack<char>();
            delimiters.Push(close);
            bool hasContent = false;
            for (int index = openIndex + 1; index < text.Length; index++)
            {
                char character = text[index];
                if (character == '(')
                {
                    delimiters.Push(')');
                    continue;
                }
                if (character == '<')
                {
                    delimiters.Push('>');
                    continue;
                }
                if (character == '[')
                {
                    delimiters.Push(']');
                    continue;
                }
                if (character == ')' || character == '>' || character == ']')
                {
                    if (delimiters.Count == 0 || delimiters.Peek() != character) return false;
                    delimiters.Pop();
                    if (delimiters.Count == 0)
                    {
                        itemCount = hasContent ? itemCount + 1 : 0;
                        nextIndex = index + 1;
                        return true;
                    }
                    continue;
                }
                if (delimiters.Count == 1 && character == ',') itemCount++;
                else if (delimiters.Count == 1 && !char.IsWhiteSpace(character)) hasContent = true;
            }
            return false;
        }

        private static DheNativeManifestMethod ResolveNativeMethod(DheGuardMethod method,
            DheCppDefinition definition)
        {
            string returnType = definition.Signature.Substring(0,
                definition.Signature.IndexOf(definition.FunctionName, StringComparison.Ordinal)).Trim();
            int attribute = returnType.LastIndexOf("IL2CPP_METHOD_ATTR", StringComparison.Ordinal);
            if (attribute >= 0) returnType = returnType.Substring(attribute + "IL2CPP_METHOD_ATTR".Length).Trim();
            if (returnType.StartsWith("inline ", StringComparison.Ordinal)) returnType = returnType.Substring(7).Trim();
            if (string.IsNullOrWhiteSpace(returnType))
                throw new BuildFailedException("Unable to resolve native return type: " + definition.FunctionName);

            List<DheNativeParameter> parameters = new List<DheNativeParameter>();
            foreach (string part in SplitCppParameters(definition.ParametersText))
            {
                Match parameter = Regex.Match(part.Trim(), @"^(?<type>.+\S)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)$");
                if (!parameter.Success)
                    throw new BuildFailedException("Unable to parse generated parameter '" + part + "'.");
                if (string.Equals(parameter.Groups["name"].Value, "method", StringComparison.Ordinal)) continue;
                parameters.Add(new DheNativeParameter
                {
                    Type = parameter.Groups["type"].Value.Trim(),
                    Name = parameter.Groups["name"].Value,
                });
            }
            bool hasThis = method.HasThis;
            if (hasThis && (parameters.Count == 0 || parameters[0].Name != "__this"))
                throw new BuildFailedException("Generated instance definition has no __this parameter: " + definition.FunctionName);
            bool usesHiddenReturn = parameters.Count > 0 && parameters[parameters.Count - 1].Name == "il2cppRetVal";
            return new DheNativeManifestMethod
            {
                functionName = definition.FunctionName,
                nativeSignature = definition.Signature,
                bodyStart = definition.BodyStart,
                returnType = returnType,
                parameters = parameters.ToArray(),
                sourceFile = definition.File,
                assemblyName = method.AssemblyName,
                declaringType = method.DeclaringType,
                methodName = method.MethodName,
                methodToken = method.MethodToken,
                managedId = method.ManagedId,
                stableMethodIdSha256 = method.StableMethodIdSha256,
                managedReturnType = method.ReturnType,
                managedParameterTypes = method.ManagedParameterTypes,
                managedHasThis = method.HasThis,
                declaringTypeIsValueType = method.DeclaringTypeIsValueType,
                genericParameterCount = method.GenericParameterCount,
                declaringTypeGenericParameterCount = method.DeclaringTypeGenericParameterCount,
                bridgeKind = "invoke-args-v1",
                usesHiddenReturnBuffer = usesHiddenReturn,
                isStatic = method.IsStatic,
                hasThis = hasThis,
                managedParameterCount = Math.Max(0, parameters.Count - (hasThis ? 1 : 0) - (usesHiddenReturn ? 1 : 0)),
            };
        }

        private static IEnumerable<string> SplitCppParameters(string text)
        {
            int angle = 0;
            int paren = 0;
            int start = 0;
            for (int index = 0; index < text.Length; index++)
            {
                switch (text[index])
                {
                    case '<': angle++; break;
                    case '>': if (angle > 0) angle--; break;
                    case '(': paren++; break;
                    case ')': if (paren > 0) paren--; break;
                    case ',':
                        if (angle == 0 && paren == 0)
                        {
                            string part = text.Substring(start, index - start).Trim();
                            if (part.Length > 0) yield return part;
                            start = index + 1;
                        }
                        break;
                }
            }
            string tail = text.Substring(start).Trim();
            if (tail.Length > 0) yield return tail;
        }

        private static string CreateGuard(DheNativeManifestMethod method)
        {
            string beginMarker = GetGuardMarker(NativeGuardBeginPrefix, method);
            string endMarker = GetGuardMarker(NativeGuardEndPrefix, method);
            string[] allNames = method.parameters.Select(parameter => parameter.Name).ToArray();
            bool hasThis = method.hasThis;
            int firstManaged = hasThis ? 1 : 0;
            int count = method.parameters.Length - firstManaged - (method.usesHiddenReturnBuffer ? 1 : 0);
            string[] managedNames = method.parameters.Skip(firstManaged).Take(Math.Max(0, count))
                .Select(parameter => parameter.Name).ToArray();
            string helper;
            bool generic = method.bridgeKind == "invoke-args-v1";
            if (generic)
            {
                List<string> args = new List<string>();
                List<string> kinds = new List<string>();
                foreach (DheNativeParameter parameter in method.parameters.Skip(firstManaged).Take(Math.Max(0, count)))
                {
                    bool raw = parameter.Type.EndsWith("*", StringComparison.Ordinal) ||
                        parameter.Type.StartsWith("Il2CppFullySharedGeneric", StringComparison.Ordinal);
                    args.Add(raw ? "reinterpret_cast<void*>(" + parameter.Name + ")" :
                        "reinterpret_cast<void*>(&" + parameter.Name + ")");
                    kinds.Add(raw ? "1u" : "0u");
                }
                string thisValue = hasThis ? "reinterpret_cast<void*>(__this)" : "nullptr";
                string result = method.usesHiddenReturnBuffer
                    ? "reinterpret_cast<void*>(il2cppRetVal)"
                    : method.returnType == "void" ? "nullptr" : "&dheResult";
                StringBuilder body = new StringBuilder();
                if (count == 0)
                {
                    body.Append("void** dheInvokeArgs = nullptr;\r\n");
                    body.Append("        const uint8_t* dheInvokeArgKinds = nullptr;\r\n");
                }
                else
                {
                    body.Append("void* dheInvokeArgs[] = { ").Append(string.Join(", ", args)).Append(" };\r\n");
                    body.Append("        const uint8_t dheInvokeArgKinds[] = { ").Append(string.Join(", ", kinds)).Append(" };\r\n");
                }
                if (method.returnType != "void" && !method.usesHiddenReturnBuffer)
                    body.Append("        ").Append(method.returnType).Append(" dheResult{};\r\n");
                body.Append("        hybridclr::dhe::ExecuteInterpreterInvokeArgs(dheMethod, ")
                    .Append(thisValue).Append(", dheInvokeArgs, dheInvokeArgKinds, ")
                    .Append(count).Append("u, ").Append(result).Append(");");
                if (method.returnType != "void" && !method.usesHiddenReturnBuffer)
                    body.Append("\r\n        return dheResult;");
                else if (method.returnType == "void") body.Append("\r\n        return;");
                helper = body.ToString();
            }
            else
            {
                string[] managedTypes = method.parameters.Skip(firstManaged).Take(Math.Max(0, count))
                    .Select(parameter => parameter.Type).ToArray();
                string shape = method.returnType + "(" + string.Join(", ", managedTypes) + ")";
                if (method.declaringTypeIsValueType && hasThis && count == 0 && method.returnType == "void")
                    helper = "hybridclr::dhe::ExecuteInterpreterValueTypeInstanceVoidNoArgs(dheMethod, __this);\r\n        return;";
                else if (hasThis && count == 0 && method.returnType == "void")
                    helper = "hybridclr::dhe::ExecuteInterpreterInstanceVoidNoArgs(dheMethod, __this);\r\n        return;";
                else if (hasThis && count == 0 && method.returnType == "bool")
                    helper = "return hybridclr::dhe::ExecuteInterpreterInstanceBool(dheMethod, __this);";
                else if (method.returnType == "void" && count == 2 &&
                    Regex.IsMatch(managedTypes[0], @"\*\s*$") &&
                    Regex.IsMatch(managedTypes[1], @"^int32_t\s*\*\s*$"))
                    helper = "hybridclr::dhe::ExecuteInterpreterRefValueI4Ref(dheMethod, " + managedNames[0] + ", " + managedNames[1] + ");\r\n        return;";
                else if (method.returnType == "int32_t" && count == 2 && managedTypes[0].EndsWith("*", StringComparison.Ordinal) && managedTypes[1] == "int32_t")
                    helper = "return hybridclr::dhe::ExecuteInterpreterPtrI4(dheMethod, " + managedNames[0] + ", " + managedNames[1] + ");";
                else if (shape == "int32_t(int32_t)")
                    helper = hasThis ? "return hybridclr::dhe::ExecuteInterpreterInstanceI4I4(dheMethod, __this, " + managedNames[0] + ");" : "return hybridclr::dhe::ExecuteInterpreterI4I4(dheMethod, " + managedNames[0] + ");";
                else if (shape == "int32_t(int32_t, int32_t)")
                    helper = "return hybridclr::dhe::ExecuteInterpreterI4I4I4(dheMethod, " + managedNames[0] + ", " + managedNames[1] + ");";
                else if (shape == "int64_t(int64_t)")
                    helper = "return hybridclr::dhe::ExecuteInterpreterI8I8(dheMethod, " + managedNames[0] + ");";
                else if (shape == "void(int32_t)")
                    helper = "hybridclr::dhe::ExecuteInterpreterVoidI4(dheMethod, " + managedNames[0] + ");\r\n        return;";
                else if (shape == "void()")
                    helper = "hybridclr::dhe::ExecuteInterpreterVoidNoArgs(dheMethod);\r\n        return;";
                else if (count == 1 && managedTypes[0].EndsWith("*", StringComparison.Ordinal) == false &&
                    Regex.IsMatch(managedTypes[0], @"_t[0-9A-Fa-f]+$") && method.returnType != "void")
                    helper = method.returnType + " dheResult{};\r\n        hybridclr::dhe::ExecuteInterpreterValue(dheMethod, &" + managedNames[0] + ", sizeof(" + managedNames[0] + "), &dheResult);\r\n        return dheResult;";
                else
                    throw new BuildFailedException("Unsupported DHE native shape: " + shape);
            }
            return "    // " + beginMarker + "\r\n" +
                "    hybridclr::dhe::RecordAotEntry();\r\n" +
                "    const RuntimeMethod* dheMethod = method;\r\n" +
                "    if (dheMethod == nullptr)\r\n    {\r\n" +
                "        dheMethod = hybridclr::dhe::ResolveMethodByToken(\"" +
                method.assemblyName.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) +
                "\", " + method.methodToken + ");\r\n    }\r\n" +
                "    if (hybridclr::dhe::ShouldDispatchToInterpreter(dheMethod))\r\n    {\r\n" +
                "        dheMethod = hybridclr::dhe::ResolveInterpreterMethod(dheMethod);\r\n" +
                "        " + helper.Replace("\r\n", "\r\n        ", StringComparison.Ordinal) + "\r\n    }\r\n" +
                "    // " + endMarker;
        }

        private static string GetGuardMarker(string prefix, DheNativeManifestMethod method)
        {
            return prefix + method.functionName + ":" +
                method.methodToken.ToString(CultureInfo.InvariantCulture);
        }

        private static string ExtractGuardBlock(string source, DheNativeManifestMethod method)
        {
            string beginMarker = GetGuardMarker(NativeGuardBeginPrefix, method);
            string endMarker = GetGuardMarker(NativeGuardEndPrefix, method);
            int begin = RequireSingleMarker(source, beginMarker, method);
            int end = RequireSingleMarker(source, endMarker, method);
            if (end <= begin)
            {
                throw new BuildFailedException("DHE guard end marker precedes its begin marker: " +
                    method.functionName + "/" + method.methodToken);
            }
            int lineStart = source.LastIndexOf('\n', begin);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            int blockEnd = end + endMarker.Length;
            int lineEnd = source.IndexOf('\n', blockEnd);
            if (lineEnd < 0) lineEnd = source.Length;
            string suffix = source.Substring(blockEnd, lineEnd - blockEnd).TrimEnd('\r');
            if (suffix.Any(character => character != ' ' && character != '\t'))
            {
                throw new BuildFailedException("DHE guard end marker has unexpected trailing content: " +
                    method.functionName + "/" + method.methodToken);
            }
            return NormalizeGuardBlock(source.Substring(lineStart, blockEnd - lineStart));
        }

        private static Dictionary<string, string> IndexExistingGuardBlocks(string source)
        {
            Dictionary<string, string> blocks = new Dictionary<string, string>(StringComparer.Ordinal);
            int cursor = 0;
            int endCount = 0;
            while ((cursor = source.IndexOf(NativeGuardBeginPrefix, cursor, StringComparison.Ordinal)) >= 0)
            {
                int markerEnd = FindLineContentEnd(source, cursor);
                string beginMarker = source.Substring(cursor, markerEnd - cursor).TrimEnd(' ', '\t', '\r');
                string suffix = beginMarker.Substring(NativeGuardBeginPrefix.Length);
                string endMarker = NativeGuardEndPrefix + suffix;
                int end = source.IndexOf(endMarker, markerEnd, StringComparison.Ordinal);
                if (end < 0)
                    throw new BuildFailedException("DHE guard end marker is missing: " + endMarker);
                int nextBegin = source.IndexOf(NativeGuardBeginPrefix, markerEnd, StringComparison.Ordinal);
                if (nextBegin >= 0 && nextBegin < end)
                    throw new BuildFailedException("DHE guard blocks overlap near: " + beginMarker);
                int lineStart = source.LastIndexOf('\n', cursor);
                lineStart = lineStart < 0 ? 0 : lineStart + 1;
                int blockEnd = end + endMarker.Length;
                int lineEnd = FindLineContentEnd(source, blockEnd);
                string trailing = source.Substring(blockEnd, lineEnd - blockEnd).TrimEnd('\r');
                if (trailing.Any(character => character != ' ' && character != '\t'))
                    throw new BuildFailedException("DHE guard end marker has unexpected trailing content: " + endMarker);
                if (!blocks.TryAdd(beginMarker,
                        NormalizeGuardBlock(source.Substring(lineStart, blockEnd - lineStart))))
                    throw new BuildFailedException("DHE guard marker is duplicated: " + beginMarker);
                cursor = blockEnd;
                endCount++;
            }
            int discoveredEnds = CountOccurrences(source, NativeGuardEndPrefix);
            if (discoveredEnds != endCount)
                throw new BuildFailedException("Generated C++ contains an orphan or duplicated DHE guard end marker.");
            return blocks;
        }

        private static int FindLineContentEnd(string source, int start)
        {
            int newline = source.IndexOf('\n', start);
            return newline < 0 ? source.Length : newline;
        }

        private static int CountOccurrences(string source, string value)
        {
            int count = 0;
            int cursor = 0;
            while ((cursor = source.IndexOf(value, cursor, StringComparison.Ordinal)) >= 0)
            {
                count++;
                cursor += value.Length;
            }
            return count;
        }

        private static string ApplyInsertions(string source, IEnumerable<Tuple<int, string>> insertions)
        {
            Tuple<int, string>[] ordered = insertions.OrderBy(item => item.Item1).ToArray();
            int extra = ordered.Sum(item => item.Item2.Length);
            StringBuilder output = new StringBuilder(source.Length + extra);
            int cursor = 0;
            foreach (Tuple<int, string> insertion in ordered)
            {
                if (insertion.Item1 < cursor || insertion.Item1 > source.Length)
                    throw new BuildFailedException("DHE native guard insertion offsets overlap or escape the source file.");
                output.Append(source, cursor, insertion.Item1 - cursor);
                output.Append(insertion.Item2);
                cursor = insertion.Item1;
            }
            output.Append(source, cursor, source.Length - cursor);
            return output.ToString();
        }

        private static int RequireSingleMarker(string source, string marker, DheNativeManifestMethod method)
        {
            int first = source.IndexOf(marker, StringComparison.Ordinal);
            if (first < 0 || source.IndexOf(marker, first + marker.Length, StringComparison.Ordinal) >= 0)
            {
                throw new BuildFailedException("DHE guard marker is missing or duplicated: " + marker + " (" +
                    method.functionName + "/" + method.methodToken + ")");
            }
            return first;
        }

        private static string NormalizeGuardBlock(string block)
        {
            return (block ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal).TrimEnd('\n');
        }

        private static string[] NormalizeAssemblyNames(IEnumerable<string> names)
        {
            return (names ?? Array.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(NormalizeAssemblyName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
        }

        private static string NormalizeAssemblyName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            string trimmed = name.Trim();
            string normalized = trimmed.EndsWith(DllExtension, StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(trimmed) : trimmed;
            if (Path.IsPathRooted(trimmed) || trimmed.Contains('/') || trimmed.Contains('\\') ||
                trimmed.Contains("..", StringComparison.Ordinal) ||
                string.Equals(normalized, ".", StringComparison.Ordinal) ||
                string.Equals(normalized, "..", StringComparison.Ordinal) ||
                Path.GetFileName(normalized) != normalized)
            {
                throw new BuildFailedException("DHE assembly name is unsafe: " + name);
            }
            return normalized;
        }

        private static string ResolvePlanReference(string planDirectory, string reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                return string.Empty;
            }
            return Path.GetFullPath(Path.IsPathRooted(reference)
                ? reference : Path.Combine(planDirectory, reference));
        }

        private static void EnsureUnique(IEnumerable<string> names, string description)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in names)
            {
                if (!seen.Add(name)) throw new BuildFailedException(description + " contains duplicate: " + name);
            }
        }

        private static string RequireDirectory(string path, string description)
        {
            string resolved = Path.GetFullPath(path ?? string.Empty);
            if (!Directory.Exists(resolved)) throw new BuildFailedException(description + " was not found: " + resolved);
            return resolved;
        }

        private static string RequireOutputDirectory(string path, string description)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new BuildFailedException(description + " is empty.");
            string resolved = Path.GetFullPath(path);
            Directory.CreateDirectory(resolved);
            return resolved;
        }

        private static void CopyAssemblySet(string sourceRoot, string destinationRoot,
            IEnumerable<string> assemblyNames, string description)
        {
            string source = RequireDirectory(sourceRoot, "DHE " + description + " source root");
            string destination = RequireOutputDirectory(destinationRoot,
                "DHE " + description + " output root");
            foreach (string rawName in assemblyNames ?? Array.Empty<string>())
            {
                string name = NormalizeAssemblyName(rawName);
                string sourcePath = RequireFile(Path.Combine(source, name + DllExtension),
                    name + " " + description + " assembly");
                File.Copy(sourcePath, Path.Combine(destination, name + DllExtension), true);
            }
        }

        private static string RequireFile(string path, string description)
        {
            string resolved = Path.GetFullPath(path ?? string.Empty);
            if (!File.Exists(resolved)) throw new BuildFailedException(description + " was not found: " + resolved);
            return resolved;
        }

        private static string ResolveProjectPath(string projectRoot, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new BuildFailedException("DHE path is empty.");
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path));
        }

        private static string ToStreamingAssetPath(string projectRoot, string path)
        {
            path = RequireProjectChild(projectRoot, path, "DHE runtime asset path");
            string relative = path.Substring(projectRoot.TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
            const string streamingPrefix = "Assets/StreamingAssets/";
            if (!relative.StartsWith(streamingPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new BuildFailedException(
                    "DHE runtime asset path must be inside Assets/StreamingAssets: " + path);
            }
            return relative.Substring(streamingPrefix.Length)
                .Replace(Path.AltDirectorySeparatorChar, '/');
        }

        private static string ResolveRuntimeAssetPath(DheRuntimePlanOptions options,
            string projectRoot, string path)
        {
            string projectPath = RequireProjectChild(projectRoot, path,
                "DHE runtime asset path");
            string runtimePath = options.RuntimeAssetPathResolver == null
                ? ToStreamingAssetPath(projectRoot, projectPath)
                : options.RuntimeAssetPathResolver(projectPath);
            if (string.IsNullOrWhiteSpace(runtimePath) || Path.IsPathRooted(runtimePath))
            {
                throw new BuildFailedException(
                    "DHE runtime asset resolver returned an empty or rooted path: " + runtimePath);
            }
            string normalized = runtimePath.Replace('\\', '/');
            if (normalized.Split('/').Any(segment => segment == "." || segment == ".."))
            {
                throw new BuildFailedException(
                    "DHE runtime asset resolver returned an unsafe path: " + runtimePath);
            }
            return normalized;
        }

        private static string RequireProjectChild(string projectRoot, string path, string description)
        {
            string resolvedRoot = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string resolvedPath = Path.GetFullPath(path ?? string.Empty);
            if (!resolvedPath.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new BuildFailedException(description + " must be inside the project root: " + resolvedPath);
            }
            return resolvedPath;
        }

        private static byte[] Sha256(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(bytes ?? Array.Empty<byte>());
        }

        private static string Sha256Hex(byte[] bytes)
        {
            return BitConverter.ToString(Sha256(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string Sha256NamedByteSet(IEnumerable<KeyValuePair<string, byte[]>> records)
        {
            using (SHA256 sha = SHA256.Create())
            {
                foreach (KeyValuePair<string, byte[]> record in records.OrderBy(item => item.Key,
                    StringComparer.Ordinal))
                {
                    byte[] name = Encoding.UTF8.GetBytes(record.Key + "\n");
                    sha.TransformBlock(name, 0, name.Length, name, 0);
                    byte[] bytes = record.Value ?? Array.Empty<byte>();
                    sha.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
                    byte[] separator = { (byte)'\n' };
                    sha.TransformBlock(separator, 0, separator.Length, separator, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string Sha256GuardBlockSet(IEnumerable<DheNativeManifestMethod> methods,
            string relativeRoot)
        {
            string root = RequireDirectory(relativeRoot, "DHE generated C++ hash root")
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string prefix = root + Path.DirectorySeparatorChar;
            List<DheGuardHashRecord> records = new List<DheGuardHashRecord>();
            HashSet<string> identities = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, Dictionary<string, string>> blocksByPath =
                new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (DheNativeManifestMethod method in methods ?? Array.Empty<DheNativeManifestMethod>())
            {
                string path = Path.GetFullPath(RequireFile(method.sourceFile,
                    "DHE generated C++ guard source"));
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new BuildFailedException("DHE generated C++ guard source escapes its root: " + path);
                string relative = path.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar, '/');
                string identity = relative + "\n" + method.functionName + "\n" +
                    method.methodToken.ToString(CultureInfo.InvariantCulture);
                if (!identities.Add(identity))
                    throw new BuildFailedException("DHE native manifest contains a duplicate guard identity: " +
                        method.functionName + "/" + method.methodToken);
                if (!blocksByPath.TryGetValue(path, out Dictionary<string, string> blocks))
                {
                    blocks = IndexExistingGuardBlocks(File.ReadAllText(path, Encoding.UTF8));
                    blocksByPath.Add(path, blocks);
                }
                string marker = GetGuardMarker(NativeGuardBeginPrefix, method);
                if (!blocks.TryGetValue(marker, out string block))
                    throw new BuildFailedException("DHE generated C++ guard block is missing: " + marker);
                records.Add(new DheGuardHashRecord
                {
                    RelativePath = relative,
                    FunctionName = method.functionName,
                    MethodToken = method.methodToken,
                    Block = block,
                });
            }
            using (SHA256 sha = SHA256.Create())
            {
                byte[] domain = Encoding.UTF8.GetBytes(NativeGuardHashContract + "\n");
                sha.TransformBlock(domain, 0, domain.Length, domain, 0);
                foreach (DheGuardHashRecord record in records
                             .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
                             .ThenBy(item => item.FunctionName, StringComparer.Ordinal)
                             .ThenBy(item => item.MethodToken))
                {
                    byte[] header = Encoding.UTF8.GetBytes(record.RelativePath + "\n" + record.FunctionName +
                        "\n" + record.MethodToken.ToString(CultureInfo.InvariantCulture) + "\n");
                    sha.TransformBlock(header, 0, header.Length, header, 0);
                    byte[] block = Encoding.UTF8.GetBytes(record.Block + "\n");
                    sha.TransformBlock(block, 0, block.Length, block, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static DheAotMetadataManifest ValidateAotMetadataFallback(
            DheRuntimePlanOptions options, string fallbackRoot, string[] expectedAssemblies)
        {
            string manifestPath = RequireFile(options.AotMetadataFallbackManifestPath,
                "DHE AOT metadata fallback manifest");
            DheAotMetadataManifest manifest;
            try
            {
                manifest = JsonUtility.FromJson<DheAotMetadataManifest>(File.ReadAllText(manifestPath));
            }
            catch (Exception exception)
            {
                throw new BuildFailedException("DHE AOT metadata fallback manifest is invalid: " +
                    manifestPath + " (" + exception.Message + ")");
            }
            if (manifest == null || manifest.schemaVersion != 1 ||
                !string.Equals(manifest.format, "hybridclr.dhe-aot-metadata-manifest.json",
                    StringComparison.Ordinal) ||
                !string.Equals(manifest.pathSemantics, "workspace-absolute-v1",
                    StringComparison.Ordinal) ||
                !string.Equals(manifest.kind, "patch-aot-metadata", StringComparison.Ordinal))
            {
                throw new BuildFailedException("DHE AOT metadata fallback manifest has an unsupported schema: " +
                    manifestPath);
            }

            string expectedTarget = string.IsNullOrWhiteSpace(options.AotMetadataFallbackExpectedTarget)
                ? (options.Target == BuildTarget.NoTarget
                    ? EditorUserBuildSettings.activeBuildTarget.ToString()
                    : options.Target.ToString())
                : options.AotMetadataFallbackExpectedTarget.Trim();
            if (!string.Equals(manifest.target, expectedTarget, StringComparison.OrdinalIgnoreCase))
            {
                throw new BuildFailedException("DHE AOT metadata fallback target does not match: " +
                    manifest.target + " / " + expectedTarget);
            }
            if (!string.Equals(Path.GetFullPath(manifest.sourceRoot),
                    Path.GetFullPath(fallbackRoot), StringComparison.OrdinalIgnoreCase))
            {
                throw new BuildFailedException("DHE AOT metadata fallback sourceRoot does not match the supplied root.");
            }
            if (!MatchesCurrentEngine(manifest.engine))
            {
                throw new BuildFailedException(
                    "DHE AOT metadata fallback engine identity does not match the current editor.");
            }
            if (manifest.runtime == null ||
                string.IsNullOrWhiteSpace(manifest.runtime.profile) ||
                !IsSha256(manifest.runtime.stagedRuntimeSha256) ||
                !IsSha256(manifest.runtime.runtimeManifestSha256) ||
                !Sha256Equals(manifest.runtime.stagedRuntimeSha256,
                    options.AotMetadataFallbackExpectedStagedRuntimeSha256, false) ||
                !Sha256Equals(manifest.runtime.runtimeManifestSha256,
                    options.AotMetadataFallbackExpectedRuntimeManifestSha256, false) ||
                (!string.IsNullOrWhiteSpace(options.AotMetadataFallbackExpectedPackageTreeSha256) &&
                 !Sha256Equals(manifest.runtime.packageTreeSha256,
                    options.AotMetadataFallbackExpectedPackageTreeSha256, true)))
            {
                throw new BuildFailedException("DHE AOT metadata fallback runtime identity does not match the requested identity.");
            }

            string[] expected = NormalizeAssemblyNames(expectedAssemblies)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
            DheAotMetadataRecord[] records = manifest.assemblies ?? Array.Empty<DheAotMetadataRecord>();
            string[] actual = records.Select(record => NormalizeAssemblyName(
                    record == null ? null : record.assemblyName))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
            if (expected.Length == 0 || records.Length != expected.Length ||
                !expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase))
            {
                throw new BuildFailedException("DHE AOT metadata fallback manifest assembly set does not match patchAOTAssemblies.");
            }
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DheAotMetadataRecord record in records)
            {
                string name = NormalizeAssemblyName(record == null ? null : record.assemblyName);
                if (!seen.Add(name) || string.IsNullOrWhiteSpace(record.sha256) ||
                    record.sha256.Length != 64)
                {
                    throw new BuildFailedException("DHE AOT metadata fallback manifest contains an invalid assembly record.");
                }
                string path = RequireFile(Path.Combine(fallbackRoot, name + DllExtension),
                    name + " DHE fallback AOT metadata");
                if (!string.Equals(Sha256Hex(File.ReadAllBytes(path)), record.sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new BuildFailedException("DHE fallback AOT metadata hash does not match manifest: " + name);
                }
            }
            return manifest;
        }

        private static bool MatchesCurrentEngine(DheAotMetadataEngine engine)
        {
            if (engine == null || string.IsNullOrWhiteSpace(engine.family) ||
                string.IsNullOrWhiteSpace(engine.version) ||
                string.IsNullOrWhiteSpace(engine.unityVersion))
            {
                return false;
            }

            // Tuanjie exposes its own product version (for example 1.10.0)
            // alongside the Unity-compatible editor version (for example
            // 2022.3.62t12). Unity exposes the same value in both fields.
            // Bind the editor ABI to unityVersion, while retaining the product
            // version as provenance instead of incorrectly requiring equality.
            if (string.Equals(engine.family, "Tuanjie", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(engine.unityVersion, Application.unityVersion,
                    StringComparison.Ordinal);
            }
            if (string.Equals(engine.family, "Unity", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(engine.unityVersion, Application.unityVersion,
                           StringComparison.Ordinal) &&
                    string.Equals(engine.version, Application.unityVersion,
                        StringComparison.Ordinal);
            }
            return false;
        }

        private static bool Sha256Equals(string actual, string expected, bool required)
        {
            if (string.IsNullOrWhiteSpace(expected)) return !required;
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
            return value.All(character => (character >= '0' && character <= '9') ||
                (character >= 'a' && character <= 'f') ||
                (character >= 'A' && character <= 'F'));
        }

        private static string SerializeStringArray(IEnumerable<string> values)
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder("[");
            bool first = true;
            foreach (string value in values ?? Array.Empty<string>())
            {
                if (!first) builder.Append(',');
                first = false;
                string encoded = JsonUtility.ToJson(new StringValueDocument { value = value });
                const string prefix = "{\"value\":";
                builder.Append(encoded.Substring(prefix.Length, encoded.Length - prefix.Length - 1));
            }
            builder.Append(']');
            return builder.ToString();
        }

        [Serializable]
        private sealed class DheMvDocument
        {
            public string assemblyName;
            public DheMvMethod[] methods;
        }

        [Serializable]
        private sealed class DheMvMethod
        {
            public string identity;
            public string stableId;
            public string name;
            public uint token;
            public uint flags;
            public string declaringType;
            public string returnType;
            public string[] parameterTypes;
            public bool isStatic;
            public bool isAbstract;
            public bool isPInvoke;
            public bool hasThis;
            public bool declaringTypeIsValueType;
            public int genericParameterCount;
            public int declaringTypeGenericParameterCount;
        }

        private sealed class DheGuardMethod
        {
            public string AssemblyName;
            public string ManagedId;
            public string StableMethodIdSha256;
            public uint MethodToken;
            public string MethodName;
            public string DeclaringType;
            public string ReturnType;
            public string[] ManagedParameterTypes;
            public bool IsStatic;
            public bool HasThis;
            public bool DeclaringTypeIsValueType;
            public uint GenericParameterCount;
            public uint DeclaringTypeGenericParameterCount;
            public bool IsChanged;
        }

        private sealed class DheCppDefinition
        {
            public string File;
            public string Signature;
            public string FunctionName;
            public string ParametersText;
            public int BodyStart;
            public string ManagedSignature;
        }

        private sealed class DheCodeGenModule
        {
            public string AssemblyName;
            public string[] MethodPointers;
        }

        private sealed class DheMetadataImage
        {
            public string AssemblyName;
            public int TypeStart;
            public int TypeCount;
        }

        [Serializable]
        private sealed class DheNativeParameter
        {
            public string Type;
            public string Name;
        }

        [Serializable]
        private sealed class DheNativeManifestMethod
        {
            public string functionName;
            [NonSerialized] public string nativeSignature;
            [NonSerialized] public int bodyStart;
            public string returnType;
            public DheNativeParameter[] parameters;
            public string sourceFile;
            public string assemblyName;
            public string declaringType;
            public string methodName;
            public uint methodToken;
            public string managedId;
            public string stableMethodIdSha256;
            public string managedReturnType;
            public string[] managedParameterTypes;
            public bool managedHasThis;
            public bool declaringTypeIsValueType;
            public uint genericParameterCount;
            public uint declaringTypeGenericParameterCount;
            public string bridgeKind;
            public bool usesHiddenReturnBuffer;
            public bool isStatic;
            public bool hasThis;
            public int managedParameterCount;
        }

        [Serializable]
        private sealed class DheNativeManifestIssue
        {
            public string id;
            public string name;
            public string assemblyName;
            public string declaringType;
            public uint methodToken;
            public string managedId;
            public string stableMethodIdSha256;
            public string[] reasons;
            [NonSerialized] public bool isChanged;
        }

        [Serializable]
        private sealed class DheNativeManifestDocument
        {
            public int schemaVersion;
            public int resolverVersion;
            public string abiContract;
            public string guardHashContract;
			public string runtimeProtocol;
            public string runtimeContract;
            public string[] runtimeCapabilities;
            public string generatedCppRoot;
            public int changedMethodCount;
            public int supportedChangedMethodCount;
            public int unsupportedChangedMethodCount;
            public string guardMode;
            public int guardedMethodCount;
            public int supportedGuardedMethodCount;
            public int unsupportedGuardedMethodCount;
            public int interpreterOnlyMethodCount;
            public int nativeEntryCount;
            public DheNativeManifestMethod[] methods;
            public DheNativeManifestIssue[] interpreterOnlyMethods;
            public DheNativeManifestIssue[] unsupportedChangedMethods;
        }

        private sealed class DheGuardHashRecord
        {
            public string RelativePath;
            public string FunctionName;
            public uint MethodToken;
            public string Block;
        }

        [Serializable]
        public sealed class DheProjectPlan
        {
            public int schemaVersion;
            public string format;
            public bool complete;
            public DheProjectAssembly[] assemblies;
        }

        [Serializable]
        public sealed class DheProjectAssembly
        {
            public string assemblyName;
            public string status;
            public string current;
            public string baseline;
            public string baseMetaVersionJson;
            public string baseMetaVersionBytes;
            public string currentMetaVersionJson;
            public string currentMetaVersionBytes;
        }

        [Serializable]
        private sealed class DheRuntimePlanDocument
        {
            public int schemaVersion;
            public string format;
            public string currentAssemblySetSha256;
			public string runtimeAssetRoot;
            public string baseMetaVersionAssetRoot;
            public string selection;
            public string aotMetadataSetId;
            public DheRuntimePlanAotMetadata[] aotMetadata;
            public DheRuntimePlanAotMetadataSet[] aotMetadataSets;
            public DheRuntimePlanBaseSelection[] baseSelections;
            public string aotMetadataManifestSha256;
            public DheRuntimePlanAssembly[] assemblies;
        }

        [Serializable]
        private sealed class DheRuntimePlanHandoffDocument
        {
            public int schemaVersion;
            public string format;
            public string baseAssemblySetSha256;
            public string selection;
            public string aotMetadataManifestSha256;
            public DheRuntimePlanAotMetadata[] aotMetadata;
            public DheRuntimePlanHandoffAssembly[] assemblies;
        }

        [Serializable]
        private sealed class StringValueDocument
        {
            public string value;
        }

        [Serializable]
        private sealed class DheRuntimePlanAssembly
        {
            public string assemblyName;
            public string current;
            public string currentMetaVersion;
            public string baseMetaVersion;
            public string currentSha256;
            public string currentMetaVersionSha256;
        }

        [Serializable]
        private sealed class DheRuntimePlanAotMetadata
        {
            public string assemblyName;
            public string sourceKind;
            public string sha256;
            public string manifestSha256;
            public string path;
        }

        [Serializable]
        private sealed class DheRuntimePlanAotMetadataSet
        {
            public string aotMetadataSetId;
            public DheRuntimePlanAotMetadata[] assemblies;
        }

        [Serializable]
        private sealed class DheRuntimePlanBaseSelection
        {
            public string baseId;
            public string aotMetadataSetId;
        }

        [Serializable]
        private sealed class DheAotMetadataManifest
        {
            public int schemaVersion;
            public string format;
            public string pathSemantics;
            public string kind;
            public string target;
            public string sourceRoot;
            public string engineWorkflow;
            public DheAotMetadataEngine engine;
            public DheAotMetadataRuntime runtime;
            public DheAotMetadataRecord[] assemblies;
        }

        [Serializable]
        private sealed class DheAotMetadataEngine
        {
            public string family;
            public string version;
            public string unityVersion;
        }

        [Serializable]
        private sealed class DheAotMetadataRuntime
        {
            public string profile;
            public string stagedRuntimeSha256;
            public string runtimeManifestSha256;
            public string packageTreeSha256;
        }

        [Serializable]
        private sealed class DheAotMetadataRecord
        {
            public string assemblyName;
            public string sha256;
        }

        [Serializable]
        private sealed class DheRuntimePlanHandoffAssembly
        {
            public string assemblyName;
            public string current;
            public string baseline;
            public string snapshot;
            public string baseMetaVersion;
            public string currentMetaVersion;
            public string baselineSha256;
            public string currentSha256;
            public string snapshotSha256;
            public string baseMetaVersionSha256;
            public string currentMetaVersionSha256;
        }
    }

    public sealed class DheNativeGuardOptions
    {
        public string[] MvJsonPaths;
        public string GeneratedCppRoot;
        public string OutputManifestPath;
        public bool RequireCompleteCoverage = true;
        /// <summary>
        /// Base Player mode. Emit a guard for every supported method in the
        /// MV method table, including unchanged methods, so later resource-only
        /// releases can select interpreter/AOT without rebuilding native code.
        /// </summary>
        public bool GuardAllMethods;
    }

    public sealed class DheNativeGuardResult
    {
        public string ManifestPath;
        public string RuntimeProtocol;
        public string RuntimeContract;
        public string[] RuntimeCapabilities;
        public int RequestedMethodCount;
        public int TransformedMethodCount;
        public int NativeEntryCount;
        public int UnsupportedMethodCount;
        public string[] GeneratedCppPaths;
        public string NativeGuardSourceSha256;
        public string NativeManifestSha256;
        public string GuardMode;
        public int GuardedMethodCount;
        public int SupportedGuardedMethodCount;
        public int UnsupportedGuardedMethodCount;
        public int InterpreterOnlyMethodCount;
        public int UnsupportedChangedMethodCount;
    }

    public sealed class DheNativeFinalizeOptions
    {
        public string ProjectRoot;
        public string ProjectPlanPath;
        public string GeneratedCppRoot;
        public string OutputManifestPath;
        public string BeeLogPath;
        public bool RequireCompleteCoverage = true;
        public bool GuardAllMethods;
        public bool RebuildPlayer;
        public int BeeMaxAttempts = 3;
        public int BeeTimeoutSeconds = 600;
    }

    public sealed class DheNativeFinalizeResult
    {
        public string ProjectPlanPath;
        public string GeneratedCppRoot;
        public string[] AssemblyNames;
        public DheNativeGuardResult GuardResult;
        public DheBeeRebuildResult BeeRebuildResult;
    }

    public sealed class DheProjectPrepareOptions
    {
        public BuildTarget Target;
        public string Mode = "Exploratory";
        public string BaselineSourceRoot;
        public string BaselineOutputRoot;
        public string CurrentAotRoot;
        public string CurrentOutputRoot;
        public bool Bootstrap;
        public bool RequireDheEqualsHotUpdate = true;
        public Action<string[]> BeforeCurrentGeneration;
        public Action<string[]> AfterCurrentGeneration;
    }

    public sealed class DheProjectPrepareResult
    {
        public BuildTarget Target;
        public string Mode;
        public string BaselineSourceRoot;
        public string BaselineOutputRoot;
        public string CurrentSourceRoot;
        public string CurrentOutputRoot;
        public bool BaselineGeneratedFromCurrent;
        public string[] HotUpdateAssemblyNames;
        public string[] DheAotAssemblyNames;
    }

    public sealed class DheBeeRebuildOptions
    {
        public string ProjectRoot;
        public string GeneratedCppRoot;
        public string LogPath;
        public int MaxAttempts = 3;
        public int TimeoutSeconds = 600;
    }

    public sealed class DheBeeRebuildResult
    {
        public string BeeBackendPath;
        public string DagPath;
        public string LogPath;
        public int Attempts;
        public int ExitCode;
    }

    public sealed class DheRuntimePlanOptions
    {
        /// <summary>
        /// Target used for all target-bound output and fallback validation.
        /// NoTarget keeps the editor's active target for legacy callers.
        /// </summary>
        public BuildTarget Target = BuildTarget.NoTarget;
        public string ProjectRoot;
        public string ProjectPlanPath;
        public string RuntimeAssetRoot;
        public string BaseMetaVersionAssetRoot;
        public string OutputRoot;
        public string StrippedAotRoot;
        /// <summary>
        /// Optional target-bound fallback for patch-AOT metadata that Unity did
        /// not emit in the current stripped directory because it was not linked.
        /// The project adapter owns the provenance and must bind this root to
        /// the same target/release baseline before calling the package API.
        /// </summary>
        public string AotMetadataFallbackRoot;
        public string AotMetadataFallbackManifestPath;
        public string AotMetadataFallbackExpectedTarget;
        public string AotMetadataFallbackExpectedStagedRuntimeSha256;
        public string AotMetadataFallbackExpectedRuntimeManifestSha256;
        public string AotMetadataFallbackExpectedPackageTreeSha256;
        public string[] AotMetadataAssemblyNames;
        /// <summary>
        /// Complete hot-update load set. It may contain legacy hotfix
        /// assemblies in addition to the DHE subset; the package only stages
        /// DHE payloads and leaves the other project-owned bytes intact.
        /// </summary>
        public string[] HotfixAssemblyNames;
        /// <summary>
        /// Optional project-owned mapping from an absolute staged asset file
        /// to the locator serialized into the runtime plan. The default emits
        /// a StreamingAssets-relative path; YooAsset/Addressables projects can
        /// emit their catalog asset path without changing package staging.
        /// </summary>
        public Func<string, string> RuntimeAssetPathResolver;
        public Func<string, byte[], byte[]> CurrentAssemblyTransform;
        public Func<string[], string[]> HotfixLoadOrderResolver;
        public Action<string> DependencyMapWriter;
    }

    public sealed class DheRuntimePlanResult
    {
        public string ProjectPlanPath;
        public string RuntimeAssetRoot;
        public string RuntimePlanPath;
        public string HandoffRoot;
        public string HandoffPlanPath;
        public string[] AssemblyNames;
    }

    public sealed class DhePlayerBuildOptions
    {
        public string OutputPath;
        public string BaselineAotRoot;
        public BuildTarget Target;
        public BuildOptions BuildOptions;
        /// <summary>
        /// Rebuilds player data from the staged runtime plan. DHE assets are
        /// generated during the workflow, so incremental data caches must not
        /// silently retain a prior plan or build identity.
        /// </summary>
        public bool CleanBuild;
        public string[] Scenes;
        /// <summary>
        /// Optional project-owned build wrapper. The callback receives the
        /// fully populated Unity options after the package has bound the
        /// previous stripped-AOT baseline in the process environment.
        /// </summary>
        public Func<BuildPlayerOptions, BuildReport> BuildPlayerCallback;
        /// <summary>
        /// Optional package-owned native finalization. When configured, the
        /// package resolves the project plan, injects guards and rebuilds Bee
        /// before temporary baseline assembly inputs are restored.
        /// </summary>
        public DheNativeFinalizeOptions NativeFinalizeOptions;
        public Action<DheNativeFinalizeResult> NativeFinalizeResultCallback;
        /// <summary>
        /// Optional generated-C++ finalizer invoked after BuildPlayer succeeds
        /// but before temporary baseline assembly substitutions are restored.
        /// A DHE adapter uses this hook to inject guards and re-evaluate Bee's
        /// native graph without invalidating the IL2CPP frontend inputs.
        /// </summary>
        public Action<BuildReport> GeneratedCppFinalizeCallback;
    }
}
