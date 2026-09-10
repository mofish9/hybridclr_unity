using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
        public string EngineWorkflow;
        public string Il2CppCodeGeneration;
        public string AotSnapshotKind;
        public string BaseId;
        public string ManagedAssemblySetSha256;
        public string AotAssemblySetSha256;
        public string AotSnapshotSha256;
        public string AotAnalysisSnapshotSha256;
        public string NativeGuardSourceSha256;
        public string NativeManifestSha256;
        public string BaseMetaVersionSetSha256;
        public string AotMetadataSetId;
        public string RuntimeProtocol;
        public string RuntimeContract;
        public string[] RuntimeCapabilities;
        public string[] AotAssemblyNames;
        public string RuntimeAssetRoot;
        public string BaseMetaVersionAssetRoot;
        public string[] AssemblyNames;
        public string[] BaseMetaVersionHashes;
    }

    /// <summary>
    /// Project-independent DHE plan, identity, metadata, and registration
    /// implementation. Projects only provide asset bytes and call this API
    /// from their existing hot-update lifecycle.
    /// </summary>
    public static partial class DheRuntime
    {
        private const string PlanAssetPath = "Assets/GameMain/HotfixDlls/DheRuntimePlan.json";
        private const string DefaultAssetRoot = "Assets/GameMain/HotfixDlls/";
		private const string NativeRuntimeProtocol = "dhe-runtime-protocol-v1";
		private const string NativeRuntimeContract = "dhe-runtime-v32";
        private static readonly string[] NativeRuntimeCapabilities =
        {
            "aot-guard-v1",
            "stable-method-identity-v1",
            "single-current-multibase-v1",
            "resource-update-plan-integrity-v1",
            "resource-update-aot-metadata-path-v1",
            "resource-update-aot-metadata-set-selection-v1",
            "atomic-multi-assembly-registration-v1",
            "current-storage-execution-plan-array-v1",
            "physical-current-interface-additions-v1",
            "physical-current-interface-evolution-v1",
            "physical-current-interface-map-v1",
            "current-parameter-cache-selection-v1",
            "physical-current-reference-virtual-invocation-v1",
            "current-generic-methodimpl-owners-v1",
            "current-virtual-signature-frames-v1",
            "current-scalar-instance-frames-v1",
            "physical-current-parent-evolution-v1",
            "current-identical-physical-frames-v1",
            "current-native-reference-physical-frames-v1",
            "current-parent-member-handles-v1",
            "current-implicit-interface-method-declarations-v1",
            "hotfix-generic-context-dispatch-v1",
            "current-parameter-default-metadata-v1",
            "shared-type-initialization-v1",
            "current-static-value-storage-v1",
			"frozen-aot-source-v1",
			"frozen-aot-snapshot-source-binding-v1",
            "mixed-interpreter-source-batch-v1",
            "deferred-aot-module-initialization-v1",
            "current-literal-field-values-v1",
            "aot-module-token-resolution-v1",
            "length-preserved-constant-strings-v1",
            "aot-inline-entry-guards-v1",
            "tracked-native-load-phase-v1",
			"frozen-generic-context-dispatch-v1",
			"supplemental-existing-type-instance-fields-v1",
            "supplemental-existing-type-static-fields-v1",
            "supplemental-existing-generic-type-fields-v1",
            "supplemental-instance-field-addresses-v1",
            "aot-fgs-field-address-null-check-v1",
            "existing-interface-method-slots-v1",
            "cross-assembly-interface-declarations-v1",
            "inherited-interface-dispatch-v1",
            "base-virtual-slots-on-current-descendants-v1",
            "existing-class-virtual-methods-v1",
            "closed-current-parent-vtables-v1",
            "open-generic-dispatch-definitions-v1",
            "supplemental-closed-generic-methods-v1",
            "supplemental-generic-memberref-signatures-v1",
            "closed-interpreter-parent-vtables-v1",
            "closed-generic-method-definitions-v1",
			"supplemental-existing-type-methods-v1",
			"removed-existing-type-methods-v1",
			"existing-type-method-signature-replacement-v1",
			"removed-existing-type-fields-v1",
			"removed-types-v1",
			"logical-existing-type-properties-events-v1",
			"logical-existing-member-custom-attributes-v1",
            "supplemental-method-custom-attributes-v1",
            "assembly-reference-evolution-v1",
            "supplemental-type-base-references-v1",
            "supplemental-type-declarations-v1",
            "supplemental-method-generic-invocation-v1",
            "supplemental-generic-unresolved-stubs-v1",
            "homologous-attribute-constructors-v1",
            "logical-attribute-members-v1",
            "supplemental-nested-types-v1",
            "supplemental-top-level-types-v1",
        };

        /// <summary>
        /// Returns the capability inventory embedded by the matching Player
        /// build pipeline. The returned array can be modified by the caller.
        /// </summary>
        public static string[] GetSupportedRuntimeCapabilities()
        {
            return (string[])NativeRuntimeCapabilities.Clone();
        }

        private static readonly Dictionary<string, DheAssemblyArtifact> Artifacts =
            new Dictionary<string, DheAssemblyArtifact>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> AotMetadataHashes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> AotMetadataPaths =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DheFrozenAotSource> FrozenAotSources =
            new Dictionary<string, DheFrozenAotSource>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, byte[]> FrozenAotSourceBytes =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, byte[]> FrozenAotBaseMetaVersionBytes =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> LoadedAssemblies =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> LoadedMutableAssemblies =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool initialized;
        private static bool enabled;
        private static bool transactionProbeAttempted;
        private static bool transactionRetryValidated;
        private static bool validationProbesEnabled;
        private static string transactionAssemblyName;
        private static string selectedPayloadVariantId;
        private static string selectedPayloadCurrentAssemblySetSha256;
        private static LoadImageErrorCode transactionFailureCode =
            LoadImageErrorCode.DHE_MV_REGISTRATION_FAILED;
        private static DheRuntimeIdentity identity;
        private static string assetRoot = DefaultAssetRoot;

        [Serializable]
        private sealed class RuntimePlan
        {
            public int schemaVersion;
            public string format;
            public string selection;
            public string currentAssemblySetSha256;
            public string payloadVariantSetSha256;
            public string runtimeAssetRoot;
            public string baseMetaVersionAssetRoot;
            public string aotMetadataSetId;
            public DheAotMetadataRecord[] aotMetadata;
            public DheAotMetadataSet[] aotMetadataSets;
            public DheBaseSelection[] baseSelections;
            public DheAssemblyRecord[] assemblies;
            public DheFrozenAotSource[] frozenAotSources;
            public DhePayloadVariant[] payloadVariants;
        }

        [Serializable]
        private sealed class DhePayloadVariant
        {
            public string variantId;
            public string currentAssemblySetSha256;
            public DheAssemblyRecord[] assemblies;
        }

        [Serializable]
        private sealed class DheAssemblyRecord
        {
            public string assemblyName;
            public string executionMode;
            public string current;
            public string currentSha256;
            public string currentMetaVersion;
            public string baseMetaVersion;
            public string currentMetaVersionSha256;
            [NonSerialized] public DheExecutionPlan executionPlan;
        }

        [Serializable]
        private sealed class DheSupportedBase
        {
            public string baseId;
            public string target;
            public string engineWorkflow;
            public string il2cppCodeGeneration;
            public string aotSnapshotSha256;
            public string aotAnalysisSnapshotSha256;
            public string baseMetaVersionSetSha256;
            public string nativeGuardSourceSha256;
            public string nativeManifestSha256;
            public string managedAssemblySetSha256;
            public string aotAssemblySetSha256;
            public string[] aotAssemblyNames;
            public string runtimeProtocol;
            public string nativeRuntimeContract;
            public string[] runtimeCapabilities;
            public string[] requiredRuntimeCapabilities;
            public string runtimeAssetRoot;
            public string baseMetaVersionAssetRoot;
            public string buildIdentitySha256;
            public string aotMetadataSetId;
            public string payloadVariantId;
            public string currentAssemblySetSha256;
            public string compatibilityPolicy;
            public bool compatible;
            public bool guardCoverageValidated;
            public int unsupportedChangeCount;
            public DheAssemblyMode[] assemblyModes;
            public DheFrozenAotSource[] frozenAotSources;
        }

        [Serializable]
        private sealed class ResourceUpdateManifest
        {
            public int schemaVersion;
            public string format;
            public string payloadModel;
            public int metaVersionSchema;
            public string runtimeComparison;
            public string compatibilityPolicy;
            public string runtimeProtocol;
            public bool compatibilityValidated;
            public bool playerUpdateRequired;
            public bool guardCoverageValidated;
            public string currentAssemblySetSha256;
            public string payloadVariantSetSha256;
            public string runtimeAssetRoot;
            public string baseMetaVersionAssetRoot;
            public string runtimePlan;
            public string runtimePlanSha256;
            public string validation;
            public string validationSha256;
            public DheSupportedBase[] supportedBases;
            public DhePayloadVariant[] payloadVariants;
        }

        [Serializable]
        private sealed class ResourceUpdateValidation
        {
            public int schemaVersion;
            public string format;
            public bool passed;
            public string compatibilityPolicy;
            public string runtimeProtocol;
            public string currentAssemblySetSha256;
            public string payloadVariantSetSha256;
            public DhePayloadVariant[] payloadVariants;
            public DheSupportedBase[] bases;
        }

        [Serializable]
        private sealed class DheAotMetadataRecord
        {
            public string assemblyName;
            public string sha256;
            public string path;
        }

        [Serializable]
        private sealed class DheAotMetadataSet
        {
            public string aotMetadataSetId;
            public DheAotMetadataRecord[] assemblies;
        }

        [Serializable]
        private sealed class DheBaseSelection
        {
            public string baseId;
            public string aotMetadataSetId;
            public string payloadVariantId;
            public string currentAssemblySetSha256;
            public DheAssemblyMode[] assemblyModes;
            public DheFrozenAotSource[] frozenAotSources;
        }

        [Serializable]
        private sealed class DheAssemblyMode
        {
            public string assemblyName;
            public string executionMode;
            public DheExecutionPlan[] executionPlans;
        }

        private sealed class DheAssemblyArtifact
        {
            public byte[] MetaVersion;
            public byte[] BaseMetaVersion;
            public byte[] Current;
            public string ExpectedCurrentSha256;
            public string ExecutionMode;
            public DheExecutionPlan ExecutionPlan;
        }

        [Serializable]
        private sealed class DheFrozenAotSource
        {
            public string assemblyName;
            public string source;
            public string sourceSha256;
            public string baseMetaVersion;
            public string baseMetaVersionSha256;
            public uint[] currentStorageTypeTokens;
            public uint[] currentExecutionMethodTokens;
            public uint[] excludedBaseTypeTokens;
            public uint[] genericContextMethodTokens;
            public string sourceKind;
        }

        [Serializable]
        private sealed class DheAotAnalysisManifest
        {
            public int schemaVersion;
            public string format;
            public string normalization;
            public string identityAssembly;
            public string identityType;
            public DheAotAnalysisSource[] assemblies;
        }

        [Serializable]
        private sealed class DheAotAnalysisSource
        {
            public string assemblyName;
            public string file;
            public string sha256;
            public string normalizedSha256;
            public bool dhe;
        }

        public static bool Enabled => enabled;

        public static int EmbeddedIdentityVersion => identity?.IdentityVersion ?? 0;

        public static string EmbeddedTarget => identity?.Target ?? string.Empty;

        public static string EmbeddedEngineWorkflow => identity?.EngineWorkflow ?? string.Empty;

        public static string EmbeddedIl2CppCodeGeneration =>
            identity?.Il2CppCodeGeneration ?? string.Empty;

        public static string EmbeddedNativeGuardSourceSha256 =>
            identity?.NativeGuardSourceSha256 ?? string.Empty;

        public static string EmbeddedNativeManifestSha256 =>
            identity?.NativeManifestSha256 ?? string.Empty;

        public static bool TransactionRetryValidated => transactionRetryValidated;

        public static string TransactionRetryAssemblyName => transactionAssemblyName;

        public static LoadImageErrorCode TransactionRetryFailure => transactionFailureCode;

        /// <summary>Variant selected by the authenticated Base-to-payload binding.</summary>
        public static string SelectedPayloadVariantId => selectedPayloadVariantId ?? string.Empty;

        /// <summary>Current assembly-set hash for <see cref="SelectedPayloadVariantId"/>.</summary>
        public static string SelectedPayloadCurrentAssemblySetSha256 =>
            selectedPayloadCurrentAssemblySetSha256 ?? string.Empty;

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

        public static string[] DifferentialAssemblyNames => Artifacts
            .Where(pair => IsDifferentialMode(pair.Value.ExecutionMode))
            .Select(pair => pair.Key)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();

        public static string[] InterpreterOnlyAssemblyNames => Artifacts
            .Where(pair => string.Equals(pair.Value.ExecutionMode, "interpreter-only",
                StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();

        private static void ResetCore()
        {
            Artifacts.Clear();
            AotMetadataHashes.Clear();
            AotMetadataPaths.Clear();
            FrozenAotSources.Clear();
            FrozenAotSourceBytes.Clear();
            FrozenAotBaseMetaVersionBytes.Clear();
            LoadedAssemblies.Clear();
            LoadedMutableAssemblies.Clear();
            identity = null;
            assetRoot = DefaultAssetRoot;
            initialized = false;
            enabled = false;
            transactionProbeAttempted = false;
            transactionRetryValidated = false;
            transactionAssemblyName = null;
            selectedPayloadVariantId = null;
            selectedPayloadCurrentAssemblySetSha256 = null;
            transactionFailureCode = LoadImageErrorCode.DHE_MV_REGISTRATION_FAILED;
            validationProbesEnabled = false;
        }

        private static bool InitializeCore(IDheRuntimeAssetProvider provider, DheRuntimeIdentity buildIdentity,
            out string error, string planAssetPath = PlanAssetPath, string runtimeAssetRoot = DefaultAssetRoot,
            bool enableValidationProbes = false)
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
                validationProbesEnabled = enableValidationProbes;
                RuntimePlan plan = JsonUtility.FromJson<RuntimePlan>(provider.LoadText(planAssetPath));
                if (plan == null || plan.schemaVersion != 1 ||
                    !string.Equals(plan.format, "hybridclr.dhe-runtime-asset-plan.json",
                        StringComparison.Ordinal) ||
                    ((plan.assemblies == null || plan.assemblies.Length == 0) &&
                     (plan.payloadVariants == null || plan.payloadVariants.Length == 0)))
                {
                    throw new InvalidDataException("DHE runtime plan has an invalid schema or no assemblies.");
                }
                if (!string.Equals(NormalizeAssetRoot(plan.runtimeAssetRoot), assetRoot,
                         StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(NormalizeAssetRoot(plan.baseMetaVersionAssetRoot),
                         NormalizeAssetRoot(identity.BaseMetaVersionAssetRoot),
                         StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(assetRoot, NormalizeAssetRoot(identity.RuntimeAssetRoot),
                         StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "DHE runtime plan asset roots do not match the Player identity.");
                }
                DheAotMetadataRecord[] selectedAotMetadata = SelectAotMetadata(plan, identity);
                DheAssemblyRecord[] selectedAssemblies = SelectPayloadAssemblies(plan, identity);
                foreach (DheAssemblyRecord record in selectedAssemblies)
                    if (record != null) record.executionPlan = null;
                ApplySelectedAssemblyModes(plan, identity, selectedAssemblies);
                SetSelectedPayloadIdentity(plan, identity);
                FrozenAotSources.Clear();
                DheBaseSelection selectedBase = (plan.baseSelections ?? Array.Empty<DheBaseSelection>())
                    .SingleOrDefault(selection => selection != null &&
                        string.Equals(selection.baseId, identity.BaseId, StringComparison.OrdinalIgnoreCase));
                CanonicalFrozenSources(selectedBase?.frozenAotSources, identity);
                var frozenSnapshot = (selectedBase?.frozenAotSources?.Length ?? 0) == 0
                    ? null : ReadFrozenSnapshot(provider, identity);
                foreach (DheFrozenAotSource source in selectedBase?.frozenAotSources ?? Array.Empty<DheFrozenAotSource>())
                {
                    string name = NormalizeAssemblyName(source?.assemblyName);
                    if (string.IsNullOrWhiteSpace(name) || Artifacts.ContainsKey(name) ||
                        !(identity.AotAssemblyNames ?? Array.Empty<string>()).Contains(name) ||
                        selectedAssemblies.Any(record => record != null && NormalizeAssemblyName(record.assemblyName) == name) ||
                        !FrozenAotSources.TryAdd(name, source) || source.sourceKind != "frozen-base-aot" ||
                        !IsSha256(source.sourceSha256) ||
                        !IsSha256(source.baseMetaVersionSha256) || source.currentStorageTypeTokens == null ||
                        source.currentExecutionMethodTokens == null || source.excludedBaseTypeTokens == null)
                        throw new InvalidDataException("DHE frozen AOT source record is invalid: " + name);
                    if (!frozenSnapshot.TryGetValue(name, out DheAotAnalysisSource captured) || captured.dhe ||
                        !string.Equals(captured.sha256, source.sourceSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("DHE frozen source is not the ordinary DLL captured by this Base: " + name);
                    if ((source.genericContextMethodTokens?.Length ?? 0) != 0 &&
                        !(identity.RuntimeCapabilities ?? Array.Empty<string>()).Contains("frozen-generic-context-dispatch-v1"))
                        throw new InvalidDataException("Player does not support frozen generic context dispatch.");
                    source.source = ValidateFrozenAssetPath(source.source, identity, name, ".dll.bytes");
                    source.baseMetaVersion = ValidateFrozenAssetPath(source.baseMetaVersion, identity, name, ".mv.bytes");
                    byte[] sourceBytes = provider.LoadBytes(source.source);
                    byte[] sourceMvBytes = provider.LoadBytes(source.baseMetaVersion);
                    new DheExecutionPlan { schemaVersion = 1, assemblyName = name,
                        baseMetaVersionSha256 = source.baseMetaVersionSha256, currentMetaVersionSha256 = source.baseMetaVersionSha256,
                        currentStorageTypeTokens = source.currentStorageTypeTokens, currentExecutionMethodTokens = source.currentExecutionMethodTokens,
                        currentStorageTypeTokenCount = source.currentStorageTypeTokens.Length,
                        currentExecutionMethodTokenCount = source.currentExecutionMethodTokens.Length }.Validate(name, sourceMvBytes, sourceMvBytes);
                    if (!string.Equals(Sha256Hex(sourceBytes), source.sourceSha256,
                            StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(Sha256Hex(sourceMvBytes), source.baseMetaVersionSha256,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("DHE frozen AOT source hash binding is invalid: " + name);
                    // Validate already checked MV framing. Its source field must
                    // name the captured DLL, not only a resource-supplied MV hash.
                    if (!string.Equals(BitConverter.ToString(sourceMvBytes, 28, 32).Replace("-", ""),
                            captured.sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("DHE frozen MV does not bind the captured Base DLL: " + name);
                    FrozenAotSourceBytes.Add(name, sourceBytes);
                    FrozenAotBaseMetaVersionBytes.Add(name, sourceMvBytes);
                }

                foreach (DheAssemblyRecord record in selectedAssemblies)
                {
                    string assemblyName = NormalizeAssemblyName(record?.assemblyName);
                    if (string.IsNullOrWhiteSpace(assemblyName) || Artifacts.ContainsKey(assemblyName))
                    {
                        throw new InvalidDataException(
                            "DHE runtime plan contains an empty or duplicate assembly.");
                    }
                    byte[] currentMetaVersion = provider.LoadBytes(ValidateAssetPath(
                        record.currentMetaVersion, assemblyName + " current MetaVersion"));
                    if (!IsSha256(record.currentSha256) ||
                        !IsSha256(record.currentMetaVersionSha256) ||
                        !string.Equals(Sha256Hex(currentMetaVersion),
                            record.currentMetaVersionSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            "DHE runtime plan current payload hash binding is invalid: " + assemblyName);
                    }
                    string executionMode = NormalizeExecutionMode(record.executionMode);
                    byte[]? baseMetaVersion = null;
                    if (IsDifferentialMode(executionMode))
                    {
                        baseMetaVersion = provider.LoadBytes(ValidateBaseMetaVersionAssetPath(
                            record.baseMetaVersion, plan.baseMetaVersionAssetRoot,
                            assemblyName + " Base MetaVersion"));
                    }
                    if (record.executionPlan != null)
                    {
                        if (!IsDifferentialMode(executionMode) || identity.EngineWorkflow != "Unity2022Fgs" ||
                            !IsSha256(identity.AotAnalysisSnapshotSha256) ||
                            !(identity.RuntimeCapabilities ?? Array.Empty<string>()).Contains(DheExecutionPlan.Capability))
                            throw new InvalidDataException("DHE Current execution plan is unsupported by this Base: " + assemblyName);
                        record.executionPlan.Validate(assemblyName, baseMetaVersion, currentMetaVersion);
                        if ((record.executionPlan.currentGenericContextMethodTokens?.Length ?? 0) != 0 &&
                            !(identity.RuntimeCapabilities ?? Array.Empty<string>()).Contains(DheExecutionPlan.GenericContextCapability))
                            throw new InvalidDataException("Base lacks " + DheExecutionPlan.GenericContextCapability + ".");
                    }
                    Artifacts.Add(assemblyName, new DheAssemblyArtifact
                    {
                        MetaVersion = currentMetaVersion,
                        BaseMetaVersion = baseMetaVersion,
                        ExpectedCurrentSha256 = record.currentSha256.ToLowerInvariant(),
                        ExecutionMode = executionMode,
                        ExecutionPlan = record.executionPlan,
                    });
                }

                var selectedMetadataNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var selectedMetadataBytes = new List<KeyValuePair<string, byte[]>>();
                foreach (DheAotMetadataRecord record in selectedAotMetadata)
                {
                    string assemblyName = NormalizeAssemblyName(record?.assemblyName);
                    string hash = record?.sha256;
                    string path = ValidateAssetPath(record?.path,
                        assemblyName + " AOT metadata");
                    if (string.IsNullOrWhiteSpace(assemblyName) ||
                        !selectedMetadataNames.Add(assemblyName) ||
                        AotMetadataHashes.ContainsKey(assemblyName) || !IsSha256(hash))
                    {
                        throw new InvalidDataException(
                            "DHE runtime plan contains an invalid AOT metadata record.");
                    }
                    byte[] metadataBytes = provider.LoadBytes(path);
                    if (!string.Equals(Sha256Hex(metadataBytes), hash,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            "DHE AOT metadata payload hash mismatch: " + assemblyName);
                    selectedMetadataBytes.Add(new KeyValuePair<string, byte[]>(assemblyName,
                        metadataBytes));
                    AotMetadataHashes.Add(assemblyName, hash.ToLowerInvariant());
                    AotMetadataPaths.Add(assemblyName, path);
                }
                string selectedSetId = string.IsNullOrWhiteSpace(plan.aotMetadataSetId)
                    ? (plan.baseSelections ?? Array.Empty<DheBaseSelection>()).Where(item => item != null &&
                        string.Equals(item.baseId, identity.BaseId,
                            StringComparison.OrdinalIgnoreCase)).Select(item => item.aotMetadataSetId)
                        .SingleOrDefault() ?? string.Empty
                    : plan.aotMetadataSetId;
                if (!IsSha256(selectedSetId) ||
                    !string.Equals(Sha256NamedByteSet(selectedMetadataBytes), selectedSetId,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "DHE AOT metadata set content does not match its authenticated identity.");

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
                AotMetadataPaths.Clear();
                LoadedAssemblies.Clear();
                LoadedMutableAssemblies.Clear();
                FrozenAotSources.Clear();
                FrozenAotSourceBytes.Clear();
                FrozenAotBaseMetaVersionBytes.Clear();
                identity = null;
                selectedPayloadVariantId = null;
                selectedPayloadCurrentAssemblySetSha256 = null;
                enabled = false;
                initialized = false;
                error = exception.Message;
                return false;
            }
        }

        /// <summary>
        /// Validates that the one current payload was audited for this Base.
        /// Base-specific MetaVersion bytes remain embedded in the Player and
        /// are never selected from the remote update.
        /// </summary>
        public static bool TryValidateResourceUpdate(IDheRuntimeAssetProvider provider,
            DheRuntimeIdentity buildIdentity, string manifestAssetPath, out string error)
        {
            error = string.Empty;
            if (provider == null || buildIdentity == null)
            {
                error = "DHE resource update selection requires a provider and Player identity.";
                return false;
            }
            try
            {
                if (!provider.Exists(manifestAssetPath))
                {
                    error = "DHE resource update manifest was not found: " + manifestAssetPath;
                    return false;
                }
                ResourceUpdateManifest manifest = JsonUtility.FromJson<ResourceUpdateManifest>(
                    provider.LoadText(manifestAssetPath));
                if (manifest != null && manifest.payloadVariants != null &&
                    manifest.payloadVariants.Length != 0 &&
                    (!IsSha256(manifest.payloadVariantSetSha256) ||
                     !string.Equals(manifest.payloadVariantSetSha256,
                         ComputePayloadVariantSetHash(manifest.payloadVariants),
                         StringComparison.OrdinalIgnoreCase)))
                {
                    error = "DHE resource manifest payload variant set hash is invalid.";
                    return false;
                }
                if (manifest == null || manifest.schemaVersion != 1 ||
                    !string.Equals(manifest.format, "hybridclr.dhe-resource-update.json",
                        StringComparison.Ordinal) ||
                    (manifest.payloadModel != "single-current-payload" &&
                     manifest.payloadModel != "variant-current-payload") ||
                    manifest.metaVersionSchema != 1 || manifest.playerUpdateRequired ||
                    !manifest.guardCoverageValidated ||
                    !string.Equals(manifest.runtimeComparison,
                        "embedded-base-mv-vs-current-mv", StringComparison.Ordinal) ||
                    !IsCompatibilityPolicy(manifest.compatibilityPolicy) ||
                    !string.Equals(manifest.runtimeProtocol, buildIdentity.RuntimeProtocol,
                        StringComparison.Ordinal) ||
                    !string.Equals(NormalizeAssetRoot(manifest.runtimeAssetRoot),
                        NormalizeAssetRoot(buildIdentity.RuntimeAssetRoot),
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(NormalizeAssetRoot(manifest.baseMetaVersionAssetRoot),
                        NormalizeAssetRoot(buildIdentity.BaseMetaVersionAssetRoot),
                        StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(manifest.runtimeAssetRoot) ||
                    string.IsNullOrWhiteSpace(manifest.baseMetaVersionAssetRoot) ||
                    !manifest.compatibilityValidated || !IsSha256(manifest.currentAssemblySetSha256) ||
                    !IsSha256(manifest.validationSha256) ||
                    !IsSha256(manifest.runtimePlanSha256) ||
                    !string.Equals(manifest.runtimePlan, "dhe-runtime-plan.json",
                        StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(manifest.validation) ||
                    manifest.validation.Contains("/") || manifest.validation.Contains("\\") ||
                    manifest.supportedBases == null)
                {
                    error = "DHE single-payload resource update schema is invalid.";
                    return false;
                }
                string manifestDirectory = Path.GetDirectoryName(
                    manifestAssetPath.Replace('/', Path.DirectorySeparatorChar))?
                    .Replace(Path.DirectorySeparatorChar, '/') ?? string.Empty;
                string validationPath = string.IsNullOrEmpty(manifestDirectory)
                    ? manifest.validation : manifestDirectory.TrimEnd('/') + "/" + manifest.validation;
                if (!provider.Exists(validationPath))
                {
                    error = "DHE resource compatibility validation was not found: " + validationPath;
                    return false;
                }
                byte[] validationBytes = provider.LoadBytes(validationPath);
                if (!string.Equals(Sha256Hex(validationBytes), manifest.validationSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    error = "DHE resource compatibility validation hash mismatch.";
                    return false;
                }
                ResourceUpdateValidation validation = JsonUtility.FromJson<ResourceUpdateValidation>(
                    System.Text.Encoding.UTF8.GetString(validationBytes));
                if (validation != null && validation.payloadVariants != null &&
                    validation.payloadVariants.Length != 0 &&
                    (!IsSha256(validation.payloadVariantSetSha256) ||
                     !string.Equals(validation.payloadVariantSetSha256,
                         ComputePayloadVariantSetHash(validation.payloadVariants),
                         StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(validation.payloadVariantSetSha256,
                         manifest.payloadVariantSetSha256, StringComparison.OrdinalIgnoreCase)))
                {
                    error = "DHE resource validation payload variant set hash is invalid.";
                    return false;
                }
                if (validation == null || validation.schemaVersion != 1 || !validation.passed ||
                    !string.Equals(validation.format,
                        "hybridclr.dhe-resource-update-validation.json", StringComparison.Ordinal) ||
                    !string.Equals(validation.compatibilityPolicy, manifest.compatibilityPolicy,
                        StringComparison.Ordinal) ||
                    !string.Equals(validation.runtimeProtocol, manifest.runtimeProtocol,
                        StringComparison.Ordinal) ||
                    !string.Equals(validation.currentAssemblySetSha256,
                        manifest.currentAssemblySetSha256, StringComparison.OrdinalIgnoreCase) ||
                    validation.bases == null)
                {
                    error = "DHE resource compatibility validation is invalid.";
                    return false;
                }
                string baseId = buildIdentity.BaseId;
                if (buildIdentity.IdentityVersion != 1 || !IsSha256(baseId) ||
                    string.IsNullOrWhiteSpace(buildIdentity.Target) ||
                    !IsBuildConfiguration(buildIdentity.EngineWorkflow,
                        buildIdentity.Il2CppCodeGeneration) ||
                    !IsSha256(buildIdentity.ManagedAssemblySetSha256) ||
                    !TryValidateAotAssemblyInventory(buildIdentity.AotAssemblyNames,
                        buildIdentity.AotAssemblySetSha256) ||
                    !IsSha256(buildIdentity.AotSnapshotSha256) ||
                    !IsSha256(buildIdentity.BaseMetaVersionSetSha256) ||
                    !IsSha256(buildIdentity.AotMetadataSetId) ||
                    !IsSha256(buildIdentity.NativeGuardSourceSha256) ||
                    !IsSha256(buildIdentity.NativeManifestSha256) ||
                    string.IsNullOrWhiteSpace(buildIdentity.RuntimeAssetRoot) ||
                    string.IsNullOrWhiteSpace(buildIdentity.BaseMetaVersionAssetRoot) ||
                    !string.Equals(baseId, ComputeBaseId(buildIdentity),
                        StringComparison.OrdinalIgnoreCase))
                {
                    error = "DHE Player identity is incomplete for resource update selection.";
                    return false;
                }
                DheSupportedBase[] matches = manifest.supportedBases.Where(item =>
                    MatchesPlayerBase(item, buildIdentity)).ToArray();
                DheSupportedBase[] validatedMatches = validation.bases.Where(item =>
                    MatchesPlayerBase(item, buildIdentity)).ToArray();
                if (matches.Length != 1 || !matches[0].compatible ||
                    !matches[0].guardCoverageValidated ||
                    matches[0].unsupportedChangeCount != 0 ||
                    !HasRequiredRuntimeCapabilities(matches[0], buildIdentity) ||
                    !string.Equals(matches[0].compatibilityPolicy,
                        manifest.compatibilityPolicy, StringComparison.Ordinal) ||
                    !string.Equals(matches[0].baseMetaVersionSetSha256,
                        buildIdentity.BaseMetaVersionSetSha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(matches[0].nativeManifestSha256,
                        buildIdentity.NativeManifestSha256, StringComparison.OrdinalIgnoreCase) ||
                    validatedMatches.Length != 1 || !validatedMatches[0].compatible ||
                    !validatedMatches[0].guardCoverageValidated ||
                    validatedMatches[0].unsupportedChangeCount != 0 ||
                    !HasRequiredRuntimeCapabilities(validatedMatches[0], buildIdentity) ||
                    !string.Equals(validatedMatches[0].baseMetaVersionSetSha256,
                        matches[0].baseMetaVersionSetSha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(validatedMatches[0].aotSnapshotSha256,
                        matches[0].aotSnapshotSha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(validatedMatches[0].nativeGuardSourceSha256,
                        matches[0].nativeGuardSourceSha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(validatedMatches[0].nativeManifestSha256,
                        matches[0].nativeManifestSha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(validatedMatches[0].nativeRuntimeContract,
                        matches[0].nativeRuntimeContract, StringComparison.Ordinal) ||
                    !string.Equals(validatedMatches[0].aotMetadataSetId,
                        matches[0].aotMetadataSetId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(validatedMatches[0].runtimeProtocol,
                        matches[0].runtimeProtocol, StringComparison.Ordinal) ||
                    !IsSha256(matches[0].buildIdentitySha256) ||
                    !string.Equals(validatedMatches[0].buildIdentitySha256,
                        matches[0].buildIdentitySha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(validatedMatches[0].compatibilityPolicy,
                        matches[0].compatibilityPolicy, StringComparison.Ordinal) ||
                    !new HashSet<string>(validatedMatches[0].runtimeCapabilities ??
                            Array.Empty<string>(), StringComparer.Ordinal)
                        .SetEquals(matches[0].runtimeCapabilities ?? Array.Empty<string>()) ||
                    !new HashSet<string>(validatedMatches[0].requiredRuntimeCapabilities ??
                            Array.Empty<string>(), StringComparer.Ordinal)
                        .SetEquals(matches[0].requiredRuntimeCapabilities ?? Array.Empty<string>()))
                {
                    error = "DHE current payload was not validated for Player base " + baseId + ".";
                    return false;
                }
                string selectedVariantId = string.IsNullOrWhiteSpace(matches[0].payloadVariantId)
                    ? "default" : matches[0].payloadVariantId;
                DhePayloadVariant manifestVariant = SelectManifestPayloadVariant(manifest,
                    selectedVariantId);
                if (manifestVariant == null || !IsSha256(manifestVariant.currentAssemblySetSha256) ||
                    (!string.IsNullOrWhiteSpace(matches[0].currentAssemblySetSha256) &&
                     !string.Equals(matches[0].currentAssemblySetSha256,
                         manifestVariant.currentAssemblySetSha256, StringComparison.OrdinalIgnoreCase)) ||
                    !string.Equals(manifestVariant.currentAssemblySetSha256,
                        FindPlanVariantHash(provider, manifestAssetPath, manifest.runtimePlan,
                            selectedVariantId, manifest.runtimePlanSha256),
                        StringComparison.OrdinalIgnoreCase))
                {
                    error = "DHE resource update payload variant is not bound to this Player base.";
                    return false;
                }
                if (!CanonicalAssemblyModes(matches[0].assemblyModes).SequenceEqual(
                        CanonicalAssemblyModes(validatedMatches[0].assemblyModes), StringComparer.Ordinal))
                    throw new InvalidDataException("DHE resource validation assembly selections do not match the manifest.");
                if (!CanonicalFrozenSources(matches[0].frozenAotSources, buildIdentity).SequenceEqual(
                        CanonicalFrozenSources(validatedMatches[0].frozenAotSources, buildIdentity), StringComparer.Ordinal))
                    throw new InvalidDataException("DHE resource validation frozen sources do not match the manifest.");
                if ((matches[0].frozenAotSources?.Length ?? 0) != 0 &&
                    !(matches[0].requiredRuntimeCapabilities ?? Array.Empty<string>()).Contains("frozen-aot-snapshot-source-binding-v1"))
                    throw new InvalidDataException("DHE resource is missing its required frozen snapshot source binding capability.");
                if ((matches[0].frozenAotSources ?? Array.Empty<DheFrozenAotSource>()).Any(source =>
                        (source.genericContextMethodTokens?.Length ?? 0) != 0) &&
                    !(matches[0].requiredRuntimeCapabilities ?? Array.Empty<string>()).Contains("frozen-generic-context-dispatch-v1"))
                    throw new InvalidDataException("DHE resource is missing its required frozen generic context capability.");
                if ((matches[0].assemblyModes ?? Array.Empty<DheAssemblyMode>()).Any(mode =>
                        (SelectedExecutionPlan(mode)?.currentGenericContextMethodTokens?.Length ?? 0) != 0) &&
                    !(matches[0].requiredRuntimeCapabilities ?? Array.Empty<string>()).Contains(DheExecutionPlan.GenericContextCapability))
                    throw new InvalidDataException("DHE resource is missing its required hotfix generic context capability.");
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static bool MatchesPlayerBase(DheSupportedBase candidate,
            DheRuntimeIdentity buildIdentity)
        {
            return candidate != null && buildIdentity != null &&
                TryValidateAotAssemblyInventory(candidate.aotAssemblyNames,
                    candidate.aotAssemblySetSha256) &&
                string.Equals(candidate.baseId, buildIdentity.BaseId,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.target, buildIdentity.Target, StringComparison.Ordinal) &&
                string.Equals(candidate.engineWorkflow, buildIdentity.EngineWorkflow,
                    StringComparison.Ordinal) &&
                string.Equals(candidate.il2cppCodeGeneration,
                    buildIdentity.Il2CppCodeGeneration, StringComparison.Ordinal) &&
                string.Equals(candidate.managedAssemblySetSha256,
                    buildIdentity.ManagedAssemblySetSha256, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.aotAssemblySetSha256,
                    buildIdentity.AotAssemblySetSha256, StringComparison.OrdinalIgnoreCase) &&
                new HashSet<string>(candidate.aotAssemblyNames ?? Array.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase).SetEquals(
                    buildIdentity.AotAssemblyNames ?? Array.Empty<string>()) &&
                string.Equals(candidate.aotSnapshotSha256, buildIdentity.AotSnapshotSha256,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.aotAnalysisSnapshotSha256, buildIdentity.AotAnalysisSnapshotSha256,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.baseMetaVersionSetSha256,
                    buildIdentity.BaseMetaVersionSetSha256, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.aotMetadataSetId, buildIdentity.AotMetadataSetId,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.nativeGuardSourceSha256,
                    buildIdentity.NativeGuardSourceSha256, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.nativeManifestSha256,
                    buildIdentity.NativeManifestSha256, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.runtimeProtocol, buildIdentity.RuntimeProtocol,
                    StringComparison.Ordinal) &&
                string.Equals(candidate.nativeRuntimeContract, buildIdentity.RuntimeContract,
                    StringComparison.Ordinal) &&
                string.Equals(NormalizeAssetRoot(candidate.runtimeAssetRoot),
                    NormalizeAssetRoot(buildIdentity.RuntimeAssetRoot),
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizeAssetRoot(candidate.baseMetaVersionAssetRoot),
                    NormalizeAssetRoot(buildIdentity.BaseMetaVersionAssetRoot),
                    StringComparison.OrdinalIgnoreCase) &&
                new HashSet<string>(candidate.runtimeCapabilities ?? Array.Empty<string>(),
                    StringComparer.Ordinal).SetEquals(buildIdentity.RuntimeCapabilities ??
                    Array.Empty<string>());
        }

        private static DhePayloadVariant SelectManifestPayloadVariant(
            ResourceUpdateManifest manifest, string variantId)
        {
            DhePayloadVariant[] variants = manifest.payloadVariants ?? Array.Empty<DhePayloadVariant>();
            if (variants.Length == 0)
            {
                return string.Equals(variantId, "default", StringComparison.OrdinalIgnoreCase)
                    ? new DhePayloadVariant
                    {
                        variantId = "default",
                        currentAssemblySetSha256 = manifest.currentAssemblySetSha256,
                    }
                    : null;
            }
            DhePayloadVariant[] matches = variants.Where(variant => variant != null &&
                string.Equals(variant.variantId, variantId, StringComparison.OrdinalIgnoreCase)).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        private static string FindPlanVariantHash(IDheRuntimeAssetProvider provider,
            string manifestAssetPath, string planAssetPath, string variantId, string expectedHash)
        {
            string directory = Path.GetDirectoryName(manifestAssetPath.Replace('/',
                Path.DirectorySeparatorChar))?.Replace(Path.DirectorySeparatorChar, '/') ?? string.Empty;
            string path = planAssetPath.Replace('\\', '/');
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) && !string.IsNullOrEmpty(directory))
                path = directory.TrimEnd('/') + "/" + path;
            if (!provider.Exists(path)) return string.Empty;
            byte[] bytes = provider.LoadBytes(path);
            if (!string.Equals(Sha256Hex(bytes), expectedHash, StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            RuntimePlan plan = JsonUtility.FromJson<RuntimePlan>(System.Text.Encoding.UTF8.GetString(bytes));
            if (plan == null) return string.Empty;
            DhePayloadVariant[] variants = plan.payloadVariants ?? Array.Empty<DhePayloadVariant>();
            if (variants.Length == 0)
                return string.Equals(variantId, "default", StringComparison.OrdinalIgnoreCase)
                    ? plan.currentAssemblySetSha256 : string.Empty;
            DhePayloadVariant[] matches = variants.Where(variant => variant != null &&
                string.Equals(variant.variantId, variantId, StringComparison.OrdinalIgnoreCase)).ToArray();
            return matches.Length == 1 ? matches[0].currentAssemblySetSha256 : string.Empty;
        }

        private static bool HasRequiredRuntimeCapabilities(DheSupportedBase candidate,
            DheRuntimeIdentity buildIdentity)
        {
            if (candidate == null || buildIdentity == null ||
                !string.Equals(candidate.runtimeProtocol, buildIdentity.RuntimeProtocol,
                    StringComparison.Ordinal) ||
                !string.Equals(candidate.nativeRuntimeContract, buildIdentity.RuntimeContract,
                    StringComparison.Ordinal)) return false;
            string[] required = candidate.requiredRuntimeCapabilities ?? Array.Empty<string>();
            if (required.Length == 0 || required.Any(string.IsNullOrWhiteSpace) ||
                required.Distinct(StringComparer.Ordinal).Count() != required.Length) return false;
            return new HashSet<string>(buildIdentity.RuntimeCapabilities ?? Array.Empty<string>(),
                StringComparer.Ordinal).IsSupersetOf(required);
        }

        /// <summary>Initializes one current payload against this Player's embedded Base MV.</summary>
        private static bool InitializeFromResourceUpdateCore(IDheRuntimeAssetProvider provider,
            DheRuntimeIdentity buildIdentity, string manifestAssetPath, out string error,
            string runtimeAssetRoot = DefaultAssetRoot, bool enableValidationProbes = false)
        {
            error = string.Empty;
            if (!TryValidateResourceUpdate(provider, buildIdentity, manifestAssetPath,
                    out error)) return false;
            try
            {
                ResourceUpdateManifest manifest = JsonUtility.FromJson<ResourceUpdateManifest>(
                    provider.LoadText(manifestAssetPath));
                if (manifest == null || manifest.schemaVersion != 1 ||
                    !string.Equals(manifest.format, "hybridclr.dhe-resource-update.json", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(manifest.runtimePlan))
                {
                    error = "DHE resource update manifest is invalid.";
                    return false;
                }
                string directory = Path.GetDirectoryName(manifestAssetPath.Replace('/', Path.DirectorySeparatorChar))?.Replace(Path.DirectorySeparatorChar, '/') ?? string.Empty;
                string planPath = manifest.runtimePlan.Replace('\\', '/');
                if (!planPath.StartsWith("Assets/", StringComparison.Ordinal) && !string.IsNullOrEmpty(directory))
                    planPath = directory.TrimEnd('/') + "/" + planPath;
                if (planPath.Split('/').Any(segment => segment == "." || segment == ".."))
                {
                    error = "DHE resource update runtime plan path is unsafe.";
                    return false;
                }
                if (!provider.Exists(planPath))
                {
                    error = "DHE resource update runtime plan was not found: " + planPath;
                    return false;
                }
                byte[] runtimePlanBytes = provider.LoadBytes(planPath);
                if (!string.Equals(Sha256Hex(runtimePlanBytes), manifest.runtimePlanSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    error = "DHE resource manifest runtime plan hash mismatch.";
                    return false;
                }
                RuntimePlan updatePlan = JsonUtility.FromJson<RuntimePlan>(
                    System.Text.Encoding.UTF8.GetString(runtimePlanBytes));
                if (updatePlan == null || updatePlan.schemaVersion != 1 ||
                    !string.Equals(updatePlan.currentAssemblySetSha256,
                        manifest.currentAssemblySetSha256, StringComparison.OrdinalIgnoreCase))
                {
                    error = "DHE resource manifest and runtime plan current sets do not match.";
                    return false;
                }
                DheSupportedBase selectedBase = manifest.supportedBases.Single(item =>
                    MatchesPlayerBase(item, buildIdentity));
                DheBaseSelection[] planSelections = updatePlan.baseSelections ??
                    Array.Empty<DheBaseSelection>();
                DheBaseSelection[] matchingSelections = planSelections.Where(item => item != null &&
                    string.Equals(item.baseId, buildIdentity.BaseId,
                        StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matchingSelections.Length != 1 ||
                    !CanonicalAssemblyModes(matchingSelections[0].assemblyModes).SequenceEqual(
                        CanonicalAssemblyModes(selectedBase.assemblyModes), StringComparer.Ordinal) ||
                    !CanonicalFrozenSources(matchingSelections[0].frozenAotSources, buildIdentity).SequenceEqual(
                        CanonicalFrozenSources(selectedBase.frozenAotSources, buildIdentity), StringComparer.Ordinal) ||
                    !string.Equals(matchingSelections[0].aotMetadataSetId,
                        selectedBase.aotMetadataSetId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(matchingSelections[0].payloadVariantId ?? "default",
                        selectedBase.payloadVariantId ?? "default", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(matchingSelections[0].currentAssemblySetSha256,
                        selectedBase.currentAssemblySetSha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(selectedBase.aotMetadataSetId,
                        buildIdentity.AotMetadataSetId, StringComparison.OrdinalIgnoreCase))
                {
                    error = "DHE resource runtime plan selected the wrong AOT metadata set.";
                    return false;
                }
                string manifestRoot = NormalizeAssetRoot(manifest.runtimeAssetRoot);
                if (!string.Equals(NormalizeAssetRoot(runtimeAssetRoot), manifestRoot,
                        StringComparison.OrdinalIgnoreCase))
                {
                    error = "DHE requested runtime asset root does not match the resource manifest.";
                    return false;
                }
                return InitializeCore(provider, buildIdentity, out error, planPath, manifestRoot,
                    enableValidationProbes);
            }
            catch (Exception exception) { error = exception.Message; return false; }
        }

        private static Dictionary<string, DheAotAnalysisSource> ReadFrozenSnapshot(
            IDheRuntimeAssetProvider provider, DheRuntimeIdentity selectedIdentity)
        {
            if (selectedIdentity.EngineWorkflow != "Unity2022Fgs" || !IsSha256(selectedIdentity.BaseId) ||
                !IsSha256(selectedIdentity.AotAnalysisSnapshotSha256) ||
                !(selectedIdentity.RuntimeCapabilities ?? Array.Empty<string>()).Contains("frozen-aot-snapshot-source-binding-v1"))
                throw new InvalidDataException("Player does not support authenticated frozen AOT sources.");
            string path = NormalizeAssetRoot(selectedIdentity.RuntimeAssetRoot) + "payload/frozen-aot/" +
                selectedIdentity.BaseId.ToLowerInvariant() + "/snapshot.json";
            byte[] bytes = provider.LoadBytes(ValidateAssetPath(path, "frozen AOT snapshot"));
            if (!string.Equals(Sha256Hex(bytes), selectedIdentity.AotAnalysisSnapshotSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("DHE frozen AOT snapshot is not the manifest embedded in this Base identity.");
            var snapshot = JsonUtility.FromJson<DheAotAnalysisManifest>(new System.Text.UTF8Encoding(false, true).GetString(bytes));
            if (snapshot == null || snapshot.schemaVersion != 1 || snapshot.format != "hybridclr.dhe-aot-analysis-snapshot.json" ||
                snapshot.normalization != "dhe-aot-analysis-normalization-v1" || snapshot.assemblies == null ||
                string.IsNullOrWhiteSpace(snapshot.identityType) || string.IsNullOrWhiteSpace(snapshot.identityAssembly))
                throw new InvalidDataException("DHE frozen AOT snapshot format is invalid.");
            var sources = new Dictionary<string, DheAotAnalysisSource>(StringComparer.Ordinal);
            foreach (DheAotAnalysisSource row in snapshot.assemblies)
            {
                string name = row?.assemblyName;
                if (string.IsNullOrWhiteSpace(name) || name == "." || name == ".." ||
                    name.IndexOfAny(new[] { '/', '\\', ':', '\r', '\n' }) >= 0 ||
                    NormalizeAssemblyName(name) != name || !sources.TryAdd(name, row) ||
                    row.file != "assemblies/" + name + ".dll" || !IsSha256(row.sha256) || !IsSha256(row.normalizedSha256))
                    throw new InvalidDataException("DHE frozen AOT snapshot assembly record is invalid.");
            }
            if (!new HashSet<string>(selectedIdentity.AotAssemblyNames ?? Array.Empty<string>(), StringComparer.Ordinal).SetEquals(sources.Keys) ||
                !new HashSet<string>(selectedIdentity.AssemblyNames ?? Array.Empty<string>(), StringComparer.Ordinal)
                    .SetEquals(sources.Values.Where(row => row.dhe).Select(row => row.assemblyName)) ||
                !sources.TryGetValue(snapshot.identityAssembly, out DheAotAnalysisSource owner) || owner.dhe)
                throw new InvalidDataException("DHE frozen AOT snapshot inventory does not match this Base.");
            return sources;
        }

        private static string[] CanonicalFrozenSources(DheFrozenAotSource[] sources, DheRuntimeIdentity selectedIdentity)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return (sources ?? Array.Empty<DheFrozenAotSource>()).Select(source =>
            {
                string name = NormalizeAssemblyName(source?.assemblyName);
                if (string.IsNullOrWhiteSpace(name) || !names.Add(name) || source.sourceKind != "frozen-base-aot" ||
                    !IsSha256(source.sourceSha256) || source.excludedBaseTypeTokens == null)
                    throw new InvalidDataException("DHE frozen source identity is invalid: " + name);
                var plan = new DheExecutionPlan { schemaVersion = 1, assemblyName = name,
                    baseMetaVersionSha256 = source.baseMetaVersionSha256, currentMetaVersionSha256 = source.baseMetaVersionSha256,
                    currentStorageTypeTokens = source.currentStorageTypeTokens, currentExecutionMethodTokens = source.currentExecutionMethodTokens,
                    currentStorageTypeTokenCount = source.currentStorageTypeTokens?.Length ?? 0,
                    currentExecutionMethodTokenCount = source.currentExecutionMethodTokens?.Length ?? 0 };
                string binding = plan.CanonicalBinding();
                string Tokens(uint[] values, uint table, uint minimum)
                {
                    uint previous = 0;
                    foreach (uint token in values)
                    {
                        if ((token >> 24) != table || (token & 0xffffffu) <= minimum || token <= previous)
                            throw new InvalidDataException("DHE frozen source token selection is invalid: " + name);
                        previous = token;
                    }
                    return string.Join(",", values.Select(token => token.ToString("X8")));
                }
                uint[] conditional = source.genericContextMethodTokens ?? Array.Empty<uint>();
                if (conditional.Any(token => !source.currentExecutionMethodTokens.Contains(token)))
                    throw new InvalidDataException("DHE conditional method is not in the frozen execution selection: " + name);
                return binding + "|" + source.sourceSha256.ToUpperInvariant() + "|" +
                    ValidateFrozenAssetPath(source.source, selectedIdentity, name, ".dll.bytes") + "|" +
                    ValidateFrozenAssetPath(source.baseMetaVersion, selectedIdentity, name, ".mv.bytes") + "|" +
                    Tokens(source.excludedBaseTypeTokens, 2, 1) + "|" + Tokens(conditional, 6, 0);
            }).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        public static bool IsDheAssembly(string assemblyName)
        {
            return enabled && Artifacts.TryGetValue(NormalizeAssemblyName(assemblyName),
                out DheAssemblyArtifact artifact) && IsDifferentialMode(artifact.ExecutionMode);
        }

        public static string GetAssemblyExecutionMode(string assemblyName)
        {
            string normalizedName = NormalizeAssemblyName(assemblyName);
            return enabled && Artifacts.TryGetValue(normalizedName,
                    out DheAssemblyArtifact artifact)
                ? artifact.ExecutionMode : string.Empty;
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

        /// <summary>
        /// Validates every planned supplemental metadata payload before loading any of them.
        /// Paths come from the authenticated runtime plan, so resource-only updates can place
        /// metadata below their own payload directory.
        /// </summary>
        public static bool LoadAotMetadataImages(IDheRuntimeAssetProvider provider,
            HomologousImageMode mode, out LoadImageErrorCode code, out string error)
        {
            code = LoadImageErrorCode.OK;
            error = string.Empty;
            if (!enabled)
            {
                error = "DHE runtime must be initialized before loading AOT metadata.";
                return false;
            }
            if (provider == null)
            {
                error = "DHE runtime asset provider is null.";
                return false;
            }

            try
            {
                var payloads = new List<KeyValuePair<string, byte[]>>();
                foreach (string assemblyName in AotMetadataPaths.Keys.OrderBy(name => name,
                             StringComparer.Ordinal))
                {
                    byte[] bytes = provider.LoadBytes(AotMetadataPaths[assemblyName]);
                    if (!ValidateAotMetadata(assemblyName, bytes, out error)) return false;
                    payloads.Add(new KeyValuePair<string, byte[]>(assemblyName, bytes));
                }

                foreach (KeyValuePair<string, byte[]> payload in payloads)
                {
                    code = RuntimeApi.LoadMetadataForAOTAssembly(payload.Value, mode);
                    if (code != LoadImageErrorCode.OK &&
                        code != LoadImageErrorCode.HOMOLOGOUS_ASSEMBLY_HAS_LOADED)
                    {
                        error = "DHE failed to load AOT metadata '" + payload.Key + "': " + code;
                        return false;
                    }
                }
                code = LoadImageErrorCode.OK;
                return true;
            }
            catch (Exception exception)
            {
                code = LoadImageErrorCode.BAD_IMAGE;
                error = exception.Message;
                return false;
            }
        }

        private static bool LoadAssemblyImageCore(string assemblyName, byte[] currentDll,
            out LoadImageErrorCode code, out string error)
        {
            error = string.Empty;
            if (Artifacts.Count != 1 || Artifacts.Values.Count(IsDifferentialArtifact) != 1 || LoadedMutableAssemblies.Count != 0)
            {
                code = LoadImageErrorCode.DHE_MV_REGISTRATION_FAILED;
                error = "DHE plans with multiple assemblies must use LoadAssemblyImages " +
                    "to preserve atomic registration.";
                return false;
            }
            if (FrozenAotSources.Count != 0)
                return LoadAssemblyImagesCore(new[] { assemblyName }, new[] { currentDll }, out code, out error);
            string normalizedName = NormalizeAssemblyName(assemblyName);
            if (!Artifacts.TryGetValue(normalizedName, out DheAssemblyArtifact artifact) ||
                !IsDifferentialArtifact(artifact))
            {
                code = LoadImageErrorCode.DHE_MV_ASSEMBLY_NOT_FOUND;
                error = "DHE runtime plan does not contain assembly: " + normalizedName;
                return false;
            }

            try
            {
                if (artifact.ExpectedCurrentSha256 != null &&
                    !string.Equals(Sha256Hex(currentDll ?? Array.Empty<byte>()),
                        artifact.ExpectedCurrentSha256, StringComparison.OrdinalIgnoreCase))
                {
                    code = LoadImageErrorCode.DHE_MV_CURRENT_HASH_MISMATCH;
                    error = "DHE current assembly does not match the runtime plan: " + normalizedName;
                    return false;
                }
                // The transaction probe must target an assembly image before
                // the normal valid MV registration has happened. Once the
                // image is registered, the native API correctly rejects a
                // second registration as already loaded, which would make a
                // post-load smoke probe test the wrong state transition.
                if (validationProbesEnabled && !transactionProbeAttempted &&
                    HasTransactionProbeCandidate(artifact))
                {
                    if (!TryRunTransactionProbe(normalizedName, artifact, currentDll,
                            out code, out error))
                    {
                        return false;
                    }
                    return true;
                }

                code = LoadNativeImages(new[] { currentDll }, new[] { artifact.BaseMetaVersion },
                    new[] { artifact.MetaVersion }, new[] { artifact });
                if (code == LoadImageErrorCode.OK)
                {
                    artifact.Current = currentDll == null ? null : (byte[])currentDll.Clone();
                    LoadedAssemblies.Add(normalizedName);
                    LoadedMutableAssemblies.Add(normalizedName);
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

        /// <summary>
        /// Loads the complete Current payload, including new interpreter-only
        /// assemblies and any captured frozen dependencies. Call before entering
        /// application code; module initializers run after metadata registration.
        /// </summary>
        private static bool LoadCurrentAssemblyImagesCore(string[] assemblyNames, byte[][] currentDlls,
            out LoadImageErrorCode code, out string error)
        {
            code = LoadImageErrorCode.DHE_MV_REGISTRATION_FAILED; error = string.Empty;
            if (!enabled || assemblyNames == null || currentDlls == null || assemblyNames.Length != currentDlls.Length ||
                assemblyNames.Length != Artifacts.Count || LoadedAssemblies.Count != 0)
            { error = "Current batch must contain the complete unloaded resource plan."; return false; }
            var inputs = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < assemblyNames.Length; ++index)
            {
                string name = NormalizeAssemblyName(assemblyNames[index]); byte[] bytes = currentDlls[index];
                if (string.IsNullOrEmpty(name) || inputs.ContainsKey(name) || !Artifacts.TryGetValue(name, out var artifact) || bytes == null ||
                    !string.Equals(Sha256Hex(bytes), artifact.ExpectedCurrentSha256, StringComparison.OrdinalIgnoreCase))
                { code = LoadImageErrorCode.DHE_MV_CURRENT_HASH_MISMATCH; error = "Current batch assembly missing, duplicate, or hash mismatch: " + name; return false; }
                inputs.Add(name, bytes);
            }
            string[] added = InterpreterOnlyAssemblyNames;
            if (added.Length == 0) return LoadAssemblyImagesCore(assemblyNames, currentDlls, out code, out error);
            if (!(identity.RuntimeCapabilities ?? Array.Empty<string>()).Contains("mixed-interpreter-source-batch-v1"))
            { error = "Base lacks mixed-interpreter-source-batch-v1."; return false; }
            try
            {
                string[] differential = DifferentialAssemblyNames;
                string[] frozen = FrozenAotSources.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
                var artifacts = differential.Select(name => Artifacts[name]).ToArray();
                var dlls = frozen.Select(name => FrozenAotSourceBytes[name]).Concat(differential.Select(name => inputs[name])).ToArray();
                var before = frozen.Select(name => FrozenAotBaseMetaVersionBytes[name]).Concat(artifacts.Select(row => row.BaseMetaVersion)).ToArray();
                var after = frozen.Select(name => FrozenAotBaseMetaVersionBytes[name]).Concat(artifacts.Select(row => row.MetaVersion)).ToArray();
                var types = frozen.Select(name => FrozenAotSources[name].currentStorageTypeTokens).Concat(artifacts.Select(row => row.ExecutionPlan?.currentStorageTypeTokens)).ToArray();
                var methods = frozen.Select(name => FrozenAotSources[name].currentExecutionMethodTokens).Concat(artifacts.Select(row => row.ExecutionPlan?.currentExecutionMethodTokens)).ToArray();
                var kinds = frozen.Select(_ => 1).Concat(differential.Select(_ => 0)).ToArray();
                var excluded = frozen.Select(name => FrozenAotSources[name].excludedBaseTypeTokens).Concat(differential.Select(_ => Array.Empty<uint>())).ToArray();
                var conditional = frozen.Select(name => FrozenAotSources[name].genericContextMethodTokens ?? Array.Empty<uint>())
                    .Concat(artifacts.Select(row => row.ExecutionPlan?.currentGenericContextMethodTokens ?? Array.Empty<uint>())).ToArray();
                code = LoadTrackedNativeBatch(dlls, before, after, types, methods, kinds, excluded, conditional,
                    added.Select(name => inputs[name]).ToArray());
                if (code != LoadImageErrorCode.OK) { error = "DHE mixed source batch returned " + code + "."; return false; }
                foreach (string name in frozen.Concat(differential).Concat(added)) LoadedAssemblies.Add(name);
                foreach (string name in differential) LoadedMutableAssemblies.Add(name);
                foreach (var pair in inputs) Artifacts[pair.Key].Current = (byte[])pair.Value.Clone();
                foreach (string name in frozen) FrozenAotSources.Remove(name);
                return true;
            }
            catch (Exception exception) { error = exception.Message; return false; }
        }

        private static bool LoadAssemblyImagesCore(string[] assemblyNames, byte[][] currentDlls,
            out LoadImageErrorCode code, out string error)
        {
            error = string.Empty;
            code = LoadImageErrorCode.DHE_MV_REGISTRATION_FAILED;
            if (InterpreterOnlyAssemblyNames.Length != 0)
            { error = "Use LoadCurrentAssemblyImages for a plan containing new interpreter assemblies."; return false; }
            if (!enabled || assemblyNames == null || currentDlls == null ||
                assemblyNames.Length != currentDlls.Length ||
                assemblyNames.Length != Artifacts.Values.Count(IsDifferentialArtifact) ||
                LoadedMutableAssemblies.Count != 0)
            {
                error = "DHE batch load must contain the complete unloaded runtime plan.";
                return false;
            }
            bool hasFrozenSources = FrozenAotSources.Count != 0;
            if (assemblyNames.Length == 0 && !hasFrozenSources)
            {
                code = LoadImageErrorCode.OK;
                return true;
            }
            if (hasFrozenSources && LoadedAssemblies.Count != 0)
            {
                error = "DHE frozen and mutable sources must be loaded in one initial transaction.";
                return false;
            }

            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var artifacts = new DheAssemblyArtifact[assemblyNames.Length];
                var normalizedNames = new string[assemblyNames.Length];
                var baseMetaVersions = new byte[assemblyNames.Length][];
                var currentMetaVersions = new byte[assemblyNames.Length][];
                for (int index = 0; index < assemblyNames.Length; index++)
                {
                    string name = NormalizeAssemblyName(assemblyNames[index]);
                    byte[] dll = currentDlls[index];
                    if (string.IsNullOrWhiteSpace(name) || !seen.Add(name) ||
                        !Artifacts.TryGetValue(name, out DheAssemblyArtifact artifact) ||
                        !IsDifferentialArtifact(artifact) ||
                        dll == null ||
                        !string.Equals(Sha256Hex(dll), artifact.ExpectedCurrentSha256,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        code = LoadImageErrorCode.DHE_MV_CURRENT_HASH_MISMATCH;
                        error = "DHE batch assembly is missing, duplicated, or has the wrong hash: " +
                            name;
                        return false;
                    }
                    normalizedNames[index] = name;
                    artifacts[index] = artifact;
                    baseMetaVersions[index] = artifact.BaseMetaVersion;
                    currentMetaVersions[index] = artifact.MetaVersion;
                }

                if (hasFrozenSources)
                {
                    var frozenNames = FrozenAotSources.Keys.OrderBy(name => name,
                        StringComparer.OrdinalIgnoreCase).ToArray();
                    var allNames = frozenNames.Concat(normalizedNames).ToArray();
                    if (allNames.Length != allNames.Distinct(StringComparer.OrdinalIgnoreCase).Count())
                    {
                        error = "DHE frozen source assembly overlaps mutable payload: " +
                            allNames.GroupBy(name => name, StringComparer.OrdinalIgnoreCase).First(group => group.Count() > 1).Key;
                        return false;
                    }
                    var allDlls = frozenNames.Select(name => FrozenAotSourceBytes[name]).Concat(currentDlls).ToArray();
                    var allBase = frozenNames.Select(name => FrozenAotBaseMetaVersionBytes[name]).Concat(baseMetaVersions).ToArray();
                    var allCurrent = frozenNames.Select(name => FrozenAotBaseMetaVersionBytes[name]).Concat(currentMetaVersions).ToArray();
                    var allTypes = frozenNames.Select(name => FrozenAotSources[name].currentStorageTypeTokens).Concat(
                        artifacts.Select(artifact => artifact.ExecutionPlan?.currentStorageTypeTokens ?? Array.Empty<uint>())).ToArray();
                    var allMethods = frozenNames.Select(name => FrozenAotSources[name].currentExecutionMethodTokens).Concat(
                        artifacts.Select(artifact => artifact.ExecutionPlan?.currentExecutionMethodTokens ?? Array.Empty<uint>())).ToArray();
                    var kinds = frozenNames.Select(_ => 1).Concat(normalizedNames.Select(_ => 0)).ToArray();
                    var excluded = frozenNames.Select(name => FrozenAotSources[name].excludedBaseTypeTokens).Concat(
                        normalizedNames.Select(_ => Array.Empty<uint>())).ToArray();
                    var conditional = frozenNames.Select(name => FrozenAotSources[name].genericContextMethodTokens ?? Array.Empty<uint>())
                        .Concat(artifacts.Select(row => row.ExecutionPlan?.currentGenericContextMethodTokens ?? Array.Empty<uint>())).ToArray();
                    code = LoadTrackedNativeBatch(allDlls, allBase, allCurrent, allTypes, allMethods,
                        kinds, excluded, conditional, Array.Empty<byte[]>());
                    if (code != LoadImageErrorCode.OK)
                    {
                        error = "DHE frozen/mutable atomic registration returned " + code + ".";
                        return false;
                    }
                    foreach (string name in allNames) LoadedAssemblies.Add(name);
                    for (int index = 0; index < artifacts.Length; ++index)
                        artifacts[index].Current = (byte[])currentDlls[index].Clone();
                    foreach (string name in normalizedNames) LoadedMutableAssemblies.Add(name);
                    foreach (string name in frozenNames) FrozenAotSources.Remove(name);
                    return true;
                }

                if (validationProbesEnabled && !transactionProbeAttempted)
                {
                    int probeIndex = Array.FindIndex(artifacts, HasTransactionProbeCandidate);
                    if (probeIndex >= 0)
                    {
                        transactionProbeAttempted = true;
                        transactionAssemblyName = normalizedNames[probeIndex];
                        byte[][] invalidBaseMetaVersions =
                            (byte[][])baseMetaVersions.Clone();
                        invalidBaseMetaVersions[probeIndex] = CreateInvalidBaseMetaVersion(
                            artifacts[probeIndex].BaseMetaVersion,
                            artifacts[probeIndex].MetaVersion);
                        transactionFailureCode =
                            LoadNativeImages(currentDlls, invalidBaseMetaVersions, currentMetaVersions, artifacts);
                        if (transactionFailureCode !=
                            LoadImageErrorCode.DHE_MV_REGISTRATION_FAILED)
                        {
                            code = transactionFailureCode;
                            error = "DHE atomic batch transaction probe returned " + code + ".";
                            return false;
                        }
                    }
                }

                code = LoadNativeImages(currentDlls, baseMetaVersions, currentMetaVersions, artifacts);
                if (code != LoadImageErrorCode.OK)
                {
                    error = "DHE atomic batch runtime returned " + code + ".";
                    return false;
                }
                for (int index = 0; index < normalizedNames.Length; index++)
                {
                    artifacts[index].Current = (byte[])currentDlls[index].Clone();
                    LoadedAssemblies.Add(normalizedNames[index]);
                    LoadedMutableAssemblies.Add(normalizedNames[index]);
                }
                if (transactionProbeAttempted)
                {
                    transactionRetryValidated = true;
                }
                return true;
            }
            catch (Exception exception)
            {
                code = LoadImageErrorCode.DHE_MV_REGISTRATION_FAILED;
                error = exception.Message;
                return false;
            }
        }

        /// <summary>
        /// Loads a current assembly that is absent from the selected Base via
        /// the ordinary HybridCLR interpreter path. The plan still authenticates
        /// its bytes, but no Base MetaVersion or native DHE registration exists
        /// for this assembly.
        /// </summary>
        private static bool LoadInterpreterAssemblyImageCore(string assemblyName, byte[] currentDll,
            out Assembly assembly, out LoadImageErrorCode code, out string error)
        {
            assembly = null;
            code = LoadImageErrorCode.DHE_MV_REGISTRATION_FAILED;
            error = string.Empty;
            string normalizedName = NormalizeAssemblyName(assemblyName);
            if (!enabled || !Artifacts.TryGetValue(normalizedName,
                    out DheAssemblyArtifact artifact) ||
                !string.Equals(artifact.ExecutionMode, "interpreter-only", StringComparison.Ordinal))
            {
                error = "DHE runtime plan does not mark assembly as interpreter-only: " + normalizedName;
                return false;
            }
            if (currentDll == null || !string.Equals(Sha256Hex(currentDll),
                    artifact.ExpectedCurrentSha256, StringComparison.OrdinalIgnoreCase))
            {
                code = LoadImageErrorCode.DHE_MV_CURRENT_HASH_MISMATCH;
                error = "Interpreter-only assembly does not match the runtime plan: " + normalizedName;
                return false;
            }
            try
            {
                nativeLoadPhase = 1;
                System.Threading.Volatile.Write(ref nativeTouched, 1);
                assembly = Assembly.Load(currentDll);
                nativeLoadPhase = 3;
                System.Threading.Volatile.Write(ref metadataCommitted, 1);
                if (assembly == null || !string.Equals(assembly.GetName().Name,
                        normalizedName, StringComparison.OrdinalIgnoreCase))
                {
                    assembly = null;
                    code = LoadImageErrorCode.BAD_IMAGE;
                    error = "Interpreter-only assembly identity mismatch: " + normalizedName;
                    return false;
                }
                artifact.Current = (byte[])currentDll.Clone();
                LoadedAssemblies.Add(normalizedName);
                code = LoadImageErrorCode.OK;
                nativeLoadPhase = 4;
                return true;
            }
            catch (Exception exception)
            {
                code = LoadImageErrorCode.BAD_IMAGE;
                error = exception.Message;
                return false;
            }
        }

        private static bool TryRunTransactionProbe(string assemblyName,
            DheAssemblyArtifact artifact, byte[] currentDll, out LoadImageErrorCode code,
            out string error)
        {
            transactionProbeAttempted = true;
            transactionAssemblyName = assemblyName;
            error = string.Empty;
            try
            {
                transactionFailureCode = LoadNativeImages(new[] { currentDll },
                    new[] { CreateInvalidBaseMetaVersion(artifact.BaseMetaVersion, artifact.MetaVersion) },
                    new[] { artifact.MetaVersion }, new[] { artifact });
                if (transactionFailureCode != LoadImageErrorCode.DHE_MV_REGISTRATION_FAILED)
                {
                    code = transactionFailureCode;
                    error = "DHE transaction probe returned " + transactionFailureCode +
                        " for " + assemblyName;
                    return false;
                }

                code = LoadNativeImages(new[] { currentDll }, new[] { artifact.BaseMetaVersion },
                    new[] { artifact.MetaVersion }, new[] { artifact });
                if (code != LoadImageErrorCode.OK)
                {
                    error = "DHE transaction probe retry returned " + code +
                        " for " + assemblyName;
                    return false;
                }

                artifact.Current = currentDll == null ? null : (byte[])currentDll.Clone();
                LoadedAssemblies.Add(assemblyName);
                transactionRetryValidated = true;
                return true;
            }
            catch (Exception exception)
            {
                code = LoadImageErrorCode.DHE_MV_REGISTRATION_FAILED;
                error = exception.Message;
                return false;
            }
        }

        private static bool RunTransactionProbeCore(out string error)
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
                if (!HasTransactionProbeCandidate(artifact))
                {
                    continue;
                }
                if (artifact.Current == null)
                {
                    error = "DHE transaction probe requires a loaded assembly: " + assemblyName;
                    return false;
                }

                return TryRunTransactionProbe(assemblyName, artifact, artifact.Current,
                    out _, out error);
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

        private static DheAotMetadataRecord[] SelectAotMetadata(RuntimePlan plan,
            DheRuntimeIdentity buildIdentity)
        {
            DheAotMetadataSet[] sets = plan.aotMetadataSets ?? Array.Empty<DheAotMetadataSet>();
            DheBaseSelection[] selections = plan.baseSelections ?? Array.Empty<DheBaseSelection>();
            if (string.Equals(plan.selection, "embedded-base-metaversion",
                    StringComparison.Ordinal))
            {
                if (!IsSha256(plan.aotMetadataSetId) ||
                    !string.Equals(plan.aotMetadataSetId, buildIdentity.AotMetadataSetId,
                        StringComparison.OrdinalIgnoreCase) || sets.Length != 0 || selections.Length != 0)
                    throw new InvalidDataException(
                        "DHE Base runtime plan AOT metadata identity is invalid.");
                return plan.aotMetadata ?? Array.Empty<DheAotMetadataRecord>();
            }
            if (!string.Equals(plan.selection,
                    "embedded-base-metaversion-and-aot-metadata-set", StringComparison.Ordinal) ||
                (plan.aotMetadata != null && plan.aotMetadata.Length != 0) || sets.Length == 0 ||
                selections.Length == 0)
                throw new InvalidDataException("DHE runtime plan has an invalid selection mode.");

            var setsById = new Dictionary<string, DheAotMetadataSet>(StringComparer.OrdinalIgnoreCase);
            foreach (DheAotMetadataSet set in sets)
            {
                if (set == null || !IsSha256(set.aotMetadataSetId) ||
                    !setsById.TryAdd(set.aotMetadataSetId, set) || set.assemblies == null)
                    throw new InvalidDataException("DHE runtime plan contains an invalid AOT metadata set.");
            }
            var selectionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DheBaseSelection selection in selections)
            {
                if (selection == null || !IsSha256(selection.baseId) ||
                    !IsSha256(selection.aotMetadataSetId) || !selectionIds.Add(selection.baseId) ||
                    !setsById.ContainsKey(selection.aotMetadataSetId))
                    throw new InvalidDataException("DHE runtime plan contains an invalid Base selection.");
            }
            DheBaseSelection[] matches = selections.Where(selection => selection != null &&
                string.Equals(selection.baseId, buildIdentity.BaseId,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 ||
                !string.Equals(matches[0].aotMetadataSetId, buildIdentity.AotMetadataSetId,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "DHE runtime plan does not select this Player's AOT metadata set.");
            return setsById[matches[0].aotMetadataSetId].assemblies;
        }

        private static DheAssemblyRecord[] SelectPayloadAssemblies(RuntimePlan plan,
            DheRuntimeIdentity buildIdentity)
        {
            DhePayloadVariant[] variants = plan.payloadVariants ?? Array.Empty<DhePayloadVariant>();
            if (variants.Length == 0)
            {
                if (plan.assemblies == null || plan.assemblies.Length == 0)
                    throw new InvalidDataException("DHE runtime plan has no payload assemblies.");
                return plan.assemblies;
            }
            var variantsById = new Dictionary<string, DhePayloadVariant>(
                StringComparer.OrdinalIgnoreCase);
            foreach (DhePayloadVariant variant in variants)
            {
                if (variant == null || string.IsNullOrWhiteSpace(variant.variantId) ||
                    !IsPayloadVariantId(variant.variantId) ||
                    !IsSha256(variant.currentAssemblySetSha256) ||
                    variant.assemblies == null || variant.assemblies.Length == 0 ||
                    !variantsById.TryAdd(variant.variantId, variant))
                    throw new InvalidDataException("DHE runtime plan contains an invalid payload variant.");
            }
            DheBaseSelection[] selections = plan.baseSelections ?? Array.Empty<DheBaseSelection>();
            DheBaseSelection[] matches = selections.Where(selection => selection != null &&
                string.Equals(selection.baseId, buildIdentity.BaseId,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException("DHE runtime plan does not select a payload variant for this Base.");
            string variantId = string.IsNullOrWhiteSpace(matches[0].payloadVariantId)
                ? "default" : matches[0].payloadVariantId;
            if (!variantsById.TryGetValue(variantId, out DhePayloadVariant selected) ||
                !string.Equals(matches[0].currentAssemblySetSha256,
                    selected.currentAssemblySetSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("DHE runtime plan selected payload variant is not bound to this Base.");
            if (!IsSha256(plan.payloadVariantSetSha256) ||
                !string.Equals(plan.payloadVariantSetSha256,
                    ComputePayloadVariantSetHash(variants), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("DHE runtime plan payload variant set hash is invalid.");
            if (!string.Equals(plan.currentAssemblySetSha256,
                    variantsById.TryGetValue("default", out DhePayloadVariant defaultVariant)
                        ? defaultVariant.currentAssemblySetSha256 : plan.currentAssemblySetSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("DHE runtime plan default payload hash is invalid.");
            return selected.assemblies;
        }

        private static void ApplySelectedAssemblyModes(RuntimePlan plan,
            DheRuntimeIdentity buildIdentity, DheAssemblyRecord[] assemblies)
        {
            DheBaseSelection[] selections = plan.baseSelections ?? Array.Empty<DheBaseSelection>();
            if (selections.Length == 0)
            {
                foreach (DheAssemblyRecord record in assemblies)
                    record.executionMode = NormalizeExecutionMode(record.executionMode);
                return;
            }

            DheBaseSelection[] matches = selections.Where(selection => selection != null &&
                string.Equals(selection.baseId, buildIdentity.BaseId,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException("DHE runtime plan does not select assembly modes for this Base.");
            DheAssemblyMode[] modes = matches[0].assemblyModes ?? Array.Empty<DheAssemblyMode>();
            if (modes.Length == 0)
            {
                foreach (DheAssemblyRecord record in assemblies)
                    record.executionMode = NormalizeExecutionMode(record.executionMode);
                return;
            }
            CanonicalAssemblyModes(modes);
            var modesByName = new Dictionary<string, DheAssemblyMode>(StringComparer.OrdinalIgnoreCase);
            foreach (DheAssemblyMode mode in modes)
            {
                string name = NormalizeAssemblyName(mode?.assemblyName);
                string executionMode = NormalizeExecutionMode(mode?.executionMode);
                if (!modesByName.TryAdd(name, mode))
                    throw new InvalidDataException("DHE runtime plan contains duplicate assembly mode: " + name);
            }
            if (modesByName.Count != assemblies.Length)
                throw new InvalidDataException("DHE runtime plan assembly modes do not match the payload.");
            foreach (DheAssemblyRecord record in assemblies)
            {
                string name = NormalizeAssemblyName(record?.assemblyName);
                if (!modesByName.TryGetValue(name, out DheAssemblyMode mode))
                    throw new InvalidDataException("DHE runtime plan has no assembly mode: " + name);
                record.executionMode = NormalizeExecutionMode(mode.executionMode);
                record.executionPlan = SelectedExecutionPlan(mode);
            }
        }

        private static DheExecutionPlan SelectedExecutionPlan(DheAssemblyMode mode)
        {
            // Unity JsonUtility materializes null inline objects as empty
            // instances. An optional array preserves absence unambiguously.
            var plans = mode?.executionPlans;
            if (plans == null || plans.Length == 0) return null;
            if (plans.Length != 1 || plans[0] == null)
                throw new InvalidDataException("DHE assembly must have zero or one execution plan.");
            return plans[0];
        }

        private static string[] CanonicalAssemblyModes(DheAssemblyMode[] modes)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return (modes ?? Array.Empty<DheAssemblyMode>()).Select(mode =>
            {
                string name = NormalizeAssemblyName(mode?.assemblyName);
                string executionMode = NormalizeExecutionMode(mode?.executionMode);
                DheExecutionPlan execution = SelectedExecutionPlan(mode);
                if (string.IsNullOrWhiteSpace(name) || !names.Add(name) ||
                    (execution != null && (!IsDifferentialMode(executionMode) || execution.assemblyName != name)))
                    throw new InvalidDataException("DHE assembly selection is invalid or duplicated.");
                return name.ToUpperInvariant() + "=" + executionMode + "|" + (execution?.CanonicalBinding() ?? "");
            }).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        private static LoadImageErrorCode LoadNativeImages(byte[][] dlls, byte[][] before, byte[][] after,
            DheAssemblyArtifact[] artifacts)
        {
            return LoadTrackedNativeBatch(dlls, before, after,
                artifacts.Select(artifact => artifact.ExecutionPlan?.currentStorageTypeTokens).ToArray(),
                artifacts.Select(artifact => artifact.ExecutionPlan?.currentExecutionMethodTokens).ToArray(),
                new int[dlls.Length], artifacts.Select(_ => Array.Empty<uint>()).ToArray(),
                artifacts.Select(artifact => artifact.ExecutionPlan?.currentGenericContextMethodTokens ?? Array.Empty<uint>()).ToArray(),
                Array.Empty<byte[]>());
        }

        private static string NormalizeExecutionMode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "dhe-differential";
            if (string.Equals(value, "dhe-differential", StringComparison.Ordinal) ||
                string.Equals(value, "interpreter-only", StringComparison.Ordinal)) return value;
            throw new InvalidDataException("DHE runtime plan contains an unknown assembly execution mode: " + value);
        }

        private static bool IsDifferentialMode(string value) =>
            string.Equals(value, "dhe-differential", StringComparison.Ordinal);

        private static bool IsDifferentialArtifact(DheAssemblyArtifact artifact) =>
            artifact != null && IsDifferentialMode(artifact.ExecutionMode);

        private static void SetSelectedPayloadIdentity(RuntimePlan plan,
            DheRuntimeIdentity buildIdentity)
        {
            DhePayloadVariant[] variants = plan.payloadVariants ?? Array.Empty<DhePayloadVariant>();
            if (variants.Length == 0)
            {
                selectedPayloadVariantId = "default";
                selectedPayloadCurrentAssemblySetSha256 = plan.currentAssemblySetSha256 ?? string.Empty;
                return;
            }

            DheBaseSelection[] matches = (plan.baseSelections ?? Array.Empty<DheBaseSelection>())
                .Where(selection => selection != null && string.Equals(selection.baseId,
                    buildIdentity.BaseId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException("DHE runtime plan does not select a payload identity for this Base.");
            string variantId = string.IsNullOrWhiteSpace(matches[0].payloadVariantId)
                ? "default" : matches[0].payloadVariantId;
            DhePayloadVariant[] selected = variants.Where(variant => variant != null &&
                string.Equals(variant.variantId, variantId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (selected.Length != 1 || !string.Equals(selected[0].currentAssemblySetSha256,
                    matches[0].currentAssemblySetSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("DHE runtime plan payload identity is not bound to this Base.");
            selectedPayloadVariantId = variantId;
            selectedPayloadCurrentAssemblySetSha256 = selected[0].currentAssemblySetSha256;
        }

        private static string ComputePayloadVariantSetHash(DhePayloadVariant[] variants)
        {
            using (SHA256 sha = SHA256.Create())
            {
                foreach (DhePayloadVariant variant in variants.OrderBy(item => item.variantId,
                             StringComparer.OrdinalIgnoreCase))
                {
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes((variant.variantId ?? string.Empty) +
                        "\n" + (variant.currentAssemblySetSha256 ?? string.Empty).ToLowerInvariant() + "\n");
                    sha.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return ToHex(sha.Hash);
            }
        }

        private static bool IsPayloadVariantId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return false;
            foreach (char character in value)
            {
                if (!(char.IsLetterOrDigit(character) || character == '-' ||
                      character == '_' || character == '.')) return false;
            }
            return true;
        }

        private static void ValidateEmbeddedIdentity()
        {
            if (identity.IdentityVersion != 1 || !string.Equals(identity.AotSnapshotKind,
                    "managed-assembly-plus-generated-cpp-v1", StringComparison.Ordinal) ||
                !string.Equals(identity.RuntimeProtocol, NativeRuntimeProtocol,
                    StringComparison.Ordinal) ||
                !string.Equals(identity.RuntimeContract, NativeRuntimeContract,
                    StringComparison.Ordinal) ||
                !new HashSet<string>(identity.RuntimeCapabilities ?? Array.Empty<string>(),
                    StringComparer.Ordinal).SetEquals(NativeRuntimeCapabilities))
            {
                throw new InvalidDataException(
                    "DHE Player build identity version or snapshot kind is invalid.");
            }
            if (!IsSha256(identity.BaseId) ||
                (identity.AotAnalysisSnapshotSha256 != null && !IsSha256(identity.AotAnalysisSnapshotSha256)) ||
                !IsSha256(identity.ManagedAssemblySetSha256) ||
                !TryValidateAotAssemblyInventory(identity.AotAssemblyNames,
                    identity.AotAssemblySetSha256) ||
                !IsSha256(identity.AotSnapshotSha256) ||
                !IsSha256(identity.BaseMetaVersionSetSha256) ||
                !IsSha256(identity.AotMetadataSetId) ||
                !IsSha256(identity.NativeGuardSourceSha256) ||
                !IsSha256(identity.NativeManifestSha256) ||
                string.IsNullOrWhiteSpace(identity.Target) ||
                !IsBuildConfiguration(identity.EngineWorkflow,
                    identity.Il2CppCodeGeneration) ||
                string.IsNullOrWhiteSpace(identity.RuntimeAssetRoot) ||
                string.IsNullOrWhiteSpace(identity.BaseMetaVersionAssetRoot) ||
                !string.Equals(identity.BaseId, ComputeBaseId(identity),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("DHE Player build identity is incomplete.");
            }
            int differentialCount = Artifacts.Values.Count(IsDifferentialArtifact);
            if (identity.AssemblyNames == null || identity.BaseMetaVersionHashes == null ||
                identity.AssemblyNames.Length != identity.BaseMetaVersionHashes.Length ||
                identity.AssemblyNames.Length != differentialCount || identity.AssemblyNames.Length == 0)
            {
                throw new InvalidDataException("DHE Player build identity is missing or empty.");
            }

            HashSet<string> identityNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> aotAssemblyNames = new HashSet<string>(
                identity.AotAssemblyNames, StringComparer.OrdinalIgnoreCase);
            List<KeyValuePair<string, byte[]>> baseMetaVersions =
                new List<KeyValuePair<string, byte[]>>();
            for (int index = 0; index < identity.AssemblyNames.Length; index++)
            {
                string assemblyName = NormalizeAssemblyName(identity.AssemblyNames[index]);
                if (string.IsNullOrWhiteSpace(assemblyName) || !identityNames.Add(assemblyName))
                {
                    throw new InvalidDataException(
                        "DHE Player build identity contains an empty or duplicate assembly.");
                }
                if (!aotAssemblyNames.Contains(assemblyName))
                {
                    throw new InvalidDataException(
                        "DHE Player identity assembly is absent from its AOT inventory: " +
                        assemblyName);
                }
                if (!Artifacts.TryGetValue(assemblyName, out DheAssemblyArtifact artifact) ||
                    !IsDifferentialArtifact(artifact) ||
                    artifact.BaseMetaVersion == null ||
                    !string.Equals(Sha256Hex(artifact.BaseMetaVersion),
                        identity.BaseMetaVersionHashes[index], StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "DHE Player build identity does not match Base MetaVersion: " + assemblyName);
                }
                baseMetaVersions.Add(new KeyValuePair<string, byte[]>(assemblyName,
                    artifact.BaseMetaVersion));
            }
            foreach (KeyValuePair<string, DheAssemblyArtifact> artifact in Artifacts.Where(pair =>
                         !IsDifferentialArtifact(pair.Value)))
            {
                if (aotAssemblyNames.Contains(artifact.Key))
                    throw new InvalidDataException(
                        "DHE interpreter-only assembly already exists in the Base AOT inventory: " +
                        artifact.Key);
            }
            if (!string.Equals(Sha256NamedByteSet(baseMetaVersions),
                    identity.BaseMetaVersionSetSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "DHE Player identity Base MetaVersion set hash does not match embedded assets.");
        }

        private static bool HasTransactionProbeCandidate(DheAssemblyArtifact artifact)
        {
            return IsDifferentialArtifact(artifact) && artifact.MetaVersion != null &&
                artifact.BaseMetaVersion != null &&
                FindChangedMetaVersionMethodTokenOffset(artifact.BaseMetaVersion,
                    artifact.MetaVersion) >= 0;
        }

        private static byte[] CreateInvalidBaseMetaVersion(byte[] baseMetaVersion,
            byte[] currentMetaVersion)
        {
            int tokenOffset = FindChangedMetaVersionMethodTokenOffset(baseMetaVersion,
                currentMetaVersion);
            if (tokenOffset < 0)
            {
                throw new InvalidDataException(
                    "DHE MetaVersion has no changed existing method for the transaction probe.");
            }
            byte[] invalid = (byte[])baseMetaVersion.Clone();
            Buffer.BlockCopy(BitConverter.GetBytes(0x0600ffffu), 0, invalid, tokenOffset, 4);
            return invalid;
        }

        private static int FindChangedMetaVersionMethodTokenOffset(byte[] baseMetaVersion,
            byte[] currentMetaVersion)
        {
            if (!TryGetMetaVersionMethodTable(baseMetaVersion, out int baseStart,
                    out int baseCount) ||
                !TryGetMetaVersionMethodTable(currentMetaVersion, out int currentStart,
                    out int currentCount))
            {
                return -1;
            }
            Dictionary<string, string> currentMethods = new Dictionary<string, string>(
                currentCount, StringComparer.Ordinal);
            for (int index = 0; index < currentCount; index++)
            {
                int offset = checked(currentStart + index * 104);
                currentMethods[Convert.ToBase64String(currentMetaVersion, offset, 32)] =
                    Convert.ToBase64String(currentMetaVersion, offset + 32, 32);
            }
            for (int index = 0; index < baseCount; index++)
            {
                int offset = checked(baseStart + index * 104);
                string stableId = Convert.ToBase64String(baseMetaVersion, offset, 32);
                string version = Convert.ToBase64String(baseMetaVersion, offset + 32, 32);
                uint flags = BitConverter.ToUInt32(baseMetaVersion, offset + 100);
                bool canHaveAotEntry = (flags & 8u) != 0 && (flags & (2u | 4u)) == 0;
                if (canHaveAotEntry && (!currentMethods.TryGetValue(stableId,
                        out string currentVersion) ||
                    !string.Equals(version, currentVersion, StringComparison.Ordinal)))
                {
                    return offset + 96;
                }
            }
            return -1;
        }

        private static bool TryGetMetaVersionMethodTable(byte[] metaVersion,
            out int methodStart, out int methodCount)
        {
            methodStart = 0;
            methodCount = 0;
            if (metaVersion == null || metaVersion.Length < 60 ||
                !string.Equals(System.Text.Encoding.ASCII.GetString(metaVersion, 0, 8),
                    "DHEMETA1", StringComparison.Ordinal) ||
                BitConverter.ToUInt32(metaVersion, 8) != 1)
            {
                return false;
            }
            uint nameSize = BitConverter.ToUInt32(metaVersion, 16);
            uint typeCount = BitConverter.ToUInt32(metaVersion, 20);
            uint rawMethodCount = BitConverter.ToUInt32(metaVersion, 24);
            long start = 60L + nameSize + 72L * typeCount;
            long expectedSize = start + 104L * rawMethodCount;
            if (start > int.MaxValue || rawMethodCount > int.MaxValue ||
                expectedSize != metaVersion.Length)
            {
                return false;
            }
            methodStart = checked((int)start);
            methodCount = checked((int)rawMethodCount);
            return true;
        }

        private static string ValidateAssetPath(string path, string description, string expectedRoot = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidDataException("DHE " + description + " path is empty.");
            }
            string normalized = path.Replace('\\', '/');
            if (!normalized.StartsWith(NormalizeAssetRoot(expectedRoot ?? assetRoot), StringComparison.Ordinal) ||
                normalized.Contains("../", StringComparison.Ordinal) || normalized.EndsWith("/..",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("DHE " + description +
                    " path escapes the hotfix asset root: " + path);
            }
            return normalized;
        }

        private static string ValidateBaseMetaVersionAssetPath(string path, string expectedRoot,
            string description)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(expectedRoot))
                throw new InvalidDataException("DHE " + description + " path is empty.");
            string normalized = path.Replace('\\', '/');
            string root = NormalizeAssetRoot(expectedRoot);
            if (!normalized.StartsWith(root, StringComparison.Ordinal) ||
                normalized.Split('/').Any(segment => segment == "." || segment == ".."))
                throw new InvalidDataException("DHE " + description +
                    " path escapes the immutable Base MetaVersion root: " + path);
            return normalized;
        }

        private static string ValidateFrozenAssetPath(string path, DheRuntimeIdentity identity,
            string assemblyName, string suffix)
        {
            string normalized = ValidateAssetPath(path, assemblyName + " frozen source", identity.RuntimeAssetRoot);
            string expected = NormalizeAssetRoot(identity.RuntimeAssetRoot) + "payload/frozen-aot/" +
                identity.BaseId.ToLowerInvariant() + "/" + assemblyName + suffix;
            if (!string.Equals(normalized, expected, StringComparison.Ordinal) ||
                normalized.StartsWith(NormalizeAssetRoot(identity.BaseMetaVersionAssetRoot), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("DHE frozen source must use a Base-specific resource path outside the immutable Base MV root: " + path);
            return normalized;
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create()) return ToHex(sha.ComputeHash(bytes ?? Array.Empty<byte>()));
        }

        private static string ComputeBaseId(DheRuntimeIdentity value)
        {
            string[] capabilities = (value.RuntimeCapabilities ?? Array.Empty<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal).OrderBy(item => item,
                    StringComparer.Ordinal).ToArray();
            string canonical = "hybridclr.dhe-base-identity-v1\n" +
                "target=" + (value.Target ?? string.Empty) + "\n" +
                "engineWorkflow=" + (value.EngineWorkflow ?? string.Empty) + "\n" +
                "il2cppCodeGeneration=" +
                (value.Il2CppCodeGeneration ?? string.Empty) + "\n" +
                "managedAssemblySetSha256=" +
                (value.ManagedAssemblySetSha256 ?? string.Empty).ToLowerInvariant() + "\n" +
                "aotAssemblySetSha256=" +
                (value.AotAssemblySetSha256 ?? string.Empty).ToLowerInvariant() + "\n" +
                "aotSnapshotSha256=" +
                (value.AotSnapshotSha256 ?? string.Empty).ToLowerInvariant() + "\n" +
                "baseMetaVersionSetSha256=" +
                (value.BaseMetaVersionSetSha256 ?? string.Empty).ToLowerInvariant() + "\n" +
                "aotMetadataSetId=" +
                (value.AotMetadataSetId ?? string.Empty).ToLowerInvariant() + "\n" +
                "nativeGuardSourceSha256=" +
                (value.NativeGuardSourceSha256 ?? string.Empty).ToLowerInvariant() + "\n" +
                "nativeManifestSha256=" +
                (value.NativeManifestSha256 ?? string.Empty).ToLowerInvariant() + "\n" +
                "runtimeProtocol=" + (value.RuntimeProtocol ?? string.Empty) + "\n" +
                "runtimeContract=" + (value.RuntimeContract ?? string.Empty) + "\n" +
                "runtimeCapabilities=" + string.Join(",", capabilities) + "\n" +
                "runtimeAssetRoot=" + NormalizeAssetRoot(value.RuntimeAssetRoot) + "\n" +
                "baseMetaVersionAssetRoot=" +
                NormalizeAssetRoot(value.BaseMetaVersionAssetRoot) + "\n";
            if (!string.IsNullOrWhiteSpace(value.AotAnalysisSnapshotSha256))
                canonical += "aotAnalysisSnapshotSha256=" + value.AotAnalysisSnapshotSha256.ToLowerInvariant() + "\n";
            return Sha256Hex(System.Text.Encoding.UTF8.GetBytes(canonical));
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

        private static bool TryValidateAotAssemblyInventory(string[] assemblyNames,
            string expectedHash)
        {
            string[] names = assemblyNames ?? Array.Empty<string>();
            if (names.Length == 0 || names.Any(string.IsNullOrWhiteSpace) ||
                names.Any(name => !string.Equals(name, NormalizeAssemblyName(name),
                    StringComparison.Ordinal)) ||
                names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length ||
                !IsSha256(expectedHash))
                return false;
            string canonical = string.Concat(names.OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => name + "\n"));
            return string.Equals(Sha256Hex(System.Text.Encoding.UTF8.GetBytes(canonical)),
                expectedHash, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCompatibilityPolicy(string value)
        {
            const string Prefix = "dhe-proven-safe-subset-";
            return !string.IsNullOrWhiteSpace(value) &&
                value.StartsWith(Prefix, StringComparison.Ordinal) &&
                value.Length <= 128 && value.All(character => char.IsLetterOrDigit(character) ||
                    character == '-' || character == '.' || character == '_');
        }

        private static string Sha256NamedByteSet(
            IEnumerable<KeyValuePair<string, byte[]>> records)
        {
            using (SHA256 sha = SHA256.Create())
            {
                foreach (KeyValuePair<string, byte[]> record in records.OrderBy(item => item.Key,
                             StringComparer.Ordinal))
                {
                    byte[] name = System.Text.Encoding.UTF8.GetBytes(record.Key + "\n");
                    sha.TransformBlock(name, 0, name.Length, name, 0);
                    byte[] bytes = record.Value ?? Array.Empty<byte>();
                    sha.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
                    byte[] separator = { (byte)'\n' };
                    sha.TransformBlock(separator, 0, separator.Length, separator, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return ToHex(sha.Hash);
            }
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
