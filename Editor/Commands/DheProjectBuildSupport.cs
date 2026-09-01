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
            bool guardPassed = guard.UnsupportedMethodCount == 0 &&
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
            });
            if (!guardPassed)
                throw new BuildFailedException("DHE native guard coverage is incomplete.");
            if (!final) return;

            DheBeeRebuildResult rebuild = nativeResult.BeeRebuildResult;
            bool rebuildPassed = rebuild != null && rebuild.ExitCode == 0;
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

            string baselineRoot = Path.GetFullPath(options.BaselineRoot);
            var baselineRecords = new List<KeyValuePair<string, byte[]>>();
            var snapshotRecords = new List<KeyValuePair<string, byte[]>>();
            var snapshotHashes = new List<string>();
            var assemblyEvidence = new List<BuildIdentityAssembly>();
            foreach (string assemblyName in assemblyNames)
            {
                string baselinePath = RequireFile(Path.Combine(baselineRoot, assemblyName + ".dll"),
                    assemblyName + " baseline assembly for build identity");
                byte[] baselineBytes = File.ReadAllBytes(baselinePath);
                byte[] snapshotBytes = Sha256(baselineBytes);
                string snapshotHash = ToHex(snapshotBytes);
                baselineRecords.Add(new KeyValuePair<string, byte[]>(assemblyName, baselineBytes));
                snapshotRecords.Add(new KeyValuePair<string, byte[]>(assemblyName, snapshotBytes));
                snapshotHashes.Add(snapshotHash);
                assemblyEvidence.Add(new BuildIdentityAssembly
                {
                    assemblyName = assemblyName,
                    baselinePath = baselinePath,
                    baselineSha256 = snapshotHash,
                    snapshotSha256 = snapshotHash,
                });
            }

            string baselineSetHash = Sha256NamedByteSet(baselineRecords);
            string snapshotSetHash = Sha256NamedByteSet(snapshotRecords);
            DheNativeGuardResult guard = nativeResult.GuardResult;
            string sourcePath = ResolveProjectAsset(options.ProjectRoot,
                options.BuildIdentityAssetPath);
            File.WriteAllText(sourcePath, BuildIdentitySource(options, baselineSetHash,
                snapshotSetHash, guard, assemblyNames, snapshotHashes.ToArray()),
                new UTF8Encoding(false));

            string identityPath = Path.Combine(Path.GetFullPath(options.OutputRoot),
                "build-identity.json");
            WriteJson(identityPath, new BuildIdentityEvidence
            {
                schemaVersion = 1,
                format = "hybridclr.dhe-build-identity.json",
                workflow = options.Workflow,
                target = options.Target,
                identityVersion = 2,
                pathSemantics = "workspace-absolute-v1",
                baselineAssemblySha256 = baselineSetHash,
                aotSnapshotSha256 = snapshotSetHash,
                aotSnapshotKind = AotSnapshotKind,
                nativeGuardSourceSha256 = guard.NativeGuardSourceSha256,
                nativeManifestSha256 = guard.NativeManifestSha256,
                generatedCppRoot = nativeResult.GeneratedCppRoot,
                generatedCppPaths = guard.GeneratedCppPaths ?? Array.Empty<string>(),
                nativeManifestPath = guard.ManifestPath,
                assemblies = assemblyEvidence.ToArray(),
            });
            AssetDatabase.ImportAsset(options.BuildIdentityAssetPath,
                ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
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
            string baselineHash, string snapshotHash, DheNativeGuardResult guard,
            string[] assemblyNames, string[] snapshotHashes)
        {
            string assemblyValues = string.Join(",\n",
                assemblyNames.Select(name => "            " + Quote(name)));
            string snapshotValues = string.Join(",\n",
                snapshotHashes.Select(hash => "            " + Quote(hash)));
            return "namespace " + options.IdentityNamespace + "\n{\n" +
                "    internal static class " + options.IdentityClassName + "\n    {\n" +
                "        public const int IdentityVersion = 2;\n" +
                "        public const string Target = " + Quote(options.Target) + ";\n" +
                "        public const string AotSnapshotKind = \"" + AotSnapshotKind + "\";\n" +
                "        public const string BaselineAssemblySha256 = \"" + baselineHash + "\";\n" +
                "        public const string AotSnapshotSha256 = \"" + snapshotHash + "\";\n" +
                "        public const string NativeGuardSourceSha256 = \"" +
                guard.NativeGuardSourceSha256 + "\";\n" +
                "        public const string NativeManifestSha256 = \"" +
                guard.NativeManifestSha256 + "\";\n" +
                "        public static readonly string[] AssemblyNames =\n        {\n" +
                assemblyValues + "\n        };\n" +
                "        public static readonly string[] SnapshotHashes =\n        {\n" +
                snapshotValues + "\n        };\n" +
                BuildIdentityFactorySource() +
                "    }\n}\n";
        }

        private static string BuildIdentityTemplate(DheProjectIdentityOptions options)
        {
            return "namespace " + options.IdentityNamespace + "\n{\n" +
                "    // Generated by HybridCLR DHE for the Player and restored after the build.\n" +
                "    internal static class " + options.IdentityClassName + "\n    {\n" +
                "        public const int IdentityVersion = 2;\n" +
                "        public const string Target = \"\";\n" +
                "        public const string AotSnapshotKind = \"uninitialized-template\";\n" +
                "        public const string BaselineAssemblySha256 = \"" + ZeroSha256 + "\";\n" +
                "        public const string AotSnapshotSha256 = \"" + ZeroSha256 + "\";\n" +
                "        public const string NativeGuardSourceSha256 = \"" + ZeroSha256 + "\";\n" +
                "        public const string NativeManifestSha256 = \"" + ZeroSha256 + "\";\n" +
                "        public static readonly string[] AssemblyNames = new string[0];\n" +
                "        public static readonly string[] SnapshotHashes = new string[0];\n" +
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
                "                AotSnapshotKind = AotSnapshotKind,\n" +
                "                BaselineAssemblySha256 = BaselineAssemblySha256,\n" +
                "                AotSnapshotSha256 = AotSnapshotSha256,\n" +
                "                NativeGuardSourceSha256 = NativeGuardSourceSha256,\n" +
                "                NativeManifestSha256 = NativeManifestSha256,\n" +
                "                AssemblyNames = AssemblyNames,\n" +
                "                SnapshotHashes = SnapshotHashes,\n" +
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

        private static string Sha256NamedByteSet(
            IEnumerable<KeyValuePair<string, byte[]>> records)
        {
            using (SHA256 sha = SHA256.Create())
            {
                foreach (KeyValuePair<string, byte[]> record in records)
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

        private static byte[] Sha256(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(bytes ?? Array.Empty<byte>());
        }

        private static string ToHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes ?? Array.Empty<byte>()).Replace("-", string.Empty)
                .ToLowerInvariant();
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
        }

        [Serializable]
        private sealed class BuildIdentityEvidence
        {
            public int schemaVersion;
            public string format;
            public string workflow;
            public string target;
            public int identityVersion;
            public string pathSemantics;
            public string baselineAssemblySha256;
            public string aotSnapshotSha256;
            public string aotSnapshotKind;
            public string nativeGuardSourceSha256;
            public string nativeManifestSha256;
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
        }
    }

    public sealed class DheProjectNativeOptions
    {
        public string ProjectRoot;
        public string ProjectPlanPath;
        public string OutputRoot;
        public string Target;
        public int BeeMaxAttempts = 3;
        public int BeeTimeoutSeconds = 600;
    }

    public sealed class DheProjectIdentityOptions
    {
        public string ProjectRoot;
        public string OutputRoot;
        public string BaselineRoot;
        public string Target;
        public string Workflow = "dhe-opt4";
        public string BuildIdentityAssetPath;
        public string IdentityNamespace;
        public string IdentityClassName = "DheBuildIdentity";
    }
}
