using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor.Build;
using UnityEngine;

namespace HybridCLR.Editor.Commands
{
    /// <summary>
    /// Validates that a project's resource build contains every file required
    /// by the package-owned DHE runtime plan.
    /// </summary>
    public static class DheProjectResourceSupport
    {
        public static DheProjectResourceEvidenceResult ValidateAndWrite(
            DheProjectResourceEvidenceOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            string runtimePlanPath = RequireFile(options.RuntimePlanPath,
                "runtime asset plan");
            string aotListPath = RequireFile(options.AotAssemblyListPath,
                "AOT metadata list");
            string packageDirectory = RequireDirectory(options.PackageDirectory,
                "resource package directory");
            string packageReport = RequireFile(options.PackageReportPath,
                "resource package report");
            string outputRoot = Path.GetFullPath(options.OutputRoot ?? string.Empty);
            Directory.CreateDirectory(outputRoot);

            RuntimePlan plan = JsonUtility.FromJson<RuntimePlan>(
                File.ReadAllText(runtimePlanPath));
            if (plan == null || plan.schemaVersion != 1 ||
                !string.Equals(plan.format, "hybridclr.dhe-runtime-asset-plan.json",
                StringComparison.Ordinal) ||
                ((plan.assemblies == null || plan.assemblies.Length == 0) &&
                 (plan.payloadVariants == null || plan.payloadVariants.Length == 0)))
                throw new BuildFailedException(
                    "DHE runtime asset plan is invalid: " + runtimePlanPath);

            var required = CollectRequiredAssets(options, plan, aotListPath);
            var assets = (options.Assets ?? Array.Empty<DheProjectResourceAsset>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.AssetPath))
                .ToDictionary(item => NormalizeAssetPath(item.AssetPath),
                    StringComparer.OrdinalIgnoreCase);
            var bundles = (options.Bundles ?? Array.Empty<DheProjectResourceBundle>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.BundleName))
                .ToDictionary(item => item.BundleName, StringComparer.OrdinalIgnoreCase);
            var records = new List<RequiredAssetEvidence>();
            foreach (RequiredAssetSpec spec in required)
            {
                if (!assets.TryGetValue(spec.AssetPath, out DheProjectResourceAsset asset))
                    throw new BuildFailedException(
                        "Resource report does not contain DHE asset: " + spec.AssetPath);
                string bundleName = asset.MainBundleName;
                if (string.IsNullOrWhiteSpace(bundleName))
                {
                    bundleName = bundles.Values.FirstOrDefault(bundle =>
                        (bundle.AssetPaths ?? Array.Empty<string>()).Any(path =>
                            string.Equals(NormalizeAssetPath(path), spec.AssetPath,
                                StringComparison.OrdinalIgnoreCase)))?.BundleName;
                }
                if (string.IsNullOrWhiteSpace(bundleName) ||
                    !bundles.TryGetValue(bundleName, out DheProjectResourceBundle bundle))
                    throw new BuildFailedException(
                        "Resource report has no bundle for DHE asset: " + spec.AssetPath);
                string bundlePath = ResolvePackageFile(packageDirectory, bundle.FileName,
                    "resource bundle for " + spec.AssetPath);
                FileInfo file = new FileInfo(RequireFile(bundlePath,
                    "resource bundle for " + spec.AssetPath));
                if (bundle.FileSize != file.Length)
                    throw new BuildFailedException(string.Format(
                        "Resource bundle size mismatch for {0}: report={1}, actual={2}",
                        spec.AssetPath, bundle.FileSize, file.Length));
                string actualHash = ComputeHash(bundlePath, options.BundleHashAlgorithm);
                if (!string.IsNullOrWhiteSpace(bundle.FileHash) &&
                    !string.Equals(actualHash, bundle.FileHash,
                        StringComparison.OrdinalIgnoreCase))
                    throw new BuildFailedException(
                        "Resource bundle hash mismatch for DHE asset: " + spec.AssetPath);
                records.Add(new RequiredAssetEvidence
                {
                    assetPath = spec.AssetPath,
                    assetKind = spec.AssetKind,
                    assemblyName = spec.AssemblyName,
                    present = true,
                    bundleName = bundle.BundleName,
                    bundleFileName = bundle.FileName,
                    bundleFileHash = bundle.FileHash,
                    bundleSha256 = Sha256File(bundlePath),
                    bundleSize = file.Length,
                });
            }

            string buildPath = Path.Combine(outputRoot,
                string.IsNullOrWhiteSpace(options.BuildEvidenceFileName)
                    ? "dhe-resource-build.json" : options.BuildEvidenceFileName);
            WriteJson(buildPath, new ResourceBuildEvidence
            {
                schemaVersion = 1,
                format = options.BuildEvidenceFormat,
                target = options.Target,
                packageName = options.PackageName,
                packageVersion = options.PackageVersion,
                packageDirectory = packageDirectory,
                buildReport = packageReport,
                buildReportSha256 = Sha256File(packageReport),
                assetCount = options.Assets?.Length ?? 0,
                bundleCount = options.Bundles?.Length ?? 0,
                requiredAssets = records.ToArray(),
                passed = true,
            });
            string adapterRoot = Path.Combine(outputRoot, "adapter");
            Directory.CreateDirectory(adapterRoot);
            string evidencePath = Path.Combine(adapterRoot, "resource-evidence.json");
            WriteJson(evidencePath, new ResourceEvidence
            {
                schemaVersion = 1,
                format = "hybridclr.dhe-resource-evidence.json",
                generatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                passed = true,
                policy = "required",
                strategy = options.Strategy,
                target = options.Target,
                pathSemantics = "workspace-absolute-v1",
                resourceBuild = Path.GetFullPath(buildPath),
                requiredAssetCount = records.Count,
            });
            return new DheProjectResourceEvidenceResult
            {
                BuildEvidencePath = buildPath,
                ResourceEvidencePath = evidencePath,
                RequiredAssetCount = records.Count,
            };
        }

        private static List<RequiredAssetSpec> CollectRequiredAssets(
            DheProjectResourceEvidenceOptions options, RuntimePlan plan, string aotListPath)
        {
            string prefix = NormalizeAssetPath(options.RuntimeAssetPrefix).TrimEnd('/') + "/";
            if (prefix == "/" || prefix.Contains("../", StringComparison.Ordinal))
                throw new BuildFailedException("DHE runtime asset prefix is invalid.");
            var result = new List<RequiredAssetSpec>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var metadataHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Action<string, string, string> add = (path, kind, assembly) =>
            {
                string normalized = NormalizeAssetPath(path);
                if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    normalized.Contains("../", StringComparison.Ordinal) ||
                    !paths.Add(normalized))
                    throw new BuildFailedException(
                        "DHE runtime asset path is unsafe or duplicated: " + normalized);
                result.Add(new RequiredAssetSpec
                {
                    AssetPath = normalized,
                    AssetKind = kind,
                    AssemblyName = string.IsNullOrWhiteSpace(assembly) ? null :
                        NormalizeAssemblyName(assembly),
                });
            };
            foreach (string control in options.ControlFileNames ?? Array.Empty<string>())
                add(prefix + control, "control", null);
            IEnumerable<RuntimeAssembly> plannedAssemblies = plan.payloadVariants != null &&
                plan.payloadVariants.Length != 0
                ? plan.payloadVariants.SelectMany(variant => variant?.assemblies ??
                    Array.Empty<RuntimeAssembly>())
                : plan.assemblies ?? Array.Empty<RuntimeAssembly>();
            foreach (RuntimeAssembly assembly in plannedAssemblies)
            {
                string name = NormalizeAssemblyName(assembly?.assemblyName);
                if (string.IsNullOrWhiteSpace(name))
                    throw new BuildFailedException(
                        "DHE runtime asset plan contains an unnamed assembly.");
                add(assembly.current, "current", name);
                add(assembly.currentMetaVersion, "current-metaversion", name);
            }

            string text = File.ReadAllText(aotListPath).Trim();
            StringArray aotList = JsonUtility.FromJson<StringArray>("{\"items\":" + text + "}");
            string[] aotAssemblies = aotList?.items ?? Array.Empty<string>();
            string[] expectedNames = aotAssemblies.Select(NormalizeAssemblyName).ToArray();
            if (expectedNames.Any(string.IsNullOrWhiteSpace) ||
                expectedNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != expectedNames.Length)
                throw new BuildFailedException("DHE AotFileList contains an invalid assembly set.");

            Action<RuntimeAotMetadata[], string> addMetadataSet = (records, setId) =>
            {
                if (!IsSha256(setId) || records == null || records.Length != expectedNames.Length)
                    throw new BuildFailedException(
                        "DHE runtime plan AOT metadata set identity is invalid.");
                var names = new HashSet<string>(expectedNames, StringComparer.OrdinalIgnoreCase);
                foreach (RuntimeAotMetadata metadata in records)
                {
                    string name = NormalizeAssemblyName(metadata?.assemblyName);
                    if (string.IsNullOrWhiteSpace(name) || !names.Remove(name) ||
                        !IsSha256(metadata.sha256))
                        throw new BuildFailedException(
                            "DHE runtime plan AOT metadata set does not match AotFileList.txt: " + name);
                    string metadataPath = NormalizeAssetPath(metadata.path);
                    if (paths.Contains(metadataPath))
                    {
                        if (!metadataHashes.TryGetValue(metadataPath, out string priorHash) ||
                            !string.Equals(priorHash, metadata.sha256,
                                StringComparison.OrdinalIgnoreCase))
                            throw new BuildFailedException(
                                "DHE runtime plan reuses an AOT metadata path with a different hash: " +
                                metadataPath);
                        continue;
                    }
                    add(metadataPath, "aot-metadata", name);
                    metadataHashes.Add(metadataPath, metadata.sha256);
                }
                if (names.Count != 0)
                    throw new BuildFailedException(
                        "DHE AotFileList contains assemblies absent from the runtime plan.");
            };

            if (string.Equals(plan.selection, "embedded-base-metaversion", StringComparison.Ordinal))
            {
                if ((plan.aotMetadataSets != null && plan.aotMetadataSets.Length != 0) ||
                    (plan.baseSelections != null && plan.baseSelections.Length != 0))
                    throw new BuildFailedException("DHE Base runtime plan contains resource selections.");
                addMetadataSet(plan.aotMetadata ?? Array.Empty<RuntimeAotMetadata>(),
                    plan.aotMetadataSetId);
            }
            else if (string.Equals(plan.selection,
                         "embedded-base-metaversion-and-aot-metadata-set", StringComparison.Ordinal))
            {
                if ((plan.aotMetadata != null && plan.aotMetadata.Length != 0) ||
                    plan.aotMetadataSets == null || plan.aotMetadataSets.Length == 0 ||
                    plan.baseSelections == null || plan.baseSelections.Length == 0)
                    throw new BuildFailedException("DHE resource runtime plan has no metadata selections.");
                var setIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (RuntimeAotMetadataSet set in plan.aotMetadataSets)
                {
                    if (set == null || !setIds.Add(set.aotMetadataSetId))
                        throw new BuildFailedException("DHE resource runtime plan contains a duplicate metadata set.");
                    addMetadataSet(set.assemblies, set.aotMetadataSetId);
                }
                var baseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (RuntimeBaseSelection selection in plan.baseSelections)
                {
                    if (selection == null || !IsSha256(selection.baseId) ||
                        !baseIds.Add(selection.baseId) ||
                        !setIds.Contains(selection.aotMetadataSetId))
                        throw new BuildFailedException("DHE resource runtime plan contains an invalid Base selection.");
                }
            }
            else throw new BuildFailedException("DHE runtime plan selection mode is invalid.");
            return result;
        }

        private static string ResolvePackageFile(string root, string name, string description)
        {
            if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name))
                throw new BuildFailedException(description + " has an unsafe file name: " + name);
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string resolved = Path.GetFullPath(Path.Combine(root,
                name.Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar)));
            if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException(description + " escapes the package directory: " + name);
            return resolved;
        }

        private static string ComputeHash(string path, string algorithm)
        {
            if (string.IsNullOrWhiteSpace(algorithm) ||
                string.Equals(algorithm, "md5", StringComparison.OrdinalIgnoreCase))
            {
                using (MD5 hash = MD5.Create())
                using (FileStream stream = File.OpenRead(path))
                    return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", string.Empty)
                        .ToLowerInvariant();
            }
            if (string.Equals(algorithm, "sha256", StringComparison.OrdinalIgnoreCase))
                return Sha256File(path);
            throw new BuildFailedException(
                "Unsupported resource bundle hash algorithm: " + algorithm);
        }

        private static string Sha256File(string path)
        {
            using (SHA256 hash = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", string.Empty)
                    .ToLowerInvariant();
        }

        private static bool IsSha256(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length == 64 &&
                value.All(Uri.IsHexDigit);
        }

        private static string NormalizeAssetPath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').TrimStart('/');
        }

        private static string NormalizeAssemblyName(string name)
        {
            return string.IsNullOrWhiteSpace(name) ? string.Empty :
                (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileNameWithoutExtension(name) : name.Trim());
        }

        private static string RequireFile(string path, string description)
        {
            string full = Path.GetFullPath(path ?? string.Empty);
            if (!File.Exists(full))
                throw new FileNotFoundException("DHE " + description + " was not found", full);
            return full;
        }

        private static string RequireDirectory(string path, string description)
        {
            string full = Path.GetFullPath(path ?? string.Empty);
            if (!Directory.Exists(full))
                throw new DirectoryNotFoundException("DHE " + description + " was not found: " + full);
            return full;
        }

        private static void WriteJson(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(value, true), new UTF8Encoding(false));
        }

        [Serializable] private sealed class StringArray { public string[] items; }
        [Serializable] private sealed class RuntimePlan
        {
            public int schemaVersion;
            public string format;
            public string selection;
            public string aotMetadataSetId;
            public RuntimeAotMetadata[] aotMetadata;
            public RuntimeAotMetadataSet[] aotMetadataSets;
            public RuntimeBaseSelection[] baseSelections;
            public RuntimeAssembly[] assemblies;
            public RuntimePayloadVariant[] payloadVariants;
        }
        [Serializable] private sealed class RuntimePayloadVariant
        {
            public string variantId;
            public string currentAssemblySetSha256;
            public RuntimeAssembly[] assemblies;
        }
        [Serializable] private sealed class RuntimeAotMetadata
        {
            public string assemblyName;
            public string sha256;
            public string path;
        }
        [Serializable] private sealed class RuntimeAotMetadataSet
        {
            public string aotMetadataSetId;
            public RuntimeAotMetadata[] assemblies;
        }
        [Serializable] private sealed class RuntimeBaseSelection
        {
            public string baseId;
            public string aotMetadataSetId;
            public string payloadVariantId;
            public string currentAssemblySetSha256;
        }
        [Serializable] private sealed class RuntimeAssembly
        {
            public string assemblyName;
            public string current;
            public string currentMetaVersion;
        }
        private sealed class RequiredAssetSpec
        {
            public string AssetPath;
            public string AssetKind;
            public string AssemblyName;
        }
        [Serializable] private sealed class RequiredAssetEvidence
        {
            public string assetPath;
            public string assetKind;
            public string assemblyName;
            public bool present;
            public string bundleName;
            public string bundleFileName;
            public string bundleFileHash;
            public string bundleSha256;
            public long bundleSize;
        }
        [Serializable] private sealed class ResourceBuildEvidence
        {
            public int schemaVersion;
            public string format;
            public string target;
            public string packageName;
            public string packageVersion;
            public string packageDirectory;
            public string buildReport;
            public string buildReportSha256;
            public int assetCount;
            public int bundleCount;
            public RequiredAssetEvidence[] requiredAssets;
            public bool passed;
        }
        [Serializable] private sealed class ResourceEvidence
        {
            public int schemaVersion;
            public string format;
            public string generatedAtUtc;
            public bool passed;
            public string policy;
            public string strategy;
            public string target;
            public string pathSemantics;
            public string resourceBuild;
            public int requiredAssetCount;
        }
    }

    public sealed class DheProjectResourceEvidenceOptions
    {
        public string Target;
        public string RuntimePlanPath;
        public string AotAssemblyListPath;
        public string RuntimeAssetPrefix;
        public string[] ControlFileNames;
        public string PackageName;
        public string PackageVersion;
        public string PackageDirectory;
        public string PackageReportPath;
        public string OutputRoot;
        public string Strategy;
        public string BuildEvidenceFormat;
        public string BuildEvidenceFileName;
        public string BundleHashAlgorithm = "md5";
        public DheProjectResourceAsset[] Assets;
        public DheProjectResourceBundle[] Bundles;
    }

    public sealed class DheProjectResourceAsset
    {
        public string AssetPath;
        public string MainBundleName;
    }

    public sealed class DheProjectResourceBundle
    {
        public string BundleName;
        public string FileName;
        public string FileHash;
        public long FileSize;
        public string[] AssetPaths;
    }

    public sealed class DheProjectResourceEvidenceResult
    {
        public string BuildEvidencePath;
        public string ResourceEvidencePath;
        public int RequiredAssetCount;
    }
}
