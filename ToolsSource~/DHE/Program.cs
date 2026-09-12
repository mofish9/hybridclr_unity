using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace HybridCLR.DheTool;

internal static partial class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private const string PackageIdAlgorithm = "sha256-canonical-manifest-v2";
    private static readonly string[] RequiredRegressionChecks =
    {
        "mv-field-order", "mv-switch-target", "mv-assembly-metadata",
        "managed-current-base-variant-metadata-stable",
        "managed-current-consecutive-variant-metadata-stable", "mv-flags-tamper",
        "mv-token-tamper", "verify-require-release", "verify-expected-id", "verify-package-id-recompute",
        "aot-metadata-set-order-independent", "aot-metadata-set-deduplicated",
        "aot-metadata-set-selection-bound", "aot-metadata-set-tamper-rejected",
        "verify-extra-source", "verify-release-bit-tamper", "evidence-role-format",
        "evidence-native-runtime-binding", "runtime-package-source-binding", "archive-safe-replace",
        "evidence-noop-aot-proof", "evidence-native-matrix-roles",
        "native-guard-unrelated-source-stable", "native-guard-block-tamper",
        "native-guard-duplicate-marker", "native-guard-missing-end-marker",
        "reference-field-and-type-removal", "value-field-removal-rejected",
        "runtime-contract-capability-negotiation",
        "runtime-contract-schema-binding", "runtime-contract-package-binding",
        "runtime-capability-package-binding",
        "runtime-capability-missing-rejected", "composite-base-id-runtime-bound",
        "composite-base-id-aot-inventory-bound", "composite-base-id-build-configuration-bound",
        "base-aot-outside-dhe-rejected",
        "resource-stage-plan-capability-bound",
        "resource-stage-aot-metadata-capability-bound",
        "resource-stage-base-registry-audit-bound",
        "resource-stage-base-registry-audit-tamper-rejected",
        "resource-stage-base-registry-binding-removal-rejected",
        "resource-stage-base-registry-lineage-bound",
        "resource-stage-base-registry-parent-tamper-rejected",
        "resource-stage-direct-base-valid",
        "resource-stage-android-apk-base-bound",
        "resource-stage-android-apk-tamper-rejected",
        "resource-cross-target-payload-release", "resource-cross-target-consecutive-three-variant",
        "resource-cross-target-selection-bound",
        "resource-cross-target-selected-payload-tamper-rejected",
        "resource-cross-target-variant-set-tamper-rejected",
        "resource-cross-target-missing-variant-rejected",
        "resource-cross-target-primary-variant-contract",
        "resource-release-build-registry-reuse",
        "resource-release-build-three-engine-onboarding",
        "resource-release-build-old-bases-preserved",
        "resource-release-build-variant-selection",
        "resource-release-build-stale-snapshot-rejected",
        "resource-release-build-missing-variant-rejected",
        "resource-release-build-duplicate-base-rejected",
        "resource-release-build-partial-output-rejected",
        "resource-release-build-replacement-restored",
        "resource-release-build-exact-active-base-coverage",
        "resource-release-build-onboarding-all-base-staging",
        "resource-release-build-reuse-all-base-staging",
        "resource-release-build-protected-release",
        "resource-release-qualify-prequalified",
        "resource-release-qualify-exact-coverage",
        "resource-release-qualify-process-contract",
        "resource-release-qualify-stale-snapshot-rejected",
        "resource-release-plan-generated-qualification",
        "resource-release-plan-exact-coverage",
        "resource-release-plan-duplicate-base",
        "resource-release-plan-registry-identity",
        "resource-release-plan-template-contract",
        "resource-release-plan-stale-snapshot",
        "resource-base-registry", "base-registry-builder",
        "base-registry-build-configuration-tamper-rejected",
        "base-registry-lineage", "base-registry-implicit-removal-rejected",
        "base-registry-explicit-retirement",
        "resource-release-ledger-initialized",
        "resource-release-ledger-continuation",
        "resource-release-ledger-base-transition",
        "resource-release-ledger-reset-rejected",
        "resource-release-ledger-stale-head-rejected",
        "registry-empty-aot-metadata-set",
        "resource-player-evidence-binding",
        "resource-player-archive-native-manifest-bound",
        "resource-player-release-ledger-binding",
        "resource-player-consecutive-release-head",
        "resource-release-aggregate-gate",
        "resource-release-aggregate-portable-authority",
        "channel-state-cas-workflow",
        "evidence-portable-mixed-toolchain-authorities",
        "evidence-immediate-predecessor-authorized",
        "resource-player-legacy-single-payload-compatibility",
        "resource-player-assembly-mode-binding",
        "resource-player-interpreter-only-update",
        "resource-player-selected-base-noop",
        "resource-player-release-readiness",
        "resource-stage-aot-inventory-missing-rejected",
        "resource-stage-aot-inventory-hash-tamper-rejected",
        "resource-stage-release-ledger-bound",
        "resource-stage-release-ledger-tamper-rejected",
        "resource-stage-consecutive-base-stable",
        "evidence-managed-release-binding", "evidence-multibase-current-binding",
        "evidence-extensible-player-engine-matrix",
        "bootstrap-engine-workflow-matrix",
        "source-boundary-git-root-resolution", "git-relative-root-resolution",
        "unity-stale-lock-recovery",
        "android-device-smoke-contract",
        "dhe-runner-provider-overlay",
        "native-universal-body-filter",
        "native-finalize-bee-graph-regeneration",
        "native-finalize-attempt-limit-rejected",
        "native-finalize-missing-reapply-rejected",
        "native-finalize-android-hash-mismatch-rejected",
        "native-finalize-ios-xcode-structure",
        "native-finalize-ios-xcode-structure-rejected",
        "metadata-stress-touch-bounded",
        "base-workflow-aot-metadata-archive",
        "integrated-source-lock-line-ending-stable",
        "integrated-source-lock-valid", "integrated-source-lock-commit-tamper",
        "integrated-source-lock-tree-tamper", "integrated-source-lock-patch-tamper",
        "generated-cpp-resolver-engine-matrix", "generated-cpp-resolver-identity-tamper",
        "layout-release-role-schemas",
        "schema-valid-document", "schema-maximum-rejected", "schema-additional-type-rejected",
        "schema-resource-release-mode-contract",
        "schema-unsupported-keyword-rejected", "schema-gate-contract",
        "schema-workflow-output-contract"
    };
    private static readonly string[] RequiredStaticReleaseEvidenceRoles =
    {
        "regression", "demo-noop", "native-tuanjie2022",
        "native-unity2022", "resolver-tuanjie2022", "resolver-unity2022"
    };
    private static readonly string[] RequiredPlayerEngineWorkflows =
    {
        "Unity2022Fgs", "Tuanjie2022Fgs"
    };
    // Keep archived Base identities readable without qualifying new Unity 2021 builds.
    private static readonly string[] KnownPlayerEngineWorkflows =
    {
        "Unity2021Standard", "Unity2022Fgs", "Tuanjie2022Fgs"
    };
    private const int MaxChangedPlayerEvidenceCount = 1024;
    private static readonly string[] RequiredResolverChecks =
    {
        "methoddef-token-overload-no-comments", "generic-method-table-overload-no-comments",
        "managed-signature-conflict-rejected", "managed-signature-complex-parameters",
        "pointer-count-tamper-rejected",
        "generic-native-owner-conflict-rejected",
        "bee-graph-regeneration-reapplies-guard", "bee-attempt-limit-rejected",
        "bee-missing-reapply-rejected", "android-gradle-root-dag-bound",
        "android-native-hash-mismatch-rejected"
    };

    public static int Main(string[] args)
    {
        try
        {
            var cli = Cli.Parse(args);
            PrepareUnityTool(cli);
#if DHE_PACKAGE_TOOL
            if (cli.Command is "help" or "") return 0;
#endif
            if (cli.Command is "help" or "") { PrintHelp(); return 0; }
            return cli.Command.ToLowerInvariant() switch
            {
                "version" => Version(cli),
                "capture-installation" => CaptureProjectInstallation(cli),
                "verify-installation" => VerifyProjectInstallation(cli),
                "mv" or "metaversion" => GenerateMetaVersion(cli),
                "batch" => Batch(cli),
                "base-registry" => BuildBaseRegistry(cli),
                "resource-release-build" => ResourceReleaseBuild(cli),
                "resource-release-plan" => ResourceReleasePlan(cli),
                "resource-release-qualify" => ResourceReleaseQualify(cli),
                "resource-update" => ResourceUpdate(cli),
                "stage-resource-update" => StageResourceUpdate(cli),
                "android-device-smoke" => AndroidDeviceSmoke(cli),
                "resource-player-evidence" => ResourcePlayerEvidence(cli),
                "resource-release-gate" => ResourceReleaseGate(cli),
                "channel-state" => ChannelState(cli),
                "baseline-manifest" => BaselineManifest(cli),
                "aot-metadata-manifest" => AotMetadataManifest(cli),
                "preflight" => Preflight(cli),
                "workflow" => Workflow(cli),
                "release-gate" => ReleaseGate(cli),
                "regression" => Regression(cli),
                "schema-validate" => SchemaValidate(cli),
                "schema-gate" => SchemaGate(cli),
                "validate" => Validate(cli),
                "archive" => Archive(cli),
                "doctor" => Doctor(cli),
                "verify-package" => VerifyPackage(cli),
                "release-evidence" => ReleaseEvidence(cli),
                "publish" => Publish(cli),
                "publish-unity-tool" => PublishUnityTool(cli),
                "install" => Install(cli),
                "new-adapter" => NewAdapter(cli),
                "new-config" => NewConfig(cli),
                "assemble-runtime" or "native-tests" or "build-managed-cases" or
                "generate-test-manifest" or "generate-metadata-stress-source" or
                "reference" or "compare-results" or "check-environment" or
                "clear-unity-project-locks" or "wait-editor" or
                "prepare-engine-test-project" or "bootstrap-repos" or
                "tree-hash" or "file-hash" => LabCommands.Run(cli),
                _ => throw new DheException($"Unknown DHE command '{cli.Command}'.")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("DHE failed: " + ex.Message);
            return 1;
        }
    }

    private static int Version(Cli cli)
    {
        var manifest = ReadJson<Dictionary<string, JsonElement>>(Path.Combine(cli.Root, "dhe-toolchain-manifest.json"));
        Console.WriteLine($"HybridCLR DHE toolchain {GetString(manifest, "toolchainVersion")} (contract {GetInt(manifest, "contractVersion")}, packageId={GetString(manifest, "packageId")})");
        return 0;
    }

    private static int GenerateMetaVersion(Cli cli)
    {
        string assembly = RequireFile(cli.Require("assembly"), "MetaVersion assembly");
        string output = SafeReportPath(cli.Require("output"), new[] { assembly });
        string binary = SafeReportPath(cli.Require("binary"), new[] { assembly, output });
        MetaVersionSnapshot snapshot = MetaVersionSnapshot.Create(assembly);
        WriteJson(output, snapshot.ToJson(assembly));
        snapshot.WriteBinary(binary);
        Console.WriteLine("DHE MetaVersion: " + binary);
        return 0;
    }

    private static int BaselineManifest(Cli cli)
    {
        var root = RequireDirectory(cli.Require("baselineroot"), "Baseline root");
        var runtimePath = RequireFile(cli.Require("runtimemanifestpath"), "Runtime manifest");
        var settingsPath = RequireFile(cli.Require("settingsfile"), "HybridCLR settings");
        var output = SafeReportPath(cli.Require("output"), new[] { root, runtimePath, settingsPath });
        var runtime = ReadJson<JsonElement>(runtimePath);
        var sets = Settings.Read(settingsPath);
        var records = sets.Dhe.Select(name => new AssemblyRecord(name, Sha256File(RequireFile(Path.Combine(root, name + ".dll"), name + " baseline assembly")))).ToArray();
        var packageLock = cli.Optional("packagelockpath");
        var package = string.IsNullOrWhiteSpace(packageLock)
            ? (JsonElement?)null
            : ReadJson<JsonElement>(RequireFile(packageLock, "Package lock"));
        var doc = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1, ["format"] = "hybridclr.dhe-baseline-manifest.json", ["generatedAtUtc"] = DateTimeOffset.UtcNow,
            ["pathSemantics"] = "workspace-absolute-v1", ["baselineKind"] = "stripped-aot", ["target"] = cli.Require("target"),
            ["sourceRoot"] = root, ["engineWorkflow"] = GetString(runtime, "engineWorkflow"), ["engine"] = runtime.TryGetProperty("engine", out var engine) ? engine : null,
            ["runtime"] = new { profile = GetString(runtime, "profile"), stagedRuntimeSha256 = GetString(runtime, "stagedRuntimeSha256"), runtimeManifestSha256 = Sha256File(runtimePath), packageTreeSha256 = package.HasValue ? GetString(package.Value, "treeSha256") : null },
            ["package"] = package, ["assemblies"] = records
        };
        WriteJson(output, doc);
        Console.WriteLine("DHE baseline manifest: " + output);
        return 0;
    }

    private static int AotMetadataManifest(Cli cli)
    {
        var root = RequireDirectory(cli.Require("root"), "AOT metadata root");
        var runtimePath = RequireFile(cli.Require("runtimemanifestpath"), "Runtime manifest");
        var settingsPath = RequireFile(cli.Require("settingsfile"), "HybridCLR settings");
        var output = SafeReportPath(cli.Require("output"), new[] { root, runtimePath, settingsPath });
        var runtime = ReadJson<JsonElement>(runtimePath); var sets = Settings.Read(settingsPath);
        var records = sets.Patch.Select(name => new AssemblyRecord(name, Sha256File(RequireFile(Path.Combine(root, name + ".dll"), name + " AOT metadata assembly")))).ToArray();
        var packageLock = cli.Optional("packagelockpath");
        WriteJson(output, new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1, ["format"] = "hybridclr.dhe-aot-metadata-manifest.json", ["generatedAtUtc"] = DateTimeOffset.UtcNow,
            ["pathSemantics"] = "workspace-absolute-v1", ["kind"] = "patch-aot-metadata", ["releaseId"] = cli.Optional("releaseid"),
            ["target"] = cli.Require("target"), ["sourceRoot"] = root, ["engineWorkflow"] = GetString(runtime, "engineWorkflow"),
            ["engine"] = runtime.TryGetProperty("engine", out var engine) ? engine : null,
            ["runtime"] = new { profile = GetString(runtime, "profile"), stagedRuntimeSha256 = GetString(runtime, "stagedRuntimeSha256"), runtimeManifestSha256 = Sha256File(runtimePath), packageTreeSha256 = packageLock == null ? null : GetString(ReadJson<JsonElement>(RequireFile(packageLock, "Package lock")), "treeSha256") },
            ["package"] = string.IsNullOrWhiteSpace(packageLock) ? null : ReadJson<JsonElement>(RequireFile(packageLock, "Package lock")), ["assemblies"] = records
        });
        Console.WriteLine("DHE AOT metadata manifest: " + output); return 0;
    }

    private static int Batch(Cli cli)
    {
        var baselineRoot = RequireDirectory(cli.Require("baselineroot"), "Baseline root");
        var currentRoot = RequireDirectory(cli.Require("currentroot"), "Current root");
        var outputRoot = SafeOutputRoot(cli.Require("outputroot"), new[] { baselineRoot, currentRoot });
        Directory.CreateDirectory(outputRoot);
        var settingsPath = cli.Optional("settingsfile");
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(settingsPath)) names.AddRange(Settings.Read(RequireFile(settingsPath, "HybridCLR settings")).Dhe);
        if (cli.Has("assemblynames")) names.AddRange(cli.GetList("assemblynames"));
        var listFile = cli.Optional("assemblylistfile");
        if (!string.IsNullOrWhiteSpace(listFile)) names.AddRange(File.ReadAllLines(RequireFile(listFile, "Assembly list")).Where(x => !string.IsNullOrWhiteSpace(x) && !x.TrimStart().StartsWith('#')));
        names = names.Select(NormalizeName).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0) throw new DheException("No DHE assemblies were supplied.");
        var sets = !string.IsNullOrWhiteSpace(settingsPath) ? Settings.Read(settingsPath) : new Settings.Sets();
        var candidates = new List<(string Name, string Baseline, string Current,
            MetaVersionSnapshot? BaseMetaVersion, MetaVersionSnapshot? CurrentMetaVersion,
            string? Error)>();
        foreach (var name in names)
        {
            var baseline = Path.Combine(baselineRoot, name + ".dll");
            var current = Path.Combine(currentRoot, name + ".dll");
            if (!File.Exists(baseline) || !File.Exists(current))
            {
                candidates.Add((name, baseline, current, null, null,
                    "Missing baseline or current assembly file."));
                continue;
            }
            try
            {
                candidates.Add((name, baseline, current, MetaVersionSnapshot.Create(baseline),
                    MetaVersionSnapshot.Create(current), null));
            }
            catch (Exception ex)
            {
                candidates.Add((name, baseline, current, null, null, ex.Message));
            }
        }
        string[] addressTakenFields = candidates.Where(candidate => candidate.CurrentMetaVersion != null)
            .SelectMany(candidate => candidate.CurrentMetaVersion!.AddressTakenFieldIdentities)
            .Distinct(StringComparer.Ordinal).ToArray();
        var records = new List<BatchRecord>();
        foreach (var candidate in candidates)
        {
            string baseJson = Path.Combine(outputRoot, candidate.Name + ".base.mv.json");
            string baseBinary = Path.Combine(outputRoot, candidate.Name + ".base.mv.bytes");
            string currentJson = Path.Combine(outputRoot, candidate.Name + ".current.mv.json");
            string currentBinary = Path.Combine(outputRoot, candidate.Name + ".current.mv.bytes");
            if (candidate.BaseMetaVersion == null || candidate.CurrentMetaVersion == null)
            {
                string status = File.Exists(candidate.Baseline) && File.Exists(candidate.Current)
                    ? "error" : "missing";
                records.Add(new BatchRecord(candidate.Name, candidate.Baseline, candidate.Current,
                    baseJson, baseBinary, currentJson, currentBinary, status, 0, 0, 0,
                    0, 0, 0,
                    Array.Empty<string>(), candidate.Error));
                continue;
            }
            ResourceUpdateCompatibility compatibility = ResourceUpdateCompatibility.AnalyzeFailClosed(
                candidate.BaseMetaVersion, candidate.CurrentMetaVersion, addressTakenFields,
                currentAssemblySet: candidates.Where(item => item.CurrentMetaVersion != null)
                    .Select(item => item.CurrentMetaVersion!),
                baselineAssemblySet: candidates.Where(item => item.BaseMetaVersion != null)
                    .Select(item => item.BaseMetaVersion!));
            WriteJson(baseJson, candidate.BaseMetaVersion.ToJson(candidate.Baseline));
            candidate.BaseMetaVersion.WriteBinary(baseBinary);
            WriteJson(currentJson, candidate.CurrentMetaVersion.ToJson(candidate.Current));
            candidate.CurrentMetaVersion.WriteBinary(currentBinary);
            records.Add(new BatchRecord(candidate.Name, candidate.Baseline, candidate.Current,
                baseJson, baseBinary, currentJson, currentBinary,
                compatibility.Compatible ? "compatible" : "incompatible",
                compatibility.ChangedMethodCount, compatibility.AddedMethodCount,
                compatibility.RemovedMethodCount, compatibility.ChangedExistingTypeCount,
                compatibility.AddedTypeCount, compatibility.RemovedTypeCount,
                compatibility.UnsupportedChanges,
                compatibility.Compatible ? null : string.Join("; ",
                    compatibility.UnsupportedChanges)));
        }
        var configErrors = new List<string>();
        var requireCoverage = cli.Has("requiredheequalshotupdate");
        if (requireCoverage && !SetEquals(sets.Hot, sets.Dhe)) configErrors.Add("dheAotAssemblies must exactly match hotUpdateAssemblies.");
        var summary = new { schemaVersion = 1, format = "hybridclr.dhe-batch-report.json", generatedAtUtc = DateTimeOffset.UtcNow,
            baselineRoot, currentRoot, compatibilityPolicy = ResourceUpdateCompatibility.Policy,
            requireDheEqualsHotUpdate = requireCoverage,
            configurationPassed = configErrors.Count == 0, configurationErrors = configErrors, hotUpdateAssemblies = sets.Hot, dheAotAssemblies = sets.Dhe,
            assemblies = records, counts = new { total = records.Count, compatible = records.Count(x => x.Status == "compatible"), incompatible = records.Count(x => x.Status == "incompatible"), missing = records.Count(x => x.Status == "missing"), error = records.Count(x => x.Status == "error") } };
        WriteJson(Path.Combine(outputRoot, "dhe-batch-summary.json"), summary);
        Console.WriteLine($"DHE batch: {summary.counts.compatible}/{summary.counts.total} compatible");
        return configErrors.Count == 0 && summary.counts.incompatible == 0 && summary.counts.missing == 0 && summary.counts.error == 0 ? 0 : 2;
    }

    private static Dictionary<string, string> ReadCurrentVariantRoots(Cli cli,
        string primaryVariantId, string primaryCurrentRoot)
    {
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [primaryVariantId] = primaryCurrentRoot,
        };
        string? raw = cli.Optional("currentvariantroots");
        if (string.IsNullOrWhiteSpace(raw))
            return roots;
        JsonElement value;
        try
        {
            using var document = JsonDocument.Parse(raw);
            value = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new DheException("CurrentVariantRoots must be a JSON object mapping variant IDs to roots: " +
                exception.Message);
        }
        if (value.ValueKind != JsonValueKind.Object)
            throw new DheException("CurrentVariantRoots must be a JSON object mapping variant IDs to roots.");
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!IsPayloadVariantId(property.Name) || property.Value.ValueKind != JsonValueKind.String)
                throw new DheException("CurrentVariantRoots contains an invalid variant ID or root: " +
                    property.Name);
            string root = RequireDirectory(property.Value.GetString() ?? string.Empty,
                "Current " + property.Name + " variant root");
            if (roots.TryGetValue(property.Name, out string? prior) &&
                !string.Equals(prior, root, StringComparison.OrdinalIgnoreCase))
                throw new DheException("CurrentVariantRoots cannot redefine the primary current root.");
            roots[property.Name] = root;
        }
        return roots;
    }

    /// <summary>
    /// Creates the authenticated registry consumed by resource-update. Base
    /// artifacts are deliberately referenced, never copied: the registry is
    /// the stable index for every online Base while the hotfix payload remains
    /// one current DLL/MV set. Existing entries can be normalized and new
    /// identities appended in one operation.
    /// </summary>
    private static int BuildBaseRegistry(Cli cli)
    {
        string outputPath = Path.GetFullPath(cli.Require("output"));
        string? existingPath = cli.Optional("existingregistry");
        string[] identityPaths;
        string[] baselineRoots;
        string[] nativeManifestPaths;
        string[] workflowValues;
        string[] variantValues;
        string[] labelValues;
        string[] aotValues;
        string? structuredEntries = cli.Optional("baseentriesjson");
        if (!string.IsNullOrWhiteSpace(structuredEntries))
        {
            string[] legacyEntryArguments =
            {
                "baseidentities", "baseidentity", "baselineroots", "baselineroot",
                "basenativemanifests", "basenativemanifest", "engineworkflows",
                "payloadvariantids", "labels", "aotmetadataroots",
            };
            if (legacyEntryArguments.Any(name => !string.IsNullOrWhiteSpace(cli.Optional(name))))
                throw new DheException("BaseEntriesJson cannot be combined with parallel new-Base arguments.");
            (identityPaths, baselineRoots, nativeManifestPaths, workflowValues,
                variantValues, labelValues, aotValues) = ReadStructuredBaseRegistryEntries(
                structuredEntries);
        }
        else
        {
            identityPaths = ReadPathList(cli, "baseidentities", "baseidentity");
            baselineRoots = ReadPathList(cli, "baselineroots", "baselineroot");
            nativeManifestPaths = ReadPathList(cli, "basenativemanifests", "basenativemanifest");
            workflowValues = cli.GetList("engineworkflows").ToArray();
            variantValues = cli.GetList("payloadvariantids").ToArray();
            labelValues = cli.GetList("labels").ToArray();
            aotValues = cli.GetList("aotmetadataroots").ToArray();
        }
        string[] retireBaseIds = cli.GetList("retirebaseids").ToArray();
        string? retirementReason = cli.Optional("retirementreason")?.Trim();
        if (retireBaseIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            retireBaseIds.Length || retireBaseIds.Any(value => !IsHex(value, 64, 64)))
            throw new DheException("RetireBaseIds contains an invalid or duplicate Base ID.");
        if (retireBaseIds.Length == 0 && !string.IsNullOrWhiteSpace(retirementReason))
            throw new DheException("RetirementReason requires RetireBaseIds.");
        if (retireBaseIds.Length != 0 &&
            (string.IsNullOrWhiteSpace(retirementReason) || retirementReason.Length > 512))
            throw new DheException(
                "Retiring a Base requires a non-empty RetirementReason of at most 512 characters.");

        var protectedPaths = new List<string>();
        protectedPaths.AddRange(identityPaths);
        protectedPaths.AddRange(baselineRoots);
        protectedPaths.AddRange(nativeManifestPaths);
        if (!string.IsNullOrWhiteSpace(existingPath)) protectedPaths.Add(existingPath);
        foreach (string path in protectedPaths)
        {
            if (File.Exists(path) && Path.GetFullPath(path).Equals(outputPath,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("Registry output must not overwrite an input: " + outputPath);
            else if (Directory.Exists(path))
            {
                EnsureOutputOutsideRoot(outputPath, path);
            }
        }

        var entries = new List<(string BaseId, string EngineWorkflow, string PayloadVariantId,
            string Label, string BaselineRoot, string NativeManifest, string BuildIdentity,
            string? AotMetadataRoot)>();
        var baseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identityFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retiredBases = new List<BaseRegistryRetirement>();
        var retiredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string registryId;
        int revision;
        string? parentRegistrySha256;

        void AddEntry(string baseId, string engineWorkflow, string payloadVariantId,
            string label, string baselineRoot, string nativeManifest, string buildIdentity,
            string? aotMetadataRoot)
        {
            if (!IsHex(baseId, 64, 64) || retiredIds.Contains(baseId) || !baseIds.Add(baseId))
                throw new DheException("Registry contains an invalid or duplicate Base ID: " + baseId);
            if (!KnownPlayerEngineWorkflows.Contains(engineWorkflow,
                    StringComparer.Ordinal) || !IsPayloadVariantId(payloadVariantId))
                throw new DheException("Registry Base workflow or payload variant is invalid: " +
                    engineWorkflow + "/" + payloadVariantId);
            if (!identityFiles.Add(Path.GetFullPath(buildIdentity)))
                throw new DheException("Registry reuses one BuildIdentity path: " + buildIdentity);
            entries.Add((baseId, engineWorkflow, payloadVariantId, label, baselineRoot,
                nativeManifest, buildIdentity, aotMetadataRoot));
        }

        if (!string.IsNullOrWhiteSpace(existingPath))
        {
            BaseRegistryDocument existing = ReadBaseRegistry(existingPath);
            registryId = cli.Optional("registryid") ?? existing.RegistryId;
            if (!string.Equals(registryId, existing.RegistryId, StringComparison.Ordinal))
                throw new DheException("RegistryId cannot change across Base registry revisions.");
            revision = checked(existing.Revision + 1);
            parentRegistrySha256 = existing.Sha256;
            retiredBases.AddRange(existing.RetiredBases);
            retiredIds.UnionWith(existing.RetiredBases.Select(item => item.BaseId));
            var retireSet = retireBaseIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] missingRetirements = retireSet.Except(existing.Entries.Select(item => item.BaseId),
                StringComparer.OrdinalIgnoreCase).ToArray();
            if (missingRetirements.Length != 0)
                throw new DheException("RetireBaseIds contains a Base that is not active in the previous registry: " +
                    string.Join(",", missingRetirements));
            foreach (BaseRegistryEntry entry in existing.Entries)
            {
                string identityBaseId = ValidateBaseRegistryArtifacts(entry.BuildIdentity,
                    entry.BaselineRoot, entry.NativeManifest, entry.EngineWorkflow);
                if (!string.Equals(identityBaseId, entry.BaseId,
                        StringComparison.OrdinalIgnoreCase))
                    throw new DheException("Existing registry Base ID does not match its build identity: " +
                        entry.BuildIdentity);
                if (retireSet.Contains(entry.BaseId))
                {
                    retiredIds.Add(entry.BaseId);
                    retiredBases.Add(new BaseRegistryRetirement(entry.BaseId,
                        entry.EngineWorkflow, entry.Label, revision, retirementReason!));
                    continue;
                }
                AddEntry(entry.BaseId, entry.EngineWorkflow, entry.PayloadVariantId,
                    entry.Label, entry.BaselineRoot, entry.NativeManifest,
                    entry.BuildIdentity, entry.AotMetadataRoot);
            }
        }
        else
        {
            if (retireBaseIds.Length != 0)
                throw new DheException("RetireBaseIds requires ExistingRegistry.");
            registryId = cli.Optional("registryid") ?? Path.GetFileNameWithoutExtension(outputPath);
            revision = 1;
            parentRegistrySha256 = null;
        }
        if (!IsRegistryId(registryId))
            throw new DheException("RegistryId must contain only letters, digits, '.', '_' or '-'.");

        if (identityPaths.Length == 0 && baselineRoots.Length == 0 &&
            nativeManifestPaths.Length == 0 && workflowValues.Length == 0 &&
            string.IsNullOrWhiteSpace(existingPath))
            throw new DheException("Base registry requires ExistingRegistry or new Base inputs.");
        if (identityPaths.Length != baselineRoots.Length ||
            identityPaths.Length != nativeManifestPaths.Length ||
            identityPaths.Length != workflowValues.Length)
            throw new DheException("Base identity, baseline, native manifest, and workflow lists must have equal lengths.");
        if (variantValues.Length != 0 && variantValues.Length != identityPaths.Length)
            throw new DheException("PayloadVariantIds must contain one value per new Base.");
        if (labelValues.Length != 0 && labelValues.Length != identityPaths.Length)
            throw new DheException("Labels must contain one value per new Base.");
        if (aotValues.Length != 0 && aotValues.Length != identityPaths.Length)
            throw new DheException("AotMetadataRoots must contain one value per new Base; use null for an empty set.");

        for (int index = 0; index < identityPaths.Length; index++)
        {
            string identityPath = RequireFile(identityPaths[index], "Base build identity");
            string baselineRoot = RequireDirectory(baselineRoots[index], "Base baseline root");
            string nativeManifestPath = RequireFile(nativeManifestPaths[index], "Base native manifest");
            string engineWorkflow = workflowValues[index];
            string variant = variantValues.Length == 0 ? "default" : variantValues[index];
            string label = labelValues.Length == 0
                ? engineWorkflow + " Base " + (index + 1).ToString(CultureInfo.InvariantCulture)
                : labelValues[index];
            if (string.IsNullOrWhiteSpace(label) || label.Length > 256)
                throw new DheException("Base registry label is empty or too long.");

            string baseId = ValidateBaseRegistryArtifacts(identityPath, baselineRoot,
                nativeManifestPath, engineWorkflow);

            string? aotRoot = null;
            if (aotValues.Length != 0 && !string.Equals(aotValues[index], "null",
                    StringComparison.OrdinalIgnoreCase) && aotValues[index] != "-")
                aotRoot = RequireDirectory(aotValues[index], "Base AOT metadata root");
            AddEntry(baseId, engineWorkflow, variant, label, baselineRoot,
                nativeManifestPath, identityPath, aotRoot);
        }

        if (entries.Count == 0)
            throw new DheException("Base registry cannot be empty.");
        string outputDirectory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(outputDirectory);
        if (File.Exists(outputPath) && !cli.Has("forceoutput"))
            throw new DheException("Registry output already exists; pass -ForceOutput to replace it.");

        string Relative(string path)
        {
            string relative = Path.GetRelativePath(outputDirectory, Path.GetFullPath(path))
                .Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
                relative.Contains('\0'))
                throw new DheException("Registry path cannot be represented relative to output: " + path);
            return relative;
        }

        var outputBases = entries.Select(entry => new
        {
            baseId = entry.BaseId,
            engineWorkflow = entry.EngineWorkflow,
            label = entry.Label,
            payloadVariantId = entry.PayloadVariantId,
            baselineRoot = Relative(entry.BaselineRoot),
            nativeManifest = Relative(entry.NativeManifest),
            buildIdentity = Relative(entry.BuildIdentity),
            aotMetadataRoot = entry.AotMetadataRoot == null ? null : Relative(entry.AotMetadataRoot),
        }).ToArray();
        var outputRetiredBases = retiredBases.OrderBy(entry => entry.RetiredAtRevision)
            .ThenBy(entry => entry.BaseId, StringComparer.OrdinalIgnoreCase).Select(entry => new
            {
                baseId = entry.BaseId,
                engineWorkflow = entry.EngineWorkflow,
                label = entry.Label,
                retiredAtRevision = entry.RetiredAtRevision,
                reason = entry.Reason,
            }).ToArray();
        WriteJson(outputPath, new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-base-registry.json",
            pathSemantics = "registry-relative-v1",
            registryId,
            revision,
            parentRegistrySha256,
            generatedAtUtc = DateTimeOffset.UtcNow,
            bases = outputBases,
            retiredBases = outputRetiredBases,
        });
        // Re-read through the same resolver used by resource-update so a
        // generated registry cannot pass merely because its JSON is shaped
        // correctly while a relative path is broken.
        _ = ReadBaseRegistry(outputPath);
        Console.WriteLine("DHE Base registry: " + outputPath);
        return 0;
    }

    private static string ValidateBaseRegistryArtifacts(string identityPath, string baselineRoot,
        string nativeManifestPath, string expectedEngineWorkflow)
    {
        JsonElement identity = ReadJson<JsonElement>(identityPath);
        if (GetInt(identity, "schemaVersion") != 1 ||
            !string.Equals(GetString(identity, "format"),
                "hybridclr.dhe-build-identity.json", StringComparison.Ordinal) ||
            GetInt(identity, "identityVersion") != 1 ||
            !string.Equals(GetString(identity, "state"), "staged-for-final-player",
                StringComparison.Ordinal))
            throw new DheException("Base build identity contract is invalid: " + identityPath);
        string baseId = GetString(identity, "baseId") ?? string.Empty;
        if (!IsHex(baseId, 64, 64))
            throw new DheException("Base build identity has an invalid baseId: " + identityPath);
        string engineWorkflow = GetString(identity, "engineWorkflow") ?? string.Empty;
        string il2cppCodeGeneration = GetString(identity, "il2cppCodeGeneration") ?? string.Empty;
        if (!string.Equals(engineWorkflow, expectedEngineWorkflow, StringComparison.Ordinal) ||
            !string.Equals(il2cppCodeGeneration,
                ExpectedIl2CppCodeGeneration(engineWorkflow), StringComparison.Ordinal))
            throw new DheException("Base build configuration does not match its engine workflow: " +
                identityPath);
        JsonElement native = ReadJson<JsonElement>(nativeManifestPath);
        if (!string.Equals(GetString(identity, "nativeManifestSha256"),
                Sha256File(nativeManifestPath), StringComparison.OrdinalIgnoreCase))
            throw new DheException("Base identity/native manifest hash binding is invalid: " + identityPath);
        if (!string.Equals(GetString(native, "guardMode"), "universal",
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Base native manifest must use universal guard mode: " + nativeManifestPath);

        if (!identity.TryGetProperty("assemblies", out JsonElement assemblies) ||
            assemblies.ValueKind != JsonValueKind.Array || assemblies.GetArrayLength() == 0)
            throw new DheException("Base build identity has no assembly records: " + identityPath);
        var records = new List<(string Name, string Path)>();
        var assemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement assembly in assemblies.EnumerateArray())
        {
            string name = NormalizeName(GetString(assembly, "assemblyName") ?? string.Empty);
            if (string.IsNullOrWhiteSpace(name) || !assemblyNames.Add(name))
                throw new DheException("Base build identity contains a missing or duplicate assembly: " +
                    identityPath);
            string path = RequireFile(Path.Combine(baselineRoot, name + ".dll"),
                "Base assembly " + name);
            string expectedHash = GetString(assembly, "baselineSha256") ?? string.Empty;
            if (!IsHex(expectedHash, 64, 64) ||
                !string.Equals(Sha256File(path), expectedHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("Base assembly hash does not match its identity: " + name);
            records.Add((name, path));
        }
        string managedSet = NamedAssemblySetHash(records);
        if (!string.Equals(managedSet, GetString(identity, "managedAssemblySetSha256"),
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Base managed assembly set does not match its identity: " + identityPath);
        string[] aotAssemblyNames = ReadIdentityAotAssemblyNames(identity, identityPath);
        if (!new HashSet<string>(aotAssemblyNames, StringComparer.OrdinalIgnoreCase)
                .IsSupersetOf(assemblyNames))
            throw new DheException(
                "Base DHE assemblies are not a subset of its complete AOT inventory: " + identityPath);
        string[] runtimeCapabilities = ReadStringArray(identity, "runtimeCapabilities");
        string computedBaseId = ComputeBaseId(GetString(identity, "target") ?? string.Empty,
            engineWorkflow, il2cppCodeGeneration, managedSet,
            GetString(identity, "aotAssemblySetSha256") ?? string.Empty,
            GetString(identity, "aotSnapshotSha256") ?? string.Empty,
            GetString(identity, "baseMetaVersionSetSha256") ?? string.Empty,
            GetString(identity, "aotMetadataSetId") ?? string.Empty,
            GetString(identity, "nativeGuardSourceSha256") ?? string.Empty,
            GetString(identity, "nativeManifestSha256") ?? string.Empty,
            GetString(identity, "runtimeProtocol") ?? string.Empty,
            GetString(identity, "runtimeContract") ?? string.Empty, runtimeCapabilities,
            GetString(identity, "runtimeAssetRoot") ?? string.Empty,
            GetString(identity, "baseMetaVersionAssetRoot") ?? string.Empty,
            GetString(identity, "aotAnalysisSnapshotSha256"));
        if (!string.Equals(baseId, computedBaseId, StringComparison.OrdinalIgnoreCase))
            throw new DheException("Base build identity composite baseId is invalid: " + identityPath);
        AotAnalysisSnapshot.Read(identityPath, identity, aotAssemblyNames, assemblyNames);
        return baseId;
    }

    private static string[] ReadPathList(Cli cli, string plural, string singular)
    {
        string? value = cli.Optional(plural) ?? cli.Optional(singular);
        return string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Path.GetFullPath).ToArray();
    }

    private static (string[] IdentityPaths, string[] BaselineRoots,
        string[] NativeManifestPaths, string[] WorkflowValues, string[] VariantValues,
        string[] LabelValues, string[] AotValues) ReadStructuredBaseRegistryEntries(string raw)
    {
        JsonElement entries;
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            entries = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new DheException("BaseEntriesJson must be a JSON array: " + exception.Message);
        }
        if (entries.ValueKind != JsonValueKind.Array)
            throw new DheException("BaseEntriesJson must be a JSON array.");
        var identities = new List<string>();
        var baselines = new List<string>();
        var nativeManifests = new List<string>();
        var workflows = new List<string>();
        var variants = new List<string>();
        var labels = new List<string>();
        var aotRoots = new List<string>();
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new DheException("BaseEntriesJson contains a non-object entry.");
            identities.Add(Path.GetFullPath(GetString(entry, "buildIdentity") ?? string.Empty));
            baselines.Add(Path.GetFullPath(GetString(entry, "baselineRoot") ?? string.Empty));
            nativeManifests.Add(Path.GetFullPath(GetString(entry, "nativeManifest") ?? string.Empty));
            workflows.Add(GetString(entry, "engineWorkflow") ?? string.Empty);
            variants.Add(GetString(entry, "payloadVariantId") ?? string.Empty);
            labels.Add(GetString(entry, "label") ?? string.Empty);
            string? aotRoot = GetString(entry, "aotMetadataRoot");
            aotRoots.Add(string.IsNullOrWhiteSpace(aotRoot) ? "null" : Path.GetFullPath(aotRoot));
        }
        return (identities.ToArray(), baselines.ToArray(), nativeManifests.ToArray(),
            workflows.ToArray(), variants.ToArray(), labels.ToArray(), aotRoots.ToArray());
    }

    /// <summary>
    /// Builds a resource-only DHE release. The command deliberately never
    /// invokes Unity or touches generated native output: a fixed Base Player
    /// consumes the resulting current DLL/MV payload. Multiple baseline roots
    /// validate every supported Base without copying Base-specific data into
    /// the payload. The Player compares its embedded Base MetaVersion with the
    /// one current MetaVersion shipped for each assembly.
    /// </summary>
    private static int ResourceUpdate(Cli cli)
    {
        var currentRoot = RequireDirectory(cli.Require("currentroot"), "Current root");
        var settingsPath = RequireFile(cli.Require("settingsfile"), "HybridCLR settings");
        string primaryCurrentVariantId = cli.Optional("currentvariantid") ?? "default";
        if (!IsPayloadVariantId(primaryCurrentVariantId))
            throw new DheException("CurrentVariantId is invalid.");
        var currentVariantRoots = ReadCurrentVariantRoots(cli, primaryCurrentVariantId,
            currentRoot);
        string? baseRegistryPath = cli.Optional("baseregistry");
        string? previousBaseRegistryPath = cli.Optional("previousbaseregistry");
        string? previousReleaseLedgerPath = cli.Optional("previousreleaseledger");
        string? channelSnapshotPath = cli.Optional("channelsnapshot");
        var outputInputs = currentVariantRoots.Values.Append(settingsPath)
            .Concat(new[] { baseRegistryPath, previousBaseRegistryPath,
                previousReleaseLedgerPath, channelSnapshotPath }.OfType<string>())
            .Concat(ReadPathList(cli, "frozenaotplans", "frozenaotplan"))
            .ToArray();
        var outputRoot = SafeOutputRoot(cli.Require("outputroot"), outputInputs);
        foreach (string input in outputInputs)
            EnsureOutputNotAncestor(outputRoot, input);
        BaseRegistryDocument? baseRegistry = null;
        BaseRegistryDocument? previousBaseRegistry = null;
        string[] baselineRoots;
        string[] nativeManifestPaths;
        string[] buildIdentityPaths;
        string[] frozenAotPlanPaths = Array.Empty<string>();
        string?[] registryAotMetadataRoots = Array.Empty<string?>();
        if (!string.IsNullOrWhiteSpace(baseRegistryPath))
        {
            string[] legacyInputs =
            {
                "baseroots", "baselineroot", "basenativemanifests", "basenativemanifest",
                "basebuildidentities", "basebuildidentity", "aotmetadataroots", "aotmetadataroot"
            };
            if (legacyInputs.Any(name => !string.IsNullOrWhiteSpace(cli.Optional(name))))
                throw new DheException("BaseRegistry cannot be combined with parallel BaseRoots, " +
                    "BaseNativeManifests, BaseBuildIdentities, or AotMetadataRoots arguments.");
            baseRegistry = ReadBaseRegistry(baseRegistryPath);
            previousBaseRegistry = ValidateBaseRegistryLineage(baseRegistry,
                previousBaseRegistryPath);
            baselineRoots = baseRegistry.Entries.Select(entry => entry.BaselineRoot).ToArray();
            nativeManifestPaths = baseRegistry.Entries.Select(entry => entry.NativeManifest).ToArray();
            buildIdentityPaths = baseRegistry.Entries.Select(entry => entry.BuildIdentity).ToArray();
            frozenAotPlanPaths = ReadPathList(cli, "frozenaotplans", "frozenaotplan");
            if (baseRegistry.Entries.Any(entry => entry.AotMetadataRoot != null))
                registryAotMetadataRoots = baseRegistry.Entries.Select(entry =>
                    entry.AotMetadataRoot).ToArray();
            foreach (string variantId in baseRegistry.Entries.Select(entry => entry.PayloadVariantId)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!currentVariantRoots.ContainsKey(variantId))
                    throw new DheException("No current root was supplied for Base payloadVariantId: " +
                        variantId);
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(previousBaseRegistryPath))
                throw new DheException("PreviousBaseRegistry requires BaseRegistry.");
            if (currentVariantRoots.Count != 1 ||
                !string.Equals(primaryCurrentVariantId, "default",
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("Named current payload variants require a BaseRegistry with " +
                    "payloadVariantId entries.");
            baselineRoots = (cli.Optional("baseroots") ?? cli.Require("baselineroot"))
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(path => RequireDirectory(path, "DHE base snapshot root")).ToArray();
            var nativeManifestValue = cli.Optional("basenativemanifests") ?? cli.Optional("basenativemanifest") ??
                throw new DheException("Missing -BaseNativeManifests (one universal native manifest per BaseRoot).");
            nativeManifestPaths = nativeManifestValue
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(path => RequireFile(path, "Base native guard manifest"))
                .ToArray();
            var buildIdentityValue = cli.Optional("basebuildidentities") ?? cli.Optional("basebuildidentity") ??
                throw new DheException("Missing -BaseBuildIdentities (one immutable build identity per BaseRoot).");
            buildIdentityPaths = buildIdentityValue
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(path => RequireFile(path, "Base Player build identity"))
                .ToArray();
            frozenAotPlanPaths = ReadPathList(cli, "frozenaotplans", "frozenaotplan");
        }
        ResourceReleaseContext resourceRelease = PrepareResourceReleaseContext(cli,
            baseRegistry, previousBaseRegistry);
        foreach (string inputRoot in currentVariantRoots.Values.Concat(baselineRoots)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            EnsureOutputOutsideRoot(outputRoot, inputRoot);
        if (!string.IsNullOrWhiteSpace(resourceRelease.ChannelStateRoot))
            EnsureOutputOutsideRoot(outputRoot, resourceRelease.ChannelStateRoot);
        if (baselineRoots.Length == 0) throw new DheException("At least one base snapshot root is required.");
        if (baselineRoots.Distinct(StringComparer.OrdinalIgnoreCase).Count() != baselineRoots.Length)
            throw new DheException("BaseRoots must not contain duplicate Base snapshot roots.");
        if (nativeManifestPaths.Length != baselineRoots.Length)
            throw new DheException("BaseRoots and BaseNativeManifests must have the same number of entries.");
        if (buildIdentityPaths.Length != baselineRoots.Length)
            throw new DheException("BaseRoots and BaseBuildIdentities must have the same number of entries.");
        if (frozenAotPlanPaths.Length != 0 && frozenAotPlanPaths.Length != baselineRoots.Length)
            throw new DheException("FrozenAotPlans must contain one plan per BaseRoot.");
        var baseIdentities = buildIdentityPaths.Select(path => ReadJson<JsonElement>(path)).ToArray();
        if (baseIdentities.Any(identity => GetInt(identity, "identityVersion") != 1))
            throw new DheException(
                "Every supported Base must use the current DHE build identity.");
        if (baseRegistry != null)
        {
            for (int index = 0; index < baseRegistry.Entries.Length; index++)
            {
                string identityBaseId = GetString(baseIdentities[index], "baseId") ?? string.Empty;
                if (!string.Equals(identityBaseId, baseRegistry.Entries[index].BaseId,
                        StringComparison.OrdinalIgnoreCase))
                    throw new DheException("DHE Base registry baseId does not match its build identity: " +
                        baseRegistry.Entries[index].BuildIdentity);
                string identityWorkflow = GetString(baseIdentities[index], "engineWorkflow") ??
                    string.Empty;
                if (!string.Equals(identityWorkflow,
                        baseRegistry.Entries[index].EngineWorkflow, StringComparison.Ordinal))
                    throw new DheException("DHE Base registry engineWorkflow does not match its build identity: " +
                        baseRegistry.Entries[index].BuildIdentity);
            }
        }
        string runtimeAssetRoot = RequireCommonBaseAssetRoot(baseIdentities,
            "runtimeAssetRoot");
        string baseMetaVersionAssetRoot = RequireCommonBaseAssetRoot(baseIdentities,
            "baseMetaVersionAssetRoot");
        if (!baseMetaVersionAssetRoot.StartsWith(runtimeAssetRoot,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException(
                "Every Base MetaVersion asset root must be below the common runtime asset root.");
        ValidateRequestedAssetRoot(cli.Optional("runtimeassetroot"), runtimeAssetRoot,
            "RuntimeAssetRoot");
        ValidateRequestedAssetRoot(cli.Optional("basemetaversionassetroot"),
            baseMetaVersionAssetRoot, "BaseMetaVersionAssetRoot");
        var settings = Settings.Read(settingsPath);
        var names = settings.Dhe;
        if (names.Length == 0) throw new DheException("No DHE assemblies are configured.");
        var payloadRoot = Path.Combine(outputRoot, "payload");
        var auditRoot = Path.Combine(outputRoot, "audit");
        var manifestPath = Path.Combine(outputRoot, "dhe-resource-update.json");
        var runtimePlanPath = Path.Combine(outputRoot, "dhe-runtime-plan.json");
        var validationPath = Path.Combine(outputRoot, "dhe-resource-update-validation.json");
        var releaseLedgerPath = Path.Combine(outputRoot, ReleaseLedgerFileName);
        Directory.CreateDirectory(outputRoot);
        File.Delete(manifestPath);
        File.Delete(runtimePlanPath);
        File.Delete(validationPath);
        File.Delete(releaseLedgerPath);
        if (Directory.Exists(payloadRoot)) Directory.Delete(payloadRoot, true);
        if (Directory.Exists(auditRoot)) Directory.Delete(auditRoot, true);
        Directory.CreateDirectory(payloadRoot);
        Directory.CreateDirectory(auditRoot);
        var currentVariants = new Dictionary<string, CurrentVariantData>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var variantRoot in currentVariantRoots.OrderBy(pair => pair.Key,
                     StringComparer.OrdinalIgnoreCase))
        {
            var currentRecords = names.Select(name => new
            {
                name,
                path = RequireFile(Path.Combine(variantRoot.Value, name + ".dll"),
                    name + " current assembly (" + variantRoot.Key + ")"),
            }).ToArray();
            var variant = new CurrentVariantData
            {
                VariantId = variantRoot.Key,
                Root = variantRoot.Value,
                CurrentSetHash = NamedAssemblySetHash(currentRecords.Select(record =>
                    (record.name, record.path))),
                Snapshots = new Dictionary<string, MetaVersionSnapshot>(StringComparer.OrdinalIgnoreCase),
                PayloadFiles = new List<object>(),
                RuntimeAssemblies = new List<object>(),
            };
            string variantPayloadPrefix = string.Equals(variantRoot.Key, "default",
                StringComparison.OrdinalIgnoreCase)
                ? "payload/"
                : "payload/variants/" + variantRoot.Key + "/";
            string variantAuditPrefix = string.Equals(variantRoot.Key, "default",
                StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : variantRoot.Key + "/";
            foreach (var record in currentRecords)
            {
                var snapshot = MetaVersionSnapshot.Create(record.path);
                variant.Snapshots.Add(record.name, snapshot);
                variant.AddressTakenFields = (variant.AddressTakenFields ?? Array.Empty<string>())
                    .Concat(snapshot.AddressTakenFieldIdentities)
                    .Distinct(StringComparer.Ordinal).ToArray();
                var dllTarget = Path.Combine(payloadRoot, variantPayloadPrefix["payload/".Length..],
                    record.name + ".dll.bytes");
                var mvTarget = Path.Combine(payloadRoot, variantPayloadPrefix["payload/".Length..],
                    record.name + ".mv.bytes");
                var mvJsonTarget = string.IsNullOrEmpty(variantAuditPrefix)
                    ? Path.Combine(auditRoot, record.name + ".mv.json")
                    : Path.Combine(auditRoot, variantAuditPrefix, record.name + ".mv.json");
                Directory.CreateDirectory(Path.GetDirectoryName(dllTarget)!);
                Directory.CreateDirectory(Path.GetDirectoryName(mvJsonTarget)!);
                File.Copy(record.path, dllTarget, true);
                File.WriteAllBytes(mvTarget, snapshot.ToBinary());
                WriteJson(mvJsonTarget, snapshot.ToJson(record.path));
                var dllAsset = variantPayloadPrefix + record.name + ".dll.bytes";
                var mvAsset = variantPayloadPrefix + record.name + ".mv.bytes";
                variant.PayloadFiles.Add(new
                {
                    assemblyName = record.name,
                    dll = dllAsset,
                    currentMetaVersion = mvAsset,
                    dllSha256 = Sha256File(dllTarget),
                    currentMetaVersionSha256 = Sha256File(mvTarget),
                });
                variant.RuntimeAssemblies.Add(new
                {
                    assemblyName = record.name,
                    executionMode = "dhe-differential",
                    current = runtimeAssetRoot + dllAsset,
                    currentMetaVersion = runtimeAssetRoot + mvAsset,
                    baseMetaVersion = baseMetaVersionAssetRoot + record.name + ".mv.bytes",
                    currentSha256 = Sha256File(dllTarget),
                    currentMetaVersionSha256 = Sha256File(mvTarget),
                });
            }
            currentVariants.Add(variant.VariantId, variant);
        }
        var primaryVariant = currentVariants[primaryCurrentVariantId];
        var currentSetHash = primaryVariant.CurrentSetHash;
        string payloadVariantSetHash = NamedVariantSetHash(currentVariants.Values.Select(variant =>
            (variant.VariantId, variant.CurrentSetHash)));
        string payloadModel = currentVariants.Count == 1 &&
                              string.Equals(primaryCurrentVariantId, "default",
                                  StringComparison.OrdinalIgnoreCase)
            ? "single-current-payload"
            : "variant-current-payload";
        string?[] aotMetadataRoots;
        var explicitAotMetadataRoots = cli.GetList("aotmetadataroots")
            .Select(path => RequireDirectory(path, "AOT metadata root")).ToArray();
        var sharedAotMetadataRoot = cli.Optional("aotmetadataroot");
        if (baseRegistry != null && (explicitAotMetadataRoots.Length != 0 ||
                !string.IsNullOrWhiteSpace(sharedAotMetadataRoot)))
            throw new DheException("BaseRegistry entries own their AotMetadataRoot; do not pass " +
                "AotMetadataRoots separately.");
        if (baseRegistry != null)
        {
            aotMetadataRoots = registryAotMetadataRoots;
        }
        else
        {
            aotMetadataRoots = explicitAotMetadataRoots;
        }
        if (aotMetadataRoots.Length != 0 && !string.IsNullOrWhiteSpace(sharedAotMetadataRoot))
            throw new DheException("Use either -AotMetadataRoots or -AotMetadataRoot, not both.");
        if (baseRegistry == null && aotMetadataRoots.Length == 0 && !string.IsNullOrWhiteSpace(sharedAotMetadataRoot))
        {
            string root = RequireDirectory(sharedAotMetadataRoot, "AOT metadata root");
            aotMetadataRoots = Enumerable.Repeat(root, baselineRoots.Length).ToArray();
        }
        if (aotMetadataRoots.Length != 0 && aotMetadataRoots.Length != baselineRoots.Length)
            throw new DheException("AotMetadataRoots must contain one entry per BaseRoot.");
        if (baseRegistry == null && aotMetadataRoots.Length == 0 && settings.Patch.Length != 0)
            throw new DheException(
                "AotMetadataRoots is required when patchAOTAssemblies is non-empty; " +
                "pass one metadata root per BaseRoot.");

        string? baseRegistryAuditPath = null;
        string? baseRegistryAuditSha256 = null;
        string? baseRegistryParentAuditPath = null;
        string? baseRegistryParentAuditSha256 = null;
        if (baseRegistry != null)
        {
            // Keep the exact authenticated registry bytes beside the release
            // evidence. The registry's external paths remain build-host input;
            // the archived copy is intentionally used for audit/hash checks only.
            baseRegistryAuditPath = "audit/dhe-base-registry.json";
            string auditPath = ResolveContainedPath(outputRoot, baseRegistryAuditPath,
                "DHE Base registry audit copy");
            File.Copy(baseRegistry.SourcePath, auditPath, true);
            baseRegistryAuditSha256 = Sha256File(auditPath);
            if (!string.Equals(baseRegistryAuditSha256, baseRegistry.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("DHE Base registry audit copy hash mismatch.");
            if (previousBaseRegistry != null)
            {
                baseRegistryParentAuditPath = "audit/dhe-base-registry-parent.json";
                string parentAuditPath = ResolveContainedPath(outputRoot,
                    baseRegistryParentAuditPath, "DHE parent Base registry audit copy");
                File.Copy(previousBaseRegistry.SourcePath, parentAuditPath, true);
                baseRegistryParentAuditSha256 = Sha256File(parentAuditPath);
                if (!string.Equals(baseRegistryParentAuditSha256,
                        previousBaseRegistry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new DheException(
                        "DHE parent Base registry audit copy hash mismatch.");
            }
        }

        var payloadFiles = primaryVariant.PayloadFiles;
        var runtimeAssemblies = primaryVariant.RuntimeAssemblies;

        var metadataSetsById = new Dictionary<string, ResourceAotMetadataSet>(
            StringComparer.OrdinalIgnoreCase);
        var metadataSetIdsByBase = new string[baselineRoots.Length];
        for (int baseIndex = 0; baseIndex < baselineRoots.Length; baseIndex++)
        {
            var setBytes = new List<(string name, byte[] bytes)>();
            var setAssemblies = new List<ResourceAotMetadataPayload>();
            string? metadataRoot = aotMetadataRoots.Length == 0 ? null : aotMetadataRoots[baseIndex];
            if (metadataRoot != null)
            {
                foreach (var metadataName in settings.Patch.OrderBy(value => value, StringComparer.Ordinal))
                {
                    var source = RequireFile(Path.Combine(metadataRoot, metadataName + ".dll"),
                        metadataName + " AOT metadata");
                    byte[] bytes = File.ReadAllBytes(source);
                    string hash = Sha256Bytes(bytes);
                    // Keep the on-disk name below Windows' legacy MAX_PATH
                    // limit even when a project/worktree path is long. The
                    // complete SHA-256 remains the authenticated identity in
                    // the runtime plan and is still checked before staging
                    // and loading; the 128-bit prefix is only a file-name
                    // lookup key.
                    string relativePath = "payload/aot-metadata/" + hash.Substring(0, 32) + ".bytes";
                    string target = ResolveContainedPath(outputRoot, relativePath,
                        "AOT metadata blob");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (File.Exists(target) && !string.Equals(Sha256File(target), hash,
                            StringComparison.OrdinalIgnoreCase))
                        throw new DheException("AOT metadata short-name collision: " + hash);
                    if (!File.Exists(target)) File.WriteAllBytes(target, bytes);
                    setBytes.Add((metadataName, bytes));
                    setAssemblies.Add(new ResourceAotMetadataPayload(metadataName,
                        "resource-update", hash, string.Empty,
                        runtimeAssetRoot + relativePath));
                }
            }
            string setId = NamedByteSetHash(setBytes);
            metadataSetIdsByBase[baseIndex] = setId;
            var set = new ResourceAotMetadataSet(setId, setAssemblies.ToArray());
            if (metadataSetsById.TryGetValue(setId, out ResourceAotMetadataSet? existing))
            {
                if (!JsonSerializer.Serialize(existing, Json).Equals(
                        JsonSerializer.Serialize(set, Json), StringComparison.Ordinal))
                    throw new DheException("AOT metadata set identity collision: " + setId);
            }
            else metadataSetsById.Add(setId, set);
        }
        ResourceAotMetadataSet[] runtimeAotMetadataSets = metadataSetsById.Values
            .OrderBy(set => set.AotMetadataSetId, StringComparer.Ordinal).ToArray();

        var candidateBases = new List<object>();
        var resourceBaseSelections = new List<ResourceAotMetadataBaseSelection>();
        var candidateBaseIdentityKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var releaseErrors = new List<string>();
        for (var baseIndex = 0; baseIndex < baselineRoots.Length; baseIndex++)
        {
            var baselineRoot = baselineRoots[baseIndex];
            var nativeManifestPath = nativeManifestPaths[baseIndex];
            var buildIdentityPath = buildIdentityPaths[baseIndex];
            var nativeManifest = ReadJson<JsonElement>(nativeManifestPath);
            var buildIdentity = baseIdentities[baseIndex];
            string aotMetadataSetId = metadataSetIdsByBase[baseIndex];
            string payloadVariantId = baseRegistry == null
                ? "default"
                : baseRegistry.Entries[baseIndex].PayloadVariantId;
            CurrentVariantData currentVariant = currentVariants[payloadVariantId];
            var baseTarget = GetString(buildIdentity, "target") ?? string.Empty;
            var baseEngineWorkflow = GetString(buildIdentity, "engineWorkflow") ?? string.Empty;
            var baseIl2CppCodeGeneration = GetString(buildIdentity,
                "il2cppCodeGeneration") ?? string.Empty;
            string expectedEngineWorkflow = baseRegistry == null ? baseEngineWorkflow :
                baseRegistry.Entries[baseIndex].EngineWorkflow;
            var baseAotSnapshotSha256 = GetString(buildIdentity, "aotSnapshotSha256") ?? string.Empty;
            var baseNativeGuardSourceSha256 = GetString(buildIdentity,
                "nativeGuardSourceSha256") ?? string.Empty;
            var baseRuntimeProtocol = GetString(nativeManifest, "runtimeProtocol") ?? string.Empty;
            var baseNativeRuntimeContract = GetString(nativeManifest, "runtimeContract") ?? string.Empty;
            var baseNativeRuntimeCapabilities = nativeManifest.TryGetProperty("runtimeCapabilities",
                    out JsonElement capabilityValues) && capabilityValues.ValueKind == JsonValueKind.Array
                ? capabilityValues.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString() ?? string.Empty).ToArray()
                : Array.Empty<string>();
            if (!string.Equals(GetString(nativeManifest, "guardMode"), "universal",
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("Base native guard manifest must be generated in universal mode: " +
                    nativeManifestPath);
            var guardedMethods = ReadNativeStableMethodIds(nativeManifest, "methods");
            var interpreterOnlyMethods = ReadNativeStableMethodIds(nativeManifest, "interpreterOnlyMethods");
            if (guardedMethods.Count == 0)
                throw new DheException("Base native guard manifest contains no stable method IDs: " +
                    nativeManifestPath + ". Rebuild the Base with the current DHE package.");

            var identityAssemblyNames = ReadIdentityAssemblyNames(buildIdentity,
                buildIdentityPath);
            var baseAotAssemblyNames = ReadIdentityAotAssemblyNames(buildIdentity,
                buildIdentityPath);
            var currentNameSet = new HashSet<string>(names,
                StringComparer.OrdinalIgnoreCase);
            var baselineNameSet = new HashSet<string>(identityAssemblyNames,
                StringComparer.OrdinalIgnoreCase);
            var baseAotNameSet = new HashSet<string>(baseAotAssemblyNames,
                StringComparer.OrdinalIgnoreCase);
            var unsupported = new List<string>();
            foreach (MetaVersionSnapshot snapshot in currentVariant.Snapshots.Values)
            {
                unsupported.AddRange(snapshot.AssemblyReferences.Keys.Where(reference =>
                        !baseAotNameSet.Contains(reference) && !currentNameSet.Contains(reference))
                    .Select(reference => "current-reference-unavailable:" + snapshot.AssemblyName + ":" + reference));
            }
            if (!baseAotNameSet.IsSupersetOf(baselineNameSet))
                throw new DheException(
                    "Base DHE assembly set is not a subset of the complete AOT inventory: " +
                    buildIdentityPath);
            // A Base may be older and therefore miss assemblies that are in the
            // current payload. The reverse direction is not safe: removing an
            // assembly cannot unload its already registered AOT image, so the
            // release is rejected instead of silently producing a partial plan.
            unsupported.AddRange(identityAssemblyNames.Where(name => !currentNameSet.Contains(name))
                .Select(name => "assembly-removed-from-current:" + name));
            var baselineRecords = identityAssemblyNames.Select(name => new
            {
                name,
                path = RequireFile(Path.Combine(baselineRoot, name + ".dll"), name + " base assembly"),
            }).ToArray();
            var managedAssemblySetSha256 = NamedAssemblySetHash(
                baselineRecords.Select(record => (record.name, record.path)));
            var baselineSnapshots = baselineRecords.ToDictionary(record => record.name,
                record => MetaVersionSnapshot.Create(record.path), StringComparer.Ordinal);
            var aotAnalysis = AotAnalysisSnapshot.Read(buildIdentityPath, buildIdentity,
                baseAotAssemblyNames, identityAssemblyNames);
            var execution = ResourceExecutionPlanner.Compile(
                baselineRecords.Where(record => currentVariant.Snapshots.ContainsKey(record.name)).Select(record => record.path),
                names.Select(name => Path.Combine(currentVariant.Root, name + ".dll")),
                aotAnalysis?.OrdinaryAssemblyPaths ?? Array.Empty<string>());
            // Preserve every obligation here. Frozen admission below may only
            // discharge exact entries after validating source-bound selections
            // and the complete ordinary native guard inventory.
            unsupported.AddRange(execution.UnsupportedChanges);
            if (execution.Impact.ChangedValueTypes.Length != 0)
            {
                if (aotAnalysis == null)
                    unsupported.Add("current-storage-aot-boundary-inventory-not-bound");
                if (baseEngineWorkflow != "Unity2022Fgs")
                    unsupported.Add("current-storage-engine-not-qualified:" + baseEngineWorkflow);
            }
            var baseId = GetString(buildIdentity, "baseId") ?? string.Empty;
            var baseMvSet = new List<(string name, byte[] bytes)>();
            var assemblyCompatibility = new List<object>();
            var uncovered = new List<string>();
            var requiredRuntimeCapabilities = new HashSet<string>(StringComparer.Ordinal)
            {
                "resource-update-plan-integrity-v1",
                "resource-update-aot-metadata-set-selection-v1",
                "aot-inline-entry-guards-v1",
            };
            if (currentVariant.Snapshots.Values.Any(snapshot => snapshot.HasEmbeddedNullStringDefaults))
                requiredRuntimeCapabilities.Add("length-preserved-constant-strings-v1");
            if (names.Except(identityAssemblyNames, StringComparer.OrdinalIgnoreCase).Any())
                requiredRuntimeCapabilities.Add("mixed-interpreter-source-batch-v1");
            if (execution.Impact.StaticValueFields.Length != 0)
            {
                requiredRuntimeCapabilities.Add("current-static-value-storage-v1");
                requiredRuntimeCapabilities.Add("shared-type-initialization-v1");
            }
            if (runtimeAotMetadataSets.Single(set => string.Equals(set.AotMetadataSetId,
                    aotMetadataSetId, StringComparison.OrdinalIgnoreCase)).Assemblies.Length > 0)
                requiredRuntimeCapabilities.Add("resource-update-aot-metadata-path-v1");
            if (!string.Equals(baseRuntimeProtocol,
                    ResourceUpdateCompatibility.RuntimeProtocol, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(baseNativeRuntimeContract) ||
                baseNativeRuntimeCapabilities.Length == 0 ||
                baseNativeRuntimeCapabilities.Any(string.IsNullOrWhiteSpace) ||
                baseNativeRuntimeCapabilities.Distinct(StringComparer.Ordinal).Count() !=
                    baseNativeRuntimeCapabilities.Length)
                unsupported.Add("base-native-runtime-protocol:" + nativeManifestPath);
            foreach (var baselineRecord in baselineRecords)
            {
                var baselineSnapshot = baselineSnapshots[baselineRecord.name];
                var baselineMvBytes = baselineSnapshot.ToBinary();
                baseMvSet.Add((baselineRecord.name, baselineMvBytes));
                if (!currentVariant.Snapshots.TryGetValue(baselineRecord.name,
                        out MetaVersionSnapshot? currentSnapshot))
                {
                    continue;
                }
                var compatibility = ResourceUpdateCompatibility.AnalyzeFailClosed(baselineSnapshot,
                    currentSnapshot, currentVariant.AddressTakenFields,
                    usesUnresolvedCallStubs: baseEngineWorkflow != "Unity2021Standard",
                    currentAssemblySet: currentVariant.Snapshots.Values,
                    currentStorageTypes: execution.Plans.TryGetValue(baselineRecord.name, out var executionPlan)
                        ? currentSnapshot.Types.Where(type => executionPlan.CurrentStorageTypeTokens.Contains(type.Token)).Select(type => type.StableId)
                        : Array.Empty<string>(),
                    currentExecutionMethodTokens: executionPlan?.CurrentExecutionMethodTokens,
                    currentGenericContextMethodTokens: executionPlan?.CurrentGenericContextMethodTokens,
                    baselineAssemblySet: baselineSnapshots.Values);
                requiredRuntimeCapabilities.UnionWith(
                    compatibility.RequiredRuntimeCapabilities);
                var missingGuards = compatibility.GuardRequiredMethods.Where(method =>
                    !guardedMethods.Contains(NativeStableMethodKey(baselineRecord.name, method.StableId)) &&
                    !interpreterOnlyMethods.Contains(NativeStableMethodKey(baselineRecord.name,
                        method.StableId))).ToArray();
                uncovered.AddRange(missingGuards.Select(method => baselineRecord.name + ":" +
                    method.StableId));
                unsupported.AddRange(compatibility.UnsupportedChanges.Select(change =>
                    baselineRecord.name + ":" + change));
                assemblyCompatibility.Add(new
                {
                    assemblyName = baselineRecord.name,
                    executionMode = "dhe-differential",
                    baselineAssemblySha256 = baselineSnapshot.AssemblySha256,
                    baseMetaVersionSha256 = Sha256Bytes(baselineMvBytes),
                    compatible = compatibility.Compatible && missingGuards.Length == 0,
                    compatibilityPolicy = ResourceUpdateCompatibility.Policy,
                    unchangedMethodCount = compatibility.UnchangedMethodCount,
                    changedMethodCount = compatibility.ChangedMethodCount,
                    bodyOnlyChangedMethodCount = compatibility.BodyOnlyChangedMethodCount,
					dependencyChangedMethodCount = compatibility.DependencyChangedMethodCount,
                    removedMethodCount = compatibility.RemovedMethodCount,
                    addedMethodCount = compatibility.AddedMethodCount,
					removedFieldCount = compatibility.RemovedFieldCount,
					addedFieldCount = compatibility.AddedFieldCount,
                    changedExistingTypeCount = compatibility.ChangedExistingTypeCount,
                    removedTypeCount = compatibility.RemovedTypeCount,
                    addedTypeCount = compatibility.AddedTypeCount,
                    guardRequiredMethodCount = compatibility.GuardRequiredMethods.Length,
                    guardCoveredMethodCount = compatibility.GuardRequiredMethods.Length - missingGuards.Length,
                    uncoveredStableMethodIds = missingGuards.Select(method => method.StableId).ToArray(),
                    unsupportedChangeCount = compatibility.UnsupportedChanges.Length,
                    unsupportedChanges = compatibility.UnsupportedChanges,
                });
            }
            foreach (string currentName in names.Where(name => !baselineNameSet.Contains(name))
                         .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                bool classified = TryClassifyAssemblyExecutionMode(currentName, baselineNameSet,
                    baseAotNameSet, out string executionMode, out string conflict);
                if (!classified) unsupported.Add(conflict);
                // There is no Base MV to compare for a newly introduced
                // assembly. Interpreter-only loading is valid only when the
                // assembly is also absent from the Base Player's complete AOT
                // inventory.
                assemblyCompatibility.Add(new
                {
                    assemblyName = currentName,
                    executionMode,
                    baselineAssemblySha256 = (string?)null,
                    baseMetaVersionSha256 = (string?)null,
                    compatible = classified,
                    compatibilityPolicy = ResourceUpdateCompatibility.Policy,
                    unchangedMethodCount = 0,
                    changedMethodCount = 0,
                    bodyOnlyChangedMethodCount = 0,
                    dependencyChangedMethodCount = 0,
                    removedMethodCount = 0,
                    addedMethodCount = 0,
                    removedFieldCount = 0,
                    addedFieldCount = 0,
                    changedExistingTypeCount = 0,
                    removedTypeCount = 0,
                    addedTypeCount = 0,
                    guardRequiredMethodCount = 0,
                    guardCoveredMethodCount = 0,
                    uncoveredStableMethodIds = Array.Empty<string>(),
                    unsupportedChangeCount = classified ? 0 : 1,
                    unsupportedChanges = classified ? Array.Empty<string>() : new[] { conflict },
                });
            }
            var baseMvSetHash = NamedByteSetHash(baseMvSet);
            var identityErrors = ValidateResourceBaseIdentity(buildIdentity, buildIdentityPath,
                nativeManifestPath, managedAssemblySetSha256, baseMvSetHash,
                aotMetadataSetId, runtimeAssetRoot, baseMetaVersionAssetRoot,
                expectedEngineWorkflow);
            unsupported.AddRange(identityErrors);
            var nativeManifestSha256 = Sha256File(nativeManifestPath);
            var baseIdentityKey = baseId;
            if (!candidateBaseIdentityKeys.Add(baseIdentityKey))
                unsupported.Add("duplicate-base-identity:" + baseId);
            var assemblyModes = new List<ResourceAssemblyMode>();
            foreach (string name in names.OrderBy(value => value,
                         StringComparer.OrdinalIgnoreCase))
            {
                if (TryClassifyAssemblyExecutionMode(name, baselineNameSet, baseAotNameSet,
                        out string executionMode, out _))
                    assemblyModes.Add(new ResourceAssemblyMode(name, executionMode,
                        execution.Plans.TryGetValue(name, out var selectedExecution) ? selectedExecution : null));
            }
            var frozenAotSources = new List<object>();
            FrozenAotAdmissionProof? frozenAdmission = null;
            if (aotAnalysis == null && requiredRuntimeCapabilities.Contains(FrozenFieldValidation.Capability))
                unsupported.Add("parent-evolution-requires-frozen-base-field-validation:" + baseId);
            if (frozenAotPlanPaths.Length != 0 || aotAnalysis != null &&
                (execution.Impact.ChangedValueTypes.Length != 0 || requiredRuntimeCapabilities.Contains(FrozenFieldValidation.Capability)))
            {
                if (aotAnalysis == null) throw new DheException("Frozen AOT planning requires an authenticated Base snapshot: " + baseId);
                string[] currentPaths = names.Select(name => Path.Combine(currentVariant.Root, name + ".dll")).ToArray();
                if (!FrozenAotSourcePlan.CurrentSetHash(currentPaths).Equals(currentVariant.CurrentSetHash, StringComparison.OrdinalIgnoreCase))
                    throw new DheException("Current inputs changed before frozen source compilation.");
                var frozen = FrozenAotAdaptation.Compile(aotAnalysis, baselineRecords.Select(record => record.path), currentPaths, execution.Impact);
                if (frozen.Assemblies.Any(source => source.Methods.Any(method => method.Reasons.Contains(FrozenFieldValidation.Reason))))
                {
                    requiredRuntimeCapabilities.Add(FrozenFieldValidation.Capability);
                    requiredRuntimeCapabilities.Add(ResourceUpdateCompatibility.FrozenBaseInstanceFrameCapability);
                }
                if (!FrozenAotSourcePlan.CurrentSetHash(currentPaths).Equals(currentVariant.CurrentSetHash, StringComparison.OrdinalIgnoreCase))
                    throw new DheException("Current inputs changed during frozen source compilation.");
                JsonElement[] sourceRows = frozenAotPlanPaths.Length != 0
                    ? FrozenAotSourcePlan.ValidateCompiled(ReadJson<JsonElement>(frozenAotPlanPaths[baseIndex]),
                        baseId, aotAnalysis.Sha256, currentVariant.CurrentSetHash, frozen)
                    : FrozenAotSourcePlan.Materialize(aotAnalysis, frozen, baseId, outputRoot);
                if (frozen.Assemblies.Length != 0)
                {
                    frozenAdmission = FrozenAotAdmission.Validate(aotAnalysis, frozen, execution, nativeManifest);
                    unsupported.RemoveAll(reason => execution.UnsupportedChanges.Contains(reason, StringComparer.Ordinal));
                    unsupported.AddRange(frozenAdmission.UnsupportedChanges);
                }
                foreach (JsonElement source in sourceRows)
                {
                    string sourceName = NormalizeName(GetString(source, "assemblyName") ?? string.Empty);
                    string sourceFile = GetString(source, "sourceFile") ?? string.Empty;
                    string baseMetaFile = GetString(source, "baseMetaVersionFile") ?? string.Empty;
                    string sourceHash = GetString(source, "sourceSha256") ?? string.Empty;
                    string mvHash = GetString(source, "baseMetaVersionSha256") ?? string.Empty;
                    if (sourceName.Length == 0 || !File.Exists(sourceFile) || !File.Exists(baseMetaFile) ||
                        !IsHex(sourceHash, 64, 64) ||
                        !IsHex(mvHash, 64, 64) ||
                        !source.TryGetProperty("currentStorageTypeTokens", out JsonElement sourceTypes) ||
                        !source.TryGetProperty("currentExecutionMethodTokens", out JsonElement sourceMethods) ||
                        !source.TryGetProperty("excludedBaseTypeTokens", out JsonElement excluded) ||
                        !source.TryGetProperty("genericContextMethodTokens", out JsonElement conditional))
                        throw new DheException("Frozen AOT source record is invalid: " + baseId + "/" + sourceName);
                    uint[] methodTokens = sourceMethods.EnumerateArray().Select(value => value.GetUInt32()).ToArray();
                    uint[] conditionalTokens = conditional.EnumerateArray().Select(value => value.GetUInt32()).ToArray();
                    if (!conditionalTokens.SequenceEqual(conditionalTokens.Distinct().OrderBy(token => token)) ||
                        conditionalTokens.Any(token => (token >> 24) != 6 || (token & 0xffffffu) == 0 || !methodTokens.Contains(token)))
                        throw new DheException("Frozen generic context selection is invalid: " + baseId + "/" + sourceName);
                    if (conditionalTokens.Length != 0) requiredRuntimeCapabilities.Add("frozen-generic-context-dispatch-v1");
                    string sourceRelative = "payload/frozen-aot/" + baseId.ToLowerInvariant() + "/" + sourceName + ".dll.bytes";
                    string mvRelative = "payload/frozen-aot/" + baseId.ToLowerInvariant() + "/" + sourceName + ".mv.bytes";
                    string sourceTarget = ResolveContainedPath(outputRoot, sourceRelative, "frozen AOT source");
                    string mvTarget = ResolveContainedPath(outputRoot, mvRelative, "frozen AOT source MV");
                    Directory.CreateDirectory(Path.GetDirectoryName(sourceTarget)!);
                    File.Copy(sourceFile, sourceTarget, true);
                    if (!Path.GetFullPath(baseMetaFile).Equals(Path.GetFullPath(mvTarget), StringComparison.OrdinalIgnoreCase))
                        File.Copy(baseMetaFile, mvTarget, true);
                    if (!string.Equals(Sha256File(sourceTarget), sourceHash, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(Sha256File(mvTarget), mvHash, StringComparison.OrdinalIgnoreCase))
                        throw new DheException("Frozen AOT source copied hash mismatch: " + baseId + "/" + sourceName);
                    using (var module = dnlib.DotNet.ModuleDefMD.Load(sourceTarget))
                    {
                        if (module.GlobalType.HasMethods || module.GlobalType.HasFields)
                        {
                            requiredRuntimeCapabilities.Add("deferred-aot-module-initialization-v1");
                            requiredRuntimeCapabilities.Add("aot-module-token-resolution-v1");
                        }
                        if (MetaVersionSnapshot.HasEmbeddedNullStringConstants(module))
                            requiredRuntimeCapabilities.Add("length-preserved-constant-strings-v1");
                    }
                    frozenAotSources.Add(new
                    {
                        assemblyName = sourceName,
                        source = runtimeAssetRoot + sourceRelative,
                        sourceSha256 = sourceHash,
                        baseMetaVersion = runtimeAssetRoot + mvRelative,
                        baseMetaVersionSha256 = mvHash,
                        currentStorageTypeTokens = sourceTypes,
                        currentExecutionMethodTokens = sourceMethods,
                        excludedBaseTypeTokens = excluded,
                        genericContextMethodTokens = conditional,
                        sourceKind = "frozen-base-aot",
                    });
                }
                if (frozenAotSources.Count != 0)
                {
                    requiredRuntimeCapabilities.Add("frozen-aot-source-v1");
                    requiredRuntimeCapabilities.Add("frozen-aot-snapshot-source-binding-v1");
                    string snapshotRelative = "payload/frozen-aot/" + baseId.ToLowerInvariant() + "/snapshot.json";
                    string snapshotTarget = ResolveContainedPath(outputRoot, snapshotRelative, "frozen AOT snapshot");
                    File.Copy(aotAnalysis.ManifestPath, snapshotTarget, true);
                    if (!string.Equals(Sha256File(snapshotTarget), aotAnalysis.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new DheException("Frozen AOT snapshot changed during resource generation: " + baseId);
                }
            }
            string[] missingRuntimeCapabilities = requiredRuntimeCapabilities
                .Except(baseNativeRuntimeCapabilities, StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (!ResourceUpdateCompatibility.CanExecuteUpdate(baseRuntimeProtocol,
                    baseNativeRuntimeContract,
                    baseNativeRuntimeCapabilities, requiredRuntimeCapabilities) &&
                missingRuntimeCapabilities.Length == 0)
                unsupported.Add("base-runtime-protocol-cannot-execute-update:" + baseRuntimeProtocol);
            unsupported.AddRange(missingRuntimeCapabilities.Select(capability =>
                "base-missing-runtime-capability:" + capability));
            bool baseCompatible = uncovered.Count == 0 && unsupported.Count == 0;
            var baseRecord = new
            {
                baseId,
                target = baseTarget,
                engineWorkflow = baseEngineWorkflow,
                il2cppCodeGeneration = baseIl2CppCodeGeneration,
                payloadVariantId,
                currentAssemblySetSha256 = currentVariant.CurrentSetHash,
                managedAssemblySetSha256,
                aotAssemblySetSha256 = GetString(buildIdentity, "aotAssemblySetSha256"),
                aotAssemblyNames = baseAotAssemblyNames,
                aotSnapshotSha256 = baseAotSnapshotSha256,
                aotAnalysisSnapshotSha256 = aotAnalysis?.Sha256,
                baseMetaVersionSetSha256 = baseMvSetHash,
                aotMetadataSetId,
                nativeGuardSourceSha256 = baseNativeGuardSourceSha256,
                nativeManifestSha256,
                runtimeProtocol = baseRuntimeProtocol,
                nativeRuntimeContract = baseNativeRuntimeContract,
                runtimeCapabilities = baseNativeRuntimeCapabilities.OrderBy(value => value,
                    StringComparer.Ordinal).ToArray(),
                requiredRuntimeCapabilities = requiredRuntimeCapabilities.OrderBy(value => value,
                    StringComparer.Ordinal).ToArray(),
                runtimeAssetRoot,
                baseMetaVersionAssetRoot,
                buildIdentitySha256 = Sha256File(buildIdentityPath),
                compatibilityPolicy = ResourceUpdateCompatibility.Policy,
                compatible = baseCompatible,
                guardCoverageValidated = uncovered.Count == 0,
                unsupportedChangeCount = unsupported.Count,
                unsupportedChanges = unsupported.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                assemblies = assemblyCompatibility.ToArray(),
                assemblyModes = assemblyModes.ToArray(),
                frozenAotSources = frozenAotSources.ToArray(),
                frozenAotAdmission = frozenAdmission,
                currentStorageImpact = execution.Impact,
            };
            candidateBases.Add(baseRecord);
            resourceBaseSelections.Add(new ResourceAotMetadataBaseSelection(baseId,
                aotMetadataSetId, payloadVariantId, currentVariant.CurrentSetHash,
                assemblyModes.ToArray(), frozenAotSources.ToArray()));
            if (!baseCompatible)
            {
                releaseErrors.Add("Base " + baseId + " is incompatible: " +
                    string.Join("; ", uncovered.Concat(unsupported).Take(32)) +
                    (uncovered.Count + unsupported.Count > 32 ? " ..." : ""));
            }
        }

        WriteJson(validationPath, new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-resource-update-validation.json",
            generatedAtUtc = DateTimeOffset.UtcNow,
            passed = releaseErrors.Count == 0,
            mode = resourceRelease.Mode,
            releaseReady = resourceRelease.ReleaseReady,
            releaseChannelId = resourceRelease.ChannelId,
            releaseRevision = resourceRelease.Revision,
            parentReleaseLedgerSha256 = resourceRelease.ParentLedgerSha256,
            releaseLedger = resourceRelease.ReleaseReady ? ReleaseLedgerFileName : null,
            compatibilityPolicy = ResourceUpdateCompatibility.Policy,
            runtimeProtocol = ResourceUpdateCompatibility.RuntimeProtocol,
            currentAssemblySetSha256 = currentSetHash,
            payloadVariantSetSha256 = payloadVariantSetHash,
            payloadVariants = currentVariants.Values.OrderBy(value => value.VariantId,
                StringComparer.OrdinalIgnoreCase).Select(value => new
            {
                variantId = value.VariantId,
                currentAssemblySetSha256 = value.CurrentSetHash,
            }).ToArray(),
            baseRegistrySha256 = baseRegistry?.Sha256,
            baseRegistryEntryCount = baseRegistry?.Entries.Length,
            baseRegistryAuditPath,
            baseRegistryAuditSha256,
            baseRegistryId = baseRegistry?.RegistryId,
            baseRegistryRevision = baseRegistry?.Revision,
            baseRegistryParentSha256 = baseRegistry?.ParentRegistrySha256,
            baseRegistryParentAuditPath,
            baseRegistryParentAuditSha256,
            baseRegistryRetiredBaseCount = baseRegistry?.RetiredBases.Length,
            baseRegistryLineageValidated = baseRegistry != null,
            candidateBaseCount = candidateBases.Count,
            compatibleBaseCount = candidateBases.Count - releaseErrors.Count,
            bases = candidateBases.ToArray(),
            errors = releaseErrors.ToArray(),
        });
        if (releaseErrors.Count > 0)
            throw new DheException("DHE resource update is not compatible with every supported Base Player. " +
                "See " + validationPath + ". " + releaseErrors[0]);

        WriteJson(runtimePlanPath, new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-runtime-asset-plan.json",
            selection = "embedded-base-metaversion-and-aot-metadata-set",
            mode = resourceRelease.Mode,
            releaseReady = resourceRelease.ReleaseReady,
            releaseChannelId = resourceRelease.ChannelId,
            releaseRevision = resourceRelease.Revision,
            parentReleaseLedgerSha256 = resourceRelease.ParentLedgerSha256,
            currentAssemblySetSha256 = currentSetHash,
            payloadVariantSetSha256 = payloadVariantSetHash,
            runtimeAssetRoot,
            baseMetaVersionAssetRoot,
            aotMetadataSetId = string.Empty,
            aotMetadata = Array.Empty<ResourceAotMetadataPayload>(),
            aotMetadataSets = runtimeAotMetadataSets,
            baseSelections = resourceBaseSelections.ToArray(),
            assemblies = runtimeAssemblies.ToArray(),
            payloadVariants = currentVariants.Values.OrderBy(value => value.VariantId,
                    StringComparer.OrdinalIgnoreCase).Select(value => new
            {
                variantId = value.VariantId,
                currentAssemblySetSha256 = value.CurrentSetHash,
                assemblies = value.RuntimeAssemblies.ToArray(),
            }).ToArray(),
        });
        WriteJson(manifestPath, new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-resource-update.json",
            generatedAtUtc = DateTimeOffset.UtcNow,
            mode = resourceRelease.Mode,
            releaseReady = resourceRelease.ReleaseReady,
            releaseChannelId = resourceRelease.ChannelId,
            releaseRevision = resourceRelease.Revision,
            parentReleaseLedgerSha256 = resourceRelease.ParentLedgerSha256,
            releaseLedger = resourceRelease.ReleaseReady ? ReleaseLedgerFileName : null,
            payloadModel,
            payloadVariantSetSha256 = payloadVariantSetHash,
            metaVersionSchema = 1,
            compatibilityPolicy = ResourceUpdateCompatibility.Policy,
            runtimeProtocol = ResourceUpdateCompatibility.RuntimeProtocol,
            compatibilityValidated = true,
            validation = "dhe-resource-update-validation.json",
            validationSha256 = Sha256File(validationPath),
            currentAssemblySetSha256 = currentSetHash,
            payloadVariants = currentVariants.Values.OrderBy(value => value.VariantId,
                    StringComparer.OrdinalIgnoreCase).Select(value => new
            {
                variantId = value.VariantId,
                currentAssemblySetSha256 = value.CurrentSetHash,
                assemblies = value.PayloadFiles.ToArray(),
            }).ToArray(),
            baseRegistrySha256 = baseRegistry?.Sha256,
            baseRegistryEntryCount = baseRegistry?.Entries.Length,
            baseRegistryAuditPath,
            baseRegistryAuditSha256,
            baseRegistryId = baseRegistry?.RegistryId,
            baseRegistryRevision = baseRegistry?.Revision,
            baseRegistryParentSha256 = baseRegistry?.ParentRegistrySha256,
            baseRegistryParentAuditPath,
            baseRegistryParentAuditSha256,
            baseRegistryRetiredBaseCount = baseRegistry?.RetiredBases.Length,
            baseRegistryLineageValidated = baseRegistry != null,
            playerUpdateRequired = false,
            guardCoverageValidated = true,
            runtimeComparison = "embedded-base-mv-vs-current-mv",
            runtimePlan = "dhe-runtime-plan.json",
            runtimePlanSha256 = Sha256File(runtimePlanPath),
            runtimeAssetRoot,
            baseMetaVersionAssetRoot,
            aotMetadataSets = runtimeAotMetadataSets,
            assemblies = payloadFiles.ToArray(),
            supportedBases = candidateBases.ToArray(),
        });
        if (resourceRelease.ReleaseReady)
        {
            try
            {
                _ = WriteReleaseLedger(outputRoot, resourceRelease, baseRegistry!,
                    currentSetHash, payloadVariantSetHash, manifestPath, validationPath);
            }
            catch
            {
                File.Delete(manifestPath);
                File.Delete(releaseLedgerPath);
                throw;
            }
        }
        Console.WriteLine("DHE " + payloadModel + " resource update: " + manifestPath);
        return 0;
    }

    private static string[] ValidateResourceBaseIdentity(JsonElement identity, string identityPath,
        string nativeManifestPath, string managedAssemblySetSha256,
        string baseMetaVersionSetSha256, string aotMetadataSetId, string runtimeAssetRoot,
        string baseMetaVersionAssetRoot, string expectedEngineWorkflow)
    {
        var errors = new List<string>();
        JsonElement nativeManifest = ReadJson<JsonElement>(nativeManifestPath);
        if (GetInt(identity, "schemaVersion") != 1 || !string.Equals(GetString(identity, "format"),
                "hybridclr.dhe-build-identity.json", StringComparison.Ordinal))
            errors.Add("base-build-identity-format:" + identityPath);
        if (GetInt(identity, "identityVersion") != 1)
            errors.Add("base-build-identity-version:" + identityPath);
        if (!string.Equals(GetString(identity, "state"), "staged-for-final-player",
                StringComparison.Ordinal))
            errors.Add("base-build-identity-state:" + identityPath);
        var target = GetString(identity, "target") ?? string.Empty;
        if (target.Length == 0 || target.Any(character => !(char.IsLetterOrDigit(character) ||
                character is '.' or '_' or '-')))
            errors.Add("base-build-identity-target:" + identityPath);
        string engineWorkflow = GetString(identity, "engineWorkflow") ?? string.Empty;
        string il2cppCodeGeneration = GetString(identity, "il2cppCodeGeneration") ?? string.Empty;
        try
        {
            if (!string.Equals(engineWorkflow, expectedEngineWorkflow, StringComparison.Ordinal) ||
                !string.Equals(il2cppCodeGeneration,
                    ExpectedIl2CppCodeGeneration(engineWorkflow), StringComparison.Ordinal))
                errors.Add("base-build-identity-build-configuration:" + identityPath);
        }
        catch (DheException)
        {
            errors.Add("base-build-identity-build-configuration:" + identityPath);
        }
        if (!string.Equals(GetString(identity, "managedAssemblySetSha256"),
                managedAssemblySetSha256, StringComparison.OrdinalIgnoreCase))
            errors.Add("base-build-identity-assembly-set-mismatch:" + identityPath);
        string[] aotAssemblyNames;
        try
        {
            aotAssemblyNames = ReadIdentityAotAssemblyNames(identity, identityPath);
        }
        catch (Exception exception)
        {
            errors.Add("base-build-identity-aot-inventory:" + exception.Message);
            aotAssemblyNames = Array.Empty<string>();
        }
        if (!IsHex(GetString(identity, "aotSnapshotSha256"), 64, 64))
            errors.Add("base-build-identity-aot-snapshot:" + identityPath);
        if (!IsHex(GetString(identity, "nativeGuardSourceSha256"), 64, 64))
            errors.Add("base-build-identity-native-guard:" + identityPath);
        if (!string.Equals(GetString(identity, "baseMetaVersionSetSha256"),
                baseMetaVersionSetSha256, StringComparison.OrdinalIgnoreCase))
            errors.Add("base-build-identity-metaversion-set-mismatch:" + identityPath);
        if (!string.Equals(GetString(identity, "aotMetadataSetId"), aotMetadataSetId,
                StringComparison.OrdinalIgnoreCase))
            errors.Add("base-build-identity-aot-metadata-set-mismatch:" + identityPath);
        if (!string.Equals(GetString(identity, "nativeManifestSha256"), Sha256File(nativeManifestPath),
                StringComparison.OrdinalIgnoreCase))
            errors.Add("base-build-identity-native-manifest-mismatch:" + identityPath);
        string runtimeProtocol = GetString(identity, "runtimeProtocol") ?? string.Empty;
        string runtimeContract = GetString(identity, "runtimeContract") ?? string.Empty;
        string[] runtimeCapabilities = ReadStringArray(identity, "runtimeCapabilities");
        if (!string.Equals(runtimeProtocol, ResourceUpdateCompatibility.RuntimeProtocol,
                StringComparison.Ordinal) ||
            !string.Equals(runtimeProtocol, GetString(nativeManifest, "runtimeProtocol"),
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(runtimeContract) ||
            !string.Equals(runtimeContract, GetString(nativeManifest, "runtimeContract"),
                StringComparison.Ordinal) ||
            runtimeCapabilities.Length == 0 ||
            runtimeCapabilities.Any(string.IsNullOrWhiteSpace) ||
            runtimeCapabilities.Distinct(StringComparer.Ordinal).Count() !=
                runtimeCapabilities.Length ||
            !new HashSet<string>(runtimeCapabilities, StringComparer.Ordinal).SetEquals(
                ReadStringArray(nativeManifest, "runtimeCapabilities")))
            errors.Add("base-build-identity-runtime-contract:" + identityPath);
        if (!string.Equals(NormalizeRuntimeAssetRoot(GetString(identity, "runtimeAssetRoot") ?? ""),
                runtimeAssetRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(NormalizeRuntimeAssetRoot(
                    GetString(identity, "baseMetaVersionAssetRoot") ?? ""),
                baseMetaVersionAssetRoot, StringComparison.OrdinalIgnoreCase))
            errors.Add("base-build-identity-asset-roots:" + identityPath);
        string computedBaseId = ComputeBaseId(target, engineWorkflow,
            il2cppCodeGeneration, managedAssemblySetSha256,
            GetString(identity, "aotAssemblySetSha256") ?? string.Empty,
            GetString(identity, "aotSnapshotSha256") ?? string.Empty,
            baseMetaVersionSetSha256, aotMetadataSetId,
            GetString(identity, "nativeGuardSourceSha256") ?? string.Empty,
            GetString(identity, "nativeManifestSha256") ?? string.Empty,
            runtimeProtocol, runtimeContract, runtimeCapabilities, runtimeAssetRoot,
            baseMetaVersionAssetRoot, GetString(identity, "aotAnalysisSnapshotSha256"));
        if (!string.Equals(GetString(identity, "baseId"), computedBaseId,
                StringComparison.OrdinalIgnoreCase))
            errors.Add("base-build-identity-composite-id:" + identityPath);
        return errors.ToArray();
    }

    private static HashSet<string> ReadNativeStableMethodIds(JsonElement manifest, string property)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!manifest.TryGetProperty(property, out var methods) || methods.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var method in methods.EnumerateArray())
        {
            var assemblyName = NormalizeName(GetString(method, "assemblyName") ?? string.Empty);
            var managedId = GetString(method, "managedId") ?? string.Empty;
            var stableId = GetString(method, "stableMethodIdSha256") ?? string.Empty;
            if (assemblyName.Length == 0 || managedId.Length == 0 || !IsHex(stableId, 64, 64) ||
                !string.Equals(Sha256Text("dhe-method-id\n" + managedId), stableId,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("Native manifest contains an invalid stable method identity in " +
                    property + ".");
            result.Add(NativeStableMethodKey(assemblyName, stableId));
        }
        return result;
    }

    private static string[] ReadIdentityAssemblyNames(JsonElement identity, string identityPath)
    {
        if (!identity.TryGetProperty("assemblies", out JsonElement assemblies) ||
            assemblies.ValueKind != JsonValueKind.Array || assemblies.GetArrayLength() == 0)
            throw new DheException("Base build identity has no assembly records: " + identityPath);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement assembly in assemblies.EnumerateArray())
        {
            string name = NormalizeName(GetString(assembly, "assemblyName") ?? string.Empty);
            if (!names.Add(name))
                throw new DheException("Base build identity contains a duplicate assembly: " + name);
        }
        return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] ReadIdentityAotAssemblyNames(JsonElement identity, string identityPath)
        => ReadAotAssemblyNames(identity, "Base build identity " + identityPath);

    private static string[] ReadAotAssemblyNames(JsonElement value, string description)
    {
        if (!value.TryGetProperty("aotAssemblyNames", out JsonElement assemblies) ||
            assemblies.ValueKind != JsonValueKind.Array || assemblies.GetArrayLength() == 0)
            throw new DheException(description + " has no complete AOT inventory.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement assembly in assemblies.EnumerateArray())
        {
            if (assembly.ValueKind != JsonValueKind.String)
                throw new DheException(description + " AOT inventory contains a non-string entry.");
            string rawName = assembly.GetString() ?? string.Empty;
            string name = NormalizeName(rawName);
            if (!string.Equals(rawName, name, StringComparison.Ordinal) || !names.Add(name))
                throw new DheException(
                    description + " AOT inventory contains a non-canonical or duplicate assembly: " +
                    rawName);
        }
        string[] result = names.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        string expectedHash = GetString(value, "aotAssemblySetSha256") ?? string.Empty;
        if (!IsHex(expectedHash, 64, 64) ||
            !string.Equals(AssemblyNameSetHash(result), expectedHash,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException(description + " AOT inventory hash is invalid.");
        return result;
    }

    private static string NativeStableMethodKey(string assemblyName, string stableId) =>
        NormalizeName(assemblyName) + ":" + stableId.ToLowerInvariant();

    private static bool TryClassifyAssemblyExecutionMode(string assemblyName,
        ISet<string> baseDheAssemblies, ISet<string> baseAotAssemblies,
        out string executionMode, out string rejection)
    {
        string name = NormalizeName(assemblyName);
        if (baseDheAssemblies.Contains(name))
        {
            executionMode = "dhe-differential";
            rejection = string.Empty;
            return true;
        }
        if (baseAotAssemblies.Contains(name))
        {
            executionMode = "rejected";
            rejection = "assembly-present-in-base-aot-outside-dhe:" + name;
            return false;
        }
        executionMode = "interpreter-only";
        rejection = string.Empty;
        return true;
    }

    private static string[] ReadStringArray(JsonElement value, string property)
    {
        return value.TryGetProperty(property, out JsonElement items) &&
               items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty).ToArray()
            : Array.Empty<string>();
    }

    private static string RequireCommonBaseAssetRoot(IEnumerable<JsonElement> identities,
        string property)
    {
        string[] roots = identities.Select(identity => NormalizeRuntimeAssetRoot(
                GetString(identity, property) ?? string.Empty))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Length != 1)
            throw new DheException("Supported Base identities do not share " + property + ".");
        return roots[0];
    }

    private static void ValidateRequestedAssetRoot(string? requested, string identityValue,
        string description)
    {
        if (!string.IsNullOrWhiteSpace(requested) &&
            !string.Equals(NormalizeRuntimeAssetRoot(requested), identityValue,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException(description +
                " does not match the immutable supported Base identities.");
    }

    private static string ComputeBaseId(string target, string engineWorkflow,
        string il2cppCodeGeneration, string managedAssemblySetSha256,
        string aotAssemblySetSha256, string aotSnapshotSha256,
        string baseMetaVersionSetSha256,
        string aotMetadataSetId,
        string nativeGuardSourceSha256, string nativeManifestSha256,
        string runtimeProtocol, string runtimeContract, IEnumerable<string> runtimeCapabilities,
        string runtimeAssetRoot, string baseMetaVersionAssetRoot, string? aotAnalysisSnapshotSha256 = null)
    {
        string[] capabilities = runtimeCapabilities.Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        string canonical = "hybridclr.dhe-base-identity-v1\n" +
            "target=" + target + "\n" +
            "engineWorkflow=" + engineWorkflow + "\n" +
            "il2cppCodeGeneration=" + il2cppCodeGeneration + "\n" +
            "managedAssemblySetSha256=" + managedAssemblySetSha256.ToLowerInvariant() + "\n" +
            "aotAssemblySetSha256=" + aotAssemblySetSha256.ToLowerInvariant() + "\n" +
            "aotSnapshotSha256=" + aotSnapshotSha256.ToLowerInvariant() + "\n" +
            "baseMetaVersionSetSha256=" + baseMetaVersionSetSha256.ToLowerInvariant() + "\n" +
            "aotMetadataSetId=" + aotMetadataSetId.ToLowerInvariant() + "\n" +
            "nativeGuardSourceSha256=" + nativeGuardSourceSha256.ToLowerInvariant() + "\n" +
            "nativeManifestSha256=" + nativeManifestSha256.ToLowerInvariant() + "\n" +
            "runtimeProtocol=" + runtimeProtocol + "\n" +
            "runtimeContract=" + runtimeContract + "\n" +
            "runtimeCapabilities=" + string.Join(",", capabilities) + "\n" +
            "runtimeAssetRoot=" + NormalizeRuntimeAssetRoot(runtimeAssetRoot) + "\n" +
            "baseMetaVersionAssetRoot=" + NormalizeRuntimeAssetRoot(baseMetaVersionAssetRoot) + "\n";
        if (!string.IsNullOrWhiteSpace(aotAnalysisSnapshotSha256))
            canonical += "aotAnalysisSnapshotSha256=" + aotAnalysisSnapshotSha256.ToLowerInvariant() + "\n";
        return Sha256Text(canonical);
    }

    private static string ExpectedIl2CppCodeGeneration(string engineWorkflow)
    {
        return engineWorkflow switch
        {
            "Unity2021Standard" => "OptimizeSpeed",
            "Unity2022Fgs" => "OptimizeSize",
            "Tuanjie2022Fgs" => "OptimizeSize",
            _ => throw new DheException("Unsupported DHE engine workflow: " + engineWorkflow),
        };
    }

    private static string NamedAssemblySetHash(IEnumerable<(string name, string path)> records)
    {
        using var sha = SHA256.Create();
        foreach (var record in records.OrderBy(item => item.name, StringComparer.Ordinal))
        {
            var name = Encoding.UTF8.GetBytes(record.name + "\n");
            sha.TransformBlock(name, 0, name.Length, name, 0);
            var bytes = File.ReadAllBytes(record.path);
            sha.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
            var separator = new byte[] { (byte)'\n' };
            sha.TransformBlock(separator, 0, separator.Length, separator, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static string AssemblyNameSetHash(IEnumerable<string> assemblyNames) =>
        Sha256Text(string.Concat(assemblyNames.Select(NormalizeName)
            .OrderBy(name => name, StringComparer.Ordinal).Select(name => name + "\n")));

    private static string NamedByteSetHash(IEnumerable<(string name, byte[] bytes)> records)
    {
        using var sha = SHA256.Create();
        foreach (var record in records.OrderBy(item => item.name, StringComparer.Ordinal))
        {
            var name = Encoding.UTF8.GetBytes(record.name + "\n");
            sha.TransformBlock(name, 0, name.Length, name, 0);
            sha.TransformBlock(record.bytes, 0, record.bytes.Length, record.bytes, 0);
            var separator = new byte[] { (byte)'\n' };
            sha.TransformBlock(separator, 0, separator.Length, separator, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static string NamedVariantSetHash(IEnumerable<(string variantId, string currentSetHash)> records)
    {
        using var sha = SHA256.Create();
        foreach (var record in records.OrderBy(item => item.variantId,
                     StringComparer.OrdinalIgnoreCase))
        {
            var bytes = Encoding.UTF8.GetBytes(record.variantId + "\n" +
                record.currentSetHash.ToLowerInvariant() + "\n");
            sha.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static string Sha256Bytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string NormalizeRuntimeAssetRoot(string value)
    {
        var normalized = (value ?? string.Empty).Replace('\\', '/').Trim();
        if (normalized.Length == 0 || normalized.StartsWith("/", StringComparison.Ordinal) || Path.IsPathRooted(normalized) || normalized.Contains("..", StringComparison.Ordinal))
            throw new DheException("RuntimeAssetRoot must be a portable project-relative path.");
        return normalized.EndsWith("/", StringComparison.Ordinal) ? normalized : normalized + "/";
    }

    private static int Preflight(Cli cli)
    {
        var settings = RequireFile(cli.Require("settingsfile"), "HybridCLR settings");
        var baseline = RequireDirectory(cli.Require("baselineroot"), "Baseline root");
        var current = RequireDirectory(cli.Require("currentroot"), "Current root");
        var output = SafeOutputRoot(cli.Require("outputroot"), new[] { settings, baseline, current });
        Directory.CreateDirectory(output);
        var batchArgs = new Cli("batch", new Dictionary<string, string>(cli.Values, StringComparer.OrdinalIgnoreCase));
        batchArgs.Values["outputroot"] = Path.Combine(output, "batch");
        var batchCode = Batch(batchArgs);
        var batchPath = Path.Combine(output, "batch", "dhe-batch-summary.json");
        var batch = ReadJson<JsonElement>(batchPath);
        var assemblies = new List<object>();
        foreach (var record in batch.GetProperty("assemblies").EnumerateArray())
        {
            var status = record.GetProperty("status").GetString() ?? "error";
            assemblies.Add(new { assemblyName = record.GetProperty("assemblyName").GetString(),
                batchStatus = status, validationPassed = status == "compatible",
                validationReport = (string?)null,
                error = record.TryGetProperty("error", out var error) ? error.GetString() : null });
        }
        var names = batch.GetProperty("assemblies").EnumerateArray().Select(x => x.GetProperty("assemblyName").GetString()!).ToArray();
        var planRecords = batch.GetProperty("assemblies").EnumerateArray().Select(record =>
        {
            var status = record.GetProperty("status").GetString() ?? "error";
            var assemblyName = record.GetProperty("assemblyName").GetString()!;
            var baselinePath = record.GetProperty("baseline").GetString()!;
            var currentPath = record.GetProperty("current").GetString()!;
            return new
            {
                assemblyName,
                status,
                baseline = baselinePath,
                current = currentPath,
                baseMetaVersionJson = record.GetProperty("baseMetaVersionJson").GetString(),
                baseMetaVersionBytes = record.GetProperty("baseMetaVersionBytes").GetString(),
                currentMetaVersionJson = record.GetProperty("currentMetaVersionJson").GetString(),
                currentMetaVersionBytes = record.GetProperty("currentMetaVersionBytes").GetString(),
                changedMethodCount = record.GetProperty("changedMethodCount").GetInt32(),
                changedExistingTypeCount = record.GetProperty("changedExistingTypeCount").GetInt32(),
                addedTypeCount = record.GetProperty("addedTypeCount").GetInt32(),
                removedTypeCount = record.GetProperty("removedTypeCount").GetInt32(),
                compatibility = status,
            };
        }).ToArray();
        var planPath = Path.Combine(output, "dhe-project-plan.json");
        WriteJson(planPath, new { schemaVersion = 1, format = "hybridclr.dhe-project-plan.json", generatedAtUtc = DateTimeOffset.UtcNow, complete = batch.GetProperty("counts").GetProperty("error").GetInt32() == 0 && batch.GetProperty("counts").GetProperty("missing").GetInt32() == 0 && batch.GetProperty("counts").GetProperty("incompatible").GetInt32() == 0, requireDheEqualsHotUpdate = cli.Has("requiredheequalshotupdate"), hotUpdateAssemblies = Settings.Read(settings).Hot, dheAotAssemblies = names, dheEqualsHotUpdate = SetEquals(Settings.Read(settings).Hot, names), settingsFile = settings, baselineRoot = baseline, currentRoot = current, batchReport = batchPath, assemblies = planRecords });
        var planValidationPath = Path.Combine(output, "project-plan-validation.json");
        var planComplete = batch.GetProperty("counts").GetProperty("error").GetInt32() == 0 && batch.GetProperty("counts").GetProperty("missing").GetInt32() == 0 && batch.GetProperty("counts").GetProperty("incompatible").GetInt32() == 0;
        WriteJson(planValidationPath, new { schemaVersion = 1, format = "hybridclr.dhe-project-plan-validation.json", generatedAtUtc = DateTimeOffset.UtcNow, pathSemantics = "workspace-absolute-v1", passed = planComplete, complete = planComplete, coverageRequired = cli.Has("requirecompletecoverage"), coverageComplete = planComplete && (!cli.Has("requirecompletecoverage") || batchCode == 0), plan = planPath, assemblies = planRecords, errors = planComplete ? Array.Empty<string>() : new[] { "DHE project plan contains missing, incompatible, or error assemblies." }, warnings = Array.Empty<string>() });
        var reportPath = Path.Combine(output, "project-preflight-report.json");
        var passed = batchCode == 0;
        var configuredDnlibPath = cli.Optional("dnlibpath");
        var dnlibPath = string.IsNullOrWhiteSpace(configuredDnlibPath)
            ? Path.Combine(AppContext.BaseDirectory, "dnlib.dll")
            : Path.GetFullPath(configuredDnlibPath);
        WriteJson(reportPath, new { schemaVersion = 1, format = "hybridclr.dhe-project-preflight.json", generatedAtUtc = DateTimeOffset.UtcNow, passed, generationPassed = passed, validationPassed = passed && planComplete, coverageRequired = cli.Has("requirecompletecoverage"), dheCoverageRequired = cli.Has("requiredheequalshotupdate"), configurationPassed = batch.GetProperty("configurationPassed").GetBoolean(), configurationErrors = batch.GetProperty("configurationErrors"), hotUpdateAssemblies = Settings.Read(settings).Hot, dheAotAssemblies = names, dheEqualsHotUpdate = SetEquals(Settings.Read(settings).Hot, names), coverageComplete = passed && planComplete, artifactReady = passed && planComplete && cli.Has("requirecompletecoverage"), releaseReady = false, sourcePreflight = (string?)null, sourcePreflightPassed = true, settingsFile = settings, projectRoot = cli.Optional("projectroot"), baselineRoot = baseline, currentRoot = current, batchReport = batchPath, projectPlan = planPath, projectPlanValidation = planValidationPath, batchExitCode = batchCode, counts = batch.GetProperty("counts"), assemblies, nativeAbiCoverage = "not-evaluated-by-offline-preflight", dnlibPath });
        Console.WriteLine("DHE project preflight: " + reportPath);
        return passed ? 0 : 1;
    }

    private static int Workflow(Cli cli) => ProductionWorkflow(cli);

    private static int ProductionWorkflow(Cli cli)
    {
        ApplyWorkflowConfig(cli);
        var outputValue = cli.Optional("outputroot");
        try { return ProductionWorkflowCore(cli); }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(outputValue))
            {
                try
                {
                    var output = Path.GetFullPath(outputValue);
                    var project = Path.GetFullPath(cli.Optional("projectpath") ?? ".");
                    if (!output.Equals(project, StringComparison.OrdinalIgnoreCase) && Directory.Exists(output))
                    {
                        var stage = (string name, string? path) =>
                        {
                            bool passed = path != null && File.Exists(path);
                            if (passed)
                            {
                                try
                                {
                                    var report = ReadJson<JsonElement>(path!);
                                    if (report.ValueKind == JsonValueKind.Object &&
                                        report.TryGetProperty("passed", out var value) &&
                                        (value.ValueKind == JsonValueKind.True ||
                                         value.ValueKind == JsonValueKind.False))
                                    {
                                        passed = value.GetBoolean();
                                    }
                                }
                                catch
                                {
                                    passed = false;
                                }
                            }
                            return new { passed, report = path };
                        };
                        WriteJson(Path.Combine(output, "project-workflow-failure.json"), new
                        {
                            schemaVersion = 1, format = "hybridclr.dhe-project-workflow-failure.json", generatedAtUtc = DateTimeOffset.UtcNow,
                            passed = false, toolchainContractVersion = 1, adapterScript = cli.Optional("adaptermethod") ?? "unknown",
                            projectPath = project, settingsFile = cli.Optional("settingsfile") ?? "unknown",
                            expectedToolchainPackageId = cli.Optional("expectedtoolchainpackageid"), error = ex.Message,
                            errors = new[] { ex.Message }, stages = new Dictionary<string, object>
                            {
                                ["toolchain"] = stage("toolchain", Path.Combine(output, "toolchain-gate.json")),
                                ["prepare"] = stage("prepare", Path.Combine(output, "adapter", "prepare.json")),
                                ["cleanCheckout"] = stage("cleanCheckout", Path.Combine(output, "clean-checkout", "clean-checkout-gate-report.json")),
                                ["sourcePreflight"] = stage("sourcePreflight", Path.Combine(output, "source-preflight", "source-preflight-report.json")),
                                ["projectPreflight"] = stage("projectPreflight", Path.Combine(output, "project-preflight", "project-preflight-report.json")),
                                ["player"] = stage("player", Path.Combine(output, "dhe-player-result.json")),
                                ["archive"] = stage("archive", Path.Combine(output, "archive-gate.json")),
                                ["release"] = stage("release", Path.Combine(output, "release-gate.json"))
                            }
                        });
                    }
                }
                catch { }
            }
            throw;
        }
    }

    private static int ProductionWorkflowCore(Cli cli)
    {
        var project = RequireDirectory(cli.Require("projectpath"), "DHE project");
        var output = SafeOutputRoot(cli.Require("outputroot"), new[] { project });
        var target = cli.Require("target");
        var settings = RequireFile(cli.Require("settingsfile"), "HybridCLR settings");
        var bootstrap = cli.Has("bootstrap");
        var baseline = bootstrap
            ? Path.GetFullPath(Path.Combine(output, "bootstrap-baseline-source"))
            : RequireDirectory(cli.Require("baselineaotroot"), "Baseline AOT root");
        var unity = ResolveUnity(cli, project);
        var adapterClass = cli.Require("adaptermethod");
        var mode = cli.Optional("mode") ?? "Release";
        if (mode is not ("Release" or "Exploratory")) throw new DheException("Mode must be Release or Exploratory.");
        if (mode == "Release" && !cli.Has("runplayer")) throw new DheException("Release workflow requires -RunPlayer.");
        if (mode == "Release" && cli.Has("stopafterpreflight")) throw new DheException("Release workflow cannot stop after preflight.");
        var timeout = int.TryParse(cli.Optional("unitytimeoutseconds"), out var seconds) ? Math.Clamp(seconds, 10, 3600) : 600;
        var current = Path.Combine(output, "current");
        var baselineCopy = Path.Combine(output, "baseline");
        var preflightRoot = Path.Combine(output, "project-preflight");
        Directory.CreateDirectory(output);
        var productionEvidence = PrepareProductionEvidence(cli, mode, project, settings, baseline, target, output);
        var buildConfiguration = ResolveWorkflowBuildConfiguration(cli,
            productionEvidence.RuntimeManifest);

        void RunBoundUnity(IEnumerable<string> arguments, IDictionary<string, string> environment,
            string logPath)
        {
            RequireWorkflowRuntimeBinding(productionEvidence.RuntimeManifest, project);
            RunUnity(unity, project, arguments, environment, logPath, timeout);
            RequireWorkflowRuntimeBinding(productionEvidence.RuntimeManifest, project);
        }

        var prepareArguments = new List<string> { "-batchmode", "-nographics", "-quit", "-projectPath", project,
            "-executeMethod", adapterClass, "-dheTarget", target, "-dheOutputRoot", output,
            "-dheBaselineRoot", baselineCopy, "-dheCurrentRoot", current, "-dheMode", mode,
            "-dheEngineWorkflow", buildConfiguration.EngineWorkflow,
            "-dheIl2CppCodeGeneration", buildConfiguration.Il2CppCodeGeneration,
            "-extraScriptingDefines=HYBRIDCLR_DHE_CURRENT_GENERATION",
            "-logFile", Path.Combine(output, "unity-prepare.log") };
        if (bootstrap) prepareArguments.AddRange(new[] { "-dheBootstrap", "true" });
        if (cli.Optional("dhecurrentinputroot") is { Length: > 0 } currentInputRoot)
            prepareArguments.AddRange(new[] { "-dheCurrentInputRoot",
                RequireDirectory(currentInputRoot, "DHE current assembly input root") });
        AppendUnityArguments(prepareArguments, cli);
        RunBoundUnity(prepareArguments,
            bootstrap ? new Dictionary<string, string>() : new Dictionary<string, string> { ["DHE_BASELINE_ROOT"] = baseline },
            Path.Combine(output, "unity-prepare-process.log"));
        var preparePath = Path.Combine(output, "adapter", "prepare.json");
        RequireFile(preparePath, "DHE adapter prepare report");

        var preflight = new Cli("preflight", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["settingsfile"] = settings, ["baselineroot"] = baselineCopy, ["currentroot"] = current,
            ["outputroot"] = preflightRoot, ["projectroot"] = project,
            ["requiredheequalshotupdate"] = "true", ["requirecompletecoverage"] = "true",
            ["dnlibpath"] = string.IsNullOrWhiteSpace(cli.Optional("dnlibpath"))
                ? Path.Combine(AppContext.BaseDirectory, "dnlib.dll")
                : Path.GetFullPath(cli.Optional("dnlibpath")!)
        });
        if (Preflight(preflight) != 0) throw new DheException("DHE project preflight failed.");
        var planPath = Path.Combine(preflightRoot, "dhe-project-plan.json");
        if (cli.Has("stopafterpreflight"))
        {
            var reportPath = Path.Combine(output, "project-workflow-report.json");
            WriteJson(reportPath, ProjectWorkflowReport(output, mode, target, project, settings, adapterClass,
                productionEvidence, preparePath, null, null, null, null, null, null, false, null, null));
            RunWorkflowSchemaGate(cli, output);
            Console.WriteLine("DHE workflow preflight passed: " + reportPath);
            return 0;
        }
        var adapterType = adapterClass.EndsWith(".Prepare", StringComparison.Ordinal) ? adapterClass[..^".Prepare".Length] : adapterClass;
        var common = new List<string> { "-batchmode", "-nographics", "-quit", "-projectPath", project,
            "-dheTarget", target, "-dheOutputRoot", output, "-dheBaselineRoot", baselineCopy,
            "-dheCurrentRoot", current, "-dheMode", mode, "-dheProjectPlan", planPath,
            "-dheEngineWorkflow", buildConfiguration.EngineWorkflow,
            "-dheIl2CppCodeGeneration", buildConfiguration.Il2CppCodeGeneration };
        if (buildConfiguration.AotMetadataAssemblies != null)
            common.AddRange(new[] { "-dheAotMetadataAssemblies",
                buildConfiguration.AotMetadataAssemblies });
        if (cli.Has("bootstrap")) common.AddRange(new[] { "-dheBootstrap", "true" });
        AppendUnityArguments(common, cli);
        RunBoundUnity(common.Append("-executeMethod").Append(adapterType + ".StageRuntimePlan").Append("-logFile").Append(Path.Combine(output, "unity-stage.log")), new Dictionary<string, string> { ["DHE_BASELINE_ROOT"] = baseline }, Path.Combine(output, "unity-stage-process.log"));
        var runtimePlanPath = Path.Combine(output, "runtime-plan", "dhe-runtime-plan.json");
        var baseAotMetadataArchive = MaterializeBaseAotMetadataRoot(output, runtimePlanPath);
        RunBoundUnity(common.Append("-executeMethod").Append(adapterType + ".BuildDheYooAsset").Append("-logFile").Append(Path.Combine(output, "unity-yooasset.log")), new Dictionary<string, string> { ["DHE_BASELINE_ROOT"] = baseline }, Path.Combine(output, "unity-yooasset-process.log"));
        var resourcePath = Path.Combine(output, "adapter", "resource-evidence.json");
        ValidateResourceEvidence(resourcePath, target);
        RunBoundUnity(common.Append("-executeMethod").Append(adapterType + ".BuildScriptsOnly").Append("-logFile").Append(Path.Combine(output, "unity-scripts.log")), new Dictionary<string, string> { ["DHE_BASELINE_ROOT"] = baseline }, Path.Combine(output, "unity-scripts-process.log"));
        if (bootstrap)
        {
            RunBoundUnity(common.Append("-executeMethod").Append(adapterType + ".BuildFinalPlayer").Append("-logFile").Append(Path.Combine(output, "unity-player.log")), new Dictionary<string, string> { ["DHE_BASELINE_ROOT"] = baseline }, Path.Combine(output, "unity-player-process.log"));
            Console.WriteLine("DHE bootstrap Player built with universal guards; use resource-update for later releases.");
        }
        else
        {
            RunBoundUnity(common.Append("-executeMethod").Append(adapterType + ".BuildFinalPlayer").Append("-logFile").Append(Path.Combine(output, "unity-player.log")), new Dictionary<string, string> { ["DHE_BASELINE_ROOT"] = baseline }, Path.Combine(output, "unity-player-process.log"));
        }
        var playerPath = Path.Combine(output, "dhe-player-result.json");
        var nativePath = Path.Combine(output, "native", "dhe-native-manifest.json");
        ValidateNativeFinalizeEvidence(Path.Combine(output, "adapter", "native-finalize.json"),
            Path.Combine(output, "adapter", "build-final-player.json"), target, project,
            nativePath);
        ValidateFinalRuntimeSourceIdentity(nativePath, project);
        RequireFile(playerPath, "DHE Player result");
        var identityPath = Path.Combine(output, "build-identity.json");
        ValidateBaseAotMetadataArchive(identityPath, baseAotMetadataArchive.SetId);
        if (mode == "Release") productionEvidence = PrepareProductionEvidence(cli, mode, project, settings, baseline, target, output);
        var workflowPath = Path.Combine(output, "player-workflow-report.json");
        WriteJson(workflowPath, PlayerWorkflowReport(output, mode, target, playerPath, nativePath, identityPath,
            runtimePlanPath, baseAotMetadataArchive.Root, baseAotMetadataArchive.SetId,
            productionEvidence));
        string? gatePath = null;
        string? archiveGatePath = null;
        if (mode == "Release")
        {
            gatePath = Path.Combine(output, "release-gate.json");
            if (ReleaseGate(new Cli("release-gate", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["workflowreport"] = workflowPath, ["projectplan"] = planPath, ["output"] = gatePath,
                ["target"] = target, ["requirecompletecoverage"] = "true"
            })) != 0) throw new DheException("DHE release gate failed: " + gatePath);
            var archiveRoot = SafeOutputRoot(cli.Require("archiveroot"), new[] { output, project, baseline });
            archiveGatePath = Path.Combine(output, "archive-gate.json");
            var archiveValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["inputroot"] = output, ["archiveroot"] = archiveRoot, ["output"] = archiveGatePath,
                ["requirecompletecoverage"] = "true"
            };
            if (cli.Has("forceoutput")) archiveValues["forceoutput"] = "true";
            if (Archive(new Cli("archive", archiveValues)) != 0) throw new DheException("DHE archive gate failed: " + archiveGatePath);
        }
        var projectWorkflowPath = Path.Combine(output, "project-workflow-report.json");
        WriteJson(projectWorkflowPath, ProjectWorkflowReport(output, mode, target, project, settings, adapterClass,
            productionEvidence, preparePath, playerPath, nativePath, identityPath, runtimePlanPath,
            baseAotMetadataArchive.Root, baseAotMetadataArchive.SetId, true, archiveGatePath, gatePath));
        RunWorkflowSchemaGate(cli, output);
        Console.WriteLine("DHE workflow passed: " + projectWorkflowPath);
        return 0;
    }

    private static void RunWorkflowSchemaGate(Cli cli, string output)
    {
        var toolchainRoot = Path.GetFullPath(cli.Optional("validationsourceroot") ??
            cli.Optional("toolchainroot") ?? cli.Root);
        var schemaGatePath = Path.Combine(output, "schema-gate.json");
        if (SchemaGate(new Cli("schema-gate", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["schemasroot"] = Path.Combine(toolchainRoot, "schemas"),
            ["inputroot"] = output,
            ["output"] = schemaGatePath,
            ["requireknownformats"] = "true"
        })) != 0)
        {
            throw new DheException("DHE workflow schema gate failed: " + schemaGatePath);
        }
    }

    private static (string EngineWorkflow, string Il2CppCodeGeneration,
        string? AotMetadataAssemblies)
        ResolveWorkflowBuildConfiguration(Cli cli, string? runtimeManifestPath)
    {
        if (string.IsNullOrWhiteSpace(runtimeManifestPath))
        {
            return (cli.Optional("engineworkflow") ?? "Exploratory",
                cli.Optional("il2cppcodegeneration") ?? "OptimizeSpeed", null);
        }

        JsonElement runtime = ReadJson<JsonElement>(RequireFile(runtimeManifestPath,
            "DHE workflow runtime manifest"));
        string engineWorkflow = GetString(runtime, "engineWorkflow") ?? string.Empty;
        string sourceRoot = Path.GetFullPath(cli.Optional("validationsourceroot") ??
            cli.Optional("toolchainroot") ?? cli.Root);
        JsonElement workflowLock = ReadJson<JsonElement>(RequireFile(Path.Combine(sourceRoot,
            "manifests", "runtime-workflows.json"), "DHE runtime workflow lock"));
        JsonElement workflow = workflowLock.GetProperty("workflows").EnumerateArray()
            .SingleOrDefault(item => string.Equals(GetString(item, "id"), engineWorkflow,
                StringComparison.Ordinal));
        if (workflow.ValueKind == JsonValueKind.Undefined)
            throw new DheException("Runtime manifest engine workflow is not locked: " +
                engineWorkflow);
        JsonElement productionWorkflow = workflow.GetProperty("productionWorkflow");
        string il2cppCodeGeneration = GetString(productionWorkflow,
            "il2cppCodeGeneration") ?? string.Empty;
        if (!string.Equals(il2cppCodeGeneration,
                ExpectedIl2CppCodeGeneration(engineWorkflow), StringComparison.Ordinal))
            throw new DheException("Locked IL2CPP code generation is invalid for " +
                engineWorkflow + ".");
        string aotMetadataMode = GetString(productionWorkflow, "aotMetadataMode") ??
            string.Empty;
        string aotMetadataPackaging = GetString(productionWorkflow,
            "aotMetadataPackaging") ?? string.Empty;
        bool supplemental = engineWorkflow == "Unity2021Standard" &&
            aotMetadataMode == "supplemental" && aotMetadataPackaging == "include";
        bool none = engineWorkflow != "Unity2021Standard" &&
            aotMetadataMode == "none" && aotMetadataPackaging == "exclude";
        if (!supplemental && !none)
            throw new DheException("Locked AOT metadata workflow is invalid for " +
                engineWorkflow + ".");
        return (engineWorkflow, il2cppCodeGeneration, none ? "none" : null);
    }

    internal static (string? Root, string SetId) MaterializeBaseAotMetadataRoot(
        string outputRoot, string runtimePlanPath)
    {
        string output = RequireDirectory(outputRoot, "DHE workflow output root");
        string planPath = RequireFile(runtimePlanPath, "DHE runtime handoff plan");
        JsonElement plan = ReadJson<JsonElement>(planPath);
        if (!string.Equals(GetString(plan, "format"),
                "hybridclr.dhe-runtime-handoff-plan.json", StringComparison.Ordinal) ||
            !plan.TryGetProperty("aotMetadata", out JsonElement metadata) ||
            metadata.ValueKind != JsonValueKind.Array)
            throw new DheException("DHE runtime handoff plan has no authenticated AOT metadata set.");

        string archiveRoot = ResolveContainedPath(output, "aot-metadata-root",
            "Base AOT metadata archive");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var setBytes = new List<(string name, byte[] bytes)>();
        string planRoot = Path.GetDirectoryName(planPath)!;
        foreach (JsonElement item in metadata.EnumerateArray())
        {
            string name = NormalizeName(GetString(item, "assemblyName") ?? string.Empty);
            if (string.IsNullOrWhiteSpace(name) || !string.Equals(Path.GetFileName(name), name,
                    StringComparison.Ordinal) || !names.Add(name))
                throw new DheException("DHE AOT metadata archive contains an invalid or duplicate assembly name.");
            string source = RequireFile(ResolveContainedPath(planRoot,
                GetString(item, "path") ?? string.Empty, "DHE AOT metadata payload"),
                name + " AOT metadata payload");
            byte[] bytes = File.ReadAllBytes(source);
            string expectedHash = GetString(item, "sha256") ?? string.Empty;
            if (!IsHex(expectedHash, 64, 64) || !string.Equals(Sha256Bytes(bytes), expectedHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("DHE AOT metadata payload hash mismatch: " + name);
            setBytes.Add((name, bytes));
        }
        string setId = NamedByteSetHash(setBytes);
        if (setBytes.Count == 0)
        {
            if (Directory.Exists(archiveRoot)) Directory.Delete(archiveRoot, true);
            return (null, setId);
        }

        string stagingRoot = ResolveContainedPath(output, "aot-metadata-root.staging",
            "Base AOT metadata archive staging");
        if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true);
        Directory.CreateDirectory(stagingRoot);
        foreach (var item in setBytes)
            File.WriteAllBytes(ResolveContainedPath(stagingRoot, item.name + ".dll",
                "Base AOT metadata assembly"), item.bytes);
        if (Directory.Exists(archiveRoot)) Directory.Delete(archiveRoot, true);
        Directory.Move(stagingRoot, archiveRoot);
        return (archiveRoot, setId);
    }

    private static void ValidateBaseAotMetadataArchive(string identityPath, string setId)
    {
        JsonElement identity = ReadJson<JsonElement>(RequireFile(identityPath,
            "DHE Base build identity"));
        if (!string.Equals(GetString(identity, "aotMetadataSetId"), setId,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException(
                "DHE Base AOT metadata archive does not match the final Player identity.");
    }

    private static object ProjectWorkflowReport(string output, string mode, string target, string project, string settings,
        string adapter, ProductionEvidence evidence, string prepare, string? player, string? native, string? identity,
        string? runtimePlan, string? aotMetadataRoot, string? aotMetadataSetId, bool playerExecuted,
        string? archiveGate, string? releaseGate)
    {
        var preflight = Path.Combine(output, "project-preflight", "project-preflight-report.json");
        var plan = Path.Combine(output, "project-preflight", "dhe-project-plan.json");
        var planValidation = Path.Combine(output, "project-preflight", "project-plan-validation.json");
        var resource = Path.Combine(output, "adapter", "resource-evidence.json");
        var corePassed = evidence.Passed && File.Exists(preflight) && File.Exists(plan) && File.Exists(planValidation) && (!playerExecuted || player != null && File.Exists(player));
        var passed = corePassed && (mode != "Release" || archiveGate != null && File.Exists(archiveGate) && releaseGate != null && File.Exists(releaseGate));
        var stages = new Dictionary<string, object?>
        {
            ["toolchain"] = new { passed = evidence.ToolchainPassed, report = evidence.ToolchainGate },
            ["prepare"] = new { passed = File.Exists(prepare), report = prepare },
            ["cleanCheckout"] = new { passed = evidence.CleanCheckoutPassed, report = evidence.CleanCheckout },
            ["sourcePreflight"] = new { passed = evidence.SourcePreflightPassed, report = evidence.SourcePreflight },
            ["projectPreflight"] = new { passed = File.Exists(preflight), report = preflight },
            ["player"] = new { passed = playerExecuted && player != null && File.Exists(player), report = player },
            ["archive"] = new { passed = archiveGate != null && File.Exists(archiveGate), report = archiveGate },
            ["release"] = new { passed = releaseGate != null && File.Exists(releaseGate), report = releaseGate }
        };
        return new
        {
            schemaVersion = 1, format = "hybridclr.dhe-project-workflow.json", generatedAtUtc = DateTimeOffset.UtcNow,
            passed, mode, toolchainContractVersion = 1, target, adapterScript = adapter,
            projectPath = project, settingsFile = settings, toolchainGate = evidence.ToolchainGate,
            expectedToolchainPackageId = evidence.ExpectedPackageId, sourcePreflight = evidence.SourcePreflight,
            cleanCheckout = evidence.CleanCheckout, projectPreflight = preflight,
            workflowReport = playerExecuted ? Path.Combine(output, "player-workflow-report.json") : null,
            aotMetadataRoot, aotMetadataSetId,
            archiveGate, releaseGate, stages, errors = Array.Empty<string>(),
            warnings = playerExecuted ? Array.Empty<string>() : new[] { "Player, archive, and release stages were not executed." }
        };
    }

    private static object PlayerWorkflowReport(string output, string mode, string target, string playerPath,
        string nativePath, string identityPath, string runtimePlanPath, string? aotMetadataRoot,
        string aotMetadataSetId, ProductionEvidence evidence)
    {
        var planPath = Path.Combine(output, "project-preflight", "dhe-project-plan.json");
        var planValidationPath = Path.Combine(output, "project-preflight", "project-plan-validation.json");
        var batchPath = Path.Combine(output, "project-preflight", "batch", "dhe-batch-summary.json");
        var resourcePath = Path.Combine(output, "adapter", "resource-evidence.json");
        var plan = ReadJson<JsonElement>(planPath);
        var player = ReadJson<JsonElement>(playerPath);
        var native = ReadJson<JsonElement>(nativePath);
        var identity = ReadJson<JsonElement>(identityPath);
        var resource = ReadJson<JsonElement>(resourcePath);
        var records = plan.GetProperty("assemblies").EnumerateArray().ToArray();
        var changed = records.Sum(record => GetInt(record, "changedMethodCount"));
        var methodCount = records.Sum(record =>
        {
            var path = GetString(record, "currentMetaVersionJson");
            return path != null && File.Exists(path) ? GetInt(ReadJson<JsonElement>(path).GetProperty("summary"), "methodCount") : 0;
        });
        var typeChanges = records.Sum(record => GetInt(record, "changedExistingTypeCount") +
            GetInt(record, "addedTypeCount") + GetInt(record, "removedTypeCount"));
        var aotNames = plan.GetProperty("dheAotAssemblies");
        var loadedNames = player.GetProperty("loadedDheAssemblies");
        var transactionStatus = GetString(player, "transactionStatus") ?? (changed == 0 ? "notApplicable" : "failed");
        return new
        {
            schemaVersion = 1, format = "hybridclr.dhe-project-player-workflow.json", generatedAtUtc = DateTimeOffset.UtcNow,
            passed = GetBool(player, "passed"), validationPassed = GetBool(player, "passed"), target, mode,
            engineWorkflow = GetString(identity, "engineWorkflow"),
            il2cppCodeGeneration = GetString(identity, "il2cppCodeGeneration"),
            coverageRequired = true, coverageGatePassed = GetBool(plan, "dheEqualsHotUpdate"),
            releaseReady = mode == "Release" && evidence.Passed && GetBool(player, "passed"), artifactValidationPassed = true,
            buildIdentityReady = true, identityVersion = GetInt(identity, "identityVersion"),
            aotSnapshotKind = GetString(identity, "aotSnapshotKind"),
            nativeGuardSourceSha256 = GetString(identity, "nativeGuardSourceSha256"), nativeManifestSha256 = GetString(identity, "nativeManifestSha256"),
            pathSemantics = "workspace-absolute-v1", projectPlan = planPath, projectPlanValidation = planValidationPath,
            batchReport = batchPath, runtimePlan = runtimePlanPath, runtimePlanProjectPath = runtimePlanPath,
            aotMetadataRoot, aotMetadataSetId,
            sourcePreflight = evidence.SourcePreflight, cleanCheckoutGate = evidence.CleanCheckout,
            toolchainGate = evidence.ToolchainGate, expectedToolchainPackageId = evidence.ExpectedPackageId,
            transaction = new { status = transactionStatus, retryValidated = GetBool(player, "retryValidated"), retryAssemblyName = GetString(player, "retryAssemblyName"), retryFailure = GetString(player, "retryFailure") },
            assemblyScope = new { strategy = "configured-hot-update-equals-dhe", aotAssemblies = aotNames, loadedDheAssemblies = loadedNames, stagedDependencies = Array.Empty<string>(), stagedDependenciesLoadedAsDhe = false },
            capability = new { methodCount, changedMethodCount = changed, typeChangeCount = typeChanges, compatibility = "compatible" },
            nativeGuardCoverage = new { manifestAvailable = true, changedMethodCount = GetInt(native, "changedMethodCount"), supportedChangedMethodCount = GetInt(native, "supportedChangedMethodCount"), unsupportedChangedMethodCount = GetInt(native, "unsupportedChangedMethodCount"), nativeEntryCount = GetInt(native, "nativeEntryCount"), guardedMethodCount = GetInt(native, "supportedChangedMethodCount"), complete = GetInt(native, "unsupportedChangedMethodCount") == 0 && GetInt(native, "changedMethodCount") == changed },
            player, playerResult = playerPath, nativeManifest = nativePath, buildIdentity = identityPath,
            resourceEvidence = resourcePath,
            resourceBuildPolicy = GetString(resource, "policy") ?? "required",
            artifactValidation = (string?)null,
            archiveManifest = (string?)null, archiveGate = (string?)null, runtimeSource = evidence.RuntimeManifest
        };
    }

    private static object PlayerEvidence(string? path, bool executed)
    {
        if (path != null && File.Exists(path)) return ReadJson<JsonElement>(path);
        return new { passed = !executed, loadError = executed ? "missing" : "not-run" };
    }

    private static string ReadHash(string? path, string property)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new string('0', 64);
        return GetString(ReadJson<JsonElement>(path), property) ?? new string('0', 64);
    }

    private static int ReleaseEvidence(Cli cli)
    {
        var sourceRoot = RequireDirectory(cli.Optional("labroot") ?? cli.Root, "DHE release source root");
        var sourceHead = GitValue(sourceRoot, "rev-parse", "HEAD");
        var sourceTree = GitValue(sourceRoot, "rev-parse", "HEAD^{tree}");
        var sourceClean = !string.IsNullOrWhiteSpace(sourceHead) &&
            string.IsNullOrWhiteSpace(GitValue(sourceRoot, "status", "--porcelain"));
        if (!sourceClean || !IsHex(sourceHead, 40, 64) || !IsHex(sourceTree, 40, 64))
            throw new DheException("Release evidence generation requires a clean Git-tracked source HEAD/tree.");

        var staticInputs = new[]
        {
            (Role: "regression", Option: "regression"),
            (Role: "demo-noop", Option: "demonoop"),
            (Role: "native-tuanjie2022", Option: "nativetuanjie2022"),
            (Role: "native-unity2022", Option: "nativeunity2022"),
            (Role: "resolver-tuanjie2022", Option: "resolvertuanjie2022"),
            (Role: "resolver-unity2022", Option: "resolverunity2022")
        }.Select(item => (item.Role, Path: RequireFile(cli.Require(item.Option), item.Role + " evidence"))).ToArray();
        var changedPlayerPaths = cli.GetList("changedplayers");
        if (changedPlayerPaths.Count == 0)
        {
            foreach (string option in new[] { "demochanged", "demochangedbase2", "demochangedbase3" })
            {
                string? value = cli.Optional(option);
                if (!string.IsNullOrWhiteSpace(value)) changedPlayerPaths.Add(value);
            }
        }
        changedPlayerPaths = changedPlayerPaths
            .Select(path => RequireFile(path, "changed Player evidence"))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (changedPlayerPaths.Count < RequiredPlayerEngineWorkflows.Length)
            throw new DheException("Release evidence requires changed Base Player reports " +
                "covering Unity 2022 and Tuanjie 2022.");
        if (changedPlayerPaths.Count > MaxChangedPlayerEvidenceCount)
            throw new DheException("Release evidence changed Player report count exceeds " +
                MaxChangedPlayerEvidenceCount + ".");

        var allInputs = staticInputs.Select(item => item.Path).Concat(changedPlayerPaths).ToArray();
        var outputRoot = SafeOutputRoot(cli.Require("outputroot"), allInputs.Append(sourceRoot));
        EnsureOutputOutsideRoot(outputRoot, sourceRoot);
        var outputPrefix = outputRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (allInputs.Any(path => path.StartsWith(outputPrefix, StringComparison.OrdinalIgnoreCase)))
            throw new DheException("Release evidence output cannot contain one of its input reports.");
        if (Directory.Exists(outputRoot))
        {
            if (!cli.Has("forceoutput") && Directory.EnumerateFileSystemEntries(outputRoot).Any())
                throw new DheException("Release evidence output is not empty: " + outputRoot);
            if (cli.Has("forceoutput")) Directory.Delete(outputRoot, true);
        }
        var reportsRoot = Path.Combine(outputRoot, "reports");
        Directory.CreateDirectory(reportsRoot);
        var files = new List<object>();
        foreach (var item in staticInputs)
        {
            var relative = "reports/" + item.Role + ".json";
            var destination = Path.Combine(outputRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            File.Copy(item.Path, destination, true);
            files.Add(new { role = item.Role, path = relative, sha256 = Sha256File(destination) });
        }
        for (int index = 0; index < changedPlayerPaths.Count; index++)
        {
            string source = changedPlayerPaths[index];
            JsonElement report = ReadJson<JsonElement>(source);
            var identity = GetChangedPlayerEvidenceIdentity(report, source);
            string relative = "reports/player-changed-" + (index + 1).ToString("D3") + ".json";
            string destination = Path.Combine(outputRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            File.Copy(source, destination, true);
            files.Add(new
            {
                role = "player-changed",
                engineWorkflow = identity.EngineWorkflow,
                baseId = identity.BaseId,
                path = relative,
                sha256 = Sha256File(destination)
            });
        }
        var evidencePath = Path.Combine(outputRoot, "dhe-toolchain-release-evidence.json");
        WriteJson(evidencePath, new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-toolchain-release-evidence.json",
            generatedAtUtc = DateTimeOffset.UtcNow,
            passed = true,
            sourceHead,
            sourceTree,
            files
        });
        var evidence = ReadJson<JsonElement>(evidencePath);
        var schema = ReadJson<JsonElement>(RequireFile(Path.Combine(sourceRoot, "schemas",
            "dhe-toolchain-release-evidence.schema.json"), "Release evidence schema"));
        var schemaErrors = new List<string>();
        ValidateSchemaVocabulary(schema, "$", schemaErrors);
        if (schemaErrors.Count == 0) ValidateJsonSchema(schema, evidence, schema, "$", schemaErrors);
        if (schemaErrors.Count > 0)
            throw new DheException("Generated release evidence violates its schema: " + string.Join("; ", schemaErrors));
        string[] configuredEvidenceRoots = cli.GetList("evidencetoolchainroots")
            .Select(path => RequireDirectory(path, "Historical evidence toolchain package"))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ValidateEvidenceFiles(evidence, outputRoot, sourceRoot, configuredEvidenceRoots);
        Console.WriteLine("DHE release evidence: " + evidencePath);
        return 0;
    }

    private static void ValidateEvidenceFiles(JsonElement evidence, string baseDirectory,
        string sourceRoot, IEnumerable<string>? additionalPackageRoots = null)
    {
        if (!evidence.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array ||
            files.GetArrayLength() < RequiredStaticReleaseEvidenceRoles.Length +
                RequiredPlayerEngineWorkflows.Length ||
            files.GetArrayLength() > RequiredStaticReleaseEvidenceRoles.Length +
                MaxChangedPlayerEvidenceCount)
            throw new DheException("Release evidence must contain the complete Player, resolver, and native matrix.");
        var sourceHead = GetString(evidence, "sourceHead");
        var sourceTree = GetString(evidence, "sourceTree");
        if (!IsHex(sourceHead, 40, 64) || !IsHex(sourceTree, 40, 64))
            throw new DheException("Release evidence source identity is invalid.");
        var staticRoles = new HashSet<string>(StringComparer.Ordinal);
        var evidenceHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var changedReports = new List<(JsonElement Report, string Path)>();
        JsonElement? regressionReport = null;
        var managedContractRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(sourceRoot),
        };
        foreach (string root in (additionalPackageRoots ?? Array.Empty<string>())
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            managedContractRoots.Add(root);
        foreach (JsonElement item in files.EnumerateArray())
        {
            string? role = GetString(item, "role");
            if (role is not ("player-changed" or "demo-noop")) continue;
            string? path = GetString(item, "path");
            string? expected = GetString(item, "sha256");
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ||
                path.Contains("..", StringComparison.Ordinal) ||
                !IsHex(expected, 64, 64))
                throw new DheException("Release evidence contains an unsafe managed file entry.");
            string full = RequireFile(Path.Combine(baseDirectory, path),
                "Release managed evidence file");
            if (!Sha256File(full).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new DheException("Release evidence file hash mismatch: " + path);
            JsonElement managedReport = ReadJson<JsonElement>(full);
            managedContractRoots.Add(ResolveManagedEvidenceContractRoot(managedReport, full,
                managedContractRoots));
        }
        foreach (var item in files.EnumerateArray())
        {
            var path = GetString(item, "path");
            var expected = GetString(item, "sha256");
            var role = GetString(item, "role");
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(expected) || Path.IsPathRooted(path) || path.Contains("..", StringComparison.Ordinal))
                throw new DheException("Release evidence contains an unsafe file entry.");
            if (string.IsNullOrWhiteSpace(role))
                throw new DheException("Release evidence contains a missing role.");
            bool changedPlayer = string.Equals(role, "player-changed", StringComparison.Ordinal);
            if (!changedPlayer && !staticRoles.Add(role))
                throw new DheException("Release evidence contains a duplicate static role: " + role + ".");
            var full = RequireFile(Path.Combine(baseDirectory, path), "Release evidence file");
            if (!Sha256File(full).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new DheException("Release evidence file hash mismatch: " + path);
            var report = ReadJson<JsonElement>(full);
            if (!GetBool(report, "passed")) throw new DheException("Release evidence report is not a passing result: " + path);
            ValidateEvidenceRole(role, report, full, sourceHead!, sourceTree!, sourceRoot,
                managedContractRoots);
            string key = GetReleaseEvidenceKey(item, report, full);
            if (!evidenceHashes.TryAdd(key, expected!))
                throw new DheException("Release evidence contains a duplicate identity: " + key + ".");
            if (role == "regression") regressionReport = report;
            if (changedPlayer) changedReports.Add((report, full));
        }
        foreach (var required in RequiredStaticReleaseEvidenceRoles)
            if (!staticRoles.Contains(required)) throw new DheException("Release evidence is missing role: " + required);
        if (!regressionReport.HasValue || !regressionReport.Value.TryGetProperty("workflowOutputs", out var workflowOutputs) ||
            workflowOutputs.ValueKind != JsonValueKind.Array ||
            workflowOutputs.GetArrayLength() != changedReports.Count + 1)
            throw new DheException("Regression evidence does not bind every changed Base and the no-op workflow.");
        foreach (var workflow in workflowOutputs.EnumerateArray())
        {
            string workflowPath = ResolveEvidencePath(GetString(workflow, "path"),
                baseDirectory, "Regression workflow output");
            JsonElement workflowReport = ReadJson<JsonElement>(workflowPath);
            string key = GetReleaseEvidenceKey(workflow, workflowReport, workflowPath);
            if (!evidenceHashes.TryGetValue(key, out var evidenceHash) ||
                !string.Equals(evidenceHash, GetString(workflow, "sha256"), StringComparison.OrdinalIgnoreCase))
                throw new DheException("Regression workflow output hash does not match release evidence: " + key);
        }
        if (!regressionReport.Value.TryGetProperty("resolverOutputs", out var resolverOutputs) ||
            resolverOutputs.ValueKind != JsonValueKind.Array ||
            resolverOutputs.GetArrayLength() != RequiredPlayerEngineWorkflows.Length)
            throw new DheException("Regression evidence does not bind the supported-engine generated-C++ resolver matrix.");
        foreach (var resolver in resolverOutputs.EnumerateArray())
        {
            var role = GetString(resolver, "role") ?? "";
            if (!evidenceHashes.TryGetValue(role, out var evidenceHash) ||
                !string.Equals(evidenceHash, GetString(resolver, "sha256"), StringComparison.OrdinalIgnoreCase))
                throw new DheException("Regression resolver output hash does not match release evidence: " + role);
        }
        MultiBaseResourceReleaseProof releaseProof =
            ReadMultiBaseResourceReleaseProof(changedReports, true);
        ValidateRegressionResourceReleaseBinding(regressionReport.Value, releaseProof);
    }

    private static void ValidateEvidenceRole(string role, JsonElement report, string reportPath,
        string sourceHead, string sourceTree, string sourceRoot,
        IEnumerable<string> managedContractRoots)
    {
        switch (role)
        {
            case "regression":
                RequireEvidenceFormat(report, "hybridclr.dhe-regression.json", role);
                if (!string.Equals(GetString(report, "sourceHead"), sourceHead, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(GetString(report, "sourceTree"), sourceTree, StringComparison.OrdinalIgnoreCase) ||
                    !GetBool(report, "sourceClean"))
                    throw new DheException("Regression evidence is not bound to the clean release source identity.");
                var checkNames = report.GetProperty("checks").EnumerateArray()
                    .Where(check => GetBool(check, "passed"))
                    .Select(check => GetString(check, "name") ?? "")
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var required in RequiredRegressionChecks)
                    if (!checkNames.Contains(required))
                        throw new DheException("Regression evidence is missing check: " + required);
                if (!GetBool(report, "realWorkflowOutputsValidated") ||
                    !report.TryGetProperty("workflowOutputs", out var workflowOutputs) ||
                    workflowOutputs.ValueKind != JsonValueKind.Array ||
                    workflowOutputs.GetArrayLength() < RequiredPlayerEngineWorkflows.Length + 1)
                    throw new DheException("Regression evidence did not validate the supported-engine changed Base matrix and no-op output tree.");
                var workflowKeys = new HashSet<string>(StringComparer.Ordinal);
                var regressionChangedReports = new List<(JsonElement Report, string Path)>();
                int noOpWorkflowCount = 0;
                foreach (var workflow in workflowOutputs.EnumerateArray())
                {
                    var workflowRole = GetString(workflow, "role") ?? "";
                    var workflowPath = ResolveEvidencePath(GetString(workflow, "path"),
                        Path.GetDirectoryName(reportPath)!, "Regression workflow output");
                    var workflowReport = ReadJson<JsonElement>(workflowPath);
                    bool changedWorkflow = workflowRole == "player-changed";
                    if (!changedWorkflow && workflowRole != "demo-noop")
                        throw new DheException("Regression workflow output has an unknown role: " + workflowRole);
                    var expectedFormat = changedWorkflow
                        ? "hybridclr.dhe-resource-player-workflow.json"
                        : "hybridclr.dhe-project-player-workflow.json";
                    RequireEvidenceFormat(workflowReport, expectedFormat, workflowRole);
                    string key = GetReleaseEvidenceKey(workflow, workflowReport, workflowPath);
                    if (!workflowKeys.Add(key) || !Sha256File(workflowPath).Equals(
                            GetString(workflow, "sha256"), StringComparison.OrdinalIgnoreCase))
                        throw new DheException("Regression workflow output identity is invalid: " + key);
                    if (changedWorkflow) regressionChangedReports.Add((workflowReport, workflowPath));
                    else noOpWorkflowCount++;
                }
                if (noOpWorkflowCount != 1 ||
                    regressionChangedReports.Count < RequiredPlayerEngineWorkflows.Length)
                    throw new DheException("Regression workflow output roles are incomplete.");
                MultiBaseResourceReleaseProof regressionReleaseProof =
                    ReadMultiBaseResourceReleaseProof(regressionChangedReports, true);
                ValidateRegressionResourceReleaseBinding(report, regressionReleaseProof);
                if (!GetBool(report, "realResolverOutputsValidated") ||
                    !report.TryGetProperty("resolverOutputs", out var resolverOutputs) ||
                    resolverOutputs.ValueKind != JsonValueKind.Array ||
                    resolverOutputs.GetArrayLength() != RequiredPlayerEngineWorkflows.Length)
                    throw new DheException("Regression evidence did not validate the supported-engine generated-C++ resolver matrix.");
                var resolverRoles = new HashSet<string>(StringComparer.Ordinal);
                foreach (var resolver in resolverOutputs.EnumerateArray())
                {
                    var resolverRole = GetString(resolver, "role") ?? "";
                    var resolverPath = ResolveEvidencePath(GetString(resolver, "path"),
                        Path.GetDirectoryName(reportPath)!, "Regression resolver output");
                    if (!resolverRoles.Add(resolverRole) || !Sha256File(resolverPath).Equals(
                            GetString(resolver, "sha256"), StringComparison.OrdinalIgnoreCase))
                        throw new DheException("Regression resolver output identity is invalid: " + resolverRole);
                    ValidateResolverEvidence(resolverRole, ReadJson<JsonElement>(resolverPath), sourceRoot);
                }
                if (!resolverRoles.SetEquals(RequiredStaticReleaseEvidenceRoles.Where(
                    required => required.StartsWith("resolver-", StringComparison.Ordinal))))
                    throw new DheException("Regression resolver output roles are incomplete.");
                break;
            case "player-changed":
            case "demo-noop":
                bool changedRole = role == "player-changed";
                RequireEvidenceFormat(report, changedRole
                    ? "hybridclr.dhe-resource-player-workflow.json"
                    : "hybridclr.dhe-project-player-workflow.json", role);
                if (!GetBool(report, "validationPassed") || !GetBool(report, "coverageGatePassed"))
                    throw new DheException(role + " evidence did not pass validation and coverage.");
                ValidateEvidenceToolIdentity(report, reportPath, sourceRoot, sourceHead,
                    sourceTree, managedContractRoots);
                ValidateManagedReleaseEvidence(report, reportPath, managedContractRoots);
                var changed = GetInt(report.GetProperty("capability"), "changedMethodCount");
                var player = report.GetProperty("player");
                if (changedRole)
                {
                    bool resourceOnly = GetString(report, "format") ==
                        "hybridclr.dhe-resource-player-workflow.json";
                    if (resourceOnly) ValidateResourcePlayerEvidenceBindings(report, reportPath);
                    if (changed <= 0 || GetInt(player, "changedMethodCount") != changed ||
                        GetInt(player, "interpreterEntryCount") <= 0 || GetInt(player, "aotEntryCount") <= 0 ||
                        !GetBool(player, "dispatchProbeValidated") ||
                        (!resourceOnly && !GetBool(player, "changedProbeChanged")) ||
                        (!resourceOnly && GetBool(player, "unchangedProbeChanged")) ||
                        !GetBool(player, "retryValidated") ||
                        GetString(player, "transactionStatus") != "validated")
                        throw new DheException("Changed Demo evidence does not prove interpreter/AOT dispatch and rollback.");
                }
                else
                {
                    if (changed != 0) throw new DheException("No-op Demo workflow contains changed methods.");
                    ValidateNoOpPlayerEvidence(player);
                }
                break;
            case "native-tuanjie2022":
            case "native-unity2022":
            case "native-unity2021":
                RequireEvidenceFormat(report, "hybridclr.dhe-native-gate.json", role);
                if (GetInt(report, "nativeExitCode") != 0 || !GetBool(report, "mergeReady") ||
                    GetBool(report, "surrogateHeadersAllowed") ||
                    !IsHex(GetString(report, "runtimeTreeSha256"), 64, 64) ||
                    !IsHex(GetString(report, "externalTreeSha256"), 64, 64))
                    throw new DheException("Native evidence does not prove a real-header passing native gate.");
                var nativeRequirement = role switch
                {
                    "native-tuanjie2022" => (Profile: "DHE-Tuanjie2022", Workflow: "Tuanjie2022Fgs"),
                    "native-unity2022" => (Profile: "DHE-Unity2022", Workflow: "Unity2022Fgs"),
                    _ => (Profile: "DHE-Unity2021", Workflow: "Unity2021Standard")
                };
                ValidateNativeReleaseEvidence(report, reportPath, sourceRoot,
                    nativeRequirement.Profile, nativeRequirement.Workflow);
                break;
            case "resolver-tuanjie2022":
            case "resolver-unity2022":
            case "resolver-unity2021":
                ValidateResolverEvidence(role, report, sourceRoot);
                break;
            default:
                throw new DheException("Unknown release evidence role: " + role);
        }
    }

    private static void ValidateResolverEvidence(string role, JsonElement report, string sourceRoot)
    {
        RequireEvidenceFormat(report, "hybridclr.dhe-cpp-resolver-regression.json", role);
        var requirement = role switch
        {
            "resolver-tuanjie2022" => (Workflow: "Tuanjie2022Fgs", UnityVersion: "2022.3.62t12"),
            "resolver-unity2022" => (Workflow: "Unity2022Fgs", UnityVersion: "2022.3.62f3"),
            "resolver-unity2021" => (Workflow: "Unity2021Standard", UnityVersion: "2021.3.45f2"),
            _ => throw new DheException("Unknown resolver evidence role: " + role)
        };
        if (!GetBool(report, "passed") ||
            !string.Equals(GetString(report, "engineWorkflow"), requirement.Workflow, StringComparison.Ordinal) ||
            !string.Equals(GetString(report, "unityVersion"), requirement.UnityVersion, StringComparison.Ordinal))
            throw new DheException(role + " does not match its locked engine workflow.");

        var schema = ReadJson<JsonElement>(RequireFile(Path.Combine(sourceRoot, "schemas",
            "dhe-cpp-resolver-regression.schema.json"), "Resolver evidence schema"));
        var schemaErrors = new List<string>();
        ValidateSchemaVocabulary(schema, "$", schemaErrors);
        if (schemaErrors.Count == 0) ValidateJsonSchema(schema, report, schema, "$", schemaErrors);
        if (schemaErrors.Count > 0)
            throw new DheException(role + " violates its schema: " + string.Join("; ", schemaErrors));

        var packageLock = ReadJson<JsonElement>(RequireFile(Path.Combine(sourceRoot, "manifests",
            "dhe-package-lock.json"), "Resolver package lock"));
        if (!string.Equals(GetString(report, "resolverSourceSha256"),
                GetString(packageLock, "resolverSourceSha256"), StringComparison.OrdinalIgnoreCase))
            throw new DheException(role + " was not executed against the locked resolver source.");
        var checks = report.GetProperty("checks").EnumerateArray()
            .Where(check => GetBool(check, "passed"))
            .Select(check => GetString(check, "name") ?? "").ToHashSet(StringComparer.Ordinal);
        foreach (var required in RequiredResolverChecks)
            if (!checks.Contains(required))
                throw new DheException(role + " is missing resolver check: " + required);
        if (report.GetProperty("errors").GetArrayLength() != 0)
            throw new DheException(role + " contains resolver errors.");
    }

    private static string ResolveManagedEvidenceContractRoot(JsonElement report,
        string reportPath, IEnumerable<string>? additionalContractRoots = null)
    {
        string reportRoot = Path.GetDirectoryName(reportPath)!;
        string expectedPackageId = GetString(report, "expectedToolchainPackageId") ??
            string.Empty;
        string gatePath = ResolveEvidencePath(GetString(report, "toolchainGate"),
            reportRoot, "Managed Player toolchain gate");
        JsonElement gate = ReadJson<JsonElement>(gatePath);
        RequireEvidenceFormat(gate, "hybridclr.dhe-toolchain-gate.json",
            "Managed Player toolchain gate");
        string gatePackageRoot = GetString(gate, "packageRoot") ?? string.Empty;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(gatePackageRoot))
        {
            candidates.Add(Path.GetFullPath(Path.IsPathRooted(gatePackageRoot)
                ? gatePackageRoot
                : Path.Combine(Path.GetDirectoryName(gatePath)!, gatePackageRoot)));
        }
        candidates.AddRange((additionalContractRoots ?? Array.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath));
        string? packageRoot = candidates.Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(candidate => Directory.Exists(candidate) &&
                InspectPackage(candidate, expectedPackageId, true).Passed);
        if (!GetBool(gate, "passed") || !GetBool(gate, "requireRelease") ||
            !GetBool(gate, "releaseReady") || packageRoot == null ||
            !string.Equals(GetString(gate, "packageId"), expectedPackageId,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(gate, "expectedPackageId"), expectedPackageId,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException(
                "Managed Player toolchain package is not an authenticated Release package. " +
                "Checked roots: " + string.Join(", ", candidates) + ".");
        return packageRoot;
    }

    private static string ResolveManagedRuntimeContractRoot(JsonElement runtime,
        string evidenceContractRoot, IEnumerable<string>? additionalContractRoots,
        string reportPath)
    {
        string expectedLockSha256 = GetString(runtime, "dheRuntimeLockSha256") ??
            string.Empty;
        IEnumerable<string> candidates = new[] { evidenceContractRoot }
            .Concat(additionalContractRoots ?? Array.Empty<string>())
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in candidates)
        {
            string lockPath = Path.Combine(candidate, "manifests", "dhe-runtime-lock.json");
            if (File.Exists(lockPath) && Sha256File(lockPath).Equals(expectedLockSha256,
                    StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        throw new DheException("Managed Player runtime lock " + expectedLockSha256 +
            " does not match an authenticated release contract for report " +
            Path.GetFullPath(reportPath) + ". Checked roots: " +
            string.Join(", ", candidates) + ".");
    }

    private static string ValidateManagedReleaseEvidence(JsonElement report, string reportPath,
        IEnumerable<string>? additionalContractRoots = null)
    {
        if (!string.Equals(GetString(report, "mode"), "Release", StringComparison.Ordinal) ||
            !GetBool(report, "releaseReady"))
            throw new DheException("Managed Player release evidence must come from a Release-ready workflow.");

        bool archivedBase = ValidateResourceBaseArchive(report, reportPath);
        string reportRoot = Path.GetDirectoryName(reportPath)!;
        // A supported Base can outlive the toolchain that built it. Validate its
        // runtime locks against that Base's authenticated Release package; the
        // current package separately authorizes the historical Package ID.
        string evidenceContractRoot = ResolveManagedEvidenceContractRoot(report, reportPath,
            additionalContractRoots);
        string runtimePath = ResolveEvidencePath(GetString(report, "runtimeSource"), reportRoot,
            "Managed Player runtime manifest");
        JsonElement runtime = ReadJson<JsonElement>(runtimePath);
        RequireEvidenceFormat(runtime, "hybridclr.dhe-runtime-manifest.json",
            "Managed Player runtime manifest");
        bool archivedRuntime = string.Equals(GetString(runtime, "pathSemantics"),
            "archive-relative-v1", StringComparison.Ordinal);
        if (archivedBase != archivedRuntime)
            throw new DheException("Managed Player archive and runtime path semantics do not agree.");
        if (!GetBool(runtime, "dheEnabled") || GetString(runtime, "dheRuntimeSourceMode") != "integrated")
            throw new DheException("Managed Player runtime evidence is not an integrated DHE runtime.");
        string contractRoot = ResolveManagedRuntimeContractRoot(runtime,
            evidenceContractRoot, additionalContractRoots, reportPath);
        JsonElement headers = runtime.GetProperty("externalHeaders");
        if (GetBool(headers, "surrogate") || GetBool(headers, "explicitlyAllowed"))
            throw new DheException("Managed Player runtime evidence uses surrogate external headers.");
        string externalTree;
        if (archivedBase)
        {
            if (!IsHex(GetString(runtime, "stagedRuntimeSha256"), 64, 64) ||
                !IsHex(GetString(headers, "stagedTreeSha256"), 64, 64))
                throw new DheException("Archived managed runtime tree identities are invalid.");
            externalTree = GetString(headers, "stagedTreeSha256")!;
        }
        else
        {
            string stagedRuntime = RequireDirectory(GetString(runtime, "stagedLibil2cpp") ??
                string.Empty, "Managed Player staged runtime");
            if (IsProjectInstallation(runtime))
                ValidateProjectInstallation(runtime, GetString(report, "projectPath") ?? throw new DheException("Missing project path."));
            else if (!TreeHashForRelease(stagedRuntime, Array.Empty<string>()).Equals(
                    GetString(runtime, "stagedRuntimeSha256"),
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("Managed Player staged runtime tree has changed.");
            string stagedHeaders = RequireDirectory(GetString(headers, "stagedPath") ??
                string.Empty, "Managed Player staged external headers");
            externalTree = TreeHashForRelease(stagedHeaders, Array.Empty<string>());
            if (!externalTree.Equals(GetString(headers, "stagedTreeSha256"),
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("Managed Player external header tree has changed.");
        }
        string currentRuntimeLock = RequireFile(Path.Combine(contractRoot, "manifests",
            "dhe-runtime-lock.json"), "Managed Player runtime lock");
        if (!Sha256File(currentRuntimeLock).Equals(GetString(runtime, "dheRuntimeLockSha256"),
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Managed Player runtime lock does not match the release source.");

        string preflightPath = ResolveEvidencePath(GetString(report, "sourcePreflight"), reportRoot,
            "Managed Player source preflight");
        JsonElement preflight = ReadJson<JsonElement>(preflightPath);
        RequireEvidenceFormat(preflight, "hybridclr.dhe-source-preflight.json",
            "Managed Player source preflight");
        string preflightRuntime = ResolveEvidencePath(GetString(preflight, "runtimeSource"),
            Path.GetDirectoryName(preflightPath)!, "Preflight runtime manifest");
        if (!GetBool(preflight, "passed") || !GetBool(preflight, "runtimeRequired") ||
            !GetBool(preflight, "runtimeReady") || !GetBool(preflight, "cleanRuntimeSourcesRequired") ||
            !GetBool(preflight, "externalHeadersRequired") ||
            !preflight.TryGetProperty("externalHeadersSurrogate", out JsonElement surrogate) ||
            surrogate.ValueKind != JsonValueKind.False ||
            !Path.GetFullPath(preflightRuntime).Equals(Path.GetFullPath(runtimePath),
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Managed Player preflight is not bound to the required real-header runtime.");

        string cleanPath = ResolveEvidencePath(GetString(report, "cleanCheckoutGate"), reportRoot,
            "Managed Player clean checkout");
        JsonElement clean = ReadJson<JsonElement>(cleanPath);
        RequireEvidenceFormat(clean, "hybridclr.dhe-clean-checkout-gate.json",
            "Managed Player clean checkout");
        if (!GetBool(clean, "passed") || !GetBool(clean, "gitCleanRequired") ||
            !GetBool(clean, "trackedSourcesRequired") || !GetBool(clean, "trackedSourcesComplete"))
            throw new DheException("Managed Player evidence is not bound to clean tracked project and tool sources.");

        bool installedProject = IsProjectInstallation(runtime);
        JsonElement repoLock = installedProject ? ReadJson<JsonElement>(currentRuntimeLock) :
            ReadJson<JsonElement>(RequireFile(Path.Combine(contractRoot, "manifests", "repo-lock.json"), "Managed Player repository lock"));
        JsonElement workflowLock = ReadJson<JsonElement>(RequireFile(Path.Combine(contractRoot, "manifests",
            "runtime-workflows.json"), "Managed Player runtime workflows"));
        string workflowId = GetString(runtime, "engineWorkflow") ?? string.Empty;
        JsonElement workflow = workflowLock.GetProperty("workflows").EnumerateArray().SingleOrDefault(item =>
            GetString(item, "id") == workflowId);
        if (workflow.ValueKind == JsonValueKind.Undefined)
            throw new DheException("Managed Player runtime workflow is not locked by the release source.");
        if (!installedProject && !externalTree.Equals(GetString(workflow.GetProperty("engine"),
                "externalHeadersTreeSha256"), StringComparison.OrdinalIgnoreCase))
            throw new DheException("Managed Player headers do not match the locked engine workflow.");
        JsonElement sources = runtime.GetProperty("source");
        if (installedProject) ValidateProjectRuntimeSourceRecords(runtime, repoLock, contractRoot);
        foreach (string repository in new[] { "hybridclr", "il2cpp_plus", "hybridclr_unity" })
        {
            if (installedProject) continue; // Validated against the authenticated release and package tool above.
            string? expected = repository == "il2cpp_plus"
                ? GetString(workflow.GetProperty("il2cppPlus"), "commit")
                : GetString(repoLock.GetProperty("repositories").GetProperty(repository), "commit");
            JsonElement actual = sources.GetProperty(repository);
            if (!string.Equals(expected, GetString(actual, "commit"), StringComparison.OrdinalIgnoreCase) ||
                GetBool(actual, "dirty") || !IsHex(GetString(actual, "treeSha256"), 64, 64))
                throw new DheException("Managed Player runtime source identity is invalid: " + repository);
            if (archivedBase) continue;
            string sourcePath = RequireDirectory(GetString(actual, "path") ?? string.Empty,
                "Managed Player " + repository + " source");
            if (!string.Equals(GitValue(sourcePath, "rev-parse", "HEAD"), expected,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(GitValue(sourcePath, "status", "--porcelain")))
                throw new DheException("Managed Player runtime source checkout is not clean at its locked commit: " +
                    repository);
            string treeRoot = repository switch
            {
                "hybridclr" => RequireDirectory(Path.Combine(sourcePath, "hybridclr"),
                    "Managed Player HybridCLR source tree"),
                "il2cpp_plus" => RequireDirectory(Path.Combine(sourcePath, "libil2cpp"),
                    "Managed Player il2cpp_plus source tree"),
                _ => sourcePath,
            };
            var ignoredPaths = actual.TryGetProperty("treeHashIgnoredPaths", out var ignoredValue) &&
                ignoredValue.ValueKind == JsonValueKind.Array
                ? ignoredValue.EnumerateArray()
                    .Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString() ?? string.Empty)
                    .Where(value => value.Length > 0)
                    .ToArray()
                : Array.Empty<string>();
            string liveTree = LabCommands.CanonicalSourceTreeHash(treeRoot,
                repository == "hybridclr_unity", ignoredPaths);
            if (!liveTree.Equals(GetString(actual, "treeSha256"), StringComparison.OrdinalIgnoreCase))
                throw new DheException("Managed Player runtime source tree has changed: " + repository);
        }

        string identityPath = ResolveEvidencePath(GetString(report, "buildIdentity"), reportRoot,
            "Managed Player build identity");
        JsonElement identity = ReadJson<JsonElement>(identityPath);
        string expectedCodeGeneration = GetString(workflow.GetProperty("productionWorkflow"),
            "il2cppCodeGeneration") ?? string.Empty;
        if (!string.Equals(GetString(identity, "engineWorkflow"), workflowId,
                StringComparison.Ordinal) ||
            !string.Equals(GetString(identity, "il2cppCodeGeneration"),
                expectedCodeGeneration, StringComparison.Ordinal) ||
            !string.Equals(expectedCodeGeneration,
                ExpectedIl2CppCodeGeneration(workflowId), StringComparison.Ordinal))
            throw new DheException(
                "Managed Player build configuration does not match its locked engine workflow.");

        if (GetString(report, "format") == "hybridclr.dhe-resource-player-workflow.json")
        {
            string baseWorkflowPath = ResolveEvidencePath(GetString(report, "baseWorkflowReport"),
                reportRoot, "Resource Base workflow");
            JsonElement baseWorkflow = ReadJson<JsonElement>(baseWorkflowPath);
            if (GetString(baseWorkflow, "mode") != "Release" || !GetBool(baseWorkflow, "releaseReady") ||
                !Path.GetFullPath(ResolveEvidencePath(GetString(baseWorkflow, "runtimeSource"),
                    Path.GetDirectoryName(baseWorkflowPath)!, "Base workflow runtime manifest"))
                    .Equals(Path.GetFullPath(runtimePath), StringComparison.OrdinalIgnoreCase))
                throw new DheException("Resource Player evidence is not bound to a Release-ready Base workflow.");
            if (GetString(report, "resourceReleaseMode") != "Release" ||
                !GetBool(report, "resourceReleaseReady") ||
                !IsRegistryId(GetString(report, "releaseChannelId") ?? string.Empty) ||
                GetInt(report, "releaseRevision") < 1 ||
                !IsHex(GetString(report, "releaseLedgerSha256"), 64, 64))
                throw new DheException(
                    "Resource Player evidence is not bound to a Release ledger.");
            string ledgerPath = ResolveEvidencePath(GetString(report, "releaseLedger"),
                reportRoot, "Resource release ledger");
            ReleaseLedgerDocument ledger = ReadReleaseLedger(ledgerPath,
                GetString(report, "releaseLedgerSha256"));
            if (!string.Equals(ledger.ChannelId, GetString(report, "releaseChannelId"),
                    StringComparison.Ordinal) ||
                ledger.Revision != GetInt(report, "releaseRevision") ||
                !string.Equals(ledger.ParentLedgerSha256,
                    GetString(report, "parentReleaseLedgerSha256"),
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException(
                    "Resource Player evidence release identity does not match its ledger.");
        }
        return contractRoot;
    }

    private static string GetReleaseEvidenceKey(JsonElement record, JsonElement report, string reportPath)
    {
        string role = GetString(record, "role") ?? string.Empty;
        if (role != "player-changed")
        {
            if (record.TryGetProperty("engineWorkflow", out _) || record.TryGetProperty("baseId", out _))
                throw new DheException("Static release evidence must not declare a Player identity: " + role + ".");
            return role;
        }
        var identity = GetChangedPlayerEvidenceIdentity(report, reportPath);
        if (!string.Equals(GetString(record, "engineWorkflow"), identity.EngineWorkflow,
                StringComparison.Ordinal) ||
            !string.Equals(GetString(record, "baseId"), identity.BaseId,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Changed Player evidence record does not match its runtime/Base identity.");
        return role + "/" + identity.EngineWorkflow + "/" + identity.BaseId.ToLowerInvariant();
    }

    private static (string EngineWorkflow, string BaseId) GetChangedPlayerEvidenceIdentity(
        JsonElement report, string reportPath)
    {
        RequireEvidenceFormat(report, "hybridclr.dhe-resource-player-workflow.json",
            "Changed Player workflow");
        string baseId = GetString(report, "selectedBaseId") ?? string.Empty;
        if (!IsHex(baseId, 64, 64))
            throw new DheException("Changed Player workflow has an invalid selected Base ID.");
        string runtimePath = ResolveEvidencePath(GetString(report, "runtimeSource"),
            Path.GetDirectoryName(reportPath)!, "Changed Player runtime manifest");
        JsonElement runtime = ReadJson<JsonElement>(runtimePath);
        RequireEvidenceFormat(runtime, "hybridclr.dhe-runtime-manifest.json",
            "Changed Player runtime manifest");
        string engineWorkflow = GetString(runtime, "engineWorkflow") ?? string.Empty;
        if (!KnownPlayerEngineWorkflows.Contains(engineWorkflow, StringComparer.Ordinal))
            throw new DheException("Changed Player workflow uses an unsupported engine workflow: " +
                engineWorkflow + ".");
        return (engineWorkflow, baseId);
    }

    private static void ValidateMultiBaseChangedEvidence(
        IReadOnlyCollection<(JsonElement Report, string Path)> reports, bool requireEngineMatrix)
    {
        int minimumReportCount = requireEngineMatrix
            ? RequiredPlayerEngineWorkflows.Length
            : 1;
        if (reports.Count < minimumReportCount)
            throw new DheException(requireEngineMatrix
                ? "Changed Player evidence requires all supported engine workflows."
                : "Resource release evidence requires at least one active Base Player.");
        string[] baseIds = reports.Select(item =>
            GetString(item.Report, "selectedBaseId") ?? string.Empty).ToArray();
        if (baseIds.Any(baseId => !IsHex(baseId, 64, 64)) ||
            baseIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != baseIds.Length)
            throw new DheException("Multi-Base changed evidence contains invalid or duplicate Base identities.");
        if (reports.Select(item => GetString(item.Report, "selectedAotMetadataSetId") ?? string.Empty)
                .Any(setId => !IsHex(setId, 64, 64)))
            throw new DheException("Multi-Base changed evidence contains an invalid AOT metadata set identity.");
        var identities = reports.Select(item =>
            (Item: item, Identity: GetChangedPlayerEvidenceIdentity(item.Report, item.Path))).ToArray();
        if (requireEngineMatrix)
        {
            var workflows = identities.Select(item => item.Identity.EngineWorkflow)
                .ToHashSet(StringComparer.Ordinal);
            if (!RequiredPlayerEngineWorkflows.All(workflows.Contains))
                throw new DheException("Changed Player evidence does not cover Unity 2022 and Tuanjie 2022.");
        }

        var first = identities[0].Item;
        // One resource release owns one authenticated manifest and validation
        // document, but it may contain more than one managed payload variant.
        // Compare the document identities globally and validate the selected
        // variant independently for every Base below.
        foreach (string property in new[] { "resourceUpdateManifestSha256",
                     "resourceUpdateValidationSha256", "payloadVariantSetSha256",
                     "releaseLedgerSha256" })
            if (identities.Skip(1).Any(item => !string.Equals(GetString(first.Report, property),
                    GetString(item.Item.Report, property), StringComparison.OrdinalIgnoreCase)))
                throw new DheException("Multi-Base changed evidence does not share one resource release document: " +
                    property + ".");

        string manifestPath = ResolveEvidencePath(GetString(first.Report, "resourceUpdateManifest"),
            Path.GetDirectoryName(first.Path)!, "Multi-Base resource update manifest");
        JsonElement manifest = ReadJson<JsonElement>(manifestPath);
        var supportedRecords = manifest.GetProperty("supportedBases").EnumerateArray().ToArray();
        var supported = supportedRecords
            .Select(item => GetString(item, "baseId") ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (supported.Count != identities.Length ||
            !supported.SetEquals(identities.Select(item => item.Identity.BaseId)))
            throw new DheException(
                "Changed Player evidence must cover every active Base in the shared resource release.");

        foreach (var item in identities)
        {
            string variantId = GetString(item.Item.Report, "selectedPayloadVariantId") ?? "default";
            string selectedCurrentSet = GetString(item.Item.Report,
                "selectedPayloadCurrentAssemblySetSha256") ??
                GetString(item.Item.Report, "currentAssemblySetSha256") ?? string.Empty;
            JsonElement manifestVariant = SelectPayloadVariant(manifest, variantId,
                "Multi-Base resource update manifest");
            string manifestVariantCurrentSet = GetString(manifestVariant,
                "currentAssemblySetSha256") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(manifestVariantCurrentSet) &&
                !string.Equals(manifestVariantCurrentSet, selectedCurrentSet,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("Multi-Base changed evidence does not match its selected payload variant.");

            JsonElement[] baseMatches = supportedRecords.Where(candidate =>
                string.Equals(GetString(candidate, "baseId"), item.Identity.BaseId,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (baseMatches.Length != 1)
                throw new DheException("The shared current payload does not uniquely declare Base identity: " +
                    item.Identity.BaseId + ".");
            JsonElement baseRecord = baseMatches[0];
            string baseVariantId = GetString(baseRecord, "payloadVariantId") ?? "default";
            string baseCurrentSet = GetString(baseRecord, "currentAssemblySetSha256") ?? string.Empty;
            if (!string.Equals(baseVariantId, variantId, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(baseCurrentSet) &&
                 !string.Equals(baseCurrentSet, selectedCurrentSet,
                     StringComparison.OrdinalIgnoreCase)))
                throw new DheException("Multi-Base changed evidence selects a variant not bound to its Base.");
            string baseTarget = GetString(baseRecord, "target") ?? string.Empty;
            string reportTarget = GetString(item.Item.Report, "target") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(baseTarget) && !string.IsNullOrWhiteSpace(reportTarget) &&
                !string.Equals(baseTarget, reportTarget, StringComparison.OrdinalIgnoreCase))
                throw new DheException("Multi-Base changed evidence target does not match its Base record.");
        }

    }

    private static MultiBaseResourceReleaseProof ReadMultiBaseResourceReleaseProof(
        IReadOnlyCollection<(JsonElement Report, string Path)> reports, bool requireEngineMatrix)
    {
        ValidateMultiBaseChangedEvidence(reports, requireEngineMatrix);
        var first = reports.First();
        string manifestPath = ResolveEvidencePath(GetString(first.Report,
            "resourceUpdateManifest"), Path.GetDirectoryName(first.Path)!,
            "Multi-Base resource update manifest");
        string manifestSha256 = Sha256File(manifestPath);
        if (!string.Equals(manifestSha256,
            GetString(first.Report, "resourceUpdateManifestSha256"),
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Multi-Base resource manifest hash is invalid.");
        string ledgerPath = ResolveEvidencePath(GetString(first.Report, "releaseLedger"),
            Path.GetDirectoryName(first.Path)!, "Multi-Base resource release ledger");
        ReleaseLedgerDocument ledger = ReadReleaseLedger(ledgerPath,
            GetString(first.Report, "releaseLedgerSha256"));
        if (!Path.GetFullPath(ledger.ResourceUpdateManifestPath).Equals(
                Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase) ||
            ledger.ActiveBaseCount != reports.Count)
            throw new DheException(
                "Multi-Base Player evidence does not match its release ledger Base set.");
        return new MultiBaseResourceReleaseProof(manifestSha256, ledger.Sha256,
            ledger.ParentLedgerSha256, ledger.ChannelId,
            ledger.Revision, ledger.BaseRegistrySha256,
            ledger.ActiveBaseCount, ledger.CurrentAssemblySetSha256,
            ledger.PayloadVariantSetSha256);
    }

    private static void ValidateChangedPlayerReleaseHead(
        MultiBaseResourceReleaseProof proof, string expectedUpdateRoot)
    {
        string root = RequireDirectory(expectedUpdateRoot,
            "Expected consecutive resource release");
        string manifestPath = RequireFile(Path.Combine(root, "dhe-resource-update.json"),
            "Expected consecutive resource manifest");
        JsonElement manifest = ReadJson<JsonElement>(manifestPath);
        ReleaseLedgerDocument? ledger = ValidateReleaseLedgerForStaging(root, manifest);
        _ = ValidateResourceUpdateCompatibility(root, manifest);
        if (ledger == null ||
            !string.Equals(Sha256File(manifestPath), proof.ResourceUpdateManifestSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(ledger.Sha256, proof.ReleaseLedgerSha256,
                StringComparison.OrdinalIgnoreCase) ||
            proof.ReleaseRevision < 2 ||
            !IsHex(proof.ParentReleaseLedgerSha256, 64, 64) ||
            !string.Equals(ledger.ParentLedgerSha256,
                proof.ParentReleaseLedgerSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(ledger.ChannelId, proof.ReleaseChannelId,
                StringComparison.Ordinal) || ledger.Revision != proof.ReleaseRevision ||
            !string.Equals(ledger.BaseRegistrySha256, proof.BaseRegistrySha256,
                StringComparison.OrdinalIgnoreCase) ||
            ledger.ActiveBaseCount != proof.ActiveBaseCount ||
            !string.Equals(ledger.CurrentAssemblySetSha256,
                proof.CurrentAssemblySetSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(ledger.PayloadVariantSetSha256,
                proof.PayloadVariantSetSha256, StringComparison.OrdinalIgnoreCase))
            throw new DheException(
                "Changed Player evidence does not match the consecutive resource release head.");
    }

    private static void ValidateRegressionResourceReleaseBinding(JsonElement regression,
        MultiBaseResourceReleaseProof proof)
    {
        if (!regression.TryGetProperty("validatedResourceRelease", out JsonElement binding) ||
            binding.ValueKind != JsonValueKind.Object ||
            !string.Equals(GetString(binding, "resourceUpdateManifestSha256"),
                proof.ResourceUpdateManifestSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(binding, "releaseLedgerSha256"),
                proof.ReleaseLedgerSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(binding, "parentReleaseLedgerSha256"),
                proof.ParentReleaseLedgerSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(binding, "releaseChannelId"), proof.ReleaseChannelId,
                StringComparison.Ordinal) ||
            GetInt(binding, "releaseRevision") != proof.ReleaseRevision ||
            !string.Equals(GetString(binding, "baseRegistrySha256"),
                proof.BaseRegistrySha256, StringComparison.OrdinalIgnoreCase) ||
            GetInt(binding, "activeBaseCount") != proof.ActiveBaseCount ||
            !string.Equals(GetString(binding, "currentAssemblySetSha256"),
                proof.CurrentAssemblySetSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(binding, "payloadVariantSetSha256"),
                proof.PayloadVariantSetSha256, StringComparison.OrdinalIgnoreCase))
            throw new DheException(
                "Regression evidence does not bind the proven consecutive resource release.");
    }

    private static void ValidateResourcePlayerEvidenceBindings(JsonElement report, string reportPath)
    {
        _ = ValidateResourceBaseArchive(report, reportPath);
        string root = Path.GetDirectoryName(reportPath)!;
        string Bound(string pathProperty, string hashProperty, string description)
        {
            string path = ResolveEvidencePath(GetString(report, pathProperty), root, description);
            if (!Sha256File(path).Equals(GetString(report, hashProperty),
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException(description + " hash does not match resource Player evidence.");
            return path;
        }

        string manifestPath = Bound("resourceUpdateManifest", "resourceUpdateManifestSha256",
            "Resource update manifest");
        string ledgerPath = Bound("releaseLedger", "releaseLedgerSha256",
            "Resource release ledger");
        string validationPath = Bound("resourceUpdateValidation", "resourceUpdateValidationSha256",
            "Resource update validation");
        string stagePath = Bound("resourceStage", "resourceStageSha256", "Resource stage");
        string playerPath = Bound("playerResult", "playerResultSha256", "Resource Player result");
        string baseWorkflowPath = Bound("baseWorkflowReport", "baseWorkflowReportSha256",
            "Base workflow report");
        string buildIdentityPath = Bound("buildIdentity", "buildIdentitySha256",
            "Base build identity");
        _ = Bound("runtimePlan", "runtimePlanSha256", "Resource runtime plan");
        string nativePath = ResolveEvidencePath(GetString(report, "nativeManifest"), root,
            "Base native manifest");
        if (!Sha256File(nativePath).Equals(GetString(report, "nativeManifestSha256"),
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Base native manifest hash does not match resource Player evidence.");

        JsonElement manifest = ReadJson<JsonElement>(manifestPath);
        JsonElement validation = ReadJson<JsonElement>(validationPath);
        JsonElement stage = ReadJson<JsonElement>(stagePath);
        JsonElement player = ReadJson<JsonElement>(playerPath);
        JsonElement baseWorkflow = ReadJson<JsonElement>(baseWorkflowPath);
        ReleaseLedgerDocument ledger = ReadReleaseLedger(ledgerPath,
            GetString(report, "releaseLedgerSha256"));
        string updateRoot = Path.GetDirectoryName(manifestPath)!;
        ReleaseLedgerDocument? liveLedger = ValidateReleaseLedgerForStaging(updateRoot,
            manifest);
        string liveValidationPath = ValidateResourceUpdateCompatibility(updateRoot, manifest);
        RequireEvidenceFormat(manifest, "hybridclr.dhe-resource-update.json", "Resource update manifest");
        RequireEvidenceFormat(validation, "hybridclr.dhe-resource-update-validation.json",
            "Resource update validation");
        RequireEvidenceFormat(stage, "hybridclr.dhe-resource-stage.json", "Resource stage");
        RequireEvidenceFormat(player, "hybridclr.dhe-player-result.json", "Resource Player result");
        RequireEvidenceFormat(baseWorkflow, "hybridclr.dhe-project-player-workflow.json",
            "Base workflow report");
        if (!Path.GetFullPath(ledger.ResourceUpdateManifestPath).Equals(
                Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(ledger.ResourceUpdateValidationPath).Equals(
                Path.GetFullPath(validationPath), StringComparison.OrdinalIgnoreCase) ||
            liveLedger == null ||
            !string.Equals(liveLedger.Sha256, ledger.Sha256,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(liveValidationPath).Equals(Path.GetFullPath(validationPath),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(report, "resourceReleaseMode"), "Release",
                StringComparison.Ordinal) || !GetBool(report, "resourceReleaseReady") ||
            !string.Equals(ledger.ChannelId, GetString(report, "releaseChannelId"),
                StringComparison.Ordinal) || ledger.Revision != GetInt(report, "releaseRevision") ||
            !string.Equals(ledger.ParentLedgerSha256,
                GetString(report, "parentReleaseLedgerSha256"),
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Resource Player evidence release ledger binding is invalid.");
        string? devicePlayerRunValue = GetString(report, "devicePlayerRun");
        string? devicePlayerRunSha256 = GetString(report, "devicePlayerRunSha256");
        if (string.Equals(GetString(report, "target"), "Android", StringComparison.Ordinal))
        {
            string devicePlayerRunPath = ResolveEvidencePath(devicePlayerRunValue,
                root, "Android device Player run");
            if (!string.Equals(Sha256File(devicePlayerRunPath), devicePlayerRunSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException(
                    "Android device Player run hash does not match resource Player evidence.");
            ValidateDevicePlayerRunBindings(devicePlayerRunPath, updateRoot, manifestPath,
                ledger, stagePath, stage, playerPath, player, buildIdentityPath);
        }
        else if (!string.IsNullOrWhiteSpace(devicePlayerRunValue) ||
                 !string.IsNullOrWhiteSpace(devicePlayerRunSha256))
        {
            throw new DheException(
                "Non-Android resource Player evidence contains an Android device run.");
        }
        string stagedLedgerPath = ResolveEvidencePath(GetString(report, "stagedReleaseLedger"),
            root, "Staged resource release ledger");
        string stagedLedgerFromStage = ResolveEvidencePath(GetString(stage,
            "stagedReleaseLedgerPath"), Path.GetDirectoryName(stagePath)!,
            "Stage report release ledger");
        if (!string.Equals(GetString(stage, "mode"), "Release", StringComparison.Ordinal) ||
            !GetBool(stage, "releaseReady") ||
            !string.Equals(GetString(stage, "releaseChannelId"), ledger.ChannelId,
                StringComparison.Ordinal) || GetInt(stage, "releaseRevision") != ledger.Revision ||
            !string.Equals(GetString(stage, "parentReleaseLedgerSha256"),
                ledger.ParentLedgerSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(stage, "releaseLedgerSha256"), ledger.Sha256,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(stagedLedgerPath).Equals(Path.GetFullPath(stagedLedgerFromStage),
                StringComparison.OrdinalIgnoreCase) ||
            !Sha256File(stagedLedgerPath).Equals(ledger.Sha256,
                StringComparison.OrdinalIgnoreCase) ||
            !OptionalJsonPropertiesEqual(validation, manifest, "mode") ||
            !OptionalJsonPropertiesEqual(validation, manifest, "releaseReady") ||
            !OptionalJsonPropertiesEqual(validation, manifest, "releaseChannelId") ||
            !OptionalJsonPropertiesEqual(validation, manifest, "releaseRevision") ||
            !OptionalJsonPropertiesEqual(validation, manifest,
                "parentReleaseLedgerSha256") ||
            !OptionalJsonPropertiesEqual(validation, manifest, "releaseLedger"))
            throw new DheException("Resource Player stage does not match its release ledger.");
        JsonElement[] selectedManifestBases = manifest.GetProperty("supportedBases")
            .EnumerateArray().Where(item => string.Equals(GetString(item, "baseId"),
                GetString(report, "selectedBaseId"), StringComparison.OrdinalIgnoreCase)).ToArray();
        string selectedVariantId = GetString(report, "selectedPayloadVariantId") ?? "default";
        JsonElement selectedManifestVariant = SelectPayloadVariant(manifest, selectedVariantId,
            "Resource update manifest");
        string selectedCurrentSet = GetString(selectedManifestVariant,
            "currentAssemblySetSha256") ?? GetString(manifest, "currentAssemblySetSha256") ?? string.Empty;
        if (selectedManifestBases.Length != 1 ||
            !string.Equals(GetString(selectedManifestBases[0], "aotMetadataSetId"),
                GetString(report, "selectedAotMetadataSetId"), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(selectedManifestBases[0], "payloadVariantId") ?? "default",
                selectedVariantId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(selectedManifestBases[0], "currentAssemblySetSha256"),
                selectedCurrentSet, StringComparison.OrdinalIgnoreCase))
            throw new DheException("Resource Player evidence selects a Base with the wrong AOT metadata set.");
        var modeErrors = new List<string>();
        string[] differentialNames = ReadPlayerAssemblyNameArray(player,
            "plannedDifferentialAssemblies", modeErrors);
        string[] interpreterOnlyNames = ReadPlayerAssemblyNameArray(player,
            "plannedInterpreterOnlyAssemblies", modeErrors);
        string[] loadedInterpreterOnlyNames = ReadPlayerAssemblyNameArray(player,
            "loadedInterpreterOnlyAssemblies", modeErrors);
        ValidatePlayerAssemblyModes(selectedManifestBases[0], selectedManifestVariant,
            differentialNames, interpreterOnlyNames, loadedInterpreterOnlyNames, modeErrors);
        string[] manifestAssemblyNames = selectedManifestVariant.GetProperty("assemblies")
            .EnumerateArray().Select(item => NormalizeName(
                GetString(item, "assemblyName") ?? string.Empty))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] playerPlannedNames = ReadPlayerAssemblyNameArray(player,
            "plannedDheAssemblies", modeErrors);
        string[] playerLoadedNames = ReadPlayerAssemblyNameArray(player,
            "loadedDheAssemblies", modeErrors);
        ValidateResourcePlayerAssemblyScope(report, manifestAssemblyNames,
            playerPlannedNames, playerLoadedNames, differentialNames,
            interpreterOnlyNames, loadedInterpreterOnlyNames, player, modeErrors);
        ValidateResourcePlayerExecution(player, CountResourceChangedMethods(selectedManifestBases[0]),
            interpreterOnlyNames.Length, modeErrors, ReadResourceDispatchAssemblies(updateRoot,
                selectedManifestVariant, selectedManifestBases[0], baseWorkflow, baseWorkflowPath,
                ReadJson<JsonElement>(buildIdentityPath)));
        if (modeErrors.Count != 0)
            throw new DheException(string.Join(" ", modeErrors));
        JsonElement selectedValidationVariant = SelectPayloadVariant(validation, selectedVariantId,
            "Resource update validation");
        string payloadVariantSetHash = GetString(manifest, "payloadVariantSetSha256") ?? string.Empty;
        string baseCleanCheckoutPath = ResolveBaseWorkflowReference(baseWorkflow,
            baseWorkflowPath, "cleanCheckoutGate", "Base workflow clean checkout gate");
        string reportCleanCheckoutPath = ResolveEvidencePath(GetString(report,
            "cleanCheckoutGate"), root, "Resource Player clean checkout gate");
        if (!GetBool(validation, "passed") || !GetBool(stage, "passed") ||
            !GetBool(player, "passed") || !GetBool(baseWorkflow, "passed") ||
            !string.Equals(JsonSerializer.Serialize(player),
                JsonSerializer.Serialize(report.GetProperty("player")), StringComparison.Ordinal) ||
             !string.Equals(GetString(stage, "selectedBaseId"), GetString(report, "selectedBaseId"),
                 StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(GetString(player, "selectedBaseId"), GetString(report, "selectedBaseId"),
                 StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(GetString(stage, "selectedAotMetadataSetId"),
                 GetString(report, "selectedAotMetadataSetId"), StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(GetString(player, "selectedAotMetadataSetId"),
                 GetString(report, "selectedAotMetadataSetId"), StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(GetString(stage, "payloadVariantId") ?? "default",
                selectedVariantId, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(GetString(player, "selectedPayloadVariantId") ?? "default",
                selectedVariantId, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(GetString(stage, "currentAssemblySetSha256"), selectedCurrentSet,
                StringComparison.OrdinalIgnoreCase) ||
             PlayerPayloadSelectionError(player, manifest, validation, selectedVariantId,
                 selectedCurrentSet) is not null ||
             !string.Equals(GetString(selectedValidationVariant, "currentAssemblySetSha256"),
                selectedCurrentSet, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(GetString(report, "currentAssemblySetSha256"), selectedCurrentSet,
                StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(GetString(report, "selectedPayloadVariantId") ?? "default",
                selectedVariantId, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(GetString(report, "selectedPayloadCurrentAssemblySetSha256"),
                selectedCurrentSet, StringComparison.OrdinalIgnoreCase) ||
             (!string.IsNullOrWhiteSpace(payloadVariantSetHash) &&
              !string.Equals(GetString(report, "payloadVariantSetSha256"), payloadVariantSetHash,
                  StringComparison.OrdinalIgnoreCase)) ||
             !OptionalJsonPropertiesEqual(report, manifest, "baseRegistrySha256") ||
             !OptionalJsonPropertiesEqual(report, manifest, "baseRegistryId") ||
             !OptionalJsonPropertiesEqual(report, manifest, "baseRegistryRevision") ||
             !OptionalJsonPropertiesEqual(report, manifest,
                 "baseRegistryLineageValidated") ||
             !OptionalJsonPropertiesEqual(stage, manifest, "baseRegistrySha256") ||
             !OptionalJsonPropertiesEqual(stage, manifest, "baseRegistryId") ||
             !OptionalJsonPropertiesEqual(stage, manifest, "baseRegistryRevision") ||
             !OptionalJsonPropertiesEqual(stage, manifest,
                 "baseRegistryLineageValidated") ||
             !Path.GetFullPath(baseCleanCheckoutPath).Equals(
                 Path.GetFullPath(reportCleanCheckoutPath),
                 StringComparison.OrdinalIgnoreCase))
            throw new DheException("Resource Player evidence live bindings do not agree.");
    }

    private static void ValidateNativeReleaseEvidence(JsonElement report, string reportPath, string sourceRoot,
        string expectedProfile, string expectedWorkflow)
    {
        sourceRoot = RequireDirectory(sourceRoot, "DHE release source root");
        var reportRoot = Path.GetDirectoryName(reportPath)!;
        var runtimeManifestPath = ResolveEvidencePath(GetString(report, "runtimeManifest"), reportRoot,
            "Native evidence runtime manifest");
        var runtimeManifestHash = Sha256File(runtimeManifestPath);
        if (!runtimeManifestHash.Equals(GetString(report, "runtimeManifestSha256"),
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Native evidence runtime manifest hash is invalid.");
        var runtime = ReadJson<JsonElement>(runtimeManifestPath);
        RequireEvidenceFormat(runtime, "hybridclr.dhe-runtime-manifest.json", "Native runtime manifest");
        if (GetString(runtime, "profile") != expectedProfile ||
            GetString(runtime, "engineWorkflow") != expectedWorkflow)
            throw new DheException("Native evidence uses an unexpected runtime profile or engine workflow.");

        var runtimeRoot = RequireDirectory(GetString(report, "runtimeRoot") ?? "", "Native runtime root");
        var manifestRuntimeRoot = RequireDirectory(GetString(runtime, "stagedLibil2cpp") ?? "",
            "Native manifest runtime root");
        if (!Path.GetFullPath(runtimeRoot).Equals(Path.GetFullPath(manifestRuntimeRoot),
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Native evidence runtime root does not match its manifest.");
        var runtimeTree = TreeHashForRelease(runtimeRoot, Array.Empty<string>());
        if (!runtimeTree.Equals(GetString(report, "runtimeTreeSha256"), StringComparison.OrdinalIgnoreCase) ||
            !runtimeTree.Equals(GetString(runtime, "stagedRuntimeSha256"), StringComparison.OrdinalIgnoreCase))
            throw new DheException("Native evidence runtime tree is not bound to its live staged runtime.");

        var headers = runtime.GetProperty("externalHeaders");
        if (GetBool(headers, "surrogate") || GetBool(headers, "explicitlyAllowed"))
            throw new DheException("Native evidence uses surrogate external headers.");
        var externalRoot = RequireDirectory(GetString(headers, "stagedPath") ?? "",
            "Native external headers root");
        var externalTree = TreeHashForRelease(externalRoot, Array.Empty<string>());
        if (!externalTree.Equals(GetString(report, "externalTreeSha256"), StringComparison.OrdinalIgnoreCase) ||
            !externalTree.Equals(GetString(headers, "stagedTreeSha256"), StringComparison.OrdinalIgnoreCase))
            throw new DheException("Native evidence external header tree is not bound to its live staged headers.");

        var currentRuntimeLock = RequireFile(Path.Combine(sourceRoot, "manifests", "dhe-runtime-lock.json"),
            "Current DHE runtime lock");
        if (!Sha256File(currentRuntimeLock).Equals(GetString(runtime, "dheRuntimeLockSha256"),
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Native evidence runtime lock does not match the release source.");
        var repoLock = ReadJson<JsonElement>(RequireFile(Path.Combine(sourceRoot, "manifests", "repo-lock.json"),
            "Current repository lock"));
        var workflows = ReadJson<JsonElement>(RequireFile(Path.Combine(sourceRoot, "manifests",
            "runtime-workflows.json"), "Current runtime workflows"));
        var workflow = workflows.GetProperty("workflows").EnumerateArray().Single(item =>
            GetString(item, "id") == expectedWorkflow);
        var runtimeSources = runtime.GetProperty("source");
        foreach (var repository in new[] { "hybridclr", "il2cpp_plus", "hybridclr_unity" })
        {
            var expected = repository == "il2cpp_plus"
                ? GetString(workflow.GetProperty("il2cppPlus"), "commit")
                : GetString(repoLock.GetProperty("repositories").GetProperty(repository), "commit");
            var actual = runtimeSources.GetProperty(repository);
            if (!string.Equals(expected, GetString(actual, "commit"), StringComparison.OrdinalIgnoreCase) ||
                GetBool(actual, "dirty") || !IsHex(GetString(actual, "treeSha256"), 64, 64))
                throw new DheException("Native evidence source identity is invalid: " + repository);
        }
        var expectedHeaders = GetString(workflow.GetProperty("engine"), "externalHeadersTreeSha256");
        if (!externalTree.Equals(expectedHeaders, StringComparison.OrdinalIgnoreCase))
            throw new DheException("Native evidence headers do not match the locked engine workflow.");
    }

    private static void ValidateEvidenceToolIdentity(JsonElement report, string reportPath,
        string sourceRoot, string sourceHead, string sourceTree,
        IEnumerable<string>? additionalPackageRoots = null)
    {
        string reportRoot = Path.GetDirectoryName(reportPath)!;
        var cleanPath = ResolveEvidencePath(GetString(report, "cleanCheckoutGate"),
            reportRoot, "Demo clean checkout evidence");
        var clean = ReadJson<JsonElement>(cleanPath);
        RequireEvidenceFormat(clean, "hybridclr.dhe-clean-checkout-gate.json", "Demo clean checkout");
        var tool = clean.GetProperty("toolGit");
        if (!GetBool(tool, "clean") || !GetBool(tool, "trackedSourcesComplete"))
            throw new DheException("Demo evidence tool identity is not clean and tracked.");

        string expectedPackageId = GetString(report, "expectedToolchainPackageId") ?? string.Empty;
        string gatePath = ResolveEvidencePath(GetString(report, "toolchainGate"), reportRoot,
            "Demo toolchain gate");
        JsonElement gate = ReadJson<JsonElement>(gatePath);
        RequireEvidenceFormat(gate, "hybridclr.dhe-toolchain-gate.json", "Demo toolchain gate");
        string recordedPackageRoot = GetString(gate, "packageRoot") ?? string.Empty;
        var packageCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(recordedPackageRoot))
            packageCandidates.Add(Path.GetFullPath(Path.IsPathRooted(recordedPackageRoot)
                ? recordedPackageRoot
                : Path.Combine(Path.GetDirectoryName(gatePath)!, recordedPackageRoot)));
        packageCandidates.AddRange((additionalPackageRoots ?? Array.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath));
        string? packageRoot = packageCandidates.Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(candidate => Directory.Exists(candidate) &&
                InspectPackage(candidate, expectedPackageId, true).Passed);
        if (packageRoot == null)
            throw new DheException("Demo toolchain package was not found. Checked roots: " +
                string.Join(", ", packageCandidates) + ".");
        PackageInspection inspection = InspectPackage(packageRoot, expectedPackageId, true);
        if (!GetBool(gate, "passed") || !GetBool(gate, "requireRelease") ||
            !GetBool(gate, "releaseReady") || !inspection.Passed ||
            !string.Equals(GetString(gate, "packageId"), expectedPackageId,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Demo evidence was not produced by its authenticated release toolchain. " +
                "Checked roots: " + string.Join(", ", packageCandidates) + ".");

        JsonElement packageManifest = ReadJson<JsonElement>(inspection.ManifestPath);
        JsonElement packageSource = packageManifest.GetProperty("sourceIdentity");
        if (!IsAuthorizedEvidenceToolSource(sourceRoot, sourceHead, sourceTree,
                GetString(tool, "head"), GetString(tool, "tree"),
                GetString(packageSource, "head"), GetString(packageSource, "tree")))
            throw new DheException("Player clean-checkout identity is not on the authenticated " +
                "authority-to-current tool source chain.");
    }

    private static bool IsAuthorizedEvidenceToolSource(string sourceRoot,
        string currentHead, string currentTree, string? evidenceHead, string? evidenceTree,
        string? authorityHead, string? authorityTree)
    {
        if (!IsHex(currentHead, 40, 64) || !IsHex(currentTree, 40, 64) ||
            !IsHex(evidenceHead, 40, 64) || !IsHex(evidenceTree, 40, 64) ||
            !IsHex(authorityHead, 40, 64) || !IsHex(authorityTree, 40, 64))
            return false;
        if (!GitCommitHasTree(sourceRoot, currentHead, currentTree) ||
            !GitCommitHasTree(sourceRoot, evidenceHead!, evidenceTree!) ||
            !GitCommitHasTree(sourceRoot, authorityHead!, authorityTree!))
            return false;
        return GitCommitIsAncestor(sourceRoot, authorityHead!, evidenceHead!) &&
            GitCommitIsAncestor(sourceRoot, evidenceHead!, currentHead);
    }

    private static bool GitCommitHasTree(string root, string commit, string expectedTree) =>
        string.Equals(GitValue(root, "rev-parse", commit + "^{tree}"), expectedTree,
            StringComparison.OrdinalIgnoreCase);

    private static bool GitCommitIsAncestor(string root, string ancestor, string descendant)
    {
        try
        {
            root = Path.GetFullPath(root);
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(root);
            start.ArgumentList.Add("merge-base");
            start.ArgumentList.Add("--is-ancestor");
            start.ArgumentList.Add(ancestor);
            start.ArgumentList.Add(descendant);
            using Process? process = Process.Start(start);
            if (process == null) return false;
            _ = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void RequireEvidenceFormat(JsonElement report, string expected, string description)
    {
        if (GetInt(report, "schemaVersion") != 1 || GetString(report, "format") != expected)
            throw new DheException(description + " evidence has an invalid format.");
    }

    private static void ValidateNoOpPlayerEvidence(JsonElement player)
    {
        if (GetInt(player, "changedMethodCount") != 0 || GetInt(player, "interpreterEntryCount") != 0 ||
            GetBool(player, "changedProbeChanged") || GetBool(player, "unchangedProbeChanged") ||
            GetString(player, "transactionStatus") != "notApplicable" ||
            !GetBool(player, "dispatchProbeValidated") || !GetBool(player, "noOpAotBehaviorValidated") ||
            !GetBool(player, "multiAssemblyValidated") || !GetBool(player, "capabilityDirectPassed") ||
            !GetBool(player, "capabilityPassed") || !GetBool(player, "secondaryAssemblyDirectValidated"))
            throw new DheException("No-op Demo evidence does not prove unchanged AOT behavior.");
    }

    private static int Validate(Cli cli)
    {
        var input = RequireFile(cli.Require("mvjson"), "MetaVersion JSON");
        var errors = new List<string>();
        JsonElement doc = default;
        try
        {
            doc = ReadJson<JsonElement>(input);
            if (GetInt(doc, "schemaVersion") != MetaVersionSnapshot.SchemaVersion ||
                !doc.TryGetProperty("format", out var format) ||
                format.GetString() != "hybridclr.dhe-metaversion.json")
                errors.Add("Invalid MetaVersion format.");
            if (!doc.TryGetProperty("assemblyName", out var assemblyName) || string.IsNullOrWhiteSpace(assemblyName.GetString()))
                errors.Add("MetaVersion assemblyName is missing.");
        }
        catch (Exception ex) { errors.Add(ex.Message); }

        var assemblyPath = cli.Optional("assembly") ?? cli.Optional("currentassembly");
        MetaVersionSnapshot? expected = null;
        if (!string.IsNullOrWhiteSpace(assemblyPath))
        {
            try
            {
                assemblyPath = RequireFile(assemblyPath, "MetaVersion assembly");
                expected = MetaVersionSnapshot.Create(assemblyPath);
                if (!string.Equals(GetString(doc, "assemblyName"), expected.AssemblyName,
                        StringComparison.Ordinal) ||
                    !string.Equals(GetString(doc, "assemblyMetadataVersion"),
                        expected.AssemblyMetadataVersion, StringComparison.OrdinalIgnoreCase) ||
                    !doc.TryGetProperty("assembly", out JsonElement assembly) ||
                    !string.Equals(GetString(assembly, "sha256"), expected.AssemblySha256,
                        StringComparison.OrdinalIgnoreCase))
                    errors.Add("MetaVersion JSON does not match the assembly.");
            }
            catch (Exception ex) { errors.Add(ex.Message); }
        }

        var binary = cli.Optional("mvbytes") ?? cli.Optional("binaryoutput");
        if (!string.IsNullOrWhiteSpace(binary))
        {
            try
            {
                binary = RequireFile(binary!, "MetaVersion binary");
                if (expected == null)
                    errors.Add("MetaVersion binary validation requires -Assembly.");
                else if (!File.ReadAllBytes(binary).SequenceEqual(expected.ToBinary()))
                    errors.Add("MetaVersion binary does not match the assembly.");
            }
            catch (Exception ex) { errors.Add(ex.Message); }
        }

        var output = cli.Optional("output");
        if (!string.IsNullOrWhiteSpace(output))
            WriteJson(SafeReportPath(output, new[] { input, assemblyPath ?? "", binary ?? "" }),
                new { schemaVersion = 1, format = "hybridclr.dhe-artifact-validation.json",
                    generatedAtUtc = DateTimeOffset.UtcNow, passed = errors.Count == 0, errors,
                    warnings = Array.Empty<string>(), metaVersionJson = input,
                    metaVersionBytes = binary, assembly = assemblyPath });
        if (errors.Count > 0) { Console.Error.WriteLine(string.Join(Environment.NewLine, errors)); return 1; }
        Console.WriteLine("DHE artifact validation passed: " + input);
        return 0;
    }

    private static int Archive(Cli cli)
    {
        var input = RequireDirectory(cli.Require("inputroot"), "Workflow output");
        var archive = SafeOutputRoot(cli.Require("archiveroot"), new[] { input });
        EnsureOutputOutsideRoot(archive, input);
        if (cli.Has("requirecompletecoverage"))
        {
            var sourceReleaseGate = Path.Combine(input, "release-gate.json");
            var sourceArtifactValidation = Path.Combine(input, "release-gate.artifact-validation.json");
            var releaseReady = File.Exists(sourceReleaseGate) && File.Exists(sourceArtifactValidation) &&
                GetBool(ReadJson<JsonElement>(sourceReleaseGate), "passed") && GetBool(ReadJson<JsonElement>(sourceArtifactValidation), "passed");
            if (!releaseReady)
            {
                var error = "Release archive requires passing release and artifact validation reports.";
                var failedOutput = cli.Optional("output");
                if (!string.IsNullOrWhiteSpace(failedOutput)) WriteJson(SafeReportPath(failedOutput, new[] { input }), new
                {
                    schemaVersion = 1, format = "hybridclr.dhe-archive-gate.json", generatedAtUtc = DateTimeOffset.UtcNow,
                    passed = false, inputRoot = input, archiveRoot = archive,
                    archiveManifest = Path.Combine(archive, "dhe-archive-manifest.json"), archiveFileCount = 0,
                    copiedValidationPassed = false, requireCompleteCoverage = true, errors = new[] { error }
                });
                Console.Error.WriteLine(error); return 1;
            }
        }
        PrepareArchiveDestination(archive, cli.Has("forceoutput"));
        Directory.CreateDirectory(archive);
        var archiveMappings = new List<ArchivePathMapping>();
        foreach (var source in Directory.GetFiles(input, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(input, source);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0].Equals("player", StringComparison.OrdinalIgnoreCase)) continue;
            var destination = Path.Combine(archive, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination, true);
        }
        var workflow = Path.Combine(archive, "player-workflow-report.json");
        var workflowDocument = ReadJson<JsonElement>(RequireFile(workflow, "Archive workflow report"));
        var archivedIdentityPath = Path.Combine(archive, "build-identity.json");
        var archivedIdentity = ReadJson<JsonElement>(RequireFile(archivedIdentityPath, "Archive build identity"));
        var archivedGeneratedCpp = new List<string>();
        if (archivedIdentity.TryGetProperty("generatedCppPaths", out var generatedPaths) && generatedPaths.ValueKind == JsonValueKind.Array)
        {
            var generatedRoot = GetString(archivedIdentity, "generatedCppRoot") ?? "";
            if (!string.IsNullOrWhiteSpace(generatedRoot))
                archiveMappings.Add(new ArchivePathMapping(Path.GetFullPath(generatedRoot), Path.Combine(archive, "generated-cpp")));
            foreach (var item in generatedPaths.EnumerateArray())
            {
                var value = item.GetString() ?? "";
                var source = Path.IsPathRooted(value) ? value : Path.Combine(generatedRoot, value);
                if (!File.Exists(source)) throw new DheException("Generated C++ evidence was not found: " + source);
                var relative = Path.GetRelativePath(generatedRoot, source);
                if (!SafeRelative(relative)) throw new DheException("Generated C++ evidence path is unsafe: " + source);
                var destinationRelative = Path.Combine("generated-cpp", relative).Replace(Path.DirectorySeparatorChar, '/');
                var destination = Path.Combine(archive, destinationRelative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination, true); archivedGeneratedCpp.Add(destinationRelative);
            }
        }
        var runtimeSource = GetString(workflowDocument, "runtimeSource");
        var archivedRuntimeManifest = Path.Combine(archive, "provenance", "runtime-manifest.json");
        if (!string.IsNullOrWhiteSpace(runtimeSource) && File.Exists(runtimeSource))
        {
            CopyArchiveExternalFile(runtimeSource, archivedRuntimeManifest, archiveMappings);
            var runtimeDocument = ReadJson<JsonElement>(runtimeSource);
            var runtimeLock = GetString(runtimeDocument, "dheRuntimeLock");
            if (!string.IsNullOrWhiteSpace(runtimeLock) && File.Exists(runtimeLock))
                CopyArchiveExternalFile(runtimeLock, Path.Combine(archive, "provenance", "dhe-runtime-lock.json"), archiveMappings);
        }
        var sourcePreflightSource = Path.Combine(input, "source-preflight", "source-preflight-report.json");
        if (File.Exists(sourcePreflightSource))
        {
            var sourcePreflightDocument = ReadJson<JsonElement>(sourcePreflightSource);
            var packageLock = GetString(sourcePreflightDocument, "packageLockPath");
            if (!string.IsNullOrWhiteSpace(packageLock) && File.Exists(packageLock))
                CopyArchiveExternalFile(packageLock, Path.Combine(archive, "provenance", "dhe-package-lock.json"), archiveMappings);
            var baselineManifest = GetString(sourcePreflightDocument, "baselineManifestPath");
            if (!string.IsNullOrWhiteSpace(baselineManifest) && File.Exists(baselineManifest))
                CopyArchiveExternalFile(baselineManifest, Path.Combine(archive, "provenance", "dhe-baseline-manifest.json"), archiveMappings);
            var settingsSource = GetString(sourcePreflightDocument, "settingsFile");
            if (!string.IsNullOrWhiteSpace(settingsSource) && File.Exists(settingsSource))
                CopyArchiveExternalFile(settingsSource, Path.Combine(archive, "provenance", "project", "ProjectSettings", "HybridCLRSettings.asset"), archiveMappings);
        }
        var cleanSource = Path.Combine(input, "clean-checkout", "clean-checkout-gate-report.json");
        if (File.Exists(cleanSource))
        {
            var cleanDocument = ReadJson<JsonElement>(cleanSource);
            var boundarySource = GetString(cleanDocument, "sourceBoundaryPath");
            if (!string.IsNullOrWhiteSpace(boundarySource) && File.Exists(boundarySource))
                CopyArchiveExternalFile(boundarySource, Path.Combine(archive, "provenance", "project", "dhe-source-boundary.json"), archiveMappings);
        }
        CopyArchiveResourceEvidence(archive, input, archiveMappings);
        RewriteArchiveJsonDocuments(archive, input, archiveMappings);
        BindArchivedNativeIdentity(archive, Path.Combine(input, "native", "dhe-native-manifest.json"));

        var manifestPath = Path.Combine(archive, "dhe-archive-manifest.json");
        var projectPlan = Path.Combine(archive, "project-preflight", "dhe-project-plan.json");
        var projectPlanValidation = Path.Combine(archive, "project-preflight", "project-plan-validation.json");
        var identity = Path.Combine(archive, "build-identity.json");
        var native = Path.Combine(archive, "native", "dhe-native-manifest.json");
        var runtimePlan = Path.Combine(archive, "runtime-plan", "dhe-runtime-plan.json");
        var releaseGate = Path.Combine(archive, "release-gate.json");
        var artifactValidation = Path.Combine(archive, "release-gate.artifact-validation.json");
        var cleanCheckout = Path.Combine(archive, "clean-checkout", "clean-checkout-gate-report.json");
        foreach (var required in new[] { workflow, projectPlan, projectPlanValidation, identity, native, runtimePlan, cleanCheckout }) RequireFile(required, "Archive evidence");
        if (cli.Has("requirecompletecoverage"))
        {
            RequireFile(releaseGate, "Release gate evidence"); RequireFile(artifactValidation, "Artifact validation evidence");
            if (!GetBool(ReadJson<JsonElement>(releaseGate), "passed") || !GetBool(ReadJson<JsonElement>(artifactValidation), "passed"))
                throw new DheException("Release archive requires passing release and artifact validation reports.");
            var target = GetString(workflowDocument, "target") ?? throw new DheException("Archive workflow target is missing.");
            if (ReleaseGate(new Cli("release-gate", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["workflowreport"] = workflow, ["projectplan"] = projectPlan, ["output"] = releaseGate,
                ["target"] = target, ["requirecompletecoverage"] = "true"
            })) != 0) throw new DheException("Archived evidence failed independent release revalidation.");
            RewriteArchiveJsonDocuments(archive, input, archiveMappings);
        }
        var identityDocument = ReadJson<JsonElement>(identity);
        var planDocument = ReadJson<JsonElement>(projectPlan);
        var cleanCheckoutDocument = ReadJson<JsonElement>(cleanCheckout);
        var assemblyRecords = planDocument.GetProperty("assemblies").EnumerateArray().Select(record => new
        {
            assemblyName = GetString(record, "assemblyName"), status = GetString(record, "status"),
            baseline = ArchiveDocumentRelative(archive, Path.GetDirectoryName(projectPlan)!, GetString(record, "baseline")), current = ArchiveDocumentRelative(archive, Path.GetDirectoryName(projectPlan)!, GetString(record, "current")),
            baseMetaVersionJson = ArchiveDocumentRelative(archive, Path.GetDirectoryName(projectPlan)!, GetString(record, "baseMetaVersionJson")),
            baseMetaVersionBytes = ArchiveDocumentRelative(archive, Path.GetDirectoryName(projectPlan)!, GetString(record, "baseMetaVersionBytes")),
            currentMetaVersionJson = ArchiveDocumentRelative(archive, Path.GetDirectoryName(projectPlan)!, GetString(record, "currentMetaVersionJson")),
            currentMetaVersionBytes = ArchiveDocumentRelative(archive, Path.GetDirectoryName(projectPlan)!, GetString(record, "currentMetaVersionBytes")),
            changedMethodCount = GetInt(record, "changedMethodCount")
        }).ToArray();
        WriteJson(manifestPath, new
        {
            schemaVersion = 1, format = "hybridclr.dhe-archive-manifest.json", generatedAtUtc = DateTimeOffset.UtcNow,
            sourceWorkflowRoot = (string?)null, pathSemantics = "archive-relative-v1",
            sourceIdentities = new { projectGit = ArchiveSourceIdentity(cleanCheckoutDocument.GetProperty("projectGit")), toolGit = ArchiveSourceIdentity(cleanCheckoutDocument.GetProperty("toolGit")) },
            workflowReport = "player-workflow-report.json", artifactValidation = "release-gate.artifact-validation.json",
            buildIdentity = "build-identity.json", nativeManifest = "native/dhe-native-manifest.json",
            immutableNativeManifest = "provenance/native-manifest.original.bin",
            immutableNativeManifestSha256 = Sha256File(Path.Combine(archive, "provenance", "native-manifest.original.bin")),
            runtimeManifest = File.Exists(archivedRuntimeManifest) ? "provenance/runtime-manifest.json" : "not-archived",
            runtimePlan = "runtime-plan/dhe-runtime-plan.json", projectPlan = "project-preflight/dhe-project-plan.json",
            projectPlanValidation = "project-preflight/project-plan-validation.json",
            generatedCppRoot = "generated-cpp", generatedCppPaths = archivedGeneratedCpp,
            assemblies = assemblyRecords,
            offlineReleaseRevalidated = cli.Has("requirecompletecoverage")
        });
        var files = Directory.GetFiles(archive, "*", SearchOption.AllDirectories)
            .Where(path => !path.Equals(manifestPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetRelativePath(archive, path).Replace(Path.DirectorySeparatorChar, '/'), StringComparer.Ordinal)
            .Select(path => new { path = Path.GetRelativePath(archive, path).Replace(Path.DirectorySeparatorChar, '/'), size = new FileInfo(path).Length, sha256 = Sha256File(path) }).ToArray();
        var manifestDocument = ReadJson<JsonElement>(manifestPath);
        var manifestNode = System.Text.Json.Nodes.JsonNode.Parse(manifestDocument.GetRawText())!.AsObject();
        manifestNode["files"] = System.Text.Json.JsonSerializer.SerializeToNode(files, Json);
        manifestNode["fileCount"] = files.Length;
        manifestNode["fileSetSha256"] = Sha256Text(string.Join("\n", files.Select(file => file.path + "|" + file.size + "|" + file.sha256)));
        File.WriteAllText(manifestPath, manifestNode.ToJsonString(Json), new UTF8Encoding(false));
        var copiedValid = files.All(file => File.Exists(Path.Combine(archive, file.path.Replace('/', Path.DirectorySeparatorChar))) && Sha256File(Path.Combine(archive, file.path.Replace('/', Path.DirectorySeparatorChar))).Equals(file.sha256, StringComparison.OrdinalIgnoreCase));
        var output = cli.Optional("output");
        if (!string.IsNullOrWhiteSpace(output)) WriteJson(SafeReportPath(output, new[] { input }), new
        {
            schemaVersion = 1, format = "hybridclr.dhe-archive-gate.json", generatedAtUtc = DateTimeOffset.UtcNow,
            passed = copiedValid, inputRoot = input, archiveRoot = archive, archiveManifest = manifestPath,
            archiveFileCount = files.Length + 1, copiedValidationPassed = copiedValid,
            requireCompleteCoverage = cli.Has("requirecompletecoverage"), errors = copiedValid ? Array.Empty<string>() : new[] { "Archived file hash validation failed." }
        });
        Console.WriteLine("DHE archive: " + archive); return copiedValid ? 0 : 1;
    }

    private static string? ArchiveRelative(string inputRoot, string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return null;
        var full = Path.GetFullPath(sourcePath);
        var relative = Path.GetRelativePath(inputRoot, full);
        return relative.StartsWith("..", StringComparison.Ordinal) ? null : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string? ArchiveDocumentRelative(string archiveRoot, string documentRoot, string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return null;
        var full = Path.GetFullPath(Path.IsPathRooted(sourcePath) ? sourcePath : Path.Combine(documentRoot, sourcePath));
        var relative = Path.GetRelativePath(archiveRoot, full).Replace(Path.DirectorySeparatorChar, '/');
        return SafeRelative(relative) ? relative : null;
    }

    private static object ArchiveSourceIdentity(JsonElement identity) => new
    {
        vcs = GetString(identity, "vcs"), head = GetString(identity, "head"), tree = GetString(identity, "tree"),
        revision = GetString(identity, "revision"), revisionSpec = GetString(identity, "revisionSpec"),
        repository = GetString(identity, "repository"), sourceBoundarySha256 = GetString(identity, "sourceBoundarySha256") ?? new string('0', 64)
    };

    private static int Doctor(Cli cli)
    {
        var root = RequireDirectory(cli.Root, "DHE tool root");
        var expected = cli.Optional("expectedpackageid");
        var requireRelease = cli.Has("requirerelease");
        var inspection = InspectPackage(root, expected, requireRelease);
        var errors = inspection.Errors.ToList();

        var projectPath = cli.Optional("projectpath");
        var projectTested = !string.IsNullOrWhiteSpace(projectPath);
        bool? projectReady = null;
        if (projectTested)
        {
            projectReady = Directory.Exists(projectPath) && File.Exists(Path.Combine(projectPath!, "ProjectSettings", "HybridCLRSettings.asset"));
            if (!projectReady.Value) errors.Add("ProjectPath does not contain ProjectSettings/HybridCLRSettings.asset.");
        }
        var output = cli.Optional("output");
        var report = new { schemaVersion = 1, format = "hybridclr.dhe-toolchain-doctor.json", generatedAtUtc = DateTimeOffset.UtcNow, passed = errors.Count == 0, requireRelease, toolchainVersion = inspection.ToolchainVersion, contractVersion = inspection.ContractVersion, packageId = inspection.PackageId, expectedPackageId = expected, packageGatePassed = inspection.Passed, dotnetVersion = Environment.Version.ToString(), dotnetAvailable = true, gitAvailable = !string.IsNullOrWhiteSpace(GitValue(root, "--version")), projectTested, projectReady, projectPath, projectGitRoot = projectTested ? GitValue(projectPath!, "rev-parse", "--show-toplevel") : null, dnlibPath = File.Exists(Path.Combine(root, "tool", "dnlib.dll")) ? Path.Combine(root, "tool", "dnlib.dll") : null, errors, warnings = Array.Empty<string>() };
        if (!string.IsNullOrWhiteSpace(output)) WriteJson(SafeReportPath(output, new[] { root }), report);
        Console.WriteLine("DHE doctor: " + (report.passed ? "passed" : "failed"));
        return report.passed ? 0 : 1;
    }

    private static int VerifyPackage(Cli cli)
    {
        var root = RequireDirectory(cli.Optional("packageroot") ?? cli.Root, "DHE package root");
        var expectedPackageId = cli.Optional("expectedpackageid");
        var requireRelease = cli.Has("requirerelease");
        var inspection = InspectPackage(root, expectedPackageId, requireRelease);
        var output = cli.Optional("output");
        if (!string.IsNullOrWhiteSpace(output))
        {
            WriteJson(SafeReportPath(output, new[] { root }), new { schemaVersion = 1, format = "hybridclr.dhe-toolchain-gate.json", generatedAtUtc = DateTimeOffset.UtcNow, passed = inspection.Passed, packageRoot = root, manifest = inspection.ManifestPath, toolchainVersion = inspection.ToolchainVersion, contractVersion = inspection.ContractVersion, packageId = inspection.PackageId, computedPackageId = inspection.ComputedPackageId, packageIdAlgorithm = PackageIdAlgorithm, expectedPackageId, packageIdValid = inspection.PackageIdValid, packageTreeSafe = inspection.PackageTreeSafe, releaseReady = inspection.ReleaseReady, releaseIdentityValid = inspection.ReleaseIdentityValid, requireRelease, expectedFileCount = inspection.ExpectedFileCount, actualFileCount = inspection.ActualFileCount, hashesValid = inspection.HashesValid, scriptsValid = inspection.ProhibitedFilesValid, jsonValid = inspection.JsonValid, schemaValid = inspection.SchemaValid, layoutValid = inspection.LayoutValid, boundaryValid = inspection.BoundaryValid, errors = inspection.Errors, warnings = Array.Empty<string>() });
        }
        if (!inspection.Passed) { Console.Error.WriteLine(string.Join(Environment.NewLine, inspection.Errors)); return 1; }
        Console.WriteLine("DHE package verification passed: " + root); return 0;
    }

    private static PackageInspection InspectPackage(string root, string? expectedPackageId, bool requireRelease)
    {
        var result = new PackageInspection { ManifestPath = Path.Combine(root, "dhe-toolchain-manifest.json") };
        if (!File.Exists(result.ManifestPath))
        {
            result.Errors.Add("DHE toolchain manifest is missing.");
            return result;
        }

        JsonElement manifest;
        try
        {
            manifest = ReadJson<JsonElement>(result.ManifestPath);
            result.JsonValid = manifest.ValueKind == JsonValueKind.Object;
        }
        catch (Exception ex)
        {
            result.Errors.Add("DHE toolchain manifest is invalid: " + ex.Message);
            return result;
        }
        if (!result.JsonValid)
        {
            result.Errors.Add("DHE toolchain manifest must be a JSON object.");
            return result;
        }

        result.ToolchainVersion = GetString(manifest, "toolchainVersion");
        result.ContractVersion = GetInt(manifest, "contractVersion");
        result.PackageId = GetString(manifest, "packageId");
        var mode = GetString(manifest, "mode") ?? "";
        result.ReleaseReady = GetBool(manifest, "releaseReady");
        result.SchemaValid = GetInt(manifest, "schemaVersion") == 1 &&
            GetString(manifest, "format") == "hybridclr.dhe-toolchain-manifest.json" &&
            GetString(manifest, "pathSemantics") == "package-relative-v1" &&
            GetString(manifest, "entryPoint") is "tool/HybridCLR.DheTool.csproj" or "HybridCLR.DheTool.dll" &&
            GetString(manifest, "packageIdAlgorithm") == PackageIdAlgorithm &&
            !string.IsNullOrWhiteSpace(result.ToolchainVersion) && result.ContractVersion == 1;
        if (!result.SchemaValid) result.Errors.Add("DHE toolchain manifest contract is invalid.");

        var entries = new List<PackageFileEntry>();
        var declared = new HashSet<string>(StringComparer.Ordinal);
        result.PackageTreeSafe = true;
        result.HashesValid = true;
        if (!manifest.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            result.Errors.Add("DHE toolchain manifest files are missing.");
            result.PackageTreeSafe = false;
            result.HashesValid = false;
        }
        else
        {
            foreach (var item in files.EnumerateArray())
            {
                var relative = GetString(item, "path") ?? "";
                var size = item.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var sizeValue) ? sizeValue : -1;
                var hash = GetString(item, "sha256") ?? "";
                if (!IsPortableRelativePath(relative) || relative.Contains('\\') || !declared.Add(relative))
                {
                    result.Errors.Add("Package manifest contains an unsafe or duplicate path: " + relative);
                    result.PackageTreeSafe = false;
                    continue;
                }
                if (size < 0 || !IsHex(hash, 64, 64))
                {
                    result.Errors.Add("Package manifest contains invalid file metadata: " + relative);
                    result.HashesValid = false;
                    continue;
                }
                entries.Add(new PackageFileEntry(relative, size, hash.ToLowerInvariant()));
                var full = ResolveContainedPath(root, relative, "Package file");
                if (!File.Exists(full))
                {
                    result.Errors.Add("Missing package file: " + relative);
                    result.HashesValid = false;
                    continue;
                }
                var info = new FileInfo(full);
                if (info.Length != size || !Sha256File(full).Equals(hash, StringComparison.OrdinalIgnoreCase))
                {
                    result.Errors.Add("Package size or hash mismatch: " + relative);
                    result.HashesValid = false;
                }
            }
        }

        result.ExpectedFileCount = manifest.TryGetProperty("fileCount", out var count) && count.TryGetInt32(out var countValue) ? countValue : -1;
        var actualFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => path != "dhe-toolchain-manifest.json" && !IsPackageGeneratedPath(path))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        result.ActualFileCount = actualFiles.Length;
        if (result.ExpectedFileCount != entries.Count || !declared.OrderBy(path => path, StringComparer.Ordinal)
                .SequenceEqual(actualFiles, StringComparer.Ordinal))
        {
            result.Errors.Add("Package actual file set does not match the authenticated manifest.");
            result.PackageTreeSafe = false;
        }
        if (Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Any(path => (File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) != 0))
        {
            result.Errors.Add("Package contains a reparse point.");
            result.PackageTreeSafe = false;
        }
        result.ProhibitedFilesValid = !actualFiles.Any(path => path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase));
        if (!result.ProhibitedFilesValid) result.Errors.Add("Package contains a prohibited host file.");

        result.LayoutValid = ValidateInstalledLayout(root, manifest, result.Errors);
        result.BoundaryValid = ValidateInstalledBoundary(root, result.Errors);
        var source = manifest.TryGetProperty("sourceIdentity", out var sourceValue) && sourceValue.ValueKind == JsonValueKind.Object
            ? sourceValue : default;
        var sourceHead = GetString(source, "head") ?? "";
        var sourceTree = GetString(source, "tree") ?? "";
        var sourceClean = GetBool(source, "clean");
        var sourceTracked = GetBool(source, "tracked");
        var commands = manifest.TryGetProperty("commands", out var commandsValue) && commandsValue.ValueKind == JsonValueKind.Array
            ? commandsValue.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString() ?? "").ToArray()
            : Array.Empty<string>();
        var layoutHash = GetString(manifest, "layoutSha256") ?? "";
        result.ComputedPackageId = CalculatePackageId(result.ToolchainVersion ?? "", result.ContractVersion ?? 0,
            mode, result.ReleaseReady, sourceHead, sourceTree, sourceClean, sourceTracked, layoutHash, commands, entries);
        result.PackageIdValid = IsHex(result.PackageId, 64, 64) &&
            string.Equals(result.PackageId, result.ComputedPackageId, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(expectedPackageId) || string.Equals(result.PackageId, expectedPackageId, StringComparison.OrdinalIgnoreCase));
        if (!result.PackageIdValid) result.Errors.Add("Package ID does not match its canonical contents or ExpectedPackageId.");

        result.ReleaseIdentityValid = mode is "Release" or "Exploratory" &&
            (mode == "Release" ? result.ReleaseReady && sourceClean && sourceTracked && IsHex(sourceHead, 40, 64) && IsHex(sourceTree, 40, 64) : !result.ReleaseReady);
        if (!result.ReleaseIdentityValid) result.Errors.Add("Package release/source identity is invalid.");
        if (requireRelease && (mode != "Release" || !result.ReleaseReady)) result.Errors.Add("A release-ready package is required.");
        result.Passed = result.Errors.Count == 0;
        return result;
    }

    private static int Publish(Cli cli)
    {
        var root = RequireDirectory(cli.Optional("labroot") ?? cli.Root, "DHE source root");
        var layoutPath = RequireFile(cli.Optional("layoutpath") ?? Path.Combine(root, "manifests", "dhe-toolchain-layout.json"), "DHE layout");
        var layout = ReadJson<JsonElement>(layoutPath); var output = Path.GetFullPath(cli.Require("outputroot"));
        ToolchainSourceCoverage.Validate(root, layout);
        EnsureOutputNotAncestor(output, root);
        if (Directory.Exists(output)) { if (!cli.Has("forceoutput")) throw new DheException("OutputRoot is not empty: " + output); Directory.Delete(output, true); }
        Directory.CreateDirectory(output);
        foreach (var entry in layout.GetProperty("exactPaths").EnumerateArray()) CopyRelative(root, output, entry.GetString()!);
        foreach (var prefix in layout.GetProperty("prefixes").EnumerateArray())
        {
            var relativePrefix = prefix.GetString()!.TrimEnd('/', '\\'); var source = Path.Combine(root, relativePrefix.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(source))
            {
                string destination = Path.Combine(output, relativePrefix);
                if (relativePrefix.Equals("tool", StringComparison.OrdinalIgnoreCase))
                    CopyDirectoryFiltered(source, destination, path =>
                    {
                        string relative = Path.GetRelativePath(source, path).Replace('\\', '/');
                        return !relative.Split('/').Any(segment =>
                            segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                            segment.Equals("bin", StringComparison.OrdinalIgnoreCase));
                    });
                else
                    CopyDirectory(source, destination);
            }
        }
        File.WriteAllText(Path.Combine(output, ".gitattributes"), "/** text eol=lf\n/tool/dnlib.dll binary\n/patches/dhe-lite/*.patch binary\n", new UTF8Encoding(false));
        var boundary = new { schemaVersion = 1, format = "hybridclr.dhe-source-boundary.json", pathBase = "manifest-directory-v1", exactPaths = new[] { ".gitattributes", "dhe-source-boundary.json", "dhe-toolchain-manifest.json" }, prefixes = new[] { "docs/", "manifests/", "patches/dhe-lite/", "schemas/", "templates/", "tool/" }, generatedPrefixes = new[] { "artifacts/", "staging/", "reports/" } };
        WriteJson(Path.Combine(output, "dhe-source-boundary.json"), boundary);
        var files = Directory.GetFiles(output, "*", SearchOption.AllDirectories)
            .Where(path => !path.Equals(Path.Combine(output, "dhe-toolchain-manifest.json"), StringComparison.OrdinalIgnoreCase))
            .Select(path => new PackageFileEntry(Path.GetRelativePath(output, path).Replace(Path.DirectorySeparatorChar, '/'),
                new FileInfo(path).Length, Sha256File(path)))
            .OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        var sourceHead = GitValue(root, "rev-parse", "HEAD"); var sourceTree = GitValue(root, "rev-parse", "HEAD^{tree}"); var tracked = !string.IsNullOrWhiteSpace(sourceHead); var clean = tracked && string.IsNullOrWhiteSpace(GitValue(root, "status", "--porcelain"));
        ToolPublicationPolicy publication = ResolveToolPublicationPolicy(cli, root, sourceHead, sourceTree, clean, tracked);
        string mode = publication.Mode;
        bool releaseReady = publication.ReleaseReady;
        var layoutHash = Sha256File(layoutPath);
        var commands = layout.GetProperty("commands").EnumerateArray().Select(value => value.GetString() ?? "").ToArray();
        var packageId = CalculatePackageId(GetString(layout, "toolchainVersion") ?? "", GetInt(layout, "contractVersion"),
            mode, releaseReady, sourceHead, sourceTree, clean, tracked, layoutHash, commands, files);
        var manifest = new { schemaVersion = 1, format = "hybridclr.dhe-toolchain-manifest.json", generatedAtUtc = DateTimeOffset.UtcNow, toolchainVersion = GetString(layout, "toolchainVersion"), contractVersion = GetInt(layout, "contractVersion"), mode, releaseReady, pathSemantics = "package-relative-v1", packageIdAlgorithm = PackageIdAlgorithm, packageId, entryPoint = "tool/HybridCLR.DheTool.csproj", commands, layoutSha256 = layoutHash, sourceIdentity = new { head = string.IsNullOrWhiteSpace(sourceHead) ? null : sourceHead, tree = string.IsNullOrWhiteSpace(sourceTree) ? null : sourceTree, clean, tracked }, fileCount = files.Length, files };
        WriteJson(Path.Combine(output, "dhe-toolchain-manifest.json"), manifest); Console.WriteLine("DHE package: " + output); return 0;
    }

    private static int Install(Cli cli)
    {
        var source = RequireDirectory(cli.Require("packageroot"), "DHE package root");
        var verifyValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["packageroot"] = source };
        var expectedPackageId = cli.Optional("expectedpackageid");
        if (!string.IsNullOrWhiteSpace(expectedPackageId)) verifyValues["expectedpackageid"] = expectedPackageId;
        if (cli.Has("requirerelease")) verifyValues["requirerelease"] = "true";
        if (VerifyPackage(new Cli("verify-package", verifyValues)) != 0)
            throw new DheException("DHE package verification failed.");
        var destination = Path.GetFullPath(cli.Require("destination")); EnsureOutputOutsideRoot(destination, source);
        var previousVersion = (string?)null;
        var previousManifest = Path.Combine(destination, "dhe-toolchain-manifest.json");
        if (File.Exists(previousManifest))
        {
            try { previousVersion = GetString(ReadJson<JsonElement>(previousManifest), "toolchainVersion"); } catch { previousVersion = null; }
        }
        if (Directory.Exists(destination)) { if (!cli.Has("forceoutput")) throw new DheException("Destination already exists; pass -ForceOutput to replace it."); Directory.Delete(destination, true); }
        CopyDirectory(source, destination);
        var installed = ReadJson<JsonElement>(Path.Combine(destination, "dhe-toolchain-manifest.json"));
        var report = new { schemaVersion = 1, format = "hybridclr.dhe-toolchain-install.json", generatedAtUtc = DateTimeOffset.UtcNow, passed = true, sourcePackage = source, destination, operation = previousVersion == null ? "install" : "upgrade", previousVersion, installedVersion = GetString(installed, "toolchainVersion"), contractVersion = GetInt(installed, "contractVersion"), packageId = GetString(installed, "packageId"), expectedPackageId = cli.Optional("expectedpackageid"), packageGatePassed = true, errors = Array.Empty<string>(), warnings = Array.Empty<string>() };
        var output = cli.Optional("output"); if (!string.IsNullOrWhiteSpace(output)) WriteJson(SafeReportPath(output, new[] { source, destination }), report);
        Console.WriteLine("DHE package installed: " + destination); return 0;
    }

    private static int NewAdapter(Cli cli)
    {
        var output = cli.Require("output");
        var full = SafeReportPath(output, Array.Empty<string>());
        var namespaceName = cli.Optional("namespace") ?? "YourGame.Editor";
        if (!IsCSharpNamespace(namespaceName))
            throw new DheException("Namespace must be a dotted C# identifier: " + namespaceName);
        var identityNamespace = cli.Optional("identitynamespace") ??
            (namespaceName.EndsWith(".Editor", StringComparison.Ordinal)
                ? namespaceName[..^".Editor".Length] : namespaceName);
        if (!IsCSharpNamespace(identityNamespace))
            throw new DheException("IdentityNamespace must be a dotted C# identifier: " +
                identityNamespace);

        var explicitTemplate = cli.Optional("template");
        var candidates = new[]
        {
            explicitTemplate,
            Path.Combine(cli.Root, "templates", "DheWorkflowBuild.cs"),
            Path.Combine(Directory.GetCurrentDirectory(), "templates", "DheWorkflowBuild.cs"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "templates", "DheWorkflowBuild.cs")),
            Path.Combine(AppContext.BaseDirectory, "templates", "DheWorkflowBuild.cs"),
        };
        var template = candidates.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!)).FirstOrDefault(File.Exists);
        if (template == null)
            throw new DheException("DHE C# adapter template was not found. Pass -Root or -Template explicitly.");

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var source = File.ReadAllText(template)
            .Replace("__DHE_NAMESPACE__", namespaceName, StringComparison.Ordinal)
            .Replace("__DHE_IDENTITY_NAMESPACE__", identityNamespace, StringComparison.Ordinal);
        File.WriteAllText(full, source, new UTF8Encoding(false));
        var identityOutput = cli.Optional("identityoutput");
        if (string.IsNullOrWhiteSpace(identityOutput))
        {
            DirectoryInfo? directory = new FileInfo(full).Directory;
            while (directory != null && !string.Equals(directory.Name, "Assets",
                       StringComparison.OrdinalIgnoreCase)) directory = directory.Parent;
            if (directory != null)
                identityOutput = Path.Combine(directory.FullName, "HybridCLRGenerated",
                    "DheBuildIdentity.cs");
        }
        if (!string.IsNullOrWhiteSpace(identityOutput))
        {
            var identityFull = SafeReportPath(identityOutput, new[] { full });
            var identityTemplate = Path.Combine(Path.GetDirectoryName(template)!,
                "DheBuildIdentity.cs");
            if (!File.Exists(identityTemplate))
                throw new DheException("DHE BuildIdentity template was not found: " + identityTemplate);
            Directory.CreateDirectory(Path.GetDirectoryName(identityFull)!);
            File.WriteAllText(identityFull, File.ReadAllText(identityTemplate)
                .Replace("__DHE_IDENTITY_NAMESPACE__", identityNamespace,
                    StringComparison.Ordinal), new UTF8Encoding(false));
            Console.WriteLine("DHE BuildIdentity template: " + identityFull);
        }
        Console.WriteLine("DHE C# adapter template: " + full);
        return 0;
    }

    private static int NewConfig(Cli cli)
    {
        var output = SafeReportPath(cli.Require("output"), Array.Empty<string>());
        string project = Path.GetFullPath(cli.Optional("projectpath") ?? Directory.GetCurrentDirectory());
        string toolRoot = Path.GetFullPath(cli.Optional("toolchainroot") ?? AppContext.BaseDirectory);
        string manifest = RequireFile(Path.Combine(toolRoot, "dhe-toolchain-manifest.json"), "Published DHE tool inventory");
        string packageId = GetString(ReadJson<JsonElement>(manifest), "packageId") ?? "";
        if (!InspectPackage(toolRoot, packageId, false).Passed)
            throw new DheException("Cannot generate config from an invalid tool bundle.");
        var config = new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-workflow-config.json",
            projectPath = project,
            settingsFile = Path.Combine(project, "ProjectSettings/HybridCLRSettings.asset"),
            outputRoot = Path.Combine(project, "artifacts/dhe-workflow"),
            baselineAotRoot = Path.Combine(project, "releases/previous/stripped-aot"),
            baselineManifestPath = Path.Combine(project, "releases/previous/stripped-aot/dhe-baseline-manifest.json"),
            runtimeManifestPath = Path.Combine(project, "HybridCLRData/DHE/runtime-manifest.json"),
            packageLockPath = Path.Combine(project, "HybridCLRData/DHE/package-lock.json"),
            sourceBoundaryPath = Path.Combine(project, "ProjectSettings/DHE/dhe-source-boundary.json"),
            archiveRoot = Path.Combine(project, "artifacts/dhe-workflow-archive"),
            toolchainRoot = toolRoot,
            expectedToolchainPackageId = packageId,
            target = "Android",
            adapterMethod = "YourGame.Editor.DheWorkflowBuild.Prepare",
            mode = "Exploratory",
            runPlayer = false,
            stopAfterPreflight = true,
            bootstrap = false,
            unityTimeoutSeconds = 600,
            unityArguments = new Dictionary<string, string>()
        };
        WriteJson(output, config);
        Console.WriteLine("DHE workflow config: " + output);
        return 0;
    }

    private static void ApplyWorkflowConfig(Cli cli)
    {
        var configPath = cli.Optional("config");
        if (string.IsNullOrWhiteSpace(configPath)) return;
        var fullConfigPath = RequireFile(configPath, "DHE workflow config");
        using var document = JsonDocument.Parse(File.ReadAllText(fullConfigPath));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new DheException("DHE workflow config must be a JSON object: " + fullConfigPath);
        var configDirectory = Path.GetDirectoryName(fullConfigPath)!;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var key = property.Name.ToLowerInvariant();
            if (key is "schemaversion" or "format" or "unityarguments") continue;
            if (cli.Values.ContainsKey(key)) continue;
            cli.Values[key] = ConfigValue(property.Value, key, configDirectory);
        }
        if (!cli.Values.ContainsKey("unityarguments") && document.RootElement.TryGetProperty("unityArguments", out var unityArguments))
            cli.Values["unityarguments"] = unityArguments.GetRawText();
    }

    private static string ConfigValue(JsonElement value, string key, string configDirectory)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => IsWorkflowPathKey(key)
                ? ResolveConfigPath(value.GetString() ?? string.Empty, configDirectory)
                : value.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            _ => throw new DheException($"Workflow config property '{key}' must be a scalar value.")
        };
    }

    private static bool IsWorkflowPathKey(string key) => key is "projectpath" or "settingsfile" or "outputroot" or "baselineaotroot" or "baselinemanifestpath" or "runtimemanifestpath" or "packagelockpath" or "sourceboundarypath" or "archiveroot" or "toolchainroot" or "dnlibpath" or "unity" or "dhecurrentinputroot";

    private static string ResolveConfigPath(string value, string configDirectory)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)) return value;
        return Path.GetFullPath(Path.Combine(configDirectory, value));
    }

    private static void AppendUnityArguments(List<string> arguments, Cli cli)
    {
        var raw = cli.Optional("unityarguments");
        if (string.IsNullOrWhiteSpace(raw)) return;
        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new DheException("Workflow unityArguments must be a JSON object.");
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(property.Name, "^[A-Za-z][A-Za-z0-9]*$"))
                throw new DheException("Workflow unityArguments contains an invalid argument name: " + property.Name);
            if (property.Name.Equals("projectPath", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("executeMethod", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("dheTarget", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("dheOutputRoot", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("dheBaselineRoot", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("dheCurrentRoot", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("dheMode", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("dheProjectPlan", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("dheCurrentInputRoot", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("dheEngineWorkflow", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("dheIl2CppCodeGeneration", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("dheAotMetadataAssemblies", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("extraScriptingDefines", StringComparison.OrdinalIgnoreCase))
                throw new DheException("Workflow unityArguments cannot override a host-owned argument: " + property.Name);
            if (property.Value.ValueKind != JsonValueKind.String && property.Value.ValueKind != JsonValueKind.Number && property.Value.ValueKind != JsonValueKind.True && property.Value.ValueKind != JsonValueKind.False)
                throw new DheException("Workflow unityArguments values must be scalar: " + property.Name);
            arguments.Add("-" + property.Name);
            arguments.Add(property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.GetRawText());
        }
    }

    private static void ValidateResourceEvidence(string path, string target)
    {
        var evidence = ReadJson<JsonElement>(RequireFile(path, "Resource evidence"));
        if (GetInt(evidence, "schemaVersion") != 1 ||
            GetString(evidence, "format") != "hybridclr.dhe-resource-evidence.json")
            throw new DheException("Invalid DHE resource evidence format: " + path);
        if (!evidence.TryGetProperty("passed", out var passed) || !passed.GetBoolean())
            throw new DheException("DHE resource evidence reports failure: " + path);
        if (!string.Equals(GetString(evidence, "target"), target, StringComparison.OrdinalIgnoreCase))
            throw new DheException("DHE resource evidence target does not match workflow target: " + path);
        if (string.IsNullOrWhiteSpace(GetString(evidence, "strategy")))
            throw new DheException("DHE resource evidence strategy is missing: " + path);
        var policy = GetString(evidence, "policy");
        if (policy != "required" && policy != "skip")
            throw new DheException("DHE resource evidence policy is invalid: " + path);
        var semantics = GetString(evidence, "pathSemantics");
        if (semantics != "workspace-absolute-v1" && semantics != "archive-relative-v1")
            throw new DheException("DHE resource evidence pathSemantics is invalid: " + path);
        string? resourceBuild = GetString(evidence, "resourceBuild");
        if (string.IsNullOrWhiteSpace(resourceBuild))
            resourceBuild = GetString(evidence, "yooAssetBuild");
        if (policy == "required" && string.IsNullOrWhiteSpace(resourceBuild))
            throw new DheException("Required DHE resource build evidence is missing: " + path);
        if (!string.IsNullOrWhiteSpace(resourceBuild))
        {
            var reportPath = ResolveEvidencePath(resourceBuild, Path.GetDirectoryName(path)!,
                "structured resource build report");
            var report = ReadJson<JsonElement>(reportPath);
            if (GetInt(report, "schemaVersion") != 1 || !GetBool(report, "passed") ||
                !string.Equals(GetString(report, "target"), target, StringComparison.OrdinalIgnoreCase))
                throw new DheException("Structured resource build report contract is invalid: " + reportPath);
            if (GetString(evidence, "strategy") != "cat-yooasset-structured-report") return;
            if (GetString(report, "format") != "hybridclr.dhe-yooasset-build.json")
                throw new DheException("YooAsset structured report format is invalid: " + reportPath);
            var packageValue = GetString(report, "packageDirectory") ?? "";
            var packageDirectory = Path.GetFullPath(Path.IsPathRooted(packageValue) ? packageValue :
                Path.Combine(Path.GetDirectoryName(reportPath)!, packageValue));
            if (!Directory.Exists(packageDirectory)) throw new DheException("Archived YooAsset bundle directory is missing.");
            var buildReport = ResolveEvidencePath(GetString(report, "buildReport"), Path.GetDirectoryName(reportPath)!,
                "YooAsset build report");
            if (!Sha256File(buildReport).Equals(GetString(report, "buildReportSha256"), StringComparison.OrdinalIgnoreCase))
                throw new DheException("YooAsset build report hash mismatch.");
            if (!report.TryGetProperty("requiredAssets", out var assets) || assets.ValueKind != JsonValueKind.Array ||
                GetInt(evidence, "requiredAssetCount") != assets.GetArrayLength())
                throw new DheException("YooAsset required asset evidence is incomplete.");
            var assetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in assets.EnumerateArray())
            {
                var assetPath = GetString(asset, "assetPath") ?? "";
                var bundleName = GetString(asset, "bundleFileName") ?? "";
                if (!GetBool(asset, "present") || !assetNames.Add(assetPath) || !IsPortableRelativePath(bundleName))
                    throw new DheException("YooAsset required asset entry is invalid: " + assetPath);
                var bundlePath = ResolveContainedPath(packageDirectory, bundleName, "YooAsset bundle");
                if (!File.Exists(bundlePath) || new FileInfo(bundlePath).Length != GetLong(asset, "bundleSize") ||
                    !Sha256File(bundlePath).Equals(GetString(asset, "bundleSha256"), StringComparison.OrdinalIgnoreCase))
                    throw new DheException("YooAsset required bundle hash/size mismatch: " + assetPath);
            }
        }
    }

    private static void PrintHelp() => Console.WriteLine(string.Join(Environment.NewLine,
        "HybridCLR DHE C# tool",
        "Commands: version, mv, batch, base-registry, resource-release-build, " +
        "resource-release-plan, resource-release-qualify, resource-update, " +
        "stage-resource-update, android-device-smoke, resource-player-evidence, " +
        "resource-release-gate, " +
        "channel-state, baseline-manifest, aot-metadata-manifest, preflight, workflow, " +
        "release-gate, regression, schema-validate, schema-gate, validate, archive, " +
        "doctor, verify-package, release-evidence, publish, install, new-adapter, " +
        "new-config, assemble-runtime, native-tests, build-managed-cases, " +
        "generate-test-manifest, generate-metadata-stress-source, reference, " +
        "compare-results, check-environment, clear-unity-project-locks, wait-editor, " +
        "prepare-engine-test-project, bootstrap-repos, tree-hash, file-hash",
        "Base registry accepts -ExistingRegistry or comma-separated -BaseIdentities, " +
        "-BaselineRoots, -BaseNativeManifests, -EngineWorkflows, with optional " +
        "-PayloadVariantIds, -Labels, and -AotMetadataRoots. Retiring an online Base " +
        "requires -RetireBaseIds and -RetirementReason.",
        "Resource release build accepts one config-relative JSON document with -Config, " +
        "uses -SchemasRoot (or <Root>/schemas), and replaces an existing output only with " +
        "-ForceOutput.",
        "Resource release plan accepts one config-relative JSON document with -Config, " +
        "joins the immutable Base runner catalog to every active Base, and generates the " +
        "exact resource-release-qualify config without starting a Player. " +
        "Resource-update automatically compiles frozen AOT adaptations against each authenticated Base snapshot.",
        "Optional -FrozenAotPlans imports one Base/Current-bound plan per Base and independently validates its selections.",
        "Resource release qualify accepts one config-relative JSON document with -Config, " +
        "stages and directly starts every process runner, accepts authenticated distributed " +
        "prequalified reports, and emits one exact-coverage aggregate gate.",
        "Release resource update accepts a protected -ChannelSnapshot, or the legacy " +
        "explicit ledger arguments. Registry revision 2 or later also requires " +
        "-PreviousBaseRegistry when its Base set changes.",
        "For target-specific current assemblies, name -CurrentRoot with " +
        "-CurrentVariantId and add the remaining JSON map with -CurrentVariantRoots. " +
        "Single-target releases may omit CurrentVariantId and retain default.",
        "Resource release qualification uses resource-release-gate with the same " +
        "channel snapshot and one Player report per active Base. Pass historical " +
        "Release package locations with -EvidenceToolchainRoots when active Bases " +
        "were built by authorized older toolchains. channel-state atomically promotes " +
        "only a state-bound passing gate.",
        "Toolchain publication regression requires -EvidenceToolchainRoots to exactly " +
        "cover the authenticated historical authority set.",
        "Example: dotnet run --project tool/HybridCLR.DheTool.csproj -- workflow " +
        "-Config <project/dhe-workflow-config.json>"));

    private static string ResolveUnity(Cli cli, string project) => RequireFile(cli.Optional("unity") ?? Environment.GetEnvironmentVariable("DHE_UNITY_EXE") ?? throw new DheException("Set -Unity or DHE_UNITY_EXE."), "Unity editor");
    private static void RunUnity(string executable, string workingDirectory, IEnumerable<string> arguments, IDictionary<string, string> environment, string logPath, int timeoutSeconds)
    {
        string unityLock = Path.Combine(Path.GetFullPath(workingDirectory), "Temp", "UnityLockfile");
        if (File.Exists(unityLock) && !TryRemoveStaleUnityLock(unityLock))
            throw new DheException("Unity project is already open or its lock is active: " + workingDirectory);
        var startedAt = DateTime.UtcNow;
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg); foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start) ?? throw new DheException("Unable to start Unity editor.");
        var outputLock = new object();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data == null) return;
            lock (outputLock) stdout.AppendLine(eventArgs.Data);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data == null) return;
            lock (outputLock) stderr.AppendLine(eventArgs.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new DheException($"Unity timed out after {timeoutSeconds} seconds.");
        }
        // Unity may leave a compiler service alive with inherited pipe handles. Stop
        // the asynchronous readers after the editor itself exits instead of waiting
        // forever for a descendant-owned EOF.
        try { process.CancelOutputRead(); } catch (InvalidOperationException) { }
        try { process.CancelErrorRead(); } catch (InvalidOperationException) { }
        WaitForUnityProjectRelease(workingDirectory, startedAt.AddSeconds(timeoutSeconds));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
        string processOutput;
        lock (outputLock) processOutput = stdout + Environment.NewLine + stderr;
        File.WriteAllText(logPath, processOutput, new UTF8Encoding(false));
        if (process.ExitCode != 0) throw new DheException($"Unity exited with code {process.ExitCode}. See {logPath}.");
        var argumentList = arguments.ToArray();
        var unityLogIndex = Array.FindIndex(argumentList, value =>
            string.Equals(value, "-logFile", StringComparison.OrdinalIgnoreCase));
        if (unityLogIndex >= 0 && unityLogIndex + 1 < argumentList.Length)
        {
            var unityLog = argumentList[unityLogIndex + 1];
            if (File.Exists(unityLog))
            {
                var text = UnityBatchLog.Read(unityLog);
                if (text.Contains("executeMethod method", StringComparison.Ordinal) &&
                        text.Contains("threw exception", StringComparison.Ordinal) ||
                    text.Contains("Application will terminate with return code 1", StringComparison.Ordinal) ||
                    text.Contains("HandleProjectAlreadyOpenInAnotherInstance", StringComparison.Ordinal))
                    throw new DheException("Unity reported a failed batch stage. See " + unityLog + ".");
            }
        }
    }

    private static void WaitForUnityProjectRelease(string project, DateTime deadline)
    {
        var lockPath = Path.Combine(Path.GetFullPath(project), "Temp", "UnityLockfile");
        DateTime? releasedAt = null;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(lockPath) && !TryRemoveStaleUnityLock(lockPath)) releasedAt = null;
            else if (releasedAt == null) releasedAt = DateTime.UtcNow;
            else if ((DateTime.UtcNow - releasedAt.Value).TotalSeconds >= 5) return;
            Thread.Sleep(250);
        }
        throw new DheException("Unity project lock was not released before the stage timeout: " + project);
    }

    private static bool TryRemoveStaleUnityLock(string lockPath)
    {
        try
        {
            using (File.Open(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(lockPath);
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static T ReadJson<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new DheException("Invalid JSON: " + path);
    private static void WriteJson(string path, object value) { ProtectUnityToolOutput(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false)); }
    private static string RequireFile(string path, string description) { var full = Path.GetFullPath(path); if (!File.Exists(full)) throw new DheException($"{description} was not found: {full}"); return full; }
    private static string RequireDirectory(string path, string description) { var full = Path.GetFullPath(path); if (!Directory.Exists(full)) throw new DheException($"{description} was not found: {full}"); return full; }
    private static string SafeReportPath(string path, IEnumerable<string> protectedPaths) { ProtectUnityToolOutput(path); var full = Path.GetFullPath(path); foreach (var item in protectedPaths) if (full.Equals(Path.GetFullPath(item), StringComparison.OrdinalIgnoreCase)) throw new DheException("Output must not overwrite an input: " + full); return full; }
    private static string SafeOutputRoot(string path, IEnumerable<string> protectedPaths) { var full = SafeReportPath(path, protectedPaths); if (Directory.Exists(full) && (File.Exists(Path.Combine(full, ".git")) || Directory.Exists(Path.Combine(full, ".git")) || Directory.Exists(Path.Combine(full, ".svn")))) throw new DheException("Output root cannot be a repository root: " + full); return full; }
    private static void EnsureOutputNotAncestor(string output, string root) { var outPath = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar; if (rootPath.StartsWith(outPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || rootPath.TrimEnd(Path.DirectorySeparatorChar).Equals(outPath, StringComparison.OrdinalIgnoreCase)) throw new DheException("Output cannot be the source root or an ancestor of it: " + output); }
    private static void EnsureOutputOutsideRoot(string output, string root) { var outPath = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); if (outPath.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || rootPath.StartsWith(outPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || rootPath.Equals(outPath, StringComparison.OrdinalIgnoreCase)) throw new DheException("Output must be external to the source root: " + output); }
    private static string Sha256File(string path) { using var sha = SHA256.Create(); using var input = File.OpenRead(path); return Convert.ToHexString(sha.ComputeHash(input)).ToLowerInvariant(); }
    private static string CalculatePackageId(string version, int contractVersion, string mode, bool releaseReady,
        string? sourceHead, string? sourceTree, bool sourceClean, bool sourceTracked, string layoutHash,
        IEnumerable<string> commands, IEnumerable<PackageFileEntry> files)
    {
        var lines = new List<string>
        {
            PackageIdAlgorithm, version, contractVersion.ToString(), mode, releaseReady ? "1" : "0",
            (sourceHead ?? "").ToLowerInvariant(), (sourceTree ?? "").ToLowerInvariant(),
            sourceClean ? "1" : "0", sourceTracked ? "1" : "0", layoutHash.ToLowerInvariant()
        };
        lines.AddRange(commands.OrderBy(value => value, StringComparer.Ordinal).Select(value => "command|" + value));
        lines.AddRange(files.OrderBy(file => file.Path, StringComparer.Ordinal)
            .Select(file => $"file|{file.Path}|{file.Size}|{file.Sha256.ToLowerInvariant()}"));
        return Sha256Text(string.Join("\n", lines));
    }

    private static bool ValidateInstalledLayout(string root, JsonElement manifest, List<string> errors)
    {
        try
        {
            var path = ResolveContainedPath(root, "manifests/dhe-toolchain-layout.json", "Toolchain layout");
            if (!File.Exists(path) || !Sha256File(path).Equals(GetString(manifest, "layoutSha256"), StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Toolchain layout hash does not match the manifest.");
                return false;
            }
            var layout = ReadJson<JsonElement>(path);
            var valid = GetInt(layout, "schemaVersion") == 1 &&
                GetString(layout, "format") == "hybridclr.dhe-toolchain-layout.json" &&
                GetString(layout, "toolchainVersion") == GetString(manifest, "toolchainVersion") &&
                GetInt(layout, "contractVersion") == GetInt(manifest, "contractVersion");
            var layoutCommands = layout.GetProperty("commands").EnumerateArray().Select(value => value.GetString() ?? "")
                .OrderBy(value => value, StringComparer.Ordinal);
            var manifestCommands = manifest.GetProperty("commands").EnumerateArray().Select(value => value.GetString() ?? "")
                .OrderBy(value => value, StringComparer.Ordinal);
            valid &= layoutCommands.SequenceEqual(manifestCommands, StringComparer.Ordinal);
            valid &= layout.GetProperty("exactPaths").EnumerateArray()
                .Any(entry => entry.GetString() == GetString(manifest, "entryPoint"));
            foreach (var entry in layout.GetProperty("exactPaths").EnumerateArray())
            {
                var relative = entry.GetString() ?? "";
                valid &= IsPortableRelativePath(relative) && File.Exists(ResolveContainedPath(root, relative, "Layout file"));
            }
            foreach (var entry in layout.GetProperty("prefixes").EnumerateArray())
            {
                var relative = (entry.GetString() ?? "").TrimEnd('/', '\\');
                valid &= IsPortableRelativePath(relative) && Directory.Exists(ResolveContainedPath(root, relative, "Layout prefix"));
            }
            if (!valid) errors.Add("Installed toolchain layout contract is invalid.");
            return valid;
        }
        catch (Exception ex)
        {
            errors.Add("Installed toolchain layout: " + ex.Message);
            return false;
        }
    }

    private static bool ValidateInstalledBoundary(string root, List<string> errors)
    {
        try
        {
            var path = ResolveContainedPath(root, "dhe-source-boundary.json", "Installed source boundary");
            var boundary = ReadJson<JsonElement>(RequireFile(path, "Installed source boundary"));
            var valid = GetInt(boundary, "schemaVersion") == 1 &&
                GetString(boundary, "format") == "hybridclr.dhe-source-boundary.json" &&
                GetString(boundary, "pathBase") == "manifest-directory-v1";
            foreach (var entry in boundary.GetProperty("exactPaths").EnumerateArray())
            {
                var relative = entry.GetString() ?? "";
                valid &= IsPortableRelativePath(relative) && File.Exists(ResolveContainedPath(root, relative, "Boundary file"));
            }
            foreach (var entry in boundary.GetProperty("prefixes").EnumerateArray())
            {
                var relative = (entry.GetString() ?? "").TrimEnd('/', '\\');
                valid &= IsPortableRelativePath(relative) && Directory.Exists(ResolveContainedPath(root, relative, "Boundary prefix"));
            }
            if (!valid) errors.Add("Installed source boundary is invalid.");
            return valid;
        }
        catch (Exception ex)
        {
            errors.Add("Installed source boundary: " + ex.Message);
            return false;
        }
    }

    private static string ResolveContainedPath(string root, string relative, string description)
    {
        if (!IsPortableRelativePath(relative)) throw new DheException(description + " path is unsafe: " + relative);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(normalizedRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new DheException(description + " escapes its root: " + relative);
        return full;
    }

    private static bool IsPackageGeneratedPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("tool/bin/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("tool/obj/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPortableRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.StartsWith('/') ||
            value.StartsWith('\\') || value.Contains(':')) return false;
        var parts = value.Replace('\\', '/').Split('/');
        return parts.All(part => part.Length > 0 && part is not "." and not "..");
    }

    private static bool IsHex(string? value, int minimumLength, int maximumLength) =>
        value != null && value.Length >= minimumLength && value.Length <= maximumLength &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
    private static string NormalizeName(string value) { var trimmed = value.Trim(); var name = trimmed.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed; if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || Path.IsPathRooted(name) || name.Contains("..", StringComparison.Ordinal)) throw new DheException("Assembly name must be a simple file name: " + value); return name; }
    private static bool IsDheExecutionMode(string? value) =>
        string.Equals(value, "dhe-differential", StringComparison.Ordinal) ||
        string.Equals(value, "interpreter-only", StringComparison.Ordinal);
    private static bool IsCSharpNamespace(string value) => value.Split('.', StringSplitOptions.None).All(part => part.Length > 0 && (char.IsLetter(part[0]) || part[0] == '_') && part.Skip(1).All(ch => char.IsLetterOrDigit(ch) || ch == '_'));
    private static bool SetEquals(IEnumerable<string> a, IEnumerable<string> b) => new HashSet<string>(a, StringComparer.OrdinalIgnoreCase).SetEquals(b);
    private static string GetString(Dictionary<string, JsonElement> d, string key) => d.TryGetValue(key, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
    private static int GetInt(Dictionary<string, JsonElement> d, string key) => d.TryGetValue(key, out var e) && e.TryGetInt32(out var v) ? v : 0;
    private static int GetInt(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var p) && p.TryGetInt32(out var value) ? value : 0;
    private static long GetLong(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var p) && p.TryGetInt64(out var value) ? value : 0;
    private static string? GetString(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static bool GetBool(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False && p.GetBoolean();
    private static void CopyDirectory(string source, string destination) => CopyDirectoryFiltered(source, destination, _ => true);

    private static void CopyDirectoryFiltered(string source, string destination, Func<string, bool> include)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            if (!include(file)) continue;
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
    private static void CopyRelative(string sourceRoot, string destinationRoot, string relative) { var source = Path.Combine(sourceRoot, relative.Replace('/', Path.DirectorySeparatorChar)); if (!File.Exists(source)) throw new DheException("Layout source file was not found: " + source); var destination = Path.Combine(destinationRoot, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination, true); }
    private static string GitValue(string root, params string[] arguments) { try { root = Path.GetFullPath(root); var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }; start.ArgumentList.Add("-C"); start.ArgumentList.Add(root); foreach (var arg in arguments) start.ArgumentList.Add(arg); using var process = Process.Start(start); if (process == null) return ""; var output = process.StandardOutput.ReadToEnd().Trim(); process.WaitForExit(); return process.ExitCode == 0 ? output : ""; } catch { return ""; } }
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class AssemblyRecord { public AssemblyRecord(string assemblyName, string sha256) { AssemblyName = assemblyName; Sha256 = sha256; } public string AssemblyName { get; } public string Sha256 { get; } }
    private sealed record PackageFileEntry(string Path, long Size, string Sha256);
    private sealed class PackageInspection
    {
        public bool Passed { get; set; }
        public string ManifestPath { get; set; } = "";
        public string? ToolchainVersion { get; set; }
        public int? ContractVersion { get; set; }
        public string? PackageId { get; set; }
        public string? ComputedPackageId { get; set; }
        public bool PackageIdValid { get; set; }
        public bool PackageTreeSafe { get; set; }
        public bool ReleaseReady { get; set; }
        public bool ReleaseIdentityValid { get; set; }
        public int ExpectedFileCount { get; set; }
        public int ActualFileCount { get; set; }
        public bool HashesValid { get; set; }
        public bool ProhibitedFilesValid { get; set; }
        public bool JsonValid { get; set; }
        public bool SchemaValid { get; set; }
        public bool LayoutValid { get; set; }
        public bool BoundaryValid { get; set; }
        public List<string> Errors { get; } = new();
    }
    private sealed record BatchRecord(string AssemblyName, string Baseline, string Current,
        string BaseMetaVersionJson, string BaseMetaVersionBytes,
        string CurrentMetaVersionJson, string CurrentMetaVersionBytes, string Status,
        int ChangedMethodCount, int AddedMethodCount, int RemovedMethodCount,
        int ChangedExistingTypeCount, int AddedTypeCount, int RemovedTypeCount,
        string[] UnsupportedChanges, string? Error);
    private sealed record ResourceAotMetadataPayload(string AssemblyName, string SourceKind,
        string Sha256, string ManifestSha256, string Path);
    private sealed record ResourceAotMetadataSet(string AotMetadataSetId,
        ResourceAotMetadataPayload[] Assemblies);
    private sealed record ResourceAotMetadataBaseSelection(string BaseId,
        string AotMetadataSetId, string PayloadVariantId,
        string CurrentAssemblySetSha256, ResourceAssemblyMode[] AssemblyModes,
        object[]? FrozenAotSources = null);
    private sealed record ResourceAssemblyMode(string AssemblyName, string ExecutionMode,
        [property: System.Text.Json.Serialization.JsonIgnore] ResourceExecutionPlan? ExecutionPlan = null)
    {
        public ResourceExecutionPlan[] ExecutionPlans => ExecutionPlan == null
            ? Array.Empty<ResourceExecutionPlan>() : new[] { ExecutionPlan };
    }
    private sealed record MultiBaseResourceReleaseProof(
        string ResourceUpdateManifestSha256,
        string ReleaseLedgerSha256,
        string? ParentReleaseLedgerSha256,
        string ReleaseChannelId,
        int ReleaseRevision,
        string BaseRegistrySha256,
        int ActiveBaseCount,
        string CurrentAssemblySetSha256,
        string PayloadVariantSetSha256);
    private sealed class CurrentVariantData
    {
        public string VariantId { get; set; } = string.Empty;
        public string Root { get; set; } = string.Empty;
        public string CurrentSetHash { get; set; } = string.Empty;
        public Dictionary<string, MetaVersionSnapshot> Snapshots { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public string[] AddressTakenFields { get; set; } = Array.Empty<string>();
        public List<object> PayloadFiles { get; set; } = new();
        public List<object> RuntimeAssemblies { get; set; } = new();
    }
    private sealed class DheException : Exception { public DheException(string message) : base(message) { } }
}

internal sealed class Cli
{
    public string Command { get; }
    public Dictionary<string, string> Values { get; }
    public string Root => Optional("root") ?? Directory.GetCurrentDirectory();
    internal Cli(string command, Dictionary<string, string> values) { Command = command; Values = values; }
    public static Cli Parse(string[] args) { var command = args.Length == 0 ? "help" : args[0].TrimStart('-'); var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); for (var i = 1; i < args.Length; i++) { var key = args[i].TrimStart('-').ToLowerInvariant(); if (key.Length == 0) continue; var value = i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[++i] : "true"; values[key] = value; } return new Cli(command, values); }
    public string Require(string key) => Values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidOperationException("Missing required argument -" + key);
    public string? Optional(string key) => Values.TryGetValue(key, out var value) ? value : null;
    public bool Has(string key) => Values.TryGetValue(key, out var value) && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    public List<string> GetList(string key) => (Optional(key) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

internal static class Settings
{
    internal sealed class Sets { public string[] Hot { get; set; } = Array.Empty<string>(); public string[] Dhe { get; set; } = Array.Empty<string>(); public string[] Patch { get; set; } = Array.Empty<string>(); }
    public static Sets Read(string path) { var hot = ReadList(path, "hotUpdateAssemblies"); var dhe = ReadList(path, "dheAotAssemblies"); var patch = ReadList(path, "patchAOTAssemblies"); return new Sets { Hot = hot, Dhe = dhe, Patch = patch }; }
    private static string[] ReadList(string path, string key) { var values = new List<string>(); var active = false; foreach (var raw in File.ReadLines(path)) { var line = raw.Trim(); if (line.StartsWith(key + ":", StringComparison.Ordinal)) { active = true; continue; } if (active && line.StartsWith("- ")) { var value = line[2..].Trim().Trim('\'', '"'); if (value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) value = value[..^4]; if (value.Length > 0) values.Add(value); continue; } if (active && line.Length > 0 && !line.StartsWith("-")) active = false; } return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); }
}
