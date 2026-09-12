# DHE project integration

The customized package owns DHE current-assembly generation, Base MetaVersion and
BuildIdentity staging, native guard finalization, runtime-plan validation, and
runtime dispatch. A Unity project supplies only resource, signing, Player
output, and device-smoke callbacks.

The required portable CLI ships in this package under `Tools~/DHE`; call it
through `DheToolCommand` as described in [Bundled build tool](dhe-bundled-tool.md).
Do not copy a separate `tools/hybridclr-dhe` source distribution into the project.

## Project-owned files

- `ProjectSettings/HybridCLRSettings.asset`: explicit, equal
  `hotUpdateAssemblies` and `dheAotAssemblies` sets.
- A build adapter: scenes, Player output, resource evidence, signing, and smoke.
- An `IDheRuntimeAssetProvider` for the project's resource framework.
- The generated zero BuildIdentity template under `Assets`.
- One runtime bootstrap call at the existing hot-update load point.
- Project source boundary and release policy under `ProjectSettings/DHE`.
  Installer owns the generated package/runtime identity under `HybridCLRData/DHE`.

The package directory may retain a Unity version suffix such as
`com.code-philosophy.hybridclr@8.13.0`; do not rename it.

## Runtime repository configuration

Approved Unity 2022 upstream baselines are package/HybridCLR 8.13.0 and
IL2CPP `v2022-8.11.0`. An opt release does not authorize an upstream upgrade.
Use `v8.13.0-opt5` and `v2022-8.11.0-opt5`; the earlier
`v2022-8.14.0-opt5` included an unapproved upstream merge and is superseded.
Repository URLs remain project settings. Baseline changes require an explicit
decision and new source-bound validation, not merely a renamed tag.

`Data~/hybridclr_version.json` selects the runtime branch/tag only, following
the existing optimized Unity 2021 convention. Installer repository addresses
come exclusively from this project's `HybridCLRSettings`:

- `hybridclrRepoURL`: the project's HybridCLR fork or mirror.
- `il2cppPlusRepoURL`: the project's IL2CPP fork or mirror.

The configured repositories must contain the selected opt5 tags. Configure the
project to use the appropriate forks before installing; Installer does not
override project settings or silently fall back to another repository. Clone
failures stop installation. Exact commits and source hashes remain in release
locks/build provenance and are checked by the DHE build identity workflow.

## Base Player adapter

After updating the complete package, run ordinary Installer. It creates
`HybridCLRData/DHE/runtime-manifest.json` and `package-lock.json` using the actual
installed native files and the running Editor. `verify-installation` validates
the package, opt5 runtime release, compiler and headers. All Base stages call
this check; a stale/mixed installation requires reinstalling before a new build.
These are generated local records, not substitutes for the immutable Base archive
or the project's package migration commit.

Existing project C# build scripts can use explicit inputs without `-dhe*`
command-line arguments:

```csharp
var result = DheProjectWorkflowRunner.BuildBase(CreateAdapter(),
    new DheProjectWorkflowOptions {
        Target = target,
        OutputRoot = outputRoot,
        BaselineRoot = System.IO.Path.Combine(outputRoot, "baseline"),
        Bootstrap = true,
        EngineWorkflow = "Unity2022Fgs",
        Il2CppCodeGeneration = "OptimizeSize",
    });
```

The complete entry includes preflight/plan generation and the four stages below.
For pipelines spanning Editor invocations, create a context with
`DheProjectWorkflowContext.Create(options)` and call the stage overloads with that
context. The CLI entries parse arguments once and delegate to these same APIs.
Import the generated identity template before entering the build. Keep its owner
in an ordinary AOT assembly, outside `dheAotAssemblies`, including projects whose
hotfix set contains Assembly-CSharp.

New adapters default `GuardOrdinaryAotMethods` to true. This preserves the ordinary
AOT call boundaries required when hotfix type layouts evolve; it does not make
ordinary AOT assemblies hot-updatable. Complete guard coverage is checked during
native finalization. Turning it off is a diagnostic restriction that cannot be
repaired by a later resource update and is not the supported project-trial profile.
The adapter exposes `BeforeCurrentGeneration` / `AfterCurrentGeneration`; the
latter runs in finally so project-specific precompiled inputs can be restored.

`DheProjectWorkflowOptions.AotMetadataAssemblyNames` defaults to an empty set.
Set it explicitly when a Base needs supplemental metadata. The archived Base
records that selection; later resource updates use each Base's selection and
verify the original metadata hashes, regardless of the current project settings.
Do not discard those DLLs when archiving a Base that uses them.

The generated adapter is a starting point for project configuration. Its default
identity path belongs to Assembly-CSharp unless an asmdef changes ownership. If
Assembly-CSharp is hotfix, put the identity and the bootstrap which calls its
internal `Create()` method in an ordinary AOT assembly, then configure
`BuildIdentityAssetPath` and the namespace accordingly. Import this setup before
building; the package must not silently change the project's assembly ownership.

## Current trial qualification

The opt5 tool is distributed as `Exploratory` for packaging trials. Its Release
checks remain enabled and require separate qualification; an opt5 runtime tag
alone does not qualify a project's Player, resources, or target device.

The current package-delivery candidate has not passed its structural Player
gate. Two Windows Bases accept the generated plan but fail while disposing a
`List<T>.Enumerator` after the hotfix value layout changes, with "the old AOT ABI
cannot be used". Generic caller closure fixes an earlier constructor boundary
but does not resolve this remaining invocation boundary. Do not promote this
candidate or infer structural readiness from method-only or managed-host tests.

Structural updates are admitted per immutable Base. Full guards do not imply
unrestricted storage migration: changing the layout of a value stored in an
existing `[ThreadStatic]` field is rejected with
`current-storage-thread-static-value-field`. A newly introduced owner is a
different declaration and can be tested separately; it does not migrate the old
thread slot. Never suppress this diagnostic or relabel a rejected package as
compatible. Keep all supported Base archives and require the same Current to
pass admission for each of them before distribution.

The current execution target is Unity 2022 on Windows. Android, macOS/iOS and
Tuanjie require their own compilation, Player, memory and device checks. The
workflow uses C# and the Editor's .NET host, but Windows results alone do not
establish cross-platform correctness or performance gains.

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

All project-owned precompiled hot-update DLLs used by `Prepare` must be built
for the active target as current-generation inputs. Do not freeze a Windows
conditional-compilation result into an Android/iOS Base; missing target-only
P/Invoke or metadata members cannot be repaired by a later resource update.

Identity staging enumerates the complete stripped AOT DLL inventory, not only
the configured DHE assemblies. It stores normalized `aotAssemblyNames` plus
`aotAssemblySetSha256`, requires the DHE set to be a subset, and repeats the
inventory and supplemental-metadata checks before and after final Player
construction. Do not prune or replace `AssembliesPostIl2CppStrip` between
scripts-only and final build.

`DheBuildPipeline.BuildPlayer` adds `HYBRIDCLR_DHE_BASE_PLAYER` only to the
Base Player compilation. A project may use this symbol for Base-only AOT
contract probes that intentionally reference APIs removed from a later current
assembly; ordinary Editor and current-generation compilation do not receive
that symbol.

`build-identity.json` uses identity v1. Its `baseId` is a composite SHA-256 over
the target, managed DHE set, complete AOT inventory, AOT snapshot,
Base-MetaVersion/AOT-metadata sets, native guard/manifest, runtime
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

Native finalization binds the Player DAG to the exact generated-C++ root. If
Bee returns 4, the package runs the current Editor's PlayerBuildProgram,
stabilizes the graph, reapplies the guards, and performs the final native
build, with an eight-attempt hard limit. Android then resolves the Gradle root
from that DAG's input data, invokes the Editor-owned Java/Gradle distribution,
and rebuilds the requested APK/AAB. `adapter/native-finalize.json` records the
DAG, Gradle root, artifact SHA-256, and aligned Bee source/archive-entry hashes
for every `libil2cpp.so`; the external C# host must revalidate this evidence.
The iOS path uses the same C# Bee state machine. The final export is recorded as
an `ios-xcode-project` artifact and is accepted only when it contains exactly one
`.xcodeproj` with `project.pbxproj`, plus `Classes`, `Libraries`, and `Data`; the
host binds a canonical directory hash to that export. macOS/Xcode compilation,
linking, signing, IPA packaging, and device gates remain separate prerequisites.

## Resource-only update

Later releases call `DheBuildPipeline.GenerateCurrentArtifacts` (or the
package workflow's Prepare phase) to compile one current DLL set. The C# host
`resource-update` command validates that set against all archived online Base
identities and emits one payload:

```text
payload/<assembly>.dll.bytes
payload/<assembly>.mv.bytes
  payload/aot-metadata/<sha256-prefix-128>.bytes # deduplicated blobs; full SHA-256 stays in the plan
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
online Base is an audited registry change; it does not create a second Base-specific
delta payload. For target-specific managed metadata shapes, one resource manifest
may contain multiple current payload variants. Pass
`-CurrentRoot C:/build/current-windows -CurrentVariantId windows` for the primary
variant and `-CurrentVariantRoots {"android":"C:/build/current-android"}` for
additional roots, then set each registry entry's `payloadVariantId` to the
variant consumed by that Base. The runtime plan keeps the variant hash and asset
paths bound to the Base selection, and staging copies only the selected DLL/MV
variant while preserving the same one-package release model. Omitting
`-CurrentVariantId` retains the legacy `default` name for single-target releases.
For each Base, an assembly in its DHE set uses `dhe-differential`; an assembly
absent from its complete AOT inventory may use `interpreter-only`. An assembly
already present in that inventory but outside DHE is rejected. This prevents a
resource update from loading a second managed image for an AOT assembly that
the Base Player has already registered.
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

For consecutive resource releases, keep the archived Base registry and each
Player's embedded Base MetaVersion unchanged. Run `resource-update` again for
the new current DLL roots (one root per variant) and stage the resulting manifest
over the previous resource root; do not promote the previous current DLL/MV into
a new baseline unless a new Base Player is intentionally shipped and added to the
registry. The same Base can consume N and N+1 without changing its embedded files.

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
4. Read every planned current DLL, preserve the project load order, and split
   the records by `DheRuntime.GetAssemblyExecutionMode`. Call
   `DheRuntime.LoadAssemblyImages` once with the `dhe-differential` records;
   then call `DheRuntime.LoadInterpreterAssemblyImage` for each
   `interpreter-only` record. Do this before game logic. The differential
   batch is atomic on the native side, while interpreter-only records are
   authenticated and loaded through the ordinary HybridCLR interpreter path.

The demo's `DheStreamingAssetReader` covers filesystem platforms and Android
APK ZIP entries; a production provider must keep the same relative-path and
integrity semantics when its catalog resolves remote bundles on Android/iOS.

Every Player reads Base MetaVersion from its immutable built-in asset root and compares
it with the current MetaVersion from its selected payload variant. Project code must
not select a per-Base remote delta or reimplement MV parsing, Base identity matching,
transaction retry, changed-method dispatch, or native identity checks.
# DHE linker preservation

When DHE is enabled, the package's UnityLinker callback preserves the complete
configured DHE assembly set. External types referenced by those assemblies are
resolved against the target Player's actual pre-link assemblies, including facade
type forwarding, and their members are preserved in the defining assembly.
Missing roots or unresolved types fail the build before emitting the extra
linker descriptor. Ordinary non-DHE builds keep their existing linker behavior.

`dhePreserveAotAssemblies` retains selected non-DHE AOT assemblies in full, even
when the Base hotfix code does not yet use their types. Its default is `mscorlib`,
`System`, and `System.Core`. Add other future-facing AOT libraries before freezing
the Base. This is a size/build-time tradeoff, not a claim that those assemblies
become hot-updatable. An explicitly empty set disables this extra preservation.

This prevents a `netstandard` facade entry from silently losing members of the
actual `mscorlib` or `System` implementation. It does not restore APIs already
stripped from an archived Base, nor does it preserve every previously unreferenced
external type. Additional future-facing AOT API surface still needs a Base-time
linker policy and validation against that Base's archived AOT inventory.
