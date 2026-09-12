using System.Text.Json;

namespace HybridCLR.DheTool;

internal static partial class Program
{
    internal static string[] ReadBaseAotMetadataNames(JsonElement identity, string[] legacyProjectNames)
    {
        string[] names;
        if (identity.TryGetProperty("aotMetadataAssemblyNames", out JsonElement declared))
        {
            if (declared.ValueKind != JsonValueKind.Array || declared.EnumerateArray().Any(row => row.ValueKind != JsonValueKind.String))
                throw new DheException("Base AOT metadata names must be an array of assembly names.");
            names = declared.EnumerateArray().Select(row => row.GetString()!).ToArray();
            if (names.Any(name => string.IsNullOrWhiteSpace(name) || name != NormalizeName(name) || Path.GetFileName(name) != name) ||
                names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
                throw new DheException("Base AOT metadata names contain unsafe or duplicate entries.");
            if (identity.TryGetProperty("aotAssemblyNames", out JsonElement inventory))
            {
                var available = inventory.EnumerateArray().Select(row => row.GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (names.Any(name => !available.Contains(name))) throw new DheException("Base metadata is outside its AOT inventory.");
            }
        }
        else if (string.Equals(GetString(identity, "aotMetadataSetId"),
                     NamedByteSetHash(Array.Empty<(string name, byte[] bytes)>()), StringComparison.OrdinalIgnoreCase))
            names = Array.Empty<string>();
        else
            names = legacyProjectNames;
        // The caller still hashes the exact selected bytes and compares the set
        // ID with the immutable Base identity; names never relax that binding.
        return names.OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }
}
