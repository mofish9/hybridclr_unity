using System.Text;
using System.Text.Json;

namespace HybridCLR.DheTool;

internal static partial class Program
{
    private const string ChannelStateStorageContract = "filesystem-cas-v1";
    private const string ChannelHeadFileName = "head.json";

    private sealed record ChannelHeadDocument(
        string SourcePath,
        string Sha256,
        string Operation,
        string ChannelId,
        int ReleaseRevision,
        string? PreviousChannelHeadSha256,
        string ReleaseLedgerSha256,
        string? ParentReleaseLedgerSha256,
        string ResourceUpdateManifestSha256,
        string ResourceUpdateValidationSha256,
        string BaseRegistryId,
        int BaseRegistryRevision,
        string BaseRegistrySha256,
        int ActiveBaseCount,
        int RetiredBaseCount,
        string CurrentAssemblySetSha256,
        string PayloadVariantSetSha256,
        string ArtifactRelativeRoot,
        string ArtifactTreeSha256,
        string ApprovalKind,
        string? ReleaseGateRelativePath,
        string? ReleaseGateSha256,
        string ToolchainPackageId,
        bool ExactActiveBaseCoverage,
        int PlayerReportCount,
        bool EngineMatrixCovered,
        string[] EngineWorkflows);

    private sealed record ChannelSnapshotDocument(
        string SourcePath,
        string Sha256,
        string StateRoot,
        string ChannelId,
        bool Initialized,
        string? ChannelHeadPath,
        string? ChannelHeadSha256,
        int? CurrentRevision,
        int NextRevision,
        bool InitializationRequired,
        string? PreviousReleaseLedger,
        string? PreviousReleaseLedgerSha256,
        string? ArtifactRoot,
        string? ArtifactTreeSha256,
        string? BaseRegistryId,
        int? BaseRegistryRevision,
        string? BaseRegistrySha256,
        int? ActiveBaseCount,
        int? RetiredBaseCount,
        string? CurrentAssemblySetSha256,
        string? PayloadVariantSetSha256);

    private static int ChannelState(Cli cli)
    {
        string operation = (cli.Optional("operation") ?? "snapshot").Trim().ToLowerInvariant();
        if (operation is not ("snapshot" or "adopt-existing" or "promote"))
            throw new DheException(
                "Channel state Operation must be snapshot, adopt-existing, or promote.");

        string stateRoot = Path.GetFullPath(cli.Require("stateroot"));
        ProtectUnityToolOutput(stateRoot);
        string channelId = cli.Require("channelid").Trim();
        if (!IsRegistryId(channelId))
            throw new DheException("ChannelId contains unsupported characters.");
        string toolchainRoot = RequireDirectory(cli.Optional("toolchainroot") ?? cli.Root,
            "DHE channel state Release toolchain");
        string expectedPackageId = cli.Require("expectedtoolchainpackageid");
        PackageInspection authority = InspectPackage(toolchainRoot, expectedPackageId, true);
        if (!authority.Passed)
            throw new DheException("Channel state requires the exact authenticated Release " +
                "toolchain: " + string.Join("; ", authority.Errors));
        string schemaRoot = RequireDirectory(cli.Optional("schemaroot") ?? toolchainRoot,
            "DHE channel state schema root");

        EnsureOutputOutsideRoot(stateRoot, toolchainRoot);
        if (!Path.GetFullPath(schemaRoot).Equals(Path.GetFullPath(toolchainRoot),
                StringComparison.OrdinalIgnoreCase))
            EnsureOutputOutsideRoot(stateRoot, schemaRoot);

        string? updateInputRoot = null;
        string? gateInputPath = null;
        if (operation == "adopt-existing")
        {
            updateInputRoot = RequireDirectory(cli.Require("resourceupdateroot"),
                "Published resource head to adopt");
        }
        else if (operation == "promote")
        {
            gateInputPath = RequireFile(cli.Require("resourcereleasegate"),
                "Resource release aggregate gate");
            RejectReparsePoint(gateInputPath, "Resource release aggregate gate");
            JsonElement gateInput = ReadJson<JsonElement>(gateInputPath);
            updateInputRoot = RequireDirectory(GetString(gateInput,
                    "resourceUpdateRoot") ?? string.Empty,
                "Qualified resource update");
            string gateRoot = Path.GetDirectoryName(gateInputPath)!;
            EnsureOutputOutsideRoot(stateRoot, gateRoot);
        }
        if (updateInputRoot != null)
        {
            EnsureOutputOutsideRoot(stateRoot, updateInputRoot);
            RejectReparseTree(updateInputRoot, "Channel resource input");
        }

        string output = SafeReportPath(cli.Require("output"), gateInputPath == null
            ? Array.Empty<string>()
            : new[] { gateInputPath });
        EnsureOutputOutsideRoot(output, stateRoot);
        EnsureOutputOutsideRoot(output, toolchainRoot);
        if (!Path.GetFullPath(schemaRoot).Equals(Path.GetFullPath(toolchainRoot),
                StringComparison.OrdinalIgnoreCase))
            EnsureOutputOutsideRoot(output, schemaRoot);
        if (updateInputRoot != null)
            EnsureOutputOutsideRoot(output, updateInputRoot);
        if (gateInputPath != null)
            EnsureOutputOutsideRoot(output, Path.GetDirectoryName(gateInputPath)!);

        Directory.CreateDirectory(stateRoot);
        RejectReparsePoint(stateRoot, "Channel StateRoot");
        if (File.Exists(Path.Combine(stateRoot, ".git")) ||
            Directory.Exists(Path.Combine(stateRoot, ".git")))
            throw new DheException("Channel StateRoot cannot be a Git repository root.");
        if (File.Exists(output)) File.Delete(output);

        int lockTimeoutSeconds = ReadChannelLockTimeout(cli);
        return operation switch
        {
            "snapshot" => WriteCurrentChannelSnapshot(stateRoot, channelId, output,
                schemaRoot, lockTimeoutSeconds),
            "adopt-existing" => AdoptExistingChannelHead(cli, stateRoot, channelId,
                output, schemaRoot, authority.PackageId!, lockTimeoutSeconds),
            "promote" => PromoteChannelHead(cli, stateRoot, channelId, output,
                schemaRoot, authority.PackageId!, lockTimeoutSeconds),
            _ => throw new DheException("Unsupported channel state operation."),
        };
    }

    private static int AdoptExistingChannelHead(Cli cli, string stateRoot,
        string channelId, string output, string schemaRoot, string toolchainPackageId,
        int lockTimeoutSeconds)
    {
        if (!cli.Has("acknowledgeexistingpublishedhead"))
            throw new DheException("Adopting an existing channel requires explicit " +
                "AcknowledgeExistingPublishedHead authorization.");
        string updateRoot = RequireDirectory(cli.Require("resourceupdateroot"),
            "Published resource head to adopt");
        EnsureOutputOutsideRoot(stateRoot, updateRoot);
        RejectReparseTree(updateRoot, "Published resource head");
        JsonElement manifest = ReadJson<JsonElement>(RequireFile(Path.Combine(updateRoot,
            "dhe-resource-update.json"), "Published resource manifest"));
        ReleaseLedgerDocument? ledger = ValidateReleaseLedgerForStaging(updateRoot, manifest);
        _ = ValidateResourceUpdateCompatibility(updateRoot, manifest);
        string expectedLedger = cli.Require("expectedreleaseledgersha256");
        if (ledger == null || !IsHex(expectedLedger, 64, 64) ||
            !string.Equals(expectedLedger, ledger.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(channelId, ledger.ChannelId, StringComparison.Ordinal))
            throw new DheException("The adopted resource head does not match the expected " +
                "channel and ledger SHA-256.");

        (string artifactRelativeRoot, string artifactTreeSha256) =
            PublishImmutableResourceArtifact(stateRoot, updateRoot, ledger);
        string[] workflows = ReadManifestEngineWorkflows(manifest);
        ChannelHeadDocument head;
        using (AcquireChannelLock(stateRoot, channelId, lockTimeoutSeconds))
        {
            if (ReadChannelHead(stateRoot, channelId) != null)
                throw new DheException("Cannot adopt over an initialized channel head.");
            head = PersistChannelHead(stateRoot, new
            {
                schemaVersion = 1,
                format = "hybridclr.dhe-channel-state.json",
                storageContract = ChannelStateStorageContract,
                generatedAtUtc = DateTimeOffset.UtcNow,
                operation = "adopted-existing-head",
                channelId,
                releaseRevision = ledger.Revision,
                previousChannelHeadSha256 = (string?)null,
                releaseLedgerSha256 = ledger.Sha256,
                parentReleaseLedgerSha256 = ledger.ParentLedgerSha256,
                resourceUpdateManifestSha256 = ledger.ResourceUpdateManifestSha256,
                resourceUpdateValidationSha256 = ledger.ResourceUpdateValidationSha256,
                baseRegistryId = ledger.BaseRegistryId,
                baseRegistryRevision = ledger.BaseRegistryRevision,
                baseRegistrySha256 = ledger.BaseRegistrySha256,
                activeBaseCount = ledger.ActiveBaseCount,
                retiredBaseCount = ledger.RetiredBaseCount,
                currentAssemblySetSha256 = ledger.CurrentAssemblySetSha256,
                payloadVariantSetSha256 = ledger.PayloadVariantSetSha256,
                artifactRelativeRoot,
                artifactTreeSha256,
                approvalKind = "adopted-existing-head",
                releaseGateRelativePath = (string?)null,
                releaseGateSha256 = (string?)null,
                toolchainPackageId,
                exactActiveBaseCoverage = false,
                playerReportCount = 0,
                engineMatrixCovered = false,
                engineWorkflows = workflows,
            }, channelId, schemaRoot);
            WriteChannelSnapshot(output, stateRoot, channelId, head, schemaRoot);
        }
        Console.WriteLine("DHE channel adopted at revision " + head.ReleaseRevision +
            ": " + output);
        return 0;
    }

    private static int PromoteChannelHead(Cli cli, string stateRoot, string channelId,
        string output, string schemaRoot, string toolchainPackageId,
        int lockTimeoutSeconds)
    {
        string gatePath = RequireFile(cli.Require("resourcereleasegate"),
            "Resource release aggregate gate");
        string gateSha256 = Sha256File(gatePath);
        JsonElement gate = RevalidateStateBoundResourceGate(gatePath, schemaRoot);
        string gateStateRoot = Path.GetFullPath(GetString(gate, "channelStateRoot") ??
            string.Empty);
        if (!gateStateRoot.Equals(Path.GetFullPath(stateRoot),
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Resource release gate is bound to another Channel StateRoot.");
        if (!string.Equals(GetString(gate, "toolchainPackageId"), toolchainPackageId,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Resource release gate and channel state toolchain " +
                "package identities differ.");
        if (!string.Equals(GetString(gate, "releaseChannelId"), channelId,
                StringComparison.Ordinal))
            throw new DheException("Resource release gate targets another channel.");

        string? expectedHead = NormalizeOptionalHash(
            cli.Optional("expectedchannelheadsha256"));
        string? gateExpectedHead = GetString(gate, "expectedChannelHeadSha256");
        bool initialize = cli.Has("initializechannel");
        if (GetBool(gate, "initializationAuthorized"))
        {
            if (!initialize || expectedHead != null || gateExpectedHead != null)
                throw new DheException("A genesis promotion requires InitializeChannel and " +
                    "must not declare a previous channel head.");
        }
        else if (initialize || !IsHex(expectedHead, 64, 64) ||
                 !string.Equals(expectedHead, gateExpectedHead,
                     StringComparison.OrdinalIgnoreCase))
        {
            throw new DheException("A continuation promotion requires the exact channel head " +
                "SHA-256 bound by its release gate.");
        }

        string updateRoot = RequireDirectory(GetString(gate, "resourceUpdateRoot") ??
            string.Empty, "Qualified resource update");
        EnsureOutputOutsideRoot(stateRoot, updateRoot);
        RejectReparseTree(updateRoot, "Qualified resource update");
        JsonElement manifest = ReadJson<JsonElement>(RequireFile(Path.Combine(updateRoot,
            "dhe-resource-update.json"), "Qualified resource manifest"));
        ReleaseLedgerDocument? ledger = ValidateReleaseLedgerForStaging(updateRoot, manifest);
        _ = ValidateResourceUpdateCompatibility(updateRoot, manifest);
        if (ledger == null || !string.Equals(ledger.Sha256,
                GetString(gate, "releaseLedgerSha256"), StringComparison.OrdinalIgnoreCase))
            throw new DheException("Qualified resource update ledger changed after approval.");

        (string artifactRelativeRoot, string artifactTreeSha256) =
            PublishImmutableResourceArtifact(stateRoot, updateRoot, ledger);
        string releaseGateRelativePath = PublishImmutableApproval(stateRoot, gatePath,
            gateSha256);
        string[] workflows = gate.GetProperty("engineWorkflows").EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .Where(value => value.Length > 0).OrderBy(value => value,
                StringComparer.Ordinal).ToArray();
        int playerReportCount = gate.GetProperty("playerReports").GetArrayLength();

        ChannelHeadDocument head;
        using (AcquireChannelLock(stateRoot, channelId, lockTimeoutSeconds))
        {
            ChannelHeadDocument? previous = ReadChannelHead(stateRoot, channelId);
            if (initialize)
            {
                if (previous != null || ledger.Revision != 1 ||
                    ledger.ParentLedgerSha256 != null)
                    throw new DheException("Channel genesis lost its compare-and-swap race.");
            }
            else
            {
                if (previous == null ||
                    !string.Equals(previous.Sha256, expectedHead,
                        StringComparison.OrdinalIgnoreCase) ||
                    ledger.Revision != previous.ReleaseRevision + 1 ||
                    !string.Equals(ledger.ParentLedgerSha256,
                        previous.ReleaseLedgerSha256, StringComparison.OrdinalIgnoreCase))
                    throw new DheException("Channel head changed before promotion; the stale " +
                        "candidate cannot be published.");
            }

            head = PersistChannelHead(stateRoot, new
            {
                schemaVersion = 1,
                format = "hybridclr.dhe-channel-state.json",
                storageContract = ChannelStateStorageContract,
                generatedAtUtc = DateTimeOffset.UtcNow,
                operation = "promoted",
                channelId,
                releaseRevision = ledger.Revision,
                previousChannelHeadSha256 = previous?.Sha256,
                releaseLedgerSha256 = ledger.Sha256,
                parentReleaseLedgerSha256 = ledger.ParentLedgerSha256,
                resourceUpdateManifestSha256 = ledger.ResourceUpdateManifestSha256,
                resourceUpdateValidationSha256 = ledger.ResourceUpdateValidationSha256,
                baseRegistryId = ledger.BaseRegistryId,
                baseRegistryRevision = ledger.BaseRegistryRevision,
                baseRegistrySha256 = ledger.BaseRegistrySha256,
                activeBaseCount = ledger.ActiveBaseCount,
                retiredBaseCount = ledger.RetiredBaseCount,
                currentAssemblySetSha256 = ledger.CurrentAssemblySetSha256,
                payloadVariantSetSha256 = ledger.PayloadVariantSetSha256,
                artifactRelativeRoot,
                artifactTreeSha256,
                approvalKind = "resource-release-gate",
                releaseGateRelativePath,
                releaseGateSha256 = gateSha256,
                toolchainPackageId,
                exactActiveBaseCoverage = true,
                playerReportCount,
                engineMatrixCovered = GetBool(gate, "engineMatrixCovered"),
                engineWorkflows = workflows,
            }, channelId, schemaRoot);
            WriteChannelSnapshot(output, stateRoot, channelId, head, schemaRoot);
        }
        Console.WriteLine("DHE channel promoted to revision " + head.ReleaseRevision +
            ": " + output);
        return 0;
    }

    private static JsonElement RevalidateStateBoundResourceGate(string gatePath,
        string schemaRoot)
    {
        JsonElement gate = ReadJson<JsonElement>(gatePath);
        RequireEvidenceFormat(gate, "hybridclr.dhe-resource-release-gate.json",
            "Resource release aggregate gate");
        ValidateDocumentAgainstSchema(gate, schemaRoot,
            "dhe-resource-release-gate.schema.json", "Resource release aggregate gate");
        if (!GetBool(gate, "passed") || !GetBool(gate, "releaseReady") ||
            !GetBool(gate, "channelStateBound") ||
            !GetBool(gate, "exactActiveBaseCoverage"))
            throw new DheException("Channel promotion requires a passing state-bound resource gate.");

        string temporary = Path.Combine(Path.GetTempPath(), "dhe-channel-gate-" +
            Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["toolchainroot"] = GetString(gate, "toolchainRoot") ?? string.Empty,
                ["expectedtoolchainpackageid"] = GetString(gate,
                    "toolchainPackageId") ?? string.Empty,
                ["schemaroot"] = schemaRoot,
                ["resourceupdateroot"] = GetString(gate, "resourceUpdateRoot") ??
                    string.Empty,
                ["channelsnapshot"] = GetString(gate, "channelSnapshot") ?? string.Empty,
                ["expectedchannelsnapshotsha256"] = GetString(gate,
                    "channelSnapshotSha256") ?? string.Empty,
                ["expectedreleaseledgersha256"] = GetString(gate,
                    "expectedReleaseLedgerSha256") ?? string.Empty,
                ["changedplayers"] = string.Join(',', gate.GetProperty("playerReports")
                    .EnumerateArray().Select(item => GetString(item, "report"))),
                ["output"] = temporary,
            };
            string? validationSource = GetString(gate, "validationSourceRoot");
            if (!string.IsNullOrWhiteSpace(validationSource))
                values["validationsourceroot"] = validationSource;
            if (GetBool(gate, "initializationAuthorized"))
                values["initializereleaseledger"] = "true";
            if (GetBool(gate, "requireEngineMatrix"))
                values["requireenginematrix"] = "true";
            string[] evidenceToolchainRoots = gate.GetProperty("evidenceToolchainPackages")
                .EnumerateArray()
                .Where(item => !string.Equals(GetString(item, "packageId"),
                    GetString(gate, "toolchainPackageId"),
                    StringComparison.OrdinalIgnoreCase))
                .Select(item => GetString(item, "packageRoot") ?? string.Empty)
                .Where(value => value.Length > 0).ToArray();
            if (evidenceToolchainRoots.Length != 0)
                values["evidencetoolchainroots"] = string.Join(',',
                    evidenceToolchainRoots);
            if (ResourceReleaseGate(new Cli("resource-release-gate", values)) != 0)
                throw new DheException("Resource release aggregate gate revalidation failed.");

            JsonElement current = ReadJson<JsonElement>(temporary);
            foreach (JsonProperty property in gate.EnumerateObject())
            {
                if (property.NameEquals("generatedAtUtc")) continue;
                if (!JsonPropertiesEqual(gate, current, property.Name))
                    throw new DheException("Resource release gate changed during revalidation: " +
                        property.Name + ".");
            }
            return gate;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool JsonPropertiesEqual(JsonElement left, JsonElement right,
        string property)
    {
        bool leftPresent = left.TryGetProperty(property, out JsonElement leftValue);
        bool rightPresent = right.TryGetProperty(property, out JsonElement rightValue);
        return leftPresent == rightPresent && (!leftPresent ||
            string.Equals(leftValue.GetRawText(), rightValue.GetRawText(),
                StringComparison.Ordinal));
    }

    private static int WriteCurrentChannelSnapshot(string stateRoot, string channelId,
        string output, string schemaRoot, int lockTimeoutSeconds)
    {
        using (AcquireChannelLock(stateRoot, channelId, lockTimeoutSeconds))
        {
            WriteChannelSnapshot(output, stateRoot, channelId,
                ReadChannelHead(stateRoot, channelId), schemaRoot);
        }
        Console.WriteLine("DHE channel snapshot: " + output);
        return 0;
    }

    private static void WriteChannelSnapshot(string output, string stateRoot,
        string channelId, ChannelHeadDocument? head, string schemaRoot)
    {
        string? artifactRoot = head == null ? null : ResolveContainedPath(stateRoot,
            head.ArtifactRelativeRoot, "Channel artifact");
        string? ledgerPath = artifactRoot == null ? null : Path.Combine(artifactRoot,
            ReleaseLedgerFileName);
        var value = new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-channel-snapshot.json",
            generatedAtUtc = DateTimeOffset.UtcNow,
            passed = true,
            storageContract = ChannelStateStorageContract,
            stateRoot,
            channelId,
            initialized = head != null,
            channelHead = head?.SourcePath,
            channelHeadSha256 = head?.Sha256,
            currentRevision = head?.ReleaseRevision,
            nextRevision = checked((head?.ReleaseRevision ?? 0) + 1),
            initializationRequired = head == null,
            previousReleaseLedger = ledgerPath,
            previousReleaseLedgerSha256 = head?.ReleaseLedgerSha256,
            artifactRoot,
            artifactTreeSha256 = head?.ArtifactTreeSha256,
            baseRegistryId = head?.BaseRegistryId,
            baseRegistryRevision = head?.BaseRegistryRevision,
            baseRegistrySha256 = head?.BaseRegistrySha256,
            activeBaseCount = head?.ActiveBaseCount,
            retiredBaseCount = head?.RetiredBaseCount,
            currentAssemblySetSha256 = head?.CurrentAssemblySetSha256,
            payloadVariantSetSha256 = head?.PayloadVariantSetSha256,
            errors = Array.Empty<string>(),
            warnings = Array.Empty<string>(),
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        JsonElement snapshot = JsonSerializer.Deserialize<JsonElement>(bytes);
        ValidateDocumentAgainstSchema(snapshot, schemaRoot,
            "dhe-channel-snapshot.schema.json", "Channel snapshot");
        string temporary = output + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteDurableFile(temporary, bytes);
            File.Move(temporary, output, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static ChannelSnapshotDocument ReadChannelSnapshot(string path,
        string? expectedSha256)
    {
        string sourcePath = RequireFile(path, "DHE channel snapshot");
        RejectReparsePoint(sourcePath, "DHE channel snapshot");
        string sha256 = Sha256File(sourcePath);
        if (!IsHex(expectedSha256, 64, 64) ||
            !string.Equals(sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new DheException("Channel snapshot does not match its expected SHA-256.");
        JsonElement snapshot = ReadJson<JsonElement>(sourcePath);
        string stateRoot = RequireDirectory(GetString(snapshot, "stateRoot") ?? string.Empty,
            "Channel snapshot StateRoot");
        RejectReparsePoint(stateRoot, "Channel snapshot StateRoot");
        string channelId = GetString(snapshot, "channelId") ?? string.Empty;
        bool initialized = GetBool(snapshot, "initialized");
        string? channelHeadPath = GetString(snapshot, "channelHead");
        string? channelHeadSha256 = GetString(snapshot, "channelHeadSha256");
        int? currentRevision = NullableInt(snapshot, "currentRevision");
        int nextRevision = GetInt(snapshot, "nextRevision");
        bool initializationRequired = GetBool(snapshot, "initializationRequired");
        string? previousLedger = GetString(snapshot, "previousReleaseLedger");
        string? previousLedgerSha256 = GetString(snapshot, "previousReleaseLedgerSha256");
        string? artifactRoot = GetString(snapshot, "artifactRoot");
        string? artifactTreeSha256 = GetString(snapshot, "artifactTreeSha256");
        string? baseRegistryId = GetString(snapshot, "baseRegistryId");
        int? baseRegistryRevision = NullableInt(snapshot, "baseRegistryRevision");
        string? baseRegistrySha256 = GetString(snapshot, "baseRegistrySha256");
        int? activeBaseCount = NullableInt(snapshot, "activeBaseCount");
        int? retiredBaseCount = NullableInt(snapshot, "retiredBaseCount");
        string? currentAssemblySetSha256 = GetString(snapshot,
            "currentAssemblySetSha256");
        string? payloadVariantSetSha256 = GetString(snapshot,
            "payloadVariantSetSha256");
        if (GetInt(snapshot, "schemaVersion") != 1 ||
            GetString(snapshot, "format") != "hybridclr.dhe-channel-snapshot.json" ||
            GetString(snapshot, "storageContract") != ChannelStateStorageContract ||
            !GetBool(snapshot, "passed") || !IsRegistryId(channelId) ||
            nextRevision < 1 || initializationRequired == initialized)
            throw new DheException("DHE channel snapshot contract is invalid.");

        using (AcquireChannelLock(stateRoot, channelId, 30))
        {
            ChannelHeadDocument? live = ReadChannelHead(stateRoot, channelId);
            if (!initialized)
            {
                if (live != null || currentRevision != null || channelHeadPath != null ||
                    channelHeadSha256 != null || previousLedger != null ||
                    previousLedgerSha256 != null || artifactRoot != null ||
                    artifactTreeSha256 != null || baseRegistryId != null ||
                    baseRegistryRevision != null || baseRegistrySha256 != null ||
                    activeBaseCount != null || retiredBaseCount != null ||
                    currentAssemblySetSha256 != null || payloadVariantSetSha256 != null ||
                    nextRevision != 1)
                    throw new DheException("Uninitialized channel snapshot is stale or malformed.");
            }
            else
            {
                string liveArtifactRoot = ResolveContainedPath(stateRoot,
                    live!.ArtifactRelativeRoot, "Snapshot artifact");
                string liveLedger = Path.Combine(liveArtifactRoot, ReleaseLedgerFileName);
                if (currentRevision != live.ReleaseRevision ||
                    nextRevision != live.ReleaseRevision + 1 ||
                    !Path.GetFullPath(channelHeadPath ?? string.Empty).Equals(
                        Path.GetFullPath(live.SourcePath), StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(channelHeadSha256, live.Sha256,
                        StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFullPath(previousLedger ?? string.Empty).Equals(
                        Path.GetFullPath(liveLedger), StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(previousLedgerSha256, live.ReleaseLedgerSha256,
                        StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFullPath(artifactRoot ?? string.Empty).Equals(
                        Path.GetFullPath(liveArtifactRoot), StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(artifactTreeSha256, live.ArtifactTreeSha256,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(baseRegistryId, live.BaseRegistryId,
                        StringComparison.Ordinal) || baseRegistryRevision != live.BaseRegistryRevision ||
                    !string.Equals(baseRegistrySha256, live.BaseRegistrySha256,
                        StringComparison.OrdinalIgnoreCase) || activeBaseCount != live.ActiveBaseCount ||
                    retiredBaseCount != live.RetiredBaseCount ||
                    !string.Equals(currentAssemblySetSha256,
                        live.CurrentAssemblySetSha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(payloadVariantSetSha256,
                        live.PayloadVariantSetSha256, StringComparison.OrdinalIgnoreCase))
                    throw new DheException("Initialized channel snapshot no longer matches its live head.");
            }
        }

        return new ChannelSnapshotDocument(sourcePath, sha256, stateRoot, channelId,
            initialized, channelHeadPath, channelHeadSha256, currentRevision, nextRevision,
            initializationRequired, previousLedger, previousLedgerSha256,
            artifactRoot, artifactTreeSha256, baseRegistryId, baseRegistryRevision,
            baseRegistrySha256, activeBaseCount, retiredBaseCount,
            currentAssemblySetSha256, payloadVariantSetSha256);
    }

    private static ChannelHeadDocument PersistChannelHead(string stateRoot, object value,
        string channelId, string schemaRoot)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        JsonElement document = JsonSerializer.Deserialize<JsonElement>(bytes);
        ValidateDocumentAgainstSchema(document, schemaRoot, "dhe-channel-state.schema.json",
            "Channel state head");
        string sha256 = Sha256Bytes(bytes);
        string channelRoot = ChannelRoot(stateRoot, channelId);
        string statesRoot = Path.Combine(channelRoot, "states");
        Directory.CreateDirectory(statesRoot);
        RejectReparsePoint(statesRoot, "Channel state history root");
        string statePath = ResolveContainedPath(channelRoot, "states/" + sha256 + ".json",
            "Channel state history");
        WriteContentAddressedFile(statePath, bytes);

        string headPath = Path.Combine(channelRoot, ChannelHeadFileName);
        RejectExistingReparsePath(headPath, "Channel head");
        string temporary = headPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteDurableFile(temporary, bytes);
            File.Move(temporary, headPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        if (!string.Equals(Sha256File(headPath), sha256, StringComparison.OrdinalIgnoreCase))
            throw new DheException("Atomic channel head replacement did not preserve its bytes.");
        return ReadChannelHead(stateRoot, channelId) ??
            throw new DheException("Promoted channel head could not be reloaded.");
    }

    private static ChannelHeadDocument? ReadChannelHead(string stateRoot, string channelId)
    {
        string channelRoot = ChannelRoot(stateRoot, channelId);
        string headPath = Path.Combine(channelRoot, ChannelHeadFileName);
        if (!File.Exists(headPath)) return null;
        RejectReparsePoint(headPath, "Channel head");
        byte[] headBytes = File.ReadAllBytes(headPath);
        string sha256 = Sha256Bytes(headBytes);
        string historyPath = ResolveContainedPath(channelRoot, "states/" + sha256 + ".json",
            "Channel state history");
        RejectExistingReparsePath(historyPath, "Channel state history");
        if (!File.Exists(historyPath) || !File.ReadAllBytes(historyPath).SequenceEqual(headBytes))
            throw new DheException("Channel head is not backed by immutable state history.");

        JsonElement state = JsonSerializer.Deserialize<JsonElement>(headBytes);
        string operation = GetString(state, "operation") ?? string.Empty;
        string stateChannelId = GetString(state, "channelId") ?? string.Empty;
        int revision = GetInt(state, "releaseRevision");
        string? previousHead = GetString(state, "previousChannelHeadSha256");
        string ledgerSha256 = GetString(state, "releaseLedgerSha256") ?? string.Empty;
        string? parentLedger = GetString(state, "parentReleaseLedgerSha256");
        string manifestSha256 = GetString(state, "resourceUpdateManifestSha256") ?? string.Empty;
        string validationSha256 = GetString(state,
            "resourceUpdateValidationSha256") ?? string.Empty;
        string registryId = GetString(state, "baseRegistryId") ?? string.Empty;
        int registryRevision = GetInt(state, "baseRegistryRevision");
        string registrySha256 = GetString(state, "baseRegistrySha256") ?? string.Empty;
        int activeBaseCount = GetInt(state, "activeBaseCount");
        int retiredBaseCount = GetInt(state, "retiredBaseCount");
        string currentSet = GetString(state, "currentAssemblySetSha256") ?? string.Empty;
        string variantSet = GetString(state, "payloadVariantSetSha256") ?? string.Empty;
        string artifactRelative = GetString(state, "artifactRelativeRoot") ?? string.Empty;
        string artifactTree = GetString(state, "artifactTreeSha256") ?? string.Empty;
        string approvalKind = GetString(state, "approvalKind") ?? string.Empty;
        string? gateRelative = GetString(state, "releaseGateRelativePath");
        string? gateSha256 = GetString(state, "releaseGateSha256");
        string toolchainPackageId = GetString(state, "toolchainPackageId") ?? string.Empty;
        bool exactCoverage = GetBool(state, "exactActiveBaseCoverage");
        int playerReportCount = GetInt(state, "playerReportCount");
        bool engineMatrix = GetBool(state, "engineMatrixCovered");
        string[] workflows = state.GetProperty("engineWorkflows").EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty).ToArray();

        bool adopted = operation == "adopted-existing-head";
        bool promoted = operation == "promoted";
        if (GetInt(state, "schemaVersion") != 1 ||
            GetString(state, "format") != "hybridclr.dhe-channel-state.json" ||
            GetString(state, "storageContract") != ChannelStateStorageContract ||
            (!adopted && !promoted) || !string.Equals(stateChannelId, channelId,
                StringComparison.Ordinal) || revision < 1 ||
            !IsHex(ledgerSha256, 64, 64) || !IsRegistryId(registryId) ||
            registryRevision < 1 || !IsHex(registrySha256, 64, 64) ||
            activeBaseCount < 1 || retiredBaseCount < 0 ||
            !IsHex(currentSet, 64, 64) || !IsHex(variantSet, 64, 64) ||
            !IsHex(manifestSha256, 64, 64) || !IsHex(validationSha256, 64, 64) ||
            !IsHex(artifactTree, 64, 64) ||
            !IsHex(toolchainPackageId, 64, 64) ||
            !IsPortableRelativePath(artifactRelative) || workflows.Length < 1 ||
            workflows.Any(workflow => !KnownPlayerEngineWorkflows.Contains(workflow,
                StringComparer.Ordinal)) || workflows.Distinct(StringComparer.Ordinal).Count() !=
                workflows.Length)
            throw new DheException("DHE channel head contract is invalid.");
        if (adopted && (previousHead != null || gateRelative != null || gateSha256 != null ||
                approvalKind != "adopted-existing-head" || exactCoverage ||
                playerReportCount != 0 || engineMatrix))
            throw new DheException("Adopted channel head contains promoted evidence fields.");
        if (promoted && (approvalKind != "resource-release-gate" ||
                !IsPortableRelativePath(gateRelative ?? string.Empty) ||
                !IsHex(gateSha256, 64, 64) || !exactCoverage || playerReportCount < 1))
            throw new DheException("Promoted channel head lacks aggregate release evidence.");
        if (promoted && revision == 1 && (previousHead != null || parentLedger != null) ||
            promoted && revision > 1 && (!IsHex(previousHead, 64, 64) ||
                !IsHex(parentLedger, 64, 64)))
            throw new DheException("Promoted channel head lineage is invalid.");

        string artifactRoot = RequireDirectory(ResolveContainedPath(stateRoot,
            artifactRelative, "Channel resource artifact"), "Channel resource artifact");
        RejectReparseTree(artifactRoot, "Channel resource artifact");
        if (!string.Equals(TreeHashForRelease(artifactRoot, Array.Empty<string>()),
                artifactTree, StringComparison.OrdinalIgnoreCase))
            throw new DheException("Channel resource artifact tree changed.");
        JsonElement manifest = ReadJson<JsonElement>(RequireFile(Path.Combine(artifactRoot,
            "dhe-resource-update.json"), "Channel artifact manifest"));
        ReleaseLedgerDocument? ledger = ValidateReleaseLedgerForStaging(artifactRoot, manifest);
        _ = ValidateResourceUpdateCompatibility(artifactRoot, manifest);
        if (ledger == null ||
            !string.Equals(ledger.Sha256, ledgerSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(ledger.ParentLedgerSha256, parentLedger,
                StringComparison.OrdinalIgnoreCase) || ledger.Revision != revision ||
            !string.Equals(ledger.ResourceUpdateManifestSha256, manifestSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(ledger.ResourceUpdateValidationSha256, validationSha256,
                StringComparison.OrdinalIgnoreCase) ||
            ledger.BaseRegistryId != registryId || ledger.BaseRegistryRevision != registryRevision ||
            !string.Equals(ledger.BaseRegistrySha256, registrySha256,
                StringComparison.OrdinalIgnoreCase) || ledger.ActiveBaseCount != activeBaseCount ||
            ledger.RetiredBaseCount != retiredBaseCount ||
            !string.Equals(ledger.CurrentAssemblySetSha256, currentSet,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(ledger.PayloadVariantSetSha256, variantSet,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Channel head does not match its immutable resource artifact.");

        if (promoted)
        {
            string gatePath = RequireFile(ResolveContainedPath(stateRoot, gateRelative!,
                "Channel release approval"), "Channel release approval");
            RejectReparsePoint(gatePath, "Channel release approval");
            JsonElement gate = ReadJson<JsonElement>(gatePath);
            if (!string.Equals(Sha256File(gatePath), gateSha256,
                    StringComparison.OrdinalIgnoreCase) || !GetBool(gate, "passed") ||
                !GetBool(gate, "channelStateBound") ||
                GetString(gate, "releaseChannelId") != channelId ||
                GetInt(gate, "releaseRevision") != revision ||
                !string.Equals(GetString(gate, "toolchainPackageId"),
                    toolchainPackageId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(GetString(gate, "releaseLedgerSha256"), ledgerSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                GetInt(gate, "activeBaseCount") != activeBaseCount ||
                gate.GetProperty("playerReports").GetArrayLength() != playerReportCount)
                throw new DheException("Channel release approval no longer matches its head.");
        }

        if (previousHead != null)
        {
            string previousPath = ResolveContainedPath(channelRoot,
                "states/" + previousHead.ToLowerInvariant() + ".json",
                "Previous channel state");
            if (!File.Exists(previousPath) ||
                !string.Equals(Sha256File(previousPath), previousHead,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("Previous channel state history is missing or changed.");
            JsonElement previousState = ReadJson<JsonElement>(previousPath);
            if (GetString(previousState, "channelId") != channelId ||
                GetInt(previousState, "releaseRevision") != revision - 1 ||
                !string.Equals(GetString(previousState, "releaseLedgerSha256"),
                    parentLedger, StringComparison.OrdinalIgnoreCase))
                throw new DheException("Previous channel state does not form a direct release chain.");
        }

        return new ChannelHeadDocument(headPath, sha256, operation, channelId, revision,
            previousHead, ledgerSha256, parentLedger, manifestSha256, validationSha256,
            registryId, registryRevision, registrySha256, activeBaseCount,
            retiredBaseCount, currentSet, variantSet, artifactRelative, artifactTree,
            approvalKind, gateRelative, gateSha256, toolchainPackageId, exactCoverage,
            playerReportCount, engineMatrix, workflows);
    }

    private static (string RelativeRoot, string TreeSha256) PublishImmutableResourceArtifact(
        string stateRoot, string sourceRoot, ReleaseLedgerDocument ledger)
    {
        string sourceTree = TreeHashForRelease(sourceRoot, Array.Empty<string>()).ToLowerInvariant();
        string relative = "artifacts/" + ledger.Sha256.ToLowerInvariant() + "/" + sourceTree;
        string artifactsRoot = Path.Combine(stateRoot, "artifacts");
        Directory.CreateDirectory(artifactsRoot);
        RejectReparsePoint(artifactsRoot, "Channel artifact root");
        string destination = ResolveContainedPath(stateRoot, relative,
            "Channel content-addressed artifact");
        RejectExistingReparsePath(Path.GetDirectoryName(destination)!,
            "Channel artifact storage");
        RejectExistingReparsePath(destination, "Channel content-addressed artifact");
        if (!Directory.Exists(destination))
        {
            string parent = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(parent);
            string staging = Path.Combine(parent, ".staging-" + Guid.NewGuid().ToString("N"));
            try
            {
                CopyDirectory(sourceRoot, staging);
                RejectReparseTree(staging, "Staged channel artifact");
                if (!string.Equals(TreeHashForRelease(staging, Array.Empty<string>()),
                        sourceTree, StringComparison.OrdinalIgnoreCase))
                    throw new DheException("Resource artifact changed while it was copied.");
                try
                {
                    Directory.Move(staging, destination);
                }
                catch (IOException) when (Directory.Exists(destination) &&
                    string.Equals(TreeHashForRelease(destination, Array.Empty<string>()),
                        sourceTree, StringComparison.OrdinalIgnoreCase))
                {
                    // A concurrent contender published the same immutable bytes.
                }
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
            }
        }
        if (!string.Equals(TreeHashForRelease(destination, Array.Empty<string>()), sourceTree,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Existing content-addressed resource artifact is inconsistent.");
        JsonElement copiedManifest = ReadJson<JsonElement>(RequireFile(Path.Combine(destination,
            "dhe-resource-update.json"), "Copied resource manifest"));
        ReleaseLedgerDocument? copiedLedger = ValidateReleaseLedgerForStaging(destination,
            copiedManifest);
        _ = ValidateResourceUpdateCompatibility(destination, copiedManifest);
        if (copiedLedger == null || !string.Equals(copiedLedger.Sha256, ledger.Sha256,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Copied resource artifact has another release ledger.");
        return (relative, sourceTree);
    }

    private static string PublishImmutableApproval(string stateRoot, string gatePath,
        string gateSha256)
    {
        string relative = "approvals/" + gateSha256.ToLowerInvariant() + ".json";
        string approvalsRoot = Path.Combine(stateRoot, "approvals");
        Directory.CreateDirectory(approvalsRoot);
        RejectReparsePoint(approvalsRoot, "Channel approval root");
        string destination = ResolveContainedPath(stateRoot, relative,
            "Channel content-addressed approval");
        byte[] bytes = File.ReadAllBytes(gatePath);
        WriteContentAddressedFile(destination, bytes);
        return relative;
    }

    private static void WriteContentAddressedFile(string path, byte[] bytes)
    {
        RejectExistingReparsePath(Path.GetDirectoryName(path)!,
            "Content-addressed storage root");
        RejectExistingReparsePath(path, "Content-addressed file");
        if (File.Exists(path))
        {
            if (!File.ReadAllBytes(path).SequenceEqual(bytes))
                throw new DheException("Content-addressed file contains different bytes: " + path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteDurableFile(temporary, bytes);
            try
            {
                File.Move(temporary, path);
            }
            catch (IOException) when (File.Exists(path) &&
                File.ReadAllBytes(path).SequenceEqual(bytes))
            {
                File.Delete(temporary);
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void WriteDurableFile(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(true);
    }

    private static FileStream AcquireChannelLock(string stateRoot, string channelId,
        int timeoutSeconds)
    {
        string channelRoot = ChannelRoot(stateRoot, channelId);
        Directory.CreateDirectory(channelRoot);
        string lockPath = Path.Combine(channelRoot, ".channel.lock");
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.WriteThrough);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
            catch (IOException exception)
            {
                throw new DheException("Timed out acquiring the protected channel lock: " +
                    exception.Message);
            }
        }
    }

    private static string ChannelRoot(string stateRoot, string channelId)
    {
        if (!IsRegistryId(channelId))
            throw new DheException("ChannelId contains unsupported characters.");
        string channelsRoot = Path.Combine(stateRoot, "channels");
        Directory.CreateDirectory(channelsRoot);
        RejectReparsePoint(channelsRoot, "Channel channels root");
        string channelRoot = ResolveContainedPath(channelsRoot, channelId, "Channel state");
        RejectExistingReparsePath(channelRoot, "Channel state");
        return channelRoot;
    }

    private static int ReadChannelLockTimeout(Cli cli)
    {
        string value = cli.Optional("locktimeoutseconds") ?? "30";
        if (!int.TryParse(value, out int seconds) || seconds < 1 || seconds > 300)
            throw new DheException("LockTimeoutSeconds must be between 1 and 300.");
        return seconds;
    }

    private static void RejectReparseTree(string root, string description)
    {
        RejectReparsePoint(root, description);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (string path in Directory.GetFileSystemEntries(pending.Pop()))
            {
                RejectReparsePoint(path, description);
                if (Directory.Exists(path)) pending.Push(path);
            }
        }
    }

    private static void RejectExistingReparsePath(string path, string description)
    {
        if (File.Exists(path) || Directory.Exists(path))
            RejectReparsePoint(path, description);
    }

    private static void RejectReparsePoint(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new DheException(description + " contains a reparse point: " + path);
    }

    private static string[] ReadManifestEngineWorkflows(JsonElement manifest) => manifest
        .GetProperty("supportedBases").EnumerateArray()
        .Select(item => GetString(item, "engineWorkflow") ?? string.Empty)
        .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();

    private static string? NormalizeOptionalHash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.ToLowerInvariant();

    private static int? NullableInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result)
            ? result
            : null;

    private static void ValidateDocumentAgainstSchema(JsonElement document,
        string schemaRoot, string schemaName, string description)
    {
        string schemaPath = RequireFile(Path.Combine(schemaRoot, "schemas", schemaName),
            description + " schema");
        JsonElement schema = ReadJson<JsonElement>(schemaPath);
        var errors = new List<string>();
        ValidateSchemaVocabulary(schema, "$", errors);
        if (errors.Count == 0) ValidateJsonSchema(schema, document, schema, "$", errors);
        if (errors.Count != 0)
            throw new DheException(description + " violates its schema: " +
                string.Join("; ", errors));
    }

    private static bool RunChannelStateRegression(string regressionRoot,
        string previousUpdateRoot, string candidateUpdateRoot,
        IReadOnlyCollection<(JsonElement Report, string Path)> reports,
        string authorityRoot, string authorityPackageId, string validationSourceRoot,
        string schemaRoot, string? baseRegistryPath, string settingsFile,
        IReadOnlyCollection<string> evidenceToolchainRoots,
        out bool protectedResourceReleaseBuildPassed,
        out string protectedResourceReleaseBuildDetails,
        out ResourceReleasePlanRegressionResult planningRegression,
        out ResourceReleaseQualificationRegressionResult qualificationRegression,
        out string details)
    {
        protectedResourceReleaseBuildPassed = false;
        protectedResourceReleaseBuildDetails =
            "protected resource release build did not complete";
        planningRegression = ResourceReleasePlanRegressionResult.Failed;
        qualificationRegression = ResourceReleaseQualificationRegressionResult.Failed;
        details = "protected channel snapshot, adoption, promotion, and CAS validated";
        try
        {
            MultiBaseResourceReleaseProof proof = ReadMultiBaseResourceReleaseProof(reports,
                true);
            JsonElement previousManifest = ReadJson<JsonElement>(RequireFile(Path.Combine(
                previousUpdateRoot, "dhe-resource-update.json"),
                "Channel regression previous resource manifest"));
            ReleaseLedgerDocument previousLedger = ValidateReleaseLedgerForStaging(
                previousUpdateRoot, previousManifest) ??
                throw new DheException("Channel regression previous ledger is missing.");
            _ = ValidateResourceUpdateCompatibility(previousUpdateRoot, previousManifest);
            if (previousLedger.Revision + 1 != proof.ReleaseRevision ||
                !string.Equals(previousLedger.Sha256, proof.ParentReleaseLedgerSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(previousLedger.ChannelId, proof.ReleaseChannelId,
                    StringComparison.Ordinal))
                throw new DheException("Channel regression inputs are not consecutive releases.");

            Dictionary<string, string> BaseChannelArguments(string stateRoot, string channelId,
                string output) => new(StringComparer.OrdinalIgnoreCase)
            {
                ["stateroot"] = stateRoot,
                ["channelid"] = channelId,
                ["toolchainroot"] = authorityRoot,
                ["expectedtoolchainpackageid"] = authorityPackageId,
                ["schemaroot"] = schemaRoot,
                ["output"] = output,
            };

            string genesisStateRoot = Path.Combine(regressionRoot,
                "channel-state-genesis");
            string genesisSnapshotPath = Path.Combine(regressionRoot,
                "channel-state-genesis-snapshot.json");
            var genesisSnapshotArguments = BaseChannelArguments(genesisStateRoot,
                proof.ReleaseChannelId + "-genesis", genesisSnapshotPath);
            genesisSnapshotArguments["operation"] = "snapshot";
            _ = ChannelState(new Cli("channel-state", genesisSnapshotArguments));
            ChannelSnapshotDocument genesisSnapshot = ReadChannelSnapshot(
                genesisSnapshotPath, Sha256File(genesisSnapshotPath));
            bool genesisSnapshotPassed = !genesisSnapshot.Initialized &&
                genesisSnapshot.InitializationRequired &&
                genesisSnapshot.CurrentRevision == null &&
                genesisSnapshot.NextRevision == 1 &&
                genesisSnapshot.ChannelHeadSha256 == null &&
                genesisSnapshot.PreviousReleaseLedgerSha256 == null;

            string primaryStateRoot = Path.Combine(regressionRoot, "channel-state-primary");
            string adoptedSnapshotPath = Path.Combine(regressionRoot,
                "channel-state-adopted-snapshot.json");
            var adopt = BaseChannelArguments(primaryStateRoot, proof.ReleaseChannelId,
                adoptedSnapshotPath);
            adopt["operation"] = "adopt-existing";
            adopt["resourceupdateroot"] = previousUpdateRoot;
            adopt["expectedreleaseledgersha256"] = previousLedger.Sha256;
            adopt["acknowledgeexistingpublishedhead"] = "true";
            if (ChannelState(new Cli("channel-state", adopt)) != 0)
                throw new DheException("Existing channel adoption failed.");
            string adoptedSnapshotSha256 = Sha256File(adoptedSnapshotPath);
            ChannelSnapshotDocument adoptedSnapshot = ReadChannelSnapshot(adoptedSnapshotPath,
                adoptedSnapshotSha256);
            if (!adoptedSnapshot.Initialized ||
                adoptedSnapshot.CurrentRevision != previousLedger.Revision ||
                adoptedSnapshot.NextRevision != proof.ReleaseRevision ||
                !string.Equals(adoptedSnapshot.PreviousReleaseLedgerSha256,
                    previousLedger.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new DheException("Adopted channel snapshot is inconsistent.");

            planningRegression = RunResourceReleasePlanningRegression(
                regressionRoot, candidateUpdateRoot, adoptedSnapshotPath,
                adoptedSnapshotSha256, reports, authorityRoot, authorityPackageId,
                schemaRoot, evidenceToolchainRoots);
            qualificationRegression = RunResourceReleaseQualificationRegression(
                regressionRoot, candidateUpdateRoot, adoptedSnapshotPath,
                adoptedSnapshotSha256, reports, authorityRoot, authorityPackageId,
                schemaRoot, evidenceToolchainRoots);

            if (!string.IsNullOrWhiteSpace(baseRegistryPath))
            {
                BaseRegistryDocument releaseBuildRegistry = ReadBaseRegistry(baseRegistryPath);
                JsonElement candidateManifest = ReadJson<JsonElement>(RequireFile(Path.Combine(
                    candidateUpdateRoot, "dhe-resource-update.json"),
                    "Protected release build candidate manifest"));
                string releaseBuildRoot = Path.Combine(regressionRoot,
                    "channel-state-resource-release-build");
                string currentRoot = Path.Combine(releaseBuildRoot, "current");
                var variants = new List<object>();
                string candidateCurrentSet = GetString(candidateManifest,
                    "currentAssemblySetSha256") ?? string.Empty;
                bool primaryAssigned = false;
                foreach (JsonElement variant in candidateManifest.GetProperty("payloadVariants")
                             .EnumerateArray())
                {
                    string variantId = GetString(variant, "variantId") ?? string.Empty;
                    string variantRoot = Path.Combine(currentRoot, variantId);
                    Directory.CreateDirectory(variantRoot);
                    foreach (JsonElement assembly in variant.GetProperty("assemblies")
                                 .EnumerateArray())
                    {
                        string name = NormalizeName(GetString(assembly,
                            "assemblyName") ?? string.Empty);
                        string source = RequireFile(ResolveContainedPath(candidateUpdateRoot,
                            GetString(assembly, "dll") ?? string.Empty,
                            "Protected release build current assembly"),
                            "Protected release build current assembly");
                        File.Copy(source, Path.Combine(variantRoot, name + ".dll"), true);
                    }
                    bool primary = !primaryAssigned && string.Equals(GetString(variant,
                        "currentAssemblySetSha256"), candidateCurrentSet,
                        StringComparison.OrdinalIgnoreCase);
                    primaryAssigned |= primary;
                    variants.Add(new { variantId, root = variantRoot, primary });
                }
                if (!primaryAssigned)
                    throw new DheException("Protected release build has no primary current variant.");

                string? previousRegistry = null;
                if (releaseBuildRegistry.Revision > 1)
                {
                    string parentAudit = GetString(candidateManifest,
                        "baseRegistryParentAuditPath") ?? string.Empty;
                    previousRegistry = RequireFile(ResolveContainedPath(candidateUpdateRoot,
                        parentAudit, "Protected release build parent registry"),
                        "Protected release build parent registry");
                }
                string configPath = Path.Combine(releaseBuildRoot,
                    "protected-release-config.json");
                string outputRoot = Path.Combine(releaseBuildRoot, "release");
                WriteJson(configPath, new
                {
                    schemaVersion = 1,
                    format = "hybridclr.dhe-resource-release-build-config.json",
                    pathSemantics = "config-relative-v1",
                    mode = "Release",
                    settingsFile,
                    outputRoot,
                    existingRegistry = releaseBuildRegistry.SourcePath,
                    previousRegistry,
                    registryId = releaseBuildRegistry.RegistryId,
                    currentVariants = variants.ToArray(),
                    newBases = Array.Empty<object>(),
                    retireBaseIds = Array.Empty<string>(),
                    retirementReason = (string?)null,
                    channelSnapshot = adoptedSnapshotPath,
                    expectedChannelSnapshotSha256 = adoptedSnapshotSha256,
                    initializeReleaseLedger = false,
                });
                string releaseBuildSchemas = Directory.Exists(Path.Combine(schemaRoot,
                    "schemas")) ? Path.Combine(schemaRoot, "schemas") : schemaRoot;
                if (ResourceReleaseBuild(new Cli("resource-release-build",
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["config"] = configPath,
                            ["schemasroot"] = releaseBuildSchemas,
                        })) != 0)
                    throw new DheException("Protected resource release build failed.");
                JsonElement releaseBuild = ReadJson<JsonElement>(Path.Combine(outputRoot,
                    "dhe-resource-release-build.json"));
                protectedResourceReleaseBuildPassed = GetBool(releaseBuild, "passed") &&
                    GetBool(releaseBuild, "releaseReady") &&
                    GetBool(releaseBuild, "exactActiveBaseCoverage") &&
                    GetInt(releaseBuild, "activeBaseCount") ==
                        releaseBuildRegistry.Entries.Length &&
                    GetInt(releaseBuild, "releaseRevision") == proof.ReleaseRevision &&
                    string.Equals(GetString(releaseBuild, "releaseChannelId"),
                        proof.ReleaseChannelId, StringComparison.Ordinal) &&
                    string.Equals(GetString(releaseBuild,
                            "parentReleaseLedgerSha256"), previousLedger.Sha256,
                        StringComparison.OrdinalIgnoreCase);
                protectedResourceReleaseBuildDetails = protectedResourceReleaseBuildPassed
                    ? "one snapshot-pinned Release build reproduced the next channel revision"
                    : "snapshot-pinned Release build report did not match the protected head";
            }

            bool snapshotResourceContextPassed = true;
            if (!string.IsNullOrWhiteSpace(baseRegistryPath))
            {
                BaseRegistryDocument registry = ReadBaseRegistry(baseRegistryPath);
                ResourceReleaseContext context = PrepareResourceReleaseContext(new Cli(
                    "resource-update", new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["mode"] = "Release",
                        ["channelsnapshot"] = adoptedSnapshotPath,
                        ["expectedchannelsnapshotsha256"] = adoptedSnapshotSha256,
                    }), registry, null);
                snapshotResourceContextPassed = context.ReleaseReady &&
                    context.ChannelId == proof.ReleaseChannelId &&
                    context.Revision == proof.ReleaseRevision &&
                    string.Equals(context.ParentLedgerSha256, previousLedger.Sha256,
                        StringComparison.OrdinalIgnoreCase);
            }

            string tamperedSnapshotPath = Path.Combine(regressionRoot,
                "channel-state-tampered-snapshot.json");
            var tamperedSnapshot = System.Text.Json.Nodes.JsonNode.Parse(
                File.ReadAllText(adoptedSnapshotPath))!.AsObject();
            tamperedSnapshot["activeBaseCount"] = previousLedger.ActiveBaseCount + 1;
            WriteJson(tamperedSnapshotPath, tamperedSnapshot);
            bool tamperedSnapshotRejected = false;
            try
            {
                _ = ReadChannelSnapshot(tamperedSnapshotPath,
                    Sha256File(tamperedSnapshotPath));
            }
            catch (DheException)
            {
                tamperedSnapshotRejected = true;
            }

            Dictionary<string, string> GateArguments(string snapshotPath,
                string snapshotSha256, string output)
            {
                var values = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["toolchainroot"] = authorityRoot,
                    ["expectedtoolchainpackageid"] = authorityPackageId,
                    ["validationsourceroot"] = validationSourceRoot,
                    ["schemaroot"] = schemaRoot,
                    ["resourceupdateroot"] = candidateUpdateRoot,
                    ["channelsnapshot"] = snapshotPath,
                    ["expectedchannelsnapshotsha256"] = snapshotSha256,
                    ["expectedreleaseledgersha256"] = proof.ReleaseLedgerSha256,
                    ["requireenginematrix"] = "true",
                    ["changedplayers"] = string.Join(',', reports.Select(item => item.Path)),
                    ["output"] = output,
                };
                if (evidenceToolchainRoots.Count != 0)
                    values["evidencetoolchainroots"] = string.Join(',',
                        evidenceToolchainRoots);
                return values;
            }

            string regressionGateRoot = Path.Combine(regressionRoot, "channel-gates");
            Directory.CreateDirectory(regressionGateRoot);
            string gatePath = Path.Combine(regressionGateRoot,
                "channel-state-resource-release-gate.json");
            if (ResourceReleaseGate(new Cli("resource-release-gate", GateArguments(
                    adoptedSnapshotPath, adoptedSnapshotSha256, gatePath))) != 0)
                throw new DheException("State-bound resource release gate failed.");
            JsonElement stateGate = ReadJson<JsonElement>(gatePath);
            if (!GetBool(stateGate, "channelStateBound") ||
                !string.Equals(GetString(stateGate, "expectedChannelHeadSha256"),
                    adoptedSnapshot.ChannelHeadSha256, StringComparison.OrdinalIgnoreCase))
                throw new DheException("Resource release gate did not bind its channel head.");

            bool gateStateRootTamperRejected = false;
            string gateStateRootTamperPath = Path.Combine(regressionGateRoot,
                "channel-state-gate-root-tamper.json");
            var gateStateRootTamper = System.Text.Json.Nodes.JsonNode.Parse(
                File.ReadAllText(gatePath))!.AsObject();
            gateStateRootTamper["channelStateRoot"] = Path.Combine(regressionRoot,
                "another-channel-state");
            WriteJson(gateStateRootTamperPath, gateStateRootTamper);
            try
            {
                var tamperedPromote = BaseChannelArguments(primaryStateRoot,
                    proof.ReleaseChannelId, Path.Combine(regressionRoot,
                        "channel-state-gate-root-tamper-output.json"));
                tamperedPromote["operation"] = "promote";
                tamperedPromote["resourcereleasegate"] = gateStateRootTamperPath;
                tamperedPromote["expectedchannelheadsha256"] =
                    adoptedSnapshot.ChannelHeadSha256!;
                _ = ChannelState(new Cli("channel-state", tamperedPromote));
            }
            catch (DheException)
            {
                gateStateRootTamperRejected = true;
            }

            bool gateDerivedFieldTamperRejected = false;
            string gateDerivedTamperPath = Path.Combine(regressionGateRoot,
                "channel-state-gate-derived-tamper.json");
            var gateDerivedTamper = System.Text.Json.Nodes.JsonNode.Parse(
                File.ReadAllText(gatePath))!.AsObject();
            gateDerivedTamper["engineMatrixCovered"] = false;
            WriteJson(gateDerivedTamperPath, gateDerivedTamper);
            try
            {
                var tamperedPromote = BaseChannelArguments(primaryStateRoot,
                    proof.ReleaseChannelId, Path.Combine(regressionRoot,
                        "channel-state-gate-derived-tamper-output.json"));
                tamperedPromote["operation"] = "promote";
                tamperedPromote["resourcereleasegate"] = gateDerivedTamperPath;
                tamperedPromote["expectedchannelheadsha256"] =
                    adoptedSnapshot.ChannelHeadSha256!;
                _ = ChannelState(new Cli("channel-state", tamperedPromote));
            }
            catch (DheException)
            {
                gateDerivedFieldTamperRejected = true;
            }

            string promotedSnapshotPath = Path.Combine(regressionRoot,
                "channel-state-promoted-snapshot.json");
            var promote = BaseChannelArguments(primaryStateRoot, proof.ReleaseChannelId,
                promotedSnapshotPath);
            promote["operation"] = "promote";
            promote["resourcereleasegate"] = gatePath;
            promote["expectedchannelheadsha256"] = adoptedSnapshot.ChannelHeadSha256!;
            if (ChannelState(new Cli("channel-state", promote)) != 0)
                throw new DheException("Qualified channel promotion failed.");
            ChannelSnapshotDocument promotedSnapshot = ReadChannelSnapshot(
                promotedSnapshotPath, Sha256File(promotedSnapshotPath));
            ChannelHeadDocument promotedHead = ReadChannelHead(primaryStateRoot,
                proof.ReleaseChannelId) ?? throw new DheException(
                    "Promoted channel head is missing.");
            bool primaryPassed = promotedSnapshot.CurrentRevision == proof.ReleaseRevision &&
                promotedSnapshot.NextRevision == proof.ReleaseRevision + 1 &&
                promotedSnapshot.ActiveBaseCount == reports.Count &&
                string.Equals(promotedSnapshot.PreviousReleaseLedgerSha256,
                    proof.ReleaseLedgerSha256, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(promotedHead.PreviousChannelHeadSha256,
                    adoptedSnapshot.ChannelHeadSha256, StringComparison.OrdinalIgnoreCase) &&
                promotedHead.PlayerReportCount == reports.Count &&
                promotedHead.ExactActiveBaseCoverage && promotedHead.EngineMatrixCovered;

            bool staleReplayRejected = false;
            try
            {
                var stale = BaseChannelArguments(primaryStateRoot, proof.ReleaseChannelId,
                    Path.Combine(regressionRoot, "channel-state-stale-replay.json"));
                stale["operation"] = "promote";
                stale["resourcereleasegate"] = gatePath;
                stale["expectedchannelheadsha256"] = adoptedSnapshot.ChannelHeadSha256!;
                _ = ChannelState(new Cli("channel-state", stale));
            }
            catch (DheException)
            {
                staleReplayRejected = true;
            }

            string concurrentStateRoot = Path.Combine(regressionRoot,
                "channel-state-concurrent");
            string concurrentAdoptedPath = Path.Combine(regressionRoot,
                "channel-state-concurrent-adopted.json");
            var concurrentAdopt = BaseChannelArguments(concurrentStateRoot,
                proof.ReleaseChannelId, concurrentAdoptedPath);
            concurrentAdopt["operation"] = "adopt-existing";
            concurrentAdopt["resourceupdateroot"] = previousUpdateRoot;
            concurrentAdopt["expectedreleaseledgersha256"] = previousLedger.Sha256;
            concurrentAdopt["acknowledgeexistingpublishedhead"] = "true";
            _ = ChannelState(new Cli("channel-state", concurrentAdopt));
            string concurrentSnapshotSha256 = Sha256File(concurrentAdoptedPath);
            ChannelSnapshotDocument concurrentSnapshot = ReadChannelSnapshot(
                concurrentAdoptedPath, concurrentSnapshotSha256);
            string concurrentGatePath = Path.Combine(regressionGateRoot,
                "channel-state-concurrent-gate.json");
            _ = ResourceReleaseGate(new Cli("resource-release-gate", GateArguments(
                concurrentAdoptedPath, concurrentSnapshotSha256, concurrentGatePath)));

            string orphanRoot = ResolveContainedPath(concurrentStateRoot,
                "artifacts/" + proof.ReleaseLedgerSha256 + "/.staging-orphan",
                "Channel crash-recovery fixture");
            Directory.CreateDirectory(orphanRoot);
            File.WriteAllText(Path.Combine(orphanRoot, "interrupted.tmp"), "orphan",
                new UTF8Encoding(false));

            bool[] concurrentResults = new bool[2];
            Task[] contenders = Enumerable.Range(0, concurrentResults.Length).Select(index =>
                Task.Run(() =>
                {
                    try
                    {
                        var contender = BaseChannelArguments(concurrentStateRoot,
                            proof.ReleaseChannelId, Path.Combine(regressionRoot,
                                "channel-state-concurrent-result-" + index + ".json"));
                        contender["operation"] = "promote";
                        contender["resourcereleasegate"] = concurrentGatePath;
                        contender["expectedchannelheadsha256"] =
                            concurrentSnapshot.ChannelHeadSha256!;
                        concurrentResults[index] = ChannelState(new Cli("channel-state",
                            contender)) == 0;
                    }
                    catch (DheException)
                    {
                        concurrentResults[index] = false;
                    }
                })).ToArray();
            Task.WaitAll(contenders);
            ChannelHeadDocument concurrentHead = ReadChannelHead(concurrentStateRoot,
                proof.ReleaseChannelId) ?? throw new DheException(
                    "Concurrent channel promotion did not publish a head.");
            bool concurrentPassed = concurrentResults.Count(value => value) == 1 &&
                concurrentHead.ReleaseRevision == proof.ReleaseRevision &&
                concurrentHead.ActiveBaseCount == reports.Count &&
                concurrentHead.PlayerReportCount == reports.Count &&
                Directory.Exists(orphanRoot);

            string recoveryStateRoot = Path.Combine(regressionRoot,
                "channel-state-output-recovery");
            string recoveryAdoptedPath = Path.Combine(regressionRoot,
                "channel-state-output-recovery-adopted.json");
            var recoveryAdopt = BaseChannelArguments(recoveryStateRoot,
                proof.ReleaseChannelId, recoveryAdoptedPath);
            recoveryAdopt["operation"] = "adopt-existing";
            recoveryAdopt["resourceupdateroot"] = previousUpdateRoot;
            recoveryAdopt["expectedreleaseledgersha256"] = previousLedger.Sha256;
            recoveryAdopt["acknowledgeexistingpublishedhead"] = "true";
            _ = ChannelState(new Cli("channel-state", recoveryAdopt));
            string recoveryAdoptedSha256 = Sha256File(recoveryAdoptedPath);
            ChannelSnapshotDocument recoverySnapshot = ReadChannelSnapshot(
                recoveryAdoptedPath, recoveryAdoptedSha256);
            string recoveryGatePath = Path.Combine(regressionGateRoot,
                "channel-state-output-recovery-gate.json");
            _ = ResourceReleaseGate(new Cli("resource-release-gate", GateArguments(
                recoveryAdoptedPath, recoveryAdoptedSha256, recoveryGatePath)));
            string invalidOutput = Path.Combine(regressionRoot,
                "channel-state-output-is-directory");
            Directory.CreateDirectory(invalidOutput);
            bool postCommitFailureObserved = false;
            try
            {
                var recoveryPromote = BaseChannelArguments(recoveryStateRoot,
                    proof.ReleaseChannelId, invalidOutput);
                recoveryPromote["operation"] = "promote";
                recoveryPromote["resourcereleasegate"] = recoveryGatePath;
                recoveryPromote["expectedchannelheadsha256"] =
                    recoverySnapshot.ChannelHeadSha256!;
                _ = ChannelState(new Cli("channel-state", recoveryPromote));
            }
            catch (IOException)
            {
                postCommitFailureObserved = true;
            }
            catch (UnauthorizedAccessException)
            {
                postCommitFailureObserved = true;
            }
            string recoveredSnapshotPath = Path.Combine(regressionRoot,
                "channel-state-output-recovered.json");
            var recoverSnapshot = BaseChannelArguments(recoveryStateRoot,
                proof.ReleaseChannelId, recoveredSnapshotPath);
            recoverSnapshot["operation"] = "snapshot";
            _ = ChannelState(new Cli("channel-state", recoverSnapshot));
            ChannelSnapshotDocument recoveredSnapshot = ReadChannelSnapshot(
                recoveredSnapshotPath, Sha256File(recoveredSnapshotPath));
            bool postCommitOutputRecoveryPassed = postCommitFailureObserved &&
                recoveredSnapshot.CurrentRevision == proof.ReleaseRevision &&
                string.Equals(recoveredSnapshot.PreviousReleaseLedgerSha256,
                    proof.ReleaseLedgerSha256, StringComparison.OrdinalIgnoreCase);

            string schemaGatePath = Path.Combine(regressionRoot,
                "channel-state-schema-gate.json");
            bool schemaPassed = SchemaGate(new Cli("schema-gate",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["schemasroot"] = Path.Combine(schemaRoot, "schemas"),
                    ["inputroot"] = primaryStateRoot,
                    ["requireknownformats"] = "true",
                    ["output"] = schemaGatePath,
                })) == 0;

            bool passed = genesisSnapshotPassed && snapshotResourceContextPassed &&
                tamperedSnapshotRejected &&
                gateStateRootTamperRejected && gateDerivedFieldTamperRejected &&
                primaryPassed && staleReplayRejected && concurrentPassed &&
                postCommitOutputRecoveryPassed && schemaPassed;
            if (!passed)
                details = "one or more channel snapshot, CAS, concurrency, or schema checks failed";
            return passed;
        }
        catch (Exception exception)
        {
            details = exception.Message;
            return false;
        }
    }
}
