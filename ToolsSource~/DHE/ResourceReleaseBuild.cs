using System.Text;
using System.Text.Json;

namespace HybridCLR.DheTool;

internal static partial class Program
{
    private sealed record ResourceReleaseBuildVariant(string VariantId, string Root,
        bool Primary);

    private sealed record ResourceReleaseBuildBase(string BuildIdentity, string BaselineRoot,
        string NativeManifest, string EngineWorkflow, string PayloadVariantId, string Label,
        string? AotMetadataRoot);

    private sealed record ResourceReleaseBuildConfig(
        string SourcePath,
        string Sha256,
        string Mode,
        string SettingsFile,
        string OutputRoot,
        string? ExistingRegistry,
        string? PreviousRegistry,
        string? RegistryId,
        ResourceReleaseBuildVariant[] CurrentVariants,
        ResourceReleaseBuildBase[] NewBases,
        string[] RetireBaseIds,
        string? RetirementReason,
        string? ChannelSnapshot,
        string? ExpectedChannelSnapshotSha256,
        bool InitializeReleaseLedger);

    private sealed record ResourceReleaseBuildVerification(
        string RegistryId,
        int RegistryRevision,
        string RegistrySha256,
        string? ParentRegistrySha256,
        int ActiveBaseCount,
        int RetiredBaseCount,
        string[] ActiveBaseIds,
        string[] AddedBaseIds,
        string[] RetiredBaseIds,
        string[] PayloadVariantIds,
        string PrimaryVariantId,
        string CurrentAssemblySetSha256,
        string PayloadVariantSetSha256,
        string? ReleaseChannelId,
        int? ReleaseRevision,
        string? ParentReleaseLedgerSha256,
        string? ReleaseLedgerSha256);

    private static int ResourceReleaseBuild(Cli cli)
    {
        string configPath = RequireFile(cli.Require("config"),
            "DHE resource release build config");
        string schemasRoot = RequireDirectory(cli.Optional("schemasroot") ??
            Path.Combine(cli.Root, "schemas"), "DHE schemas root");
        ResourceReleaseBuildConfig config = ReadResourceReleaseBuildConfig(configPath,
            schemasRoot);
        string settingsSha256 = Sha256File(config.SettingsFile);
        string finalRoot = SafeOutputRoot(config.OutputRoot,
            EnumerateResourceReleaseBuildInputs(config));
        if (Directory.Exists(finalRoot) && !cli.Has("forceoutput"))
            throw new DheException("Resource release output already exists; pass -ForceOutput to replace it.");
        ValidateResourceReleaseBuildBoundaries(config, finalRoot, schemasRoot);

        string parent = Path.GetDirectoryName(finalRoot)!;
        Directory.CreateDirectory(parent);
        string outputName = Path.GetFileName(finalRoot.TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        string stagingRoot = Path.Combine(parent, "." + outputName + ".staging-" +
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stagingRoot);
            string auditRoot = Path.Combine(stagingRoot, "audit");
            Directory.CreateDirectory(auditRoot);
            string auditConfig = Path.Combine(auditRoot,
                "dhe-resource-release-build-config.json");
            File.Copy(config.SourcePath, auditConfig, false);
            if (!string.Equals(Sha256File(auditConfig), config.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("Resource release build config changed while it was copied.");

            ChannelSnapshotDocument? channelSnapshot = null;
            if (config.Mode == "Release")
            {
                channelSnapshot = ReadChannelSnapshot(config.ChannelSnapshot!,
                    config.ExpectedChannelSnapshotSha256);
                string auditSnapshot = Path.Combine(auditRoot,
                    "dhe-channel-snapshot.json");
                File.Copy(channelSnapshot.SourcePath, auditSnapshot, false);
                if (!string.Equals(Sha256File(auditSnapshot), channelSnapshot.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                    throw new DheException("Channel snapshot changed while it was copied.");
                if (config.InitializeReleaseLedger == channelSnapshot.Initialized)
                    throw new DheException(channelSnapshot.Initialized
                        ? "An initialized channel snapshot cannot initialize a release ledger."
                        : "An uninitialized channel snapshot requires InitializeReleaseLedger.");
            }

            BaseRegistryDocument? existingRegistry = string.IsNullOrWhiteSpace(
                config.ExistingRegistry) ? null : ReadBaseRegistry(config.ExistingRegistry);
            if (!string.IsNullOrWhiteSpace(config.RegistryId) && existingRegistry != null &&
                !string.Equals(config.RegistryId, existingRegistry.RegistryId,
                    StringComparison.Ordinal))
                throw new DheException("Config RegistryId does not match ExistingRegistry.");

            bool registryChanged = config.NewBases.Length != 0 ||
                config.RetireBaseIds.Length != 0;
            string registryPath;
            string? previousRegistryForResource;
            if (registryChanged || existingRegistry == null)
            {
                if (existingRegistry == null && config.NewBases.Length == 0)
                    throw new DheException("The first Base registry requires at least one new Base.");
                registryPath = Path.Combine(stagingRoot, "registry",
                    "dhe-base-registry.json");
                var registryArguments = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["output"] = registryPath,
                    ["baseentriesjson"] = JsonSerializer.Serialize(config.NewBases.Select(item =>
                        new
                        {
                            buildIdentity = item.BuildIdentity,
                            baselineRoot = item.BaselineRoot,
                            nativeManifest = item.NativeManifest,
                            engineWorkflow = item.EngineWorkflow,
                            payloadVariantId = item.PayloadVariantId,
                            label = item.Label,
                            aotMetadataRoot = item.AotMetadataRoot,
                        }), Json),
                };
                if (existingRegistry != null)
                    registryArguments["existingregistry"] = existingRegistry.SourcePath;
                if (!string.IsNullOrWhiteSpace(config.RegistryId))
                    registryArguments["registryid"] = config.RegistryId;
                if (config.RetireBaseIds.Length != 0)
                {
                    registryArguments["retirebaseids"] = string.Join(',',
                        config.RetireBaseIds);
                    registryArguments["retirementreason"] = config.RetirementReason!;
                }
                if (BuildBaseRegistry(new Cli("base-registry", registryArguments)) != 0)
                    throw new DheException("DHE Base registry build failed.");
                previousRegistryForResource = existingRegistry?.SourcePath;
            }
            else
            {
                registryPath = existingRegistry.SourcePath;
                previousRegistryForResource = config.PreviousRegistry;
            }

            BaseRegistryDocument registry = ReadBaseRegistry(registryPath);
            if (existingRegistry != null && registryChanged &&
                !string.Equals(registry.ParentRegistrySha256, existingRegistry.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("ExistingRegistry changed during successor construction.");
            string[] unusedVariants = config.CurrentVariants.Select(item => item.VariantId)
                .Except(registry.Entries.Select(item => item.PayloadVariantId),
                    StringComparer.OrdinalIgnoreCase).ToArray();
            if (unusedVariants.Length != 0)
                throw new DheException("CurrentVariants contains values selected by no active Base: " +
                    string.Join(',', unusedVariants) + ".");
            if (channelSnapshot?.Initialized == true)
            {
                if (!string.Equals(channelSnapshot.BaseRegistryId, registry.RegistryId,
                        StringComparison.Ordinal))
                    throw new DheException("Base registry ID does not match the protected channel snapshot.");
                bool reused = string.Equals(channelSnapshot.BaseRegistrySha256, registry.Sha256,
                    StringComparison.OrdinalIgnoreCase);
                bool successor = registry.Revision == channelSnapshot.BaseRegistryRevision + 1 &&
                    string.Equals(registry.ParentRegistrySha256,
                        channelSnapshot.BaseRegistrySha256, StringComparison.OrdinalIgnoreCase);
                if (!reused && !successor)
                    throw new DheException("Base registry must be the protected registry or its direct successor.");
            }

            ResourceReleaseBuildVariant primary = config.CurrentVariants.Single(item =>
                item.Primary);
            var variantRoots = config.CurrentVariants.ToDictionary(item => item.VariantId,
                item => item.Root, StringComparer.OrdinalIgnoreCase);
            var resourceArguments = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["settingsfile"] = config.SettingsFile,
                ["currentroot"] = primary.Root,
                ["currentvariantid"] = primary.VariantId,
                ["currentvariantroots"] = JsonSerializer.Serialize(variantRoots, Json),
                ["baseregistry"] = registry.SourcePath,
                ["outputroot"] = Path.Combine(stagingRoot, "resource"),
                ["mode"] = config.Mode,
            };
            if (!string.IsNullOrWhiteSpace(previousRegistryForResource))
                resourceArguments["previousbaseregistry"] = previousRegistryForResource;
            if (config.Mode == "Release")
            {
                resourceArguments["channelsnapshot"] = config.ChannelSnapshot!;
                resourceArguments["expectedchannelsnapshotsha256"] =
                    config.ExpectedChannelSnapshotSha256!;
                if (config.InitializeReleaseLedger)
                    resourceArguments["initializereleaseledger"] = "true";
            }
            if (ResourceUpdate(new Cli("resource-update", resourceArguments)) != 0)
                throw new DheException("DHE resource update build failed.");

            ResourceReleaseBuildVerification verification =
                VerifyResourceReleaseBuildArtifacts(config, registry,
                    existingRegistry, Path.Combine(stagingRoot, "resource"));
            if (!string.Equals(Sha256File(config.SourcePath), config.Sha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Sha256File(config.SettingsFile), settingsSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("Resource release build config or settings changed " +
                    "during construction.");
            string reportPath = Path.Combine(stagingRoot,
                "dhe-resource-release-build.json");
            WriteJson(reportPath, new
            {
                schemaVersion = 1,
                format = "hybridclr.dhe-resource-release-build.json",
                generatedAtUtc = DateTimeOffset.UtcNow,
                passed = true,
                pathSemantics = "release-build-relative-v1",
                mode = config.Mode,
                releaseReady = config.Mode == "Release",
                configuration = "audit/dhe-resource-release-build-config.json",
                configurationSha256 = config.Sha256,
                settingsSha256,
                registryDisposition = existingRegistry == null
                    ? "created-initial"
                    : registryChanged ? "created-successor" : "reused",
                registry = registryChanged || existingRegistry == null
                    ? "registry/dhe-base-registry.json" : null,
                registryId = verification.RegistryId,
                registryRevision = verification.RegistryRevision,
                registrySha256 = verification.RegistrySha256,
                parentRegistrySha256 = verification.ParentRegistrySha256,
                activeBaseCount = verification.ActiveBaseCount,
                retiredBaseCount = verification.RetiredBaseCount,
                addedBaseCount = verification.AddedBaseIds.Length,
                retiredThisBuildCount = verification.RetiredBaseIds.Length,
                activeBaseIds = verification.ActiveBaseIds,
                addedBaseIds = verification.AddedBaseIds,
                retiredBaseIds = verification.RetiredBaseIds,
                primaryVariantId = verification.PrimaryVariantId,
                payloadVariantIds = verification.PayloadVariantIds,
                currentAssemblySetSha256 = verification.CurrentAssemblySetSha256,
                payloadVariantSetSha256 = verification.PayloadVariantSetSha256,
                channelSnapshot = config.Mode == "Release"
                    ? "audit/dhe-channel-snapshot.json" : null,
                channelSnapshotSha256 = config.Mode == "Release"
                    ? config.ExpectedChannelSnapshotSha256 : null,
                releaseChannelId = verification.ReleaseChannelId,
                releaseRevision = verification.ReleaseRevision,
                parentReleaseLedgerSha256 = verification.ParentReleaseLedgerSha256,
                resourceRoot = "resource",
                resourceUpdateManifest = "resource/dhe-resource-update.json",
                resourceUpdateManifestSha256 = Sha256File(Path.Combine(stagingRoot,
                    "resource", "dhe-resource-update.json")),
                resourceUpdateValidation =
                    "resource/dhe-resource-update-validation.json",
                resourceUpdateValidationSha256 = Sha256File(Path.Combine(stagingRoot,
                    "resource", "dhe-resource-update-validation.json")),
                runtimePlan = "resource/dhe-runtime-plan.json",
                runtimePlanSha256 = Sha256File(Path.Combine(stagingRoot, "resource",
                    "dhe-runtime-plan.json")),
                releaseLedger = config.Mode == "Release"
                    ? "resource/dhe-release-ledger.json" : null,
                releaseLedgerSha256 = verification.ReleaseLedgerSha256,
                schemaGate = "audit/dhe-schema-gate.json",
                exactActiveBaseCoverage = true,
                unsupportedChangeCount = 0,
                playerEvidenceRequired = true,
                resourceReleaseGateRequired = true,
                channelPromotionRequired = config.Mode == "Release",
                warnings = config.Mode == "Release" ? Array.Empty<string>() : new[]
                {
                    "Exploratory output is not eligible for channel promotion.",
                },
            });

            int schemaExit = SchemaGate(new Cli("schema-gate",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["schemasroot"] = schemasRoot,
                    ["inputroot"] = stagingRoot,
                    ["output"] = Path.Combine(auditRoot, "dhe-schema-gate.json"),
                    ["requireknownformats"] = "true",
                }));
            if (schemaExit != 0)
                throw new DheException("Resource release build schema gate failed.");

            PublishResourceReleaseDirectory(stagingRoot, finalRoot,
                cli.Has("forceoutput"));
            Console.WriteLine("DHE resource release build: " + finalRoot);
            return 0;
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
                Directory.Delete(stagingRoot, true);
        }
    }

    private static ResourceReleaseBuildConfig ReadResourceReleaseBuildConfig(string path,
        string schemasRoot)
    {
        string schemaPath = RequireFile(Path.Combine(schemasRoot,
            "dhe-resource-release-build-config.schema.json"),
            "DHE resource release build config schema");
        JsonElement schema = ReadJson<JsonElement>(schemaPath);
        JsonElement document = ReadJson<JsonElement>(path);
        var errors = new List<string>();
        ValidateSchemaVocabulary(schema, "$", errors);
        if (errors.Count == 0)
            ValidateJsonSchema(schema, document, schema, "$", errors);
        if (errors.Count != 0)
            throw new DheException("DHE resource release build config is invalid: " +
                string.Join("; ", errors.Take(16)));

        string configDirectory = Path.GetDirectoryName(path)!;
        string Resolve(string value) => ResolveConfigPath(value, configDirectory);
        string? ResolveOptional(string property)
        {
            string? value = GetString(document, property);
            return string.IsNullOrWhiteSpace(value) ? null : Resolve(value);
        }

        string mode = GetString(document, "mode") ?? "Release";
        var variants = document.GetProperty("currentVariants").EnumerateArray().Select(item =>
            new ResourceReleaseBuildVariant(
                GetString(item, "variantId") ?? string.Empty,
                Resolve(GetString(item, "root") ?? string.Empty),
                GetBool(item, "primary"))).ToArray();
        if (variants.Count(item => item.Primary) != 1)
            throw new DheException("CurrentVariants must contain exactly one primary variant.");
        if (variants.Select(item => item.VariantId).Distinct(
                StringComparer.OrdinalIgnoreCase).Count() != variants.Length)
            throw new DheException("CurrentVariants contains a duplicate variantId.");
        foreach (ResourceReleaseBuildVariant variant in variants)
        {
            if (!IsPayloadVariantId(variant.VariantId))
                throw new DheException("CurrentVariants contains an invalid variantId.");
            _ = RequireDirectory(variant.Root, "Current " + variant.VariantId + " variant root");
        }

        var newBases = document.GetProperty("newBases").EnumerateArray().Select(item =>
            new ResourceReleaseBuildBase(
                RequireFile(Resolve(GetString(item, "buildIdentity") ?? string.Empty),
                    "New Base build identity"),
                RequireDirectory(Resolve(GetString(item, "baselineRoot") ?? string.Empty),
                    "New Base baseline root"),
                RequireFile(Resolve(GetString(item, "nativeManifest") ?? string.Empty),
                    "New Base native manifest"),
                GetString(item, "engineWorkflow") ?? string.Empty,
                GetString(item, "payloadVariantId") ?? string.Empty,
                GetString(item, "label") ?? string.Empty,
                item.TryGetProperty("aotMetadataRoot", out JsonElement aotRoot) &&
                    aotRoot.ValueKind == JsonValueKind.String
                    ? RequireDirectory(Resolve(aotRoot.GetString() ?? string.Empty),
                        "New Base AOT metadata root") : null)).ToArray();
        string[] retireBaseIds = document.GetProperty("retireBaseIds").EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty).ToArray();
        string? retirementReason = GetString(document, "retirementReason");
        string? existingRegistry = ResolveOptional("existingRegistry");
        string? previousRegistry = ResolveOptional("previousRegistry");
        string? channelSnapshot = ResolveOptional("channelSnapshot");
        string? expectedSnapshot = GetString(document,
            "expectedChannelSnapshotSha256");
        bool initialize = GetBool(document, "initializeReleaseLedger");
        if (mode == "Release")
        {
            if (channelSnapshot == null || !IsHex(expectedSnapshot, 64, 64))
                throw new DheException("Release mode requires a SHA-256-pinned ChannelSnapshot.");
        }
        else if (channelSnapshot != null || expectedSnapshot != null || initialize)
        {
            throw new DheException("Exploratory mode cannot use channel or release-ledger inputs.");
        }
        if (newBases.Length == 0 && retireBaseIds.Length == 0 && existingRegistry == null)
            throw new DheException("Config requires ExistingRegistry or a Base-set change.");
        bool registryChanged = newBases.Length != 0 || retireBaseIds.Length != 0;
        if (previousRegistry != null && (existingRegistry == null || registryChanged))
            throw new DheException("PreviousRegistry is only used when an unchanged " +
                "ExistingRegistry is reused.");
        if (retireBaseIds.Length != 0 && existingRegistry == null)
            throw new DheException("RetireBaseIds requires ExistingRegistry.");
        if (retireBaseIds.Length == 0 && !string.IsNullOrWhiteSpace(retirementReason) ||
            retireBaseIds.Length != 0 && string.IsNullOrWhiteSpace(retirementReason))
            throw new DheException("RetirementReason must be supplied exactly when Bases are retired.");
        if (newBases.Any(item => !variants.Any(variant => string.Equals(variant.VariantId,
                item.PayloadVariantId, StringComparison.OrdinalIgnoreCase))))
            throw new DheException("Every new Base must select a configured current variant.");

        string settingsFile = RequireFile(Resolve(GetString(document, "settingsFile") ??
            string.Empty), "HybridCLR settings");
        Settings.Sets settings = Settings.Read(settingsFile);
        if (settings.Dhe.Length == 0 || !SetEquals(settings.Hot, settings.Dhe))
            throw new DheException("Formal DHE resource releases require dheAotAssemblies to " +
                "exactly match the non-empty hotUpdateAssemblies set.");
        return new ResourceReleaseBuildConfig(path, Sha256File(path), mode, settingsFile,
            Resolve(GetString(document, "outputRoot") ?? string.Empty), existingRegistry,
            previousRegistry, GetString(document, "registryId"), variants, newBases,
            retireBaseIds, retirementReason, channelSnapshot, expectedSnapshot, initialize);
    }

    private static IEnumerable<string> EnumerateResourceReleaseBuildInputs(
        ResourceReleaseBuildConfig config)
    {
        yield return config.SourcePath;
        yield return config.SettingsFile;
        if (config.ExistingRegistry != null) yield return config.ExistingRegistry;
        if (config.PreviousRegistry != null) yield return config.PreviousRegistry;
        if (config.ChannelSnapshot != null) yield return config.ChannelSnapshot;
        foreach (ResourceReleaseBuildVariant variant in config.CurrentVariants)
            yield return variant.Root;
        foreach (ResourceReleaseBuildBase item in config.NewBases)
        {
            yield return item.BuildIdentity;
            yield return item.BaselineRoot;
            yield return item.NativeManifest;
            if (item.AotMetadataRoot != null) yield return item.AotMetadataRoot;
        }
    }

    private static void ValidateResourceReleaseBuildBoundaries(
        ResourceReleaseBuildConfig config, string outputRoot, string schemasRoot)
    {
        foreach (string input in EnumerateResourceReleaseBuildInputs(config))
        {
            EnsureOutputNotAncestor(outputRoot, input);
            if (Directory.Exists(input)) EnsureOutputOutsideRoot(outputRoot, input);
        }
        EnsureOutputOutsideRoot(outputRoot, schemasRoot);
    }

    private static ResourceReleaseBuildVerification VerifyResourceReleaseBuildArtifacts(
        ResourceReleaseBuildConfig config, BaseRegistryDocument registry,
        BaseRegistryDocument? existingRegistry, string resourceRoot)
    {
        string manifestPath = RequireFile(Path.Combine(resourceRoot,
            "dhe-resource-update.json"), "Resource update manifest");
        string validationPath = RequireFile(Path.Combine(resourceRoot,
            "dhe-resource-update-validation.json"), "Resource update validation");
        string runtimePlanPath = RequireFile(Path.Combine(resourceRoot,
            "dhe-runtime-plan.json"), "Resource runtime plan");
        JsonElement manifest = ReadJson<JsonElement>(manifestPath);
        JsonElement validation = ReadJson<JsonElement>(validationPath);
        JsonElement runtimePlan = ReadJson<JsonElement>(runtimePlanPath);
        if (GetString(manifest, "format") != "hybridclr.dhe-resource-update.json" ||
            GetString(validation, "format") !=
                "hybridclr.dhe-resource-update-validation.json" ||
            GetString(runtimePlan, "format") != "hybridclr.dhe-runtime-asset-plan.json" ||
            !GetBool(validation, "passed") || !GetBool(manifest,
                "compatibilityValidated"))
            throw new DheException("Resource release artifacts did not pass their canonical validation.");
        if (!string.Equals(GetString(manifest, "baseRegistrySha256"), registry.Sha256,
                StringComparison.OrdinalIgnoreCase) ||
            GetInt(manifest, "baseRegistryEntryCount") != registry.Entries.Length ||
            GetInt(manifest, "baseRegistryRetiredBaseCount") != registry.RetiredBases.Length ||
            !string.Equals(GetString(manifest, "baseRegistryId"), registry.RegistryId,
                StringComparison.Ordinal) ||
            GetInt(manifest, "baseRegistryRevision") != registry.Revision)
            throw new DheException("Resource manifest does not exactly bind the active Base registry.");

        static string[] ReadIds(JsonElement parent, string property)
        {
            if (!parent.TryGetProperty(property, out JsonElement values) ||
                values.ValueKind != JsonValueKind.Array)
                throw new DheException("Resource release is missing " + property + ".");
            return values.EnumerateArray().Select(item => GetString(item, "baseId") ??
                string.Empty).ToArray();
        }
        string[] registryIds = registry.Entries.Select(item => item.BaseId)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] manifestIds = ReadIds(manifest, "supportedBases")
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] validationIds = ReadIds(validation, "bases")
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] selectionIds = ReadIds(runtimePlan, "baseSelections")
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!registryIds.SequenceEqual(manifestIds, StringComparer.OrdinalIgnoreCase) ||
            !registryIds.SequenceEqual(validationIds, StringComparer.OrdinalIgnoreCase) ||
            !registryIds.SequenceEqual(selectionIds, StringComparer.OrdinalIgnoreCase))
            throw new DheException("Resource release does not cover every active Base exactly once.");
        foreach (JsonElement item in validation.GetProperty("bases").EnumerateArray())
            if (!GetBool(item, "compatible") || !GetBool(item, "guardCoverageValidated") ||
                GetInt(item, "unsupportedChangeCount") != 0)
                throw new DheException("Resource release contains an incompatible Base.");

        string[] configuredVariants = config.CurrentVariants.Select(item => item.VariantId)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] manifestVariants = manifest.GetProperty("payloadVariants").EnumerateArray()
            .Select(item => GetString(item, "variantId") ?? string.Empty)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] planVariants = runtimePlan.GetProperty("payloadVariants").EnumerateArray()
            .Select(item => GetString(item, "variantId") ?? string.Empty)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!configuredVariants.SequenceEqual(manifestVariants,
                StringComparer.OrdinalIgnoreCase) ||
            !configuredVariants.SequenceEqual(planVariants, StringComparer.OrdinalIgnoreCase))
            throw new DheException("Resource payload variant set does not match the build config.");
        string[] assemblyNames = Settings.Read(config.SettingsFile).Dhe;
        foreach (ResourceReleaseBuildVariant variant in config.CurrentVariants)
        {
            string liveSet = NamedAssemblySetHash(assemblyNames.Select(name =>
                (name, RequireFile(Path.Combine(variant.Root, name + ".dll"),
                    name + " current assembly (" + variant.VariantId + ")"))));
            JsonElement manifestVariant = manifest.GetProperty("payloadVariants")
                .EnumerateArray().Single(item => string.Equals(GetString(item, "variantId"),
                    variant.VariantId, StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(liveSet, GetString(manifestVariant,
                    "currentAssemblySetSha256"), StringComparison.OrdinalIgnoreCase))
                throw new DheException("Current variant changed during resource release construction: " +
                    variant.VariantId + ".");
        }
        var baseVariants = registry.Entries.ToDictionary(item => item.BaseId,
            item => item.PayloadVariantId, StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement selection in runtimePlan.GetProperty("baseSelections").EnumerateArray())
        {
            string baseId = GetString(selection, "baseId") ?? string.Empty;
            if (!baseVariants.TryGetValue(baseId, out string? expected) ||
                !string.Equals(expected, GetString(selection, "payloadVariantId"),
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("A Base selected the wrong resource payload variant.");
        }

        ResourceReleaseBuildVariant primary = config.CurrentVariants.Single(item =>
            item.Primary);
        JsonElement primaryManifestVariant = manifest.GetProperty("payloadVariants")
            .EnumerateArray().Single(item => string.Equals(GetString(item, "variantId"),
                primary.VariantId, StringComparison.OrdinalIgnoreCase));
        string currentSet = GetString(manifest, "currentAssemblySetSha256") ?? string.Empty;
        if (!string.Equals(currentSet, GetString(primaryManifestVariant,
                "currentAssemblySetSha256"), StringComparison.OrdinalIgnoreCase))
            throw new DheException("Resource manifest primary current assembly set is invalid.");

        string[] oldIds = existingRegistry?.Entries.Select(item => item.BaseId).ToArray() ??
            Array.Empty<string>();
        string[] addedIds = registryIds.Except(oldIds, StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        bool registryCreated = existingRegistry == null ||
            !string.Equals(existingRegistry.Sha256, registry.Sha256,
                StringComparison.OrdinalIgnoreCase);
        string[] retiredIds = registryCreated
            ? registry.RetiredBases.Where(item =>
                    item.RetiredAtRevision == registry.Revision)
                .Select(item => item.BaseId).OrderBy(value => value,
                    StringComparer.OrdinalIgnoreCase).ToArray()
            : Array.Empty<string>();

        ReleaseLedgerDocument? ledger = null;
        if (config.Mode == "Release")
        {
            ledger = ReadReleaseLedger(Path.Combine(resourceRoot,
                ReleaseLedgerFileName));
            if (!GetBool(manifest, "releaseReady") || !GetBool(validation,
                    "releaseReady") || !GetBool(runtimePlan, "releaseReady"))
                throw new DheException("Release-mode resource artifacts are not release-ready.");
        }
        else if (GetBool(manifest, "releaseReady") || File.Exists(Path.Combine(resourceRoot,
                     ReleaseLedgerFileName)))
        {
            throw new DheException("Exploratory resource build published Release state.");
        }

        return new ResourceReleaseBuildVerification(registry.RegistryId,
            registry.Revision, registry.Sha256, registry.ParentRegistrySha256,
            registry.Entries.Length, registry.RetiredBases.Length, registryIds,
            addedIds, retiredIds, configuredVariants, primary.VariantId, currentSet,
            GetString(manifest, "payloadVariantSetSha256") ?? string.Empty,
            GetString(manifest, "releaseChannelId"),
            manifest.TryGetProperty("releaseRevision", out JsonElement revision) &&
                revision.ValueKind == JsonValueKind.Number ? revision.GetInt32() : null,
            GetString(manifest, "parentReleaseLedgerSha256"), ledger?.Sha256);
    }

    private static void PublishResourceReleaseDirectory(string stagingRoot,
        string outputRoot, bool forceOutput, Action? beforeFinalMove = null)
    {
        string? backupRoot = null;
        if (Directory.Exists(outputRoot))
        {
            if (!forceOutput)
                throw new DheException("Resource release output already exists.");
            string parent = Path.GetDirectoryName(outputRoot)!;
            backupRoot = Path.Combine(parent, "." + Path.GetFileName(outputRoot) +
                ".backup-" + Guid.NewGuid().ToString("N"));
            Directory.Move(outputRoot, backupRoot);
        }
        try
        {
            beforeFinalMove?.Invoke();
            Directory.Move(stagingRoot, outputRoot);
        }
        catch
        {
            if (backupRoot != null && Directory.Exists(backupRoot) &&
                !Directory.Exists(outputRoot))
                Directory.Move(backupRoot, outputRoot);
            throw;
        }
        if (backupRoot != null && Directory.Exists(backupRoot))
        {
            try
            {
                Directory.Delete(backupRoot, true);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("DHE warning: published output is valid but the old " +
                    "backup could not be removed: " + exception.Message);
            }
        }
    }
}
