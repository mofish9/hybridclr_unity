using System.Globalization;
using System.Text.Json;

namespace HybridCLR.DheTool;

internal static partial class Program
{
    private const string ResourceReleasePlanConfigFormat =
        "hybridclr.dhe-resource-release-plan-config.json";
    private const string BaseRunnerCatalogFormat =
        "hybridclr.dhe-base-runner-catalog.json";
    private const string ResourceReleasePlanFormat =
        "hybridclr.dhe-resource-release-plan.json";

    private sealed record ResourceReleasePlanConfig(
        string SourcePath,
        string Sha256,
        string ResourceUpdateRoot,
        string RunnerCatalog,
        string OutputRoot,
        string QualificationOutputRoot,
        string ChannelSnapshot,
        string ExpectedChannelSnapshotSha256,
        string ExpectedToolchainPackageId,
        string[] EvidenceToolchainRoots,
        bool RequireEngineMatrix,
        int TimeoutSeconds);

    private sealed record BaseRunnerCatalogEntry(
        string BaseId,
        string RunnerKind,
        string? AssetRoot,
        string? BuildIdentity,
        string? BaseWorkflowReport,
        string? PlayerExecutable,
        string? WorkingDirectory,
        string[]? PlayerArguments,
        string[] ImmutableFiles,
        string? PlayerWorkflowReportTemplate);

    private sealed record BaseRunnerCatalog(
        string SourcePath,
        string Sha256,
        string RegistryId,
        int RegistryRevision,
        string RegistrySha256,
        BaseRunnerCatalogEntry[] Entries);

    private sealed record PlannedBaseRunner(
        BaseRunnerCatalogEntry Catalog,
        string EngineWorkflow,
        string Target,
        string PayloadVariantId,
        int ExpectedChangedMethodCount,
        string? PlayerWorkflowReport);

    private sealed record ResourceReleasePlanRegressionResult(
        bool GeneratedQualificationPassed,
        bool ExactCoverageRejected,
        bool DuplicateBaseRejected,
        bool RegistryIdentityRejected,
        bool TemplateContractRejected,
        bool StaleSnapshotRejected,
        string Details)
    {
        internal static readonly ResourceReleasePlanRegressionResult Failed = new(
            false, false, false, false, false, false,
            "resource release planning regression did not complete");
    }

    private static int ResourceReleasePlan(Cli cli)
    {
        string configPath = RequireFile(cli.Require("config"),
            "DHE resource release plan config");
        string schemasRoot = RequireDirectory(cli.Optional("schemasroot") ??
            Path.Combine(cli.Root, "schemas"), "DHE schemas root");
        ResourceReleasePlanConfig config = ReadResourceReleasePlanConfig(configPath,
            schemasRoot);
        string toolchainRoot = RequireDirectory(cli.Root,
            "DHE resource release planning toolchain");
        PackageInspection package = InspectPackage(toolchainRoot,
            config.ExpectedToolchainPackageId, true);
        if (!package.Passed || !IsHex(package.PackageId, 64, 64))
            throw new DheException("Resource release planning requires the exact " +
                "authenticated Release toolchain: " + string.Join("; ", package.Errors));

        JsonElement packageManifest = ReadJson<JsonElement>(RequireFile(
            package.ManifestPath, "DHE toolchain manifest"));
        JsonElement packageSource = packageManifest.GetProperty("sourceIdentity");
        string packageSourceHead = GetString(packageSource, "head") ?? string.Empty;
        string packageSourceTree = GetString(packageSource, "tree") ?? string.Empty;

        string manifestPath = RequireFile(Path.Combine(config.ResourceUpdateRoot,
            "dhe-resource-update.json"), "DHE resource update manifest");
        JsonElement manifest = ReadJson<JsonElement>(manifestPath);
        RequireEvidenceFormat(manifest, "hybridclr.dhe-resource-update.json",
            "Resource update manifest");
        ReleaseLedgerDocument? ledger = ValidateReleaseLedgerForStaging(
            config.ResourceUpdateRoot, manifest);
        string validationPath = ValidateResourceUpdateCompatibility(
            config.ResourceUpdateRoot, manifest);
        if (ledger == null || !GetBool(manifest, "releaseReady") ||
            !string.Equals(GetString(manifest, "mode"), "Release",
                StringComparison.Ordinal))
            throw new DheException("Resource release planning requires a Release-ready " +
                "resource candidate with an authenticated ledger.");

        ChannelSnapshotDocument snapshot = ReadChannelSnapshot(config.ChannelSnapshot,
            config.ExpectedChannelSnapshotSha256);
        ValidateResourceReleasePlanContinuation(ledger, snapshot);

        BaseRunnerCatalog catalog = ReadBaseRunnerCatalog(config.RunnerCatalog,
            schemasRoot);
        if (!string.Equals(catalog.RegistryId, ledger.BaseRegistryId,
                StringComparison.Ordinal) ||
            catalog.RegistryRevision != ledger.BaseRegistryRevision ||
            !string.Equals(catalog.RegistrySha256, ledger.BaseRegistrySha256,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Base runner catalog does not match the resource " +
                "candidate Base registry identity.");

        IReadOnlyDictionary<string, string> evidencePackages =
            ValidateResourceReleasePlanAuthorities(toolchainRoot,
                package.PackageId!, config.EvidenceToolchainRoots);
        PlannedBaseRunner[] runners = BuildResourceReleasePlanRunners(catalog,
            manifest, ledger);
        string[] protectedInputs = EnumerateResourceReleasePlanInputs(config, catalog)
            .Concat(runners.Where(item => item.PlayerWorkflowReport != null)
                .Select(item => item.PlayerWorkflowReport!))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        bool engineMatrixCovered = RequiredPlayerEngineWorkflows.All(workflow =>
            runners.Any(item => string.Equals(item.EngineWorkflow, workflow,
                StringComparison.Ordinal)));
        if (config.RequireEngineMatrix && !engineMatrixCovered)
            throw new DheException("Resource release plan does not cover the required " +
                "three-engine matrix.");

        string finalRoot = SafeOutputRoot(config.OutputRoot,
            protectedInputs);
        if (Directory.Exists(finalRoot) || File.Exists(finalRoot))
            throw new DheException("Resource release plan OutputRoot must be new.");
        if (Directory.Exists(config.QualificationOutputRoot) ||
            File.Exists(config.QualificationOutputRoot))
            throw new DheException("QualificationOutputRoot must be new when the plan is " +
                "created.");
        ValidateResourceReleasePlanOutputs(finalRoot, config.QualificationOutputRoot,
            toolchainRoot, schemasRoot, snapshot.StateRoot,
            protectedInputs);

        string parent = Path.GetDirectoryName(finalRoot)!;
        Directory.CreateDirectory(parent);
        string stagingRoot = Path.Combine(parent, "." + Path.GetFileName(finalRoot) +
            ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stagingRoot);
            string auditRoot = Path.Combine(stagingRoot, "audit");
            Directory.CreateDirectory(auditRoot);
            string auditConfig = Path.Combine(auditRoot,
                "dhe-resource-release-plan-config.json");
            string auditCatalog = Path.Combine(auditRoot,
                "dhe-base-runner-catalog.json");
            File.Copy(config.SourcePath, auditConfig, false);
            File.Copy(catalog.SourcePath, auditCatalog, false);
            if (!string.Equals(Sha256File(auditConfig), config.Sha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Sha256File(auditCatalog), catalog.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("Resource release planning inputs changed while " +
                    "they were copied.");

            object[] qualificationBases = runners.Select(item => new
            {
                baseId = item.Catalog.BaseId,
                runnerKind = item.Catalog.RunnerKind,
                assetRoot = item.Catalog.AssetRoot,
                buildIdentity = item.Catalog.BuildIdentity,
                baseWorkflowReport = item.Catalog.BaseWorkflowReport,
                playerExecutable = item.Catalog.PlayerExecutable,
                workingDirectory = item.Catalog.WorkingDirectory,
                playerArguments = item.Catalog.PlayerArguments,
                immutableFiles = item.Catalog.ImmutableFiles,
                playerWorkflowReport = item.PlayerWorkflowReport,
            }).ToArray<object>();
            string qualificationConfigPath = Path.Combine(stagingRoot,
                "dhe-resource-release-qualification-config.json");
            WriteJson(qualificationConfigPath, new
            {
                schemaVersion = 1,
                format = "hybridclr.dhe-resource-release-qualification-config.json",
                pathSemantics = "config-relative-v1",
                resourceUpdateRoot = config.ResourceUpdateRoot,
                outputRoot = config.QualificationOutputRoot,
                channelSnapshot = config.ChannelSnapshot,
                expectedChannelSnapshotSha256 = config.ExpectedChannelSnapshotSha256,
                expectedToolchainPackageId = config.ExpectedToolchainPackageId,
                evidenceToolchainRoots = config.EvidenceToolchainRoots,
                requireEngineMatrix = config.RequireEngineMatrix,
                timeoutSeconds = config.TimeoutSeconds,
                bases = qualificationBases,
            });
            _ = ReadValidatedPlanningDocument(qualificationConfigPath, schemasRoot,
                "dhe-resource-release-qualification-config.schema.json",
                "hybridclr.dhe-resource-release-qualification-config.json",
                "generated resource release qualification config");

            string planPath = Path.Combine(stagingRoot,
                "dhe-resource-release-plan.json");
            WriteJson(planPath, new
            {
                schemaVersion = 1,
                format = ResourceReleasePlanFormat,
                generatedAtUtc = DateTimeOffset.UtcNow,
                passed = true,
                releaseReady = true,
                pathSemantics = "plan-owned-relative-external-absolute-v1",
                configuration = "audit/dhe-resource-release-plan-config.json",
                configurationSha256 = config.Sha256,
                runnerCatalog = "audit/dhe-base-runner-catalog.json",
                runnerCatalogSha256 = catalog.Sha256,
                toolchainRoot,
                toolchainPackageId = package.PackageId,
                toolchainSourceHead = packageSourceHead,
                toolchainSourceTree = packageSourceTree,
                resourceUpdateRoot = config.ResourceUpdateRoot,
                resourceUpdateManifest = manifestPath,
                resourceUpdateManifestSha256 = Sha256File(manifestPath),
                resourceUpdateValidation = validationPath,
                resourceUpdateValidationSha256 = Sha256File(validationPath),
                releaseChannelId = ledger.ChannelId,
                releaseRevision = ledger.Revision,
                releaseLedgerSha256 = ledger.Sha256,
                parentReleaseLedgerSha256 = ledger.ParentLedgerSha256,
                channelSnapshot = config.ChannelSnapshot,
                channelSnapshotSha256 = config.ExpectedChannelSnapshotSha256,
                channelStateRoot = snapshot.StateRoot,
                expectedChannelHeadSha256 = snapshot.ChannelHeadSha256,
                baseRegistryId = ledger.BaseRegistryId,
                baseRegistryRevision = ledger.BaseRegistryRevision,
                baseRegistrySha256 = ledger.BaseRegistrySha256,
                activeBaseCount = runners.Length,
                processRunnerCount = runners.Count(item => item.Catalog.RunnerKind ==
                    QualificationProcessRunner),
                prequalifiedRunnerCount = runners.Count(item => item.Catalog.RunnerKind ==
                    QualificationPrequalifiedRunner),
                evidenceToolchainPackageIds = evidencePackages.Keys.OrderBy(value => value,
                    StringComparer.Ordinal).ToArray(),
                requireEngineMatrix = config.RequireEngineMatrix,
                engineMatrixCovered,
                exactActiveBaseCoverage = true,
                qualificationConfig = "dhe-resource-release-qualification-config.json",
                qualificationConfigSha256 = Sha256File(qualificationConfigPath),
                qualificationOutputRoot = config.QualificationOutputRoot,
                jobs = runners.Select(item => new
                {
                    baseId = item.Catalog.BaseId,
                    runnerKind = item.Catalog.RunnerKind,
                    item.EngineWorkflow,
                    item.Target,
                    item.PayloadVariantId,
                    item.ExpectedChangedMethodCount,
                    item.PlayerWorkflowReport,
                }).ToArray(),
                errors = Array.Empty<string>(),
                warnings = config.RequireEngineMatrix ? Array.Empty<string>() : new[]
                {
                    "The generated qualification does not require the three-engine matrix.",
                },
            });
            _ = ReadValidatedPlanningDocument(planPath, schemasRoot,
                "dhe-resource-release-plan.schema.json", ResourceReleasePlanFormat,
                "resource release plan");

            int schemaExit = SchemaGate(new Cli("schema-gate",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["schemasroot"] = schemasRoot,
                    ["inputroot"] = stagingRoot,
                    ["output"] = Path.Combine(auditRoot, "dhe-schema-gate.json"),
                    ["requireknownformats"] = "true",
                }));
            if (schemaExit != 0)
                throw new DheException("Resource release plan schema gate failed.");
            PublishResourceReleaseDirectory(stagingRoot, finalRoot, false);
            Console.WriteLine("DHE resource release plan: " + finalRoot);
            return 0;
        }
        finally
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true);
        }
    }

    private static ResourceReleasePlanConfig ReadResourceReleasePlanConfig(string path,
        string schemasRoot)
    {
        JsonElement document = ReadValidatedPlanningDocument(path, schemasRoot,
            "dhe-resource-release-plan-config.schema.json",
            ResourceReleasePlanConfigFormat, "resource release plan config");
        string directory = Path.GetDirectoryName(path)!;
        string Resolve(string value) => ResolveConfigPath(value, directory);
        string[] evidenceRoots = document.GetProperty("evidenceToolchainRoots")
            .EnumerateArray().Select(item => RequireDirectory(Resolve(
                item.GetString() ?? string.Empty),
                "Historical evidence toolchain package")).ToArray();
        var config = new ResourceReleasePlanConfig(path, Sha256File(path),
            RequireDirectory(Resolve(GetString(document, "resourceUpdateRoot") ??
                string.Empty), "DHE resource update root"),
            RequireFile(Resolve(GetString(document, "runnerCatalog") ?? string.Empty),
                "DHE Base runner catalog"),
            Resolve(GetString(document, "outputRoot") ?? string.Empty),
            Resolve(GetString(document, "qualificationOutputRoot") ?? string.Empty),
            RequireFile(Resolve(GetString(document, "channelSnapshot") ?? string.Empty),
                "DHE channel snapshot"),
            (GetString(document, "expectedChannelSnapshotSha256") ??
                string.Empty).ToLowerInvariant(),
            (GetString(document, "expectedToolchainPackageId") ??
                string.Empty).ToLowerInvariant(),
            evidenceRoots, GetBool(document, "requireEngineMatrix"),
            GetInt(document, "timeoutSeconds"));
        foreach (string value in evidenceRoots.Append(config.OutputRoot)
                     .Append(config.QualificationOutputRoot))
            if (value.Contains(',', StringComparison.Ordinal))
                throw new DheException("Resource release plan list paths cannot contain " +
                    "commas: " + value);
        return config;
    }

    private static BaseRunnerCatalog ReadBaseRunnerCatalog(string path,
        string schemasRoot)
    {
        JsonElement document = ReadValidatedPlanningDocument(path, schemasRoot,
            "dhe-base-runner-catalog.schema.json", BaseRunnerCatalogFormat,
            "Base runner catalog");
        string directory = Path.GetDirectoryName(path)!;
        string Resolve(string value) => ResolveConfigPath(value, directory);
        string? ResolveOptional(JsonElement item, string property, bool directoryPath)
        {
            string? value = GetString(item, property);
            if (string.IsNullOrWhiteSpace(value)) return null;
            string resolved = Resolve(value);
            return directoryPath ? RequireDirectory(resolved,
                "Runner catalog " + property) : RequireFile(resolved,
                "Runner catalog " + property);
        }

        var entries = new List<BaseRunnerCatalogEntry>();
        foreach (JsonElement item in document.GetProperty("bases").EnumerateArray())
        {
            string runnerKind = GetString(item, "runnerKind") ?? string.Empty;
            string baseId = (GetString(item, "baseId") ?? string.Empty).ToLowerInvariant();
            string? assetRoot = ResolveOptional(item, "assetRoot", true);
            string? buildIdentity = ResolveOptional(item, "buildIdentity", false);
            string? baseWorkflow = ResolveOptional(item, "baseWorkflowReport", false);
            string? executable = ResolveOptional(item, "playerExecutable", false);
            string? workingDirectory = ResolveOptional(item, "workingDirectory", true);
            string[]? arguments = item.TryGetProperty("playerArguments",
                    out JsonElement argumentValues) &&
                argumentValues.ValueKind == JsonValueKind.Array
                    ? argumentValues.EnumerateArray().Select(value =>
                        value.GetString() ?? string.Empty).ToArray()
                    : null;
            string[] immutableFiles = item.GetProperty("immutableFiles")
                .EnumerateArray().Select(value => RequireFile(Resolve(
                    value.GetString() ?? string.Empty),
                    "Runner catalog immutable Player file")).ToArray();
            string? reportTemplate = GetString(item,
                "playerWorkflowReportTemplate");

            if (runnerKind == QualificationProcessRunner)
            {
                if (assetRoot == null || buildIdentity == null || baseWorkflow == null ||
                    executable == null || workingDirectory == null || arguments == null ||
                    immutableFiles.Length == 0 || reportTemplate != null)
                    throw new DheException("Process runner catalog entry " + baseId +
                        " has incomplete or conflicting inputs.");
                if (arguments.Count(value => value == QualificationResultToken) != 1 ||
                    arguments.Count(value => value == QualificationLogToken) != 1 ||
                    !immutableFiles.Contains(executable,
                        StringComparer.OrdinalIgnoreCase))
                    throw new DheException("Process runner catalog entry " + baseId +
                        " violates the result/log or immutable executable contract.");
            }
            else if (runnerKind == QualificationPrequalifiedRunner)
            {
                if (assetRoot != null || buildIdentity != null || baseWorkflow != null ||
                    executable != null || workingDirectory != null || arguments != null ||
                    immutableFiles.Length != 0 || string.IsNullOrWhiteSpace(reportTemplate))
                    throw new DheException("Prequalified runner catalog entry " + baseId +
                        " must contain only PlayerWorkflowReportTemplate.");
            }
            else
            {
                throw new DheException("Runner catalog runnerKind is unsupported: " +
                    runnerKind + ".");
            }
            entries.Add(new BaseRunnerCatalogEntry(baseId, runnerKind, assetRoot,
                buildIdentity, baseWorkflow, executable, workingDirectory, arguments,
                immutableFiles, reportTemplate));
        }
        if (entries.Select(item => item.BaseId).Distinct(
                StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
            throw new DheException("Base runner catalog contains a duplicate Base ID.");
        return new BaseRunnerCatalog(path, Sha256File(path),
            GetString(document, "baseRegistryId") ?? string.Empty,
            GetInt(document, "baseRegistryRevision"),
            (GetString(document, "baseRegistrySha256") ??
                string.Empty).ToLowerInvariant(), entries.ToArray());
    }

    private static JsonElement ReadValidatedPlanningDocument(string path,
        string schemasRoot, string schemaName, string expectedFormat,
        string description)
    {
        string schemaPath = RequireFile(Path.Combine(schemasRoot, schemaName),
            "DHE " + description + " schema");
        JsonElement schema = ReadJson<JsonElement>(schemaPath);
        JsonElement document = ReadJson<JsonElement>(path);
        var errors = new List<string>();
        ValidateSchemaVocabulary(schema, "$", errors);
        if (errors.Count == 0)
            ValidateJsonSchema(schema, document, schema, "$", errors);
        if (errors.Count != 0 || !string.Equals(GetString(document, "format"),
                expectedFormat, StringComparison.Ordinal))
            throw new DheException("DHE " + description + " is invalid: " +
                string.Join("; ", errors.Take(16)));
        return document;
    }

    private static void ValidateResourceReleasePlanContinuation(
        ReleaseLedgerDocument ledger, ChannelSnapshotDocument snapshot)
    {
        if (!string.Equals(ledger.ChannelId, snapshot.ChannelId,
                StringComparison.Ordinal) || ledger.Revision != snapshot.NextRevision)
            throw new DheException("Resource candidate does not continue the protected " +
                "channel snapshot.");
        if (snapshot.Initialized)
        {
            if (!string.Equals(ledger.ParentLedgerSha256,
                    snapshot.PreviousReleaseLedgerSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("Resource candidate parent ledger does not match " +
                    "the protected channel snapshot.");
        }
        else if (ledger.Revision != 1 || ledger.ParentLedgerSha256 != null)
        {
            throw new DheException("A genesis resource plan must use revision 1 without " +
                "a parent ledger.");
        }
    }

    private static IReadOnlyDictionary<string, string>
        ValidateResourceReleasePlanAuthorities(string toolchainRoot,
            string currentPackageId, IEnumerable<string> roots)
    {
        IReadOnlyDictionary<string, string> packages = ReadEvidenceToolchainRoots(roots,
            currentPackageId);
        EvidenceAuthoritySet authoritySet = ReadEvidenceAuthoritySet(toolchainRoot,
            toolchainRoot, currentPackageId);
        foreach ((string packageId, string root) in packages)
        {
            if (!authoritySet.Authorities.TryGetValue(packageId,
                    out EvidenceAuthority? expected))
                throw new DheException("Historical evidence package is not authorized by " +
                    "the current toolchain: " + packageId + ".");
            JsonElement manifest = ReadJson<JsonElement>(RequireFile(Path.Combine(root,
                "dhe-toolchain-manifest.json"), "Historical toolchain manifest"));
            JsonElement source = manifest.GetProperty("sourceIdentity");
            ValidateAuthorizedHistoricalPackage(expected,
                new PlayerToolchainAuthority(root,
                    GetString(manifest, "toolchainVersion") ?? string.Empty,
                    GetString(manifest, "packageId") ?? string.Empty,
                    GetString(source, "head") ?? string.Empty,
                    GetString(source, "tree") ?? string.Empty));
        }
        return packages;
    }

    private static PlannedBaseRunner[] BuildResourceReleasePlanRunners(
        BaseRunnerCatalog catalog, JsonElement manifest, ReleaseLedgerDocument ledger)
    {
        JsonElement[] active = manifest.GetProperty("supportedBases").EnumerateArray()
            .Where(item => GetBool(item, "compatible")).ToArray();
        if (active.Length == 0 || active.Length != ledger.ActiveBaseCount ||
            active.Length != manifest.GetProperty("supportedBases").GetArrayLength())
            throw new DheException("Resource manifest active Base coverage is invalid.");
        var manifestBases = active.ToDictionary(item =>
            GetString(item, "baseId") ?? string.Empty, item => item,
            StringComparer.OrdinalIgnoreCase);
        string[] activeIds = manifestBases.Keys.OrderBy(value => value,
            StringComparer.OrdinalIgnoreCase).ToArray();
        string[] catalogIds = catalog.Entries.Select(item => item.BaseId)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!activeIds.SequenceEqual(catalogIds, StringComparer.OrdinalIgnoreCase))
            throw new DheException("Base runner catalog must exactly cover every active " +
                "Base in the resource candidate.");

        var results = new List<PlannedBaseRunner>();
        var reportPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BaseRunnerCatalogEntry entry in catalog.Entries.OrderBy(item =>
                     item.BaseId, StringComparer.OrdinalIgnoreCase))
        {
            JsonElement selected = manifestBases[entry.BaseId];
            string engineWorkflow = GetString(selected, "engineWorkflow") ?? string.Empty;
            string target = GetString(selected, "target") ?? string.Empty;
            string variantId = GetString(selected, "payloadVariantId") ?? "default";
            int expectedChanged = selected.GetProperty("assemblies").EnumerateArray()
                .Sum(item => GetInt(item, "changedMethodCount"));
            string? reportPath = null;
            if (entry.RunnerKind == QualificationProcessRunner)
            {
                JsonElement identity = ReadJson<JsonElement>(entry.BuildIdentity!);
                RequireEvidenceFormat(identity, "hybridclr.dhe-build-identity.json",
                    "Runner catalog Base identity");
                JsonElement workflow = ReadJson<JsonElement>(entry.BaseWorkflowReport!);
                RequireEvidenceFormat(workflow,
                    "hybridclr.dhe-project-player-workflow.json",
                    "Runner catalog Base workflow");
                if (!GetBool(workflow, "passed") ||
                    !string.Equals(GetString(identity, "baseId"), entry.BaseId,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(GetString(identity, "target"), target,
                        StringComparison.Ordinal) ||
                    !string.Equals(GetString(identity, "engineWorkflow"), engineWorkflow,
                        StringComparison.Ordinal) ||
                    !string.Equals(Sha256File(entry.BuildIdentity!),
                        GetString(selected, "buildIdentitySha256"),
                        StringComparison.OrdinalIgnoreCase))
                    throw new DheException("Process runner identity or workflow does not " +
                        "match Base " + entry.BaseId + ".");
            }
            else
            {
                reportPath = ExpandPrequalifiedReportTemplate(
                    entry.PlayerWorkflowReportTemplate!, catalog.SourcePath, ledger,
                    entry.BaseId);
                if (!reportPaths.Add(reportPath))
                    throw new DheException("Prequalified report templates resolve to a " +
                        "duplicate path: " + reportPath + ".");
            }
            foreach (string path in entry.ImmutableFiles)
                if (path.Contains(',', StringComparison.Ordinal))
                    throw new DheException("Runner catalog list paths cannot contain commas: " +
                        path);
            results.Add(new PlannedBaseRunner(entry, engineWorkflow, target, variantId,
                expectedChanged, reportPath));
        }
        return results.ToArray();
    }

    private static string ExpandPrequalifiedReportTemplate(string template,
        string catalogPath, ReleaseLedgerDocument ledger, string baseId)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{baseId}"] = baseId,
            ["{releaseChannelId}"] = ledger.ChannelId,
            ["{releaseRevision}"] = ledger.Revision.ToString(
                CultureInfo.InvariantCulture),
            ["{releaseLedgerSha256}"] = ledger.Sha256,
        };
        if (CountOrdinal(template, "{baseId}") != 1 ||
            CountOrdinal(template, "{releaseLedgerSha256}") != 1)
            throw new DheException("Prequalified report template must contain exact " +
                "{baseId} and {releaseLedgerSha256} tokens once each.");
        string expanded = template;
        foreach ((string token, string value) in tokens)
            expanded = expanded.Replace(token, value, StringComparison.Ordinal);
        if (expanded.Contains('{', StringComparison.Ordinal) ||
            expanded.Contains('}', StringComparison.Ordinal) ||
            expanded.Contains(',', StringComparison.Ordinal))
            throw new DheException("Prequalified report template contains an unknown " +
                "token or unsupported comma.");
        string fullPath = Path.GetFullPath(ResolveConfigPath(expanded,
            Path.GetDirectoryName(catalogPath)!));
        string normalized = fullPath.Replace('\\', '/');
        if (!normalized.Contains(baseId, StringComparison.OrdinalIgnoreCase) ||
            !normalized.Contains(ledger.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new DheException("Prequalified report path must retain the expanded " +
                "Base ID and release ledger SHA-256 after normalization.");
        return fullPath;
    }

    private static int CountOrdinal(string value, string token)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(token, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += token.Length;
        }
        return count;
    }

    private static IEnumerable<string> EnumerateResourceReleasePlanInputs(
        ResourceReleasePlanConfig config, BaseRunnerCatalog catalog)
    {
        yield return config.SourcePath;
        yield return config.ResourceUpdateRoot;
        yield return config.RunnerCatalog;
        yield return config.ChannelSnapshot;
        foreach (string root in config.EvidenceToolchainRoots) yield return root;
        foreach (BaseRunnerCatalogEntry item in catalog.Entries)
        {
            if (item.AssetRoot != null) yield return item.AssetRoot;
            if (item.BuildIdentity != null) yield return item.BuildIdentity;
            if (item.BaseWorkflowReport != null) yield return item.BaseWorkflowReport;
            if (item.PlayerExecutable != null) yield return item.PlayerExecutable;
            if (item.WorkingDirectory != null) yield return item.WorkingDirectory;
            foreach (string path in item.ImmutableFiles) yield return path;
        }
    }

    private static void ValidateResourceReleasePlanOutputs(string planRoot,
        string qualificationRoot, string toolchainRoot, string schemasRoot,
        string stateRoot, IEnumerable<string> inputs)
    {
        EnsureOutputOutsideRoot(planRoot, toolchainRoot);
        EnsureOutputOutsideRoot(planRoot, schemasRoot);
        EnsureOutputOutsideRoot(planRoot, stateRoot);
        EnsureOutputOutsideRoot(qualificationRoot, toolchainRoot);
        EnsureOutputOutsideRoot(qualificationRoot, schemasRoot);
        EnsureOutputOutsideRoot(qualificationRoot, stateRoot);
        EnsureOutputOutsideRoot(qualificationRoot, planRoot);
        EnsureOutputNotAncestor(qualificationRoot, planRoot);
        foreach (string input in inputs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            EnsureOutputNotAncestor(planRoot, input);
            EnsureOutputNotAncestor(qualificationRoot, input);
            if (Directory.Exists(input))
            {
                EnsureOutputOutsideRoot(planRoot, input);
                EnsureOutputOutsideRoot(qualificationRoot, input);
            }
        }
    }

    private static ResourceReleasePlanRegressionResult
        RunResourceReleasePlanningRegression(string regressionRoot,
            string resourceUpdateRoot, string channelSnapshot,
            string channelSnapshotSha256,
            IReadOnlyCollection<(JsonElement Report, string Path)> reports,
            string authorityRoot, string authorityPackageId, string schemaRoot,
            IReadOnlyCollection<string> evidenceToolchainRoots)
    {
        string root = Path.Combine(regressionRoot,
            "resource-release-planning-regression");
        Directory.CreateDirectory(root);
        string schemasRoot = Directory.Exists(Path.Combine(schemaRoot, "schemas"))
            ? Path.Combine(schemaRoot, "schemas") : schemaRoot;
        JsonElement manifest = ReadJson<JsonElement>(RequireFile(Path.Combine(
            resourceUpdateRoot, "dhe-resource-update.json"),
            "Planning regression resource manifest"));
        ReleaseLedgerDocument ledger = ValidateReleaseLedgerForStaging(
            resourceUpdateRoot, manifest) ?? throw new DheException(
            "Planning regression resource ledger is missing.");
        string reportRoot = Path.Combine(root, "platform-results");
        string reportTemplate = Path.Combine(reportRoot, "{releaseLedgerSha256}",
            "{baseId}", "resource-player-workflow-report.json");

        object CatalogEntry((JsonElement Report, string Path) item,
            string? template = null) => new
        {
            baseId = GetString(item.Report, "selectedBaseId"),
            runnerKind = QualificationPrequalifiedRunner,
            assetRoot = (string?)null,
            buildIdentity = (string?)null,
            baseWorkflowReport = (string?)null,
            playerExecutable = (string?)null,
            workingDirectory = (string?)null,
            playerArguments = (string[]?)null,
            immutableFiles = Array.Empty<string>(),
            playerWorkflowReportTemplate = template ?? reportTemplate,
        };

        string WriteCatalog(string name, object[] bases, int? revision = null,
            string? sha256 = null)
        {
            string path = Path.Combine(root, name + "-catalog.json");
            WriteJson(path, new
            {
                schemaVersion = 1,
                format = BaseRunnerCatalogFormat,
                pathSemantics = "config-relative-v1",
                baseRegistryId = ledger.BaseRegistryId,
                baseRegistryRevision = revision ?? ledger.BaseRegistryRevision,
                baseRegistrySha256 = sha256 ?? ledger.BaseRegistrySha256,
                bases,
            });
            return path;
        }

        string WritePlanConfig(string name, string catalog,
            string expectedSnapshotSha256)
        {
            string path = Path.Combine(root, name + "-config.json");
            WriteJson(path, new
            {
                schemaVersion = 1,
                format = ResourceReleasePlanConfigFormat,
                pathSemantics = "config-relative-v1",
                resourceUpdateRoot,
                runnerCatalog = catalog,
                outputRoot = Path.Combine(root, name + "-plan"),
                qualificationOutputRoot = Path.Combine(root,
                    name + "-qualification"),
                channelSnapshot,
                expectedChannelSnapshotSha256 = expectedSnapshotSha256,
                expectedToolchainPackageId = authorityPackageId,
                evidenceToolchainRoots = evidenceToolchainRoots.ToArray(),
                requireEngineMatrix = true,
                timeoutSeconds = 30,
            });
            return path;
        }

        int RunPlan(string configPath) => ResourceReleasePlan(new Cli(
            "resource-release-plan", new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["root"] = authorityRoot,
                ["schemasroot"] = schemasRoot,
                ["config"] = configPath,
            }));

        bool RejectPlan(string name, object[] bases, int? registryRevision = null,
            string? registrySha256 = null, string? snapshotSha256 = null)
        {
            string catalog = WriteCatalog(name, bases, registryRevision,
                registrySha256);
            string config = WritePlanConfig(name, catalog,
                snapshotSha256 ?? channelSnapshotSha256);
            string output = Path.Combine(root, name + "-plan");
            try
            {
                _ = RunPlan(config);
                return false;
            }
            catch (DheException)
            {
                return !Directory.Exists(output) && !Directory.GetDirectories(root,
                    "." + name + "-plan.staging-*", SearchOption.TopDirectoryOnly).Any();
            }
        }

        bool generatedQualificationPassed = false;
        bool exactCoverageRejected = false;
        bool duplicateBaseRejected = false;
        bool registryIdentityRejected = false;
        bool templateContractRejected = false;
        bool staleSnapshotRejected = false;
        string details = ResourceReleasePlanRegressionResult.Failed.Details;
        try
        {
            object[] allBases = reports.Select(item => CatalogEntry(item)).ToArray();
            string successCatalog = WriteCatalog("success", allBases);
            string successConfig = WritePlanConfig("success", successCatalog,
                channelSnapshotSha256);
            if (RunPlan(successConfig) != 0)
                throw new DheException("Resource release planning regression failed.");
            string successPlanRoot = Path.Combine(root, "success-plan");
            JsonElement plan = ReadJson<JsonElement>(Path.Combine(successPlanRoot,
                "dhe-resource-release-plan.json"));
            string qualificationConfig = Path.Combine(successPlanRoot,
                GetString(plan, "qualificationConfig") ?? string.Empty);
            var sourceReports = reports.ToDictionary(item =>
                GetString(item.Report, "selectedBaseId") ?? string.Empty,
                item => item.Path, StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement job in plan.GetProperty("jobs").EnumerateArray())
            {
                string baseId = GetString(job, "baseId") ?? string.Empty;
                string destination = GetString(job, "playerWorkflowReport") ??
                    string.Empty;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(sourceReports[baseId], destination, false);
            }
            int qualificationExit = ResourceReleaseQualify(new Cli(
                "resource-release-qualify", new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["root"] = authorityRoot,
                    ["schemasroot"] = schemasRoot,
                    ["config"] = qualificationConfig,
                }));
            string qualificationRoot = Path.Combine(root,
                "success-qualification");
            JsonElement qualification = ReadJson<JsonElement>(Path.Combine(
                qualificationRoot, "dhe-resource-release-qualification.json"));
            generatedQualificationPassed = qualificationExit == 0 &&
                GetBool(plan, "passed") && GetBool(plan, "releaseReady") &&
                GetBool(plan, "exactActiveBaseCoverage") &&
                GetBool(plan, "engineMatrixCovered") &&
                GetInt(plan, "activeBaseCount") == reports.Count &&
                GetInt(plan, "prequalifiedRunnerCount") == reports.Count &&
                string.Equals(Sha256File(qualificationConfig),
                    GetString(plan, "qualificationConfigSha256"),
                    StringComparison.OrdinalIgnoreCase) &&
                GetBool(qualification, "passed") &&
                GetBool(qualification, "exactActiveBaseCoverage") &&
                GetInt(qualification, "activeBaseCount") == reports.Count;

            exactCoverageRejected = RejectPlan("missing-base",
                allBases.Skip(1).ToArray());
            duplicateBaseRejected = RejectPlan("duplicate-base",
                allBases.Concat(new[] { allBases[0] }).ToArray());
            registryIdentityRejected = RejectPlan("registry-revision", allBases,
                    registryRevision: ledger.BaseRegistryRevision + 1) &&
                RejectPlan("registry-sha", allBases,
                    registrySha256: new string('e', 64));

            object missingToken = CatalogEntry(reports.First(), Path.Combine(reportRoot,
                "{releaseLedgerSha256}", "missing-base",
                "resource-player-workflow-report.json"));
            object[] missingTokenBases = allBases.ToArray();
            missingTokenBases[0] = missingToken;
            object unknownToken = CatalogEntry(reports.First(), Path.Combine(reportRoot,
                "{releaseLedgerSha256}", "{baseId}", "{unknown}",
                "resource-player-workflow-report.json"));
            object[] unknownTokenBases = allBases.ToArray();
            unknownTokenBases[0] = unknownToken;
            templateContractRejected = RejectPlan("missing-token",
                    missingTokenBases) && RejectPlan("unknown-token", unknownTokenBases);
            staleSnapshotRejected = RejectPlan("stale-snapshot", allBases,
                snapshotSha256: new string('f', 64));
            details = "one registry-bound plan generated and executed an exact-coverage " +
                "qualification and rejected missing, duplicate, registry, template, " +
                "and stale-snapshot inputs";
        }
        catch (Exception exception)
        {
            details = exception.Message;
        }
        return new ResourceReleasePlanRegressionResult(generatedQualificationPassed,
            exactCoverageRejected, duplicateBaseRejected, registryIdentityRejected,
            templateContractRejected, staleSnapshotRejected, details);
    }
}
