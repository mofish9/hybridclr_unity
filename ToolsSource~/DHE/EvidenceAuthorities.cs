using System.Text.Json;

namespace HybridCLR.DheTool;

internal static partial class Program
{
    private const string EvidenceAuthorityPolicy = "explicit-package-id-set-v1";
    private const string EvidenceAuthorityFileName =
        "manifests/dhe-toolchain-evidence-authorities.json";

    private sealed record EvidenceAuthority(
        string ToolchainVersion,
        string PackageId,
        string SourceHead,
        string SourceTree);

    private sealed record EvidenceAuthoritySet(
        string Policy,
        string? SourcePath,
        string? Sha256,
        IReadOnlyDictionary<string, EvidenceAuthority> Authorities)
    {
        public static EvidenceAuthoritySet None { get; } = new(
            "none", null, null,
            new Dictionary<string, EvidenceAuthority>(StringComparer.OrdinalIgnoreCase));
    }

    private sealed record PlayerToolchainAuthority(
        string PackageRoot,
        string ToolchainVersion,
        string PackageId,
        string SourceHead,
        string SourceTree);

    private static IReadOnlyDictionary<string, string> ReadEvidenceToolchainRoots(
        IEnumerable<string> roots, string currentPackageId)
    {
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string value in roots)
        {
            string root = RequireDirectory(value, "Historical evidence toolchain package");
            PackageInspection inspection = InspectPackage(root, null, true);
            if (!inspection.Passed || !IsHex(inspection.PackageId, 64, 64))
                throw new DheException("Historical evidence toolchain package is invalid: " +
                    root + ": " + string.Join("; ", inspection.Errors));
            if (string.Equals(inspection.PackageId, currentPackageId,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("EvidenceToolchainRoots must not replace the current " +
                    "publishing toolchain package.");
            if (!packages.TryAdd(inspection.PackageId!, root))
                throw new DheException("EvidenceToolchainRoots contains duplicate package ID: " +
                    inspection.PackageId + ".");
        }
        return packages;
    }

    private static void EnsureEvidenceToolchainRootsAreReferenced(
        IReadOnlyDictionary<string, string> packages, IEnumerable<string> referencedPackageIds)
    {
        HashSet<string> referenced = referencedPackageIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        string[] unused = packages.Keys.Where(packageId => !referenced.Contains(packageId))
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (unused.Length != 0)
            throw new DheException("EvidenceToolchainRoots contains packages not referenced " +
                "by active Base evidence: " + string.Join(", ", unused) + ".");
    }

    private static EvidenceAuthoritySet ReadEvidenceAuthoritySet(string packageRoot,
        string schemaRoot, string currentPackageId)
    {
        string path = Path.Combine(packageRoot,
            EvidenceAuthorityFileName.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return EvidenceAuthoritySet.None;

        RejectReparsePoint(path, "DHE toolchain evidence authority set");
        JsonElement document = ReadJson<JsonElement>(path);
        ValidateDocumentAgainstSchema(document, schemaRoot,
            "dhe-toolchain-evidence-authorities.schema.json",
            "DHE toolchain evidence authority set");
        if (GetInt(document, "schemaVersion") != 1 ||
            GetString(document, "format") !=
                "hybridclr.dhe-toolchain-evidence-authorities.json" ||
            GetString(document, "policy") != EvidenceAuthorityPolicy)
            throw new DheException("DHE toolchain evidence authority set contract is invalid.");

        var authorities = new Dictionary<string, EvidenceAuthority>(
            StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in document.GetProperty("authorities").EnumerateArray())
        {
            var authority = new EvidenceAuthority(
                GetString(item, "toolchainVersion") ?? string.Empty,
                (GetString(item, "packageId") ?? string.Empty).ToLowerInvariant(),
                (GetString(item, "sourceHead") ?? string.Empty).ToLowerInvariant(),
                (GetString(item, "sourceTree") ?? string.Empty).ToLowerInvariant());
            if (!IsToolchainVersion(authority.ToolchainVersion) ||
                !IsHex(authority.PackageId, 64, 64) ||
                !IsHex(authority.SourceHead, 40, 64) ||
                !IsHex(authority.SourceTree, 40, 64) ||
                string.Equals(authority.PackageId, currentPackageId,
                    StringComparison.OrdinalIgnoreCase) ||
                !authorities.TryAdd(authority.PackageId, authority))
                throw new DheException(
                    "DHE toolchain evidence authority set contains an invalid, current, or duplicate package.");
        }
        return new EvidenceAuthoritySet(EvidenceAuthorityPolicy, Path.GetFullPath(path),
            Sha256File(path), authorities);
    }

    private static bool IsToolchainVersion(string value)
    {
        string[] parts = value.Split('.');
        return parts.Length == 3 && parts.All(part => part.Length > 0 &&
            part.All(char.IsDigit));
    }

    private static bool HasImmediatePredecessorAuthority(string currentVersion,
        EvidenceAuthoritySet authoritySet)
    {
        if (!System.Version.TryParse(currentVersion, out System.Version? parsed) ||
            parsed == null || parsed.Build <= 0)
            return false;
        string predecessor = parsed.Major + "." + parsed.Minor + "." +
            (parsed.Build - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return authoritySet.Authorities.Values.Any(authority => string.Equals(
            authority.ToolchainVersion, predecessor, StringComparison.Ordinal));
    }

    private static void ValidateAuthorizedHistoricalPackage(EvidenceAuthority expected,
        PlayerToolchainAuthority actual)
    {
        if (!string.Equals(expected.PackageId, actual.PackageId,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.ToolchainVersion, actual.ToolchainVersion,
                StringComparison.Ordinal) ||
            !string.Equals(expected.SourceHead, actual.SourceHead,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.SourceTree, actual.SourceTree,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Historical Player toolchain package does not match its " +
                "authenticated authority-set record.");
    }

    private static bool RunEvidenceAuthoritySetRegression(string regressionRoot,
        string candidatePackageRoot,
        IReadOnlyCollection<(JsonElement Report, string Path)> reports,
        IReadOnlyCollection<string> historicalPackageRoots,
        out string details)
    {
        details = "portable mixed toolchain authority set validated";
        try
        {
            JsonElement candidateManifest = ReadJson<JsonElement>(RequireFile(Path.Combine(
                candidatePackageRoot, "dhe-toolchain-manifest.json"),
                "Candidate toolchain manifest"));
            string candidatePackageId = GetString(candidateManifest, "packageId") ??
                string.Empty;
            EvidenceAuthoritySet authoritySet = ReadEvidenceAuthoritySet(
                candidatePackageRoot, candidatePackageRoot, candidatePackageId);
            if (authoritySet.Policy != EvidenceAuthorityPolicy ||
                !IsHex(authoritySet.Sha256, 64, 64))
                throw new DheException("Candidate package lacks its authenticated evidence authorities.");

            IReadOnlyDictionary<string, string> historicalPackages =
                ReadEvidenceToolchainRoots(historicalPackageRoots, candidatePackageId);
            if (historicalPackages.Count != authoritySet.Authorities.Count ||
                authoritySet.Authorities.Keys.Any(packageId =>
                    !historicalPackages.ContainsKey(packageId)))
                throw new DheException("Regression EvidenceToolchainRoots must exactly cover " +
                    "the candidate package authority set.");
            foreach (EvidenceAuthority expected in authoritySet.Authorities.Values)
            {
                PlayerToolchainAuthority actual = InspectReleaseToolchainAuthority(
                    historicalPackages[expected.PackageId], expected.PackageId,
                    "Regression historical evidence toolchain package");
                ValidateAuthorizedHistoricalPackage(expected, actual);
            }

            var actualAuthorities = new List<(JsonElement Report, string Path,
                PlayerToolchainAuthority Authority)>();
            foreach ((JsonElement report, string path) in reports)
            {
                string packageId = GetString(report, "expectedToolchainPackageId") ??
                    string.Empty;
                if (!authoritySet.Authorities.TryGetValue(packageId,
                        out EvidenceAuthority? expected))
                    throw new DheException("Active Base evidence package is not authorized: " +
                        packageId + ".");
                PlayerToolchainAuthority actual = ValidateExactPlayerToolchainAuthority(
                    report, path, packageId, historicalPackages[packageId]);
                ValidateAuthorizedHistoricalPackage(expected, actual);
                actualAuthorities.Add((report, path, actual));
            }
            bool mixedAuthorities = actualAuthorities.Select(item => item.Authority.PackageId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2;
            bool unknownRejected = !authoritySet.Authorities.ContainsKey(new string('f', 64));

            (JsonElement firstReport, string firstPath,
                PlayerToolchainAuthority firstActual) = actualAuthorities.First();
            EvidenceAuthority firstExpected =
                authoritySet.Authorities[firstActual.PackageId];
            bool sourceTamperRejected = false;
            try
            {
                ValidateAuthorizedHistoricalPackage(firstExpected with
                {
                    SourceTree = new string('f', firstExpected.SourceTree.Length),
                }, firstActual);
            }
            catch (DheException)
            {
                sourceTamperRejected = true;
            }

            string relocatedRoot = Path.Combine(regressionRoot,
                "evidence-authority-relocated-package");
            CopyDirectory(firstActual.PackageRoot, relocatedRoot);
            IReadOnlyDictionary<string, string> relocatedPackages =
                ReadEvidenceToolchainRoots(new[] { relocatedRoot }, candidatePackageId);
            PlayerToolchainAuthority relocatedActual =
                ValidateExactPlayerToolchainAuthority(firstReport, firstPath,
                    firstActual.PackageId, relocatedPackages[firstActual.PackageId]);
            ValidateAuthorizedHistoricalPackage(firstExpected, relocatedActual);
            bool relocationAccepted = string.Equals(relocatedActual.PackageRoot,
                Path.GetFullPath(relocatedRoot), StringComparison.OrdinalIgnoreCase);

            (JsonElement Report, string Path, PlayerToolchainAuthority Authority) second =
                actualAuthorities.First(item => !string.Equals(item.Authority.PackageId,
                    firstActual.PackageId, StringComparison.OrdinalIgnoreCase));
            bool wrongIdRootRejected = false;
            try
            {
                _ = ValidateExactPlayerToolchainAuthority(firstReport, firstPath,
                    firstActual.PackageId, second.Authority.PackageRoot);
            }
            catch (DheException)
            {
                wrongIdRootRejected = true;
            }

            bool currentPackageSubstitutionRejected = false;
            try
            {
                _ = ReadEvidenceToolchainRoots(new[] { firstActual.PackageRoot },
                    firstActual.PackageId);
            }
            catch (DheException)
            {
                currentPackageSubstitutionRejected = true;
            }

            string duplicatePackageRoot = Path.Combine(regressionRoot,
                "evidence-authority-duplicate-root-package");
            CopyDirectory(firstActual.PackageRoot, duplicatePackageRoot);
            bool duplicatePackageRootRejected = false;
            try
            {
                _ = ReadEvidenceToolchainRoots(new[]
                {
                    relocatedRoot, duplicatePackageRoot,
                }, candidatePackageId);
            }
            catch (DheException)
            {
                duplicatePackageRootRejected = true;
            }

            bool unusedPackageRootRejected = false;
            try
            {
                IReadOnlyDictionary<string, string> mixedRoots =
                    ReadEvidenceToolchainRoots(new[]
                    {
                        relocatedRoot, second.Authority.PackageRoot,
                    }, candidatePackageId);
                EnsureEvidenceToolchainRootsAreReferenced(mixedRoots,
                    new[] { firstActual.PackageId });
            }
            catch (DheException)
            {
                unusedPackageRootRejected = true;
            }

            string duplicateRoot = Path.Combine(regressionRoot,
                "evidence-authority-duplicate-package");
            string duplicateManifestRoot = Path.Combine(duplicateRoot, "manifests");
            Directory.CreateDirectory(duplicateManifestRoot);
            var duplicate = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(
                authoritySet.SourcePath!))!.AsObject();
            System.Text.Json.Nodes.JsonNode duplicateRecord =
                System.Text.Json.Nodes.JsonNode.Parse(duplicate["authorities"]!
                    .AsArray()[0]!.ToJsonString())!;
            duplicate["authorities"]!.AsArray().Add(duplicateRecord);
            WriteJson(Path.Combine(duplicateManifestRoot,
                "dhe-toolchain-evidence-authorities.json"), duplicate);
            bool duplicateRejected = false;
            try
            {
                _ = ReadEvidenceAuthoritySet(duplicateRoot, candidatePackageRoot,
                    candidatePackageId);
            }
            catch (DheException)
            {
                duplicateRejected = true;
            }

            bool passed = mixedAuthorities && unknownRejected &&
                sourceTamperRejected && relocationAccepted && wrongIdRootRejected &&
                currentPackageSubstitutionRejected && duplicatePackageRootRejected &&
                unusedPackageRootRejected && duplicateRejected;
            if (!passed)
                details = "mixed, relocation, wrong-ID, unused-root, source-tamper, " +
                    "or duplicate authority check failed";
            return passed;
        }
        catch (Exception exception)
        {
            details = exception.Message;
            return false;
        }
    }

    private static PlayerToolchainAuthority InspectReleaseToolchainAuthority(
        string packageRoot, string expectedPackageId, string description)
    {
        string root = RequireDirectory(packageRoot, description);
        PackageInspection inspection = InspectPackage(root, expectedPackageId, true);
        if (!inspection.Passed)
            throw new DheException(description + " is not the expected Release package: " +
                string.Join("; ", inspection.Errors));
        JsonElement manifest = ReadJson<JsonElement>(inspection.ManifestPath);
        JsonElement source = manifest.GetProperty("sourceIdentity");
        string version = GetString(manifest, "toolchainVersion") ?? string.Empty;
        string packageId = inspection.PackageId ?? string.Empty;
        string sourceHead = GetString(source, "head") ?? string.Empty;
        string sourceTree = GetString(source, "tree") ?? string.Empty;
        if (!IsToolchainVersion(version) || !IsHex(packageId, 64, 64) ||
            !IsHex(sourceHead, 40, 64) || !IsHex(sourceTree, 40, 64))
            throw new DheException(description + " identity is incomplete.");
        return new PlayerToolchainAuthority(root, version, packageId.ToLowerInvariant(),
            sourceHead.ToLowerInvariant(), sourceTree.ToLowerInvariant());
    }
}
