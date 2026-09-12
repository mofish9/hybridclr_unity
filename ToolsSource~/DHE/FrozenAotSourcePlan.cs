using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HybridCLR.DheTool;

internal static class FrozenAotSourcePlan
{
    public static JsonElement[] Materialize(AotAnalysisSnapshot snapshot, FrozenAotCompilation compilation,
        string baseId, string outputRoot)
    {
        string root = Path.Combine(outputRoot, "payload/frozen-aot", baseId.ToLowerInvariant());
        var rows = new List<JsonElement>();
        foreach (var plan in compilation.Assemblies)
        {
            var source = snapshot.Assemblies.Single(source => source.AssemblyName == plan.AssemblyName);
            source.ReadVerifiedBytes();
            byte[] mv = MetaVersionSnapshot.Create(source.Path).ToBinary();
            if (!SameHash(Convert.ToHexString(SHA256.HashData(mv)), plan.SourceMetaVersionSha256))
                throw new InvalidDataException("Frozen source MV changed during materialization: " + source.AssemblyName);
            Directory.CreateDirectory(root);
            string mvFile = Path.Combine(root, source.AssemblyName + ".mv.bytes");
            File.WriteAllBytes(mvFile, mv);
            rows.Add(JsonSerializer.SerializeToElement(new { assemblyName = source.AssemblyName,
                sourceFile = source.Path, sourceSha256 = source.Sha256, baseMetaVersionFile = mvFile,
                baseMetaVersionSha256 = plan.SourceMetaVersionSha256,
                currentStorageTypeTokens = plan.ExecutionPlan.CurrentStorageTypeTokens,
                currentExecutionMethodTokens = plan.ExecutionPlan.CurrentExecutionMethodTokens,
                excludedBaseTypeTokens = plan.ExcludedBaseTypeTokens,
                genericContextMethodTokens = plan.GenericContextMethodTokens, sourceKind = "frozen-base-aot" }));
        }
        return rows.ToArray();
    }

    // Same named-byte-set encoding as the resource manifest. The input is the
    // complete selected Current payload, including newly introduced assemblies.
    public static string CurrentSetHash(IEnumerable<string> paths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var records = paths.Select(path => (Name: Path.GetFileNameWithoutExtension(path), Path: path)).ToArray();
        if (records.Select(record => record.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != records.Length)
            throw new InvalidDataException("Frozen source planning received duplicate Current assemblies.");
        foreach (var record in records.OrderBy(record => record.Name, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(record.Name + "\n"));
            hash.AppendData(File.ReadAllBytes(record.Path));
            hash.AppendData(new byte[] { (byte)'\n' });
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static JsonElement[] Validate(JsonElement plan, string baseId, AotAnalysisSnapshot snapshot,
        IEnumerable<string> baselines, IEnumerable<string> currentPaths, string expectedCurrentSetHash)
    {
        string[] current = currentPaths.ToArray();
        if (!SameHash(CurrentSetHash(current), expectedCurrentSetHash))
            throw new InvalidDataException("Current assemblies changed before frozen source validation.");
        var expected = FrozenAotAdaptation.Compile(snapshot, baselines, current);
        if (!SameHash(CurrentSetHash(current), expectedCurrentSetHash))
            throw new InvalidDataException("Current assemblies changed during frozen source validation.");
        return ValidateCompiled(plan, baseId, snapshot.Sha256, expectedCurrentSetHash, expected);
    }

    internal static JsonElement[] ValidateCompiled(JsonElement plan, string baseId, string snapshotHash,
        string currentSetHash, FrozenAotCompilation expected)
    {
        string Text(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : string.Empty;
        if (plan.ValueKind != JsonValueKind.Object ||
            !plan.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int schema) || schema != 1 ||
            Text(plan, "format") != "hybridclr.dhe-frozen-aot-source-plan.json" ||
            !SameHash(Text(plan, "baseId"), baseId) ||
            !SameHash(Text(plan, "aotAnalysisSnapshotSha256"), snapshotHash) ||
            !SameHash(Text(plan, "currentAssemblySetSha256"), currentSetHash) ||
            !plan.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array ||
            !plan.TryGetProperty("sourceCount", out var count) || count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out int sourceCount) ||
            sourceCount != sources.GetArrayLength() || sourceCount != expected.Assemblies.Length)
            throw new InvalidDataException("Frozen AOT source plan is not bound to the selected Base and Current payload: " + baseId);
        var byName = expected.Assemblies.ToDictionary(source => source.AssemblyName, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Frozen AOT source must be an object.");
            string name = Text(source, "assemblyName");
            if (!seen.Add(name) || !byName.TryGetValue(name, out var compiled) ||
                Text(source, "sourceKind") != "frozen-base-aot" ||
                !SameHash(Text(source, "sourceSha256"), compiled.SourceDllSha256) ||
                !SameHash(Text(source, "baseMetaVersionSha256"), compiled.SourceMetaVersionSha256))
                throw new InvalidDataException("Frozen AOT source does not match the recompiled Base source: " + name);
            void Tokens(string key, uint[] tokens)
            {
                if (!source.TryGetProperty(key, out var values) || values.ValueKind != JsonValueKind.Array ||
                    values.GetArrayLength() != tokens.Length ||
                    !values.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out uint token)
                        ? token : uint.MaxValue).SequenceEqual(tokens))
                    throw new InvalidDataException("Frozen AOT selection differs from the actual Current payload: " + name + "/" + key);
            }
            Tokens("currentStorageTypeTokens", compiled.ExecutionPlan.CurrentStorageTypeTokens);
            Tokens("currentExecutionMethodTokens", compiled.ExecutionPlan.CurrentExecutionMethodTokens);
            Tokens("excludedBaseTypeTokens", compiled.ExcludedBaseTypeTokens);
            Tokens("genericContextMethodTokens", compiled.GenericContextMethodTokens);
            foreach (var binding in new[] { ("sourceFile", compiled.SourceDllSha256), ("baseMetaVersionFile", compiled.SourceMetaVersionSha256) })
            {
                string file = Text(source, binding.Item1);
                if (!File.Exists(file) || !SameHash(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), binding.Item2))
                    throw new InvalidDataException("Frozen AOT input bytes do not match the captured Base: " + name + "/" + binding.Item1);
            }
        }
        return sources.EnumerateArray().ToArray();
    }

    private static bool SameHash(string actual, string expected) => actual.Length == 64 && expected.Length == 64 &&
        actual.All(Uri.IsHexDigit) && expected.All(Uri.IsHexDigit) && actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
}
