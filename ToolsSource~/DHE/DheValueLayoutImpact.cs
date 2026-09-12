using dnlib.DotNet;

namespace HybridCLR.DheTool;

// Planning only: an impact result does not enable value-layout loading. The
// runtime/storage implementation must satisfy these obligations before release.
public sealed record DheValueLayoutMethodImpact(string AssemblyName, string MethodIdentity,
    string Decision, string[] ChangedValueTypes)
{
    public uint CurrentMethodToken { get; init; }
}
public sealed record DheValueLayoutTypeImpact(string TypeIdentity, bool RequiresOrdinaryAotBridge,
    string[] ChangedValueTypes)
{
    public string AssemblyName { get; init; } = "";
    public string DefinitionIdentity { get; init; } = "";
    public uint CurrentTypeToken { get; init; }
}
public sealed record DheValueLayoutImpactResult(string[] ChangedValueTypes,
    DheValueLayoutTypeImpact[] Layouts, DheValueLayoutMethodImpact[] Methods)
{
    public string[] ChangedStaticValueFields { get; init; } = Array.Empty<string>();
    public DheStaticValueFieldImpact[] StaticValueFields { get; init; } = Array.Empty<DheStaticValueFieldImpact>();
}

public sealed record DheStaticValueFieldImpact(string Identity, string AssemblyName,
    bool OrdinaryAot, bool ThreadStatic, bool HasRva)
{
    public uint FieldToken { get; init; }
    public uint DeclaringTypeToken { get; init; }
}

public static class DheValueLayoutImpact
{
    public static DheValueLayoutImpactResult Analyze(IEnumerable<string> baselinePaths,
        IEnumerable<string> currentPaths, IEnumerable<string>? ordinaryAotPaths = null)
    {
        var baseline = baselinePaths.Select(MetaVersionSnapshot.Create).ToDictionary(
            value => value.AssemblyName, StringComparer.Ordinal);
        using var analysis = new Analysis(currentPaths, ordinaryAotPaths ?? Array.Empty<string>());
        return analysis.Run(baseline);
    }

    private sealed record Context(Bound[] Types, Bound[] Methods)
    {
        public static readonly Context Empty = new(Array.Empty<Bound>(), Array.Empty<Bound>());
    }
    private sealed record Bound(TypeSig Signature, Context Context);
    private sealed record Use(TypeDef Definition, Context Context, string Key);

    private sealed class Analysis : IDisposable
    {
        private readonly Dictionary<string, ModuleDefMD> modules = new(StringComparer.Ordinal);
        private readonly HashSet<string> hotfixAssemblies = new(StringComparer.Ordinal);
        private readonly HashSet<string> changed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> fieldTypeDependencies = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> layouts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> genericLayouts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TypeDef> layoutDefinitions = new(StringComparer.Ordinal);
        private readonly HashSet<string> visiting = new(StringComparer.Ordinal);

        public Analysis(IEnumerable<string> paths, IEnumerable<string> ordinaryAotPaths)
        {
            try
            {
                foreach (var input in paths.Select(path => (Path: path, Hotfix: true))
                    .Concat(ordinaryAotPaths.Select(path => (Path: path, Hotfix: false))))
                {
                    var module = ModuleDefMD.Load(input.Path);
                    string name = module.Assembly?.Name.String ?? throw new InvalidDataException("Missing assembly.");
                    if (!modules.TryAdd(name, module))
                    {
                        module.Dispose();
                        throw new InvalidDataException("Duplicate current assembly: " + name);
                    }
                    if (input.Hotfix) hotfixAssemblies.Add(name);
                }
            }
            catch { Dispose(); throw; }
        }

        public DheValueLayoutImpactResult Run(Dictionary<string, MetaVersionSnapshot> baseline)
        {
            if (baseline.Keys.Any(name => !hotfixAssemblies.Contains(name)))
                throw new InvalidDataException("Layout analysis requires Current input for every Base hotfix assembly.");
            foreach (var entry in modules)
            {
                if (!baseline.TryGetValue(entry.Key, out var before)) continue;
                var after = MetaVersionSnapshot.Create(entry.Value.Location).Types.ToDictionary(type => type.StableId);
                // Storage includes the physical types of reference fields, even
                // when their pointer width remains unchanged. Inline below still
                // traverses only embedded values; reference cycles use a fixed point.
                foreach (var type in before.Types.Where(type => !type.IsInterface))
                    if (after.TryGetValue(type.StableId, out var next) &&
                        !string.Equals(type.LayoutVersion, next.LayoutVersion, StringComparison.Ordinal))
                        changed.Add(entry.Key + "|" + type.Identity);
            }
            if (changed.Count == 0)
                return new(Array.Empty<string>(), Array.Empty<DheValueLayoutTypeImpact>(),
                    Array.Empty<DheValueLayoutMethodImpact>());

            ExpandExistingFieldTypeDependencies(baseline);

            var methods = new List<DheValueLayoutMethodImpact>();
            var staticFields = new SortedSet<string>(StringComparer.Ordinal);
            var staticDetails = new List<DheStaticValueFieldImpact>();
            foreach (var entry in modules.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                bool ordinary = !hotfixAssemblies.Contains(entry.Key);
                if (!baseline.TryGetValue(entry.Key, out var before) && !ordinary) continue;
                // Ordinary AOT input is the frozen Base code, never a hotfix MV.
                // Its own retained definitions are the reference inventory.
                var oldMethods = (ordinary
                    ? entry.Value.GetTypes().SelectMany(type => type.Methods).Select(MetaVersionSnapshot.MethodIdentity)
                    : before!.Methods.Select(method => method.Identity)).ToHashSet(StringComparer.Ordinal);
                var oldFields = (ordinary
                    ? entry.Value.GetTypes().SelectMany(type => type.Fields).Select(field =>
                        field.DeclaringType.FullName + "::" + field.Name + "|" + field.FieldType.FullName)
                    : before!.Fields.Select(field => field.Identity)).ToHashSet(StringComparer.Ordinal);
                foreach (TypeDef type in entry.Value.GetTypes().Where(type => type.Name != "<Module>"))
                {
                    bool generic = false;
                    Layout(new Use(type, Context.Empty, TypeKey(type)), ref generic);
                    foreach (FieldDef field in type.Fields.Where(field => field.IsStatic))
                    {
                        var dependencies = new HashSet<string>(StringComparer.Ordinal);
                        bool open = false;
                        Inline(field.FieldType, Context.Empty, dependencies, ref open);
                        string identity = field.DeclaringType.FullName + "::" + field.Name + "|" + field.FieldType.FullName;
                        if (dependencies.Count != 0 && oldFields.Contains(identity))
                        {
                            staticFields.Add(entry.Key + ":" + identity);
                            staticDetails.Add(new(entry.Key + ":" + identity, entry.Key, ordinary,
                                field.CustomAttributes.Any(attribute => attribute.TypeFullName == "System.ThreadStaticAttribute"), field.HasFieldRVA)
                            { FieldToken = field.MDToken.Raw, DeclaringTypeToken = type.MDToken.Raw });
                        }
                    }
                    foreach (MethodDef method in type.Methods)
                    {
                        string identity = MetaVersionSnapshot.MethodIdentity(method);
                        if (!oldMethods.Contains(identity)) continue; // New methods are already interpreted.
                        var dependencies = new HashSet<string>(StringComparer.Ordinal);
                        bool openContext = false;
                        Signature(type.ToTypeSig(), Context.Empty, dependencies, ref openContext);
                        MethodSignature(method.MethodSig, Context.Empty, dependencies, ref openContext);
                        if (method.HasBody)
                        {
                            foreach (var local in method.Body.Variables)
                                Signature(local.Type, Context.Empty, dependencies, ref openContext);
                            foreach (var handler in method.Body.ExceptionHandlers)
                                Signature(handler.CatchType?.ToTypeSig(), Context.Empty, dependencies, ref openContext);
                            foreach (var instruction in method.Body.Instructions)
                            {
                                if (instruction.Operand is IMethod call)
                                {
                                    Signature(call.DeclaringType.ToTypeSig(), Context.Empty, dependencies, ref openContext);
                                    Context owner = GenericContext(call.DeclaringType.ToTypeSig(), Context.Empty);
                                    Bound[] arguments = call is MethodSpec spec
                                        ? spec.GenericInstMethodSig.GenericArguments.Select(arg => new Bound(arg, Context.Empty)).ToArray()
                                        : Array.Empty<Bound>();
                                    var context = new Context(owner.Types, arguments);
                                    MethodSignature(call.MethodSig, context, dependencies, ref openContext);
                                    foreach (Bound argument in arguments)
                                        Signature(argument.Signature, argument.Context, dependencies, ref openContext);
                                }
                                else if (instruction.Operand is IField field)
                                {
                                    Signature(field.DeclaringType.ToTypeSig(), Context.Empty, dependencies, ref openContext);
                                    Context owner = GenericContext(field.DeclaringType.ToTypeSig(), Context.Empty);
                                    Signature(field.FieldSig?.Type, owner, dependencies, ref openContext);
                                }
                                else if (instruction.Operand is ITypeDefOrRef reference)
                                    Signature(reference.ToTypeSig(), Context.Empty, dependencies, ref openContext);
                                else if (instruction.Operand is TypeSig signature)
                                    Signature(signature, Context.Empty, dependencies, ref openContext);
                                else if (instruction.Operand is MethodSig indirect)
                                    MethodSignature(indirect, Context.Empty, dependencies, ref openContext);
                            }
                        }
                        if (dependencies.Count != 0 || openContext)
                            methods.Add(new(entry.Key, identity,
                                ordinary || method.IsPinvokeImpl ? "native-abi-bridge" :
                                dependencies.Count != 0 ? "interpret" : "inspect-generic-context",
                                dependencies.OrderBy(value => value, StringComparer.Ordinal).ToArray())
                            { CurrentMethodToken = method.MDToken.Raw });
                    }
                }
            }
            return new(changed.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                layouts.Where(entry => entry.Value.Count != 0).OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => new DheValueLayoutTypeImpact(entry.Key,
                        !hotfixAssemblies.Contains(entry.Key.Split('|')[0]),
                        entry.Value.OrderBy(value => value, StringComparer.Ordinal).ToArray())
                    {
                        AssemblyName = layoutDefinitions[entry.Key].DefinitionAssembly.Name.String,
                        DefinitionIdentity = TypeKey(layoutDefinitions[entry.Key]),
                        CurrentTypeToken = layoutDefinitions[entry.Key].MDToken.Raw,
                    }).ToArray(),
                methods.OrderBy(method => method.AssemblyName, StringComparer.Ordinal)
                    .ThenBy(method => method.MethodIdentity, StringComparer.Ordinal).ToArray())
            { ChangedStaticValueFields = staticFields.ToArray(),
                StaticValueFields = staticDetails.OrderBy(field => field.Identity, StringComparer.Ordinal).ToArray() };
        }

        private void MethodSignature(MethodSig? signature, Context context,
            HashSet<string> dependencies, ref bool open)
        {
            if (signature == null) return;
            Signature(signature.RetType, context, dependencies, ref open);
            foreach (TypeSig parameter in signature.Params)
                Signature(parameter, context, dependencies, ref open);
            if (signature.ParamsAfterSentinel != null)
                foreach (TypeSig parameter in signature.ParamsAfterSentinel)
                    Signature(parameter, context, dependencies, ref open);
        }

        private void ExpandExistingFieldTypeDependencies(Dictionary<string, MetaVersionSnapshot> baseline)
        {
            foreach (string root in changed) fieldTypeDependencies.Add(root, new HashSet<string>(StringComparer.Ordinal) { root });
            var candidates = new List<TypeDef>();
            foreach (var entry in modules)
            {
                if (!hotfixAssemblies.Contains(entry.Key) || !baseline.TryGetValue(entry.Key, out var before)) continue;
                var existing = before.Types.Select(type => type.Identity).ToHashSet(StringComparer.Ordinal);
                candidates.AddRange(entry.Value.GetTypes().Where(type => !type.IsInterface && existing.Contains(type.FullName)));
            }
            bool added;
            do
            {
                added = false;
                foreach (TypeDef type in candidates)
                {
                    var dependencies = new HashSet<string>(StringComparer.Ordinal);
                    if (type.BaseType != null) CollectSelectedConcreteTypes(type.BaseType.ToTypeSig(), dependencies);
                    foreach (FieldDef field in type.Fields.Where(field => !field.IsStatic))
                        CollectSelectedConcreteTypes(field.FieldType, dependencies);
                    if (dependencies.Count == 0) continue;
                    string key = TypeKey(type);
                    if (!fieldTypeDependencies.TryGetValue(key, out var stored))
                        fieldTypeDependencies.Add(key, stored = new HashSet<string>(StringComparer.Ordinal));
                    int previous = stored.Count;
                    stored.UnionWith(dependencies);
                    added |= stored.Count != previous;
                }
            } while (added);
        }

        private void CollectSelectedConcreteTypes(TypeSig signature, HashSet<string> dependencies)
        {
            // T alone is not a declaration-wide dependency. Closed arguments are
            // mapped by the existing context-sensitive generic execution path.
            if (signature == null || signature is CorLibTypeSig || signature is GenericSig) return;
            if (signature is GenericInstSig generic)
            {
                CollectSelectedConcreteTypes(generic.GenericType, dependencies);
                foreach (TypeSig argument in generic.GenericArguments) CollectSelectedConcreteTypes(argument, dependencies);
            }
            else if (signature is TypeDefOrRefSig named && fieldTypeDependencies.TryGetValue(TypeKey(named.TypeDefOrRef), out var roots))
                dependencies.UnionWith(roots);
            if (signature.Next != null) CollectSelectedConcreteTypes(signature.Next, dependencies);
        }

        private void Signature(TypeSig? signature, Context context, HashSet<string> dependencies, ref bool open)
        {
            if (signature == null || signature is CorLibTypeSig) return;
            if (signature is GenericSig variable)
            {
                Bound[] arguments = variable is GenericVar ? context.Types : context.Methods;
                if (variable.Number >= arguments.Length) { open = true; return; }
                Bound bound = arguments[variable.Number];
                Signature(bound.Signature, bound.Context, dependencies, ref open);
                return;
            }
            Use? use = ResolveUse(signature, context);
            if (use != null) dependencies.UnionWith(Layout(use, ref open));
            if (signature is GenericInstSig instance)
                foreach (TypeSig argument in instance.GenericArguments)
                    Signature(argument, context, dependencies, ref open);
            if (signature.Next != null) Signature(signature.Next, context, dependencies, ref open);
            if (signature is FnPtrSig pointer) MethodSignature(pointer.MethodSig, context, dependencies, ref open);
        }

        private HashSet<string> Layout(Use use, ref bool open)
        {
            if (layouts.TryGetValue(use.Key, out var cached))
            {
                open |= genericLayouts[use.Key];
                return cached;
            }
            if (!visiting.Add(use.Key))
                throw new InvalidDataException("Recursive inline value layout: " + use.Key);
            var result = new HashSet<string>(StringComparer.Ordinal);
            bool generic = false;
            if (fieldTypeDependencies.TryGetValue(TypeKey(use.Definition), out var roots)) result.UnionWith(roots);
            if (!use.Definition.IsValueType && use.Definition.BaseType != null)
            {
                Use? parent = ResolveUse(use.Definition.BaseType.ToTypeSig(), use.Context);
                if (parent != null) result.UnionWith(Layout(parent, ref generic));
            }
            foreach (FieldDef field in use.Definition.Fields.Where(field => !field.IsStatic))
                Inline(field.FieldType, use.Context, result, ref generic);
            visiting.Remove(use.Key);
            layouts.Add(use.Key, result);
            layoutDefinitions.Add(use.Key, use.Definition);
            genericLayouts.Add(use.Key, generic);
            open |= generic;
            return result;
        }

        private void Inline(TypeSig signature, Context context, HashSet<string> result, ref bool open)
        {
            // ECMA primitive storage is intrinsic. Core-library definitions such
            // as Int32.m_value : int are not recursive user-defined value layouts.
            if (signature is CorLibTypeSig) return;
            if (signature is GenericSig variable)
            {
                Bound[] arguments = variable is GenericVar ? context.Types : context.Methods;
                if (variable.Number >= arguments.Length) { open = true; return; }
                Bound bound = arguments[variable.Number];
                Inline(bound.Signature, bound.Context, result, ref open);
                return;
            }
            if (signature is ModifierSig || signature is PinnedSig)
            {
                Inline(signature.Next, context, result, ref open);
                return;
            }
            // References, arrays, pointers and byrefs have fixed pointer storage.
            if (!signature.IsValueType) return;
            Use? use = ResolveUse(signature, context);
            if (use != null) result.UnionWith(Layout(use, ref open));
            // External value-generic storage (e.g. Nullable<T>) may embed T.
            else if (signature is GenericInstSig generic)
                foreach (TypeSig argument in generic.GenericArguments)
                    Inline(argument, context, result, ref open);
        }

        private Use? ResolveUse(TypeSig signature, Context context)
        {
            ITypeDefOrRef? reference = signature is GenericInstSig instance
                ? instance.GenericType.TypeDefOrRef : (signature as TypeDefOrRefSig)?.TypeDefOrRef;
            if (reference == null) return null;
            string name = reference.DefinitionAssembly?.Name.String ?? "";
            if (!modules.TryGetValue(name, out var module)) return null; // Ordinary AOT domain.
            if (reference.DefinitionAssembly!.FullName != module.Assembly.FullName)
                throw new InvalidDataException("Assembly identity mismatch: " + reference.FullName);
            TypeDef definition = module.Find(reference.FullName, false) ??
                throw new InvalidDataException("Missing current hotfix type: " + name + "|" + reference.FullName);
            return new(definition, GenericContext(signature, context), Key(signature, context));
        }

        private static Context GenericContext(TypeSig signature, Context context) => new(
            signature is GenericInstSig closed
                ? closed.GenericArguments.Select(argument => new Bound(argument, context)).ToArray()
                : Array.Empty<Bound>(), Array.Empty<Bound>());

        private string Key(TypeSig signature, Context context)
        {
            if (signature is GenericSig variable)
            {
                Bound[] arguments = variable is GenericVar ? context.Types : context.Methods;
                if (variable.Number >= arguments.Length) return signature.FullName;
                Bound bound = arguments[variable.Number];
                return Key(bound.Signature, bound.Context);
            }
            if (signature is GenericInstSig generic)
                return TypeKey(generic.GenericType.TypeDefOrRef) + "<" +
                    string.Join(",", generic.GenericArguments.Select(argument => Key(argument, context))) + ">";
            if (signature is TypeDefOrRefSig reference) return TypeKey(reference.TypeDefOrRef);
            return signature.Next == null ? signature.FullName : signature.ElementType + "(" + Key(signature.Next, context) + ")";
        }

        private static string TypeKey(ITypeDefOrRef type) =>
            (type.DefinitionAssembly?.Name.String ?? "") + "|" + type.FullName;
        public void Dispose() { foreach (var module in modules.Values) module.Dispose(); }
    }
}
