namespace HybridCLR.DheTool;

internal sealed class ResourceUpdateCompatibility
{
    internal const string PhysicalInterfaceAdditionCapability = "physical-current-interface-additions-v1";
    internal const string PhysicalInterfaceEvolutionCapability = "physical-current-interface-evolution-v1";
    internal const string ImplicitInterfaceDeclarationCapability = "current-implicit-interface-method-declarations-v1";
    internal const string PhysicalInterfaceMapCapability = "physical-current-interface-map-v1";
    internal const string ParameterCacheSelectionCapability = "current-parameter-cache-selection-v1";
    internal const string ReferenceVirtualInvocationCapability = "physical-current-reference-virtual-invocation-v1";
    internal const string GenericMethodImplOwnerCapability = "current-generic-methodimpl-owners-v1";
    internal const string VirtualSignatureFrameCapability = "current-virtual-signature-frames-v1";
    internal const string ScalarInstanceFrameCapability = "current-scalar-instance-frames-v1";
    internal const string PhysicalParentEvolutionCapability = "physical-current-parent-evolution-v1";
    internal const string IdenticalPhysicalFrameCapability = "current-identical-physical-frames-v1";
    internal const string NativeReferencePhysicalFrameCapability = "current-native-reference-physical-frames-v1";
    internal const string ParentMemberHandleCapability = "current-parent-member-handles-v1";
    internal const string FrozenFieldObjectValidationCapability = "frozen-field-object-validation-v1";
    internal const string FrozenBaseInstanceFrameCapability = "frozen-base-instance-frames-v1";
	public const string Policy = "dhe-proven-safe-subset-v1";
	public const string RuntimeProtocol = "dhe-runtime-protocol-v1";
    public const string CurrentNativeRuntimeContract = "dhe-runtime-v33";
    public static readonly string[] KnownRuntimeCapabilities =
    {
		"aot-guard-v1",
		"stable-method-identity-v1",
		"single-current-multibase-v1",
		"resource-update-plan-integrity-v1",
		"resource-update-aot-metadata-path-v1",
		"resource-update-aot-metadata-set-selection-v1",
		"atomic-multi-assembly-registration-v1",
        "current-storage-execution-plan-array-v1",
        PhysicalInterfaceAdditionCapability,
        PhysicalInterfaceEvolutionCapability,
        ImplicitInterfaceDeclarationCapability,
        PhysicalInterfaceMapCapability,
        ParameterCacheSelectionCapability,
        ReferenceVirtualInvocationCapability,
        GenericMethodImplOwnerCapability,
        VirtualSignatureFrameCapability,
        ScalarInstanceFrameCapability,
        PhysicalParentEvolutionCapability,
        IdenticalPhysicalFrameCapability,
        NativeReferencePhysicalFrameCapability,
        ParentMemberHandleCapability,
        FrozenFieldObjectValidationCapability,
        FrozenBaseInstanceFrameCapability,
        ResourceExecutionPlan.GenericContextCapability,
        "current-parameter-default-metadata-v1",
        "shared-type-initialization-v1",
        "current-static-value-storage-v1",
		"frozen-aot-source-v1",
		"frozen-aot-snapshot-source-binding-v1",
        "mixed-interpreter-source-batch-v1",
        "deferred-aot-module-initialization-v1",
        "current-literal-field-values-v1",
        "aot-module-token-resolution-v1",
        "length-preserved-constant-strings-v1",
        "aot-inline-entry-guards-v1",
        "tracked-native-load-phase-v1",
        "frozen-generic-context-dispatch-v1",
		"supplemental-existing-type-instance-fields-v1",
        "supplemental-existing-type-static-fields-v1",
        "supplemental-existing-generic-type-fields-v1",
        "supplemental-instance-field-addresses-v1",
        "aot-fgs-field-address-null-check-v1",
        "existing-interface-method-slots-v1",
        "cross-assembly-interface-declarations-v1",
        "inherited-interface-dispatch-v1",
        "base-virtual-slots-on-current-descendants-v1",
        "existing-class-virtual-methods-v1",
        "closed-current-parent-vtables-v1",
        "open-generic-dispatch-definitions-v1",
        "supplemental-closed-generic-methods-v1",
        "supplemental-generic-memberref-signatures-v1",
        "closed-interpreter-parent-vtables-v1",
        "closed-generic-method-definitions-v1",
		"supplemental-existing-type-methods-v1",
		"removed-existing-type-methods-v1",
		"existing-type-method-signature-replacement-v1",
		"removed-existing-type-fields-v1",
		"removed-types-v1",
		"logical-existing-type-properties-events-v1",
		"logical-existing-member-custom-attributes-v1",
        "supplemental-method-custom-attributes-v1",
        "assembly-reference-evolution-v1",
        "supplemental-type-base-references-v1",
        "supplemental-type-declarations-v1",
        "supplemental-method-generic-invocation-v1",
        "supplemental-generic-unresolved-stubs-v1",
        "homologous-attribute-constructors-v1",
        "logical-attribute-members-v1",
        "supplemental-nested-types-v1",
        "supplemental-top-level-types-v1",
    };

    public int UnchangedMethodCount { get; private init; }
    public int ChangedMethodCount { get; private init; }
    public int BodyOnlyChangedMethodCount { get; private init; }
	public int DependencyChangedMethodCount { get; private init; }
    public int RemovedMethodCount { get; private init; }
    public int AddedMethodCount { get; private init; }
	public int RemovedFieldCount { get; private init; }
	public int AddedFieldCount { get; private init; }
    public int ChangedExistingTypeCount { get; private init; }
    public int RemovedTypeCount { get; private init; }
    public int AddedTypeCount { get; private init; }
    public MetaVersionMethod[] GuardRequiredMethods { get; private init; } =
        Array.Empty<MetaVersionMethod>();
    public string[] RequiredRuntimeCapabilities { get; private init; } = Array.Empty<string>();
    public string[] UnsupportedChanges { get; private init; } = Array.Empty<string>();
    public bool Compatible => UnsupportedChanges.Length == 0;

    public static bool CanExecuteUpdate(string runtimeProtocol, string runtimeContract,
        IEnumerable<string> availableCapabilities,
        IEnumerable<string> requiredCapabilities)
    {
        string[] available = availableCapabilities.Where(value =>
            !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
        string[] required = requiredCapabilities.Where(value =>
            !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
        return string.Equals(runtimeProtocol, RuntimeProtocol, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(runtimeContract) &&
            required.Length != 0 &&
            new HashSet<string>(available, StringComparer.Ordinal).IsSupersetOf(required);
    }

    public static ResourceUpdateCompatibility Analyze(MetaVersionSnapshot baseline,
        MetaVersionSnapshot current, IEnumerable<string>? addressTakenFields = null,
        bool usesUnresolvedCallStubs = true, IEnumerable<MetaVersionSnapshot>? currentAssemblySet = null,
        IEnumerable<string>? currentStorageTypes = null, IEnumerable<uint>? currentExecutionMethodTokens = null,
        IEnumerable<uint>? currentGenericContextMethodTokens = null,
        IEnumerable<MetaVersionSnapshot>? baselineAssemblySet = null)
    {
        var baselineMethods = baseline.Methods.ToDictionary(method => method.StableId,
            StringComparer.OrdinalIgnoreCase);
        var currentMethods = current.Methods.ToDictionary(method => method.StableId,
            StringComparer.OrdinalIgnoreCase);
        var baselineTypes = baseline.Types.ToDictionary(type => type.StableId,
            StringComparer.OrdinalIgnoreCase);
        var currentTypes = current.Types.ToDictionary(type => type.StableId,
            StringComparer.OrdinalIgnoreCase);
        var baselineFields = baseline.Fields.ToDictionary(field => field.StableId,
            StringComparer.OrdinalIgnoreCase);
        var currentFields = current.Fields.ToDictionary(field => field.StableId,
            StringComparer.OrdinalIgnoreCase);
        var unsupported = new List<string>();
        // Materialize once and reject ambiguous sets before downstream capability
        // scans build their own lookup tables. An invalid graph must never suppress
        // a required capability or silently choose one of two same-name snapshots.
        baselineAssemblySet = baselineAssemblySet?.ToArray();
        currentAssemblySet = currentAssemblySet?.ToArray();
        bool InvalidPeers(IEnumerable<MetaVersionSnapshot>? peers, MetaVersionSnapshot own)
        {
            if (peers == null) return false;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return peers.Any(peer => !names.Add(peer.AssemblyName) ||
                string.Equals(peer.AssemblyName, own.AssemblyName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(peer.AssemblySha256, own.AssemblySha256, StringComparison.OrdinalIgnoreCase));
        }
        if (InvalidPeers(baselineAssemblySet, baseline)) unsupported.Add("invalid-baseline-assembly-snapshot-set");
        if (InvalidPeers(currentAssemblySet, current)) unsupported.Add("invalid-current-assembly-snapshot-set");
        if (unsupported.Count != 0) return new ResourceUpdateCompatibility { UnsupportedChanges = unsupported.ToArray() };
        var physicalTypes = new HashSet<string>(currentStorageTypes ?? Array.Empty<string>(), StringComparer.Ordinal);
        var executionTokens = new HashSet<uint>(currentExecutionMethodTokens ?? Array.Empty<uint>());
        uint[] conditionalTokens = (currentGenericContextMethodTokens ?? Array.Empty<uint>()).ToArray();
        var currentMethodsByToken = current.Methods.ToDictionary(method => method.Token);
        if (conditionalTokens.Distinct().Count() != conditionalTokens.Length)
            unsupported.Add("duplicate-conditional-generic-selection");
        foreach (uint token in conditionalTokens)
        {
            if (!executionTokens.Contains(token) || !currentMethodsByToken.TryGetValue(token, out var method) ||
                (method.GenericParameterCount == 0 && method.DeclaringTypeGenericParameterCount == 0) ||
                physicalTypes.Contains(method.DeclaringTypeStableId) ||
                !baselineMethods.TryGetValue(method.StableId, out var previous) || previous.Version != method.Version)
                unsupported.Add("invalid-conditional-generic-selection:" + token.ToString("X8"));
        }
        if (!string.Equals(baseline.AssemblyName, current.AssemblyName,
                StringComparison.Ordinal))
            unsupported.Add("assembly-name-change:" + baseline.AssemblyName + "->" +
                current.AssemblyName);
        if (!string.Equals(baseline.AssemblyNonReferenceMetadataVersion,
                current.AssemblyNonReferenceMetadataVersion, StringComparison.OrdinalIgnoreCase))
            unsupported.Add("assembly-or-module-metadata-change:" + baseline.AssemblyName);
        foreach (var reference in baseline.AssemblyReferences)
        {
            if (current.AssemblyReferences.TryGetValue(reference.Key, out string? currentIdentity) &&
                !string.Equals(reference.Value, currentIdentity, StringComparison.Ordinal))
                unsupported.Add("existing-assembly-reference-identity-change:" + reference.Key);
        }
        // Full type names alone do not distinguish two assemblies defining the
        // same type. Do not let reference evolution silently retarget old AOT IL.
        foreach (var type in baseline.TypeReferenceScopes)
        {
            if (current.TypeReferenceScopes.TryGetValue(type.Key, out string? currentScope) &&
                !string.Equals(type.Value, currentScope, StringComparison.Ordinal))
                unsupported.Add("existing-type-reference-scope-change:" + type.Key);
        }

        MetaVersionMethod[] changed = baseline.Methods.Where(method =>
            currentMethods.TryGetValue(method.StableId, out MetaVersionMethod? currentMethod) &&
            (!string.Equals(method.Version, currentMethod.Version, StringComparison.OrdinalIgnoreCase) ||
             executionTokens.Contains(currentMethod.Token))).ToArray();
        MetaVersionMethod[] removed = baseline.Methods.Where(method =>
            !currentMethods.ContainsKey(method.StableId)).ToArray();
        MetaVersionMethod[] added = current.Methods.Where(method =>
            !baselineMethods.ContainsKey(method.StableId)).ToArray();

        bool parameterDefaultsChanged = false;
        bool implicitInterfaceDeclarationsChanged = false;
        foreach (MetaVersionMethod method in changed)
        {
            MetaVersionMethod currentMethod = currentMethods[method.StableId];
            if (!string.Equals(method.NonCustomMetadataVersion,
                    currentMethod.NonCustomMetadataVersion,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (method.ParameterDefaultIndependentMetadataVersion.Length != 0 &&
                    string.Equals(method.ParameterDefaultIndependentMetadataVersion,
                        currentMethod.ParameterDefaultIndependentMetadataVersion, StringComparison.OrdinalIgnoreCase))
                    parameterDefaultsChanged = true;
                else if (physicalTypes.Contains(method.DeclaringTypeStableId) &&
                    baselineTypes.TryGetValue(method.DeclaringTypeStableId, out var ownerBefore) &&
                    currentTypes.TryGetValue(method.DeclaringTypeStableId, out var ownerAfter) &&
                    HasOnlySupportedPhysicalInterfaceEvolution(ownerBefore, ownerAfter) &&
                    HasOnlySupportedImplicitInterfaceDeclarationChange(method, currentMethod))
                {
                    implicitInterfaceDeclarationsChanged = true;
                    parameterDefaultsChanged |= !string.Equals(method.InterfaceDeclarationIndependentMetadataVersion,
                        currentMethod.InterfaceDeclarationIndependentMetadataVersion, StringComparison.OrdinalIgnoreCase);
                }
                else
                    unsupported.Add("existing-method-metadata-change:" + method.Identity);
            }
        }
		// A removed Base method keeps its native symbol for binary compatibility,
		// but its universal guard resolves to a MissingMethodException tombstone.
		// A same-name added method is therefore also a safe signature replacement.

        foreach (MetaVersionField field in baseline.Fields)
        {
            if (baselineTypes.TryGetValue(field.DeclaringTypeStableId,
                    out MetaVersionType? baselineDeclaringType) &&
                baselineDeclaringType.IsPrivateImplementationDetails)
                continue;
			if (!currentTypes.ContainsKey(field.DeclaringTypeStableId))
				continue;
            if (!currentFields.TryGetValue(field.StableId, out MetaVersionField? currentField))
			{
				if (!field.IsStatic && field.DeclaringTypeIsValueType && !physicalTypes.Contains(field.DeclaringTypeStableId))
					unsupported.Add("removed-instance-field-on-existing-value-type:" + field.Identity);
			}
			else if (!string.Equals(field.NonCustomMetadataVersion,
					 currentField.NonCustomMetadataVersion,
                         StringComparison.OrdinalIgnoreCase) &&
                     (field.IsStatic || !physicalTypes.Contains(field.DeclaringTypeStableId)) &&
                     !IsSupportedLiteralValueEvolution(field, currentField))
                unsupported.Add("existing-field-metadata-change:" + field.Identity);
        }
        var allAddressTakenFields = new HashSet<string>(addressTakenFields ??
            current.AddressTakenFieldIdentities, StringComparer.Ordinal);
        foreach (MetaVersionField field in current.Fields.Where(field =>
                     !baselineFields.ContainsKey(field.StableId) &&
                     baselineTypes.ContainsKey(field.DeclaringTypeStableId)))
        {
			if (baselineTypes[field.DeclaringTypeStableId].IsPrivateImplementationDetails)
                continue;
			if (!field.IsStatic && !IsSupportedInstanceFieldAddition(field) && !physicalTypes.Contains(field.DeclaringTypeStableId))
				unsupported.Add(UnsupportedInstanceFieldReason(field) + ":" + field.Identity);
            else if (field.IsStatic && (field.IsThreadStatic || field.HasRva))
                unsupported.Add("added-threadstatic-or-rva-field-on-existing-type:" +
                    field.Identity);
        }

        var interfaceImplementations = new HashSet<string>(current.InterfaceImplementationMethodIdentities,
            StringComparer.Ordinal);
        bool requiresInterfaceSlots = false;
        bool requiresClassVirtualMethods = removed.Any(method => method.IsVirtual &&
            !method.DeclaringTypeIsInterface && !method.DeclaringTypeIsValueType &&
            currentTypes.ContainsKey(method.DeclaringTypeStableId));
        foreach (MetaVersionMethod method in added)
        {
            if (!baselineTypes.TryGetValue(method.DeclaringTypeStableId, out MetaVersionType? declaringType))
                continue;
            if (declaringType.IsInterface || method.DeclaringTypeIsInterface)
            {
                if (!method.IsStatic && !method.IsPInvoke && method.IsAbstract)
                    requiresInterfaceSlots = true;
                else
                    unsupported.Add("added-method-on-existing-interface:" + method.Identity);
            }
            else if (interfaceImplementations.Contains(method.Identity))
                requiresInterfaceSlots = true;
            else if (method.IsVirtual && !method.IsStatic && !method.IsPInvoke && !method.DeclaringTypeIsValueType)
                requiresClassVirtualMethods = true;
            else if (method.IsVirtual || (method.Flags & (2u | 4u)) != 0)
                unsupported.Add("added-virtual-abstract-or-pinvoke-method-on-existing-type:" + method.Identity);
        }

        MetaVersionType[] changedTypes = baseline.Types.Where(type =>
            currentTypes.TryGetValue(type.StableId, out MetaVersionType? currentType) &&
            !string.Equals(type.Version, currentType.Version, StringComparison.OrdinalIgnoreCase)).ToArray();
        bool requiresPhysicalInterfaceAddition = false;
        bool requiresPhysicalInterfaceEvolution = false;
        bool requiresPhysicalParentEvolution = false;
        var parentGraphs = new PhysicalParentGraphs(baseline, current, baselineAssemblySet, currentAssemblySet);
        foreach (MetaVersionType type in changedTypes)
        {
            MetaVersionType currentType = currentTypes[type.StableId];
            bool physicalInterfaceAddition = physicalTypes.Contains(type.StableId) &&
                HasOnlySupportedPhysicalInterfaceAddition(type, currentType);
            bool physicalInterfaceEvolution = !physicalInterfaceAddition && physicalTypes.Contains(type.StableId) &&
                HasOnlySupportedPhysicalInterfaceEvolution(type, currentType);
            bool physicalParentEvolution = physicalTypes.Contains(type.StableId) &&
                HasOnlySupportedPhysicalParentEvolution(type, currentType, baseline, current, parentGraphs);
            requiresPhysicalInterfaceAddition |= physicalInterfaceAddition;
            requiresPhysicalInterfaceEvolution |= physicalInterfaceEvolution;
            requiresPhysicalParentEvolution |= physicalParentEvolution;
            if (!string.Equals(type.LayoutVersion, currentType.LayoutVersion, StringComparison.OrdinalIgnoreCase) &&
                ((!string.Equals(type.NonFieldLayoutVersion, currentType.NonFieldLayoutVersion,
                     StringComparison.OrdinalIgnoreCase) && !physicalInterfaceAddition && !physicalInterfaceEvolution && !physicalParentEvolution) ||
                 (!physicalTypes.Contains(type.StableId) && !HasOnlySupportedInstanceFieldEvolution(type, baselineFields, currentFields))))
                unsupported.Add("existing-type-layout-or-vtable-change:" + type.Identity);
			if (!string.Equals(type.NonCustomUnsupportedDeclarativeVersion,
					currentType.NonCustomUnsupportedDeclarativeVersion,
					StringComparison.OrdinalIgnoreCase))
				unsupported.Add("existing-type-unsupported-declarative-metadata-change:" +
					type.Identity);
            if (!string.Equals(type.StaticFieldVersion, currentType.StaticFieldVersion,
                    StringComparison.OrdinalIgnoreCase) && !type.IsPrivateImplementationDetails &&
				!HasOnlySupportedStaticFieldEvolution(type, baselineFields, currentFields))
                unsupported.Add("existing-type-static-field-change:" + type.Identity);
        }

        MetaVersionType[] removedTypes = baseline.Types.Where(type =>
            !currentTypes.ContainsKey(type.StableId)).ToArray();

        MetaVersionType[] addedTypes = current.Types.Where(type =>
            !baselineTypes.ContainsKey(type.StableId)).ToArray();
		MetaVersionField[] removedFields = baseline.Fields.Where(field =>
			!currentFields.ContainsKey(field.StableId)).ToArray();
        MetaVersionField[] addedFields = current.Fields.Where(field =>
			!baselineFields.ContainsKey(field.StableId)).ToArray();

        var requiredCapabilities = new HashSet<string>(StringComparer.Ordinal)
        {
            "aot-guard-v1",
            "stable-method-identity-v1",
            "single-current-multibase-v1",
            "atomic-multi-assembly-registration-v1",
            "aot-inline-entry-guards-v1",
        };
        if (current.HasEmbeddedNullStringDefaults)
            requiredCapabilities.Add("length-preserved-constant-strings-v1");
        if (requiresPhysicalParentEvolution)
        {
            requiredCapabilities.Add(PhysicalParentEvolutionCapability);
            requiredCapabilities.Add(IdenticalPhysicalFrameCapability);
            requiredCapabilities.Add(NativeReferencePhysicalFrameCapability);
            requiredCapabilities.Add(ParentMemberHandleCapability);
            requiredCapabilities.Add(FrozenFieldObjectValidationCapability);
            requiredCapabilities.Add(FrozenBaseInstanceFrameCapability);
        }
        if (parameterDefaultsChanged || added.Any(method => method.HasParameterDefaults))
            requiredCapabilities.Add("current-parameter-default-metadata-v1");
        if (parameterDefaultsChanged || physicalTypes.Count != 0 || conditionalTokens.Length != 0)
            requiredCapabilities.Add(ParameterCacheSelectionCapability);
        if (current.Types.Any(type => physicalTypes.Contains(type.StableId) && (type.Flags & 1u) == 0 && !type.IsInterface))
        {
            requiredCapabilities.Add(PhysicalInterfaceMapCapability);
            requiredCapabilities.Add(ReferenceVirtualInvocationCapability);
        }
        if (implicitInterfaceDeclarationsChanged)
            requiredCapabilities.Add(ImplicitInterfaceDeclarationCapability);
        // MethodImpl resolution runs during preparation even for a no-op update.
        // A generic declaration introduced wholly in Current does not reuse a
        // Base generic container. Other DHE assemblies are conservatively treated
        // as having a Base declaration because this analysis is per assembly.
        if (current.GenericMethodImplDeclarations.Any(declaration =>
                declaration.AssemblyName == baseline.AssemblyName
                    ? baseline.Types.Any(type => type.Identity == declaration.TypeName)
                    : currentAssemblySet == null || currentAssemblySet.Any(assembly =>
                        assembly.AssemblyName == declaration.AssemblyName)))
            requiredCapabilities.Add(GenericMethodImplOwnerCapability);
        if (current.Methods.Any(method => method.IsVirtual && HasNonScalarSignature(method) &&
                baselineMethods.ContainsKey(method.StableId) &&
                (physicalTypes.Contains(method.DeclaringTypeStableId) || executionTokens.Contains(method.Token))))
            requiredCapabilities.Add(VirtualSignatureFrameCapability);
        // Native entry into a selected instance body is safe only when its
        // receiver storage is retained and its complete frame remains scalar.
        // The runtime still proves exact owner mapping and all physical parents.
        if (current.Methods.Any(method => executionTokens.Contains(method.Token) && !method.IsStatic &&
                !method.DeclaringTypeIsValueType && !method.IsAbstract && !method.IsPInvoke &&
                !physicalTypes.Contains(method.DeclaringTypeStableId) && !HasNonScalarSignature(method) &&
                baselineMethods.ContainsKey(method.StableId)))
            requiredCapabilities.Add(ScalarInstanceFrameCapability);
        if (baseline.Fields.Any(field => currentFields.TryGetValue(field.StableId, out var currentField) &&
                !string.Equals(field.NonCustomMetadataVersion, currentField.NonCustomMetadataVersion, StringComparison.OrdinalIgnoreCase) &&
                IsSupportedLiteralValueEvolution(field, currentField)))
            requiredCapabilities.Add("current-literal-field-values-v1");
        if (baseline.Methods.Concat(current.Methods).Any(method => method.DeclaringType == "<Module>" && method.Name == ".cctor"))
        {
            requiredCapabilities.Add("deferred-aot-module-initialization-v1");
            requiredCapabilities.Add("aot-module-token-resolution-v1");
        }
        if (changed.Concat(removed).Any(method => method.Name == ".cctor") ||
            added.Any(method => method.Name == ".cctor" && baselineTypes.ContainsKey(method.DeclaringTypeStableId)) ||
            current.Fields.Any(field => field.IsStatic && baselineTypes.ContainsKey(field.DeclaringTypeStableId) &&
                !baselineFields.ContainsKey(field.StableId)))
            requiredCapabilities.Add("shared-type-initialization-v1");
        if (requiresInterfaceSlots)
            requiredCapabilities.Add("existing-interface-method-slots-v1");
        if (requiresPhysicalInterfaceAddition)
            requiredCapabilities.Add(PhysicalInterfaceAdditionCapability);
        if (requiresPhysicalInterfaceEvolution)
            requiredCapabilities.Add(PhysicalInterfaceEvolutionCapability);
        if (physicalTypes.Count != 0 || executionTokens.Count != 0)
            requiredCapabilities.Add("current-storage-execution-plan-array-v1");
        if (conditionalTokens.Length != 0 || current.Methods.Any(method => executionTokens.Contains(method.Token) &&
                (method.GenericParameterCount != 0 || method.DeclaringTypeGenericParameterCount != 0)))
            requiredCapabilities.Add(ResourceExecutionPlan.GenericContextCapability);
        if (requiresClassVirtualMethods)
            requiredCapabilities.Add("existing-class-virtual-methods-v1");
        if (current.Fields.Any(field => !field.IsStatic && field.DeclaringTypeIsGeneric &&
                baselineFields.ContainsKey(field.StableId) && allAddressTakenFields.Contains(field.Identity)))
            requiredCapabilities.Add("aot-fgs-field-address-null-check-v1");
        if (RequiresClosedCurrentParentVtables(addedTypes, baseline, current, currentAssemblySet))
            requiredCapabilities.Add("closed-current-parent-vtables-v1");
        if (current.Methods.Any(method => method.IsVirtual && method.GenericParameterCount != 0 &&
                baselineMethods.ContainsKey(method.StableId)))
            requiredCapabilities.Add("open-generic-dispatch-definitions-v1");
        if (added.Any(method => method.DeclaringTypeGenericParameterCount != 0 &&
                baselineTypes.ContainsKey(method.DeclaringTypeStableId)))
        {
            requiredCapabilities.Add("supplemental-closed-generic-methods-v1");
            requiredCapabilities.Add("supplemental-generic-memberref-signatures-v1");
        }
        if (current.Methods.Any(method => method.GenericParameterCount != 0 &&
                method.DeclaringTypeGenericParameterCount != 0))
            requiredCapabilities.Add("closed-generic-method-definitions-v1");
        if (addedTypes.Any(type => current.TypeParents.TryGetValue(type.Identity, out var parent) &&
                parent.AssemblyName == current.AssemblyName && parent.DefinitionName != null &&
                parent.DefinitionName != parent.TypeName && !baseline.Types.Any(original => original.Identity == parent.DefinitionName)))
            requiredCapabilities.Add("closed-interpreter-parent-vtables-v1");
        // A Current MemberRef declaration can name an interface method absent
        // from the Base definition table. Older slot-only runtimes cannot
        // resolve that declaration during atomic multi-image registration.
        var evolvedInterfaces = added.Where(method => method.DeclaringTypeIsInterface &&
            baselineTypes.ContainsKey(method.DeclaringTypeStableId)).Select(method => method.DeclaringType)
            .ToHashSet(StringComparer.Ordinal);
        if (evolvedInterfaces.Count != 0 && (currentAssemblySet == null || currentAssemblySet.Any(assembly =>
                assembly.AssemblyName != current.AssemblyName && evolvedInterfaces.Any(name =>
                    assembly.TypeReferenceScopes.TryGetValue(name, out string? scope) &&
                    scope.Split('\n').Any(identity => identity.Split(',')[0] == current.AssemblyName)))))
            requiredCapabilities.Add("cross-assembly-interface-declarations-v1");
        var interfaceOwners = current.Types.Where(type => !type.IsInterface &&
            type.LocalDeclarationReferencedTypeNames.Any(evolvedInterfaces.Contains))
            .Select(type => type.Identity).ToHashSet(StringComparer.Ordinal);
        if (evolvedInterfaces.Count != 0 && (currentAssemblySet == null || currentAssemblySet.Any(assembly =>
                assembly.TypeParents.Values.Any(parent => parent.AssemblyName == current.AssemblyName &&
                    interfaceOwners.Contains(parent.DefinitionName ?? parent.TypeName)))))
        {
            requiredCapabilities.Add("inherited-interface-dispatch-v1");
            requiredCapabilities.Add("base-virtual-slots-on-current-descendants-v1");
        }
        if (!new HashSet<string>(baseline.AssemblyReferences.Values, StringComparer.Ordinal)
                .SetEquals(current.AssemblyReferences.Values))
            requiredCapabilities.Add("assembly-reference-evolution-v1");
        if (added.Any(method => method.HasCustomAttributes &&
                baselineTypes.ContainsKey(method.DeclaringTypeStableId)))
            requiredCapabilities.Add("supplemental-method-custom-attributes-v1");
        if (added.Any(method => (method.GenericParameterCount != 0 || method.DeclaringTypeGenericParameterCount != 0) &&
                baselineTypes.ContainsKey(method.DeclaringTypeStableId)))
        {
            requiredCapabilities.Add("supplemental-method-generic-invocation-v1");
            if (usesUnresolvedCallStubs)
                requiredCapabilities.Add("supplemental-generic-unresolved-stubs-v1");
        }
        if (addedFields.Any(field => baselineTypes.ContainsKey(field.DeclaringTypeStableId) &&
                !field.IsStatic))
            requiredCapabilities.Add("supplemental-existing-type-instance-fields-v1");
        if (addedFields.Any(field => baselineTypes.ContainsKey(field.DeclaringTypeStableId) &&
                !field.IsStatic && allAddressTakenFields.Contains(field.Identity)))
            requiredCapabilities.Add("supplemental-instance-field-addresses-v1");
        if (addedFields.Any(field => baselineTypes.ContainsKey(field.DeclaringTypeStableId) &&
                field.IsStatic))
            requiredCapabilities.Add("supplemental-existing-type-static-fields-v1");
        if (addedFields.Any(field => field.DeclaringTypeIsGeneric &&
                baselineTypes.ContainsKey(field.DeclaringTypeStableId)) ||
            removedFields.Any(field => field.DeclaringTypeIsGeneric &&
                currentTypes.ContainsKey(field.DeclaringTypeStableId)))
            requiredCapabilities.Add("supplemental-existing-generic-type-fields-v1");
        if (added.Any(method => baselineTypes.ContainsKey(method.DeclaringTypeStableId)))
            requiredCapabilities.Add("supplemental-existing-type-methods-v1");
        if (removed.Length != 0)
            requiredCapabilities.Add("removed-existing-type-methods-v1");
        if (removedFields.Any(field => currentTypes.ContainsKey(field.DeclaringTypeStableId)))
            requiredCapabilities.Add("removed-existing-type-fields-v1");
        if (removedTypes.Length != 0)
            requiredCapabilities.Add("removed-types-v1");
        if (addedTypes.Any(type => type.IsNested))
            requiredCapabilities.Add("supplemental-nested-types-v1");
        if (addedTypes.Any(type => !type.IsNested))
            requiredCapabilities.Add("supplemental-top-level-types-v1");
        var baseTypeNames = new HashSet<string>(baseline.Types.Select(type => type.Identity), StringComparer.Ordinal);
        if (current.LocalAttributeConstructorTypeNames.Any(baseTypeNames.Contains))
            requiredCapabilities.Add("homologous-attribute-constructors-v1");
        if (RequiresLogicalAttributeMetadata(baseline, currentAssemblySet ?? new[] { current }))
            requiredCapabilities.Add("logical-attribute-members-v1");
        if (addedTypes.Any(type => type.LocalReferencedTypeNames.Any(baseTypeNames.Contains)))
            requiredCapabilities.Add("supplemental-type-base-references-v1");
        if (addedTypes.Any(type => type.LocalDeclarationReferencedTypeNames.Any(baseTypeNames.Contains)))
            requiredCapabilities.Add("supplemental-type-declarations-v1");
        if (HasSignatureReplacement(removed, added))
            requiredCapabilities.Add("existing-type-method-signature-replacement-v1");
        if (changedTypes.Any(type => currentTypes.TryGetValue(type.StableId,
                out MetaVersionType? currentType) &&
                !string.Equals(type.DeclarativeVersion, currentType.DeclarativeVersion,
                    StringComparison.OrdinalIgnoreCase)))
            requiredCapabilities.Add("logical-existing-type-properties-events-v1");
        if (changed.Any(method => !string.Equals(method.CustomAttributeVersion,
                    currentMethods[method.StableId].CustomAttributeVersion,
                    StringComparison.OrdinalIgnoreCase)) ||
            baseline.Fields.Any(field => currentFields.TryGetValue(field.StableId,
                    out MetaVersionField? currentField) &&
                !string.Equals(field.CustomAttributeVersion, currentField.CustomAttributeVersion,
                    StringComparison.OrdinalIgnoreCase)) ||
            changedTypes.Any(type => !string.Equals(type.CustomAttributeVersion,
                currentTypes[type.StableId].CustomAttributeVersion,
                StringComparison.OrdinalIgnoreCase)))
            requiredCapabilities.Add("logical-existing-member-custom-attributes-v1");

        return new ResourceUpdateCompatibility
        {
            UnchangedMethodCount = baseline.Methods.Length - changed.Length - removed.Length,
            ChangedMethodCount = changed.Length,
			BodyOnlyChangedMethodCount = changed.Count(method =>
				string.Equals(method.MetadataVersion, currentMethods[method.StableId].MetadataVersion,
					StringComparison.OrdinalIgnoreCase) &&
				!string.Equals(method.BodyVersion, currentMethods[method.StableId].BodyVersion,
					StringComparison.OrdinalIgnoreCase) &&
				string.Equals(method.DependencyVersion, currentMethods[method.StableId].DependencyVersion,
					StringComparison.OrdinalIgnoreCase)),
			DependencyChangedMethodCount = changed.Count(method =>
				!string.Equals(method.DependencyVersion, currentMethods[method.StableId].DependencyVersion,
					StringComparison.OrdinalIgnoreCase)),
            RemovedMethodCount = removed.Length,
            AddedMethodCount = added.Length,
			RemovedFieldCount = removedFields.Length,
			AddedFieldCount = addedFields.Length,
            ChangedExistingTypeCount = changedTypes.Length,
            RemovedTypeCount = removedTypes.Length,
            AddedTypeCount = addedTypes.Length,
            GuardRequiredMethods = changed.Concat(removed).Where(MethodCanHaveAotEntry).ToArray(),
            RequiredRuntimeCapabilities = requiredCapabilities.OrderBy(value => value,
                StringComparer.Ordinal).ToArray(),
            UnsupportedChanges = unsupported.Distinct(StringComparer.Ordinal).OrderBy(value => value,
                StringComparer.Ordinal).ToArray(),
        };
    }

    // The producer must fail closed when malformed or internally inconsistent
    // snapshots reach analysis.  Keep the strict Analyze API for policy tests,
    // while giving batch/resource workflows a deterministic, auditable result
    // instead of an unstructured process exception.
    public static ResourceUpdateCompatibility AnalyzeFailClosed(MetaVersionSnapshot baseline,
        MetaVersionSnapshot current, IEnumerable<string>? addressTakenFields = null,
        bool usesUnresolvedCallStubs = true, IEnumerable<MetaVersionSnapshot>? currentAssemblySet = null,
        IEnumerable<string>? currentStorageTypes = null, IEnumerable<uint>? currentExecutionMethodTokens = null,
        IEnumerable<uint>? currentGenericContextMethodTokens = null,
        IEnumerable<MetaVersionSnapshot>? baselineAssemblySet = null)
    {
        try
        {
            return Analyze(baseline, current, addressTakenFields, usesUnresolvedCallStubs,
                currentAssemblySet, currentStorageTypes, currentExecutionMethodTokens,
                currentGenericContextMethodTokens, baselineAssemblySet);
        }
        catch (Exception exception) when (IsNonFatalAnalysisFailure(exception))
        {
            string detail = NormalizeAnalysisFailure(exception.Message);
            string reason = "analysis-failed:" + exception.GetType().Name +
                (detail.Length == 0 ? string.Empty : ":" + detail);
            return new ResourceUpdateCompatibility
            {
                UnsupportedChanges = new[] { reason },
            };
        }
    }

    private static bool IsNonFatalAnalysisFailure(Exception exception) =>
        exception is not OutOfMemoryException &&
        exception is not StackOverflowException &&
        exception is not AccessViolationException;

    private static string NormalizeAnalysisFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return string.Empty;
        string value = string.Join(" ", message.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 240 ? value : value[..240];
    }

    private static bool HasNonScalarSignature(MetaVersionMethod method)
        => method.GenericParameterCount != 0 || method.DeclaringTypeGenericParameterCount != 0 ||
            !IsScalarFrameType(method.ReturnType) || method.ParameterTypes.Any(type => !IsScalarFrameType(type));

    private static bool IsScalarFrameType(string type) => type is
        "System.Void" or "System.Boolean" or "System.Char" or "System.SByte" or "System.Byte" or
        "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32" or "System.Int64" or
        "System.UInt64" or "System.Single" or "System.Double" or "System.IntPtr" or "System.UIntPtr" or
        "System.String" or "System.Object";

    private static bool RequiresClosedCurrentParentVtables(IEnumerable<MetaVersionType> addedTypes,
        MetaVersionSnapshot baseline, MetaVersionSnapshot current,
        IEnumerable<MetaVersionSnapshot>? currentAssemblySet)
    {
        var snapshots = (currentAssemblySet ?? new[] { current }).ToDictionary(
            snapshot => snapshot.AssemblyName, StringComparer.OrdinalIgnoreCase);
        snapshots[current.AssemblyName] = current;
        var baseNames = baseline.Types.Select(type => type.Identity).ToHashSet(StringComparer.Ordinal);
        foreach (MetaVersionType added in addedTypes.Where(type => !type.IsInterface))
        {
            var reference = new MetaVersionTypeReference(current.AssemblyName, added.Identity);
            var visited = new HashSet<MetaVersionTypeReference>();
            while (visited.Add(reference) && snapshots.TryGetValue(reference.AssemblyName, out var owner) &&
                owner.TypeParents.TryGetValue(reference.TypeName, out var parent))
            {
                string definitionName = parent.DefinitionName ?? parent.TypeName;
                if (definitionName != parent.TypeName)
                {
                    bool localParent = string.Equals(parent.AssemblyName, baseline.AssemblyName,
                        StringComparison.OrdinalIgnoreCase);
                    // Other DHE assemblies can contain an already-native parent;
                    // this per-assembly comparison does not have their Base types.
                    if (localParent ? baseNames.Contains(definitionName) :
                        currentAssemblySet == null || snapshots.ContainsKey(parent.AssemblyName))
                        return true;
                }
                reference = new MetaVersionTypeReference(parent.AssemblyName, definitionName);
            }
        }
        return false;
    }

    private static bool RequiresLogicalAttributeMetadata(MetaVersionSnapshot baseline,
        IEnumerable<MetaVersionSnapshot> currentAssemblySet)
    {
        var snapshots = currentAssemblySet.ToDictionary(snapshot => snapshot.AssemblyName, StringComparer.OrdinalIgnoreCase);
        var baseTypes = baseline.Types.Select(type => type.Identity).ToHashSet(StringComparer.Ordinal);
        var baseMethods = baseline.Methods.Select(method => method.Identity).ToHashSet(StringComparer.Ordinal);
        foreach (MetaVersionAttributeUse use in snapshots.Values.SelectMany(snapshot => snapshot.AttributeUses))
        {
            if (string.Equals(use.AssemblyName, baseline.AssemblyName, StringComparison.OrdinalIgnoreCase) &&
                baseTypes.Contains(use.TypeName) && !baseMethods.Contains(use.ConstructorIdentity))
                return true;
            if (!use.HasNamedProperties) continue;
            var type = new MetaVersionTypeReference(use.AssemblyName, use.TypeName);
            var visited = new HashSet<MetaVersionTypeReference>();
            while (visited.Add(type))
            {
                if (string.Equals(type.AssemblyName, baseline.AssemblyName, StringComparison.OrdinalIgnoreCase) &&
                    baseTypes.Contains(type.TypeName)) return true;
                if (!snapshots.TryGetValue(type.AssemblyName, out MetaVersionSnapshot? owner) ||
                    !owner.TypeParents.TryGetValue(type.TypeName, out MetaVersionTypeReference? parent)) break;
                type = parent;
            }
        }
        return false;
    }

    private static bool HasSignatureReplacement(IEnumerable<MetaVersionMethod> removed,
        IEnumerable<MetaVersionMethod> added)
    {
        var removedNames = new HashSet<string>(removed.Select(method =>
            method.DeclaringTypeStableId + "\n" + method.Name), StringComparer.Ordinal);
        return added.Any(method => removedNames.Contains(method.DeclaringTypeStableId + "\n" +
            method.Name));
    }

    private static bool MethodCanHaveAotEntry(MetaVersionMethod method) =>
        (method.Flags & 8u) != 0 && (method.Flags & (2u | 4u)) == 0;

    private static bool IsSupportedLiteralValueEvolution(MetaVersionField before, MetaVersionField after) =>
        before.IsStatic && after.IsStatic && before.IsLiteral && after.IsLiteral &&
        before.HasConstant && after.HasConstant && !before.HasRva && !after.HasRva &&
        !before.IsThreadStatic && !after.IsThreadStatic &&
        before.ConstantIndependentMetadataVersion.Length != 0 &&
        string.Equals(before.ConstantIndependentMetadataVersion, after.ConstantIndependentMetadataVersion,
            StringComparison.OrdinalIgnoreCase);
    private static bool HasOnlySupportedImplicitInterfaceDeclarationChange(MetaVersionMethod baseline, MetaVersionMethod current)
    {
        const uint slots = 0x40u | 0x20u | 0x100u; // ECMA MethodAttributes: Virtual, Final, NewSlot.
        uint before = baseline.DeclarationAttributes & slots, after = current.DeclarationAttributes & slots;
        // This transition creates/removes the compiler's own sealed interface slot.
        // Override/finality edits, generic methods and ABI/access changes need separate qualification.
        return ((before == slots && after == 0) || (before == 0 && after == slots)) &&
            (baseline.DeclarationAttributes & 7u) == 6u && (current.DeclarationAttributes & 7u) == 6u &&
            !baseline.IsStatic && !current.IsStatic && !baseline.IsAbstract && !current.IsAbstract &&
            !baseline.IsPInvoke && !current.IsPInvoke && !baseline.IsConstructor && !current.IsConstructor &&
            baseline.GenericParameterCount == 0 && current.GenericParameterCount == 0 &&
            (baseline.Flags & 8u) != 0 && (current.Flags & 8u) != 0 &&
            baseline.InterfaceDeclarationAndDefaultIndependentMetadataVersion.Length != 0 &&
            string.Equals(baseline.InterfaceDeclarationAndDefaultIndependentMetadataVersion,
                current.InterfaceDeclarationAndDefaultIndependentMetadataVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasOnlySupportedPhysicalInterfaceAddition(MetaVersionType baseline, MetaVersionType current)
    {
        return HasOnlySupportedPhysicalInterfaceEvolution(baseline, current) &&
            current.InterfaceIdentities.Length > baseline.InterfaceIdentities.Length &&
            current.InterfaceIdentities.ToHashSet(StringComparer.Ordinal).IsSupersetOf(baseline.InterfaceIdentities);
    }

    private static bool HasOnlySupportedPhysicalParentEvolution(MetaVersionType before, MetaVersionType after,
        MetaVersionSnapshot baseline, MetaVersionSnapshot current, PhysicalParentGraphs graphs)
    {
        if (!before.CanUsePhysicalParentEvolution || !after.CanUsePhysicalParentEvolution ||
            before.ParentIndependentLayoutVersion.Length == 0 ||
            before.ParentIndependentLayoutVersion != after.ParentIndependentLayoutVersion ||
            !baseline.TypeParents.TryGetValue(before.Identity, out var oldParent) ||
            !current.TypeParents.TryGetValue(after.Identity, out var newParent) || oldParent == newParent)
            return false;
        var oldBoundary = graphs.Boundary(baseline.AssemblyName, before.Identity, true);
        return oldBoundary != null && oldBoundary == graphs.Boundary(current.AssemblyName, after.Identity, false);
    }

    // Both sides use their own original/current assembly set. The producer binds
    // the original set to the archived Base identity; Current is never a fallback
    // for a missing Base peer. Index once per assembly analysis, not per type.
    private sealed class PhysicalParentGraphs
    {
        private readonly Dictionary<string, MetaVersionSnapshot>? before, after;
        private readonly Dictionary<(string Assembly, string Type), MetaVersionType> beforeTypes, afterTypes;
        private readonly HashSet<string> mutableAssemblies;

        internal PhysicalParentGraphs(MetaVersionSnapshot baseline, MetaVersionSnapshot current,
            IEnumerable<MetaVersionSnapshot>? baselinePeers, IEnumerable<MetaVersionSnapshot>? currentPeers)
        {
            before = Assemblies(baseline, baselinePeers); after = Assemblies(current, currentPeers);
            beforeTypes = Types(before); afterTypes = Types(after);
            mutableAssemblies = new HashSet<string>((before?.Keys ?? Enumerable.Empty<string>())
                .Concat(after?.Keys ?? Enumerable.Empty<string>()), StringComparer.Ordinal);
        }

        private static Dictionary<string, MetaVersionSnapshot>? Assemblies(MetaVersionSnapshot own,
            IEnumerable<MetaVersionSnapshot>? peers)
        {
            var assemblies = new Dictionary<string, MetaVersionSnapshot>(StringComparer.Ordinal);
            foreach (var peer in peers ?? Array.Empty<MetaVersionSnapshot>())
                if (!assemblies.TryAdd(peer.AssemblyName, peer)) return null;
            if (assemblies.TryGetValue(own.AssemblyName, out var supplied) &&
                !string.Equals(supplied.AssemblySha256, own.AssemblySha256, StringComparison.OrdinalIgnoreCase)) return null;
            assemblies[own.AssemblyName] = own;
            return assemblies;
        }

        private static Dictionary<(string Assembly, string Type), MetaVersionType> Types(Dictionary<string, MetaVersionSnapshot>? assemblies)
            => assemblies == null ? new() : assemblies.Values.SelectMany(assembly => assembly.Types
                .Select(type => (Key: (assembly.AssemblyName, type.Identity), Type: type))).ToDictionary(row => row.Key, row => row.Type);

        internal string? Boundary(string ownerAssembly, string ownerType, bool original)
        {
            if (before == null || after == null) return null;
            var assemblies = original ? before : after;
            var definitions = original ? beforeTypes : afterTypes;
            if (!definitions.TryGetValue((ownerAssembly, ownerType), out var owner) || !owner.ParentGenericParametersValid) return null;
            var context = Enumerable.Range(0, owner.ParentGenericParameterCount)
                .Select(index => MetaVersionParentSignature.OwnerParameter((uint)index)).ToArray();
            var parent = owner.PhysicalParent?.Close(context);
            var visited = new HashSet<(string Assembly, string Type)> { (ownerAssembly, ownerType) };
            var validatedArguments = new HashSet<MetaVersionParentSignature>();
            bool ValidSignature(MetaVersionParentSignature value)
            {
                if (!validatedArguments.Add(value)) return true;
                if (value.Kind == dnlib.DotNet.ElementType.Var) return value.Index < context.Length;
                if (value.AssemblyName.Length != 0)
                {
                    if (assemblies.ContainsKey(value.AssemblyName))
                    {
                        if (!definitions.TryGetValue((value.AssemblyName, value.DefinitionName), out var argument) || !argument.ParentGenericParametersValid ||
                            argument.ParentGenericParameterCount != value.Arguments.Length ||
                            ((argument.Flags & 1u) != 0) != value.IsValueType)
                            return false;
                    }
                    else if (mutableAssemblies.Contains(value.AssemblyName)) return false;
                }
                return value.Arguments.All(ValidSignature);
            }
            while (parent != null && parent.IsReferenceParent)
            {
                // Repeat definitions are illegal even when each trip changes
                // arguments (for example A<T> : A<List<T>>).
                var key = (parent.AssemblyName, parent.DefinitionName);
                if (!visited.Add(key) || string.IsNullOrEmpty(parent.AssemblyName) || !ValidSignature(parent)) return null;
                if (!assemblies.ContainsKey(parent.AssemblyName))
                    return mutableAssemblies.Contains(parent.AssemblyName) ? null : parent.Key;
                if (!definitions.TryGetValue(key, out var definition) || !definition.CanBePhysicalReferenceParent || !definition.ParentGenericParametersValid ||
                    definition.ParentGenericParameterCount != parent.Arguments.Length)
                    return null;
                parent = definition.PhysicalParent?.Close(parent.Arguments);
            }
            return null;
        }
    }

    private static bool HasOnlySupportedPhysicalInterfaceEvolution(MetaVersionType baseline, MetaVersionType current)
    {
        if (!baseline.CanUsePhysicalInterfaceAddition || !current.CanUsePhysicalInterfaceAddition ||
            string.IsNullOrEmpty(baseline.InterfaceAdditionLayoutVersion) ||
            !string.Equals(baseline.InterfaceAdditionLayoutVersion, current.InterfaceAdditionLayoutVersion,
                StringComparison.Ordinal))
            return false;
        var before = baseline.InterfaceIdentities.ToHashSet(StringComparer.Ordinal);
        var after = current.InterfaceIdentities.ToHashSet(StringComparer.Ordinal);
        return before.Count == baseline.InterfaceIdentities.Length &&
            after.Count == current.InterfaceIdentities.Length && !after.SetEquals(before);
    }

	private static bool HasOnlySupportedStaticFieldEvolution(MetaVersionType type,
        IReadOnlyDictionary<string, MetaVersionField> baselineFields,
        IReadOnlyDictionary<string, MetaVersionField> currentFields)
    {
        MetaVersionField[] before = baselineFields.Values.Where(field =>
            string.Equals(field.DeclaringTypeStableId, type.StableId,
				StringComparison.OrdinalIgnoreCase) && field.IsStatic).ToArray();
        MetaVersionField[] after = currentFields.Values.Where(field =>
            string.Equals(field.DeclaringTypeStableId, type.StableId,
				StringComparison.OrdinalIgnoreCase) && field.IsStatic).ToArray();
		if (before.Any(field => currentFields.TryGetValue(field.StableId,
				out MetaVersionField? current) &&
            !string.Equals(field.NonCustomMetadataVersion,
				current.NonCustomMetadataVersion, StringComparison.OrdinalIgnoreCase) &&
            !IsSupportedLiteralValueEvolution(field, current)))
            return false;
        return after.Where(field => !baselineFields.ContainsKey(field.StableId)).All(field =>
            field.IsStatic && !field.IsThreadStatic && !field.HasRva);
    }

	private static bool HasOnlySupportedInstanceFieldEvolution(MetaVersionType type,
		IReadOnlyDictionary<string, MetaVersionField> baselineFields,
		IReadOnlyDictionary<string, MetaVersionField> currentFields)
	{
		MetaVersionField[] before = baselineFields.Values.Where(field =>
			string.Equals(field.DeclaringTypeStableId, type.StableId,
				StringComparison.OrdinalIgnoreCase) && !field.IsStatic).ToArray();
		MetaVersionField[] after = currentFields.Values.Where(field =>
			string.Equals(field.DeclaringTypeStableId, type.StableId,
				StringComparison.OrdinalIgnoreCase) && !field.IsStatic).ToArray();
		string[] existingBefore = before.Where(field => currentFields.ContainsKey(field.StableId))
			.OrderBy(field => field.DeclarationIndex)
			.Select(field => field.StableId).ToArray();
		string[] existingAfter = after.Where(field => baselineFields.ContainsKey(field.StableId))
			.OrderBy(field => field.DeclarationIndex).Select(field => field.StableId).ToArray();
		if (!existingBefore.SequenceEqual(existingAfter, StringComparer.OrdinalIgnoreCase))
			return false;
		if (before.Any(field => currentFields.TryGetValue(field.StableId,
				out MetaVersionField? current) &&
			!string.Equals(field.NonCustomMetadataVersion,
				current.NonCustomMetadataVersion, StringComparison.OrdinalIgnoreCase)))
			return false;
		if (type.IsInterface || before.Any(field => field.DeclaringTypeIsValueType) &&
			(after.Length != before.Length || after.Any(field =>
				!baselineFields.ContainsKey(field.StableId))))
			return false;
		return after.Where(field => !baselineFields.ContainsKey(field.StableId))
			.All(IsSupportedInstanceFieldAddition);
	}

	private static bool IsSupportedInstanceFieldAddition(MetaVersionField field) =>
		!field.IsStatic && !field.IsLiteral && !field.IsThreadStatic &&
		!field.DeclaringTypeIsValueType && !field.HasRva &&
		!field.HasUnsupportedSidecarType;

	private static string UnsupportedInstanceFieldReason(MetaVersionField field)
	{
		if (field.DeclaringTypeIsValueType)
			return "added-instance-field-on-existing-value-type";
		if (field.HasUnsupportedSidecarType)
			return "added-instance-field-has-pointer-or-byref-type";
		return "unsupported-added-instance-field-on-existing-type";
	}
}
