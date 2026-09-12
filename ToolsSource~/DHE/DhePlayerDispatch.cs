using System.Text.Json;

namespace HybridCLR.DheTool;

internal static class DhePlayerDispatch
{
    internal sealed record AssemblyPair(MetaVersionSnapshot? Baseline, MetaVersionSnapshot Current);

    // A retained entry may execute interpreted callees. Its own MV must still
    // agree with its native routing flag; an aggregate counter alone is insufficient.
    internal static void Validate(JsonElement player, IEnumerable<AssemblyPair> assemblies)
    {
        if (player.TryGetProperty("changedProbeChanged", out var changedProbe) &&
            changedProbe.ValueKind == JsonValueKind.True)
            return;
        if (!player.TryGetProperty("structuralEntryDispatch", out var entry) ||
            entry.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Changed Player has no MV-bound mixed-call receipt.");

        string identity = entry.GetProperty("methodIdentity").GetString() ?? string.Empty;
        string stableId = entry.GetProperty("methodStableId").GetString() ?? string.Empty;
        var matches = assemblies.SelectMany(pair => pair.Current.Methods
            .Where(method => string.Equals(method.StableId, stableId, StringComparison.OrdinalIgnoreCase))
            .Select(method => (Pair: pair, Method: method))).ToArray();
        if (matches.Length != 1 || matches[0].Method.Identity != identity)
            throw new InvalidDataException("Mixed-call method identity is absent or ambiguous in Current.");
        var match = matches[0];
        var baselineMethod = match.Pair.Baseline?.Methods.SingleOrDefault(method =>
            string.Equals(method.StableId, stableId, StringComparison.OrdinalIgnoreCase));
        bool present = baselineMethod != null;
        bool changed = !present || !string.Equals(baselineMethod!.Version,
            match.Method.Version, StringComparison.OrdinalIgnoreCase);
        int entries = entry.GetProperty("interpreterEntries").GetInt32();
        if (entry.GetProperty("presentInBase").GetBoolean() != present ||
            entry.GetProperty("expectedChanged").GetBoolean() != changed ||
            entry.GetProperty("nativeChanged").GetBoolean() != (present && changed) ||
            !entry.GetProperty("executed").GetBoolean() || entries <= 0 ||
            entries > player.GetProperty("interpreterEntryCount").GetInt32())
            throw new InvalidDataException("Mixed-call routing or execution disagrees with Base/Current MV.");
    }
}
