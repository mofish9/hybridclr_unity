#nullable enable
using System.Security.Cryptography;
using System.Text.Json;
using dnlib.DotNet;

namespace HybridCLR.DheTool;

// Reads the immutable package capture. Final-build semantic normalization is
// verified by the package before a Base is archived; never infer that proof
// from a directory supplied separately by a resource build caller.
internal sealed record AotAnalysisAssembly(string AssemblyName, string Path, string Sha256,
    bool Dhe, uint[] ExcludedTypeTokens)
{
    public byte[] ReadVerifiedBytes()
    {
        byte[] bytes = File.ReadAllBytes(Path);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Frozen AOT source changed after snapshot validation: " + AssemblyName);
        return bytes;
    }
}

internal sealed record AotAnalysisSnapshot(string ManifestPath, string Sha256, AotAnalysisAssembly[] Assemblies)
{
    public string[] OrdinaryAssemblyPaths => Assemblies.Where(source => !source.Dhe).Select(source => source.Path).ToArray();
    public static AotAnalysisSnapshot? Read(string identityPath, JsonElement identity,
        IEnumerable<string> expectedAotNames, IEnumerable<string> expectedDheNames)
    {
        string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var item) &&
            item.ValueKind == JsonValueKind.String ? item.GetString() : null;
        bool Hash(string? value) => value != null && value.Length == 64 && value.All(Uri.IsHexDigit);
        string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        string? hash = Text(identity, "aotAnalysisSnapshotSha256"), relative = Text(identity, "aotAnalysisSnapshot");
        // Historical Bases can still receive updates which do not require a
        // layout plan. A partially present or malformed binding is never absent.
        if (!identity.TryGetProperty("aotAnalysisSnapshotSha256", out _) &&
            !identity.TryGetProperty("aotAnalysisSnapshot", out _)) return null;
        if (!Hash(hash) || relative != "aot-analysis/" + hash!.ToLowerInvariant() + "/manifest.json")
            throw new InvalidDataException("Base AOT analysis snapshot path/hash binding is invalid.");
        string manifestPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(identityPath))!,
            relative.Replace('/', Path.DirectorySeparatorChar));
        byte[] bytes = File.ReadAllBytes(manifestPath);
        if (!Digest(bytes).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Base AOT analysis manifest hash mismatch.");
        using var document = JsonDocument.Parse(bytes);
        var manifest = document.RootElement;
        if (!manifest.TryGetProperty("schemaVersion", out var version) || version.GetInt32() != 1 ||
            Text(manifest, "format") != "hybridclr.dhe-aot-analysis-snapshot.json" ||
            Text(manifest, "normalization") != "dhe-aot-analysis-normalization-v1" ||
            string.IsNullOrWhiteSpace(Text(manifest, "identityType")) ||
            string.IsNullOrWhiteSpace(Text(manifest, "identityAssembly")) ||
            !manifest.TryGetProperty("assemblies", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Base AOT analysis manifest format is invalid.");
        string root = Path.GetDirectoryName(manifestPath)!;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dhe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<AotAnalysisAssembly>();
        int identityOwners = 0;
        foreach (JsonElement row in rows.EnumerateArray())
        {
            string? name = Text(row, "assemblyName"), file = Text(row, "file");
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == ".." ||
                name.IndexOfAny(new[] { '/', '\\', ':', '\r', '\n' }) >= 0 ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !names.Add(name) ||
                file != "assemblies/" + name + ".dll" || !Hash(Text(row, "sha256")) ||
                !Hash(Text(row, "normalizedSha256")) || !row.TryGetProperty("dhe", out var isDhe) ||
                (isDhe.ValueKind != JsonValueKind.True && isDhe.ValueKind != JsonValueKind.False))
                throw new InvalidDataException("Base AOT analysis assembly record is invalid.");
            string path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            byte[] dll = File.ReadAllBytes(path);
            if (!Digest(dll).Equals(Text(row, "sha256"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Base AOT analysis DLL hash mismatch: " + name);
            using var module = ModuleDefMD.Load(dll);
            if (module.Assembly?.Name.String != name || !module.IsILOnly)
                throw new InvalidDataException("Base AOT analysis DLL identity is invalid: " + name);
            bool owner = module.GetTypes().Any(type => type.FullName == Text(manifest, "identityType"));
            if (owner != (name == Text(manifest, "identityAssembly")) || owner && isDhe.GetBoolean())
                throw new InvalidDataException("Base AOT analysis generated identity owner is invalid.");
            if (owner) identityOwners++;
            if (isDhe.GetBoolean()) dhe.Add(name);
            string identityType = Text(manifest, "identityType")!;
            uint[] excluded = owner ? module.GetTypes().Where(type => type.FullName == identityType ||
                type.FullName.StartsWith(identityType + "/", StringComparison.Ordinal))
                .Select(type => type.MDToken.Raw).OrderBy(token => token).ToArray() : Array.Empty<uint>();
            sources.Add(new(name, path, Text(row, "sha256")!.ToUpperInvariant(), isDhe.GetBoolean(), excluded));
        }
        string[] actualFiles = Directory.GetFiles(Path.Combine(root, "assemblies"), "*", SearchOption.AllDirectories);
        if (identityOwners != 1 || names.Count == 0 || !names.SetEquals(expectedAotNames) ||
            !dhe.SetEquals(expectedDheNames) || actualFiles.Length != names.Count ||
            !names.SetEquals(actualFiles.Select(path => Path.GetFileNameWithoutExtension(path))))
            throw new InvalidDataException("Base AOT analysis inventory/classification mismatch.");
        return new(manifestPath, hash!.ToLowerInvariant(), sources.OrderBy(source => source.AssemblyName, StringComparer.Ordinal).ToArray());
    }
}
