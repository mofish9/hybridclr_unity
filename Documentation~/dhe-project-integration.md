# DHE project integration

The customized package owns DHE assembly generation, runtime-plan staging,
native guard finalization, build identity generation, and runtime validation.
A Unity project supplies only framework-specific callbacks.

## Project-owned files

- `ProjectSettings/HybridCLRSettings.asset`: `dheAotAssemblies` policy.
- A build adapter: scenes, Player build scopes, resource packaging evidence,
  platform output naming, and target smoke launch.
- A runtime asset provider for the project's resource framework.
- A generated build-identity template under `Assets`.
- Runtime bootstrap calls at the existing hot-update load points.

Package lock, source-boundary, and smoke-probe JSON files are project policy
data. Keep them under `ProjectSettings/DHE`; they are not package logic.
Pass `-PackageLockPath` and `-SourceBoundaryPath` explicitly when invoking a
toolchain version whose generated config still uses the legacy Assets path.

## Editor adapter

Create a `DheProjectWorkflowAdapter` and delegate the generic entry points:

```csharp
public static void Prepare()
{
    DheProjectWorkflowRunner.Prepare(CreateAdapter());
}

public static void StageRuntimePlan()
{
    DheProjectWorkflowRunner.StageRuntimePlan(CreateAdapter());
}

public static void BuildScriptsOnly()
{
    DheProjectWorkflowRunner.BuildScriptsOnly(CreateAdapter());
}

public static void BuildFinalPlayer()
{
    DheProjectWorkflowRunner.BuildFinalPlayer(CreateAdapter());
}
```

The adapter must set `ProjectRoot`, `RuntimeAssetRoot`, build identity fields,
scene and Player callbacks. Set the assembly transform, load-order, dependency
map, extra runtime assets, and Player smoke callbacks only when the project
needs them.

Runtime assets contain the current DLL, MV binary, and baseline snapshot hash.
The complete baseline DLL is retained only in the workflow handoff output for
independent validation and must not be collected into the resource package.

Map the project's resource build report to `DheProjectResourceAsset` and
`DheProjectResourceBundle`, then call
`DheProjectResourceSupport.ValidateAndWrite`. The package derives the required
asset set from the runtime plan and writes the standard evidence. Use
`DheProjectSmokeSupport.Run` for the standard headless Player protocol.

The initial Android/iOS Player build can call
`DheBuildPipeline.BuildBootstrapPlayer`. Normal non-DHE builds should call
`DheBuildPipeline.ClearDheRuntimePlanAssets` before collecting legacy hotfix
assets so a previous DHE plan cannot leak into the package.

## Runtime adapter

Implement `IDheRuntimeAssetProvider` for YooAsset, Addressables, StreamingAssets,
or the project's custom resource system. During the existing hot-update load
flow:

1. Call `DheRuntime.Reset` and `DheRuntime.Initialize`.
2. Validate the supplemental AOT metadata list and each metadata payload.
3. Before `Assembly.Load`, call `DheRuntime.LoadAssemblyImage` for assemblies
   selected by `DheRuntime.IsDheAssembly`.

Project code must not reimplement MV parsing, snapshot validation, transaction
retry, changed-method dispatch checks, or native identity convergence. Those
operations belong to this package.
