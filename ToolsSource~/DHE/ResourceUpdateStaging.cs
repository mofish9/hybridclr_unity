using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.IO.Compression;

namespace HybridCLR.DheTool;

internal static partial class Program
{
    private static int StageResourceUpdate(Cli cli)
    {
        var updateRoot = RequireDirectory(cli.Require("updateroot"), "DHE resource update root");
        var assetRoot = RequireDirectory(cli.Require("assetroot"), "DHE runtime asset destination");
        ProtectUnityToolOutput(assetRoot);
        var baseBuildIdentityPath = RequireFile(cli.Require("basebuildidentity"),
            "Base Player build identity");
        var baseBuildIdentity = ReadJson<JsonElement>(baseBuildIdentityPath);
        var manifestPath = RequireFile(Path.Combine(updateRoot, "dhe-resource-update.json"),
            "DHE resource update manifest");
        var manifest = ReadJson<JsonElement>(manifestPath);
        if (GetInt(manifest, "schemaVersion") != 1 ||
            !string.Equals(GetString(manifest, "format"), "hybridclr.dhe-resource-update.json",
                StringComparison.Ordinal) ||
            !new[] { "single-current-payload", "variant-current-payload" }.Contains(
                GetString(manifest, "payloadModel"), StringComparer.Ordinal) ||
            !string.Equals(GetString(manifest, "compatibilityPolicy"),
                ResourceUpdateCompatibility.Policy, StringComparison.Ordinal) ||
            !string.Equals(GetString(manifest, "runtimeProtocol"),
                ResourceUpdateCompatibility.RuntimeProtocol, StringComparison.Ordinal) ||
            !GetBool(manifest, "compatibilityValidated") ||
            GetBool(manifest, "playerUpdateRequired"))
            throw new DheException("Resource update must be a schema v1 single-current-payload release.");

        ReleaseLedgerDocument? releaseLedger = ValidateReleaseLedgerForStaging(updateRoot,
            manifest);
        var validationSource = ValidateResourceUpdateCompatibility(updateRoot, manifest);
        var selectedBase = ValidateStagingBuildIdentity(baseBuildIdentityPath,
            baseBuildIdentity, manifest);

        var runtimeAssetRoot = RequirePortableAssetRoot(GetString(manifest, "runtimeAssetRoot"),
            "runtimeAssetRoot");
        var baseMetaVersionAssetRoot = RequirePortableAssetRoot(
            GetString(manifest, "baseMetaVersionAssetRoot"), "baseMetaVersionAssetRoot");
        if (!baseMetaVersionAssetRoot.StartsWith(runtimeAssetRoot, StringComparison.OrdinalIgnoreCase))
            throw new DheException("BaseMetaVersionAssetRoot must be contained by RuntimeAssetRoot for staging.");
        var baseRelative = baseMetaVersionAssetRoot[runtimeAssetRoot.Length..].TrimEnd('/');
        if (!IsPortableRelativePath(baseRelative))
            throw new DheException("Base MetaVersion destination is not a portable relative path.");
        string? basePlayerApkOption = cli.Optional("baseplayerapk");
        using MaterializedAndroidApkBase? androidApkBase =
            string.IsNullOrWhiteSpace(basePlayerApkOption)
                ? null
                : MaterializedAndroidApkBase.Create(
                    RequireFile(basePlayerApkOption, "Immutable Android Base APK"),
                    runtimeAssetRoot, baseMetaVersionAssetRoot, baseBuildIdentityPath);
        if (androidApkBase != null &&
            (!string.Equals(GetString(baseBuildIdentity, "target"), "Android",
                 StringComparison.Ordinal) ||
             !string.Equals(GetString(selectedBase, "target"), "Android",
                 StringComparison.Ordinal)))
            throw new DheException("BasePlayerApk requires an Android Base identity and registry entry.");
        var embeddedBaseRoot = androidApkBase?.MaterializedRoot ??
            RequireDirectory(ResolveContainedPath(assetRoot, baseRelative,
                "Embedded Base MetaVersion root"), "Embedded Base MetaVersion root");
        string embeddedBaseRootRecord = androidApkBase?.LogicalRoot ?? embeddedBaseRoot;
        var embeddedBase = ValidateEmbeddedBaseMetaVersionSet(embeddedBaseRoot, manifest,
            baseBuildIdentity, selectedBase);
        var baseTreeBefore = TreeHashForRelease(embeddedBaseRoot, Array.Empty<string>());
        string selectedVariantId = GetString(selectedBase, "payloadVariantId") ?? "default";
        JsonElement manifestVariant = SelectPayloadVariant(manifest, selectedVariantId,
            "Resource update manifest");
        string selectedCurrentSetHash = GetString(manifestVariant, "currentAssemblySetSha256") ??
            GetString(manifest, "currentAssemblySetSha256") ?? string.Empty;

        var runtimePlanRelative = GetString(manifest, "runtimePlan") ?? string.Empty;
        var runtimePlanSource = RequireFile(ResolveContainedPath(updateRoot, runtimePlanRelative,
            "DHE runtime plan"), "DHE runtime plan");
        string runtimePlanSha256 = GetString(manifest, "runtimePlanSha256") ?? string.Empty;
        if (!IsHex(runtimePlanSha256, 64, 64) ||
            !string.Equals(Sha256File(runtimePlanSource), runtimePlanSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("DHE runtime plan hash does not match the resource manifest.");
        var runtimePlan = ReadJson<JsonElement>(runtimePlanSource);
        ValidatePayloadVariantSetHash(runtimePlan, "DHE runtime plan");
        JsonElement runtimePlanVariant = SelectPayloadVariant(runtimePlan, selectedVariantId,
            "DHE runtime plan");
        if (GetInt(runtimePlan, "schemaVersion") != 1 ||
            !string.Equals(GetString(runtimePlan, "format"),
                "hybridclr.dhe-runtime-asset-plan.json", StringComparison.Ordinal) ||
            !string.Equals(GetString(runtimePlan, "selection"),
                "embedded-base-metaversion-and-aot-metadata-set", StringComparison.Ordinal) ||
            !string.Equals(GetString(runtimePlanVariant, "currentAssemblySetSha256"),
                selectedCurrentSetHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(runtimePlan, "payloadVariantSetSha256"),
                GetString(manifest, "payloadVariantSetSha256"), StringComparison.OrdinalIgnoreCase) ||
            !OptionalJsonPropertiesEqual(runtimePlan, manifest, "mode") ||
            !OptionalJsonPropertiesEqual(runtimePlan, manifest, "releaseReady") ||
            !OptionalJsonPropertiesEqual(runtimePlan, manifest, "releaseChannelId") ||
            !OptionalJsonPropertiesEqual(runtimePlan, manifest, "releaseRevision") ||
            !OptionalJsonPropertiesEqual(runtimePlan, manifest,
                "parentReleaseLedgerSha256") ||
            (!string.IsNullOrWhiteSpace(GetString(selectedBase, "currentAssemblySetSha256")) &&
             !string.Equals(GetString(selectedBase, "currentAssemblySetSha256"),
                 selectedCurrentSetHash, StringComparison.OrdinalIgnoreCase)) ||
            !string.Equals(RequirePortableAssetRoot(GetString(runtimePlan, "baseMetaVersionAssetRoot"),
                    "runtime plan baseMetaVersionAssetRoot"), baseMetaVersionAssetRoot,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("DHE runtime plan is not bound to the resource update manifest.");

        var payloads = ValidateResourceUpdatePayload(updateRoot, manifest, runtimePlan,
            selectedBase, runtimeAssetRoot, baseMetaVersionAssetRoot);
        var immutableFiles = cli.GetList("immutablefiles")
            .Concat(androidApkBase == null
                ? Array.Empty<string>()
                : new[] { androidApkBase.ArtifactPath })
            .Select(path => RequireFile(path, "Immutable Player file"))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var immutableBefore = immutableFiles.ToDictionary(path => path, Sha256File,
            StringComparer.OrdinalIgnoreCase);

        var stagedFiles = new List<object>();
        foreach (var payload in payloads)
        {
            if (!payload.AssetRoot.StartsWith(runtimeAssetRoot,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("DHE payload asset root is outside runtime asset root: " +
                    payload.AssetRoot);
            string assetPrefix = payload.AssetRoot[runtimeAssetRoot.Length..].Trim('/');
            string stagedRelative = string.IsNullOrEmpty(assetPrefix)
                ? payload.RelativePath
                : assetPrefix + "/" + payload.RelativePath;
            if (stagedRelative.Equals(baseRelative, StringComparison.OrdinalIgnoreCase) ||
                stagedRelative.StartsWith(baseRelative + "/", StringComparison.OrdinalIgnoreCase))
                throw new DheException("Resource payload targets the immutable Base MetaVersion tree: " + stagedRelative);
            var target = ResolveContainedPath(assetRoot, stagedRelative,
                "DHE staged payload");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(payload.SourcePath, target, true);
            var targetHash = Sha256File(target);
            if (!targetHash.Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new DheException("Staged DHE payload hash mismatch: " + payload.RelativePath);
            stagedFiles.Add(new { path = stagedRelative, assetPath = payload.AssetRoot + payload.RelativePath, sha256 = targetHash });
        }

        var planFileName = Path.GetFileName(runtimePlanRelative);
        var requestedPlanFileName = cli.Optional("planfilename");
        if (!string.IsNullOrWhiteSpace(requestedPlanFileName) &&
            !string.Equals(requestedPlanFileName, planFileName, StringComparison.Ordinal))
            throw new DheException(
                "PlanFileName cannot differ from the immutable resource manifest runtimePlan path.");
        if (!IsPortableRelativePath(planFileName) || planFileName.Contains('/'))
            throw new DheException("PlanFileName must be a simple portable file name.");
        var stagedPlanPath = ResolveContainedPath(assetRoot, planFileName, "Staged runtime plan");
        File.Copy(runtimePlanSource, stagedPlanPath, true);
        if (!string.Equals(Sha256File(stagedPlanPath), runtimePlanSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Staged DHE runtime plan hash mismatch.");
        var manifestFileName = cli.Optional("manifestfilename") ?? "dhe-resource-update.json";
        if (!IsPortableRelativePath(manifestFileName) || manifestFileName.Contains('/'))
            throw new DheException("ManifestFileName must be a simple portable file name.");
        var stagedManifestPath = ResolveContainedPath(assetRoot, manifestFileName,
            "Staged resource update manifest");
        File.Copy(manifestPath, stagedManifestPath, true);
        var validationFileName = Path.GetFileName(validationSource);
        var stagedValidationPath = ResolveContainedPath(assetRoot, validationFileName,
            "Staged resource compatibility validation");
        File.Copy(validationSource, stagedValidationPath, true);
        if (!string.Equals(Sha256File(stagedValidationPath),
                GetString(manifest, "validationSha256"), StringComparison.OrdinalIgnoreCase))
            throw new DheException("Staged DHE resource compatibility validation hash mismatch.");

        string? stagedReleaseLedgerPath = null;
        if (releaseLedger != null)
        {
            stagedReleaseLedgerPath = ResolveContainedPath(assetRoot, ReleaseLedgerFileName,
                "Staged DHE release ledger");
            File.Copy(releaseLedger.SourcePath, stagedReleaseLedgerPath, true);
            if (!string.Equals(Sha256File(stagedReleaseLedgerPath), releaseLedger.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("Staged DHE release ledger hash mismatch.");
        }

        var baseTreeAfter = TreeHashForRelease(embeddedBaseRoot, Array.Empty<string>());
        if (!baseTreeAfter.Equals(baseTreeBefore, StringComparison.OrdinalIgnoreCase))
            throw new DheException("Resource staging modified the embedded Base MetaVersion tree.");
        var immutableRecords = immutableBefore.Select(pair =>
        {
            var after = Sha256File(pair.Key);
            if (!after.Equals(pair.Value, StringComparison.OrdinalIgnoreCase))
                throw new DheException("Resource staging modified immutable Player file: " + pair.Key);
            return new { path = pair.Key, sha256Before = pair.Value, sha256After = after };
        }).ToArray();

        var output = SafeReportPath(cli.Require("output"), new[] { manifestPath, runtimePlanSource });
        WriteJson(output, new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-resource-stage.json",
            generatedAtUtc = DateTimeOffset.UtcNow,
            passed = true,
            mode = GetString(manifest, "mode") ?? "Exploratory",
            releaseReady = releaseLedger != null,
            releaseChannelId = releaseLedger?.ChannelId,
            releaseRevision = releaseLedger?.Revision,
            parentReleaseLedgerSha256 = releaseLedger?.ParentLedgerSha256,
            releaseLedgerSha256 = releaseLedger?.Sha256,
            stagedReleaseLedgerPath,
            updateRoot,
            assetRoot,
            payloadModel = GetString(manifest, "payloadModel") ?? "single-current-payload",
            payloadVariantId = selectedVariantId,
            payloadVariantSetSha256 = GetString(manifest, "payloadVariantSetSha256"),
            currentAssemblySetSha256 = selectedCurrentSetHash,
            stagedPlanPath,
            stagedPlanSha256 = runtimePlanSha256,
            stagedManifestPath,
            stagedValidationPath,
            embeddedBaseRoot = embeddedBaseRootRecord,
            embeddedBaseSourceKind = androidApkBase == null ? "directory" : "android-apk",
            embeddedBaseArtifactPath = androidApkBase?.ArtifactPath,
            embeddedBaseArtifactSha256 = androidApkBase?.ArtifactSha256,
            embeddedBaseEntryRoot = androidApkBase?.EntryRoot,
            selectedBaseId = embeddedBase.BaseId,
            selectedAotMetadataSetId = GetString(selectedBase, "aotMetadataSetId"),
            baseRegistrySha256 = GetString(manifest, "baseRegistrySha256"),
            baseRegistryEntryCount = manifest.TryGetProperty("baseRegistryEntryCount",
                out JsonElement registryEntryCount) && registryEntryCount.ValueKind == JsonValueKind.Number
                ? registryEntryCount.GetInt32()
                : (int?)null,
            baseRegistryId = GetString(manifest, "baseRegistryId"),
            baseRegistryRevision = manifest.TryGetProperty("baseRegistryRevision",
                out JsonElement registryRevision) && registryRevision.ValueKind == JsonValueKind.Number
                ? registryRevision.GetInt32()
                : (int?)null,
            baseRegistryParentSha256 = GetString(manifest, "baseRegistryParentSha256"),
            baseRegistryRetiredBaseCount = manifest.TryGetProperty("baseRegistryRetiredBaseCount",
                out JsonElement registryRetiredBaseCount) &&
                registryRetiredBaseCount.ValueKind == JsonValueKind.Number
                ? registryRetiredBaseCount.GetInt32()
                : (int?)null,
            baseRegistryLineageValidated = GetBool(manifest,
                "baseRegistryLineageValidated"),
            baseBuildIdentityPath,
            baseBuildIdentitySha256 = Sha256File(baseBuildIdentityPath),
            baseMetaVersionSetSha256 = embeddedBase.SetSha256,
            baseMetaVersionTreeSha256Before = baseTreeBefore,
            baseMetaVersionTreeSha256After = baseTreeAfter,
            baseMetaVersionUnchanged = true,
            stagedFiles = stagedFiles.ToArray(),
            immutableFiles = immutableRecords,
        });
        Console.WriteLine("DHE resource update staged without modifying the Base: " + output);
        return 0;
    }

    private sealed class MaterializedAndroidApkBase : IDisposable
    {
        private MaterializedAndroidApkBase(string artifactPath, string artifactSha256,
            string entryRoot, string materializedRoot)
        {
            ArtifactPath = artifactPath;
            ArtifactSha256 = artifactSha256;
            EntryRoot = entryRoot;
            MaterializedRoot = materializedRoot;
            LogicalRoot = artifactPath + "!/" + entryRoot.TrimEnd('/');
        }

        public string ArtifactPath { get; }
        public string ArtifactSha256 { get; }
        public string EntryRoot { get; }
        public string MaterializedRoot { get; }
        public string LogicalRoot { get; }

        public static MaterializedAndroidApkBase Create(string apkPath,
            string runtimeAssetRoot, string baseMetaVersionAssetRoot,
            string buildIdentityPath)
        {
            if (!string.Equals(Path.GetExtension(apkPath), ".apk",
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("BasePlayerApk must name an .apk file.");
            string entryRoot = "assets/" + baseMetaVersionAssetRoot.Trim('/');
            string runtimeRoot = runtimeAssetRoot.Trim('/');
            int separator = runtimeRoot.LastIndexOf('/');
            if (separator <= 0)
                throw new DheException(
                    "Android RuntimeAssetRoot must have a parent for build-identity.json.");
            string identityEntryName = "assets/" + runtimeRoot[..separator] +
                "/build-identity.json";
            string materializedRoot = Path.Combine(Path.GetTempPath(),
                "hybridclr-dhe-apk-base-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(materializedRoot);
            try
            {
                using ZipArchive apk = ZipFile.OpenRead(apkPath);
                ZipArchiveEntry[] identityEntries = apk.Entries.Where(entry =>
                    string.Equals(entry.FullName, identityEntryName,
                        StringComparison.Ordinal)).ToArray();
                if (identityEntries.Length != 1)
                    throw new DheException("Android Base APK must contain exactly one " +
                        identityEntryName + ".");
                byte[] embeddedIdentity = ReadZipEntry(identityEntries[0]);
                if (!string.Equals(Sha256Bytes(embeddedIdentity),
                        Sha256File(buildIdentityPath), StringComparison.OrdinalIgnoreCase))
                    throw new DheException(
                        "Android Base APK build identity differs from BaseBuildIdentity.");

                string prefix = entryRoot + "/";
                ZipArchiveEntry[] entries = apk.Entries.Where(entry =>
                    entry.FullName.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                if (entries.Length == 0)
                    throw new DheException(
                        "Android Base APK contains no embedded Base MetaVersion files.");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ZipArchiveEntry entry in entries)
                {
                    string relative = entry.FullName[prefix.Length..];
                    if (!IsPortableRelativePath(relative) || relative.Contains('/') ||
                        !relative.EndsWith(".mv.bytes", StringComparison.OrdinalIgnoreCase) ||
                        !names.Add(relative))
                        throw new DheException(
                            "Android Base APK contains an invalid Base MetaVersion entry: " +
                            entry.FullName);
                    File.WriteAllBytes(Path.Combine(materializedRoot, relative),
                        ReadZipEntry(entry));
                }
                return new MaterializedAndroidApkBase(apkPath, Sha256File(apkPath),
                    entryRoot, materializedRoot);
            }
            catch
            {
                Directory.Delete(materializedRoot, true);
                throw;
            }
        }

        private static byte[] ReadZipEntry(ZipArchiveEntry entry)
        {
            if (entry.Length <= 0 || entry.Length > int.MaxValue)
                throw new DheException("Android Base APK entry has an invalid size: " +
                    entry.FullName);
            using Stream input = entry.Open();
            using var output = new MemoryStream(checked((int)entry.Length));
            input.CopyTo(output);
            return output.ToArray();
        }

        public void Dispose()
        {
            if (Directory.Exists(MaterializedRoot))
                Directory.Delete(MaterializedRoot, true);
        }
    }

    /// <summary>
    /// Binds a resource-only update and its real Player smoke back to the
    /// immutable Base workflow. This replaces the obsolete changed-Player
    /// rebuild as the changed lane consumed by toolchain release evidence.
    /// </summary>
    private static int ResourcePlayerEvidence(Cli cli)
    {
        string updateRoot = RequireDirectory(cli.Require("resourceupdateroot"),
            "DHE resource update root");
        string manifestPath = RequireFile(Path.Combine(updateRoot, "dhe-resource-update.json"),
            "DHE resource update manifest");
        JsonElement manifest = ReadJson<JsonElement>(manifestPath);
        RequireEvidenceFormat(manifest, "hybridclr.dhe-resource-update.json",
            "Resource update manifest");
        ReleaseLedgerDocument? releaseLedger = ValidateReleaseLedgerForStaging(updateRoot,
            manifest);
        string validationPath = ValidateResourceUpdateCompatibility(updateRoot, manifest);
        JsonElement validation = ReadJson<JsonElement>(validationPath);
        string runtimePlanPath = RequireFile(ResolveContainedPath(updateRoot,
            GetString(manifest, "runtimePlan") ?? string.Empty, "DHE resource runtime plan"),
            "DHE resource runtime plan");

        string stagePath = RequireFile(cli.Require("stagereport"), "DHE resource stage report");
        JsonElement stage = ReadJson<JsonElement>(stagePath);
        RequireEvidenceFormat(stage, "hybridclr.dhe-resource-stage.json", "Resource stage");
        string playerPath = RequireFile(cli.Require("playerresult"), "DHE resource Player result");
        JsonElement player = ReadJson<JsonElement>(playerPath);
        RequireEvidenceFormat(player, "hybridclr.dhe-player-result.json", "Resource Player");
        string baseWorkflowPath = RequireFile(cli.Require("baseworkflowreport"),
            "DHE Base workflow report");
        JsonElement baseWorkflow = ReadJson<JsonElement>(baseWorkflowPath);
        RequireEvidenceFormat(baseWorkflow, "hybridclr.dhe-project-player-workflow.json",
            "Base workflow");
        string? baseArchiveManifestPath = ValidateBaseArchiveForWorkflow(baseWorkflowPath);

        var errors = new List<string>();
        if (!GetBool(validation, "passed") || !GetBool(stage, "passed") ||
            !GetBool(baseWorkflow, "passed") || !GetBool(player, "passed"))
            errors.Add("Resource update, stage, Base workflow, and Player must all pass.");
        if (string.Equals(GetString(baseWorkflow, "mode"), "Release",
                StringComparison.Ordinal) && releaseLedger == null)
            errors.Add("Release Base evidence requires a Release-ready resource ledger.");
        try { ValidateNoOpPlayerEvidence(baseWorkflow.GetProperty("player")); }
        catch (Exception ex) { errors.Add("Base workflow is not a complete no-op proof: " + ex.Message); }

        string selectedBaseId = GetString(stage, "selectedBaseId") ?? string.Empty;
        string selectedAotMetadataSetId = GetString(stage,
            "selectedAotMetadataSetId") ?? string.Empty;
        string selectedVariantId = GetString(stage, "payloadVariantId") ?? "default";
        JsonElement selectedBase = manifest.GetProperty("supportedBases").EnumerateArray()
            .SingleOrDefault(item => string.Equals(GetString(item, "baseId"), selectedBaseId,
                StringComparison.OrdinalIgnoreCase));
        if (selectedBase.ValueKind == JsonValueKind.Undefined || !GetBool(selectedBase, "compatible"))
            errors.Add("Resource stage did not select one compatible Base record.");

        JsonElement selectedManifestVariant = SelectPayloadVariant(manifest, selectedVariantId,
            "Resource update manifest");
        JsonElement selectedValidationVariant = SelectPayloadVariant(validation, selectedVariantId,
            "Resource update validation");
        string currentSet = GetString(selectedManifestVariant, "currentAssemblySetSha256") ??
            GetString(manifest, "currentAssemblySetSha256") ?? string.Empty;
        string target = GetString(player, "target") ?? string.Empty;
        string? payloadSelectionError = PlayerPayloadSelectionError(player, manifest,
            validation, selectedVariantId, currentSet);
        if (!string.Equals(Path.GetFullPath(GetString(stage, "updateRoot") ?? string.Empty),
                Path.GetFullPath(updateRoot), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(stage, "currentAssemblySetSha256"), currentSet,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(selectedValidationVariant, "currentAssemblySetSha256") ??
                GetString(validation, "currentAssemblySetSha256"), currentSet,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(selectedBase, "target"), target, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(selectedBase, "aotMetadataSetId"),
                selectedAotMetadataSetId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(selectedBase, "payloadVariantId") ?? "default",
                selectedVariantId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(selectedBase, "currentAssemblySetSha256"), currentSet,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(player, "selectedBaseId"), selectedBaseId,
                StringComparison.OrdinalIgnoreCase) ||
            !OptionalJsonPropertiesEqual(stage, manifest, "baseRegistrySha256") ||
            !OptionalJsonPropertiesEqual(stage, manifest, "baseRegistryEntryCount") ||
            !OptionalJsonPropertiesEqual(stage, manifest, "baseRegistryId") ||
            !OptionalJsonPropertiesEqual(stage, manifest, "baseRegistryRevision") ||
            !OptionalJsonPropertiesEqual(stage, manifest, "baseRegistryParentSha256") ||
            !OptionalJsonPropertiesEqual(stage, manifest,
                "baseRegistryRetiredBaseCount") ||
            !OptionalJsonPropertiesEqual(stage, manifest,
                "baseRegistryLineageValidated") ||
            !OptionalJsonPropertiesEqual(stage, manifest, "mode") ||
            !OptionalJsonPropertiesEqual(stage, manifest, "releaseReady") ||
            !OptionalJsonPropertiesEqual(stage, manifest, "releaseChannelId") ||
            !OptionalJsonPropertiesEqual(stage, manifest, "releaseRevision") ||
            !OptionalJsonPropertiesEqual(stage, manifest,
                "parentReleaseLedgerSha256") ||
            payloadSelectionError is not null)
            errors.Add(payloadSelectionError ??
                "Resource, stage, Base, and Player selection identities do not agree.");

        string stagedManifest = RequireFile(GetString(stage, "stagedManifestPath") ?? string.Empty,
            "Staged resource manifest");
        string stagedValidation = RequireFile(GetString(stage, "stagedValidationPath") ?? string.Empty,
            "Staged resource validation");
        string stagedPlan = RequireFile(GetString(stage, "stagedPlanPath") ?? string.Empty,
            "Staged runtime plan");
        if (!Sha256File(stagedManifest).Equals(Sha256File(manifestPath),
                StringComparison.OrdinalIgnoreCase) ||
            !Sha256File(stagedValidation).Equals(Sha256File(validationPath),
                StringComparison.OrdinalIgnoreCase) ||
            !Sha256File(stagedPlan).Equals(Sha256File(runtimePlanPath),
                StringComparison.OrdinalIgnoreCase) ||
            !Sha256File(stagedPlan).Equals(GetString(stage, "stagedPlanSha256"),
                StringComparison.OrdinalIgnoreCase) ||
            !Sha256File(runtimePlanPath).Equals(GetString(manifest, "runtimePlanSha256"),
                StringComparison.OrdinalIgnoreCase))
            errors.Add("Staged resource manifest, validation, or runtime plan bytes drifted.");

        string? stagedReleaseLedger = null;
        if (releaseLedger != null)
        {
            stagedReleaseLedger = RequireFile(GetString(stage,
                "stagedReleaseLedgerPath") ?? string.Empty, "Staged release ledger");
            if (!Sha256File(stagedReleaseLedger).Equals(releaseLedger.Sha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !releaseLedger.Sha256.Equals(GetString(stage,
                    "releaseLedgerSha256"), StringComparison.OrdinalIgnoreCase))
                errors.Add("Staged release ledger bytes drifted.");
        }

        string buildIdentityPath = RequireFile(GetString(stage, "baseBuildIdentityPath") ?? string.Empty,
            "Staged Base build identity");
        string workflowIdentityPath = ResolveEvidencePath(GetString(baseWorkflow, "buildIdentity"),
            Path.GetDirectoryName(baseWorkflowPath)!, "Base workflow build identity");
        string nativeManifestPath = ResolveBaseWorkflowNativeManifest(baseWorkflow,
            baseWorkflowPath);
        JsonElement nativeManifest = ReadJson<JsonElement>(nativeManifestPath);
        if (!Sha256File(buildIdentityPath).Equals(GetString(stage, "baseBuildIdentitySha256"),
                StringComparison.OrdinalIgnoreCase) ||
            !Sha256File(buildIdentityPath).Equals(Sha256File(workflowIdentityPath),
                StringComparison.OrdinalIgnoreCase) ||
            !Sha256File(buildIdentityPath).Equals(GetString(selectedBase, "buildIdentitySha256"),
                StringComparison.OrdinalIgnoreCase) ||
            !Sha256File(nativeManifestPath).Equals(GetString(selectedBase, "nativeManifestSha256"),
                StringComparison.OrdinalIgnoreCase) ||
            !Sha256File(nativeManifestPath).Equals(GetString(player, "nativeManifestSha256"),
                StringComparison.OrdinalIgnoreCase))
            errors.Add("Base build identity or native manifest is not bound to the selected resource record.");

        string? devicePlayerRunPath = null;
        string? requestedDevicePlayerRun = cli.Optional("deviceplayerrun");
        if (string.Equals(target, "Android", StringComparison.Ordinal))
        {
            devicePlayerRunPath = RequireFile(cli.Require("deviceplayerrun"),
                "Android device Player run");
            ValidateDevicePlayerRunBindings(devicePlayerRunPath, updateRoot, manifestPath,
                releaseLedger, stagePath, stage, playerPath, player, buildIdentityPath);
        }
        else if (!string.IsNullOrWhiteSpace(requestedDevicePlayerRun))
        {
            throw new DheException(
                "DevicePlayerRun is currently supported only for Android Player evidence.");
        }

        string[] assemblyNames = selectedManifestVariant.GetProperty("assemblies").EnumerateArray()
            .Select(item => GetString(item, "assemblyName") ?? string.Empty)
            .Where(name => name.Length > 0).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] plannedNames = player.GetProperty("plannedDheAssemblies").EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] loadedNames = player.GetProperty("loadedDheAssemblies").EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] differentialNames = ReadPlayerAssemblyNameArray(player,
            "plannedDifferentialAssemblies", errors);
        string[] interpreterOnlyNames = ReadPlayerAssemblyNameArray(player,
            "plannedInterpreterOnlyAssemblies", errors);
        string[] loadedInterpreterOnlyNames = ReadPlayerAssemblyNameArray(player,
            "loadedInterpreterOnlyAssemblies", errors);
        if (!assemblyNames.SequenceEqual(plannedNames, StringComparer.OrdinalIgnoreCase) ||
            !assemblyNames.SequenceEqual(loadedNames, StringComparer.OrdinalIgnoreCase))
            errors.Add("Resource manifest and Player assembly scopes do not agree.");
        ValidatePlayerAssemblyModes(selectedBase, selectedManifestVariant,
            differentialNames, interpreterOnlyNames, loadedInterpreterOnlyNames, errors);
        ValidatePlayerAssemblies(player, assemblyNames, errors);

        int expectedChanged = selectedBase.ValueKind == JsonValueKind.Undefined ? 0 :
            CountResourceChangedMethods(selectedBase);
        ValidateResourcePlayerExecution(player, expectedChanged,
            interpreterOnlyNames.Length, errors, ReadResourceDispatchAssemblies(updateRoot,
                selectedManifestVariant, selectedBase, baseWorkflow, baseWorkflowPath,
                ReadJson<JsonElement>(buildIdentityPath)));

        if (!GetBool(stage, "baseMetaVersionUnchanged") ||
            stage.GetProperty("immutableFiles").EnumerateArray().Any(item =>
                !string.Equals(GetString(item, "sha256Before"), GetString(item, "sha256After"),
                    StringComparison.OrdinalIgnoreCase)))
            errors.Add("Resource stage did not preserve immutable Base files.");
        if (errors.Count > 0) throw new DheException(string.Join(" ", errors));

        string sourcePreflightPath = ResolveBaseWorkflowReference(baseWorkflow,
            baseWorkflowPath, "sourcePreflight", "Base workflow source preflight");
        string cleanCheckoutGatePath = ResolveBaseWorkflowReference(baseWorkflow,
            baseWorkflowPath, "cleanCheckoutGate", "Base workflow clean checkout gate");
        string toolchainGatePath = ResolveBaseWorkflowReference(baseWorkflow,
            baseWorkflowPath, "toolchainGate", "Base workflow toolchain gate");
        string runtimeSourcePath = ResolveBaseWorkflowReference(baseWorkflow,
            baseWorkflowPath, "runtimeSource", "Base workflow runtime manifest");

        int methodCount = selectedBase.GetProperty("assemblies").EnumerateArray().Sum(item =>
            GetInt(item, "unchangedMethodCount") + GetInt(item, "changedMethodCount") +
            GetInt(item, "addedMethodCount"));
        int typeChangeCount = selectedBase.GetProperty("assemblies").EnumerateArray().Sum(item =>
            GetInt(item, "changedExistingTypeCount") + GetInt(item, "addedTypeCount") +
            GetInt(item, "removedTypeCount"));
        int guardedMethodCount = selectedBase.GetProperty("assemblies").EnumerateArray()
            .Sum(item => GetInt(item, "guardCoveredMethodCount"));
        var evidenceInputs = new List<string>
        {
            manifestPath, validationPath, runtimePlanPath, stagePath, playerPath, baseWorkflowPath,
            buildIdentityPath, nativeManifestPath,
        };
        if (releaseLedger != null) evidenceInputs.Add(releaseLedger.SourcePath);
        if (devicePlayerRunPath != null) evidenceInputs.Add(devicePlayerRunPath);
        var output = SafeReportPath(cli.Require("output"), evidenceInputs);
        WriteJson(output, new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-resource-player-workflow.json",
            generatedAtUtc = DateTimeOffset.UtcNow,
            passed = true,
            validationPassed = true,
            resourceReleaseMode = GetString(manifest, "mode") ?? "Exploratory",
            resourceReleaseReady = releaseLedger != null,
            releaseChannelId = releaseLedger?.ChannelId,
            releaseRevision = releaseLedger?.Revision,
            parentReleaseLedgerSha256 = releaseLedger?.ParentLedgerSha256,
            releaseLedger = releaseLedger?.SourcePath,
            releaseLedgerSha256 = releaseLedger?.Sha256,
            stagedReleaseLedger,
            target,
            engineWorkflow = GetString(selectedBase, "engineWorkflow"),
            il2cppCodeGeneration = GetString(selectedBase, "il2cppCodeGeneration"),
            mode = GetString(baseWorkflow, "mode"),
            coverageRequired = true,
            coverageGatePassed = true,
            releaseReady = ResourcePlayerReleaseReady(baseWorkflow) && releaseLedger != null,
            artifactValidationPassed = true,
            buildIdentityReady = true,
            identityVersion = 1,
            aotSnapshotKind = GetString(player, "aotSnapshotKind"),
            nativeGuardSourceSha256 = GetString(player, "nativeGuardSourceSha256"),
            nativeManifestSha256 = GetString(player, "nativeManifestSha256"),
            pathSemantics = "workspace-absolute-v1",
            projectPlan = manifestPath,
            projectPlanValidation = validationPath,
            batchReport = validationPath,
            runtimePlan = runtimePlanPath,
            runtimePlanProjectPath = stagedPlan,
            sourcePreflight = sourcePreflightPath,
            cleanCheckoutGate = cleanCheckoutGatePath,
            toolchainGate = toolchainGatePath,
            expectedToolchainPackageId = GetString(baseWorkflow, "expectedToolchainPackageId"),
            transaction = new
            {
                status = GetString(player, "transactionStatus"),
                retryValidated = GetBool(player, "retryValidated"),
                retryAssemblyName = GetString(player, "retryAssemblyName"),
                retryFailure = GetString(player, "retryFailure"),
            },
            assemblyScope = new
            {
                strategy = "single-current-multibase-resource",
                aotAssemblies = assemblyNames,
                loadedDheAssemblies = loadedNames,
                differentialAssemblies = differentialNames,
                interpreterOnlyAssemblies = interpreterOnlyNames,
                loadedInterpreterOnlyAssemblies = loadedInterpreterOnlyNames,
                stagedDependencies = Array.Empty<string>(),
                stagedDependenciesLoadedAsDhe = false,
                secondaryAssemblyChangedValidated = GetBool(player,
                    "secondaryAssemblyChangedValidated"),
                secondaryAssemblyDirectValidated = GetBool(player,
                    "secondaryAssemblyDirectValidated"),
            },
            capability = new
            {
                methodCount,
                changedMethodCount = expectedChanged,
                typeChangeCount,
                compatibility = "compatible",
            },
            nativeGuardCoverage = new
            {
                manifestAvailable = true,
                changedMethodCount = expectedChanged,
                supportedChangedMethodCount = expectedChanged,
                unsupportedChangedMethodCount = 0,
                nativeEntryCount = GetInt(nativeManifest, "nativeEntryCount"),
                guardedMethodCount,
                complete = true,
            },
            player,
            playerResult = playerPath,
            nativeManifest = nativeManifestPath,
            buildIdentity = buildIdentityPath,
            resourceEvidence = stagePath,
            resourceBuildPolicy = "required",
            resourceUpdateManifest = manifestPath,
            resourceUpdateManifestSha256 = Sha256File(manifestPath),
            resourceUpdateValidation = validationPath,
            resourceUpdateValidationSha256 = Sha256File(validationPath),
            resourceStage = stagePath,
            resourceStageSha256 = Sha256File(stagePath),
            baseWorkflowReport = baseWorkflowPath,
            baseWorkflowReportSha256 = Sha256File(baseWorkflowPath),
            playerResultSha256 = Sha256File(playerPath),
            devicePlayerRun = devicePlayerRunPath,
            devicePlayerRunSha256 = devicePlayerRunPath == null
                ? null : Sha256File(devicePlayerRunPath),
            buildIdentitySha256 = Sha256File(buildIdentityPath),
            runtimePlanSha256 = Sha256File(runtimePlanPath),
            selectedBaseId,
            selectedAotMetadataSetId,
            selectedPayloadVariantId = selectedVariantId,
            selectedPayloadCurrentAssemblySetSha256 = currentSet,
            payloadVariantSetSha256 = GetString(manifest, "payloadVariantSetSha256"),
            currentAssemblySetSha256 = currentSet,
            baseRegistrySha256 = GetString(manifest, "baseRegistrySha256"),
            baseRegistryId = GetString(manifest, "baseRegistryId"),
            baseRegistryRevision = manifest.TryGetProperty("baseRegistryRevision",
                out JsonElement registryRevision) && registryRevision.ValueKind == JsonValueKind.Number
                ? registryRevision.GetInt32()
                : (int?)null,
            baseRegistryLineageValidated = GetBool(manifest,
                "baseRegistryLineageValidated"),
            artifactValidation = validationPath,
            archiveManifest = baseArchiveManifestPath,
            archiveManifestSha256 = baseArchiveManifestPath == null
                ? null : Sha256File(baseArchiveManifestPath),
            archiveGate = (string?)null,
            runtimeSource = runtimeSourcePath,
        });
        Console.WriteLine("DHE resource Player workflow evidence: " + output);
        return 0;
    }

    private static void ValidateDevicePlayerRunBindings(string deviceRunPath,
        string updateRoot, string manifestPath, ReleaseLedgerDocument? releaseLedger,
        string stagePath, JsonElement stage, string playerPath, JsonElement player,
        string buildIdentityPath)
    {
        JsonElement run = ReadJson<JsonElement>(deviceRunPath);
        RequireEvidenceFormat(run, "hybridclr.dhe-device-player-run.json",
            "Android device Player run");
        string apkPath = RequireFile(GetString(run, "apkPath") ?? string.Empty,
            "Android device run APK");
        string logcatPath = RequireFile(GetString(run, "logcat") ?? string.Empty,
            "Android device run logcat");
        string stagedAssetRoot = RequireDirectory(GetString(run,
            "stagedAssetRoot") ?? string.Empty, "Android staged asset root");
        AndroidStagedFile[] stagedFiles = ReadAndroidStagedFiles(stage, stagedAssetRoot);
        Dictionary<string, string> reportFiles = run.GetProperty("stagedFiles")
            .EnumerateArray().ToDictionary(item =>
                GetString(item, "path") ?? string.Empty,
                item => GetString(item, "sha256") ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
        bool fileSetMatches = reportFiles.Count == stagedFiles.Length &&
            stagedFiles.All(file => reportFiles.TryGetValue(file.RelativePath,
                out string? hash) && string.Equals(hash, file.Sha256,
                    StringComparison.OrdinalIgnoreCase));
        JsonElement[] immutableApkRecords = stage.GetProperty("immutableFiles")
            .EnumerateArray().Where(item => string.Equals(Path.GetFullPath(
                    GetString(item, "path") ?? string.Empty), apkPath,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        bool releaseMatches = releaseLedger != null &&
            string.Equals(Path.GetFullPath(GetString(run,
                    "releaseLedger") ?? string.Empty), releaseLedger.SourcePath,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(GetString(run, "releaseLedgerSha256"), releaseLedger.Sha256,
                StringComparison.OrdinalIgnoreCase);
        if (!GetBool(run, "passed") ||
            !string.Equals(GetString(run, "target"), "Android", StringComparison.Ordinal) ||
            !GetBool(run, "uniqueProcessId") || GetInt(run, "processId") <= 0 ||
            !GetBool(run, "processExited") || !GetBool(run, "playerPassed") ||
            !GetBool(run, "installValidated") ||
            !GetBool(run, "payloadPushValidated") ||
            !GetBool(run, "payloadRoundTripValidated") ||
            !GetBool(run, "launchValidated") ||
            !string.Equals(Path.GetFullPath(GetString(run,
                    "resourceUpdateRoot") ?? string.Empty), updateRoot,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(GetString(run,
                    "resourceUpdateManifest") ?? string.Empty), manifestPath,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(run, "resourceUpdateManifestSha256"),
                Sha256File(manifestPath), StringComparison.OrdinalIgnoreCase) ||
            !releaseMatches ||
            !string.Equals(Path.GetFullPath(GetString(run,
                    "stageReport") ?? string.Empty), stagePath,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(run, "stageReportSha256"), Sha256File(stagePath),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(GetString(run,
                    "playerResult") ?? string.Empty), playerPath,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(run, "playerResultSha256"), Sha256File(playerPath),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(GetString(run,
                    "baseBuildIdentity") ?? string.Empty), buildIdentityPath,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(run, "baseBuildIdentitySha256"),
                Sha256File(buildIdentityPath), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(run, "selectedBaseId"),
                GetString(stage, "selectedBaseId"), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(run, "selectedBaseId"),
                GetString(player, "selectedBaseId"), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(run, "selectedPayloadVariantId") ?? "default",
                GetString(stage, "payloadVariantId") ?? "default",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(run, "selectedCurrentAssemblySetSha256"),
                GetString(stage, "currentAssemblySetSha256"),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(GetString(stage,
                    "assetRoot") ?? string.Empty), stagedAssetRoot,
                StringComparison.OrdinalIgnoreCase) ||
            GetInt(run, "stagedFileCount") != stagedFiles.Length || !fileSetMatches ||
            !string.Equals(GetString(stage, "embeddedBaseSourceKind"), "android-apk",
                StringComparison.Ordinal) ||
            !string.Equals(Path.GetFullPath(GetString(stage,
                    "embeddedBaseArtifactPath") ?? string.Empty), apkPath,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(run, "apkSha256"), Sha256File(apkPath),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(stage, "embeddedBaseArtifactSha256"),
                Sha256File(apkPath), StringComparison.OrdinalIgnoreCase) ||
            immutableApkRecords.Length != 1 ||
            !string.Equals(GetString(immutableApkRecords[0], "sha256Before"),
                Sha256File(apkPath), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(immutableApkRecords[0], "sha256After"),
                Sha256File(apkPath), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(run, "logcatSha256"), Sha256File(logcatPath),
                StringComparison.OrdinalIgnoreCase))
            throw new DheException(
                "Android device Player run does not match its APK, stage, payload, or Player result.");
    }

    private static string ResolveBaseWorkflowReference(JsonElement baseWorkflow,
        string baseWorkflowPath, string property, string description)
    {
        return ResolveEvidencePath(GetString(baseWorkflow, property),
            Path.GetDirectoryName(baseWorkflowPath)!, description);
    }

    private static string? ValidateBaseArchiveForWorkflow(string baseWorkflowPath,
        string? explicitArchiveManifestPath = null, string? expectedArchiveManifestSha256 = null)
    {
        string workflow = RequireFile(baseWorkflowPath, "Archived Base workflow");
        string archiveManifestPath = explicitArchiveManifestPath == null
            ? Path.Combine(Path.GetDirectoryName(workflow)!, "dhe-archive-manifest.json")
            : RequireFile(explicitArchiveManifestPath, "DHE Base archive manifest");
        if (!File.Exists(archiveManifestPath))
        {
            if (explicitArchiveManifestPath != null)
                throw new DheException("DHE Base archive manifest was not found: " +
                    archiveManifestPath);
            return null;
        }

        archiveManifestPath = Path.GetFullPath(archiveManifestPath);
        if (!string.IsNullOrWhiteSpace(expectedArchiveManifestSha256) &&
            (!IsHex(expectedArchiveManifestSha256, 64, 64) ||
             !Sha256File(archiveManifestPath).Equals(expectedArchiveManifestSha256,
                 StringComparison.OrdinalIgnoreCase)))
            throw new DheException("DHE Base archive manifest hash does not match the resource evidence.");

        string archiveRoot = Path.GetDirectoryName(archiveManifestPath)!;
        JsonElement archiveManifest = ReadJson<JsonElement>(archiveManifestPath);
        RequireEvidenceFormat(archiveManifest, "hybridclr.dhe-archive-manifest.json",
            "DHE Base archive manifest");
        if (!GetBool(archiveManifest, "offlineReleaseRevalidated"))
            throw new DheException("DHE Base archive was not independently revalidated.");
        string archivedWorkflow = RequireFile(ResolveContainedPath(archiveRoot,
            GetString(archiveManifest, "workflowReport") ?? string.Empty,
            "Archived Base workflow"), "Archived Base workflow");
        if (!Path.GetFullPath(archivedWorkflow).Equals(Path.GetFullPath(workflow),
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("DHE Base archive does not bind the selected workflow report.");

        var records = archiveManifest.GetProperty("files").EnumerateArray().ToArray();
        if (records.Length != GetInt(archiveManifest, "fileCount"))
            throw new DheException("DHE Base archive file count does not match its index.");
        var indexedPaths = new HashSet<string>(StringComparer.Ordinal);
        var fileSetRecords = new List<string>();
        foreach (JsonElement record in records)
        {
            string relative = GetString(record, "path") ?? string.Empty;
            if (!IsPortableRelativePath(relative) || relative.Contains('\\') ||
                !indexedPaths.Add(relative))
                throw new DheException("DHE Base archive contains an unsafe or duplicate path: " +
                    relative);
            string path = RequireFile(ResolveContainedPath(archiveRoot, relative,
                "DHE Base archive file"), "DHE Base archive file");
            long size = GetLong(record, "size");
            string hash = GetString(record, "sha256") ?? string.Empty;
            if (new FileInfo(path).Length != size || !IsHex(hash, 64, 64) ||
                !Sha256File(path).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new DheException("DHE Base archive file hash or size is invalid: " + relative);
            fileSetRecords.Add(relative + "|" + size + "|" + hash);
        }
        string[] actualPaths = Directory.GetFiles(archiveRoot, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFullPath(path).Equals(archiveManifestPath,
                StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(archiveRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (!actualPaths.SequenceEqual(indexedPaths.OrderBy(path => path, StringComparer.Ordinal),
                StringComparer.Ordinal) ||
            !Sha256Text(string.Join("\n", fileSetRecords)).Equals(
                GetString(archiveManifest, "fileSetSha256"),
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("DHE Base archive file set does not match its manifest.");
        return archiveManifestPath;
    }

    private static bool ValidateResourceBaseArchive(JsonElement report, string reportPath)
    {
        if (!string.Equals(GetString(report, "format"),
                "hybridclr.dhe-resource-player-workflow.json", StringComparison.Ordinal))
            return false;
        string? archiveManifestValue = GetString(report, "archiveManifest");
        string? archiveManifestSha256 = GetString(report, "archiveManifestSha256");
        if (string.IsNullOrWhiteSpace(archiveManifestValue) &&
            string.IsNullOrWhiteSpace(archiveManifestSha256))
            return false;
        if (string.IsNullOrWhiteSpace(archiveManifestValue) ||
            string.IsNullOrWhiteSpace(archiveManifestSha256))
            throw new DheException("Resource Player Base archive identity is incomplete.");
        string reportRoot = Path.GetDirectoryName(reportPath)!;
        string baseWorkflowPath = ResolveEvidencePath(GetString(report,
            "baseWorkflowReport"), reportRoot, "Resource Base workflow");
        string archiveManifestPath = ResolveEvidencePath(archiveManifestValue,
            reportRoot, "Resource Base archive manifest");
        _ = ValidateBaseArchiveForWorkflow(baseWorkflowPath, archiveManifestPath,
            archiveManifestSha256);
        return true;
    }

    private static string ResolveBaseWorkflowNativeManifest(JsonElement baseWorkflow,
        string baseWorkflowPath)
    {
        string reportRoot = Path.GetDirectoryName(baseWorkflowPath)!;
        string nativeManifestPath = ResolveBaseWorkflowReference(baseWorkflow,
            baseWorkflowPath, "nativeManifest", "Base workflow native manifest");
        string expectedHash = GetString(baseWorkflow, "nativeManifestSha256") ??
            string.Empty;
        if (Sha256File(nativeManifestPath).Equals(expectedHash,
                StringComparison.OrdinalIgnoreCase))
            return nativeManifestPath;

        string archiveManifestPath = Path.Combine(reportRoot,
            "dhe-archive-manifest.json");
        if (!File.Exists(archiveManifestPath))
            return nativeManifestPath;

        JsonElement archiveManifest = ReadJson<JsonElement>(archiveManifestPath);
        RequireEvidenceFormat(archiveManifest,
            "hybridclr.dhe-archive-manifest.json", "DHE Base archive manifest");
        if (!GetBool(archiveManifest, "offlineReleaseRevalidated"))
            throw new DheException("DHE Base archive was not independently revalidated.");

        string archivedWorkflowPath = RequireFile(ResolveContainedPath(reportRoot,
            GetString(archiveManifest, "workflowReport") ?? string.Empty,
            "Archived Base workflow"), "Archived Base workflow");
        if (!Path.GetFullPath(archivedWorkflowPath).Equals(
                Path.GetFullPath(baseWorkflowPath), StringComparison.OrdinalIgnoreCase))
            throw new DheException(
                "DHE Base archive does not bind the selected workflow report.");

        string immutableRelative = GetString(archiveManifest,
            "immutableNativeManifest") ?? string.Empty;
        string immutablePath = RequireFile(ResolveContainedPath(reportRoot,
            immutableRelative, "Archived immutable native manifest"),
            "Archived immutable native manifest");
        string immutableHash = Sha256File(immutablePath);
        if (!IsHex(expectedHash, 64, 64) ||
            !immutableHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase) ||
            !immutableHash.Equals(GetString(archiveManifest,
                    "immutableNativeManifestSha256"),
                StringComparison.OrdinalIgnoreCase))
            throw new DheException(
                "Archived immutable native manifest hash does not match the Base workflow.");

        JsonElement[] fileRecords = archiveManifest.GetProperty("files")
            .EnumerateArray().Where(item => string.Equals(GetString(item, "path"),
                immutableRelative.Replace('\\', '/'), StringComparison.Ordinal)).ToArray();
        if (fileRecords.Length != 1 ||
            !immutableHash.Equals(GetString(fileRecords[0], "sha256"),
                StringComparison.OrdinalIgnoreCase) ||
            GetLong(fileRecords[0], "size") != new FileInfo(immutablePath).Length)
            throw new DheException(
                "DHE Base archive file index does not bind the immutable native manifest.");

        JsonElement immutableNative = ReadJson<JsonElement>(immutablePath);
        if (GetInt(immutableNative, "schemaVersion") != 1 ||
            GetInt(immutableNative, "resolverVersion") != 3)
            throw new DheException(
                "Archived immutable native manifest has an invalid schema or resolver version.");
        return immutablePath;
    }

    private static string[] ReadPlayerAssemblyNameArray(JsonElement player, string property,
        List<string> errors)
    {
        if (!player.TryGetProperty(property, out JsonElement values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            errors.Add("Resource Player " + property + " must be an array.");
            return Array.Empty<string>();
        }

        var names = new List<string>();
        foreach (JsonElement value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString()))
            {
                errors.Add("Resource Player " + property +
                    " contains a non-string or empty assembly name.");
                continue;
            }
            try
            {
                string rawName = value.GetString() ?? string.Empty;
                string name = NormalizeName(rawName);
                if (!string.Equals(rawName, name, StringComparison.Ordinal))
                    errors.Add("Resource Player " + property +
                        " contains a non-canonical assembly name: " + rawName);
                else
                    names.Add(name);
            }
            catch (Exception exception)
            {
                errors.Add("Resource Player " + property + ": " + exception.Message);
            }
        }
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            errors.Add("Resource Player " + property + " contains duplicate assembly names.");
        return names.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void ValidatePlayerAssemblyModes(JsonElement selectedBase,
        JsonElement selectedManifestVariant, string[] differentialNames,
        string[] interpreterOnlyNames, string[] loadedInterpreterOnlyNames,
        List<string> errors)
    {
        if (selectedBase.ValueKind == JsonValueKind.Undefined ||
            !selectedBase.TryGetProperty("assemblyModes", out JsonElement modeValues) ||
            modeValues.ValueKind != JsonValueKind.Array)
        {
            errors.Add("Selected resource Base has no assembly execution mode map.");
            return;
        }

        var expectedModes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement value in modeValues.EnumerateArray())
        {
            string name;
            try
            {
                string rawName = GetString(value, "assemblyName") ?? string.Empty;
                name = NormalizeName(rawName);
                if (!string.Equals(rawName, name, StringComparison.Ordinal))
                    throw new DheException("non-canonical assembly name: " + rawName);
            }
            catch (Exception exception)
            {
                errors.Add("Selected resource Base assembly mode: " + exception.Message);
                continue;
            }
            string mode = GetString(value, "executionMode") ?? string.Empty;
            if (!IsDheExecutionMode(mode) || !expectedModes.TryAdd(name, mode))
                errors.Add("Selected resource Base has an invalid or duplicate assembly mode: " +
                    name + "/" + mode);
        }

        string[] payloadNames = selectedManifestVariant.GetProperty("assemblies")
            .EnumerateArray().Select(item => NormalizeName(
                GetString(item, "assemblyName") ?? string.Empty))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (payloadNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != payloadNames.Length ||
            !new HashSet<string>(payloadNames, StringComparer.OrdinalIgnoreCase)
                .SetEquals(expectedModes.Keys))
            errors.Add("Selected resource Base mode map does not cover the payload assembly set.");

        string[] expectedDifferential = expectedModes.Where(pair =>
                string.Equals(pair.Value, "dhe-differential", StringComparison.Ordinal))
            .Select(pair => pair.Key).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] expectedInterpreterOnly = expectedModes.Where(pair =>
                string.Equals(pair.Value, "interpreter-only", StringComparison.Ordinal))
            .Select(pair => pair.Key).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!expectedDifferential.SequenceEqual(differentialNames,
                StringComparer.OrdinalIgnoreCase) ||
            !expectedInterpreterOnly.SequenceEqual(interpreterOnlyNames,
                StringComparer.OrdinalIgnoreCase))
            errors.Add("Resource Player planned assembly execution modes do not match the selected Base.");
        if (!expectedInterpreterOnly.SequenceEqual(loadedInterpreterOnlyNames,
                StringComparer.OrdinalIgnoreCase))
            errors.Add("Resource Player did not load the complete interpreter-only assembly set.");
    }

    private static void ValidateResourcePlayerAssemblyScope(JsonElement report,
        string[] manifestAssemblyNames, string[] playerPlannedNames,
        string[] playerLoadedNames, string[] differentialNames,
        string[] interpreterOnlyNames, string[] loadedInterpreterOnlyNames,
        JsonElement player, List<string> errors)
    {
        if (!report.TryGetProperty("assemblyScope", out JsonElement scope) ||
            scope.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Resource Player workflow assembly scope is missing.");
            return;
        }

        string[] reportedAssemblies = ReadPlayerAssemblyNameArray(scope,
            "aotAssemblies", errors);
        string[] reportedLoaded = ReadPlayerAssemblyNameArray(scope,
            "loadedDheAssemblies", errors);
        string[] reportedDifferential = ReadPlayerAssemblyNameArray(scope,
            "differentialAssemblies", errors);
        string[] reportedInterpreterOnly = ReadPlayerAssemblyNameArray(scope,
            "interpreterOnlyAssemblies", errors);
        string[] reportedLoadedInterpreterOnly = ReadPlayerAssemblyNameArray(scope,
            "loadedInterpreterOnlyAssemblies", errors);
        bool dependenciesEmpty = scope.TryGetProperty("stagedDependencies",
            out JsonElement dependencies) && dependencies.ValueKind == JsonValueKind.Array &&
            dependencies.GetArrayLength() == 0;
        if (!string.Equals(GetString(scope, "strategy"),
                "single-current-multibase-resource", StringComparison.Ordinal) ||
            !manifestAssemblyNames.SequenceEqual(playerPlannedNames,
                StringComparer.OrdinalIgnoreCase) ||
            !manifestAssemblyNames.SequenceEqual(playerLoadedNames,
                StringComparer.OrdinalIgnoreCase) ||
            !manifestAssemblyNames.SequenceEqual(reportedAssemblies,
                StringComparer.OrdinalIgnoreCase) ||
            !playerLoadedNames.SequenceEqual(reportedLoaded,
                StringComparer.OrdinalIgnoreCase) ||
            !differentialNames.SequenceEqual(reportedDifferential,
                StringComparer.OrdinalIgnoreCase) ||
            !interpreterOnlyNames.SequenceEqual(reportedInterpreterOnly,
                StringComparer.OrdinalIgnoreCase) ||
            !loadedInterpreterOnlyNames.SequenceEqual(reportedLoadedInterpreterOnly,
                StringComparer.OrdinalIgnoreCase) ||
            !dependenciesEmpty || GetBool(scope, "stagedDependenciesLoadedAsDhe") ||
            !GetBool(scope, "secondaryAssemblyChangedValidated") ||
            !GetBool(player, "secondaryAssemblyChangedValidated") ||
            !GetBool(scope, "secondaryAssemblyDirectValidated") ||
            !GetBool(player, "secondaryAssemblyDirectValidated"))
            errors.Add("Resource Player workflow assembly scope differs from its manifest or Player result.");
    }

    private static int CountResourceChangedMethods(JsonElement selectedBase) =>
        selectedBase.GetProperty("assemblies").EnumerateArray().Sum(item =>
            GetInt(item, "changedMethodCount") + GetInt(item, "removedMethodCount") +
            GetInt(item, "addedMethodCount"));

    private static IEnumerable<DhePlayerDispatch.AssemblyPair> ReadResourceDispatchAssemblies(
        string updateRoot, JsonElement variant, JsonElement selectedBase,
        JsonElement baseWorkflow, string baseWorkflowPath, JsonElement buildIdentity)
    {
        string planPath = ResolveBaseWorkflowReference(baseWorkflow, baseWorkflowPath,
            "projectPlan", "Base workflow project plan");
        var plan = ReadJson<JsonElement>(planPath);
        var baselineRecords = plan.GetProperty("assemblies").EnumerateArray()
            .ToDictionary(item => NormalizeName(GetString(item, "assemblyName") ?? ""),
                StringComparer.OrdinalIgnoreCase);
        var identityRecords = buildIdentity.GetProperty("assemblies").EnumerateArray()
            .ToDictionary(item => NormalizeName(GetString(item, "assemblyName") ?? ""),
                StringComparer.OrdinalIgnoreCase);
        var selectedRecords = selectedBase.GetProperty("assemblies").EnumerateArray()
            .ToDictionary(item => NormalizeName(GetString(item, "assemblyName") ?? ""),
                StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement record in variant.GetProperty("assemblies").EnumerateArray())
        {
            string name = NormalizeName(GetString(record, "assemblyName") ?? "");
            string currentPath = RequireFile(ResolveContainedPath(updateRoot,
                GetString(record, "dll") ?? "", "Current dispatch assembly"), "Current dispatch assembly");
            var current = MetaVersionSnapshot.Create(currentPath);
            if (current.AssemblyName != name ||
                !current.AssemblySha256.Equals(GetString(record, "dllSha256"),
                    StringComparison.OrdinalIgnoreCase) ||
                !Sha256Bytes(current.ToBinary()).Equals(GetString(record, "currentMetaVersionSha256"),
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("Mixed-call Current DLL/MV does not match the selected resource: " + name);
            MetaVersionSnapshot? baseline = null;
            if (identityRecords.TryGetValue(name, out var identity))
            {
                if (!baselineRecords.TryGetValue(name, out var baselineRecord) ||
                    !selectedRecords.TryGetValue(name, out var selected))
                    throw new DheException("Mixed-call Base assembly records are incomplete: " + name);
                string baselinePath = ResolveEvidencePath(GetString(baselineRecord, "baseline"),
                    Path.GetDirectoryName(planPath)!, "Base dispatch assembly");
                baseline = MetaVersionSnapshot.Create(baselinePath);
                string baseMvHash = Sha256Bytes(baseline.ToBinary());
                if (baseline.AssemblyName != name ||
                    !baseline.AssemblySha256.Equals(GetString(identity, "baselineSha256"),
                        StringComparison.OrdinalIgnoreCase) ||
                    !baseline.AssemblySha256.Equals(GetString(selected, "baselineAssemblySha256"),
                        StringComparison.OrdinalIgnoreCase) ||
                    !baseMvHash.Equals(GetString(identity, "baseMetaVersionSha256"),
                        StringComparison.OrdinalIgnoreCase) ||
                    !baseMvHash.Equals(GetString(selected, "baseMetaVersionSha256"),
                        StringComparison.OrdinalIgnoreCase))
                    throw new DheException("Mixed-call Base DLL/MV does not match the Player identity: " + name);
            }
            yield return new DhePlayerDispatch.AssemblyPair(baseline, current);
        }
    }

    private static void ValidateResourcePlayerExecution(JsonElement player, int expectedChanged,
        int interpreterOnlyAssemblyCount, List<string> errors,
        IEnumerable<DhePlayerDispatch.AssemblyPair>? dispatchAssemblies = null)
    {
        if (expectedChanged < 0 || interpreterOnlyAssemblyCount < 0)
        {
            errors.Add("Resource Player evidence contains an invalid change count.");
            return;
        }
        if (GetInt(player, "changedMethodCount") != expectedChanged ||
            GetInt(player, "expectedChangedMethodCount") != expectedChanged ||
            GetInt(player, "aotEntryCount") <= 0 ||
            !GetBool(player, "resourceUpdateManifestPresent") ||
            !GetBool(player, "resourceUpdateValidated") ||
            !GetBool(player, "dispatchProbeValidated") ||
            !GetBool(player, "multiAssemblyValidated"))
        {
            errors.Add("Resource Player did not prove its selected update and unchanged AOT path.");
            return;
        }

        if (expectedChanged > 0)
        {
            try { DhePlayerDispatch.Validate(player, dispatchAssemblies ??
                Array.Empty<DhePlayerDispatch.AssemblyPair>()); }
            catch (Exception exception) { errors.Add("Changed dispatch: " + exception.Message); }
            if (GetInt(player, "interpreterEntryCount") <= 0 ||
                !GetBool(player, "capabilityPassed") ||
                !GetBool(player, "secondaryAssemblyChangedValidated") ||
                !GetBool(player, "structuralPassed") || !GetBool(player, "retryValidated") ||
                GetString(player, "transactionStatus") != "validated" ||
                GetString(player, "retryFailure") != "DHE_MV_REGISTRATION_FAILED")
                errors.Add("Resource Player did not prove changed interpreter/AOT dispatch, " +
                    "structure, and rollback.");
            return;
        }

        try
        {
            ValidateNoOpPlayerEvidence(player);
        }
        catch (Exception exception)
        {
            errors.Add("No-op resource update did not preserve Base AOT behavior: " +
                exception.Message);
        }
    }

    private static bool ResourcePlayerReleaseReady(JsonElement baseWorkflow) =>
        string.Equals(GetString(baseWorkflow, "mode"), "Release", StringComparison.Ordinal) &&
        GetBool(baseWorkflow, "releaseReady");

    private static JsonElement ValidateStagingBuildIdentity(string identityPath,
        JsonElement identity, JsonElement manifest)
    {
        if (GetInt(identity, "schemaVersion") != 1 ||
            !string.Equals(GetString(identity, "format"),
                "hybridclr.dhe-build-identity.json", StringComparison.Ordinal) ||
            GetInt(identity, "identityVersion") != 1 ||
            !string.Equals(GetString(identity, "state"), "staged-for-final-player",
                StringComparison.Ordinal) ||
            !string.Equals(GetString(identity, "aotSnapshotKind"),
                "managed-assembly-plus-generated-cpp-v1", StringComparison.Ordinal))
            throw new DheException("Base Player build identity contract is invalid.");

        string baseId = GetString(identity, "baseId") ?? string.Empty;
        string target = GetString(identity, "target") ?? string.Empty;
        string engineWorkflow = GetString(identity, "engineWorkflow") ?? string.Empty;
        string il2cppCodeGeneration = GetString(identity, "il2cppCodeGeneration") ?? string.Empty;
        string managedSet = GetString(identity, "managedAssemblySetSha256") ?? string.Empty;
        string aotAssemblySet = GetString(identity, "aotAssemblySetSha256") ?? string.Empty;
        string[] aotAssemblyNames = ReadAotAssemblyNames(identity,
            "Base Player build identity");
        string snapshot = GetString(identity, "aotSnapshotSha256") ?? string.Empty;
        string? analysisSnapshot = GetString(identity, "aotAnalysisSnapshotSha256");
        string baseMetaVersionSet = GetString(identity, "baseMetaVersionSetSha256") ?? string.Empty;
        string aotMetadataSetId = GetString(identity, "aotMetadataSetId") ?? string.Empty;
        string guard = GetString(identity, "nativeGuardSourceSha256") ?? string.Empty;
        string nativeManifest = GetString(identity, "nativeManifestSha256") ?? string.Empty;
        string runtimeProtocol = GetString(identity, "runtimeProtocol") ?? string.Empty;
        string runtimeContract = GetString(identity, "runtimeContract") ?? string.Empty;
        string runtimeAssetRoot = RequirePortableAssetRoot(
            GetString(identity, "runtimeAssetRoot"), "identity runtimeAssetRoot");
        string baseMetaVersionAssetRoot = RequirePortableAssetRoot(
            GetString(identity, "baseMetaVersionAssetRoot"),
            "identity baseMetaVersionAssetRoot");
        string[] runtimeCapabilities = ReadRuntimeCapabilities(identity,
            "runtimeCapabilities");
        if (target.Length == 0 || target.Any(character => !(char.IsLetterOrDigit(character) ||
                character is '.' or '_' or '-')) ||
            !string.Equals(il2cppCodeGeneration,
                ExpectedIl2CppCodeGeneration(engineWorkflow), StringComparison.Ordinal) ||
            !IsHex(baseId, 64, 64) || !IsHex(managedSet, 64, 64) ||
            !IsHex(aotAssemblySet, 64, 64) ||
            !IsHex(snapshot, 64, 64) || !IsHex(baseMetaVersionSet, 64, 64) ||
            (analysisSnapshot != null && !IsHex(analysisSnapshot, 64, 64)) ||
            !IsHex(aotMetadataSetId, 64, 64) ||
            !IsHex(guard, 64, 64) || !IsHex(nativeManifest, 64, 64) ||
            !string.Equals(runtimeProtocol, ResourceUpdateCompatibility.RuntimeProtocol,
                StringComparison.Ordinal) || string.IsNullOrWhiteSpace(runtimeContract) ||
            !IsValidCapabilitySet(runtimeCapabilities))
            throw new DheException("Base Player build identity fields are invalid.");

        string computedBaseId = ComputeBaseId(target, engineWorkflow,
            il2cppCodeGeneration, managedSet, aotAssemblySet, snapshot,
            baseMetaVersionSet, aotMetadataSetId, guard, nativeManifest, runtimeProtocol, runtimeContract,
            runtimeCapabilities, runtimeAssetRoot, baseMetaVersionAssetRoot, analysisSnapshot);
        if (!string.Equals(baseId, computedBaseId, StringComparison.OrdinalIgnoreCase))
            throw new DheException("Base Player build identity composite baseId is invalid.");

        if (!manifest.TryGetProperty("supportedBases", out JsonElement supportedBases) ||
            supportedBases.ValueKind != JsonValueKind.Array)
            throw new DheException("Resource update supported Base records are missing.");
        JsonElement[] matches = supportedBases.EnumerateArray().Where(candidate =>
            string.Equals(GetString(candidate, "baseId"), baseId,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
            throw new DheException(
                "Base Player build identity does not uniquely match a supported Base ID.");

        JsonElement selected = matches[0];
        string identitySha256 = Sha256File(identityPath);
        string[] selectedCapabilities = ReadRuntimeCapabilities(selected,
            "runtimeCapabilities");
        if (!string.Equals(GetString(selected, "buildIdentitySha256"), identitySha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(selected, "target"), target,
                StringComparison.Ordinal) ||
            !string.Equals(GetString(selected, "engineWorkflow"), engineWorkflow,
                StringComparison.Ordinal) ||
            !string.Equals(GetString(selected, "il2cppCodeGeneration"),
                il2cppCodeGeneration, StringComparison.Ordinal) ||
            !string.Equals(GetString(selected, "managedAssemblySetSha256"), managedSet,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(selected, "aotAssemblySetSha256"), aotAssemblySet,
                StringComparison.OrdinalIgnoreCase) ||
            !new HashSet<string>(ReadAotAssemblyNames(selected,
                    "Selected resource update Base"), StringComparer.OrdinalIgnoreCase)
                .SetEquals(aotAssemblyNames) ||
            !string.Equals(GetString(selected, "aotSnapshotSha256"), snapshot,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(selected, "aotAnalysisSnapshotSha256"), analysisSnapshot,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(selected, "baseMetaVersionSetSha256"),
                baseMetaVersionSet, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(selected, "aotMetadataSetId"),
                aotMetadataSetId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(selected, "nativeGuardSourceSha256"), guard,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(selected, "nativeManifestSha256"), nativeManifest,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(selected, "runtimeProtocol"), runtimeProtocol,
                StringComparison.Ordinal) ||
            !string.Equals(GetString(selected, "nativeRuntimeContract"), runtimeContract,
                StringComparison.Ordinal) ||
            !new HashSet<string>(selectedCapabilities, StringComparer.Ordinal)
                .SetEquals(runtimeCapabilities) ||
            !string.Equals(RequirePortableAssetRoot(GetString(selected, "runtimeAssetRoot"),
                    "supported Base runtimeAssetRoot"), runtimeAssetRoot,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(RequirePortableAssetRoot(
                    GetString(selected, "baseMetaVersionAssetRoot"),
                    "supported Base baseMetaVersionAssetRoot"), baseMetaVersionAssetRoot,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(RequirePortableAssetRoot(GetString(manifest, "runtimeAssetRoot"),
                    "manifest runtimeAssetRoot"), runtimeAssetRoot,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(RequirePortableAssetRoot(
                    GetString(manifest, "baseMetaVersionAssetRoot"),
                    "manifest baseMetaVersionAssetRoot"), baseMetaVersionAssetRoot,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException(
                "Base Player build identity does not match its supported Base record.");
        return selected;
    }

    private static (string BaseId, string SetSha256) ValidateEmbeddedBaseMetaVersionSet(
        string embeddedBaseRoot, JsonElement manifest, JsonElement identity,
        JsonElement selectedBase)
    {
        if (!identity.TryGetProperty("assemblies", out JsonElement identityAssemblies) ||
            identityAssemblies.ValueKind != JsonValueKind.Array ||
            !selectedBase.TryGetProperty("assemblies", out JsonElement selectedAssemblies) ||
            selectedAssemblies.ValueKind != JsonValueKind.Array)
            throw new DheException("Base Player assembly identity records are missing.");
        if (!manifest.TryGetProperty("assemblies", out JsonElement assemblies) ||
            assemblies.ValueKind != JsonValueKind.Array || assemblies.GetArrayLength() == 0)
            throw new DheException("Resource update assembly records are missing.");
        string[] names = identityAssemblies.EnumerateArray().Select(assembly =>
                NormalizeName(GetString(assembly, "assemblyName") ?? string.Empty))
            .ToArray();
        string[] payloadNames = assemblies.EnumerateArray().Select(assembly =>
                NormalizeName(GetString(assembly, "assemblyName") ?? string.Empty)).ToArray();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length ||
            payloadNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != payloadNames.Length ||
            !new HashSet<string>(payloadNames, StringComparer.OrdinalIgnoreCase).IsSupersetOf(names))
            throw new DheException("Resource update assembly records are duplicated or omit a Base assembly.");
        var identityByName = identityAssemblies.EnumerateArray().ToDictionary(record =>
                NormalizeName(GetString(record, "assemblyName") ?? string.Empty),
            record => record, StringComparer.OrdinalIgnoreCase);
        var selectedByName = selectedAssemblies.EnumerateArray().ToDictionary(record =>
                NormalizeName(GetString(record, "assemblyName") ?? string.Empty),
            record => record, StringComparer.OrdinalIgnoreCase);
        if (identityByName.Count != names.Length ||
            !new HashSet<string>(names, StringComparer.OrdinalIgnoreCase)
                .SetEquals(identityByName.Keys) ||
            !new HashSet<string>(names, StringComparer.OrdinalIgnoreCase)
                .IsSubsetOf(selectedByName.Keys))
            throw new DheException(
                "Base Player assembly identity does not match the resource update set.");
        if (Directory.GetFiles(embeddedBaseRoot, "*.mv2.bytes",
                SearchOption.TopDirectoryOnly).Length != 0)
            throw new DheException(
                "Embedded Base MetaVersion root contains retired .mv2.bytes artifacts.");

        string[] actualFiles = Directory.GetFiles(embeddedBaseRoot, "*.mv.bytes",
            SearchOption.TopDirectoryOnly);
        string[] actualNames = actualFiles.Select(path =>
                Path.GetFileName(path)[..^".mv.bytes".Length])
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!new HashSet<string>(names, StringComparer.OrdinalIgnoreCase).SetEquals(actualNames) ||
            actualNames.Length != names.Length)
            throw new DheException(
                "Embedded Base MetaVersion assembly set does not match the resource update.");

        var records = new List<(string name, byte[] bytes)>();
        foreach (string name in names)
        {
            string path = RequireFile(Path.Combine(embeddedBaseRoot, name + ".mv.bytes"),
                name + " embedded Base MetaVersion");
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 60 ||
                !Encoding.ASCII.GetString(bytes, 0, 8).Equals(MetaVersionSnapshot.Magic,
                    StringComparison.Ordinal) ||
                BitConverter.ToUInt32(bytes, 8) != MetaVersionSnapshot.SchemaVersion ||
                BitConverter.ToUInt32(bytes, 12) != MetaVersionSnapshot.StrictFlag)
                throw new DheException("Embedded Base MetaVersion header is invalid: " + name);
            int nameLength = checked((int)BitConverter.ToUInt32(bytes, 16));
            if (nameLength <= 0 || nameLength > bytes.Length - 60 ||
                !Encoding.UTF8.GetString(bytes, 60, nameLength).Equals(name,
                    StringComparison.Ordinal))
                throw new DheException(
                    "Embedded Base MetaVersion assembly identity is invalid: " + name);
            string sha256 = Sha256Bytes(bytes);
            JsonElement identityAssembly = identityByName[name];
            JsonElement selectedAssembly = selectedByName[name];
            if (!string.Equals(GetString(identityAssembly, "baseMetaVersionSha256"), sha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(GetString(identityAssembly, "embeddedBaseMetaVersionSha256"),
                    sha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(GetString(selectedAssembly, "baseMetaVersionSha256"), sha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(GetString(identityAssembly, "baselineSha256"),
                    GetString(selectedAssembly, "baselineAssemblySha256"),
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException(
                    "Embedded Base MetaVersion is not bound to the Player identity: " + name);
            records.Add((name, bytes));
        }

        string setSha256 = NamedByteSetHash(records);
        if (!string.Equals(GetString(identity, "baseMetaVersionSetSha256"), setSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(selectedBase, "baseMetaVersionSetSha256"), setSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException(
                "Embedded Base MetaVersion set does not match the selected Player identity.");
        string baseId = GetString(identity, "baseId") ?? string.Empty;
        return (baseId, setSha256);
    }

    private static ResourcePayload[] ValidateResourceUpdatePayload(string updateRoot, JsonElement manifest,
        JsonElement runtimePlan, JsonElement selectedBase, string runtimeAssetRoot,
        string baseMetaVersionAssetRoot)
    {
        string variantId = GetString(selectedBase, "payloadVariantId") ?? "default";
        JsonElement manifestVariant = SelectPayloadVariant(manifest, variantId,
            "Resource update manifest");
        JsonElement planVariant = SelectPayloadVariant(runtimePlan, variantId,
            "DHE runtime plan");
        JsonElement assemblies = manifestVariant.TryGetProperty("assemblies", out var selectedAssemblies)
            ? selectedAssemblies
            : default;
        JsonElement planAssemblies = planVariant.TryGetProperty("assemblies", out var selectedPlanAssemblies)
            ? selectedPlanAssemblies
            : default;
        if (assemblies.ValueKind != JsonValueKind.Array || assemblies.GetArrayLength() == 0 ||
            planAssemblies.ValueKind != JsonValueKind.Array ||
            planAssemblies.GetArrayLength() != assemblies.GetArrayLength())
            throw new DheException("Resource update assembly records are missing or inconsistent.");

        string variantHash = GetString(manifestVariant, "currentAssemblySetSha256") ?? string.Empty;
        if (!IsHex(variantHash, 64, 64) ||
            !string.Equals(GetString(planVariant, "currentAssemblySetSha256"), variantHash,
                StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(GetString(selectedBase, "currentAssemblySetSha256")) &&
             !string.Equals(GetString(selectedBase, "currentAssemblySetSha256"), variantHash,
                 StringComparison.OrdinalIgnoreCase)))
            throw new DheException("Resource update payload variant hash is not bound to the selected Base.");

        var planByName = planAssemblies.EnumerateArray().ToDictionary(
            item => NormalizeName(GetString(item, "assemblyName") ?? string.Empty),
            item => item, StringComparer.OrdinalIgnoreCase);
        var selectedModes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (selectedBase.TryGetProperty("assemblyModes", out JsonElement assemblyModes) &&
            assemblyModes.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement modeRecord in assemblyModes.EnumerateArray())
            {
                string name = NormalizeName(GetString(modeRecord, "assemblyName") ?? string.Empty);
                string mode = GetString(modeRecord, "executionMode") ?? string.Empty;
                if (!IsDheExecutionMode(mode) || !selectedModes.TryAdd(name, mode))
                    throw new DheException("Resource update Base assembly modes are invalid: " + name);
            }
            if (selectedModes.Count != planByName.Count ||
                !selectedModes.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(planByName.Keys))
                throw new DheException("Resource update Base assembly modes do not match the payload.");
        }
        var payloads = new List<ResourcePayload>();
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in assemblies.EnumerateArray())
        {
            var name = NormalizeName(GetString(assembly, "assemblyName") ?? string.Empty);
            if (!planByName.TryGetValue(name, out var plan))
                throw new DheException("Runtime plan has no record for payload assembly: " + name);
            AddResourcePayload(updateRoot, GetString(assembly, "dll"), GetString(assembly, "dllSha256"),
                GetString(plan, "current"), runtimeAssetRoot, payloads, paths);
            AddResourcePayload(updateRoot, GetString(assembly, "currentMetaVersion"),
                GetString(assembly, "currentMetaVersionSha256"), GetString(plan, "currentMetaVersion"),
                runtimeAssetRoot, payloads, paths);
            string mode = selectedModes.TryGetValue(name, out string? selectedMode)
                ? selectedMode : GetString(plan, "executionMode") ?? "dhe-differential";
            if (selectedBase.TryGetProperty("assemblyModes", out JsonElement modeTable))
            {
                JsonElement modeRow = modeTable.EnumerateArray().Single(row =>
                    NormalizeName(GetString(row, "assemblyName") ?? "") == name);
                if (modeRow.TryGetProperty("executionPlan", out JsonElement executionValue) && executionValue.ValueKind != JsonValueKind.Null)
                {
                    var execution = executionValue.Deserialize<ResourceExecutionPlan>(Json)
                        ?? throw new DheException("Invalid resource execution plan: " + name);
                    execution.CanonicalBinding();
                    JsonElement baseAssembly = selectedBase.GetProperty("assemblies").EnumerateArray().Single(row =>
                        NormalizeName(GetString(row, "assemblyName") ?? "") == name);
                    if (execution.AssemblyName != name || mode != "dhe-differential" ||
                        !string.Equals(execution.BaseMetaVersionSha256, GetString(baseAssembly, "baseMetaVersionSha256"), StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(execution.CurrentMetaVersionSha256, GetString(assembly, "currentMetaVersionSha256"), StringComparison.OrdinalIgnoreCase))
                        throw new DheException("Resource execution plan MV binding mismatch: " + name);
                }
            }
            if (!IsDheExecutionMode(mode))
                throw new DheException("Runtime plan execution mode is invalid for " + name + ".");
            var expectedBasePath = baseMetaVersionAssetRoot + name + ".mv.bytes";
            string? basePath = GetString(plan, "baseMetaVersion");
            if (mode == "dhe-differential" && !string.Equals(basePath, expectedBasePath,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("Runtime plan Base MetaVersion path is invalid for " + name + ".");
            if (mode == "interpreter-only" && !string.IsNullOrWhiteSpace(basePath) &&
                !string.Equals(basePath, expectedBasePath, StringComparison.OrdinalIgnoreCase))
                throw new DheException("Interpreter-only Base MetaVersion path is invalid for " + name + ".");
        }

        if (selectedBase.TryGetProperty("frozenAotSources", out JsonElement frozenSources) &&
            frozenSources.ValueKind == JsonValueKind.Array && frozenSources.GetArrayLength() != 0)
        {
            JsonElement runtimeBase = runtimePlan.GetProperty("baseSelections").EnumerateArray().Single(row =>
                string.Equals(GetString(row, "baseId"), GetString(selectedBase, "baseId"), StringComparison.OrdinalIgnoreCase));
            JsonElement runtimeSources = runtimeBase.GetProperty("frozenAotSources");
            if (runtimeSources.GetArrayLength() != frozenSources.GetArrayLength())
                throw new DheException("Resource and runtime frozen AOT source records differ.");
            string snapshotRelative = "payload/frozen-aot/" +
                (GetString(selectedBase, "baseId") ?? string.Empty).ToLowerInvariant() + "/snapshot.json";
            AddResourcePayload(updateRoot, snapshotRelative, GetString(selectedBase, "aotAnalysisSnapshotSha256") ?? string.Empty,
                runtimeAssetRoot + snapshotRelative, runtimeAssetRoot, payloads, paths);
            foreach (JsonElement source in frozenSources.EnumerateArray())
            {
                string name = NormalizeName(GetString(source, "assemblyName") ?? string.Empty);
                string sourceAsset = GetString(source, "source") ?? string.Empty;
                string sourceHash = GetString(source, "sourceSha256") ?? string.Empty;
                string mvAsset = GetString(source, "baseMetaVersion") ?? string.Empty;
                string mvHash = GetString(source, "baseMetaVersionSha256") ?? string.Empty;
                string expectedPrefix = runtimeAssetRoot + "payload/frozen-aot/" +
                    (GetString(selectedBase, "baseId") ?? string.Empty).ToLowerInvariant() + "/" + name;
                JsonElement runtimeSource = runtimeSources.EnumerateArray().SingleOrDefault(row =>
                    string.Equals(GetString(row, "assemblyName"), name, StringComparison.OrdinalIgnoreCase));
                if (runtimeSource.ValueKind != JsonValueKind.Object ||
                    GetString(runtimeSource, "source") != sourceAsset ||
                    GetString(runtimeSource, "sourceSha256") != sourceHash ||
                    GetString(runtimeSource, "baseMetaVersion") != mvAsset ||
                    GetString(runtimeSource, "baseMetaVersionSha256") != mvHash ||
                    sourceAsset != expectedPrefix + ".dll.bytes" || mvAsset != expectedPrefix + ".mv.bytes" ||
                    sourceAsset.StartsWith(baseMetaVersionAssetRoot, StringComparison.OrdinalIgnoreCase) ||
                    mvAsset.StartsWith(baseMetaVersionAssetRoot, StringComparison.OrdinalIgnoreCase))
                    throw new DheException("Resource/runtime frozen AOT binding mismatch: " + name);
                AddResourcePayload(updateRoot, sourceAsset[runtimeAssetRoot.Length..], sourceHash,
                    sourceAsset, runtimeAssetRoot, payloads, paths);
                AddResourcePayload(updateRoot, mvAsset[runtimeAssetRoot.Length..], mvHash,
                    mvAsset, runtimeAssetRoot, payloads, paths);
            }
        }

        if (!runtimePlan.TryGetProperty("aotMetadata", out JsonElement legacyMetadata) ||
            legacyMetadata.ValueKind != JsonValueKind.Array || legacyMetadata.GetArrayLength() != 0 ||
            !manifest.TryGetProperty("aotMetadataSets", out JsonElement manifestSets) ||
            manifestSets.ValueKind != JsonValueKind.Array || manifestSets.GetArrayLength() == 0 ||
            !runtimePlan.TryGetProperty("aotMetadataSets", out JsonElement planSets) ||
            planSets.ValueKind != JsonValueKind.Array ||
            planSets.GetArrayLength() != manifestSets.GetArrayLength())
            throw new DheException("Resource update AOT metadata sets are missing or inconsistent.");
        var planSetsById = planSets.EnumerateArray().ToDictionary(item =>
            GetString(item, "aotMetadataSetId") ?? string.Empty, item => item,
            StringComparer.OrdinalIgnoreCase);
        if (planSetsById.Count != planSets.GetArrayLength())
            throw new DheException("Runtime plan AOT metadata sets are duplicated.");
        var validSetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement manifestSet in manifestSets.EnumerateArray())
        {
            string setId = GetString(manifestSet, "aotMetadataSetId") ?? string.Empty;
            if (!IsHex(setId, 64, 64) || !validSetIds.Add(setId) ||
                !planSetsById.TryGetValue(setId, out JsonElement planSet) ||
                !manifestSet.TryGetProperty("assemblies", out JsonElement manifestMetadata) ||
                manifestMetadata.ValueKind != JsonValueKind.Array ||
                !planSet.TryGetProperty("assemblies", out JsonElement planMetadata) ||
                planMetadata.ValueKind != JsonValueKind.Array ||
                manifestMetadata.GetArrayLength() != planMetadata.GetArrayLength())
                throw new DheException("Resource update AOT metadata set is invalid: " + setId);
            var metadataPlanByName = planMetadata.EnumerateArray().ToDictionary(item =>
                    NormalizeName(GetString(item, "assemblyName") ?? string.Empty), item => item,
                StringComparer.OrdinalIgnoreCase);
            if (metadataPlanByName.Count != planMetadata.GetArrayLength())
                throw new DheException("Runtime plan AOT metadata records are duplicated: " + setId);
            var manifestNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var setBytes = new List<(string name, byte[] bytes)>();
            foreach (JsonElement metadata in manifestMetadata.EnumerateArray())
            {
                string name = NormalizeName(GetString(metadata, "assemblyName") ?? string.Empty);
                if (name.Length == 0 || !manifestNames.Add(name) ||
                    !metadataPlanByName.TryGetValue(name, out JsonElement planRecord))
                    throw new DheException("Resource update AOT metadata record is invalid: " + name);
                string assetPath = GetString(metadata, "path") ?? string.Empty;
                if (!assetPath.StartsWith(runtimeAssetRoot + "payload/aot-metadata/",
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(GetString(planRecord, "path"), assetPath,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(GetString(planRecord, "sha256"),
                        GetString(metadata, "sha256"), StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(GetString(planRecord, "sourceKind"),
                        GetString(metadata, "sourceKind"), StringComparison.Ordinal) ||
                    !string.Equals(GetString(planRecord, "manifestSha256"),
                        GetString(metadata, "manifestSha256"), StringComparison.OrdinalIgnoreCase))
                    throw new DheException(
                        "Resource update AOT metadata is not bound to the runtime plan: " + name);
                string relativePath = assetPath[runtimeAssetRoot.Length..];
                AddResourcePayload(updateRoot, relativePath, GetString(metadata, "sha256"),
                    GetString(planRecord, "path"), runtimeAssetRoot, payloads, paths);
                setBytes.Add((name, File.ReadAllBytes(ResolveContainedPath(updateRoot, relativePath,
                    "DHE resource AOT metadata"))));
            }
            if (!string.Equals(NamedByteSetHash(setBytes), setId,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("Resource update AOT metadata set hash mismatch: " + setId);
        }

        if (!runtimePlan.TryGetProperty("baseSelections", out JsonElement selections) ||
            selections.ValueKind != JsonValueKind.Array ||
            !manifest.TryGetProperty("supportedBases", out JsonElement supportedBases) ||
            supportedBases.ValueKind != JsonValueKind.Array ||
            selections.GetArrayLength() != supportedBases.GetArrayLength())
            throw new DheException("Resource update Base metadata selections are missing.");
        var selectionsByBase = selections.EnumerateArray().ToDictionary(item =>
            GetString(item, "baseId") ?? string.Empty, item => item,
            StringComparer.OrdinalIgnoreCase);
        if (selectionsByBase.Count != selections.GetArrayLength())
            throw new DheException("Resource update Base metadata selections are duplicated.");
        foreach (JsonElement supportedBase in supportedBases.EnumerateArray())
        {
            string baseId = GetString(supportedBase, "baseId") ?? string.Empty;
            string setId = GetString(supportedBase, "aotMetadataSetId") ?? string.Empty;
            string baseVariantId = GetString(supportedBase, "payloadVariantId") ?? "default";
            JsonElement variant = SelectPayloadVariant(manifest, baseVariantId,
                "Resource update manifest");
            string[] supportedModes = CanonicalResourceAssemblyModes(supportedBase,
                variant, "Resource supported Base " + baseId);
            if (!IsHex(baseId, 64, 64) || !IsHex(setId, 64, 64) ||
                !validSetIds.Contains(setId) ||
                !selectionsByBase.TryGetValue(baseId, out JsonElement selection) ||
                !supportedModes.SequenceEqual(CanonicalResourceAssemblyModes(selection,
                    variant, "Runtime plan Base selection " + baseId),
                    StringComparer.OrdinalIgnoreCase) ||
                !CanonicalResourceFrozenSources(supportedBase).SequenceEqual(
                    CanonicalResourceFrozenSources(selection), StringComparer.Ordinal) ||
                !string.Equals(GetString(selection, "aotMetadataSetId"), setId,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(GetString(selection, "payloadVariantId") ?? "default",
                    GetString(supportedBase, "payloadVariantId") ?? "default",
                    StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(GetString(selection, "currentAssemblySetSha256")) &&
                 !string.Equals(GetString(selection, "currentAssemblySetSha256"),
                     GetString(supportedBase, "currentAssemblySetSha256"),
                     StringComparison.OrdinalIgnoreCase)))
                throw new DheException("Resource update Base metadata selection is invalid: " + baseId);
        }
        string selectedBaseId = GetString(selectedBase, "baseId") ?? string.Empty;
        if (selectedBase.TryGetProperty("assemblyModes", out JsonElement selectedModesElement) &&
            selectedModesElement.ValueKind == JsonValueKind.Array)
        {
            if (!selectionsByBase.TryGetValue(selectedBaseId, out JsonElement selectedSelection) ||
                !selectedSelection.TryGetProperty("assemblyModes", out JsonElement planModesElement) ||
                planModesElement.ValueKind != JsonValueKind.Array)
                throw new DheException("Resource update runtime plan is missing the selected Base assembly modes.");
            string[] manifestModes = selectedModesElement.EnumerateArray().Select(mode =>
                    NormalizeName(GetString(mode, "assemblyName") ?? string.Empty) + "=" +
                    (GetString(mode, "executionMode") ?? string.Empty))
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            string[] planModes = planModesElement.EnumerateArray().Select(mode =>
                    NormalizeName(GetString(mode, "assemblyName") ?? string.Empty) + "=" +
                    (GetString(mode, "executionMode") ?? string.Empty))
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            if (!manifestModes.SequenceEqual(planModes, StringComparer.OrdinalIgnoreCase))
                throw new DheException("Resource update runtime plan assembly modes do not match the selected Base.");
        }
        if (!string.Equals(GetString(selectedBase, "aotMetadataSetId"),
                GetString(selectionsByBase[GetString(selectedBase, "baseId") ?? string.Empty],
                    "aotMetadataSetId"), StringComparison.OrdinalIgnoreCase))
            throw new DheException("Selected Base AOT metadata set does not match the runtime plan.");
        return payloads.ToArray();
    }

    private static JsonElement SelectPayloadVariant(JsonElement document, string variantId,
        string description)
    {
        if (document.TryGetProperty("payloadVariants", out JsonElement variants) &&
            variants.ValueKind == JsonValueKind.Array)
        {
            JsonElement[] matches = variants.EnumerateArray().Where(item =>
                string.Equals(GetString(item, "variantId"), variantId,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                throw new DheException(description + " does not contain exactly one payload variant: " +
                    variantId);
            return matches[0];
        }
        if (!string.Equals(variantId, "default", StringComparison.OrdinalIgnoreCase))
            throw new DheException(description + " has no payload variant: " + variantId);
        return document;
    }

    private static bool IsImplicitSingleDefaultPayload(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object ||
            !document.TryGetProperty("payloadVariants", out JsonElement variants))
            return true;
        if (variants.ValueKind != JsonValueKind.Array || variants.GetArrayLength() != 1)
            return false;
        JsonElement only = variants.EnumerateArray().Single();
        return string.Equals(GetString(only, "variantId"), "default",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A pre-variant Player result may omit payload selection fields. That is
    /// unambiguous only when both authenticated documents expose one implicit
    /// default payload. Explicit variant releases must carry both fields.
    /// </summary>
    private static string? PlayerPayloadSelectionError(JsonElement player,
        JsonElement manifest, JsonElement validation, string expectedVariantId,
        string expectedCurrentSet)
    {
        bool requiresExplicitSelection =
            !string.Equals(GetString(manifest, "payloadModel"),
                "single-current-payload", StringComparison.Ordinal) ||
            !IsImplicitSingleDefaultPayload(manifest) ||
            !IsImplicitSingleDefaultPayload(validation);
        string? actualVariantId = GetString(player, "selectedPayloadVariantId");
        string? actualCurrentSet = GetString(player,
            "selectedPayloadCurrentAssemblySetSha256");
        if (actualVariantId is null && requiresExplicitSelection)
            return "Resource Player result must record selectedPayloadVariantId for a variant payload.";
        if (actualCurrentSet is null && requiresExplicitSelection)
            return "Resource Player result must record selectedPayloadCurrentAssemblySetSha256 for a variant payload.";
        if (!requiresExplicitSelection && (actualVariantId is null) !=
            (actualCurrentSet is null))
            return "Resource Player result must record both payload selection fields or omit both for a legacy single payload.";
        if (actualVariantId is not null &&
            !string.Equals(actualVariantId, expectedVariantId,
                StringComparison.OrdinalIgnoreCase))
            return "Resource Player result selected payload variant does not match the staged variant.";
        if (actualCurrentSet is not null &&
            !string.Equals(actualCurrentSet, expectedCurrentSet,
                StringComparison.OrdinalIgnoreCase))
            return "Resource Player result selected payload hash does not match the staged payload.";
        if (actualVariantId is null &&
            !string.Equals(expectedVariantId, "default", StringComparison.OrdinalIgnoreCase))
            return "Legacy Resource Player result can only infer the default payload variant.";
        return null;
    }

    private static string ValidateResourceUpdateCompatibility(string updateRoot, JsonElement manifest)
    {
        ValidateBaseRegistryAudit(updateRoot, manifest);
        var validationRelative = GetString(manifest, "validation") ?? string.Empty;
        var expectedValidationHash = GetString(manifest, "validationSha256");
        var validationPath = RequireFile(ResolveContainedPath(updateRoot, validationRelative,
            "DHE resource compatibility validation"), "DHE resource compatibility validation");
        if (!IsHex(expectedValidationHash, 64, 64) ||
            !string.Equals(Sha256File(validationPath), expectedValidationHash,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("DHE resource compatibility validation hash mismatch.");
        var validation = ReadJson<JsonElement>(validationPath);
        if (GetInt(validation, "schemaVersion") != 1 ||
            !string.Equals(GetString(validation, "format"),
                "hybridclr.dhe-resource-update-validation.json", StringComparison.Ordinal) ||
            !GetBool(validation, "passed") ||
            !string.Equals(GetString(validation, "compatibilityPolicy"),
                ResourceUpdateCompatibility.Policy, StringComparison.Ordinal) ||
            !string.Equals(GetString(validation, "runtimeProtocol"),
                ResourceUpdateCompatibility.RuntimeProtocol, StringComparison.Ordinal) ||
            !string.Equals(GetString(validation, "currentAssemblySetSha256"),
                GetString(manifest, "currentAssemblySetSha256"), StringComparison.OrdinalIgnoreCase))
            throw new DheException("DHE resource compatibility validation did not pass.");

        foreach (string property in new[]
        {
            "mode", "releaseReady", "releaseChannelId", "releaseRevision",
            "parentReleaseLedgerSha256", "releaseLedger",
            "baseRegistrySha256", "baseRegistryEntryCount", "baseRegistryAuditPath",
            "baseRegistryAuditSha256", "baseRegistryId", "baseRegistryRevision",
            "baseRegistryParentSha256", "baseRegistryParentAuditPath",
            "baseRegistryParentAuditSha256", "baseRegistryRetiredBaseCount",
            "baseRegistryLineageValidated"
        })
        {
            if (!OptionalJsonPropertiesEqual(validation, manifest, property))
                throw new DheException(
                    "DHE resource release or registry binding differs between manifest and validation: " +
                    property);
        }

        if (!manifest.TryGetProperty("supportedBases", out JsonElement supportedBases) ||
            supportedBases.ValueKind != JsonValueKind.Array || supportedBases.GetArrayLength() == 0 ||
            !validation.TryGetProperty("bases", out JsonElement validatedBases) ||
            validatedBases.ValueKind != JsonValueKind.Array ||
            validatedBases.GetArrayLength() != supportedBases.GetArrayLength())
            throw new DheException("DHE resource compatibility Base records are missing or inconsistent.");

        ValidatePayloadVariantSet(manifest, validation);

        var validatedById = validatedBases.EnumerateArray().ToDictionary(
            ResourceBaseIdentityKey, item => item,
            StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement supportedBase in supportedBases.EnumerateArray())
        {
            string baseId = GetString(supportedBase, "baseId") ?? string.Empty;
            string variantId = GetString(supportedBase, "payloadVariantId") ?? "default";
            JsonElement variant = SelectPayloadVariant(manifest, variantId,
                "Resource update manifest");
            string[] supportedModes = CanonicalResourceAssemblyModes(supportedBase,
                variant, "DHE resource supported Base " + baseId);
            string identityKey = ResourceBaseIdentityKey(supportedBase);
            string[] runtimeCapabilities = ReadRuntimeCapabilities(supportedBase,
                "runtimeCapabilities");
            string[] requiredRuntimeCapabilities = ReadRuntimeCapabilities(supportedBase,
                "requiredRuntimeCapabilities");
            string[] supportedAotAssemblies = ReadAotAssemblyNames(supportedBase,
                "DHE resource supported Base " + baseId);
            if (!IsHex(baseId, 64, 64) || !GetBool(supportedBase, "compatible") ||
                !GetBool(supportedBase, "guardCoverageValidated") ||
                GetInt(supportedBase, "unsupportedChangeCount") != 0 ||
                !string.Equals(GetString(supportedBase, "runtimeProtocol"),
                    ResourceUpdateCompatibility.RuntimeProtocol, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(GetString(supportedBase, "nativeRuntimeContract")) ||
                !IsValidCapabilitySet(runtimeCapabilities) ||
                !IsValidCapabilitySet(requiredRuntimeCapabilities) ||
                !new HashSet<string>(runtimeCapabilities, StringComparer.Ordinal)
                    .IsSupersetOf(requiredRuntimeCapabilities) ||
                !IsHex(GetString(supportedBase, "buildIdentitySha256"), 64, 64) ||
                !validatedById.TryGetValue(identityKey, out JsonElement validatedBase) ||
                !supportedModes.SequenceEqual(CanonicalResourceAssemblyModes(validatedBase,
                    variant, "DHE resource validated Base " + baseId),
                    StringComparer.OrdinalIgnoreCase) ||
                !CanonicalResourceFrozenSources(supportedBase).SequenceEqual(
                    CanonicalResourceFrozenSources(validatedBase), StringComparer.Ordinal) ||
                !GetBool(validatedBase, "compatible") ||
                !GetBool(validatedBase, "guardCoverageValidated") ||
                GetInt(validatedBase, "unsupportedChangeCount") != 0 ||
                !new HashSet<string>(ReadAotAssemblyNames(validatedBase,
                        "DHE resource validated Base " + baseId),
                    StringComparer.OrdinalIgnoreCase).SetEquals(supportedAotAssemblies) ||
                !string.Equals(GetString(validatedBase, "nativeRuntimeContract"),
                    GetString(supportedBase, "nativeRuntimeContract"), StringComparison.Ordinal) ||
                !string.Equals(GetString(validatedBase, "aotMetadataSetId"),
                    GetString(supportedBase, "aotMetadataSetId"), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(GetString(validatedBase, "payloadVariantId") ?? "default",
                    GetString(supportedBase, "payloadVariantId") ?? "default",
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(GetString(validatedBase, "currentAssemblySetSha256"),
                    GetString(supportedBase, "currentAssemblySetSha256"),
                    StringComparison.OrdinalIgnoreCase) ||
                !new HashSet<string>(ReadRuntimeCapabilities(validatedBase,
                        "runtimeCapabilities"), StringComparer.Ordinal)
                    .SetEquals(runtimeCapabilities) ||
                !new HashSet<string>(ReadRuntimeCapabilities(validatedBase,
                        "requiredRuntimeCapabilities"), StringComparer.Ordinal)
                    .SetEquals(requiredRuntimeCapabilities))
                throw new DheException("DHE resource update contains an unvalidated Base: " + baseId);
        }
        return validationPath;
    }

    private static string[] CanonicalResourceFrozenSources(JsonElement record)
    {
        if (!record.TryGetProperty("frozenAotSources", out JsonElement sources) || sources.ValueKind == JsonValueKind.Null)
            return Array.Empty<string>();
        if (sources.ValueKind != JsonValueKind.Array) throw new DheException("Frozen sources must be an array.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        uint[] Tokens(JsonElement source, string property, uint table, uint minimum)
        {
            if (!source.TryGetProperty(property, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
                throw new DheException("Frozen source is missing " + property + ".");
            uint[] tokens = array.EnumerateArray().Select(value => value.GetUInt32()).ToArray();
            if (!tokens.SequenceEqual(tokens.Distinct().OrderBy(token => token)) ||
                tokens.Any(token => (token >> 24) != table || (token & 0xffffffu) <= minimum))
                throw new DheException("Invalid frozen source selection: " + property);
            return tokens;
        }
        return sources.EnumerateArray().Select(source =>
        {
            string name = NormalizeName(GetString(source, "assemblyName") ?? string.Empty);
            string hash = GetString(source, "sourceSha256") ?? string.Empty;
            string mvHash = GetString(source, "baseMetaVersionSha256") ?? string.Empty;
            string path = GetString(source, "source") ?? string.Empty;
            string mvPath = GetString(source, "baseMetaVersion") ?? string.Empty;
            if (name.Length == 0 || !names.Add(name) || !IsHex(hash, 64, 64) || !IsHex(mvHash, 64, 64) ||
                string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(mvPath) || GetString(source, "sourceKind") != "frozen-base-aot")
                throw new DheException("Invalid frozen source identity: " + name);
            uint[] types = Tokens(source, "currentStorageTypeTokens", 2, 1);
            uint[] methods = Tokens(source, "currentExecutionMethodTokens", 6, 0);
            uint[] excluded = Tokens(source, "excludedBaseTypeTokens", 2, 1);
            uint[] conditional = Tokens(source, "genericContextMethodTokens", 6, 0);
            if (conditional.Any(token => !methods.Contains(token)))
                throw new DheException("Conditional frozen method is not selected: " + name);
            // JSON string encoding makes the binding unambiguous even when a
            // path contains separators used by the method-plan canonical form.
            return JsonSerializer.Serialize(new { name, hash = hash.ToUpperInvariant(), mvHash = mvHash.ToUpperInvariant(),
                path = path.Replace('\\', '/'), mvPath = mvPath.Replace('\\', '/'), types, methods, excluded, conditional });
        }).OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static string[] CanonicalResourceAssemblyModes(JsonElement record,
        JsonElement payloadVariant, string description)
    {
        if (!record.TryGetProperty("assemblyModes", out JsonElement modes) ||
            modes.ValueKind != JsonValueKind.Array)
            throw new DheException(description + " has no assembly mode table.");
        string[] expectedNames = payloadVariant.GetProperty("assemblies").EnumerateArray()
            .Select(item => NormalizeName(GetString(item, "assemblyName") ?? string.Empty))
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var values = new List<string>();
        foreach (JsonElement mode in modes.EnumerateArray())
        {
            string name = NormalizeName(GetString(mode, "assemblyName") ?? string.Empty);
            string executionMode = GetString(mode, "executionMode") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name) ||
                !IsDheExecutionMode(executionMode))
                throw new DheException(description + " contains an invalid assembly mode.");
            string executionBinding = "";
            if (mode.TryGetProperty("executionPlans", out JsonElement executions) && executions.ValueKind != JsonValueKind.Null)
            {
                if (executions.ValueKind != JsonValueKind.Array || executions.GetArrayLength() > 1)
                    throw new DheException(description + " must contain zero or one execution plan.");
                if (executions.GetArrayLength() == 1)
                {
                    var execution = executions[0].Deserialize<ResourceExecutionPlan>(Json)
                        ?? throw new DheException(description + " has an invalid execution plan.");
                    if (executionMode != "dhe-differential" || execution.AssemblyName != name)
                        throw new DheException(description + " execution plan has no matching Base assembly.");
                    executionBinding = execution.CanonicalBinding();
                }
            }
            values.Add(name + "=" + executionMode + "|" + executionBinding);
        }
        if (!names.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(expectedNames, StringComparer.OrdinalIgnoreCase))
            throw new DheException(description + " assembly mode table is incomplete.");
        return values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void ValidatePayloadVariantSet(JsonElement manifest, JsonElement validation)
    {
        ValidatePayloadVariantSetHash(manifest, "DHE resource manifest");
        ValidatePayloadVariantSetHash(validation, "DHE resource validation");
        bool manifestHasVariants = manifest.TryGetProperty("payloadVariants", out JsonElement manifestVariants);
        bool validationHasVariants = validation.TryGetProperty("payloadVariants", out JsonElement validationVariants);
        if (manifestHasVariants != validationHasVariants)
            throw new DheException("DHE resource payload variant records are not bound.");
        if (!manifestHasVariants)
            return;
        if (manifestVariants.ValueKind != JsonValueKind.Array || validationVariants.ValueKind != JsonValueKind.Array ||
            manifestVariants.GetArrayLength() == 0 ||
            manifestVariants.GetArrayLength() != validationVariants.GetArrayLength())
            throw new DheException("DHE resource payload variant records are invalid.");
        var validationById = validationVariants.EnumerateArray().ToDictionary(item =>
            GetString(item, "variantId") ?? string.Empty, item => item,
            StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement variant in manifestVariants.EnumerateArray())
        {
            string id = GetString(variant, "variantId") ?? string.Empty;
            string hash = GetString(variant, "currentAssemblySetSha256") ?? string.Empty;
            if (!IsPayloadVariantId(id) || !ids.Add(id) || !IsHex(hash, 64, 64) ||
                !validationById.TryGetValue(id, out JsonElement validated) ||
                !string.Equals(GetString(validated, "currentAssemblySetSha256"), hash,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("DHE resource payload variant record is invalid: " + id);
        }
    }

    private static void ValidatePayloadVariantSetHash(JsonElement document, string description)
    {
        if (!document.TryGetProperty("payloadVariants", out JsonElement variants))
            return;
        if (variants.ValueKind != JsonValueKind.Array || variants.GetArrayLength() == 0)
            throw new DheException(description + " payload variant records are invalid.");
        string setHash = GetString(document, "payloadVariantSetSha256") ?? string.Empty;
        if (!IsHex(setHash, 64, 64) ||
            !string.Equals(setHash, ComputePayloadVariantSetHash(variants),
                StringComparison.OrdinalIgnoreCase))
            throw new DheException(description + " payload variant set hash is invalid.");
    }

    private static string ComputePayloadVariantSetHash(JsonElement variants)
    {
        using var sha = SHA256.Create();
        foreach (JsonElement variant in variants.EnumerateArray().OrderBy(item =>
                     GetString(item, "variantId") ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            byte[] bytes = Encoding.UTF8.GetBytes((GetString(variant, "variantId") ?? string.Empty) +
                "\n" + (GetString(variant, "currentAssemblySetSha256") ?? string.Empty).ToLowerInvariant() +
                "\n");
            sha.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static void ValidateBaseRegistryAudit(string updateRoot, JsonElement manifest)
    {
        string? registrySha256 = GetString(manifest, "baseRegistrySha256");
        string? auditPathValue = GetString(manifest, "baseRegistryAuditPath");
        string? auditSha256 = GetString(manifest, "baseRegistryAuditSha256");
        if (string.IsNullOrWhiteSpace(registrySha256))
        {
            if (!manifest.TryGetProperty("baseRegistryEntryCount", out JsonElement entryCount) ||
                entryCount.ValueKind != JsonValueKind.Null ||
                !string.IsNullOrWhiteSpace(auditPathValue) ||
                !string.IsNullOrWhiteSpace(auditSha256) ||
                !string.IsNullOrWhiteSpace(GetString(manifest, "baseRegistryId")) ||
                manifest.TryGetProperty("baseRegistryRevision", out JsonElement noRegistryRevision) &&
                    noRegistryRevision.ValueKind != JsonValueKind.Null ||
                !string.IsNullOrWhiteSpace(GetString(manifest, "baseRegistryParentSha256")) ||
                !string.IsNullOrWhiteSpace(GetString(manifest, "baseRegistryParentAuditPath")) ||
                !string.IsNullOrWhiteSpace(GetString(manifest, "baseRegistryParentAuditSha256")) ||
                manifest.TryGetProperty("baseRegistryRetiredBaseCount",
                    out JsonElement noRegistryRetiredCount) &&
                    noRegistryRetiredCount.ValueKind != JsonValueKind.Null ||
                GetBool(manifest, "baseRegistryLineageValidated"))
                throw new DheException("DHE resource update has Base registry audit fields without a registry.");
            return;
        }

        if (!IsHex(registrySha256, 64, 64) ||
            !string.Equals(auditPathValue, "audit/dhe-base-registry.json",
                StringComparison.Ordinal) ||
            !IsHex(auditSha256, 64, 64))
            throw new DheException("DHE Base registry audit binding is invalid.");
        string auditPath = RequireFile(ResolveContainedPath(updateRoot, auditPathValue!,
            "DHE Base registry audit copy"), "DHE Base registry audit copy");
        if (!string.Equals(Sha256File(auditPath), registrySha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Sha256File(auditPath), auditSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("DHE Base registry audit copy hash does not match the manifest.");

        // Do not resolve paths from the archived document: those paths are
        // relative to the original registry location. Validate its identity
        // and entry set here, while ReadBaseRegistry validates paths at build
        // time before the copy is made.
        JsonElement archived = ReadJson<JsonElement>(auditPath);
        string registryId = GetString(archived, "registryId") ?? string.Empty;
        int revision = archived.TryGetProperty("revision", out JsonElement archivedRevision) &&
                       archivedRevision.ValueKind == JsonValueKind.Number
            ? archivedRevision.GetInt32()
            : 1;
        string? parentSha256 = GetString(archived, "parentRegistrySha256");
        JsonElement retiredBases = archived.TryGetProperty("retiredBases",
            out JsonElement archivedRetiredBases)
            ? archivedRetiredBases
            : default;
        int retiredCount = retiredBases.ValueKind == JsonValueKind.Array
            ? retiredBases.GetArrayLength()
            : 0;
        if (GetInt(archived, "schemaVersion") != 1 ||
            !string.Equals(GetString(archived, "format"),
                "hybridclr.dhe-base-registry.json", StringComparison.Ordinal) ||
            (GetString(archived, "pathSemantics") is not ("registry-relative-v1" or
                "workspace-absolute-v1")) ||
            !IsRegistryId(registryId) || revision < 1 ||
            (revision == 1 && !string.IsNullOrWhiteSpace(parentSha256)) ||
            (revision > 1 && !IsHex(parentSha256, 64, 64)) ||
            !archived.TryGetProperty("bases", out JsonElement bases) ||
            bases.ValueKind != JsonValueKind.Array || bases.GetArrayLength() == 0 ||
            GetInt(manifest, "baseRegistryEntryCount") != bases.GetArrayLength() ||
            !string.Equals(GetString(manifest, "baseRegistryId"), registryId,
                StringComparison.Ordinal) ||
            GetInt(manifest, "baseRegistryRevision") != revision ||
            !string.Equals(GetString(manifest, "baseRegistryParentSha256"), parentSha256,
                StringComparison.OrdinalIgnoreCase) ||
            GetInt(manifest, "baseRegistryRetiredBaseCount") != retiredCount ||
            !GetBool(manifest, "baseRegistryLineageValidated"))
            throw new DheException("DHE archived Base registry identity or entry count is invalid.");

        var active = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement entry in bases.EnumerateArray())
        {
            string baseId = GetString(entry, "baseId") ?? string.Empty;
            string workflow = GetString(entry, "engineWorkflow") ?? string.Empty;
            string label = GetString(entry, "label") ?? string.Empty;
            string payloadVariantId = GetString(entry, "payloadVariantId") ?? "default";
            if (!IsHex(baseId, 64, 64) || !active.TryAdd(baseId, entry) ||
                !KnownPlayerEngineWorkflows.Contains(workflow,
                    StringComparer.Ordinal) ||
                string.IsNullOrWhiteSpace(label) || label.Length > 256 ||
                !IsPayloadVariantId(payloadVariantId))
                throw new DheException("DHE archived Base registry contains an invalid entry.");
        }

        var retired = ReadArchivedRetirements(retiredBases, revision,
            active.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
        string? parentAuditPathValue = GetString(manifest,
            "baseRegistryParentAuditPath");
        string? parentAuditSha256 = GetString(manifest,
            "baseRegistryParentAuditSha256");
        if (revision == 1)
        {
            if (!string.IsNullOrWhiteSpace(parentAuditPathValue) ||
                !string.IsNullOrWhiteSpace(parentAuditSha256))
                throw new DheException(
                    "DHE registry revision 1 must not contain a parent registry audit.");
            return;
        }
        if (!string.Equals(parentAuditPathValue,
                "audit/dhe-base-registry-parent.json", StringComparison.Ordinal) ||
            !IsHex(parentAuditSha256, 64, 64) ||
            !string.Equals(parentAuditSha256, parentSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("DHE parent Base registry audit binding is invalid.");
        string parentAuditPath = RequireFile(ResolveContainedPath(updateRoot,
            parentAuditPathValue!, "DHE parent Base registry audit copy"),
            "DHE parent Base registry audit copy");
        if (!string.Equals(Sha256File(parentAuditPath), parentSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException(
                "DHE parent Base registry audit copy hash does not match the manifest.");
        JsonElement parent = ReadJson<JsonElement>(parentAuditPath);
        string parentRegistryId = GetString(parent, "registryId") ?? string.Empty;
        int parentRevision = parent.TryGetProperty("revision", out JsonElement parentRevisionValue) &&
                             parentRevisionValue.ValueKind == JsonValueKind.Number
            ? parentRevisionValue.GetInt32()
            : 1;
        if (GetInt(parent, "schemaVersion") != 1 ||
            !string.Equals(GetString(parent, "format"),
                "hybridclr.dhe-base-registry.json", StringComparison.Ordinal) ||
            !string.Equals(parentRegistryId, registryId, StringComparison.Ordinal) ||
            parentRevision + 1 != revision ||
            !parent.TryGetProperty("bases", out JsonElement parentBases) ||
            parentBases.ValueKind != JsonValueKind.Array || parentBases.GetArrayLength() == 0)
            throw new DheException("DHE parent Base registry identity is invalid.");

        var parentActive = new Dictionary<string, JsonElement>(
            StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement entry in parentBases.EnumerateArray())
        {
            string baseId = GetString(entry, "baseId") ?? string.Empty;
            string workflow = GetString(entry, "engineWorkflow") ?? string.Empty;
            string label = GetString(entry, "label") ?? string.Empty;
            string payloadVariantId = GetString(entry, "payloadVariantId") ?? "default";
            if (!IsHex(baseId, 64, 64) || !parentActive.TryAdd(baseId, entry) ||
                !KnownPlayerEngineWorkflows.Contains(workflow,
                    StringComparer.Ordinal) ||
                string.IsNullOrWhiteSpace(label) || label.Length > 256 ||
                !IsPayloadVariantId(payloadVariantId))
                throw new DheException(
                    "DHE parent Base registry contains an invalid entry.");
        }
        JsonElement parentRetiredValues = parent.TryGetProperty("retiredBases",
            out JsonElement parsedParentRetired)
            ? parsedParentRetired
            : default;
        var parentRetired = ReadArchivedRetirements(parentRetiredValues,
            parentRevision, parentActive.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
        foreach (var item in parentRetired)
        {
            if (!retired.TryGetValue(item.Key, out BaseRegistryRetirement? carried) ||
                !SameRetirement(item.Value, carried))
                throw new DheException(
                    "DHE Base registry dropped or changed a previous retirement.");
        }
        foreach (var item in retired)
        {
            BaseRegistryRetirement retirement = item.Value;
            if (retirement.RetiredAtRevision < revision)
            {
                if (!parentRetired.TryGetValue(item.Key,
                        out BaseRegistryRetirement? earlier) ||
                    !SameRetirement(retirement, earlier))
                    throw new DheException(
                        "DHE Base registry introduced a backdated retirement.");
                continue;
            }
            if (!parentActive.TryGetValue(item.Key, out JsonElement parentEntry) ||
                !string.Equals(GetString(parentEntry, "engineWorkflow"),
                    retirement.EngineWorkflow, StringComparison.Ordinal) ||
                !string.Equals(GetString(parentEntry, "label"), retirement.Label,
                    StringComparison.Ordinal))
                throw new DheException(
                    "DHE Base registry retirement does not match an active parent Base.");
        }
        foreach (var item in parentActive)
        {
            bool retained = active.TryGetValue(item.Key, out JsonElement retainedEntry);
            bool explicitlyRetired = retired.TryGetValue(item.Key,
                out BaseRegistryRetirement? retirement) &&
                retirement.RetiredAtRevision == revision;
            if (retained == explicitlyRetired)
                throw new DheException(
                    "DHE Base registry parent entry was not retained or explicitly retired.");
            if (retained && !string.Equals(GetString(item.Value, "engineWorkflow"),
                    GetString(retainedEntry, "engineWorkflow"), StringComparison.Ordinal))
                throw new DheException(
                    "DHE Base registry changed an active Base engine workflow.");
        }
    }

    private static Dictionary<string, BaseRegistryRetirement> ReadArchivedRetirements(
        JsonElement values, int registryRevision, HashSet<string> activeIds)
    {
        var result = new Dictionary<string, BaseRegistryRetirement>(
            StringComparer.OrdinalIgnoreCase);
        if (values.ValueKind == JsonValueKind.Undefined)
            return result;
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 1024)
            throw new DheException("DHE archived Base registry retirements are invalid.");
        foreach (JsonElement value in values.EnumerateArray())
        {
            var retirement = new BaseRegistryRetirement(
                GetString(value, "baseId") ?? string.Empty,
                GetString(value, "engineWorkflow") ?? string.Empty,
                GetString(value, "label") ?? string.Empty,
                GetInt(value, "retiredAtRevision"),
                GetString(value, "reason") ?? string.Empty);
            if (!IsHex(retirement.BaseId, 64, 64) ||
                activeIds.Contains(retirement.BaseId) ||
                !KnownPlayerEngineWorkflows.Contains(retirement.EngineWorkflow,
                    StringComparer.Ordinal) ||
                string.IsNullOrWhiteSpace(retirement.Label) || retirement.Label.Length > 256 ||
                retirement.RetiredAtRevision < 2 ||
                retirement.RetiredAtRevision > registryRevision ||
                string.IsNullOrWhiteSpace(retirement.Reason) ||
                retirement.Reason.Length > 512 ||
                !result.TryAdd(retirement.BaseId, retirement))
                throw new DheException(
                    "DHE archived Base registry contains an invalid retirement record.");
        }
        return result;
    }

    private static string[] ReadRuntimeCapabilities(JsonElement value, string property)
    {
        return value.TryGetProperty(property, out JsonElement capabilities) &&
               capabilities.ValueKind == JsonValueKind.Array
            ? capabilities.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty).ToArray()
            : Array.Empty<string>();
    }

    private static bool IsValidCapabilitySet(string[] values) =>
        values.Length != 0 && !values.Any(string.IsNullOrWhiteSpace) &&
        values.Distinct(StringComparer.Ordinal).Count() == values.Length;

    private static bool OptionalJsonPropertiesEqual(JsonElement left, JsonElement right,
        string property)
    {
        bool leftPresent = left.TryGetProperty(property, out JsonElement leftValue);
        bool rightPresent = right.TryGetProperty(property, out JsonElement rightValue);
        return leftPresent == rightPresent &&
            (!leftPresent || JsonEquivalent(leftValue, rightValue));
    }

    private static string ResourceBaseIdentityKey(JsonElement value)
    {
        var target = GetString(value, "target") ?? string.Empty;
        var baseId = GetString(value, "baseId") ?? string.Empty;
        var managed = GetString(value, "managedAssemblySetSha256") ?? string.Empty;
        var aotAssemblySet = GetString(value, "aotAssemblySetSha256") ?? string.Empty;
        var snapshot = GetString(value, "aotSnapshotSha256") ?? string.Empty;
        var analysisSnapshot = GetString(value, "aotAnalysisSnapshotSha256");
        var baseMetaVersion = GetString(value, "baseMetaVersionSetSha256") ?? string.Empty;
        var aotMetadataSetId = GetString(value, "aotMetadataSetId") ?? string.Empty;
        var guard = GetString(value, "nativeGuardSourceSha256") ?? string.Empty;
        var nativeManifest = GetString(value, "nativeManifestSha256") ?? string.Empty;
        if (target.Length == 0 || !IsHex(baseId, 64, 64) || !IsHex(managed, 64, 64) ||
            !IsHex(aotAssemblySet, 64, 64) ||
            !IsHex(snapshot, 64, 64) ||
            (analysisSnapshot != null && !IsHex(analysisSnapshot, 64, 64)) ||
            !IsHex(baseMetaVersion, 64, 64) || !IsHex(aotMetadataSetId, 64, 64) ||
            !IsHex(guard, 64, 64) ||
            !IsHex(nativeManifest, 64, 64))
            throw new DheException("DHE resource update contains an incomplete Player Base identity.");
        return string.Join("|", target, baseId, managed, aotAssemblySet, snapshot, analysisSnapshot, baseMetaVersion,
            aotMetadataSetId, guard,
            nativeManifest);
    }

    private static void AddResourcePayload(string updateRoot, string? relativePath, string? expectedHash,
        string? planAssetPath, string runtimeAssetRoot, List<ResourcePayload> payloads,
        Dictionary<string, string> paths)
    {
        var relative = relativePath ?? string.Empty;
        if (!relative.StartsWith("payload/", StringComparison.OrdinalIgnoreCase) ||
            !IsHex(expectedHash, 64, 64) ||
            !string.Equals(planAssetPath, runtimeAssetRoot + relative, StringComparison.OrdinalIgnoreCase))
            throw new DheException("Resource payload path/hash binding is invalid: " + relative);
        if (paths.TryGetValue(relative, out string? priorHash))
        {
            if (!string.Equals(priorHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new DheException("Resource payload path has conflicting hashes: " + relative);
            return;
        }
        var source = RequireFile(ResolveContainedPath(updateRoot, relative, "DHE resource payload"),
            "DHE resource payload");
        if (!Sha256File(source).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new DheException("DHE resource payload hash mismatch: " + relative);
        paths.Add(relative, expectedHash!);
        payloads.Add(new ResourcePayload(relative, source, expectedHash!.ToLowerInvariant(), runtimeAssetRoot));
    }

    private static string RequirePortableAssetRoot(string? value, string description)
    {
        var normalized = (value ?? string.Empty).Replace('\\', '/');
        if (!normalized.EndsWith("/", StringComparison.Ordinal) ||
            !IsPortableRelativePath(normalized.TrimEnd('/')))
            throw new DheException(description + " must be a portable relative directory path.");
        return normalized;
    }

    private sealed record ResourcePayload(string RelativePath, string SourcePath, string Sha256, string AssetRoot);
}
