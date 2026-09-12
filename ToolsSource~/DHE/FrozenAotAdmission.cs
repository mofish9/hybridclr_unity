using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;

namespace HybridCLR.DheTool;

internal sealed record FrozenAotAdmissionProof(string SnapshotSha256, int OrdinaryAssemblyCount,
    int ExecutableMethodCount, string[] MissingGuards, string[] DischargedChanges, string[] UnsupportedChanges);

internal static class FrozenAotAdmission
{
    // The caller authenticates this manifest against the immutable build identity.
    // Check every executable ordinary method, including currently unaffected ones:
    // a resource cannot retroactively install a missing guard in its Base Player.
    public static FrozenAotAdmissionProof Validate(AotAnalysisSnapshot snapshot,
        FrozenAotCompilation frozen, ResourceExecutionCompilation execution, JsonElement native)
    {
        var coverage = new HashSet<string>(StringComparer.Ordinal);
        string Text(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : "";
        foreach (string table in new[] { "methods", "interpreterOnlyMethods" })
        {
            if (!native.TryGetProperty(table, out var rows) || rows.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Frozen admission requires the complete native guard manifest.");
            foreach (var row in rows.EnumerateArray())
            {
                bool valid = table == "methods" ? Text(row, "bridgeKind") == "invoke-args-v1" :
                    row.TryGetProperty("reasons", out var reasons) && reasons.ValueKind == JsonValueKind.Array &&
                    reasons.GetArrayLength() == 1 && reasons[0].GetString() == "no generated native entry";
                if (valid && row.TryGetProperty("methodToken", out var token) && token.TryGetUInt32(out uint value))
                    coverage.Add(Key(Text(row, "assemblyName"), value, Text(row, "stableMethodIdSha256"), Text(row, "managedId")));
            }
        }
        var errors = new SortedSet<string>(execution.UnsupportedChanges, StringComparer.Ordinal);
        var discharged = new SortedSet<string>(StringComparer.Ordinal);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var selections = frozen.Assemblies.ToDictionary(plan => plan.AssemblyName, StringComparer.Ordinal);
        int methodCount = 0, assemblyCount = 0;
        foreach (var source in snapshot.Assemblies.Where(source => !source.Dhe))
        {
            assemblyCount++;
            using var module = ModuleDefMD.Load(source.ReadVerifiedBytes());
            var types = module.GetTypes().ToDictionary(type => type.MDToken.Raw);
            var methods = types.Values.SelectMany(type => type.Methods).ToDictionary(method => method.MDToken.Raw);
            bool Covered(MethodDef method) => coverage.Contains(Key(source.AssemblyName, method.MDToken.Raw,
                Digest("dhe-method-id\n" + MetaVersionSnapshot.MethodIdentity(method)), MetaVersionSnapshot.MethodIdentity(method)));
            foreach (var method in methods.Values.Where(method => method.HasBody && !method.IsAbstract && !method.IsPinvokeImpl &&
                !source.ExcludedTypeTokens.Contains(method.DeclaringType.MDToken.Raw)))
            {
                methodCount++;
                if (!Covered(method)) missing.Add(source.AssemblyName + ":" + method.MDToken.Raw + ":" + MetaVersionSnapshot.MethodIdentity(method));
            }
            if (!selections.TryGetValue(source.AssemblyName, out var plan)) continue;
            if (plan.SnapshotSha256 != snapshot.Sha256 || plan.SourceDllSha256 != source.Sha256 ||
                !plan.ExcludedBaseTypeTokens.SequenceEqual(source.ExcludedTypeTokens))
                throw new InvalidDataException("Frozen admission source identity differs from the Base: " + source.AssemblyName);
            plan.ExecutionPlan.CanonicalBinding();
            bool Selected(MethodDef method) => method.IsAbstract || method.HasBody && method.IsIL && !method.IsUnmanaged &&
                !method.IsPinvokeImpl && !method.IsInternalCall && !source.ExcludedTypeTokens.Contains(method.DeclaringType.MDToken.Raw) &&
                plan.ExecutionPlan.CurrentExecutionMethodTokens.Contains(method.MDToken.Raw) && Covered(method);
            // Current storage needs both its original initializer and every
            // concrete affected native accessor. Keeping only a field record
            // would still let old AOT code read/write the smaller Base slot.
            bool allAffectedMethodsSelected = execution.Impact.Methods.Where(method => method.AssemblyName == source.AssemblyName &&
                method.ChangedValueTypes.Length != 0).All(impact => methods.TryGetValue(impact.CurrentMethodToken, out var method) &&
                    MetaVersionSnapshot.MethodIdentity(method) == impact.MethodIdentity && Selected(method));
            foreach (var field in execution.Impact.StaticValueFields.Where(field => field.OrdinaryAot && field.AssemblyName == source.AssemblyName))
                if (!field.ThreadStatic && !field.HasRva && allAffectedMethodsSelected &&
                    types.TryGetValue(field.DeclaringTypeToken, out var owner) && !source.ExcludedTypeTokens.Contains(field.DeclaringTypeToken) &&
                    owner.Fields.Any(definition => definition.MDToken.Raw == field.FieldToken && definition.IsStatic && !definition.IsLiteral && !definition.HasFieldRVA) &&
                    plan.StaticValueFieldTokens.Contains(field.FieldToken) && owner.Methods.Where(method => method.IsStaticConstructor).All(Selected))
                    discharged.Add("current-storage-ordinary-aot-static-field:" + field.Identity);
            foreach (var impact in execution.Impact.Methods.Where(method => method.AssemblyName == source.AssemblyName &&
                method.Decision == "native-abi-bridge" && method.ChangedValueTypes.Length != 0))
                if (methods.TryGetValue(impact.CurrentMethodToken, out var method) && !method.IsAbstract &&
                    MetaVersionSnapshot.MethodIdentity(method) == impact.MethodIdentity && Selected(method))
                    discharged.Add("current-storage-native-abi:" + source.AssemblyName + ":" + impact.MethodIdentity);
            foreach (var layout in execution.Impact.Layouts.Where(type => type.AssemblyName == source.AssemblyName && type.RequiresOrdinaryAotBridge))
                if (types.TryGetValue(layout.CurrentTypeToken, out var type) && !source.ExcludedTypeTokens.Contains(layout.CurrentTypeToken) &&
                    (layout.TypeIdentity != layout.DefinitionIdentity || plan.ExecutionPlan.CurrentStorageTypeTokens.Contains(layout.CurrentTypeToken)) &&
                    type.Methods.All(Selected))
                    discharged.Add("current-storage-ordinary-aot-layout:" + layout.TypeIdentity);
        }
        foreach (var obligation in frozen.Obligations)
        {
            // Generic definitions with no concrete changed-value dependency are
            // resolved by the runtime's closed-context dispatch. They are not
            // blanket source replacement requests. All their Base guards above
            // are still mandatory, and native-only concrete obligations remain.
            if (obligation.Kind == "generic-context-analysis" || obligation.Kind == "base-native-guard-required") continue;
            errors.Add("frozen-aot-" + obligation.Kind + ":" + obligation.AssemblyName + ":" + obligation.Identity);
        }
        if (missing.Count == 0) errors.ExceptWith(discharged);
        else
        {
            discharged.Clear();
            foreach (string key in missing) errors.Add("frozen-aot-missing-base-guard:" + key);
        }
        return new(snapshot.Sha256, assemblyCount, methodCount, missing.ToArray(), discharged.ToArray(), errors.ToArray());
    }

    private static string Key(string assembly, uint token, string stableId, string identity) =>
        assembly + ":" + token + ":" + stableId.ToUpperInvariant() + ":" + identity;
    private static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
