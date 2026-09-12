using System.Security.Cryptography;

namespace HybridCLR.DheTool;

internal sealed record ResourceExecutionPlan(int SchemaVersion, string AssemblyName,
    string BaseMetaVersionSha256, string CurrentMetaVersionSha256,
    uint[] CurrentStorageTypeTokens, uint[] CurrentExecutionMethodTokens,
    int CurrentStorageTypeTokenCount, int CurrentExecutionMethodTokenCount)
{
    public const string Capability = "current-storage-execution-plan-array-v1";
    public const string GenericContextCapability = "hotfix-generic-context-dispatch-v1";
    public uint[] CurrentGenericContextMethodTokens { get; init; } = Array.Empty<uint>();
    public int CurrentGenericContextMethodTokenCount { get; init; }

    public string CanonicalBinding()
    {
        static bool Hash(string value) => value != null && value.Length == 64 && value.All(Uri.IsHexDigit);
        static void Tokens(uint[] values, uint table, uint minimumRow)
        {
            if (values == null) throw new InvalidDataException("Execution plan selection array is missing.");
            uint previous = 0;
            foreach (uint token in values)
            {
                if ((token >> 24) != table || (token & 0xffffffu) <= minimumRow || token <= previous)
                    throw new InvalidDataException("Execution plan tokens must be valid, unique and sorted.");
                previous = token;
            }
        }
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(AssemblyName) ||
            AssemblyName.IndexOfAny(new[] { '\r', '\n', '|', '/', '\\' }) >= 0 ||
            !Hash(BaseMetaVersionSha256) || !Hash(CurrentMetaVersionSha256))
            throw new InvalidDataException("Execution plan identity is invalid.");
        Tokens(CurrentStorageTypeTokens, 2, 1); Tokens(CurrentExecutionMethodTokens, 6, 0);
        uint[] conditional = CurrentGenericContextMethodTokens ?? Array.Empty<uint>();
        Tokens(conditional, 6, 0);
        if (CurrentGenericContextMethodTokenCount != conditional.Length ||
            conditional.Any(token => !CurrentExecutionMethodTokens.Contains(token)))
            throw new InvalidDataException("Conditional generic methods must be counted execution-plan selections.");
        if (CurrentStorageTypeTokenCount != CurrentStorageTypeTokens.Length ||
            CurrentExecutionMethodTokenCount != CurrentExecutionMethodTokens.Length)
            throw new InvalidDataException("Execution plan token counts do not match its selections.");
        return AssemblyName + "|" + BaseMetaVersionSha256.ToUpperInvariant() + "|" + CurrentMetaVersionSha256.ToUpperInvariant() +
            "|" + string.Join(",", CurrentStorageTypeTokens.Select(token => token.ToString("X8"))) +
            "|" + string.Join(",", CurrentExecutionMethodTokens.Select(token => token.ToString("X8"))) +
            (conditional.Length == 0 ? "" : "|generic=" + string.Join(",", conditional.Select(token => token.ToString("X8"))));
    }
}

internal sealed record ResourceExecutionCompilation(DheValueLayoutImpactResult Impact,
    IReadOnlyDictionary<string, ResourceExecutionPlan> Plans, string[] UnsupportedChanges);

internal static class ResourceExecutionPlanner
{
    public static ResourceExecutionCompilation Compile(IEnumerable<string> baselinePaths,
        IEnumerable<string> currentPaths, IEnumerable<string> ordinaryAotPaths)
    {
        string[] beforeFiles = baselinePaths.ToArray(), afterFiles = currentPaths.ToArray();
        var before = beforeFiles.Select(MetaVersionSnapshot.Create).ToDictionary(snapshot => snapshot.AssemblyName, StringComparer.Ordinal);
        var after = afterFiles.Select(MetaVersionSnapshot.Create).ToDictionary(snapshot => snapshot.AssemblyName, StringComparer.Ordinal);
        var impact = DheValueLayoutImpact.Analyze(beforeFiles, afterFiles, ordinaryAotPaths);
        var errors = new SortedSet<string>(StringComparer.Ordinal);
        var plans = new Dictionary<string, ResourceExecutionPlan>(StringComparer.Ordinal);
        foreach (var field in impact.StaticValueFields)
        {
            if (field.OrdinaryAot) errors.Add("current-storage-ordinary-aot-static-field:" + field.Identity);
            if (field.ThreadStatic) errors.Add("current-storage-thread-static-value-field:" + field.Identity);
            if (field.HasRva) errors.Add("current-storage-rva-static-value-field:" + field.Identity);
        }
        // A Base-native method which embeds or copies the changed value layout
        // needs a verified native ABI bridge, not a hotfix interpreter selection.
        foreach (var layout in impact.Layouts.Where(item => item.RequiresOrdinaryAotBridge))
            errors.Add("current-storage-ordinary-aot-layout:" + layout.TypeIdentity);
        foreach (var method in impact.Methods.Where(item => item.Decision == "native-abi-bridge" && item.ChangedValueTypes.Length != 0))
            errors.Add("current-storage-native-abi:" + method.AssemblyName + ":" + method.MethodIdentity);
        if (impact.ChangedValueTypes.Length == 0) return new(impact, plans, errors.ToArray());
        foreach (var entry in before.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!after.TryGetValue(entry.Key, out var current))
                throw new InvalidDataException("Current is missing Base assembly: " + entry.Key);
            var baseline = entry.Value;
            var baseTypes = baseline.Types.ToDictionary(type => type.StableId, StringComparer.Ordinal);
            var baseMethods = baseline.Methods.ToDictionary(method => method.StableId, StringComparer.Ordinal);
            var physical = impact.Layouts.Where(type => type.AssemblyName == entry.Key && !type.RequiresOrdinaryAotBridge)
                .Select(type => type.CurrentTypeToken).ToHashSet();
            var selectedTypes = current.Types.Where(type => physical.Contains(type.Token) && baseTypes.ContainsKey(type.StableId)).ToArray();
            var selectedOwners = selectedTypes.Select(type => type.StableId).ToHashSet(StringComparer.Ordinal);
            var methodTokens = impact.Methods.Where(method => method.AssemblyName == entry.Key && method.Decision != "native-abi-bridge")
                .Select(method => method.CurrentMethodToken).ToHashSet();
            var selectedMethods = current.Methods.Where(method => baseMethods.ContainsKey(method.StableId) &&
                (methodTokens.Contains(method.Token) || selectedOwners.Contains(method.DeclaringTypeStableId))).ToArray();
            foreach (var type in selectedTypes)
                if (baseTypes[type.StableId].Flags != type.Flags)
                    errors.Add("current-storage-type-kind-change:" + entry.Key + ":" + type.Identity);
            foreach (var method in selectedMethods.Where(method =>
                !((method.Flags & 2u) != 0 && (baseMethods[method.StableId].Flags & 2u) != 0) &&
                (!CanExecute(method.Flags) || !CanExecute(baseMethods[method.StableId].Flags))))
                errors.Add("current-storage-native-member:" + entry.Key + ":" + method.Identity);
            // Added methods have no Base guard. Abstract members have no call frame.
            uint[] executable = selectedMethods.Where(method => CanExecute(method.Flags) && CanExecute(baseMethods[method.StableId].Flags))
                .Select(method => method.Token).Distinct().OrderBy(token => token).ToArray();
            uint[] physicalTokens = selectedTypes.Select(type => type.Token).Distinct().OrderBy(token => token).ToArray();
            var inspected = impact.Methods.Where(method => method.AssemblyName == entry.Key &&
                    method.Decision == "inspect-generic-context" && method.ChangedValueTypes.Length == 0)
                .Select(method => method.CurrentMethodToken).ToHashSet();
            uint[] conditional = selectedMethods.Where(method => executable.Contains(method.Token) &&
                    inspected.Contains(method.Token) && !selectedOwners.Contains(method.DeclaringTypeStableId) &&
                    (method.GenericParameterCount != 0 || method.DeclaringTypeGenericParameterCount != 0) &&
                    method.Version == baseMethods[method.StableId].Version)
                .Select(method => method.Token).OrderBy(token => token).ToArray();
            var plan = new ResourceExecutionPlan(1, entry.Key, Hash(baseline.ToBinary()), Hash(current.ToBinary()),
                physicalTokens, executable, physicalTokens.Length, executable.Length)
            { CurrentGenericContextMethodTokens = conditional, CurrentGenericContextMethodTokenCount = conditional.Length };
            plan.CanonicalBinding(); plans.Add(entry.Key, plan);
        }
        return new(impact, plans, errors.ToArray());
    }
    private static bool CanExecute(uint flags) => (flags & 8u) != 0 && (flags & (2u | 4u)) == 0;
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
