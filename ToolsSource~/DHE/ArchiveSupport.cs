using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HybridCLR.DheTool;

internal static partial class Program
{
    private sealed record ArchivePathMapping(string Source, string Destination);

    private static void PrepareArchiveDestination(string archive, bool force)
    {
        var full = Path.GetFullPath(archive).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(full) || string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(full, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new DheException("ArchiveRoot cannot be a filesystem or user-profile root: " + full);
        if (Directory.Exists(Path.Combine(full, ".git")) || File.Exists(Path.Combine(full, ".git")) ||
            Directory.Exists(Path.Combine(full, ".svn")))
            throw new DheException("ArchiveRoot cannot be a source repository root: " + full);
        if (!Directory.Exists(full)) return;
        if (!force) throw new DheException("ArchiveRoot already exists; pass -ForceOutput to replace a prior DHE archive.");
        var marker = Path.Combine(full, "dhe-archive-manifest.json");
        if (!File.Exists(marker) || GetString(ReadJson<JsonElement>(marker), "format") != "hybridclr.dhe-archive-manifest.json")
            throw new DheException("ArchiveRoot is not a replaceable DHE archive: " + full);
        Directory.Delete(full, true);
    }

    private static void CopyArchiveExternalFile(string source, string destination, IList<ArchivePathMapping> mappings)
    {
        source = RequireFile(source, "Archive provenance file");
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, true);
        mappings.Add(new ArchivePathMapping(source, destination));
    }

    private static void CopyArchiveResourceEvidence(string archive, string input, IList<ArchivePathMapping> mappings)
    {
        var evidencePath = Path.Combine(input, "adapter", "resource-evidence.json");
        if (!File.Exists(evidencePath)) return;
        var evidence = ReadJson<JsonElement>(evidencePath);
        var reportReference = GetString(evidence, "resourceBuild");
        if (string.IsNullOrWhiteSpace(reportReference)) reportReference = GetString(evidence, "yooAssetBuild");
        if (string.IsNullOrWhiteSpace(reportReference)) return;
        var reportPath = ResolveEvidencePath(reportReference, Path.GetDirectoryName(evidencePath)!,
            "Structured resource build report");
        var report = ReadJson<JsonElement>(reportPath);
        if (GetString(report, "format") != "hybridclr.dhe-yooasset-build.json" || !GetBool(report, "passed"))
            throw new DheException("YooAsset archive evidence is not a passing structured report.");
        var packageDirectory = GetString(report, "packageDirectory");
        if (string.IsNullOrWhiteSpace(packageDirectory) || !Directory.Exists(packageDirectory))
            throw new DheException("YooAsset package directory was not found for archive evidence.");
        var bundleRoot = Path.Combine(archive, "resource-bundles");
        Directory.CreateDirectory(bundleRoot);
        mappings.Add(new ArchivePathMapping(Path.GetFullPath(packageDirectory), bundleRoot));
        if (report.TryGetProperty("requiredAssets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var fileName = GetString(asset, "bundleFileName");
                if (string.IsNullOrWhiteSpace(fileName) || !IsPortableRelativePath(fileName))
                    throw new DheException("YooAsset archive evidence contains an unsafe bundle file name.");
                var candidates = Directory.GetFiles(packageDirectory, Path.GetFileName(fileName), SearchOption.AllDirectories);
                if (candidates.Length != 1) throw new DheException("YooAsset required bundle is missing or ambiguous: " + fileName);
                var destination = Path.Combine(bundleRoot, fileName.Replace('/', Path.DirectorySeparatorChar));
                CopyArchiveExternalFile(candidates[0], destination, mappings);
            }
        }
        var buildReport = GetString(report, "buildReport");
        if (!string.IsNullOrWhiteSpace(buildReport) && File.Exists(buildReport))
            CopyArchiveExternalFile(buildReport, Path.Combine(archive, "provenance", "yooasset-build-report" + Path.GetExtension(buildReport)), mappings);
    }

    private static void RewriteArchiveJsonDocuments(string archive, string input,
        IEnumerable<ArchivePathMapping> externalMappings)
    {
        var mappings = new List<ArchivePathMapping>
        {
            new(Path.GetFullPath(input), Path.GetFullPath(archive))
        };
        mappings.AddRange(externalMappings);
        mappings = mappings.OrderByDescending(mapping => mapping.Source.Length).ToList();
        foreach (var path in Directory.GetFiles(archive, "*.json", SearchOption.AllDirectories))
        {
            // Already portable, content-addressed bytes are part of the embedded
            // Base identity. Even JSON whitespace must remain unchanged.
            if (GetString(ReadJson<JsonElement>(path), "format") ==
                "hybridclr.dhe-aot-analysis-snapshot.json") continue;
            RewriteArchiveJson(path, mappings);
        }
        var violations = Directory.GetFiles(archive, "*.json", SearchOption.AllDirectories)
            .SelectMany(path => FindAbsoluteJsonStrings(path).Select(value => Path.GetRelativePath(archive, path) + ": " + value))
            .Take(20).ToArray();
        if (violations.Length > 0)
            throw new DheException("Archive JSON still contains absolute paths: " + string.Join("; ", violations));
    }

    private static void BindArchivedNativeIdentity(string archive, string originalNativeManifest)
    {
        var immutablePath = Path.Combine(archive, "provenance", "native-manifest.original.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(immutablePath)!);
        File.Copy(RequireFile(originalNativeManifest, "Original native manifest"), immutablePath, true);
        var identityPath = Path.Combine(archive, "build-identity.json");
        var identity = JsonNode.Parse(File.ReadAllText(identityPath))?.AsObject() ??
            throw new DheException("Archived build identity is invalid.");
        identity["nativeManifestPath"] = Path.GetRelativePath(Path.GetDirectoryName(identityPath)!, immutablePath)
            .Replace(Path.DirectorySeparatorChar, '/');
        File.WriteAllText(identityPath, identity.ToJsonString(Json), new UTF8Encoding(false));

        var normalizedPath = Path.Combine(archive, "native", "dhe-native-manifest.json");
        var normalized = JsonNode.Parse(File.ReadAllText(normalizedPath))?.AsObject() ??
            throw new DheException("Archived native manifest is invalid.");
        normalized["pathSemantics"] = "archive-relative-v1";
        normalized["sourceManifest"] = Path.GetRelativePath(Path.GetDirectoryName(normalizedPath)!, immutablePath)
            .Replace(Path.DirectorySeparatorChar, '/');
        normalized["sourceManifestSha256"] = Sha256File(immutablePath);
        File.WriteAllText(normalizedPath, normalized.ToJsonString(Json), new UTF8Encoding(false));
    }

    private static void RewriteArchiveJson(string path, IReadOnlyList<ArchivePathMapping> mappings)
    {
        var node = JsonNode.Parse(File.ReadAllText(path)) ?? throw new DheException("Invalid archive JSON: " + path);
        RewriteArchiveNode(node, path, null, mappings);
        File.WriteAllText(path, node.ToJsonString(Json), new UTF8Encoding(false));
    }

    private static void RewriteArchiveNode(JsonNode node, string documentPath, string? propertyName,
        IReadOnlyList<ArchivePathMapping> mappings)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToArray())
            {
                if (pair.Value == null) continue;
                if (pair.Key == "pathSemantics" && pair.Value is JsonValue)
                {
                    obj[pair.Key] = "archive-relative-v1";
                    continue;
                }
                if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text) && IsPortableAbsolutePath(text))
                {
                    obj[pair.Key] = ArchiveMappedValue(text, documentPath, pair.Key, mappings);
                    continue;
                }
                RewriteArchiveNode(pair.Value, documentPath, pair.Key, mappings);
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                var child = array[index];
                if (child == null) continue;
                if (child is JsonValue value && value.TryGetValue<string>(out var text) && IsPortableAbsolutePath(text))
                    array[index] = ArchiveMappedValue(text, documentPath, propertyName ?? "item", mappings);
                else RewriteArchiveNode(child, documentPath, propertyName, mappings);
            }
        }
    }

    private static string ArchiveMappedValue(string value, string documentPath, string propertyName,
        IReadOnlyList<ArchivePathMapping> mappings)
    {
        var full = Path.GetFullPath(value);
        foreach (var mapping in mappings)
        {
            var source = mapping.Source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!full.Equals(source, StringComparison.OrdinalIgnoreCase) &&
                !full.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var suffix = full.Equals(source, StringComparison.OrdinalIgnoreCase) ? "" : Path.GetRelativePath(source, full);
            var destination = suffix.Length == 0 ? mapping.Destination : Path.Combine(mapping.Destination, suffix);
            return Path.GetRelativePath(Path.GetDirectoryName(documentPath)!, destination).Replace(Path.DirectorySeparatorChar, '/');
        }
        return "external-provenance/" + SanitizeArchiveName(propertyName);
    }

    private static string SanitizeArchiveName(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value.ToLowerInvariant())
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-');
        return builder.Length == 0 ? "path" : builder.ToString();
    }

    private static IEnumerable<string> FindAbsoluteJsonStrings(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return EnumerateJsonStrings(document.RootElement).Where(IsPortableAbsolutePath).ToArray();
    }

    private static IEnumerable<string> EnumerateJsonStrings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) yield return element.GetString() ?? "";
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) foreach (var value in EnumerateJsonStrings(item)) yield return value;
        else if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject()) foreach (var value in EnumerateJsonStrings(property.Value)) yield return value;
    }

    private static bool IsPortableAbsolutePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return Path.IsPathRooted(value) || value.StartsWith('/') || value.StartsWith("\\\\", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal) || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/';
    }
}
