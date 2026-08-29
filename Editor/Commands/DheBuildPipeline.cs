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
            if (plan == null || plan.assemblies == null || plan.assemblies.Length == 0)
            {
                throw new BuildFailedException("DHE project plan is empty: " + planPath);
            }

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
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DheProjectAssembly assembly in plan.assemblies)
            {
                string assemblyName = NormalizeAssemblyName(assembly == null ? null : assembly.assemblyName);
                if (string.IsNullOrWhiteSpace(assemblyName) || !names.Add(assemblyName))
                {
                    throw new BuildFailedException("DHE project plan contains an empty or duplicate assembly.");
                }

                string currentPath = RequireFile(assembly.current, assemblyName + " current assembly");
                string baselinePath = RequireFile(assembly.baseline, assemblyName + " baseline assembly");
                string mvPath = RequireFile(assembly.mvBytes, assemblyName + " MV binary");
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

            string[] loadList = names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
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

            string[] aotAssemblies = options.AotMetadataAssemblyNames ?? Array.Empty<string>();
            string strippedAotRoot = aotAssemblies.Length == 0
                ? string.Empty : RequireDirectory(options.StrippedAotRoot, "DHE stripped AOT root");
            foreach (string name in NormalizeAssemblyNames(aotAssemblies))
            {
                string source = RequireFile(Path.Combine(strippedAotRoot,
                    name + DllExtension), name + " stripped AOT metadata");
                File.Copy(source, Path.Combine(currentAssetRoot, name + ".bytes"), true);
            }
            File.WriteAllText(Path.Combine(currentAssetRoot, "AotFileList.txt"),
                SerializeStringArray(NormalizeAssemblyNames(aotAssemblies)),
                System.Text.Encoding.UTF8);

            DheRuntimePlanDocument runtimePlan = new DheRuntimePlanDocument
            {
                schemaVersion = 1,
                format = "hybridclr.dhe-runtime-plan.json",
                assemblies = runtimeRecords.ToArray(),
            };
            File.WriteAllText(Path.Combine(currentAssetRoot, "DheRuntimePlan.json"),
                JsonUtility.ToJson(runtimePlan, true), System.Text.Encoding.UTF8);
            DheRuntimePlanHandoffDocument handoffPlan = new DheRuntimePlanHandoffDocument
            {
                schemaVersion = 1,
                format = "hybridclr.dhe-runtime-plan.json",
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
                BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = options.Scenes,
                    locationPathName = outputPath,
                    target = options.Target,
                    targetGroup = group,
                    options = options.BuildOptions,
                });
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
            foreach (string pattern in new[] { "*.dll.bytes", "*.mv.bytes", "*.aot-snapshot.bytes" })
            {
                foreach (string path in Directory.GetFiles(root, pattern, SearchOption.TopDirectoryOnly))
                {
                    File.Delete(path);
                }
            }
            foreach (string fileName in new[] { "DheRuntimePlan.json", "HotfixFileList.txt",
                "HotfixAotDependencyMap.txt", "AotFileList.txt" })
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
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string NormalizeAssemblyName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            string trimmed = name.Trim();
            return trimmed.EndsWith(DllExtension, StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(trimmed) : trimmed;
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
            public DheProjectAssembly[] assemblies;
        }

        [Serializable]
        public sealed class DheProjectAssembly
        {
            public string assemblyName;
            public string current;
            public string baseline;
            public string mvBytes;
        }

        [Serializable]
        private sealed class DheRuntimePlanDocument
        {
            public int schemaVersion;
            public string format;
            public DheRuntimePlanAssembly[] assemblies;
        }

        [Serializable]
        private sealed class DheRuntimePlanHandoffDocument
        {
            public int schemaVersion;
            public string format;
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
        public string[] AotMetadataAssemblyNames;
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
    }
}
