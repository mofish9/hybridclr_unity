# DHE project integration

The customized package owns DHE current-assembly generation, Base MetaVersion and
BuildIdentity staging, native guard finalization, runtime-plan validation, and
runtime dispatch. A Unity project supplies only resource, signing, Player
output, and device-smoke callbacks.

## Project-owned files

- `ProjectSettings/HybridCLRSettings.asset`: explicit, equal
  `hotUpdateAssemblies` and `dheAotAssemblies` sets.
- A build adapter: scenes, Player output, resource evidence, signing, and smoke.
- An `IDheRuntimeAssetProvider` for the project's resource framework.
- The generated zero BuildIdentity template under `Assets`.
- One runtime bootstrap call at the existing hot-update load point.
- Package lock, source boundary, and release policy under
  `ProjectSettings/DHE`.

The package directory may retain a Unity version suffix such as
`com.code-philosophy.hybridclr@8.13.0`; do not rename it.

## Base Player adapter

Create a `DheProjectWorkflowAdapter` and delegate the generic entry points:

```csharp
public static void Prepare() =>
    DheProjectWorkflowRunner.Prepare(CreateAdapter());

public static void StageRuntimePlan() =>
    DheProjectWorkflowRunner.StageRuntimePlan(CreateAdapter());

public static void BuildScriptsOnly() =>
    DheProjectWorkflowRunner.BuildScriptsOnly(CreateAdapter());

public static void BuildFinalPlayer() =>
    DheProjectWorkflowRunner.BuildFinalPlayer(CreateAdapter());
```

The adapter sets `ProjectRoot`, `RuntimeAssetRoot`, BuildIdentity fields,
scene and Player callbacks. Assembly transform, load order, dependency map,
extra assets, and smoke callbacks are optional project policies.

Run the host with `-Bootstrap -RunPlayer` for a new Base. The scripts-only
phase emits universal guards and stages Base MetaVersion plus a
`state=staged-for-final-player` identity. The final phase compiles the exact
identity and always restores the source template. Archive `baseline/`,
`build-identity.json`, and `native/dhe-native-manifest.json` for every
online Base.

`DheBuildPipeline.BuildPlayer` adds `HYBRIDCLR_DHE_BASE_PLAYER` only to the
Base Player compilation. A project may use this symbol for Base-only AOT
contract probes that intentionally reference APIs removed from a later current
assembly; ordinary Editor and current-generation compilation do not receive
that symbol.

`build-identity.json` uses identity v1. Its `baseId` is a composite SHA-256 over
the target, managed/AOT/Base-MetaVersion/AOT-metadata sets, native guard/manifest, runtime
protocol/contract/capabilities, and runtime asset roots. Do not use an app
version or managed assembly hash as a Base selector.
`runtimeContract` is an immutable runtime implementation release identifier;
every managed or native runtime implementation change must allocate a new
value. Compatibility across those values is decided by protocol and capability
subset, never by treating two implementation builds as the same Base.

Android and iOS use the same complete `DheProjectWorkflowRunner` lifecycle;
building a Player directly is not a bootstrap because it would omit the
runtime plan, universal guards, and BuildIdentity stages. Normal non-DHE builds
must call `DheBuildPipeline.ClearDheRuntimePlanAssets` before collecting legacy
hotfix assets.

## Resource-only update

Later releases call `DheBuildPipeline.GenerateCurrentArtifacts` (or the
package workflow's Prepare phase) to compile one current DLL set. The C# host
`resource-update` command validates that set against all archived online Base
identities and emits one payload:

```text
payload/<assembly>.dll.bytes
payload/<assembly>.mv.bytes
  payload/aot-metadata/<sha256>.bytes   # deduplicated supplemental metadata blobs
dhe-runtime-plan.json
dhe-resource-update.json
dhe-resource-update-validation.json
```

`PrepareProjectArtifacts` brackets a project-owned current-input callback
with an `AfterCurrentGeneration` callback in `finally`. Projects that swap
precompiled DHE inputs must restore the Base-compatible inputs there so a
failed generation cannot leave the Unity project uncompilable.

No Base DLL or Base MetaVersion is copied into this payload. Use the host
`stage-resource-update` command before the project's YooAsset, Addressables,
or custom catalog build, and pass the exact Player archive's
`build-identity.json` with `-BaseBuildIdentity`. It validates the identity
schema, composite `baseId`, and file SHA, selects that exact `supportedBases`
record, and checks every embedded Base MetaVersion against it. This remains
unambiguous when two Player builds share the same Base MetaVersion set but have
different runtime/native identities. It also proves that the embedded tree and
optional Player/GameAssembly files did not change.
The manifest binds `dhe-runtime-plan.json` with `runtimePlanSha256`; staging validates
that hash and every current DLL, MetaVersion, and optional supplemental AOT metadata
payload before copying. Missing or modified bytes reject the whole update.
`DheProjectResourceSupport.ValidateAndWrite` remains the structured evidence
boundary for a project-owned resource build.

The host derives `runtimeAssetRoot` and `baseMetaVersionAssetRoot` from the
archived identities and requires every supported Base to agree. It also derives
`requiredRuntimeCapabilities` independently for each Base. Runtime build labels
may differ under `dhe-runtime-protocol-v1`; a Base is accepted only
when its declared capability set contains every capability required by its own
Base-to-current diff. Keep every still-supported production Base in the command
input. Omitting an old Base is equivalent to ending support for it.
For repeatable releases, store that set in a
`hybridclr.dhe-base-registry.json` file and pass `-BaseRegistry` to the host.
Use `registry-relative-v1` paths when the registry travels with the archived
Base records. The host rejects duplicate Base IDs, unsupported engine workflows,
mixed registry/parallel arguments, and identity/baseId mismatches, then records
the registry SHA-256 and entry count in the resource manifest. Adding a new
online Base is an audited registry change; it does not create a second current
DLL/MV payload.
Every resource update requires `resource-update-plan-integrity-v1` and
`resource-update-aot-metadata-set-selection-v1`; updates carrying non-empty
supplemental metadata also require `resource-update-aot-metadata-path-v1`. The
Base runtime must advertise `stable-method-identity-v1`; this capability binds
the current-to-Base method mapping fix into the Base identity and prevents an
older runtime from being treated as equivalent. The
metadata set identity is part of BuildIdentity/baseId. A resource plan contains
one content-addressed `aotMetadataSets` entry per distinct Base metadata set and
maps every `baseId` through `baseSelections`; a Player loads only the set selected
for its own identity. A Player built with an older managed runtime or without this
identity/capability is rejected instead of being treated as an equivalent Base.

## Runtime adapter

Implement `IDheRuntimeAssetProvider` for the resource system. During the
existing hot-update load flow:

1. Construct the compile-time `DheRuntimeIdentity` from the generated identity
   class.
2. Call `DheRuntime.Reset`, then
   `DheRuntime.InitializeFromResourceUpdate(provider, identity,
   manifestAssetPath, out error)`.
3. Call `DheRuntime.LoadAotMetadataImages`; it resolves paths from the validated
   runtime plan, verifies every payload before the first native load, and then loads
   the complete supplemental metadata set.
4. Read every planned current DLL, preserve the project load order, and call
   `DheRuntime.LoadAssemblyImages` once before any ordinary `Assembly.Load`
   or game logic. The batch call publishes the complete DHE set atomically.

Every Player reads Base MetaVersion from its immutable built-in asset root and compares
it with the one remote current MetaVersion. Project code must not select a per-Base
remote delta or reimplement MV parsing, Base identity matching, transaction
retry, changed-method dispatch, or native identity checks.
