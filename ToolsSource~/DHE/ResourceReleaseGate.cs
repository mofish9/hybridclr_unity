using System.Globalization;
using System.Text.Json;

namespace HybridCLR.DheTool;

internal static partial class Program
{
    private static int ResourceReleaseGate(Cli cli)
    {
        string updateRoot = Path.GetFullPath(cli.Require("resourceupdateroot"));
        string toolchainRoot = Path.GetFullPath(cli.Optional("toolchainroot") ?? cli.Root);
        string expectedPackageId = cli.Require("expectedtoolchainpackageid");
        string? validationSourceOption = cli.Optional("validationsourceroot");
        string schemaRoot = Path.GetFullPath(cli.Optional("schemaroot") ?? toolchainRoot);
        string validationSourceRoot = string.IsNullOrWhiteSpace(validationSourceOption)
            ? toolchainRoot
            : Path.GetFullPath(validationSourceOption);
        string? channelSnapshotOption = cli.Optional("channelsnapshot");
        string? channelSnapshotPath = string.IsNullOrWhiteSpace(channelSnapshotOption)
            ? null
            : Path.GetFullPath(channelSnapshotOption);
        List<string> evidenceToolchainRoots = cli.GetList("evidencetoolchainroots")
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        List<string> playerPaths = cli.GetList("changedplayers")
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (playerPaths.Count == 0 || playerPaths.Count > MaxChangedPlayerEvidenceCount)
            throw new DheException("Resource release gate requires between one and " +
                MaxChangedPlayerEvidenceCount + " distinct Player reports.");

        string output = SafeReportPath(cli.Require("output"), channelSnapshotPath == null
            ? playerPaths
            : playerPaths.Append(channelSnapshotPath));
        foreach (string protectedRoot in new[]
                 {
                     updateRoot, toolchainRoot, validationSourceRoot, schemaRoot,
                 }.Concat(evidenceToolchainRoots)
                 .Concat(playerPaths.Select(path => Path.GetDirectoryName(path)!))
                 .Distinct(StringComparer.OrdinalIgnoreCase))
            EnsureOutputOutsideRoot(output, protectedRoot);
        ChannelSnapshotDocument? channelSnapshot = null;
        if (channelSnapshotPath != null)
        {
            channelSnapshot = ReadChannelSnapshot(channelSnapshotPath,
                cli.Require("expectedchannelsnapshotsha256"));
            EnsureOutputOutsideRoot(output, channelSnapshot.StateRoot);
        }
        else if (!string.IsNullOrWhiteSpace(cli.Optional("expectedchannelsnapshotsha256")))
        {
            throw new DheException("ExpectedChannelSnapshotSha256 requires ChannelSnapshot.");
        }
        if (File.Exists(output)) File.Delete(output);

        updateRoot = RequireDirectory(updateRoot, "DHE resource release candidate");
        toolchainRoot = RequireDirectory(toolchainRoot, "DHE Release toolchain");
        schemaRoot = RequireDirectory(schemaRoot, "DHE resource release schema root");
        validationSourceRoot = RequireDirectory(validationSourceRoot,
            "DHE validation source root");
        RejectReparseTree(updateRoot, "DHE resource release candidate");
        foreach (string playerPath in playerPaths)
        {
            RequireFile(playerPath, "Changed Player report");
            RejectReparsePoint(playerPath, "Changed Player report");
        }
        PackageInspection authority = InspectPackage(toolchainRoot, expectedPackageId, true);
        if (!authority.Passed)
            throw new DheException("Resource release gate requires the exact authenticated " +
                "Release toolchain: " + string.Join("; ", authority.Errors));

        JsonElement authorityManifest = ReadJson<JsonElement>(authority.ManifestPath);
        JsonElement authoritySource = authorityManifest.GetProperty("sourceIdentity");
        string authorityHead = GetString(authoritySource, "head") ?? string.Empty;
        string authorityTree = GetString(authoritySource, "tree") ?? string.Empty;
        EvidenceAuthoritySet evidenceAuthoritySet = ReadEvidenceAuthoritySet(
            toolchainRoot, schemaRoot, authority.PackageId!);
        IReadOnlyDictionary<string, string> evidenceToolchainPackages =
            ReadEvidenceToolchainRoots(evidenceToolchainRoots, authority.PackageId!);
        string currentHead = string.IsNullOrWhiteSpace(validationSourceOption)
            ? authorityHead
            : GitValue(validationSourceRoot, "rev-parse", "HEAD");
        string currentTree = string.IsNullOrWhiteSpace(validationSourceOption)
            ? authorityTree
            : GitValue(validationSourceRoot, "rev-parse", "HEAD^{tree}");
        if (!IsHex(currentHead, 40, 64) || !IsHex(currentTree, 40, 64))
            throw new DheException("Resource release validation source identity is invalid.");
        if (!string.IsNullOrWhiteSpace(validationSourceOption) &&
            (!GitCommitHasTree(validationSourceRoot, authorityHead, authorityTree) ||
             !GitCommitIsAncestor(validationSourceRoot, authorityHead, currentHead)))
            throw new DheException("Release toolchain authority is not an ancestor of the " +
                "resource release validation source.");

        var reports = new List<(JsonElement Report, string Path)>();
        var playerAuthorityModes = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var playerAuthorityPackageIds = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var playerAuthorities = new Dictionary<string, PlayerToolchainAuthority>(
            StringComparer.OrdinalIgnoreCase);
        var runtimeContractAuthorities = new Dictionary<string, PlayerToolchainAuthority>(
            StringComparer.OrdinalIgnoreCase);
        bool historicalToolEvidenceAccepted = false;
        bool portableHistoricalToolEvidenceAccepted = false;
        foreach (string playerPath in playerPaths)
        {
            string path = RequireFile(playerPath, "Resource release Player report");
            JsonElement report = ReadJson<JsonElement>(path);
            RequireEvidenceFormat(report, "hybridclr.dhe-resource-player-workflow.json",
                "Resource release Player");
            if (!GetBool(report, "passed") || !GetBool(report, "validationPassed") ||
                !GetBool(report, "coverageGatePassed"))
                throw new DheException("Resource release Player did not pass workflow gates: " + path);
            ValidateResourcePlayerEvidenceBindings(report, path);
            string runtimeContractRoot = ValidateManagedReleaseEvidence(report, path,
                evidenceToolchainPackages.Values.Append(toolchainRoot));
            string runtimeContractPackageId;
            if (Path.GetFullPath(runtimeContractRoot).Equals(toolchainRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                runtimeContractPackageId = authority.PackageId!;
            }
            else
            {
                runtimeContractPackageId = evidenceToolchainPackages.SingleOrDefault(item =>
                    Path.GetFullPath(item.Value).Equals(Path.GetFullPath(runtimeContractRoot),
                        StringComparison.OrdinalIgnoreCase)).Key ?? string.Empty;
                if (!IsHex(runtimeContractPackageId, 64, 64))
                    throw new DheException("Managed Player runtime contract did not resolve " +
                        "to an authenticated evidence toolchain package.");
            }
            if (!runtimeContractAuthorities.ContainsKey(runtimeContractPackageId))
                runtimeContractAuthorities.Add(runtimeContractPackageId,
                    InspectReleaseToolchainAuthority(runtimeContractRoot,
                        runtimeContractPackageId,
                        "Managed Player runtime contract package"));
            ValidateResourceReleasePlayerCorrectness(report);

            string reportPackageId = GetString(report,
                "expectedToolchainPackageId") ?? string.Empty;
            if (string.Equals(reportPackageId, expectedPackageId,
                    StringComparison.OrdinalIgnoreCase))
            {
                playerAuthorities.Add(path, ValidateExactPlayerToolchainAuthority(
                    report, path, expectedPackageId, toolchainRoot));
                playerAuthorityModes.Add(path, "current-package");
            }
            else if (evidenceAuthoritySet.Authorities.TryGetValue(reportPackageId,
                         out EvidenceAuthority? expectedAuthority))
            {
                PlayerToolchainAuthority actualAuthority =
                    ValidateExactPlayerToolchainAuthority(report, path, reportPackageId,
                        evidenceToolchainPackages.TryGetValue(reportPackageId,
                            out string? relocatedRoot) ? relocatedRoot : null);
                ValidateAuthorizedHistoricalPackage(expectedAuthority, actualAuthority);
                playerAuthorities.Add(path, actualAuthority);
                playerAuthorityModes.Add(path, "authorized-historical-package");
                historicalToolEvidenceAccepted = true;
                portableHistoricalToolEvidenceAccepted = true;
            }
            else
            {
                if (evidenceAuthoritySet.Policy != "none")
                    throw new DheException("Historical Player toolchain package is not " +
                        "authorized by the current Release package: " + reportPackageId + ".");
                if (string.IsNullOrWhiteSpace(validationSourceOption))
                    throw new DheException("Historical Player toolchain evidence requires " +
                        "ValidationSourceRoot ancestry validation.");
                ValidateEvidenceToolIdentity(report, path, validationSourceRoot,
                    currentHead, currentTree, evidenceToolchainPackages.Values);
                playerAuthorities.Add(path, ValidateExactPlayerToolchainAuthority(
                    report, path, reportPackageId,
                    evidenceToolchainPackages.TryGetValue(reportPackageId,
                        out string? relocatedRoot) ? relocatedRoot : null));
                playerAuthorityModes.Add(path, "git-ancestry");
                historicalToolEvidenceAccepted = true;
            }
            playerAuthorityPackageIds.Add(path, reportPackageId.ToLowerInvariant());
            reports.Add((report, path));
        }
        EnsureEvidenceToolchainRootsAreReferenced(evidenceToolchainPackages,
            playerAuthorityPackageIds.Values.Concat(runtimeContractAuthorities.Keys));

        bool requireEngineMatrix = cli.Has("requireenginematrix");
        MultiBaseResourceReleaseProof proof = ReadMultiBaseResourceReleaseProof(reports,
            requireEngineMatrix);
        string manifestPath = RequireFile(Path.Combine(updateRoot,
            "dhe-resource-update.json"), "Resource release manifest");
        JsonElement manifest = ReadJson<JsonElement>(manifestPath);
        ReleaseLedgerDocument? releaseLedger = ValidateReleaseLedgerForStaging(updateRoot,
            manifest);
        string validationPath = ValidateResourceUpdateCompatibility(updateRoot, manifest);
        if (releaseLedger == null ||
            !string.Equals(Sha256File(manifestPath), proof.ResourceUpdateManifestSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(releaseLedger.Sha256, proof.ReleaseLedgerSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(releaseLedger.ChannelId, proof.ReleaseChannelId,
                StringComparison.Ordinal) ||
            releaseLedger.Revision != proof.ReleaseRevision ||
            !string.Equals(releaseLedger.ParentLedgerSha256,
                proof.ParentReleaseLedgerSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(releaseLedger.BaseRegistrySha256, proof.BaseRegistrySha256,
                StringComparison.OrdinalIgnoreCase) ||
            releaseLedger.ActiveBaseCount != proof.ActiveBaseCount ||
            !string.Equals(releaseLedger.CurrentAssemblySetSha256,
                proof.CurrentAssemblySetSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(releaseLedger.PayloadVariantSetSha256,
                proof.PayloadVariantSetSha256, StringComparison.OrdinalIgnoreCase))
            throw new DheException("Player evidence does not match the resource release candidate.");

        string expectedLedgerSha256 = cli.Require("expectedreleaseledgersha256");
        bool initialize = cli.Has("initializereleaseledger");
        string expectedChannelId;
        int expectedRevision;
        string? expectedPrevious;
        if (channelSnapshot != null)
        {
            if (!string.IsNullOrWhiteSpace(cli.Optional("expectedreleasechannelid")) ||
                !string.IsNullOrWhiteSpace(cli.Optional("expectedreleaserevision")) ||
                !string.IsNullOrWhiteSpace(cli.Optional("expectedpreviousreleaseledgersha256")))
                throw new DheException("ChannelSnapshot cannot be combined with explicit channel, " +
                    "revision, or previous-ledger expectations.");
            expectedChannelId = channelSnapshot.ChannelId;
            expectedRevision = channelSnapshot.NextRevision;
            expectedPrevious = channelSnapshot.PreviousReleaseLedgerSha256;
            if (initialize != channelSnapshot.InitializationRequired)
                throw new DheException(channelSnapshot.InitializationRequired
                    ? "An uninitialized channel snapshot requires explicit ledger initialization."
                    : "An initialized channel snapshot cannot reinitialize its ledger.");
        }
        else
        {
            expectedChannelId = cli.Require("expectedreleasechannelid");
            string expectedRevisionText = cli.Require("expectedreleaserevision");
            if (!int.TryParse(expectedRevisionText, NumberStyles.None,
                    CultureInfo.InvariantCulture, out expectedRevision) || expectedRevision < 1)
                throw new DheException("ExpectedReleaseRevision must be a positive integer.");
            expectedPrevious = NormalizeOptionalHash(
                cli.Optional("expectedpreviousreleaseledgersha256"));
        }
        if (!IsRegistryId(expectedChannelId) || !IsHex(expectedLedgerSha256, 64, 64) ||
            !string.Equals(expectedChannelId, releaseLedger.ChannelId,
                StringComparison.Ordinal) || expectedRevision != releaseLedger.Revision ||
            !string.Equals(expectedLedgerSha256, releaseLedger.Sha256,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Resource release candidate does not match the protected expected head.");

        if (releaseLedger.Revision == 1)
        {
            if (!initialize || !string.IsNullOrWhiteSpace(expectedPrevious) ||
                !string.IsNullOrWhiteSpace(releaseLedger.ParentLedgerSha256))
                throw new DheException("Release revision 1 requires explicit channel initialization " +
                    "and must not declare a previous ledger head.");
        }
        else if (initialize || !IsHex(expectedPrevious, 64, 64) ||
                 !string.Equals(expectedPrevious, releaseLedger.ParentLedgerSha256,
                     StringComparison.OrdinalIgnoreCase))
        {
            throw new DheException("A release continuation requires the exact protected previous " +
                "ledger head and cannot initialize the channel.");
        }

        string[] workflows = reports.Select(item =>
                GetChangedPlayerEvidenceIdentity(item.Report, item.Path).EngineWorkflow)
            .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        bool engineMatrixCovered = RequiredPlayerEngineWorkflows.All(workflows.Contains);
        var playerRecords = reports.Select(item =>
        {
            var identity = GetChangedPlayerEvidenceIdentity(item.Report, item.Path);
            JsonElement player = item.Report.GetProperty("player");
            return new
            {
                baseId = identity.BaseId,
                engineWorkflow = identity.EngineWorkflow,
                target = GetString(item.Report, "target"),
                payloadVariantId = GetString(item.Report,
                    "selectedPayloadVariantId") ?? "default",
                aotMetadataSetId = GetString(item.Report, "selectedAotMetadataSetId"),
                currentAssemblySetSha256 = GetString(item.Report,
                    "selectedPayloadCurrentAssemblySetSha256") ??
                    GetString(item.Report, "currentAssemblySetSha256"),
                changedMethodCount = GetInt(player, "changedMethodCount"),
                interpreterEntryCount = GetInt(player, "interpreterEntryCount"),
                aotEntryCount = GetInt(player, "aotEntryCount"),
                toolchainPackageId = playerAuthorityPackageIds[item.Path],
                toolchainAuthorityMode = playerAuthorityModes[item.Path],
                report = item.Path,
                reportSha256 = Sha256File(item.Path),
            };
        }).OrderBy(item => item.baseId, StringComparer.OrdinalIgnoreCase).ToArray();

        WriteJson(output, new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-resource-release-gate.json",
            generatedAtUtc = DateTimeOffset.UtcNow,
            passed = true,
            releaseReady = true,
            toolchainRoot,
            toolchainPackageId = authority.PackageId,
            toolchainSourceHead = authorityHead,
            toolchainSourceTree = authorityTree,
            validationSourceRoot = string.IsNullOrWhiteSpace(validationSourceOption)
                ? null : validationSourceRoot,
            validationSourceHead = currentHead,
            validationSourceTree = currentTree,
            historicalToolEvidenceAccepted,
            portableHistoricalToolEvidenceAccepted,
            evidenceAuthorityPolicy = evidenceAuthoritySet.Policy,
            evidenceAuthoritySet = evidenceAuthoritySet.SourcePath,
            evidenceAuthoritySetSha256 = evidenceAuthoritySet.Sha256,
            evidencePackageIds = playerAuthorityPackageIds.Values
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            evidenceToolchainPackages = playerAuthorities.Values
                .Concat(runtimeContractAuthorities.Values)
                .GroupBy(item => item.PackageId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => item.PackageId, StringComparer.Ordinal)
                .Select(item => new
                {
                    packageId = item.PackageId,
                    packageRoot = item.PackageRoot,
                    toolchainVersion = item.ToolchainVersion,
                    sourceHead = item.SourceHead,
                    sourceTree = item.SourceTree,
                }).ToArray(),
            resourceUpdateRoot = updateRoot,
            resourceUpdateManifest = manifestPath,
            resourceUpdateManifestSha256 = proof.ResourceUpdateManifestSha256,
            resourceUpdateValidation = validationPath,
            resourceUpdateValidationSha256 = releaseLedger.ResourceUpdateValidationSha256,
            releaseLedger = releaseLedger.SourcePath,
            releaseLedgerSha256 = releaseLedger.Sha256,
            parentReleaseLedgerSha256 = releaseLedger.ParentLedgerSha256,
            releaseChannelId = releaseLedger.ChannelId,
            releaseRevision = releaseLedger.Revision,
            initializationAuthorized = initialize,
            expectedReleaseChannelId = expectedChannelId,
            expectedReleaseRevision = expectedRevision,
            expectedReleaseLedgerSha256 = expectedLedgerSha256.ToLowerInvariant(),
            expectedPreviousReleaseLedgerSha256 = expectedPrevious?.ToLowerInvariant(),
            channelStateBound = channelSnapshot != null,
            channelStateRoot = channelSnapshot?.StateRoot,
            channelSnapshot = channelSnapshot?.SourcePath,
            channelSnapshotSha256 = channelSnapshot?.Sha256,
            expectedChannelHeadSha256 = channelSnapshot?.ChannelHeadSha256,
            baseRegistryId = releaseLedger.BaseRegistryId,
            baseRegistryRevision = releaseLedger.BaseRegistryRevision,
            baseRegistrySha256 = releaseLedger.BaseRegistrySha256,
            activeBaseCount = releaseLedger.ActiveBaseCount,
            retiredBaseCount = releaseLedger.RetiredBaseCount,
            currentAssemblySetSha256 = releaseLedger.CurrentAssemblySetSha256,
            payloadVariantSetSha256 = releaseLedger.PayloadVariantSetSha256,
            requireEngineMatrix,
            engineMatrixCovered,
            engineWorkflows = workflows,
            exactActiveBaseCoverage = true,
            playerReports = playerRecords,
            errors = Array.Empty<string>(),
            warnings = Array.Empty<string>(),
        });

        string schemaPath = RequireFile(Path.Combine(schemaRoot, "schemas",
            "dhe-resource-release-gate.schema.json"), "Resource release gate schema");
        JsonElement schema = ReadJson<JsonElement>(schemaPath);
        JsonElement result = ReadJson<JsonElement>(output);
        var schemaErrors = new List<string>();
        ValidateSchemaVocabulary(schema, "$", schemaErrors);
        if (schemaErrors.Count == 0)
            ValidateJsonSchema(schema, result, schema, "$", schemaErrors);
        if (schemaErrors.Count != 0)
        {
            File.Delete(output);
            throw new DheException("Resource release gate output violates its schema: " +
                string.Join("; ", schemaErrors));
        }
        Console.WriteLine("DHE resource release gate passed: " + output);
        return 0;
    }

    private static PlayerToolchainAuthority ValidateExactPlayerToolchainAuthority(JsonElement report,
        string reportPath, string expectedPackageId, string? packageRootOverride = null)
    {
        string root = Path.GetDirectoryName(reportPath)!;
        string gatePath = ResolveEvidencePath(GetString(report, "toolchainGate"), root,
            "Resource Player toolchain gate");
        JsonElement gate = ReadJson<JsonElement>(gatePath);
        RequireEvidenceFormat(gate, "hybridclr.dhe-toolchain-gate.json",
            "Resource Player toolchain gate");
        string recordedPackageRoot = GetString(gate, "packageRoot") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(recordedPackageRoot))
            throw new DheException("Resource Player toolchain gate lacks its package root.");
        string packageRoot = packageRootOverride == null
            ? RequireDirectory(recordedPackageRoot, "Resource Player toolchain package")
            : RequireDirectory(packageRootOverride, "Relocated Resource Player toolchain package");
        PlayerToolchainAuthority authority = InspectReleaseToolchainAuthority(packageRoot,
            expectedPackageId, "Resource Player toolchain package");
        if (!GetBool(gate, "passed") ||
            !GetBool(gate, "requireRelease") || !GetBool(gate, "releaseReady") ||
            !string.Equals(GetString(gate, "packageId"), expectedPackageId,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Resource Player was not produced by the expected Release toolchain.");
        return authority;
    }

    private static void ValidateResourceReleasePlayerCorrectness(JsonElement report)
    {
        JsonElement player = report.GetProperty("player");
        int changed = GetInt(report.GetProperty("capability"), "changedMethodCount");
        var errors = new List<string>();
        string[] interpreterOnlyAssemblies = ReadPlayerAssemblyNameArray(player,
            "plannedInterpreterOnlyAssemblies", errors);
        ValidateResourcePlayerExecution(player, changed, interpreterOnlyAssemblies.Length, errors);
        if (!GetBool(player, "capabilityDirectPassed") ||
            !GetBool(player, "secondaryAssemblyDirectValidated"))
            errors.Add("Resource Player did not prove direct capability and secondary assembly execution.");
        if (errors.Count != 0)
            throw new DheException(string.Join(" ", errors));
    }
}
