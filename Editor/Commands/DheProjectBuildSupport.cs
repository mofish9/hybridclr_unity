using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace HybridCLR.Editor.Commands
{
    /// <summary>
    /// Package-owned project workflow support. Project adapters provide build
    /// and resource callbacks, while native evidence and Player identity stay
    /// identical across projects.
    /// </summary>
    public static class DheProjectBuildSupport
    {
        private const string AotSnapshotKind = "managed-assembly-plus-generated-cpp-v1";
        private const string ZeroSha256 =
            "0000000000000000000000000000000000000000000000000000000000000000";

        public static void ValidateBuildConfiguration(string engineWorkflow,
            string il2cppCodeGeneration)
        {
            string expected = engineWorkflow switch
            {
                "Unity2021Standard" => "OptimizeSpeed",
                "Unity2022Fgs" => "OptimizeSize",
                "Tuanjie2022Fgs" => "OptimizeSize",
                _ => throw new BuildFailedException(
                    "DHE engine workflow is unsupported: " + engineWorkflow),
            };
            if (!string.Equals(il2cppCodeGeneration, expected, StringComparison.Ordinal))
                throw new BuildFailedException("DHE engine workflow " + engineWorkflow +
                    " requires IL2CPP code generation " + expected + ", got " +
                    il2cppCodeGeneration + ".");
        }

        public static void ApplyIl2CppCodeGeneration(BuildTarget target, string expected)
        {
            if (!Enum.TryParse(expected, false, out Il2CppCodeGeneration value) ||
                value != Il2CppCodeGeneration.OptimizeSpeed &&
                value != Il2CppCodeGeneration.OptimizeSize)
                throw new BuildFailedException(
                    "DHE IL2CPP code generation must be OptimizeSpeed or OptimizeSize.");
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
#if UNITY_2022_1_OR_NEWER
            PlayerSettings.SetIl2CppCodeGeneration(
                NamedBuildTarget.FromBuildTargetGroup(group), value);
#else
            EditorUserBuildSettings.il2CppCodeGeneration = value;
#endif
            if (!string.Equals(GetIl2CppCodeGeneration(target), expected,
                    StringComparison.Ordinal))
                throw new BuildFailedException(
                    "DHE failed to apply the required IL2CPP code generation mode.");
        }

        public static string GetIl2CppCodeGeneration(BuildTarget target)
        {
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
#if UNITY_2022_1_OR_NEWER
            return PlayerSettings.GetIl2CppCodeGeneration(
                NamedBuildTarget.FromBuildTargetGroup(group)).ToString();
#else
            return EditorUserBuildSettings.il2CppCodeGeneration.ToString();
#endif
        }

        public static DheNativeFinalizeOptions CreateNativeFinalizeOptions(
            DheProjectNativeOptions options, bool rebuildPlayer)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            string outputRoot = Path.GetFullPath(options.OutputRoot);
            return new DheNativeFinalizeOptions
            {
                ProjectRoot = Path.GetFullPath(options.ProjectRoot),
                ProjectPlanPath = Path.GetFullPath(options.ProjectPlanPath),
                OutputManifestPath = Path.Combine(outputRoot, "native", "dhe-native-manifest.json"),
                BeeLogPath = Path.Combine(outputRoot, "native", "bee-rebuild.log"),
                RequireCompleteCoverage = true,
                RebuildPlayer = rebuildPlayer,
                GuardAllMethods = options.GuardAllMethods,
                BeeMaxAttempts = options.BeeMaxAttempts,
                BeeTimeoutSeconds = options.BeeTimeoutSeconds,
            };
        }

        public static void WriteNativeEvidence(DheProjectNativeOptions options,
            DheNativeFinalizeResult nativeResult, bool final)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (nativeResult?.GuardResult == null)
                throw new BuildFailedException("DHE native finalization returned no guard result.");

            string adapterRoot = Path.Combine(Path.GetFullPath(options.OutputRoot), "adapter");
            Directory.CreateDirectory(adapterRoot);
            DheNativeGuardResult guard = nativeResult.GuardResult;
            bool guardPassed = guard.UnsupportedChangedMethodCount == 0 &&
                (guard.RequestedMethodCount == 0 || guard.NativeEntryCount > 0);
            WriteJson(Path.Combine(adapterRoot, "native-guards.json"), new NativeGuardsEvidence
            {
                schemaVersion = 1,
                format = "hybridclr.dhe-adapter-native-guards.json",
                generatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                passed = guardPassed,
                target = options.Target,
                generatedCppRoot = nativeResult.GeneratedCppRoot,
                generatedCppPaths = guard.GeneratedCppPaths ?? Array.Empty<string>(),
                manifestPath = guard.ManifestPath,
                nativeGuardSourceSha256 = guard.NativeGuardSourceSha256,
                nativeManifestSha256 = guard.NativeManifestSha256,
                requestedMethodCount = guard.RequestedMethodCount,
                transformedMethodCount = guard.TransformedMethodCount,
                nativeEntryCount = guard.NativeEntryCount,
                unsupportedMethodCount = guard.UnsupportedMethodCount,
                guardMode = guard.GuardMode,
                guardedMethodCount = guard.GuardedMethodCount,
                supportedGuardedMethodCount = guard.SupportedGuardedMethodCount,
                unsupportedGuardedMethodCount = guard.UnsupportedGuardedMethodCount,
                interpreterOnlyMethodCount = guard.InterpreterOnlyMethodCount,
                unsupportedChangedMethodCount = guard.UnsupportedChangedMethodCount,
            });
            if (!guardPassed)
                throw new BuildFailedException("DHE native guard coverage is incomplete.");
            if (!final) return;

            DheBeeRebuildResult rebuild = nativeResult.BeeRebuildResult;
            DhePlayerArtifactFinalizeResult artifact = nativeResult.PlayerArtifactResult;
            bool artifactRequired = string.Equals(options.Target, BuildTarget.Android.ToString(),
                StringComparison.OrdinalIgnoreCase);
            bool artifactPassed = !artifactRequired || artifact != null && artifact.Passed &&
                artifact.ExitCode == 0 && !string.IsNullOrWhiteSpace(artifact.OutputSha256) &&
                !string.IsNullOrWhiteSpace(artifact.GradleRoot) &&
                artifact.NativeLibraryEntries != null &&
                artifact.NativeLibrarySourcePaths != null &&
                artifact.NativeLibrarySha256 != null &&
                artifact.NativeLibraryEntries.Length > 0 &&
                artifact.NativeLibraryEntries.Length == artifact.NativeLibrarySourcePaths.Length &&
                artifact.NativeLibraryEntries.Length == artifact.NativeLibrarySha256.Length;
            bool rebuildPassed = rebuild != null && rebuild.ExitCode == 0 && artifactPassed;
            WriteJson(Path.Combine(adapterRoot, "native-finalize.json"), new NativeFinalizeEvidence
            {
                schemaVersion = 1,
                format = "hybridclr.dhe-adapter-native-finalize.json",
                generatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                passed = rebuildPassed,
                target = options.Target,
                generatedCppRoot = nativeResult.GeneratedCppRoot,
                manifestPath = guard.ManifestPath,
                nativeGuardSourceSha256 = guard.NativeGuardSourceSha256,
                nativeManifestSha256 = guard.NativeManifestSha256,
                beeBackendPath = rebuild?.BeeBackendPath,
                dagPath = rebuild?.DagPath,
                logPath = rebuild?.LogPath,
                attempts = rebuild?.Attempts ?? 0,
                exitCode = rebuild?.ExitCode ?? -1,
                graphRegenerations = rebuild?.GraphRegenerations ?? 0,
                guardReapplications = rebuild?.GuardReapplications ?? 0,
                buildProgramPath = rebuild?.BuildProgramPath,
                playerArtifactKind = artifact?.Kind,
                playerArtifactPath = artifact?.OutputPath,
                playerArtifactSha256 = artifact?.OutputSha256,
                playerArtifactGradleRoot = artifact?.GradleRoot,
                playerArtifactBuildToolPath = artifact?.BuildToolPath,
                playerArtifactBuildProgramPath = artifact?.BuildProgramPath,
                playerArtifactBuildTask = artifact?.BuildTask,
                playerArtifactBuildLogPath = artifact?.BuildLogPath,
                playerArtifactExitCode = artifact?.ExitCode ?? -1,
                playerArtifactNativeLibraryEntries = artifact?.NativeLibraryEntries ?? Array.Empty<string>(),
                playerArtifactNativeLibrarySourcePaths = artifact?.NativeLibrarySourcePaths ?? Array.Empty<string>(),
                playerArtifactNativeLibrarySha256 = artifact?.NativeLibrarySha256 ?? Array.Empty<string>(),
            });
            if (!rebuildPassed)
                throw new BuildFailedException("DHE native Player rebuild did not complete.");
        }

        public static void StageBuildIdentity(DheProjectIdentityOptions options,
            DheNativeFinalizeResult nativeResult)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (nativeResult?.GuardResult == null)
                throw new BuildFailedException(
                    "DHE build identity requires a native finalization result.");

            DheBuildPipeline.ValidateAssemblyScope(true, out _, out string[] configuredAssemblies);
            string[] assemblyNames = (nativeResult.AssemblyNames ?? Array.Empty<string>())
                .Select(NormalizeAssemblyName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            if (assemblyNames.Length == 0 ||
                !new HashSet<string>(assemblyNames, StringComparer.OrdinalIgnoreCase)
                    .SetEquals(configuredAssemblies))
                throw new BuildFailedException(
                    "DHE build identity assembly set does not match project settings.");

            if (string.IsNullOrWhiteSpace(options.AotAssemblyRoot))
                throw new BuildFailedException(
                    "DHE stripped AOT assembly inventory root is not configured.");
            string aotAssemblyRoot = Path.GetFullPath(options.AotAssemblyRoot);
            string[] aotAssemblyNames = ReadAotAssemblyInventory(aotAssemblyRoot);
            var aotAssemblyNameSet = new HashSet<string>(aotAssemblyNames,
                StringComparer.OrdinalIgnoreCase);
            string[] missingDheAssemblies = assemblyNames.Where(name =>
                !aotAssemblyNameSet.Contains(name)).ToArray();
            if (missingDheAssemblies.Length != 0)
                throw new BuildFailedException(
                    "DHE Base assemblies are missing from the stripped AOT inventory: " +
                    string.Join(", ", missingDheAssemblies));
            string aotAssemblySetHash = Sha256AssemblyNameSet(aotAssemblyNames);

            string baselineRoot = Path.GetFullPath(options.BaselineRoot);
            var baselineRecords = new List<KeyValuePair<string, byte[]>>();
            var snapshotRecords = new List<KeyValuePair<string, byte[]>>();
            var baseMetaVersionRecords = new List<KeyValuePair<string, byte[]>>();
            var baseMetaVersionHashes = new List<string>();
            var assemblyEvidence = new List<BuildIdentityAssembly>();
            string projectPlanPath = RequireFile(options.ProjectPlanPath,
                "project plan for build identity");
            BuildIdentityProjectPlan projectPlan = JsonUtility.FromJson<BuildIdentityProjectPlan>(
                File.ReadAllText(projectPlanPath));
            if (projectPlan?.assemblies == null)
                throw new BuildFailedException("DHE build identity project plan has no assemblies.");
            string projectPlanRoot = Path.GetDirectoryName(projectPlanPath);
            foreach (string assemblyName in assemblyNames)
            {
                string baselinePath = RequireFile(Path.Combine(baselineRoot, assemblyName + ".dll"),
                    assemblyName + " baseline assembly for build identity");
                byte[] baselineBytes = File.ReadAllBytes(baselinePath);
                byte[] snapshotBytes = Sha256(baselineBytes);
                string snapshotHash = ToHex(snapshotBytes);
                BuildIdentityProjectAssembly planAssembly = projectPlan.assemblies.SingleOrDefault(item =>
                    string.Equals(NormalizeAssemblyName(item?.assemblyName), assemblyName,
                        StringComparison.OrdinalIgnoreCase));
                if (planAssembly == null || string.IsNullOrWhiteSpace(
                        planAssembly.baseMetaVersionBytes))
                    throw new BuildFailedException("DHE project plan has no Base MetaVersion for " +
                        assemblyName + ".");
                string baseMetaVersionPath = Path.IsPathRooted(planAssembly.baseMetaVersionBytes)
                    ? Path.GetFullPath(planAssembly.baseMetaVersionBytes)
                    : Path.GetFullPath(Path.Combine(projectPlanRoot,
                        planAssembly.baseMetaVersionBytes));
                byte[] baseMetaVersionBytes = File.ReadAllBytes(RequireFile(baseMetaVersionPath,
                    assemblyName + " Base MetaVersion for build identity"));
                string baseMetaVersionHash = ToHex(Sha256(baseMetaVersionBytes));
                string embeddedBaseMetaVersionPath = ResolveOptionalProjectPath(options.ProjectRoot,
                    options.BaseMetaVersionAssetRoot, assemblyName + ".mv.bytes");
                string embeddedBaseMetaVersionHash = string.Empty;
                if (!string.IsNullOrWhiteSpace(embeddedBaseMetaVersionPath))
                {
                    embeddedBaseMetaVersionPath = RequireFile(embeddedBaseMetaVersionPath,
                        assemblyName + " embedded Base MetaVersion");
                    embeddedBaseMetaVersionHash = ToHex(Sha256(
                        File.ReadAllBytes(embeddedBaseMetaVersionPath)));
                    if (!string.Equals(baseMetaVersionHash, embeddedBaseMetaVersionHash,
                            StringComparison.OrdinalIgnoreCase))
                        throw new BuildFailedException("DHE embedded Base MetaVersion does not match " +
                            "the current Base plan for " + assemblyName + ". Run StageRuntimePlan again.");
                }
                baselineRecords.Add(new KeyValuePair<string, byte[]>(assemblyName, baselineBytes));
                snapshotRecords.Add(new KeyValuePair<string, byte[]>(assemblyName, snapshotBytes));
                baseMetaVersionRecords.Add(new KeyValuePair<string, byte[]>(assemblyName,
                    baseMetaVersionBytes));
                baseMetaVersionHashes.Add(baseMetaVersionHash);
                assemblyEvidence.Add(new BuildIdentityAssembly
                {
                    assemblyName = assemblyName,
                    baselinePath = baselinePath,
                    baselineSha256 = snapshotHash,
                    snapshotSha256 = snapshotHash,
                    baseMetaVersionPath = baseMetaVersionPath,
                    baseMetaVersionSha256 = baseMetaVersionHash,
                    embeddedBaseMetaVersionPath = ToProjectRelativePath(options.ProjectRoot,
                        embeddedBaseMetaVersionPath),
                    embeddedBaseMetaVersionSha256 = embeddedBaseMetaVersionHash,
                });
            }

            string baselineSetHash = Sha256NamedByteSet(baselineRecords);
            string snapshotSetHash = Sha256NamedByteSet(snapshotRecords);
            string baseMetaVersionSetHash = Sha256NamedByteSet(baseMetaVersionRecords);
            DheNativeGuardResult guard = nativeResult.GuardResult;
            string runtimePlanPath = RequireFile(ResolveProjectPath(options.ProjectRoot,
                options.RuntimePlanPath), "runtime plan for build identity");
            BuildIdentityRuntimePlan runtimePlan = JsonUtility.FromJson<BuildIdentityRuntimePlan>(
                File.ReadAllText(runtimePlanPath));
            if (runtimePlan == null || runtimePlan.schemaVersion != 1 ||
                !string.Equals(runtimePlan.format,
                    "hybridclr.dhe-runtime-asset-plan.json", StringComparison.Ordinal) ||
                !IsSha256(runtimePlan.aotMetadataSetId))
                throw new BuildFailedException(
                    "DHE build identity requires the current runtime plan schema.");
            ValidateBaseRuntimePlanMetadata(options.ProjectRoot, runtimePlanPath, runtimePlan,
                aotAssemblyRoot);
            string runtimeAssetRoot = NormalizeAssetRoot(runtimePlan.runtimeAssetRoot,
                "runtime asset root");
            string baseMetaVersionAssetRoot = NormalizeAssetRoot(
                runtimePlan.baseMetaVersionAssetRoot, "Base MetaVersion asset root");
            if (!baseMetaVersionAssetRoot.StartsWith(runtimeAssetRoot,
                    StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException(
                    "DHE Base MetaVersion asset root must be below the runtime asset root.");
            if (string.IsNullOrWhiteSpace(guard.RuntimeProtocol) ||
                string.IsNullOrWhiteSpace(guard.RuntimeContract) ||
                guard.RuntimeCapabilities == null || guard.RuntimeCapabilities.Length == 0)
                throw new BuildFailedException(
                    "DHE native result has no runtime protocol or capability identity.");
            string[] runtimeCapabilities = guard.RuntimeCapabilities
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal).OrderBy(value => value,
                    StringComparer.Ordinal).ToArray();
            string engineWorkflow = RequireBuildIdentityValue(options.EngineWorkflow,
                "engine workflow");
            string il2cppCodeGeneration = RequireBuildIdentityValue(
                options.Il2CppCodeGeneration, "IL2CPP code generation");
            ValidateBuildConfiguration(engineWorkflow, il2cppCodeGeneration);
            string actualCodeGeneration = GetIl2CppCodeGeneration(
                ParseBuildTarget(options.Target));
            if (!string.Equals(actualCodeGeneration, il2cppCodeGeneration,
                    StringComparison.Ordinal))
                throw new BuildFailedException("DHE Player IL2CPP code generation is " +
                    actualCodeGeneration + ", expected " + il2cppCodeGeneration + ".");
            string baseId = ComputeBaseId(options.Target, engineWorkflow,
                il2cppCodeGeneration, baselineSetHash, aotAssemblySetHash,
                snapshotSetHash,
                baseMetaVersionSetHash, runtimePlan.aotMetadataSetId,
                guard.NativeGuardSourceSha256,
                guard.NativeManifestSha256, guard.RuntimeProtocol, guard.RuntimeContract,
                runtimeCapabilities, runtimeAssetRoot, baseMetaVersionAssetRoot);
            string sourcePath = ResolveProjectAsset(options.ProjectRoot,
                options.BuildIdentityAssetPath);
            string source = BuildIdentitySource(options, baseId, baselineSetHash,
                aotAssemblySetHash, aotAssemblyNames,
                snapshotSetHash, baseMetaVersionSetHash, runtimePlan.aotMetadataSetId,
                guard, runtimeCapabilities,
                runtimeAssetRoot, baseMetaVersionAssetRoot, assemblyNames,
                baseMetaVersionHashes.ToArray());
            File.WriteAllText(sourcePath, source, new UTF8Encoding(false));
            string stagedSourceSha256 = ToHex(Sha256(new UTF8Encoding(false).GetBytes(source)));

            string identityPath = Path.Combine(Path.GetFullPath(options.OutputRoot),
                "build-identity.json");
            WriteJson(identityPath, new BuildIdentityEvidence
            {
                schemaVersion = 1,
                format = "hybridclr.dhe-build-identity.json",
                workflow = options.Workflow,
                target = options.Target,
                engineWorkflow = engineWorkflow,
                il2cppCodeGeneration = il2cppCodeGeneration,
                identityVersion = 1,
                state = "staged-for-final-player",
                pathSemantics = "workspace-absolute-v1",
                stagedSourcePath = options.BuildIdentityAssetPath.Replace('\\', '/'),
                stagedSourceSha256 = stagedSourceSha256,
                baseId = baseId,
                managedAssemblySetSha256 = baselineSetHash,
                aotAssemblySetSha256 = aotAssemblySetHash,
                aotAssemblyNames = aotAssemblyNames,
                aotSnapshotSha256 = snapshotSetHash,
                aotSnapshotKind = AotSnapshotKind,
                nativeGuardSourceSha256 = guard.NativeGuardSourceSha256,
                nativeManifestSha256 = guard.NativeManifestSha256,
                baseMetaVersionSetSha256 = baseMetaVersionSetHash,
                aotMetadataSetId = runtimePlan.aotMetadataSetId,
                runtimeProtocol = guard.RuntimeProtocol,
                runtimeContract = guard.RuntimeContract,
                runtimeCapabilities = runtimeCapabilities,
                runtimeAssetRoot = runtimeAssetRoot,
                baseMetaVersionAssetRoot = baseMetaVersionAssetRoot,
                generatedCppRoot = nativeResult.GeneratedCppRoot,
                generatedCppPaths = guard.GeneratedCppPaths ?? Array.Empty<string>(),
                nativeManifestPath = guard.ManifestPath,
                assemblies = assemblyEvidence.ToArray(),
            });
            AssetDatabase.ImportAsset(options.BuildIdentityAssetPath,
                ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        }

        public static void ValidateStagedBuildIdentity(DheProjectIdentityOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            string identityPath = RequireFile(Path.Combine(Path.GetFullPath(options.OutputRoot),
                "build-identity.json"), "DHE staged build identity");
            BuildIdentityEvidence identity = JsonUtility.FromJson<BuildIdentityEvidence>(
                File.ReadAllText(identityPath));
            string expectedSourcePath = ResolveProjectAsset(options.ProjectRoot,
                options.BuildIdentityAssetPath);
            if (identity == null || identity.identityVersion != 1 ||
                !IsBuildConfiguration(identity.engineWorkflow,
                    identity.il2cppCodeGeneration) ||
                !string.Equals(identity.state, "staged-for-final-player", StringComparison.Ordinal) ||
                !string.Equals((identity.stagedSourcePath ?? string.Empty).Replace('\\', '/'),
                    options.BuildIdentityAssetPath.Replace('\\', '/'),
                    StringComparison.OrdinalIgnoreCase) ||
                !IsSha256(identity.stagedSourceSha256) || !File.Exists(expectedSourcePath) ||
                !string.Equals(ToHex(Sha256(File.ReadAllBytes(expectedSourcePath))),
                    identity.stagedSourceSha256, StringComparison.OrdinalIgnoreCase) ||
                !TryValidateAotAssemblyInventory(identity.aotAssemblyNames,
                    identity.aotAssemblySetSha256, out _) ||
                !(identity.assemblies ?? Array.Empty<BuildIdentityAssembly>()).All(assembly =>
                    new HashSet<string>(identity.aotAssemblyNames,
                        StringComparer.OrdinalIgnoreCase).Contains(
                        NormalizeAssemblyName(assembly?.assemblyName))) ||
                !string.Equals(identity.engineWorkflow, options.EngineWorkflow,
                    StringComparison.Ordinal) ||
                !string.Equals(identity.il2cppCodeGeneration,
                    options.Il2CppCodeGeneration, StringComparison.Ordinal) ||
                !string.Equals(identity.il2cppCodeGeneration,
                    GetIl2CppCodeGeneration(ParseBuildTarget(options.Target)),
                    StringComparison.Ordinal) ||
                !string.Equals(identity.baseId, ComputeBaseId(identity.target,
                    identity.engineWorkflow, identity.il2cppCodeGeneration,
                    identity.managedAssemblySetSha256, identity.aotAssemblySetSha256,
                    identity.aotSnapshotSha256,
                    identity.baseMetaVersionSetSha256, identity.aotMetadataSetId,
                    identity.nativeGuardSourceSha256,
                    identity.nativeManifestSha256, identity.runtimeProtocol,
                    identity.runtimeContract, identity.runtimeCapabilities,
                    identity.runtimeAssetRoot, identity.baseMetaVersionAssetRoot),
                    StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException("DHE final Player has no valid staged BuildIdentity. " +
                    "Run the scripts-only DHE build stage again before BuildFinalPlayer.");

            string[] currentAotAssemblyNames = ReadAotAssemblyInventory(options.AotAssemblyRoot);
            if (!string.Equals(Sha256AssemblyNameSet(currentAotAssemblyNames),
                    identity.aotAssemblySetSha256, StringComparison.OrdinalIgnoreCase) ||
                !new HashSet<string>(currentAotAssemblyNames, StringComparer.OrdinalIgnoreCase)
                    .SetEquals(identity.aotAssemblyNames))
                throw new BuildFailedException(
                    "DHE stripped AOT assembly inventory changed after identity staging. " +
                    "Run the scripts-only DHE build stage again.");

            foreach (BuildIdentityAssembly assembly in identity.assemblies ??
                Array.Empty<BuildIdentityAssembly>())
            {
                if (string.IsNullOrWhiteSpace(assembly.embeddedBaseMetaVersionPath)) continue;
                string path = RequireFile(ResolveProjectRelativeEvidence(options.ProjectRoot,
                        assembly.embeddedBaseMetaVersionPath),
                    assembly.assemblyName + " embedded Base MetaVersion");
                if (!IsSha256(assembly.embeddedBaseMetaVersionSha256) ||
                    !string.Equals(ToHex(Sha256(File.ReadAllBytes(path))),
                        assembly.embeddedBaseMetaVersionSha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(assembly.baseMetaVersionSha256,
                        assembly.embeddedBaseMetaVersionSha256, StringComparison.OrdinalIgnoreCase))
                    throw new BuildFailedException("DHE embedded Base MetaVersion changed after identity " +
                        "staging for " + assembly.assemblyName + ". Run StageRuntimePlan and scripts-only again.");
            }
        }

        public static bool FinalNativeIdentityMatches(string outputRoot,
            DheNativeFinalizeResult nativeResult, out string error)
        {
            error = string.Empty;
            if (nativeResult?.GuardResult == null)
            {
                error = "The final native result is missing.";
                return false;
            }
            string identityPath = Path.Combine(Path.GetFullPath(outputRoot), "build-identity.json");
            if (!File.Exists(identityPath))
            {
                error = "The staged build identity is missing: " + identityPath;
                return false;
            }
            BuildIdentityEvidence identity = JsonUtility.FromJson<BuildIdentityEvidence>(
                File.ReadAllText(identityPath));
            DheNativeGuardResult guard = nativeResult.GuardResult;
            if (identity == null || !string.Equals(identity.nativeGuardSourceSha256,
                    guard.NativeGuardSourceSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(identity.nativeManifestSha256, guard.NativeManifestSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "Expected guard/manifest " + identity?.nativeGuardSourceSha256 + "/" +
                    identity?.nativeManifestSha256 + ", final " + guard.NativeGuardSourceSha256 +
                    "/" + guard.NativeManifestSha256 + ".";
                return false;
            }
            return true;
        }

        public static void ValidateFinalNativeIdentity(string outputRoot,
            DheNativeFinalizeResult nativeResult)
        {
            if (!FinalNativeIdentityMatches(outputRoot, nativeResult, out string error))
                throw new BuildFailedException(
                    "DHE final native identity did not converge after rebuild. " + error);
        }

        public static void RestoreBuildIdentityTemplate(DheProjectIdentityOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            string source = BuildIdentityTemplate(options);
            string sourcePath = ResolveProjectAsset(options.ProjectRoot,
                options.BuildIdentityAssetPath);
            if (File.Exists(sourcePath) && string.Equals(File.ReadAllText(sourcePath), source,
                StringComparison.Ordinal))
                return;
            File.WriteAllText(sourcePath, source, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(options.BuildIdentityAssetPath,
                ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        }

        private static string BuildIdentitySource(DheProjectIdentityOptions options,
            string baseId, string managedAssemblySetHash, string aotAssemblySetHash,
            string[] aotAssemblyNames, string snapshotHash,
            string baseMetaVersionSetHash, string aotMetadataSetId, DheNativeGuardResult guard,
            string[] runtimeCapabilities, string runtimeAssetRoot,
            string baseMetaVersionAssetRoot, string[] assemblyNames,
            string[] baseMetaVersionHashes)
        {
            string assemblyValues = string.Join(",\n",
                assemblyNames.Select(name => "            " + Quote(name)));
            string baseMetaVersionValues = string.Join(",\n",
                baseMetaVersionHashes.Select(hash => "            " + Quote(hash)));
            string capabilityValues = string.Join(",\n",
                runtimeCapabilities.Select(value => "            " + Quote(value)));
            string aotAssemblyValues = string.Join(",\n",
                aotAssemblyNames.Select(name => "            " + Quote(name)));
            return "namespace " + options.IdentityNamespace + "\n{\n" +
                "    internal static class " + options.IdentityClassName + "\n    {\n" +
                "        public const int IdentityVersion = 1;\n" +
                "        public const string Target = " + Quote(options.Target) + ";\n" +
                "        public const string EngineWorkflow = " +
                Quote(options.EngineWorkflow) + ";\n" +
                "        public const string Il2CppCodeGeneration = " +
                Quote(options.Il2CppCodeGeneration) + ";\n" +
                "        public const string AotSnapshotKind = \"" + AotSnapshotKind + "\";\n" +
                "        public const string BaseId = \"" + baseId + "\";\n" +
                "        public const string ManagedAssemblySetSha256 = \"" +
                managedAssemblySetHash + "\";\n" +
                "        public const string AotAssemblySetSha256 = \"" +
                aotAssemblySetHash + "\";\n" +
                "        public const string AotSnapshotSha256 = \"" + snapshotHash + "\";\n" +
                "        public const string NativeGuardSourceSha256 = \"" +
                guard.NativeGuardSourceSha256 + "\";\n" +
                "        public const string NativeManifestSha256 = \"" +
                guard.NativeManifestSha256 + "\";\n" +
                "        public const string BaseMetaVersionSetSha256 = \"" +
                baseMetaVersionSetHash + "\";\n" +
                "        public const string AotMetadataSetId = \"" +
                aotMetadataSetId + "\";\n" +
                "        public const string RuntimeProtocol = " + Quote(guard.RuntimeProtocol) + ";\n" +
                "        public const string RuntimeContract = " + Quote(guard.RuntimeContract) + ";\n" +
                "        public const string RuntimeAssetRoot = " + Quote(runtimeAssetRoot) + ";\n" +
                "        public const string BaseMetaVersionAssetRoot = " +
                Quote(baseMetaVersionAssetRoot) + ";\n" +
                "        public static readonly string[] RuntimeCapabilities =\n        {\n" +
                capabilityValues + "\n        };\n" +
                "        public static readonly string[] AotAssemblyNames =\n        {\n" +
                aotAssemblyValues + "\n        };\n" +
                "        public static readonly string[] AssemblyNames =\n        {\n" +
                assemblyValues + "\n        };\n" +
                "        public static readonly string[] BaseMetaVersionHashes =\n        {\n" +
                baseMetaVersionValues + "\n        };\n" +
                BuildIdentityFactorySource() +
                "    }\n}\n";
        }

        private static string BuildIdentityTemplate(DheProjectIdentityOptions options)
        {
            return "namespace " + options.IdentityNamespace + "\n{\n" +
                "    // Generated by HybridCLR DHE for the Player and restored after the build.\n" +
                "    internal static class " + options.IdentityClassName + "\n    {\n" +
                "        public const int IdentityVersion = 1;\n" +
                "        public const string Target = \"\";\n" +
                "        public const string EngineWorkflow = \"\";\n" +
                "        public const string Il2CppCodeGeneration = \"\";\n" +
                "        public const string AotSnapshotKind = \"uninitialized-template\";\n" +
                "        public const string BaseId = \"" + ZeroSha256 + "\";\n" +
                "        public const string ManagedAssemblySetSha256 = \"" + ZeroSha256 + "\";\n" +
                "        public const string AotAssemblySetSha256 = \"" + ZeroSha256 + "\";\n" +
                "        public const string AotSnapshotSha256 = \"" + ZeroSha256 + "\";\n" +
                "        public const string NativeGuardSourceSha256 = \"" + ZeroSha256 + "\";\n" +
                "        public const string NativeManifestSha256 = \"" + ZeroSha256 + "\";\n" +
                "        public const string BaseMetaVersionSetSha256 = \"" + ZeroSha256 + "\";\n" +
                "        public const string AotMetadataSetId = \"" + ZeroSha256 + "\";\n" +
                "        public const string RuntimeProtocol = \"\";\n" +
                "        public const string RuntimeContract = \"\";\n" +
                "        public const string RuntimeAssetRoot = \"\";\n" +
                "        public const string BaseMetaVersionAssetRoot = \"\";\n" +
                "        public static readonly string[] RuntimeCapabilities = new string[0];\n" +
                "        public static readonly string[] AotAssemblyNames = new string[0];\n" +
                "        public static readonly string[] AssemblyNames = new string[0];\n" +
                "        public static readonly string[] BaseMetaVersionHashes = new string[0];\n" +
                BuildIdentityFactorySource() +
                "    }\n}\n";
        }

        private static string BuildIdentityFactorySource()
        {
            return "        public static HybridCLR.DheRuntimeIdentity Create()\n" +
                "        {\n" +
                "            return new HybridCLR.DheRuntimeIdentity\n" +
                "            {\n" +
                "                IdentityVersion = IdentityVersion,\n" +
                "                Target = Target,\n" +
                "                EngineWorkflow = EngineWorkflow,\n" +
                "                Il2CppCodeGeneration = Il2CppCodeGeneration,\n" +
                "                AotSnapshotKind = AotSnapshotKind,\n" +
                "                BaseId = BaseId,\n" +
                "                ManagedAssemblySetSha256 = ManagedAssemblySetSha256,\n" +
                "                AotAssemblySetSha256 = AotAssemblySetSha256,\n" +
                "                AotSnapshotSha256 = AotSnapshotSha256,\n" +
                "                NativeGuardSourceSha256 = NativeGuardSourceSha256,\n" +
                "                NativeManifestSha256 = NativeManifestSha256,\n" +
                "                BaseMetaVersionSetSha256 = BaseMetaVersionSetSha256,\n" +
                "                AotMetadataSetId = AotMetadataSetId,\n" +
                "                RuntimeProtocol = RuntimeProtocol,\n" +
                "                RuntimeContract = RuntimeContract,\n" +
                "                RuntimeCapabilities = RuntimeCapabilities,\n" +
                "                AotAssemblyNames = AotAssemblyNames,\n" +
                "                RuntimeAssetRoot = RuntimeAssetRoot,\n" +
                "                BaseMetaVersionAssetRoot = BaseMetaVersionAssetRoot,\n" +
                "                AssemblyNames = AssemblyNames,\n" +
                "                BaseMetaVersionHashes = BaseMetaVersionHashes,\n" +
                "            };\n" +
                "        }\n";
        }

        private static string ResolveProjectAsset(string projectRoot, string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath) || Path.IsPathRooted(assetPath) ||
                !assetPath.Replace('\\', '/').StartsWith("Assets/", StringComparison.Ordinal))
                throw new BuildFailedException(
                    "DHE build identity path must be project-relative below Assets: " + assetPath);
            string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string resolved = Path.GetFullPath(Path.Combine(root,
                assetPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException("DHE build identity path escapes the project.");
            Directory.CreateDirectory(Path.GetDirectoryName(resolved));
            return resolved;
        }

        private static string ResolveProjectPath(string projectRoot, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new BuildFailedException("DHE project path is empty.");
            string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string resolved = Path.GetFullPath(Path.IsPathRooted(path) ? path :
                Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException("DHE project path escapes the project: " + path);
            return resolved;
        }

        private static string ResolveOptionalProjectPath(string projectRoot, string root,
            string fileName)
        {
            if (string.IsNullOrWhiteSpace(root)) return string.Empty;
            string resolvedRoot = Path.GetFullPath(Path.IsPathRooted(root) ? root :
                Path.Combine(projectRoot, root.Replace('/', Path.DirectorySeparatorChar)));
            string project = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolvedRoot.StartsWith(project, StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException("DHE Base MetaVersion asset root is outside the project.");
            return Path.Combine(resolvedRoot, fileName);
        }

        private static string ToProjectRelativePath(string projectRoot, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException("DHE identity asset is outside the project.");
            return full.Substring(root.Length).Replace('\\', '/');
        }

        private static string ResolveProjectRelativeEvidence(string projectRoot, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
                throw new BuildFailedException("DHE identity project path is invalid: " + relative);
            string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException("DHE identity project path escapes the project: " + relative);
            return full;
        }

        private static string RequireProjectChild(string projectRoot, string path,
            string description)
        {
            string resolvedRoot = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string resolvedPath = Path.GetFullPath(path ?? string.Empty);
            if (!resolvedPath.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException(description + " must be inside the project root: " +
                    resolvedPath);
            return resolvedPath;
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
            foreach (char c in value)
                if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        private static void ValidateBaseRuntimePlanMetadata(string projectRoot,
            string runtimePlanPath, BuildIdentityRuntimePlan runtimePlan,
            string aotAssemblyRoot)
        {
            if (!string.Equals(runtimePlan.selection, "embedded-base-metaversion",
                    StringComparison.Ordinal) ||
                (runtimePlan.aotMetadataSets != null && runtimePlan.aotMetadataSets.Length != 0) ||
                (runtimePlan.baseSelections != null && runtimePlan.baseSelections.Length != 0) ||
                runtimePlan.aotMetadata == null)
                throw new BuildFailedException(
                    "DHE Base runtime plan must use embedded-base metadata selection.");

            string runtimeAssetRoot = NormalizeAssetRoot(runtimePlan.runtimeAssetRoot,
                "runtime asset root");
            string planDirectory = Path.GetDirectoryName(Path.GetFullPath(runtimePlanPath));
            planDirectory = RequireProjectChild(projectRoot, planDirectory,
                "DHE runtime plan directory");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var records = new List<KeyValuePair<string, byte[]>>();
            foreach (BuildIdentityAotMetadata metadata in runtimePlan.aotMetadata)
            {
                string name = NormalizeAssemblyName(metadata?.assemblyName);
                string logicalPath = (metadata?.path ?? string.Empty).Replace('\\', '/');
                if (metadata == null || string.IsNullOrWhiteSpace(name) || !names.Add(name) ||
                    !IsSha256(metadata.sha256) || string.IsNullOrWhiteSpace(logicalPath) ||
                    !logicalPath.StartsWith(runtimeAssetRoot, StringComparison.OrdinalIgnoreCase) ||
                    logicalPath.Split('/').Any(segment => segment == "." || segment == ".."))
                    throw new BuildFailedException(
                        "DHE Base runtime plan contains an invalid AOT metadata record.");
                // The plan path is a logical catalog locator and may be
                // produced by YooAsset/Addressables. StageRuntimePlan writes
                // the physical metadata beside the plan, so hash that file
                // directly instead of assuming a StreamingAssets layout.
                string path = Path.Combine(planDirectory, name + ".bytes");
                byte[] bytes = File.ReadAllBytes(RequireFile(path,
                    name + " AOT metadata for build identity"));
                if (!string.Equals(ToHex(Sha256(bytes)), metadata.sha256,
                        StringComparison.OrdinalIgnoreCase))
                    throw new BuildFailedException(
                        "DHE Base runtime plan AOT metadata hash mismatch for " + name + ".");
                string strippedPath = RequireFile(Path.Combine(aotAssemblyRoot, name + ".dll"),
                    name + " stripped AOT metadata source");
                if (!string.Equals(ToHex(Sha256(File.ReadAllBytes(strippedPath))), metadata.sha256,
                        StringComparison.OrdinalIgnoreCase))
                    throw new BuildFailedException(
                        "DHE Base runtime plan AOT metadata is stale for " + name +
                        ". Run StageRuntimePlan again after regenerating stripped AOT assemblies.");
                records.Add(new KeyValuePair<string, byte[]>(name, bytes));
            }
            if (!string.Equals(Sha256NamedByteSet(records), runtimePlan.aotMetadataSetId,
                    StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException(
                    "DHE Base runtime plan AOT metadata set identity is invalid.");
        }

        private static string Sha256NamedByteSet(
            IEnumerable<KeyValuePair<string, byte[]>> records)
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
                    sha.TransformBlock(separator, 0, 1, separator, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return ToHex(sha.Hash);
            }
        }

        private static string Sha256AssemblyNameSet(IEnumerable<string> assemblyNames)
        {
            string canonical = string.Concat((assemblyNames ?? Array.Empty<string>())
                .Select(NormalizeAssemblyName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => name + "\n"));
            return ToHex(Sha256(Encoding.UTF8.GetBytes(canonical)));
        }

        private static string[] ReadAotAssemblyInventory(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
                throw new BuildFailedException(
                    "DHE stripped AOT assembly inventory root is not configured.");
            string fullRoot = Path.GetFullPath(root);
            if (!Directory.Exists(fullRoot))
                throw new DirectoryNotFoundException(
                    "DHE stripped AOT assembly inventory was not found: " + fullRoot);
            string[] names = Directory.GetFiles(fullRoot, "*.dll", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileNameWithoutExtension)
                .Select(NormalizeAssemblyName)
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();
            if (names.Length == 0 || names.Any(string.IsNullOrWhiteSpace) ||
                names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
                throw new BuildFailedException(
                    "DHE stripped AOT assembly inventory is empty or contains duplicate names.");
            return names;
        }

        private static bool TryValidateAotAssemblyInventory(string[] assemblyNames,
            string expectedHash, out string error)
        {
            error = string.Empty;
            string[] names = assemblyNames ?? Array.Empty<string>();
            if (names.Length == 0 || names.Any(string.IsNullOrWhiteSpace) ||
                names.Any(name => !string.Equals(name, NormalizeAssemblyName(name),
                    StringComparison.Ordinal)) ||
                names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length ||
                !IsSha256(expectedHash) ||
                !string.Equals(Sha256AssemblyNameSet(names), expectedHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "DHE AOT assembly inventory is missing, duplicated, or has an invalid hash.";
                return false;
            }
            return true;
        }

        private static byte[] Sha256(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(bytes ?? Array.Empty<byte>());
        }

        private static string ToHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes ?? Array.Empty<byte>()).Replace("-", string.Empty)
                .ToLowerInvariant();
        }

        private static BuildTarget ParseBuildTarget(string value)
        {
            if (Enum.TryParse(value, true, out BuildTarget target) &&
                target != BuildTarget.NoTarget)
                return target;
            throw new BuildFailedException("DHE build identity target is invalid: " + value);
        }

        private static string RequireBuildIdentityValue(string value, string description)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Any(character =>
                    !char.IsLetterOrDigit(character) && character != '.' && character != '_' &&
                    character != '-'))
                throw new BuildFailedException("DHE build identity " + description + " is invalid.");
            return value;
        }

        private static bool IsBuildConfiguration(string engineWorkflow,
            string il2cppCodeGeneration)
        {
            return string.Equals(engineWorkflow, "Unity2021Standard", StringComparison.Ordinal)
                    && string.Equals(il2cppCodeGeneration, "OptimizeSpeed", StringComparison.Ordinal) ||
                (string.Equals(engineWorkflow, "Unity2022Fgs", StringComparison.Ordinal) ||
                 string.Equals(engineWorkflow, "Tuanjie2022Fgs", StringComparison.Ordinal))
                    && string.Equals(il2cppCodeGeneration, "OptimizeSize", StringComparison.Ordinal);
        }

        private static string ComputeBaseId(string target, string engineWorkflow,
            string il2cppCodeGeneration, string managedAssemblySetSha256,
            string aotAssemblySetSha256, string aotSnapshotSha256,
            string baseMetaVersionSetSha256,
            string aotMetadataSetId,
            string nativeGuardSourceSha256, string nativeManifestSha256,
            string runtimeProtocol, string runtimeContract, IEnumerable<string> runtimeCapabilities,
            string runtimeAssetRoot, string baseMetaVersionAssetRoot)
        {
            string[] capabilities = (runtimeCapabilities ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal).OrderBy(value => value,
                    StringComparer.Ordinal).ToArray();
            string canonical = "hybridclr.dhe-base-identity-v1\n" +
                "target=" + (target ?? string.Empty) + "\n" +
                "engineWorkflow=" + (engineWorkflow ?? string.Empty) + "\n" +
                "il2cppCodeGeneration=" + (il2cppCodeGeneration ?? string.Empty) + "\n" +
                "managedAssemblySetSha256=" +
                (managedAssemblySetSha256 ?? string.Empty).ToLowerInvariant() + "\n" +
                "aotAssemblySetSha256=" +
                (aotAssemblySetSha256 ?? string.Empty).ToLowerInvariant() + "\n" +
                "aotSnapshotSha256=" +
                (aotSnapshotSha256 ?? string.Empty).ToLowerInvariant() + "\n" +
                "baseMetaVersionSetSha256=" +
                (baseMetaVersionSetSha256 ?? string.Empty).ToLowerInvariant() + "\n" +
                "aotMetadataSetId=" +
                (aotMetadataSetId ?? string.Empty).ToLowerInvariant() + "\n" +
                "nativeGuardSourceSha256=" +
                (nativeGuardSourceSha256 ?? string.Empty).ToLowerInvariant() + "\n" +
                "nativeManifestSha256=" +
                (nativeManifestSha256 ?? string.Empty).ToLowerInvariant() + "\n" +
                "runtimeProtocol=" + (runtimeProtocol ?? string.Empty) + "\n" +
                "runtimeContract=" + (runtimeContract ?? string.Empty) + "\n" +
                "runtimeCapabilities=" + string.Join(",", capabilities) + "\n" +
                "runtimeAssetRoot=" + (runtimeAssetRoot ?? string.Empty) + "\n" +
                "baseMetaVersionAssetRoot=" + (baseMetaVersionAssetRoot ?? string.Empty) + "\n";
            return ToHex(Sha256(Encoding.UTF8.GetBytes(canonical)));
        }

        private static string NormalizeAssetRoot(string value, string description)
        {
            string normalized = (value ?? string.Empty).Replace('\\', '/').TrimEnd('/') + "/";
            if (normalized == "/" || normalized.StartsWith("/", StringComparison.Ordinal) ||
                Path.IsPathRooted(normalized) ||
                normalized.Split('/').Any(segment => segment == "." || segment == ".."))
                throw new BuildFailedException("DHE " + description +
                    " must be a portable runtime-relative directory: " + value);
            return normalized;
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        }

        private static string NormalizeAssemblyName(string name)
        {
            string trimmed = (name ?? string.Empty).Trim();
            return trimmed.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(trimmed) : trimmed;
        }

        private static string RequireFile(string path, string description)
        {
            string full = Path.GetFullPath(path ?? string.Empty);
            if (!File.Exists(full)) throw new FileNotFoundException(
                "DHE " + description + " was not found", full);
            return full;
        }

        private static void WriteJson(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(value, true), new UTF8Encoding(false));
        }

        [Serializable]
        private sealed class NativeGuardsEvidence
        {
            public int schemaVersion;
            public string format;
            public string generatedAtUtc;
            public bool passed;
            public string target;
            public string generatedCppRoot;
            public string[] generatedCppPaths;
            public string manifestPath;
            public string nativeGuardSourceSha256;
            public string nativeManifestSha256;
            public int requestedMethodCount;
            public int transformedMethodCount;
            public int nativeEntryCount;
            public int unsupportedMethodCount;
            public string guardMode;
            public int guardedMethodCount;
            public int supportedGuardedMethodCount;
            public int unsupportedGuardedMethodCount;
            public int interpreterOnlyMethodCount;
            public int unsupportedChangedMethodCount;
        }

        [Serializable]
        private sealed class NativeFinalizeEvidence
        {
            public int schemaVersion;
            public string format;
            public string generatedAtUtc;
            public bool passed;
            public string target;
            public string generatedCppRoot;
            public string manifestPath;
            public string nativeGuardSourceSha256;
            public string nativeManifestSha256;
            public string beeBackendPath;
            public string dagPath;
            public string logPath;
            public int attempts;
            public int exitCode;
            public int graphRegenerations;
            public int guardReapplications;
            public string buildProgramPath;
            public string playerArtifactKind;
            public string playerArtifactPath;
            public string playerArtifactSha256;
            public string playerArtifactGradleRoot;
            public string playerArtifactBuildToolPath;
            public string playerArtifactBuildProgramPath;
            public string playerArtifactBuildTask;
            public string playerArtifactBuildLogPath;
            public int playerArtifactExitCode;
            public string[] playerArtifactNativeLibraryEntries;
            public string[] playerArtifactNativeLibrarySourcePaths;
            public string[] playerArtifactNativeLibrarySha256;
        }

        [Serializable]
        private sealed class BuildIdentityEvidence
        {
            public int schemaVersion;
            public string format;
            public string workflow;
            public string target;
            public string engineWorkflow;
            public string il2cppCodeGeneration;
            public int identityVersion;
            public string state;
            public string pathSemantics;
            public string stagedSourcePath;
            public string stagedSourceSha256;
            public string baseId;
            public string managedAssemblySetSha256;
            public string aotAssemblySetSha256;
            public string[] aotAssemblyNames;
            public string aotSnapshotSha256;
            public string aotSnapshotKind;
            public string nativeGuardSourceSha256;
            public string nativeManifestSha256;
            public string baseMetaVersionSetSha256;
            public string aotMetadataSetId;
            public string runtimeProtocol;
            public string runtimeContract;
            public string[] runtimeCapabilities;
            public string runtimeAssetRoot;
            public string baseMetaVersionAssetRoot;
            public string generatedCppRoot;
            public string[] generatedCppPaths;
            public string nativeManifestPath;
            public BuildIdentityAssembly[] assemblies;
        }

        [Serializable]
        private sealed class BuildIdentityAssembly
        {
            public string assemblyName;
            public string baselinePath;
            public string baselineSha256;
            public string snapshotSha256;
            public string baseMetaVersionPath;
            public string baseMetaVersionSha256;
            public string embeddedBaseMetaVersionPath;
            public string embeddedBaseMetaVersionSha256;
        }

        [Serializable]
        private sealed class BuildIdentityProjectPlan
        {
            public BuildIdentityProjectAssembly[] assemblies;
        }

        [Serializable]
        private sealed class BuildIdentityProjectAssembly
        {
            public string assemblyName;
            public string baseMetaVersionBytes;
        }

        [Serializable]
        private sealed class BuildIdentityRuntimePlan
        {
            public int schemaVersion;
            public string format;
            public string runtimeAssetRoot;
            public string baseMetaVersionAssetRoot;
            public string selection;
            public string aotMetadataSetId;
            public BuildIdentityAotMetadata[] aotMetadata;
            public BuildIdentityAotMetadataSet[] aotMetadataSets;
            public BuildIdentityBaseSelection[] baseSelections;
        }

        [Serializable]
        private sealed class BuildIdentityAotMetadata
        {
            public string assemblyName;
            public string sha256;
            public string path;
        }

        [Serializable]
        private sealed class BuildIdentityAotMetadataSet
        {
            public string aotMetadataSetId;
            public BuildIdentityAotMetadata[] assemblies;
        }

        [Serializable]
        private sealed class BuildIdentityBaseSelection
        {
            public string baseId;
            public string aotMetadataSetId;
        }
    }

    public sealed class DheProjectNativeOptions
    {
        public string ProjectRoot;
        public string ProjectPlanPath;
        public string OutputRoot;
        public string Target;
        public int BeeMaxAttempts = 8;
        public int BeeTimeoutSeconds = 600;
        public bool GuardAllMethods;
    }

    public sealed class DheProjectIdentityOptions
    {
        public string ProjectRoot;
        public string OutputRoot;
        public string BaselineRoot;
        public string AotAssemblyRoot;
        public string ProjectPlanPath;
        public string Target;
        public string EngineWorkflow;
        public string Il2CppCodeGeneration;
        public string Workflow = "dhe-opt4";
        public string BuildIdentityAssetPath;
        public string RuntimePlanPath;
        public string BaseMetaVersionAssetRoot;
        public string IdentityNamespace;
        public string IdentityClassName = "DheBuildIdentity";
    }
}
