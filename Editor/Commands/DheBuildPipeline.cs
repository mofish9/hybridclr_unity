using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
        private const string DllExtension = ".dll";
        private const string DllBytesExtension = ".dll.bytes";

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
            string root = Path.GetFullPath(runtimeAssetRoot ?? string.Empty);
            if (!Directory.Exists(root))
            {
                return;
            }
            ClearDhePayloadFiles(root);
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

        public static DheRuntimePlanResult StageRuntimePlan(DheRuntimePlanOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            string projectRoot = RequireDirectory(options.ProjectRoot, "DHE project root");
            string planPath = RequireFile(options.ProjectPlanPath, "DHE project plan");
            string assetRoot = ResolveProjectPath(projectRoot, options.RuntimeAssetRoot);
            string outputRoot = ResolveProjectPath(projectRoot, options.OutputRoot);
            string currentAssetRoot = RequireProjectChild(projectRoot, assetRoot,
                "DHE runtime asset root");
            Directory.CreateDirectory(currentAssetRoot);
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

            // Remove only DHE payloads from the previous plan. Legacy hotfix
            // bytes in the same directory are owned by the project adapter and
            // must survive a mixed DHE/legacy staging pass.
            ClearRuntimeAssets(currentAssetRoot);
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
                string mvPath = RequireFile(ResolvePlanReference(planDirectory, assembly.mvBytes),
                    assemblyName + " MV binary");
                byte[] currentBytes = File.ReadAllBytes(currentPath);
                if (options.CurrentAssemblyTransform != null)
                {
                    currentBytes = options.CurrentAssemblyTransform(assemblyName, currentBytes) ??
                        throw new BuildFailedException("DHE current assembly transform returned null: " + assemblyName);
                }

                string dllFileName = assemblyName + DllBytesExtension;
                string mvFileName = assemblyName + ".mv.bytes";
                string snapshotFileName = assemblyName + ".aot-snapshot.bytes";
                File.WriteAllBytes(Path.Combine(currentAssetRoot, dllFileName), currentBytes);
                File.Copy(mvPath, Path.Combine(currentAssetRoot, mvFileName), true);
                byte[] baselineBytes = File.ReadAllBytes(baselinePath);
                File.WriteAllBytes(Path.Combine(currentAssetRoot, snapshotFileName), Sha256(baselineBytes));

                runtimeRecords.Add(new DheRuntimePlanAssembly
                {
                    assemblyName = assemblyName,
                    current = ToProjectAssetPath(projectRoot, Path.Combine(currentAssetRoot, dllFileName)),
                    mv = ToProjectAssetPath(projectRoot, Path.Combine(currentAssetRoot, mvFileName)),
                    snapshot = ToProjectAssetPath(projectRoot, Path.Combine(currentAssetRoot, snapshotFileName)),
                });

                string handoffCurrent = assemblyName + ".current.dll";
                string handoffBaseline = assemblyName + ".baseline.dll";
                string handoffMv = assemblyName + ".mv.bytes";
                string handoffSnapshot = assemblyName + ".snapshot.bytes";
                File.Copy(currentPath, Path.Combine(handoffRoot, handoffCurrent), true);
                File.Copy(baselinePath, Path.Combine(handoffRoot, handoffBaseline), true);
                File.Copy(mvPath, Path.Combine(handoffRoot, handoffMv), true);
                File.Copy(Path.Combine(currentAssetRoot, snapshotFileName),
                    Path.Combine(handoffRoot, handoffSnapshot), true);
                handoffRecords.Add(new DheRuntimePlanHandoffAssembly
                {
                    assemblyName = assemblyName,
                    current = handoffCurrent,
                    baseline = handoffBaseline,
                    mv = handoffMv,
                    snapshot = handoffSnapshot,
                    baselineSha256 = Sha256Hex(baselineBytes),
                    currentSha256 = Sha256Hex(File.ReadAllBytes(currentPath)),
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
                File.Copy(source, Path.Combine(currentAssetRoot, name + ".bytes"), true);
                aotMetadata.Add(new DheRuntimePlanAotMetadata
                {
                    assemblyName = name,
                    sourceKind = sourceKind,
                    sha256 = Sha256Hex(File.ReadAllBytes(source)),
                    manifestSha256 = fallbackManifest == null ? string.Empty : fallbackManifestSha256,
                    path = ToProjectAssetPath(projectRoot,
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
                aotMetadata = aotMetadata.ToArray(),
                aotMetadataManifestSha256 = fallbackManifest == null ? string.Empty : fallbackManifestSha256,
                assemblies = runtimeRecords.ToArray(),
            };
            File.WriteAllText(Path.Combine(currentAssetRoot, "DheRuntimePlan.json"),
                JsonUtility.ToJson(runtimePlan, true), System.Text.Encoding.UTF8);
            DheRuntimePlanHandoffDocument handoffPlan = new DheRuntimePlanHandoffDocument
            {
                schemaVersion = 1,
                format = "hybridclr.dhe-runtime-handoff-plan.json",
                aotMetadataManifestSha256 = fallbackManifest == null ? string.Empty : fallbackManifestSha256,
                aotMetadata = handoffAotMetadata.ToArray(),
                assemblies = handoffRecords.ToArray(),
            };
            string handoffPlanPath = Path.Combine(handoffRoot, "dhe-runtime-plan.json");
            File.WriteAllText(handoffPlanPath, JsonUtility.ToJson(handoffPlan, true),
                System.Text.Encoding.UTF8);
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

            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(options.Target);
            if (EditorUserBuildSettings.activeBuildTarget != options.Target &&
                !EditorUserBuildSettings.SwitchActiveBuildTarget(group, options.Target))
            {
                throw new BuildFailedException("Unable to switch active build target to " + options.Target);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            string previousBaseline = Environment.GetEnvironmentVariable(BaselineEnvironmentVariable);
            Environment.SetEnvironmentVariable(BaselineEnvironmentVariable, baselineRoot);
            try
            {
                BuildPlayerOptions buildOptions = new BuildPlayerOptions
                {
                    scenes = options.Scenes,
                    locationPathName = outputPath,
                    target = options.Target,
                    targetGroup = group,
                    options = options.BuildOptions,
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
                return report;
            }
            finally
            {
                Environment.SetEnvironmentVariable(BaselineEnvironmentVariable, previousBaseline);
            }
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
            foreach (string path in Directory.GetFiles(root, "*.aot-snapshot.bytes",
                SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(path);
                generatedAssemblies.Add(fileName.Substring(0,
                    fileName.Length - ".aot-snapshot.bytes".Length));
            }

            foreach (string name in generatedAssemblies)
            {
                foreach (string suffix in new[] { ".dll.bytes", ".mv.bytes", ".aot-snapshot.bytes" })
                {
                    string path = Path.Combine(root, name + suffix);
                    if (File.Exists(path)) File.Delete(path);
                }
            }
            foreach (string fileName in new[] { "DheRuntimePlan.json", "DheSmokeConfig.json" })
            {
                string path = Path.Combine(root, fileName);
                if (File.Exists(path)) File.Delete(path);
            }
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

        private static string ToProjectAssetPath(string projectRoot, string path)
        {
            path = RequireProjectChild(projectRoot, path, "DHE runtime asset path");
            string relative = path.Substring(projectRoot.TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
            return relative.Replace(Path.AltDirectorySeparatorChar, '/');
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
                ? EditorUserBuildSettings.activeBuildTarget.ToString()
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
            public string mvBytes;
        }

        [Serializable]
        private sealed class DheRuntimePlanDocument
        {
            public int schemaVersion;
            public string format;
            public DheRuntimePlanAotMetadata[] aotMetadata;
            public string aotMetadataManifestSha256;
            public DheRuntimePlanAssembly[] assemblies;
        }

        [Serializable]
        private sealed class DheRuntimePlanHandoffDocument
        {
            public int schemaVersion;
            public string format;
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
            public string mv;
            public string snapshot;
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
            public string mv;
            public string snapshot;
            public string baselineSha256;
            public string currentSha256;
        }
    }

    public sealed class DheRuntimePlanOptions
    {
        public string ProjectRoot;
        public string ProjectPlanPath;
        public string RuntimeAssetRoot;
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
        public string[] Scenes;
        /// <summary>
        /// Optional project-owned build wrapper. The callback receives the
        /// fully populated Unity options after the package has bound the
        /// previous stripped-AOT baseline in the process environment.
        /// </summary>
        public Func<BuildPlayerOptions, BuildReport> BuildPlayerCallback;
    }
}
