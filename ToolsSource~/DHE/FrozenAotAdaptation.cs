using System.Security.Cryptography;
using dnlib.DotNet;

namespace HybridCLR.DheTool;

// Compiler selections remain separate from resource admission. Guards and native
// ABI obligations must be proved by the Base build before these selections can
// become a loadable resource execution plan.
internal sealed record FrozenAotMethod(uint Token, string Identity, string[] Reasons);
internal sealed record FrozenAotObligation(string Kind, string AssemblyName, uint Token, string Identity);
internal sealed record FrozenAotAssemblyPlan(string AssemblyName, string SnapshotSha256,
    string SourceDllSha256, string SourceMetaVersionSha256, uint[] ExcludedBaseTypeTokens,
    uint[] StaticValueFieldTokens, ResourceExecutionPlan ExecutionPlan, FrozenAotMethod[] Methods)
{
    public uint[] GenericContextMethodTokens { get; init; } = Array.Empty<uint>();
}
internal sealed record FrozenAotCompilation(FrozenAotAssemblyPlan[] Assemblies, FrozenAotObligation[] Obligations,
    DheValueLayoutImpactResult Impact);

internal static class FrozenAotAdaptation
{
    public static FrozenAotCompilation Compile(AotAnalysisSnapshot snapshot,
        IEnumerable<string> baselinePaths, IEnumerable<string> currentPaths, DheValueLayoutImpactResult? verifiedImpact = null)
    {
        // Reauthenticate the binding and full inventory, even if the caller
        // retained a snapshot object while files changed on disk.
        if (!Hash(File.ReadAllBytes(snapshot.ManifestPath)).Equals(snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Frozen AOT snapshot manifest changed after validation.");
        foreach (var source in snapshot.Assemblies) source.ReadVerifiedBytes();
        string[] before = baselinePaths.ToArray(), current = currentPaths.ToArray();
        var baselines = before.Select(MetaVersionSnapshot.Create).ToArray();
        var baselineNames = baselines.Select(mv => mv.AssemblyName).ToArray();
        if (baselineNames.Length != baselineNames.Distinct(StringComparer.Ordinal).Count() ||
            !baselineNames.ToHashSet(StringComparer.Ordinal).SetEquals(snapshot.Assemblies.Where(source => source.Dhe).Select(source => source.AssemblyName)))
            throw new InvalidDataException("Frozen AOT planning requires the Base's complete original hotfix set.");
        for (int index = 0; index < baselines.Length; ++index)
        {
            var baseline = baselines[index];
            var captured = snapshot.Assemblies.Single(source => source.Dhe && source.AssemblyName == baseline.AssemblyName);
            if (!baseline.AssemblySha256.Equals(captured.Sha256, StringComparison.OrdinalIgnoreCase) &&
                !DheAotBaselineIdentity.Matches(File.ReadAllBytes(before[index]), captured.ReadVerifiedBytes()))
                throw new InvalidDataException("Hotfix baseline is not the captured Base source: " + baseline.AssemblyName);
        }
        var impact = verifiedImpact ?? DheValueLayoutImpact.Analyze(before, current, snapshot.OrdinaryAssemblyPaths);
        bool parentChanged = FrozenFieldValidation.HasParentChange(baselines, current.Select(MetaVersionSnapshot.Create));
        if (parentChanged && !snapshot.Assemblies.Any(source => !source.Dhe && source.AssemblyName == "mscorlib"))
            throw new InvalidDataException("Parent evolution requires authenticated Base mscorlib field accessors.");
        var plans = new List<FrozenAotAssemblyPlan>();
        var obligations = new List<FrozenAotObligation>();
        foreach (var source in snapshot.Assemblies.Where(source => !source.Dhe))
        {
            using var module = ModuleDefMD.Load(source.ReadVerifiedBytes());
            var types = module.GetTypes().ToDictionary(type => type.MDToken.Raw);
            var methods = module.GetTypes().SelectMany(type => type.Methods).ToDictionary(method => method.MDToken.Raw);
            var ownerLayouts = impact.Layouts.Where(type => type.RequiresOrdinaryAotBridge && type.AssemblyName == source.AssemblyName).ToArray();
            // A changed generic argument requires a new closed instantiation,
            // not a replacement for the frozen generic definition. Keeping that
            // definition also preserves runtime identities such as Nullable<T>.
            // An owner with a concrete affected field has an open-definition
            // impact of its own and still needs physical Current storage.
            var physical = ownerLayouts.Where(type => type.TypeIdentity == type.DefinitionIdentity)
                .Select(type => type.CurrentTypeToken).ToHashSet();
            var statics = impact.StaticValueFields.Where(field => field.OrdinaryAot && field.AssemblyName == source.AssemblyName).ToArray();
            var affected = impact.Methods.Where(method => method.AssemblyName == source.AssemblyName).ToArray();
            var selected = new Dictionary<uint, HashSet<string>>();
            bool Excluded(uint owner) => source.ExcludedTypeTokens.Contains(owner);
            void Obligation(string kind, uint token, string identity) => obligations.Add(new(kind, source.AssemblyName, token, identity));
            void Select(MethodDef method, string reason)
            {
                if (Excluded(method.DeclaringType.MDToken.Raw))
                {
                    Obligation("excluded-base-identity", method.MDToken.Raw, MetaVersionSnapshot.MethodIdentity(method));
                    return;
                }
                if (method.IsAbstract) return;
                if (!method.HasBody || !method.IsIL || method.IsUnmanaged || method.IsPinvokeImpl || method.IsInternalCall)
                {
                    Obligation("native-only-abi", method.MDToken.Raw, MetaVersionSnapshot.MethodIdentity(method));
                    return;
                }
                if (!selected.TryGetValue(method.MDToken.Raw, out var reasons))
                    selected.Add(method.MDToken.Raw, reasons = new(StringComparer.Ordinal));
                reasons.Add(reason);
            }
            if (parentChanged && source.AssemblyName == "mscorlib")
                foreach (var method in FrozenFieldValidation.Select(module)) Select(method, FrozenFieldValidation.Reason);
            foreach (var method in affected)
            {
                if (method.ChangedValueTypes.Length != 0) Select(methods[method.CurrentMethodToken], "changed-value-dependency");
                else if (!Excluded(methods[method.CurrentMethodToken].DeclaringType.MDToken.Raw))
                    Obligation("generic-context-analysis", method.CurrentMethodToken, method.MethodIdentity);
            }
            foreach (uint token in ownerLayouts.Select(type => type.CurrentTypeToken).Distinct())
            {
                if (Excluded(token))
                {
                    Obligation("excluded-base-identity", token, types[token].FullName);
                    physical.Remove(token);
                    continue;
                }
                foreach (MethodDef method in types[token].Methods)
                    Select(method, physical.Contains(token) ? "physical-owner" : "generic-argument-storage");
            }
            foreach (var field in statics)
            {
                if (Excluded(field.DeclaringTypeToken)) Obligation("excluded-base-identity", field.FieldToken, field.Identity);
                if (field.ThreadStatic) Obligation("thread-static-storage", field.FieldToken, field.Identity);
                if (field.HasRva) Obligation("rva-storage", field.FieldToken, field.Identity);
                foreach (var cctor in types[field.DeclaringTypeToken].Methods.Where(method => method.IsStaticConstructor))
                    Select(cctor, "static-storage-initializer");
            }
            if (physical.Count == 0 && selected.Count == 0 && statics.Length == 0) continue;
            var mv = MetaVersionSnapshot.Create(source.Path);
            source.ReadVerifiedBytes();
            if (!mv.AssemblySha256.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Frozen AOT MV does not match its Base DLL: " + source.AssemblyName);
            string mvHash = Hash(mv.ToBinary());
            uint[] typeTokens = physical.OrderBy(token => token).ToArray();
            uint[] methodTokens = selected.Keys.OrderBy(token => token).ToArray();
            var plan = new ResourceExecutionPlan(1, source.AssemblyName, mvHash, mvHash,
                typeTokens, methodTokens, typeTokens.Length, methodTokens.Length);
            plan.CanonicalBinding();
            var entries = methodTokens.Select(token => new FrozenAotMethod(token, MetaVersionSnapshot.MethodIdentity(methods[token]),
                selected[token].OrderBy(reason => reason, StringComparer.Ordinal).ToArray())).ToArray();
            foreach (var method in entries) Obligation("base-native-guard-required", method.Token, method.Identity);
            plans.Add(new(source.AssemblyName, snapshot.Sha256, source.Sha256, mvHash,
                source.ExcludedTypeTokens.ToArray(), statics.Where(field => !Excluded(field.DeclaringTypeToken))
                    .Select(field => field.FieldToken).Distinct().OrderBy(token => token).ToArray(), plan, entries)
            {
                GenericContextMethodTokens = entries.Where(method => method.Reasons.Length == 1 &&
                    method.Reasons[0] == "generic-argument-storage").Select(method => method.Token).OrderBy(token => token).ToArray()
            });
        }
        return new(plans.OrderBy(plan => plan.AssemblyName, StringComparer.Ordinal).ToArray(),
            obligations.Distinct().OrderBy(item => item.AssemblyName, StringComparer.Ordinal).ThenBy(item => item.Kind, StringComparer.Ordinal)
                .ThenBy(item => item.Token).ToArray(), impact);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
