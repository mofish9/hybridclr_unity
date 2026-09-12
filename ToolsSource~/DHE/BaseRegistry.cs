using System.Text.Json;
using System.Linq;

namespace HybridCLR.DheTool;

internal static partial class Program
{
    private sealed record BaseRegistryEntry(
        string BaseId,
        string EngineWorkflow,
        string PayloadVariantId,
        string Label,
        string BaselineRoot,
        string NativeManifest,
        string BuildIdentity,
        string? AotMetadataRoot);

    private sealed record BaseRegistryRetirement(
        string BaseId,
        string EngineWorkflow,
        string Label,
        int RetiredAtRevision,
        string Reason);

    private sealed record BaseRegistryDocument(
        string SourcePath,
        string PathSemantics,
        string Sha256,
        string RegistryId,
        int Revision,
        string? ParentRegistrySha256,
        BaseRegistryEntry[] Entries,
        BaseRegistryRetirement[] RetiredBases);

    private static BaseRegistryDocument ReadBaseRegistry(string path,
        bool requireArtifacts = true)
    {
        string sourcePath = RequireFile(path, "DHE Base registry");
        JsonElement document = ReadJson<JsonElement>(sourcePath);
        if (GetInt(document, "schemaVersion") != 1 ||
            !string.Equals(GetString(document, "format"),
                "hybridclr.dhe-base-registry.json", StringComparison.Ordinal))
            throw new DheException("DHE Base registry must use schema v1.");

        string pathSemantics = GetString(document, "pathSemantics") ?? string.Empty;
        if (pathSemantics is not ("registry-relative-v1" or "workspace-absolute-v1"))
            throw new DheException("DHE Base registry pathSemantics is invalid.");
        string registryId = GetString(document, "registryId") ?? string.Empty;
        if (!IsRegistryId(registryId))
            throw new DheException("DHE Base registry registryId is missing or invalid.");
        int revision = document.TryGetProperty("revision", out JsonElement revisionValue) &&
                       revisionValue.ValueKind == JsonValueKind.Number
            ? revisionValue.GetInt32()
            : 1;
        string? parentRegistrySha256 = GetString(document, "parentRegistrySha256");
        if (revision < 1 ||
            (revision == 1 && !string.IsNullOrWhiteSpace(parentRegistrySha256)) ||
            (revision > 1 && !IsHex(parentRegistrySha256, 64, 64)))
            throw new DheException("DHE Base registry revision lineage is invalid.");
        if (!document.TryGetProperty("bases", out JsonElement bases) ||
            bases.ValueKind != JsonValueKind.Array || bases.GetArrayLength() == 0)
            throw new DheException("DHE Base registry must contain at least one Base.");
        if (bases.GetArrayLength() > 1024)
            throw new DheException("DHE Base registry contains too many Base entries.");

        string registryDirectory = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
        var entries = new List<BaseRegistryEntry>(bases.GetArrayLength());
        var baseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buildIdentityPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in bases.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new DheException("DHE Base registry contains a non-object Base entry.");
            string baseId = GetString(item, "baseId") ?? string.Empty;
            if (!IsHex(baseId, 64, 64) || !baseIds.Add(baseId))
                throw new DheException("DHE Base registry contains an invalid or duplicate baseId.");
            string engineWorkflow = GetString(item, "engineWorkflow") ?? string.Empty;
            if (!KnownPlayerEngineWorkflows.Contains(engineWorkflow,
                    StringComparer.Ordinal))
                throw new DheException("DHE Base registry contains an unsupported engineWorkflow: " +
                    engineWorkflow);
            string payloadVariantId = GetString(item, "payloadVariantId") ?? "default";
            if (!IsPayloadVariantId(payloadVariantId))
                throw new DheException("DHE Base registry contains an invalid payloadVariantId.");
            string label = GetString(item, "label") ?? baseId;
            if (string.IsNullOrWhiteSpace(label) || label.Length > 256)
                throw new DheException("DHE Base registry contains an invalid label.");

            string baselineRoot = ResolveBaseRegistryPath(item, "baselineRoot", registryDirectory,
                pathSemantics, requireDirectory: true, requireArtifacts: requireArtifacts);
            string nativeManifest = ResolveBaseRegistryPath(item, "nativeManifest", registryDirectory,
                pathSemantics, requireDirectory: false, requireArtifacts: requireArtifacts);
            string buildIdentity = ResolveBaseRegistryPath(item, "buildIdentity", registryDirectory,
                pathSemantics, requireDirectory: false, requireArtifacts: requireArtifacts);
            if (!buildIdentityPaths.Add(buildIdentity))
                throw new DheException("DHE Base registry reuses one build identity path for multiple entries.");

            string? aotMetadataRoot = null;
            if (item.TryGetProperty("aotMetadataRoot", out JsonElement metadataValue) &&
                metadataValue.ValueKind != JsonValueKind.Null)
            {
                if (metadataValue.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(metadataValue.GetString()))
                    throw new DheException("DHE Base registry aotMetadataRoot must be a path or null.");
                aotMetadataRoot = ResolveBaseRegistryPath(item, "aotMetadataRoot",
                    registryDirectory, pathSemantics, requireDirectory: true,
                    requireArtifacts: requireArtifacts);
            }

            entries.Add(new BaseRegistryEntry(baseId, engineWorkflow, payloadVariantId, label, baselineRoot,
                nativeManifest, buildIdentity, aotMetadataRoot));
        }

        var retiredBases = new List<BaseRegistryRetirement>();
        var retiredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (document.TryGetProperty("retiredBases", out JsonElement retiredValues))
        {
            if (retiredValues.ValueKind != JsonValueKind.Array || retiredValues.GetArrayLength() > 1024)
                throw new DheException("DHE Base registry retiredBases is invalid.");
            foreach (JsonElement item in retiredValues.EnumerateArray())
            {
                string baseId = GetString(item, "baseId") ?? string.Empty;
                string engineWorkflow = GetString(item, "engineWorkflow") ?? string.Empty;
                string label = GetString(item, "label") ?? string.Empty;
                int retiredAtRevision = GetInt(item, "retiredAtRevision");
                string reason = GetString(item, "reason") ?? string.Empty;
                if (!IsHex(baseId, 64, 64) || !retiredIds.Add(baseId) ||
                    baseIds.Contains(baseId) ||
                    !KnownPlayerEngineWorkflows.Contains(engineWorkflow,
                        StringComparer.Ordinal) ||
                    string.IsNullOrWhiteSpace(label) || label.Length > 256 ||
                    retiredAtRevision < 2 || retiredAtRevision > revision ||
                    string.IsNullOrWhiteSpace(reason) || reason.Length > 512)
                    throw new DheException("DHE Base registry contains an invalid retirement record.");
                retiredBases.Add(new BaseRegistryRetirement(baseId, engineWorkflow, label,
                    retiredAtRevision, reason));
            }
        }

        return new BaseRegistryDocument(sourcePath, pathSemantics, Sha256File(sourcePath),
            registryId, revision, parentRegistrySha256, entries.ToArray(),
            retiredBases.OrderBy(item => item.RetiredAtRevision)
                .ThenBy(item => item.BaseId, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static bool IsRegistryId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            return false;
        return value.All(character => char.IsLetterOrDigit(character) ||
            character is '-' or '_' or '.');
    }

    private static BaseRegistryDocument? ValidateBaseRegistryLineage(
        BaseRegistryDocument current, string? previousPath)
    {
        if (current.Revision == 1)
        {
            if (!string.IsNullOrWhiteSpace(previousPath))
                throw new DheException(
                    "PreviousBaseRegistry cannot be supplied for registry revision 1.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(previousPath))
            throw new DheException(
                "Base registry revision 2 or later requires PreviousBaseRegistry.");

        // The parent file in a published resource is an authenticated identity
        // copy. Its artifact paths are relative to the original registry and
        // need not exist beside that relocated audit copy.
        BaseRegistryDocument previous = ReadBaseRegistry(previousPath,
            requireArtifacts: false);
        if (!string.Equals(current.RegistryId, previous.RegistryId, StringComparison.Ordinal) ||
            current.Revision != previous.Revision + 1 ||
            !string.Equals(current.ParentRegistrySha256, previous.Sha256,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException(
                "Base registry does not identify the supplied previous registry as its direct parent.");

        var previousActive = previous.Entries.ToDictionary(item => item.BaseId,
            StringComparer.OrdinalIgnoreCase);
        var currentActive = current.Entries.ToDictionary(item => item.BaseId,
            StringComparer.OrdinalIgnoreCase);
        var previousRetired = previous.RetiredBases.ToDictionary(item => item.BaseId,
            StringComparer.OrdinalIgnoreCase);
        var currentRetired = current.RetiredBases.ToDictionary(item => item.BaseId,
            StringComparer.OrdinalIgnoreCase);

        foreach (BaseRegistryRetirement retirement in previous.RetiredBases)
        {
            if (!currentRetired.TryGetValue(retirement.BaseId, out BaseRegistryRetirement? carried) ||
                !SameRetirement(retirement, carried))
                throw new DheException(
                    "Base registry dropped or changed an earlier retirement record: " +
                    retirement.BaseId + ".");
        }
        foreach (BaseRegistryRetirement retirement in current.RetiredBases)
        {
            if (retirement.RetiredAtRevision < current.Revision)
            {
                if (!previousRetired.TryGetValue(retirement.BaseId,
                        out BaseRegistryRetirement? earlier) ||
                    !SameRetirement(retirement, earlier))
                    throw new DheException(
                        "Base registry introduced a backdated retirement record: " +
                        retirement.BaseId + ".");
                continue;
            }
            if (!previousActive.TryGetValue(retirement.BaseId, out BaseRegistryEntry? active) ||
                !string.Equals(active.EngineWorkflow, retirement.EngineWorkflow,
                    StringComparison.Ordinal) ||
                !string.Equals(active.Label, retirement.Label, StringComparison.Ordinal))
                throw new DheException(
                    "Base registry retirement does not match an active Base in its parent: " +
                    retirement.BaseId + ".");
        }
        foreach (BaseRegistryEntry entry in previous.Entries)
        {
            bool retained = currentActive.TryGetValue(entry.BaseId,
                out BaseRegistryEntry? currentEntry);
            bool retired = currentRetired.TryGetValue(entry.BaseId,
                out BaseRegistryRetirement? retirement) &&
                retirement.RetiredAtRevision == current.Revision;
            if (retained == retired)
                throw new DheException(
                    "Every parent Base must be retained or explicitly retired exactly once: " +
                    entry.BaseId + ".");
            if (retained && !string.Equals(entry.EngineWorkflow,
                    currentEntry!.EngineWorkflow, StringComparison.Ordinal))
                throw new DheException(
                    "An active Base changed engine workflow across registry revisions: " +
                    entry.BaseId + ".");
        }
        foreach (BaseRegistryEntry entry in current.Entries)
        {
            if (previousRetired.ContainsKey(entry.BaseId))
                throw new DheException(
                    "A retired Base cannot be reactivated: " + entry.BaseId + ".");
        }
        return previous;
    }

    private static bool SameRetirement(BaseRegistryRetirement left,
        BaseRegistryRetirement right) =>
        string.Equals(left.BaseId, right.BaseId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.EngineWorkflow, right.EngineWorkflow, StringComparison.Ordinal) &&
        string.Equals(left.Label, right.Label, StringComparison.Ordinal) &&
        left.RetiredAtRevision == right.RetiredAtRevision &&
        string.Equals(left.Reason, right.Reason, StringComparison.Ordinal);

    private static bool IsPayloadVariantId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
            return false;
        return value.All(character => char.IsLetterOrDigit(character) ||
            character is '-' or '_' or '.');
    }

    private static string ResolveBaseRegistryPath(JsonElement entry, string property,
        string registryDirectory, string pathSemantics, bool requireDirectory,
        bool requireArtifacts)
    {
        string raw = GetString(entry, property) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw) || raw.IndexOf('\0') >= 0)
            throw new DheException("DHE Base registry " + property + " is missing or invalid.");
        bool rooted = Path.IsPathRooted(raw);
        if (pathSemantics == "registry-relative-v1" && rooted)
            throw new DheException("DHE Base registry " + property +
                " must be relative to the registry file.");
        if (pathSemantics == "workspace-absolute-v1" && !rooted)
            throw new DheException("DHE Base registry " + property +
                " must be an absolute workspace path.");
        string resolved = Path.GetFullPath(rooted ? raw : Path.Combine(registryDirectory, raw));
        if (requireArtifacts)
        {
            if (requireDirectory)
                RequireDirectory(resolved, "DHE Base registry " + property);
            else
                RequireFile(resolved, "DHE Base registry " + property);
        }
        return resolved;
    }
}
