using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace HybridCLR.DheTool;

internal static partial class Program
{
    private const string QualificationProcessRunner = "process";
    private const string QualificationPrequalifiedRunner = "prequalified";
    private const string QualificationResultToken = "{result}";
    private const string QualificationLogToken = "{log}";

    private sealed record ResourceReleaseQualificationRegressionResult(
        bool PrequalifiedPassed,
        bool ExactCoverageRejected,
        bool ProcessContractRejected,
        bool StaleSnapshotRejected,
        string Details)
    {
        internal static readonly ResourceReleaseQualificationRegressionResult Failed =
            new(false, false, false, false,
                "multi-Base resource release qualification regression did not complete");
    }

    private sealed record ResourceReleaseQualificationBase(
        string BaseId,
        string RunnerKind,
        string? AssetRoot,
        string? BuildIdentity,
        string? BaseWorkflowReport,
        string? PlayerExecutable,
        string? WorkingDirectory,
        string[]? PlayerArguments,
        string[] ImmutableFiles,
        string? PlayerWorkflowReport);

    private sealed record ResourceReleaseQualificationConfig(
        string SourcePath,
        string Sha256,
        string ResourceUpdateRoot,
        string OutputRoot,
        string ChannelSnapshot,
        string ExpectedChannelSnapshotSha256,
        string ExpectedToolchainPackageId,
        string[] EvidenceToolchainRoots,
        bool RequireEngineMatrix,
        int TimeoutSeconds,
        ResourceReleaseQualificationBase[] Bases);

    private sealed record ResourceReleaseQualificationEvidence(
        string BaseId,
        string RunnerKind,
        string EngineWorkflow,
        string Target,
        string PayloadVariantId,
        int ChangedMethodCount,
        int ExpectedChangedMethodCount,
        int InterpreterEntryCount,
        int AotEntryCount,
        int? ProcessId,
        int? ExitCode,
        long? DurationMilliseconds,
        string? AssetRoot,
        string? StageReport,
        string? StageReportSha256,
        string? PlayerResult,
        string? PlayerResultSha256,
        string? PlayerLog,
        string PlayerWorkflowReport,
        string PlayerWorkflowReportSha256,
        bool OutputOwned);

    private static int ResourceReleaseQualify(Cli cli)
    {
        string configPath = RequireFile(cli.Require("config"),
            "DHE resource release qualification config");
        string schemasRoot = RequireDirectory(cli.Optional("schemasroot") ??
            Path.Combine(cli.Root, "schemas"), "DHE schemas root");
        ResourceReleaseQualificationConfig config =
            ReadResourceReleaseQualificationConfig(configPath, schemasRoot);
        string toolchainRoot = RequireDirectory(cli.Root,
            "DHE resource release qualification toolchain");
        PackageInspection package = InspectPackage(toolchainRoot,
            config.ExpectedToolchainPackageId, true);
        if (!package.Passed)
            throw new DheException("Resource release qualification requires the exact " +
                "authenticated Release toolchain: " + string.Join("; ", package.Errors));

        string manifestPath = RequireFile(Path.Combine(config.ResourceUpdateRoot,
            "dhe-resource-update.json"), "DHE resource update manifest");
        string manifestSha256 = Sha256File(manifestPath);
        JsonElement manifest = ReadJson<JsonElement>(manifestPath);
        RequireEvidenceFormat(manifest, "hybridclr.dhe-resource-update.json",
            "Resource update manifest");
        ReleaseLedgerDocument? ledger = ValidateReleaseLedgerForStaging(
            config.ResourceUpdateRoot, manifest);
        string validationPath = ValidateResourceUpdateCompatibility(
            config.ResourceUpdateRoot, manifest);
        string validationSha256 = Sha256File(validationPath);
        if (ledger == null || !string.Equals(GetString(manifest, "mode"), "Release",
                StringComparison.Ordinal) || !GetBool(manifest, "releaseReady"))
            throw new DheException("Resource release qualification requires a Release-ready " +
                "resource candidate with an authenticated ledger.");
        ChannelSnapshotDocument snapshot = ReadChannelSnapshot(config.ChannelSnapshot,
            config.ExpectedChannelSnapshotSha256);

        string[] activeBaseIds = manifest.GetProperty("supportedBases").EnumerateArray()
            .Where(item => GetBool(item, "compatible"))
            .Select(item => GetString(item, "baseId") ?? string.Empty)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] configuredBaseIds = config.Bases.Select(item => item.BaseId)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        if (activeBaseIds.Length == 0 || activeBaseIds.Length !=
                manifest.GetProperty("supportedBases").GetArrayLength() ||
            !activeBaseIds.SequenceEqual(configuredBaseIds,
                StringComparer.OrdinalIgnoreCase))
            throw new DheException("Qualification runners must exactly cover every compatible " +
                "active Base in the resource manifest.");

        ValidateResourceReleaseQualificationInputs(config, manifest, toolchainRoot,
            schemasRoot, snapshot.StateRoot);
        string outputRoot = SafeOutputRoot(config.OutputRoot,
            EnumerateResourceReleaseQualificationInputs(config));
        if (Directory.Exists(outputRoot) || File.Exists(outputRoot))
            throw new DheException("Resource release qualification output must be a new path.");
        ValidateResourceReleaseQualificationOutput(config, outputRoot, toolchainRoot,
            schemasRoot, snapshot.StateRoot);

        string gatePath = Path.Combine(outputRoot, "dhe-resource-release-gate.json");
        string summaryPath = Path.Combine(outputRoot,
            "dhe-resource-release-qualification.json");
        try
        {
            Directory.CreateDirectory(outputRoot);
            string auditRoot = Path.Combine(outputRoot, "audit");
            Directory.CreateDirectory(auditRoot);
            string auditConfig = Path.Combine(auditRoot,
                "dhe-resource-release-qualification-config.json");
            File.Copy(config.SourcePath, auditConfig, false);
            if (!string.Equals(Sha256File(auditConfig), config.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("Qualification config changed while it was copied.");

            var evidence = new List<ResourceReleaseQualificationEvidence>();
            foreach (ResourceReleaseQualificationBase item in config.Bases.OrderBy(
                         value => value.BaseId, StringComparer.OrdinalIgnoreCase))
            {
                evidence.Add(item.RunnerKind == QualificationProcessRunner
                    ? RunQualificationProcess(config, item, outputRoot)
                    : ReadPrequalifiedEvidence(item));
            }

            string[] playerReports = evidence.Select(item => item.PlayerWorkflowReport)
                .ToArray();
            var gateArguments = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["toolchainroot"] = toolchainRoot,
                ["expectedtoolchainpackageid"] = config.ExpectedToolchainPackageId,
                ["schemaroot"] = toolchainRoot,
                ["resourceupdateroot"] = config.ResourceUpdateRoot,
                ["channelsnapshot"] = config.ChannelSnapshot,
                ["expectedchannelsnapshotsha256"] =
                    config.ExpectedChannelSnapshotSha256,
                ["expectedreleaseledgersha256"] = ledger.Sha256,
                ["changedplayers"] = string.Join(',', playerReports),
                ["output"] = gatePath,
            };
            if (config.RequireEngineMatrix)
                gateArguments["requireenginematrix"] = "true";
            if (config.EvidenceToolchainRoots.Length != 0)
                gateArguments["evidencetoolchainroots"] = string.Join(',',
                    config.EvidenceToolchainRoots);
            if (ResourceReleaseGate(new Cli("resource-release-gate", gateArguments)) != 0)
                throw new DheException("Resource release aggregate gate failed.");

            JsonElement gate = ReadJson<JsonElement>(gatePath);
            if (!GetBool(gate, "passed") || !GetBool(gate, "releaseReady") ||
                !GetBool(gate, "exactActiveBaseCoverage") ||
                GetInt(gate, "activeBaseCount") != activeBaseIds.Length ||
                GetInt(gate, "releaseRevision") != ledger.Revision ||
                !string.Equals(GetString(gate, "releaseLedgerSha256"), ledger.Sha256,
                    StringComparison.OrdinalIgnoreCase) ||
                config.RequireEngineMatrix && !GetBool(gate, "engineMatrixCovered"))
                throw new DheException("Resource release aggregate gate did not preserve the " +
                    "qualification contract.");
            ValidateQualificationGateEvidence(config, evidence, gate, manifestPath,
                manifestSha256, validationPath, validationSha256);

            WriteJson(summaryPath, new
            {
                schemaVersion = 1,
                format = "hybridclr.dhe-resource-release-qualification.json",
                generatedAtUtc = DateTimeOffset.UtcNow,
                passed = true,
                releaseReady = true,
                pathSemantics =
                    "qualification-owned-relative-external-absolute-v1",
                configuration = QualificationOwnedPath(auditConfig, outputRoot),
                configurationSha256 = config.Sha256,
                toolchainRoot,
                toolchainPackageId = package.PackageId,
                resourceUpdateRoot = config.ResourceUpdateRoot,
                resourceUpdateManifest = manifestPath,
                resourceUpdateManifestSha256 = manifestSha256,
                resourceUpdateValidation = validationPath,
                resourceUpdateValidationSha256 = validationSha256,
                releaseChannelId = ledger.ChannelId,
                releaseRevision = ledger.Revision,
                releaseLedgerSha256 = ledger.Sha256,
                channelSnapshot = config.ChannelSnapshot,
                channelSnapshotSha256 = config.ExpectedChannelSnapshotSha256,
                activeBaseCount = activeBaseIds.Length,
                processRunnerCount = evidence.Count(item => item.RunnerKind ==
                    QualificationProcessRunner),
                prequalifiedRunnerCount = evidence.Count(item => item.RunnerKind ==
                    QualificationPrequalifiedRunner),
                requireEngineMatrix = config.RequireEngineMatrix,
                engineMatrixCovered = GetBool(gate, "engineMatrixCovered"),
                exactActiveBaseCoverage = true,
                playerReports = evidence.Select(item => new
                {
                    item.BaseId,
                    item.RunnerKind,
                    item.EngineWorkflow,
                    item.Target,
                    item.PayloadVariantId,
                    item.ChangedMethodCount,
                    item.ExpectedChangedMethodCount,
                    item.InterpreterEntryCount,
                    item.AotEntryCount,
                    item.ProcessId,
                    item.ExitCode,
                    item.DurationMilliseconds,
                    item.AssetRoot,
                    item.StageReport,
                    item.StageReportSha256,
                    item.PlayerResult,
                    item.PlayerResultSha256,
                    item.PlayerLog,
                    playerWorkflowReport = item.OutputOwned
                        ? QualificationOwnedPath(item.PlayerWorkflowReport, outputRoot)
                        : item.PlayerWorkflowReport,
                    item.PlayerWorkflowReportSha256,
                    item.OutputOwned,
                }).ToArray(),
                resourceReleaseGate = QualificationOwnedPath(gatePath, outputRoot),
                resourceReleaseGateSha256 = Sha256File(gatePath),
                promotionRequired = true,
                errors = Array.Empty<string>(),
                warnings = config.RequireEngineMatrix ? Array.Empty<string>() : new[]
                {
                    "The qualification did not require the complete three-engine matrix.",
                },
            });

            int schemaExit = SchemaGate(new Cli("schema-gate",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["schemasroot"] = schemasRoot,
                    ["inputroot"] = outputRoot,
                    ["output"] = Path.Combine(auditRoot, "dhe-schema-gate.json"),
                    ["requireknownformats"] = "true",
                }));
            if (schemaExit != 0)
                throw new DheException("Resource release qualification schema gate failed.");
            Console.WriteLine("DHE resource release qualification: " + outputRoot);
            return 0;
        }
        catch
        {
            if (File.Exists(summaryPath)) File.Delete(summaryPath);
            if (File.Exists(gatePath)) File.Delete(gatePath);
            throw;
        }
    }

    private static ResourceReleaseQualificationConfig
        ReadResourceReleaseQualificationConfig(string path, string schemasRoot)
    {
        string schemaPath = RequireFile(Path.Combine(schemasRoot,
            "dhe-resource-release-qualification-config.schema.json"),
            "DHE resource release qualification config schema");
        JsonElement schema = ReadJson<JsonElement>(schemaPath);
        JsonElement document = ReadJson<JsonElement>(path);
        var errors = new List<string>();
        ValidateSchemaVocabulary(schema, "$", errors);
        if (errors.Count == 0)
            ValidateJsonSchema(schema, document, schema, "$", errors);
        if (errors.Count != 0)
            throw new DheException("DHE resource release qualification config is invalid: " +
                string.Join("; ", errors.Take(16)));

        string configDirectory = Path.GetDirectoryName(path)!;
        string Resolve(string value) => ResolveConfigPath(value, configDirectory);
        string? ResolveOptional(JsonElement parent, string property, bool directory)
        {
            string? value = GetString(parent, property);
            if (string.IsNullOrWhiteSpace(value)) return null;
            string resolved = Resolve(value);
            return directory
                ? RequireDirectory(resolved, "Qualification " + property)
                : RequireFile(resolved, "Qualification " + property);
        }

        var bases = new List<ResourceReleaseQualificationBase>();
        foreach (JsonElement item in document.GetProperty("bases").EnumerateArray())
        {
            string baseId = (GetString(item, "baseId") ?? string.Empty).ToLowerInvariant();
            string runnerKind = GetString(item, "runnerKind") ?? string.Empty;
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
            string[] immutableFiles = item.GetProperty("immutableFiles").EnumerateArray()
                .Select(value => RequireFile(Resolve(value.GetString() ?? string.Empty),
                    "Qualification immutable Player file")).ToArray();
            string? playerWorkflow = ResolveOptional(item, "playerWorkflowReport", false);

            if (runnerKind == QualificationProcessRunner)
            {
                if (assetRoot == null || buildIdentity == null || baseWorkflow == null ||
                    executable == null || workingDirectory == null || arguments == null ||
                    playerWorkflow != null || immutableFiles.Length == 0)
                    throw new DheException("Process runner " + baseId +
                        " has incomplete or conflicting inputs.");
                if (arguments.Count(value => value == QualificationResultToken) != 1 ||
                    arguments.Count(value => value == QualificationLogToken) != 1)
                    throw new DheException("Process runner " + baseId + " must contain exact " +
                        "{result} and {log} argument tokens once each.");
                if (!immutableFiles.Contains(executable, StringComparer.OrdinalIgnoreCase))
                    throw new DheException("Process runner " + baseId +
                        " must include PlayerExecutable in ImmutableFiles.");
            }
            else if (runnerKind == QualificationPrequalifiedRunner)
            {
                if (assetRoot != null || buildIdentity != null || baseWorkflow != null ||
                    executable != null || workingDirectory != null || arguments != null ||
                    immutableFiles.Length != 0 || playerWorkflow == null)
                    throw new DheException("Prequalified runner " + baseId +
                        " must contain only PlayerWorkflowReport evidence.");
            }
            else
            {
                throw new DheException("Qualification runnerKind is unsupported: " +
                    runnerKind + ".");
            }
            bases.Add(new ResourceReleaseQualificationBase(baseId, runnerKind, assetRoot,
                buildIdentity, baseWorkflow, executable, workingDirectory, arguments,
                immutableFiles, playerWorkflow));
        }
        if (bases.Select(item => item.BaseId).Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != bases.Count)
            throw new DheException("Qualification config contains a duplicate Base ID.");

        string[] evidenceRoots = document.GetProperty("evidenceToolchainRoots")
            .EnumerateArray().Select(item => RequireDirectory(
                Resolve(item.GetString() ?? string.Empty),
                "Historical evidence toolchain package")).ToArray();
        var config = new ResourceReleaseQualificationConfig(path, Sha256File(path),
            RequireDirectory(Resolve(GetString(document, "resourceUpdateRoot") ??
                string.Empty), "DHE resource update root"),
            Resolve(GetString(document, "outputRoot") ?? string.Empty),
            RequireFile(Resolve(GetString(document, "channelSnapshot") ?? string.Empty),
                "DHE channel snapshot"),
            (GetString(document, "expectedChannelSnapshotSha256") ??
                string.Empty).ToLowerInvariant(),
            (GetString(document, "expectedToolchainPackageId") ??
                string.Empty).ToLowerInvariant(),
            evidenceRoots, GetBool(document, "requireEngineMatrix"),
            GetInt(document, "timeoutSeconds"), bases.ToArray());
        foreach (string pathValue in EnumerateResourceReleaseQualificationListValues(config))
            if (pathValue.Contains(',', StringComparison.Ordinal))
                throw new DheException("Qualification list paths cannot contain commas: " +
                    pathValue);
        return config;
    }

    private static void ValidateResourceReleaseQualificationInputs(
        ResourceReleaseQualificationConfig config, JsonElement manifest,
        string toolchainRoot, string schemasRoot, string stateRoot)
    {
        var manifestBases = manifest.GetProperty("supportedBases").EnumerateArray()
            .ToDictionary(item => GetString(item, "baseId") ?? string.Empty, item => item,
                StringComparer.OrdinalIgnoreCase);
        foreach (ResourceReleaseQualificationBase item in config.Bases)
        {
            if (!manifestBases.ContainsKey(item.BaseId))
                throw new DheException("Qualification Base is absent from the resource " +
                    "manifest: " + item.BaseId + ".");
            if (item.RunnerKind == QualificationProcessRunner)
            {
                JsonElement identity = ReadJson<JsonElement>(item.BuildIdentity!);
                if (!string.Equals(GetString(identity, "baseId"), item.BaseId,
                        StringComparison.OrdinalIgnoreCase))
                    throw new DheException("Process BuildIdentity does not match Base " +
                        item.BaseId + ".");
                JsonElement workflow = ReadJson<JsonElement>(item.BaseWorkflowReport!);
                RequireEvidenceFormat(workflow,
                    "hybridclr.dhe-project-player-workflow.json", "Base workflow");
                if (!GetBool(workflow, "passed"))
                    throw new DheException("Process Base workflow did not pass: " +
                        item.BaseId + ".");
            }
            else
            {
                JsonElement report = ReadJson<JsonElement>(item.PlayerWorkflowReport!);
                RequireEvidenceFormat(report,
                    "hybridclr.dhe-resource-player-workflow.json",
                    "Prequalified resource Player workflow");
                if (!GetBool(report, "passed") ||
                    !string.Equals(GetString(report, "selectedBaseId"), item.BaseId,
                        StringComparison.OrdinalIgnoreCase))
                    throw new DheException("Prequalified Player report does not match Base " +
                        item.BaseId + ".");
            }
        }
        _ = RequireDirectory(toolchainRoot, "Qualification toolchain root");
        _ = RequireDirectory(schemasRoot, "Qualification schemas root");
        _ = RequireDirectory(stateRoot, "Qualification channel state root");
    }

    private static ResourceReleaseQualificationEvidence RunQualificationProcess(
        ResourceReleaseQualificationConfig config,
        ResourceReleaseQualificationBase item, string outputRoot)
    {
        string baseRoot = Path.Combine(outputRoot, "bases", item.BaseId);
        Directory.CreateDirectory(baseRoot);
        string stagePath = Path.Combine(baseRoot, "dhe-resource-stage.json");
        var stageArguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["updateroot"] = config.ResourceUpdateRoot,
            ["assetroot"] = item.AssetRoot!,
            ["basebuildidentity"] = item.BuildIdentity!,
            ["immutablefiles"] = string.Join(',', item.ImmutableFiles),
            ["output"] = stagePath,
        };
        if (StageResourceUpdate(new Cli("stage-resource-update", stageArguments)) != 0)
            throw new DheException("Resource staging failed for Base " + item.BaseId + ".");

        string resultPath = Path.Combine(baseRoot, "dhe-player-result.json");
        string logPath = Path.Combine(baseRoot, "dhe-player.log");
        var start = new ProcessStartInfo(item.PlayerExecutable!)
        {
            WorkingDirectory = item.WorkingDirectory!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in item.PlayerArguments!)
        {
            start.ArgumentList.Add(argument switch
            {
                QualificationResultToken => resultPath,
                QualificationLogToken => logPath,
                "{assetRoot}" => item.AssetRoot!,
                "{baseId}" => item.BaseId,
                _ => argument,
            });
        }

        int processId;
        int exitCode;
        var stopwatch = Stopwatch.StartNew();
        using (Process process = Process.Start(start) ??
               throw new DheException("Unable to start Player for Base " + item.BaseId + "."))
        {
            processId = process.Id;
            if (!process.WaitForExit(config.TimeoutSeconds * 1000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new DheException("Player timed out for Base " + item.BaseId +
                    " after " + config.TimeoutSeconds + " seconds.");
            }
            exitCode = process.ExitCode;
        }
        stopwatch.Stop();
        if (exitCode != 0)
            throw new DheException("Player exited with code " + exitCode + " for Base " +
                item.BaseId + ". See " + logPath + ".");
        _ = RequireFile(logPath, "Qualification Player log");
        ValidateQualificationImmutableFiles(item, stagePath);
        JsonElement player = ReadJson<JsonElement>(RequireFile(resultPath,
            "Qualification Player result"));
        RequireEvidenceFormat(player, "hybridclr.dhe-player-result.json",
            "Qualification Player result");
        if (!GetBool(player, "passed") ||
            !string.Equals(GetString(player, "selectedBaseId"), item.BaseId,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Player result did not pass for Base " +
                item.BaseId + ".");

        string workflowPath = Path.Combine(baseRoot,
            "resource-player-workflow-report.json");
        if (ResourcePlayerEvidence(new Cli("resource-player-evidence",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["resourceupdateroot"] = config.ResourceUpdateRoot,
                    ["stagereport"] = stagePath,
                    ["playerresult"] = resultPath,
                    ["baseworkflowreport"] = item.BaseWorkflowReport!,
                    ["output"] = workflowPath,
                })) != 0)
            throw new DheException("Resource Player evidence failed for Base " +
                item.BaseId + ".");
        JsonElement workflow = ReadJson<JsonElement>(workflowPath);
        if (!GetBool(workflow, "passed") ||
            !string.Equals(GetString(workflow, "selectedBaseId"), item.BaseId,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Resource Player workflow did not pass for Base " +
                item.BaseId + ".");

        return CreateQualificationEvidence(item, workflow, workflowPath, true,
            processId, exitCode, checked((long)stopwatch.Elapsed.TotalMilliseconds),
            item.AssetRoot, QualificationOwnedPath(stagePath, outputRoot),
            Sha256File(stagePath), QualificationOwnedPath(resultPath, outputRoot),
            Sha256File(resultPath), QualificationOwnedPath(logPath, outputRoot));
    }

    private static ResourceReleaseQualificationEvidence ReadPrequalifiedEvidence(
        ResourceReleaseQualificationBase item)
    {
        string reportPath = RequireFile(item.PlayerWorkflowReport!,
            "Prequalified resource Player workflow");
        JsonElement report = ReadJson<JsonElement>(reportPath);
        RequireEvidenceFormat(report, "hybridclr.dhe-resource-player-workflow.json",
            "Prequalified resource Player workflow");
        if (!GetBool(report, "passed") ||
            !string.Equals(GetString(report, "selectedBaseId"), item.BaseId,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Prequalified Player report changed before qualification " +
                "for Base " + item.BaseId + ".");
        return CreateQualificationEvidence(item, report, reportPath, false,
            null, null, null, null, null, null, null, null, null);
    }

    private static ResourceReleaseQualificationEvidence CreateQualificationEvidence(
        ResourceReleaseQualificationBase item, JsonElement workflow, string workflowPath,
        bool outputOwned, int? processId, int? exitCode, long? durationMilliseconds,
        string? assetRoot, string? stageReport, string? stageReportSha256,
        string? playerResult, string? playerResultSha256, string? playerLog)
    {
        JsonElement player = workflow.GetProperty("player");
        return new ResourceReleaseQualificationEvidence(item.BaseId, item.RunnerKind,
            GetString(workflow, "engineWorkflow") ?? string.Empty,
            GetString(workflow, "target") ?? string.Empty,
            GetString(workflow, "selectedPayloadVariantId") ??
                GetString(player, "selectedPayloadVariantId") ?? "default",
            GetInt(player, "changedMethodCount"),
            GetInt(player, "expectedChangedMethodCount"),
            GetInt(player, "interpreterEntryCount"), GetInt(player, "aotEntryCount"),
            processId, exitCode, durationMilliseconds, assetRoot, stageReport,
            stageReportSha256, playerResult, playerResultSha256, playerLog,
            workflowPath, Sha256File(workflowPath), outputOwned);
    }

    private static void ValidateQualificationImmutableFiles(
        ResourceReleaseQualificationBase item, string stagePath)
    {
        JsonElement stage = ReadJson<JsonElement>(stagePath);
        var staged = stage.GetProperty("immutableFiles").EnumerateArray()
            .ToDictionary(value => Path.GetFullPath(GetString(value, "path") ??
                string.Empty), value => GetString(value, "sha256After") ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
        if (staged.Count != item.ImmutableFiles.Length)
            throw new DheException("Immutable Player evidence count changed for Base " +
                item.BaseId + ".");
        foreach (string path in item.ImmutableFiles)
        {
            string fullPath = Path.GetFullPath(path);
            if (!staged.TryGetValue(fullPath, out string? expected) ||
                !string.Equals(Sha256File(fullPath), expected,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("Immutable Player file changed during execution for " +
                    "Base " + item.BaseId + ": " + fullPath + ".");
        }
    }

    private static void ValidateQualificationGateEvidence(
        ResourceReleaseQualificationConfig config,
        IReadOnlyCollection<ResourceReleaseQualificationEvidence> evidence,
        JsonElement gate, string manifestPath, string manifestSha256,
        string validationPath, string validationSha256)
    {
        if (!string.Equals(Sha256File(config.SourcePath), config.Sha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Sha256File(manifestPath), manifestSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Sha256File(validationPath), validationSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(gate, "resourceUpdateManifestSha256"),
                manifestSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(gate, "resourceUpdateValidationSha256"),
                validationSha256, StringComparison.OrdinalIgnoreCase))
            throw new DheException("Qualification inputs changed during aggregate validation.");

        var expected = evidence.ToDictionary(item => item.BaseId,
            StringComparer.OrdinalIgnoreCase);
        JsonElement[] reports = gate.GetProperty("playerReports").EnumerateArray().ToArray();
        if (reports.Length != expected.Count)
            throw new DheException("Aggregate gate Player report count changed.");
        foreach (JsonElement report in reports)
        {
            string baseId = GetString(report, "baseId") ?? string.Empty;
            if (!expected.TryGetValue(baseId,
                    out ResourceReleaseQualificationEvidence? item) ||
                !string.Equals(GetString(report, "reportSha256"),
                    item.PlayerWorkflowReportSha256, StringComparison.OrdinalIgnoreCase) ||
                GetInt(report, "changedMethodCount") != item.ChangedMethodCount ||
                GetInt(report, "interpreterEntryCount") != item.InterpreterEntryCount ||
                GetInt(report, "aotEntryCount") != item.AotEntryCount ||
                !string.Equals(GetString(report, "engineWorkflow"), item.EngineWorkflow,
                    StringComparison.Ordinal) ||
                !string.Equals(GetString(report, "payloadVariantId"), item.PayloadVariantId,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("Aggregate gate changed Player evidence for Base " +
                    baseId + ".");
        }
    }

    private static IEnumerable<string> EnumerateResourceReleaseQualificationInputs(
        ResourceReleaseQualificationConfig config)
    {
        yield return config.SourcePath;
        yield return config.ResourceUpdateRoot;
        yield return config.ChannelSnapshot;
        foreach (string root in config.EvidenceToolchainRoots) yield return root;
        foreach (ResourceReleaseQualificationBase item in config.Bases)
        {
            if (item.AssetRoot != null) yield return item.AssetRoot;
            if (item.BuildIdentity != null) yield return item.BuildIdentity;
            if (item.BaseWorkflowReport != null) yield return item.BaseWorkflowReport;
            if (item.PlayerExecutable != null) yield return item.PlayerExecutable;
            if (item.WorkingDirectory != null) yield return item.WorkingDirectory;
            foreach (string path in item.ImmutableFiles) yield return path;
            if (item.PlayerWorkflowReport != null) yield return item.PlayerWorkflowReport;
        }
    }

    private static IEnumerable<string> EnumerateResourceReleaseQualificationListValues(
        ResourceReleaseQualificationConfig config)
    {
        yield return config.OutputRoot;
        foreach (string root in config.EvidenceToolchainRoots) yield return root;
        foreach (ResourceReleaseQualificationBase item in config.Bases)
        {
            foreach (string path in item.ImmutableFiles) yield return path;
            if (item.PlayerWorkflowReport != null) yield return item.PlayerWorkflowReport;
        }
    }

    private static void ValidateResourceReleaseQualificationOutput(
        ResourceReleaseQualificationConfig config, string outputRoot,
        string toolchainRoot, string schemasRoot, string stateRoot)
    {
        foreach (string input in EnumerateResourceReleaseQualificationInputs(config))
        {
            EnsureOutputNotAncestor(outputRoot, input);
            if (Directory.Exists(input)) EnsureOutputOutsideRoot(outputRoot, input);
        }
        EnsureOutputOutsideRoot(outputRoot, toolchainRoot);
        EnsureOutputOutsideRoot(outputRoot, schemasRoot);
        EnsureOutputOutsideRoot(outputRoot, stateRoot);
    }

    private static string QualificationOwnedPath(string path, string outputRoot)
    {
        string relative = Path.GetRelativePath(outputRoot, path).Replace('\\', '/');
        if (!IsPortableRelativePath(relative))
            throw new DheException("Qualification-owned path escaped its output root: " +
                path);
        return relative;
    }

    private static ResourceReleaseQualificationRegressionResult
        RunResourceReleaseQualificationRegression(string regressionRoot,
            string resourceUpdateRoot, string channelSnapshot,
            string channelSnapshotSha256,
            IReadOnlyCollection<(JsonElement Report, string Path)> reports,
            string authorityRoot, string authorityPackageId, string schemaRoot,
            IReadOnlyCollection<string> evidenceToolchainRoots)
    {
        string root = Path.Combine(regressionRoot,
            "resource-release-qualification-regression");
        Directory.CreateDirectory(root);
        string schemasRoot = Directory.Exists(Path.Combine(schemaRoot, "schemas"))
            ? Path.Combine(schemaRoot, "schemas") : schemaRoot;

        object Prequalified((JsonElement Report, string Path) item) => new
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
            playerWorkflowReport = item.Path,
        };

        string WriteConfig(string name, string outputRoot, object[] bases,
            string expectedSnapshotSha256)
        {
            string path = Path.Combine(root, name + ".json");
            WriteJson(path, new
            {
                schemaVersion = 1,
                format = "hybridclr.dhe-resource-release-qualification-config.json",
                pathSemantics = "config-relative-v1",
                resourceUpdateRoot,
                outputRoot,
                channelSnapshot,
                expectedChannelSnapshotSha256 = expectedSnapshotSha256,
                expectedToolchainPackageId = authorityPackageId,
                evidenceToolchainRoots = evidenceToolchainRoots.ToArray(),
                requireEngineMatrix = true,
                timeoutSeconds = 30,
                bases,
            });
            return path;
        }

        int Run(string configPath) => ResourceReleaseQualify(new Cli(
            "resource-release-qualify", new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["root"] = authorityRoot,
                ["schemasroot"] = schemasRoot,
                ["config"] = configPath,
            }));

        bool prequalifiedPassed = false;
        bool exactCoverageRejected = false;
        bool processContractRejected = false;
        bool staleSnapshotRejected = false;
        string details = ResourceReleaseQualificationRegressionResult.Failed.Details;
        try
        {
            object[] allBases = reports.Select(Prequalified).ToArray();
            string successOutput = Path.Combine(root, "prequalified-output");
            string successConfig = WriteConfig("prequalified-config", successOutput,
                allBases, channelSnapshotSha256);
            string? headPath = ReadChannelSnapshot(channelSnapshot,
                channelSnapshotSha256).ChannelHeadPath;
            string? headBefore = headPath == null ? null : Sha256File(headPath);
            if (Run(successConfig) != 0)
                throw new DheException("Prequalified qualification regression failed.");
            JsonElement summary = ReadJson<JsonElement>(Path.Combine(successOutput,
                "dhe-resource-release-qualification.json"));
            string? headAfter = headPath == null ? null : Sha256File(headPath);
            prequalifiedPassed = GetBool(summary, "passed") &&
                GetBool(summary, "exactActiveBaseCoverage") &&
                GetInt(summary, "activeBaseCount") == reports.Count &&
                GetInt(summary, "processRunnerCount") == 0 &&
                GetInt(summary, "prequalifiedRunnerCount") == reports.Count &&
                string.Equals(headBefore, headAfter, StringComparison.OrdinalIgnoreCase);

            string missingOutput = Path.Combine(root, "missing-output");
            string missingConfig = WriteConfig("missing-config", missingOutput,
                allBases.Skip(1).ToArray(), channelSnapshotSha256);
            try { _ = Run(missingConfig); }
            catch (DheException)
            {
                exactCoverageRejected = !Directory.Exists(missingOutput);
            }

            (JsonElement Report, string Path) first = reports.First();
            string reportDirectory = Path.GetDirectoryName(first.Path)!;
            string buildIdentity = ResolveEvidencePath(GetString(first.Report,
                "buildIdentity"), reportDirectory, "Qualification regression build identity");
            string baseWorkflow = ResolveEvidencePath(GetString(first.Report,
                "baseWorkflowReport"), reportDirectory,
                "Qualification regression Base workflow");
            string stagePath = ResolveEvidencePath(GetString(first.Report,
                "resourceStage"), reportDirectory, "Qualification regression stage");
            string assetRoot = GetString(ReadJson<JsonElement>(stagePath), "assetRoot") ??
                throw new DheException("Qualification regression stage has no asset root.");
            object invalidProcess = new
            {
                baseId = GetString(first.Report, "selectedBaseId"),
                runnerKind = QualificationProcessRunner,
                assetRoot,
                buildIdentity,
                baseWorkflowReport = baseWorkflow,
                playerExecutable = buildIdentity,
                workingDirectory = Path.GetDirectoryName(buildIdentity),
                playerArguments = new[] { QualificationResultToken },
                immutableFiles = new[] { buildIdentity },
                playerWorkflowReport = (string?)null,
            };
            object[] invalidProcessBases = allBases.ToArray();
            invalidProcessBases[0] = invalidProcess;
            string invalidProcessConfig = WriteConfig("invalid-process-config",
                Path.Combine(root, "invalid-process-output"), invalidProcessBases,
                channelSnapshotSha256);
            try { _ = ReadResourceReleaseQualificationConfig(invalidProcessConfig,
                schemasRoot); }
            catch (DheException) { processContractRejected = true; }

            string staleOutput = Path.Combine(root, "stale-output");
            string staleConfig = WriteConfig("stale-config", staleOutput, allBases,
                new string('f', 64));
            try { _ = Run(staleConfig); }
            catch (DheException)
            {
                staleSnapshotRejected = !Directory.Exists(staleOutput);
            }
            details = "one config aggregated every active Base report without changing " +
                "the channel and rejected incomplete, invalid-process, and stale inputs";
        }
        catch (Exception exception)
        {
            details = exception.Message;
        }
        return new ResourceReleaseQualificationRegressionResult(prequalifiedPassed,
            exactCoverageRejected, processContractRejected, staleSnapshotRejected, details);
    }
}
