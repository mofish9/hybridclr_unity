using System.Text.Json;

namespace HybridCLR.DheTool;

internal static partial class Program
{
    private sealed record ToolPublicationPolicy(string Mode, bool ReleaseReady,
        string? EvidencePath, string? EvidenceSha256);

    // Source and portable-binary distributions share exactly the same evidence
    // admission. A build configuration called Release is not a qualification.
    private static ToolPublicationPolicy ResolveToolPublicationPolicy(Cli cli,
        string root, string sourceHead, string sourceTree, bool clean, bool tracked)
    {
        string requested = cli.Optional("mode") ?? "Exploratory";
        string mode = requested.Equals("Release", StringComparison.OrdinalIgnoreCase) ? "Release" :
            requested.Equals("Exploratory", StringComparison.OrdinalIgnoreCase) ? "Exploratory" :
            throw new DheException("Mode must be Exploratory or Release.");
        if (mode == "Exploratory")
        {
            if (!string.IsNullOrWhiteSpace(cli.Optional("releaseevidence")))
                throw new DheException("ReleaseEvidence requires -Mode Release.");
            return new ToolPublicationPolicy(mode, false, null, null);
        }
        if (!clean || !tracked)
            throw new DheException("Release publishing requires a clean Git-tracked source tree.");
        string path = RequireFile(cli.Require("releaseevidence"), "DHE toolchain release evidence");
        string evidenceHash = Sha256File(path);
        JsonElement evidence = ReadJson<JsonElement>(path);
        if (GetInt(evidence, "schemaVersion") != 1 ||
            GetString(evidence, "format") != "hybridclr.dhe-toolchain-release-evidence.json" || !GetBool(evidence, "passed"))
            throw new DheException("Toolchain release evidence is not a passing production evidence report.");
        if (!string.Equals(GetString(evidence, "sourceHead"), sourceHead, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(evidence, "sourceTree"), sourceTree, StringComparison.OrdinalIgnoreCase))
            throw new DheException("Toolchain release evidence does not match the source HEAD/tree being published.");
        ValidateEvidenceFiles(evidence, Path.GetDirectoryName(path)!, root, cli.GetList("evidencetoolchainroots"));
        if (!Sha256File(path).Equals(evidenceHash, StringComparison.OrdinalIgnoreCase))
            throw new DheException("Toolchain release evidence changed during validation.");
        return new ToolPublicationPolicy(mode, true, path, evidenceHash);
    }
}
