using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace HybridCLR
{
    /// <summary>
    /// Project resource systems implement this narrow boundary so the DHE
    /// runtime stays independent from YooAsset, Addressables, and custom
    /// download frameworks.
    /// </summary>
    public interface IDheRuntimeAssetProvider
    {
        bool Exists(string assetPath);

        string LoadText(string assetPath);

        byte[] LoadBytes(string assetPath);
    }

    [Serializable]
    public sealed class DheRuntimeIdentity
    {
        public int IdentityVersion;
        public string Target;
        public string AotSnapshotKind;
        public string BaselineAssemblySha256;
        public string AotSnapshotSha256;
        public string NativeGuardSourceSha256;
        public string NativeManifestSha256;
        public string[] AssemblyNames;
        public string[] SnapshotHashes;
    }

    /// <summary>
    /// Project-independent DHE plan, identity, metadata, and registration
    /// implementation. Projects only provide asset bytes and call this API
    /// from their existing hot-update lifecycle.
    /// </summary>
    public static class DheRuntime
    {
        private const string PlanAssetPath = "Assets/GameMain/HotfixDlls/DheRuntimePlan.json";
        private const string DefaultAssetRoot = "Assets/GameMain/HotfixDlls/";

        private static readonly Dictionary<string, DheAssemblyArtifact> Artifacts =
            new Dictionary<string, DheAssemblyArtifact>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> AotMetadataHashes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> LoadedAssemblies =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool initialized;
        private static bool enabled;
        private static bool transactionProbeAttempted;
        private static bool transactionRetryValidated;
        private static string transactionAssemblyName;
        private static LoadImageErrorCode transactionFailureCode =
            LoadImageErrorCode.DHE_MV_REGISTRATION_FAILED;
        private static DheRuntimeIdentity identity;
        private static string assetRoot = DefaultAssetRoot;

        [Serializable]
        private sealed class RuntimePlan
        {
            public int schemaVersion;
            public string format;
            public DheAotMetadataRecord[] aotMetadata;
            public DheAssemblyRecord[] assemblies;
        }

        [Serializable]
        private sealed class DheAssemblyRecord
        {
            public string assemblyName;
            public string mv;
            public string snapshot;
        }

        [Serializable]
        private sealed class DheAotMetadataRecord
        {
            public string assemblyName;
            public string sha256;
        }

        private sealed class DheAssemblyArtifact
        {
            public byte[] MetaVersion;
            public byte[] Snapshot;
            public byte[] Current;
        }

        public static bool Enabled => enabled;

        public static int EmbeddedIdentityVersion => identity?.IdentityVersion ?? 0;

        public static string EmbeddedTarget => identity?.Target ?? string.Empty;

        public static string EmbeddedNativeGuardSourceSha256 =>
            identity?.NativeGuardSourceSha256 ?? string.Empty;

        public static string EmbeddedNativeManifestSha256 =>
            identity?.NativeManifestSha256 ?? string.Empty;

        public static bool TransactionRetryValidated => transactionRetryValidated;

        public static string TransactionRetryAssemblyName => transactionAssemblyName;

        public static LoadImageErrorCode TransactionRetryFailure => transactionFailureCode;

        public static string[] PlannedAssemblyNames
        {
            get
            {
                string[] names = new string[Artifacts.Count];
                Artifacts.Keys.CopyTo(names, 0);
                Array.Sort(names, StringComparer.OrdinalIgnoreCase);
                return names;
            }
        }

        public static string[] LoadedAssemblyNames
        {
            get
            {
                string[] names = new string[LoadedAssemblies.Count];
                LoadedAssemblies.CopyTo(names);
                Array.Sort(names, StringComparer.OrdinalIgnoreCase);
                return names;
            }
        }

        public static void Reset()
        {
            Artifacts.Clear();
            AotMetadataHashes.Clear();
            LoadedAssemblies.Clear();
            identity = null;
            assetRoot = DefaultAssetRoot;
            initialized = false;
            enabled = false;
            transactionProbeAttempted = false;
            transactionRetryValidated = false;
            transactionAssemblyName = null;
            transactionFailureCode = LoadImageErrorCode.DHE_MV_REGISTRATION_FAILED;
        }

        public static bool Initialize(IDheRuntimeAssetProvider provider, DheRuntimeIdentity buildIdentity,
            out string error, string planAssetPath = PlanAssetPath, string runtimeAssetRoot = DefaultAssetRoot)
        {
            error = string.Empty;
            if (initialized)
            {
                return true;
            }

#if UNITY_EDITOR
            if (Application.isEditor)
            {
                initialized = true;
                return true;
            }
#endif
            if (provider == null)
            {
                error = "DHE runtime asset provider is null.";
                return false;
            }
            if (!provider.Exists(planAssetPath))
            {
                initialized = true;
                return true;
            }

            try
            {
                assetRoot = NormalizeAssetRoot(runtimeAssetRoot);
                identity = buildIdentity ?? throw new InvalidDataException(
                    "DHE Player build identity is missing.");
                RuntimePlan plan = JsonUtility.FromJson<RuntimePlan>(provider.LoadText(planAssetPath));
                if (plan == null || plan.schemaVersion != 1 ||
                    !string.Equals(plan.format, "hybridclr.dhe-runtime-asset-plan.json",
                        StringComparison.Ordinal) || plan.assemblies == null || plan.assemblies.Length == 0)
                {
                    throw new InvalidDataException("DHE runtime plan has an invalid schema or no assemblies.");
                }

                foreach (DheAssemblyRecord record in plan.assemblies)
                {
                    string assemblyName = NormalizeAssemblyName(record?.assemblyName);
                    if (string.IsNullOrWhiteSpace(assemblyName) || Artifacts.ContainsKey(assemblyName))
                    {
                        throw new InvalidDataException(
                            "DHE runtime plan contains an empty or duplicate assembly.");
                    }
                    Artifacts.Add(assemblyName, new DheAssemblyArtifact
                    {
                        MetaVersion = provider.LoadBytes(ValidateAssetPath(record.mv, assemblyName + " mv")),
                        Snapshot = provider.LoadBytes(ValidateAssetPath(record.snapshot,
                            assemblyName + " snapshot")),
                    });
                }

                foreach (DheAotMetadataRecord record in plan.aotMetadata ??
                    Array.Empty<DheAotMetadataRecord>())
                {
                    string assemblyName = NormalizeAssemblyName(record?.assemblyName);
                    string hash = record?.sha256;
                    if (string.IsNullOrWhiteSpace(assemblyName) ||
                        AotMetadataHashes.ContainsKey(assemblyName) || !IsSha256(hash))
                    {
                        throw new InvalidDataException(
                            "DHE runtime plan contains an invalid AOT metadata record.");
                    }
                    AotMetadataHashes.Add(assemblyName, hash.ToLowerInvariant());
                }

                ValidateEmbeddedIdentity();
                enabled = true;
                initialized = true;
                Debug.Log("DHE runtime plan loaded. Assemblies: " +
                    string.Join(", ", Artifacts.Keys));
                return true;
            }
            catch (Exception exception)
            {
                Artifacts.Clear();
                AotMetadataHashes.Clear();
                LoadedAssemblies.Clear();
                identity = null;
                enabled = false;
                initialized = false;
                error = exception.Message;
                return false;
            }
        }

        public static bool IsDheAssembly(string assemblyName)
        {
            return enabled && Artifacts.ContainsKey(NormalizeAssemblyName(assemblyName));
        }

        public static bool ValidateAotMetadata(string assemblyName, byte[] bytes, out string error)
        {
            error = string.Empty;
            if (!enabled || AotMetadataHashes.Count == 0)
            {
                return true;
            }
            string normalizedName = NormalizeAssemblyName(assemblyName);
            if (!AotMetadataHashes.TryGetValue(normalizedName, out string expectedHash))
            {
                error = "DHE runtime plan does not contain AOT metadata: " + normalizedName;
                return false;
            }
            string actualHash;
            using (SHA256 sha = SHA256.Create())
            {
                actualHash = BitConverter.ToString(sha.ComputeHash(bytes ?? Array.Empty<byte>()))
                    .Replace("-", string.Empty).ToLowerInvariant();
            }
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                error = "DHE AOT metadata hash mismatch for " + normalizedName + ": expected " +
                    expectedHash + ", actual " + actualHash;
                return false;
            }
            return true;
        }

        public static bool ValidateAotMetadataAssemblySet(string[] assemblyNames, out string error)
        {
            error = string.Empty;
            if (!enabled || AotMetadataHashes.Count == 0)
            {
                return true;
            }
            HashSet<string> actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string assemblyName in assemblyNames ?? Array.Empty<string>())
            {
                string normalizedName = NormalizeAssemblyName(assemblyName);
                if (!actual.Add(normalizedName))
                {
                    error = "DHE AOT metadata list contains a duplicate assembly: " + normalizedName;
                    return false;
                }
            }
            if (actual.Count != AotMetadataHashes.Count)
            {
                error = "DHE AOT metadata list count does not match the runtime plan.";
                return false;
            }
            foreach (string expectedName in AotMetadataHashes.Keys)
            {
                if (!actual.Contains(expectedName))
                {
                    error = "DHE AOT metadata list is missing: " + expectedName;
                    return false;
                }
            }
            return true;
        }

        public static bool LoadAssemblyImage(string assemblyName, byte[] currentDll,
            out LoadImageErrorCode code, out string error)
        {
            error = string.Empty;
            string normalizedName = NormalizeAssemblyName(assemblyName);
            if (!Artifacts.TryGetValue(normalizedName, out DheAssemblyArtifact artifact))
            {
                code = LoadImageErrorCode.DHE_MV_ASSEMBLY_NOT_FOUND;
                error = "DHE runtime plan does not contain assembly: " + normalizedName;
                return false;
            }

            try
            {
                code = RuntimeApi.LoadDifferentialHybridAssemblyWithMetaVersion(
                    currentDll, artifact.MetaVersion, artifact.Snapshot);
                if (code == LoadImageErrorCode.OK)
                {
                    artifact.Current = currentDll == null ? null : (byte[])currentDll.Clone();
                    LoadedAssemblies.Add(normalizedName);
                    return true;
                }
                error = "DHE runtime returned " + code;
                return false;
            }
            catch (Exception exception)
            {
                code = LoadImageErrorCode.DHE_MV_REGISTRATION_FAILED;
                error = exception.Message;
                return false;
            }
        }

        public static bool RunTransactionProbe(out string error)
        {
            error = string.Empty;
            if (transactionProbeAttempted)
            {
                return transactionRetryValidated;
            }
            transactionProbeAttempted = true;
            if (!enabled)
            {
                error = "DHE runtime plan is not enabled.";
                return false;
            }

            foreach (string assemblyName in PlannedAssemblyNames)
            {
                DheAssemblyArtifact artifact = Artifacts[assemblyName];
                if (artifact.MetaVersion == null || GetMetaVersionMethodCount(artifact.MetaVersion) <= 0)
                {
                    continue;
                }
                if (artifact.Current == null)
                {
                    error = "DHE transaction probe requires a loaded assembly: " + assemblyName;
                    return false;
                }

                transactionAssemblyName = assemblyName;
                transactionFailureCode = RuntimeApi.LoadDifferentialHybridAssemblyWithMetaVersion(
                    artifact.Current, CreateInvalidMetaVersion(artifact.MetaVersion), artifact.Snapshot);
                if (transactionFailureCode != LoadImageErrorCode.DHE_MV_REGISTRATION_FAILED)
                {
                    error = "DHE transaction probe returned " + transactionFailureCode +
                        " for " + assemblyName;
                    return false;
                }
                LoadImageErrorCode retryCode = RuntimeApi.LoadDifferentialHybridAssemblyWithMetaVersion(
                    artifact.Current, artifact.MetaVersion, artifact.Snapshot);
                if (retryCode != LoadImageErrorCode.OK)
                {
                    error = "DHE transaction probe retry returned " + retryCode +
                        " for " + assemblyName;
                    return false;
                }
                transactionRetryValidated = true;
                return true;
            }

            error = "DHE transaction probe found no MV with changed methods.";
            return false;
        }

        public static bool ValidateEmbeddedIdentityForRuntime(string expectedTarget,
            string expectedNativeManifestSha256, string expectedNativeGuardSourceSha256, out string error)
        {
            error = string.Empty;
            if (identity == null || !string.Equals(identity.Target, expectedTarget,
                StringComparison.OrdinalIgnoreCase))
            {
                error = "DHE Player identity target does not match the requested target.";
                return false;
            }
            if (!string.Equals(identity.NativeManifestSha256, expectedNativeManifestSha256,
                StringComparison.OrdinalIgnoreCase))
            {
                error = "DHE Player identity native manifest hash does not match the build report.";
                return false;
            }
            if (!string.Equals(identity.NativeGuardSourceSha256, expectedNativeGuardSourceSha256,
                StringComparison.OrdinalIgnoreCase))
            {
                error = "DHE Player identity generated C++ hash does not match the build report.";
                return false;
            }
            return true;
        }

        private static void ValidateEmbeddedIdentity()
        {
            if (identity.IdentityVersion != 2 || !string.Equals(identity.AotSnapshotKind,
                "managed-assembly-plus-generated-cpp-v1", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "DHE Player build identity version or snapshot kind is invalid.");
            }
            if (!IsSha256(identity.BaselineAssemblySha256) ||
                !IsSha256(identity.AotSnapshotSha256) ||
                !IsSha256(identity.NativeGuardSourceSha256) ||
                !IsSha256(identity.NativeManifestSha256) || string.IsNullOrWhiteSpace(identity.Target))
            {
                throw new InvalidDataException("DHE Player build identity is incomplete.");
            }
            if (identity.AssemblyNames == null || identity.SnapshotHashes == null ||
                identity.AssemblyNames.Length != identity.SnapshotHashes.Length ||
                identity.AssemblyNames.Length != Artifacts.Count || identity.AssemblyNames.Length == 0)
            {
                throw new InvalidDataException("DHE Player build identity is missing or empty.");
            }

            HashSet<string> identityNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < identity.AssemblyNames.Length; index++)
            {
                string assemblyName = NormalizeAssemblyName(identity.AssemblyNames[index]);
                if (string.IsNullOrWhiteSpace(assemblyName) || !identityNames.Add(assemblyName))
                {
                    throw new InvalidDataException(
                        "DHE Player build identity contains an empty or duplicate assembly.");
                }
                if (!Artifacts.TryGetValue(assemblyName, out DheAssemblyArtifact artifact) ||
                    artifact.Snapshot == null || !string.Equals(ToHex(artifact.Snapshot),
                        identity.SnapshotHashes[index], StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "DHE Player build identity does not match assembly snapshot: " + assemblyName);
                }
            }
        }

        private static int GetMetaVersionMethodCount(byte[] metaVersion)
        {
            if (metaVersion == null || metaVersion.Length < 20 ||
                !string.Equals(System.Text.Encoding.ASCII.GetString(metaVersion, 0, 8),
                    "DHEMVLT1", StringComparison.Ordinal))
            {
                return 0;
            }
            uint methodCount = BitConverter.ToUInt32(metaVersion, 16);
            uint nameSize = BitConverter.ToUInt32(metaVersion, 12);
            return methodCount == 0 || nameSize > int.MaxValue ||
                88L + nameSize + 4L * methodCount > metaVersion.Length
                    ? 0 : checked((int)methodCount);
        }

        private static byte[] CreateInvalidMetaVersion(byte[] metaVersion)
        {
            int methodCount = GetMetaVersionMethodCount(metaVersion);
            int nameSize = checked((int)BitConverter.ToUInt32(metaVersion, 12));
            int tokenOffset = checked(88 + nameSize);
            if (methodCount <= 0 || tokenOffset > metaVersion.Length - 4)
            {
                throw new InvalidDataException(
                    "DHE MV payload has no method token for the transaction probe.");
            }
            byte[] invalid = (byte[])metaVersion.Clone();
            Buffer.BlockCopy(BitConverter.GetBytes(0x0600ffffu), 0, invalid, tokenOffset, 4);
            return invalid;
        }

        private static string ValidateAssetPath(string path, string description)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidDataException("DHE " + description + " path is empty.");
            }
            string normalized = path.Replace('\\', '/');
            if (!normalized.StartsWith(assetRoot, StringComparison.Ordinal) ||
                normalized.Contains("../", StringComparison.Ordinal) || normalized.EndsWith("/..",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("DHE " + description +
                    " path escapes the hotfix asset root: " + path);
            }
            return normalized;
        }

        private static string NormalizeAssetRoot(string value)
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? DefaultAssetRoot :
                value.Replace('\\', '/');
            return normalized.EndsWith("/", StringComparison.Ordinal) ? normalized : normalized + "/";
        }

        private static string NormalizeAssemblyName(string assemblyName)
        {
            if (string.IsNullOrWhiteSpace(assemblyName))
            {
                return string.Empty;
            }
            return assemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(assemblyName) : assemblyName.Trim();
        }

        private static string ToHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes ?? Array.Empty<byte>()).Replace("-", string.Empty)
                .ToLowerInvariant();
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
            {
                return false;
            }
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (!(character >= '0' && character <= '9') &&
                    !(character >= 'a' && character <= 'f') &&
                    !(character >= 'A' && character <= 'F'))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
