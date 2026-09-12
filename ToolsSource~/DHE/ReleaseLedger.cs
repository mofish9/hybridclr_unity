using System.Text.Json;

namespace HybridCLR.DheTool;

internal static partial class Program
{
    private const string ReleaseLedgerFileName = "dhe-release-ledger.json";

    private sealed record ReleaseLedgerDocument(
        string SourcePath,
        string Sha256,
        string ChannelId,
        int Revision,
        string? ParentLedgerSha256,
        string BaseRegistryId,
        int BaseRegistryRevision,
        string BaseRegistrySha256,
        int ActiveBaseCount,
        int RetiredBaseCount,
        string CurrentAssemblySetSha256,
        string PayloadVariantSetSha256,
        string ResourceUpdateManifestPath,
        string ResourceUpdateManifestSha256,
        string ResourceUpdateValidationPath,
        string ResourceUpdateValidationSha256);

    private sealed record ResourceReleaseContext(
        string Mode,
        bool ReleaseReady,
        string? ChannelId,
        int? Revision,
        string? ParentLedgerSha256,
        string? ChannelStateRoot)
    {
        public static ResourceReleaseContext Exploratory { get; } =
            new("Exploratory", false, null, null, null, null);
    }

    private static ResourceReleaseContext PrepareResourceReleaseContext(Cli cli,
        BaseRegistryDocument? registry, BaseRegistryDocument? previousRegistry)
    {
        string mode = cli.Optional("mode") ?? "Exploratory";
        if (mode is not ("Release" or "Exploratory"))
            throw new DheException("Resource update Mode must be Release or Exploratory.");

        bool initialize = cli.Has("initializereleaseledger");
        string? previousPath = cli.Optional("previousreleaseledger");
        string? expectedPreviousSha256 = cli.Optional("expectedpreviousreleaseledgersha256");
        string? requestedChannelId = cli.Optional("releasechannelid")?.Trim();
        string? snapshotPath = cli.Optional("channelsnapshot");
        string? expectedSnapshotSha256 = cli.Optional("expectedchannelsnapshotsha256");
        string? channelStateRoot = null;
        bool hasLedgerArguments = initialize || !string.IsNullOrWhiteSpace(previousPath) ||
            !string.IsNullOrWhiteSpace(expectedPreviousSha256) ||
            !string.IsNullOrWhiteSpace(requestedChannelId) ||
            !string.IsNullOrWhiteSpace(snapshotPath) ||
            !string.IsNullOrWhiteSpace(expectedSnapshotSha256);

        if (mode == "Exploratory")
        {
            if (hasLedgerArguments)
                throw new DheException(
                    "Release ledger arguments require resource-update -Mode Release.");
            return ResourceReleaseContext.Exploratory;
        }

        if (registry == null)
            throw new DheException("Release resource updates require -BaseRegistry.");
        if (!string.IsNullOrWhiteSpace(snapshotPath))
        {
            if (!string.IsNullOrWhiteSpace(previousPath) ||
                !string.IsNullOrWhiteSpace(expectedPreviousSha256) ||
                !string.IsNullOrWhiteSpace(requestedChannelId))
                throw new DheException("ChannelSnapshot cannot be combined with explicit previous " +
                    "ledger or ReleaseChannelId arguments.");
            ChannelSnapshotDocument snapshot = ReadChannelSnapshot(snapshotPath,
                expectedSnapshotSha256);
            channelStateRoot = snapshot.StateRoot;
            requestedChannelId = snapshot.ChannelId;
            if (snapshot.Initialized)
            {
                if (initialize)
                    throw new DheException("An initialized channel snapshot cannot initialize a new ledger.");
                previousPath = snapshot.PreviousReleaseLedger;
                expectedPreviousSha256 = snapshot.PreviousReleaseLedgerSha256;
            }
            else if (!initialize)
            {
                throw new DheException("An uninitialized channel snapshot requires " +
                    "InitializeReleaseLedger authorization.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(expectedSnapshotSha256))
        {
            throw new DheException("ExpectedChannelSnapshotSha256 requires ChannelSnapshot.");
        }
        if (initialize == !string.IsNullOrWhiteSpace(previousPath))
            throw new DheException(
                "Release resource updates require exactly one of InitializeReleaseLedger or PreviousReleaseLedger.");

        if (initialize)
        {
            if (!string.IsNullOrWhiteSpace(expectedPreviousSha256))
                throw new DheException(
                    "ExpectedPreviousReleaseLedgerSha256 cannot be used when initializing a release ledger.");
            if (!IsRegistryId(requestedChannelId ?? string.Empty))
                throw new DheException(
                    "ReleaseChannelId must contain only letters, digits, '.', '_' or '-'.");
            return new ResourceReleaseContext("Release", true, requestedChannelId, 1,
                null, channelStateRoot);
        }

        if (!IsHex(expectedPreviousSha256, 64, 64))
            throw new DheException(
                "A continuation requires ExpectedPreviousReleaseLedgerSha256 from the published release head.");
        ReleaseLedgerDocument previous = ReadReleaseLedger(previousPath!,
            expectedPreviousSha256);
        if (!string.IsNullOrWhiteSpace(requestedChannelId) &&
            !string.Equals(requestedChannelId, previous.ChannelId, StringComparison.Ordinal))
            throw new DheException("ReleaseChannelId does not match the previous release ledger.");
        if (!string.Equals(registry.RegistryId, previous.BaseRegistryId,
                StringComparison.Ordinal))
            throw new DheException(
                "Base registry ID does not match the published release ledger.");

        bool sameRegistry = string.Equals(registry.Sha256,
            previous.BaseRegistrySha256, StringComparison.OrdinalIgnoreCase);
        if (sameRegistry)
        {
            if (registry.Revision != previous.BaseRegistryRevision)
                throw new DheException(
                    "Base registry revision does not match the published release ledger.");
        }
        else
        {
            if (registry.Revision != previous.BaseRegistryRevision + 1 ||
                !string.Equals(registry.ParentRegistrySha256,
                    previous.BaseRegistrySha256, StringComparison.OrdinalIgnoreCase))
                throw new DheException(
                    "Base registry must be unchanged or the direct successor of the published release head.");
            if (previousRegistry == null ||
                !string.Equals(previousRegistry.Sha256,
                    previous.BaseRegistrySha256, StringComparison.OrdinalIgnoreCase))
                throw new DheException(
                    "PreviousBaseRegistry must be the registry pinned by the previous release ledger.");
        }

        return new ResourceReleaseContext("Release", true, previous.ChannelId,
            checked(previous.Revision + 1), previous.Sha256, channelStateRoot);
    }

    private static ReleaseLedgerDocument WriteReleaseLedger(string outputRoot,
        ResourceReleaseContext release, BaseRegistryDocument registry,
        string currentAssemblySetSha256, string payloadVariantSetSha256,
        string manifestPath, string validationPath)
    {
        if (!release.ReleaseReady || string.IsNullOrWhiteSpace(release.ChannelId) ||
            release.Revision == null)
            throw new DheException("Cannot write a ledger for a non-Release resource update.");

        string ledgerPath = Path.Combine(outputRoot, ReleaseLedgerFileName);
        WriteJson(ledgerPath, new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-release-ledger.json",
            pathSemantics = "release-relative-v1",
            generatedAtUtc = DateTimeOffset.UtcNow,
            mode = "Release",
            releaseReady = true,
            channelId = release.ChannelId,
            revision = release.Revision.Value,
            parentLedgerSha256 = release.ParentLedgerSha256,
            baseRegistryId = registry.RegistryId,
            baseRegistryRevision = registry.Revision,
            baseRegistrySha256 = registry.Sha256,
            activeBaseCount = registry.Entries.Length,
            retiredBaseCount = registry.RetiredBases.Length,
            currentAssemblySetSha256,
            payloadVariantSetSha256,
            resourceUpdateManifest = Path.GetFileName(manifestPath),
            resourceUpdateManifestSha256 = Sha256File(manifestPath),
            resourceUpdateValidation = Path.GetFileName(validationPath),
            resourceUpdateValidationSha256 = Sha256File(validationPath),
        });
        return ReadReleaseLedger(ledgerPath, Sha256File(ledgerPath));
    }

    private static ReleaseLedgerDocument? ValidateReleaseLedgerForStaging(string updateRoot,
        JsonElement manifest)
    {
        string mode = GetString(manifest, "mode") ?? "Exploratory";
        if (mode == "Exploratory")
        {
            if (GetBool(manifest, "releaseReady") ||
                !string.IsNullOrWhiteSpace(GetString(manifest, "releaseLedger")) ||
                !string.IsNullOrWhiteSpace(GetString(manifest, "releaseChannelId")) ||
                manifest.TryGetProperty("releaseRevision", out JsonElement revision) &&
                    revision.ValueKind != JsonValueKind.Null ||
                !string.IsNullOrWhiteSpace(GetString(manifest,
                    "parentReleaseLedgerSha256")))
                throw new DheException(
                    "Exploratory resource update contains Release ledger fields.");
            return null;
        }
        if (mode != "Release" || !GetBool(manifest, "releaseReady"))
            throw new DheException("Resource update release mode is invalid.");

        string relative = GetString(manifest, "releaseLedger") ?? string.Empty;
        if (!string.Equals(relative, ReleaseLedgerFileName, StringComparison.Ordinal))
            throw new DheException("Release resource update has an invalid ledger path.");
        string ledgerPath = RequireFile(ResolveContainedPath(updateRoot, relative,
            "DHE release ledger"), "DHE release ledger");
        return ReadReleaseLedger(ledgerPath);
    }

    private static ReleaseLedgerDocument ReadReleaseLedger(string path,
        string? expectedSha256 = null)
    {
        string sourcePath = RequireFile(path, "DHE release ledger");
        string sha256 = Sha256File(sourcePath);
        if (!string.IsNullOrWhiteSpace(expectedSha256) &&
            (!IsHex(expectedSha256, 64, 64) ||
             !string.Equals(sha256, expectedSha256,
                 StringComparison.OrdinalIgnoreCase)))
            throw new DheException(
                "Previous release ledger does not match the published head SHA-256.");

        JsonElement ledger = ReadJson<JsonElement>(sourcePath);
        string channelId = GetString(ledger, "channelId") ?? string.Empty;
        int revision = GetInt(ledger, "revision");
        string? parentSha256 = GetString(ledger, "parentLedgerSha256");
        string registryId = GetString(ledger, "baseRegistryId") ?? string.Empty;
        int registryRevision = GetInt(ledger, "baseRegistryRevision");
        string registrySha256 = GetString(ledger, "baseRegistrySha256") ?? string.Empty;
        int activeBaseCount = GetInt(ledger, "activeBaseCount");
        int retiredBaseCount = GetInt(ledger, "retiredBaseCount");
        string currentSet = GetString(ledger, "currentAssemblySetSha256") ?? string.Empty;
        string variantSet = GetString(ledger, "payloadVariantSetSha256") ?? string.Empty;
        string manifestRelative = GetString(ledger, "resourceUpdateManifest") ?? string.Empty;
        string manifestSha256 = GetString(ledger,
            "resourceUpdateManifestSha256") ?? string.Empty;
        string validationRelative = GetString(ledger,
            "resourceUpdateValidation") ?? string.Empty;
        string validationSha256 = GetString(ledger,
            "resourceUpdateValidationSha256") ?? string.Empty;

        if (GetInt(ledger, "schemaVersion") != 1 ||
            !string.Equals(GetString(ledger, "format"),
                "hybridclr.dhe-release-ledger.json", StringComparison.Ordinal) ||
            !string.Equals(GetString(ledger, "pathSemantics"),
                "release-relative-v1", StringComparison.Ordinal) ||
            !string.Equals(GetString(ledger, "mode"), "Release",
                StringComparison.Ordinal) || !GetBool(ledger, "releaseReady") ||
            !IsRegistryId(channelId) || revision < 1 ||
            (revision == 1 && !string.IsNullOrWhiteSpace(parentSha256)) ||
            (revision > 1 && !IsHex(parentSha256, 64, 64)) ||
            !IsRegistryId(registryId) || registryRevision < 1 ||
            !IsHex(registrySha256, 64, 64) || activeBaseCount < 1 ||
            retiredBaseCount < 0 || !IsHex(currentSet, 64, 64) ||
            !IsHex(variantSet, 64, 64) ||
            !IsPortableRelativePath(manifestRelative) || manifestRelative.Contains('/') ||
            !IsHex(manifestSha256, 64, 64) ||
            !IsPortableRelativePath(validationRelative) || validationRelative.Contains('/') ||
            !IsHex(validationSha256, 64, 64))
            throw new DheException("DHE release ledger contract is invalid: " + sourcePath);

        string root = Path.GetDirectoryName(sourcePath)!;
        string manifestPath = RequireFile(ResolveContainedPath(root, manifestRelative,
            "Ledger resource manifest"), "Ledger resource manifest");
        string validationPath = RequireFile(ResolveContainedPath(root, validationRelative,
            "Ledger resource validation"), "Ledger resource validation");
        if (!string.Equals(Sha256File(manifestPath), manifestSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Sha256File(validationPath), validationSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("DHE release ledger artifact hash is invalid.");

        JsonElement manifest = ReadJson<JsonElement>(manifestPath);
        if (!string.Equals(GetString(manifest, "format"),
                "hybridclr.dhe-resource-update.json", StringComparison.Ordinal) ||
            !string.Equals(GetString(manifest, "mode"), "Release", StringComparison.Ordinal) ||
            !GetBool(manifest, "releaseReady") ||
            !string.Equals(GetString(manifest, "releaseLedger"),
                Path.GetFileName(sourcePath), StringComparison.Ordinal) ||
            !string.Equals(GetString(manifest, "releaseChannelId"), channelId,
                StringComparison.Ordinal) || GetInt(manifest, "releaseRevision") != revision ||
            !string.Equals(GetString(manifest, "parentReleaseLedgerSha256"),
                parentSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(manifest, "baseRegistryId"), registryId,
                StringComparison.Ordinal) ||
            GetInt(manifest, "baseRegistryRevision") != registryRevision ||
            !string.Equals(GetString(manifest, "baseRegistrySha256"), registrySha256,
                StringComparison.OrdinalIgnoreCase) ||
            GetInt(manifest, "baseRegistryEntryCount") != activeBaseCount ||
            GetInt(manifest, "baseRegistryRetiredBaseCount") != retiredBaseCount ||
            !string.Equals(GetString(manifest, "currentAssemblySetSha256"), currentSet,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(manifest, "payloadVariantSetSha256"), variantSet,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(manifest, "validation"), validationRelative,
                StringComparison.Ordinal) ||
            !string.Equals(GetString(manifest, "validationSha256"), validationSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("DHE release ledger does not match its resource manifest.");

        return new ReleaseLedgerDocument(sourcePath, sha256, channelId, revision,
            parentSha256, registryId, registryRevision, registrySha256,
            activeBaseCount, retiredBaseCount, currentSet, variantSet,
            manifestPath, manifestSha256, validationPath, validationSha256);
    }
}
