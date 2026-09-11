# DHE code and asset delivery (candidate)

Build a delivery after producing a validated DHE resource update and rebuilding
the assets for its Current assemblies. `DheDeliveryBuilder.Build(resourceDirectory,
assetFiles, target, engineWorkflow, newOutputDirectory)` copies code and assets into
one new directory and returns the SHA-256 of `dhe-delivery.json`. `assetFiles` maps
project-chosen asset IDs to authored files, including required dependencies/catalogs.
Use the same directory on all supported Bases of that target/engine workflow.

This API does not infer that an arbitrary asset was built with particular code:
the project's build pipeline must supply the corresponding outputs together. It
binds those outputs against later mixing/corruption. It does not implement content
download, signing, save-data migration or engine-specific AssetBundle rebuilding.

Before entering hotfix code, call `DheRuntime.TryPrepareDelivery` with the selected
delivery provider, a provider for embedded Base resources, the embedded identity,
manifest path and the expected manifest SHA-256 supplied by the release selection.
The delivery provider accepts paths relative to the manifest's directory. The Base
provider accepts the existing virtual runtime paths. Only Base MV reads may fall
back to the Base provider; missing Current files are not silently taken from Base.

Preparation validates target, complete file hashes and Current identity, then
configures the existing DHE plan. On success call the returned handle's
`LoadCurrentAssemblies`. This rechecks assets, loads the complete Current batch and
planned AOT supplemental metadata, and publishes asset access only on success.
Use `LoadAssetBytes(id)` on that handle to retrieve verified asset bytes, for example
for `AssetBundle.LoadFromMemory`. Do not bypass the handle with a mutable latest
asset catalog. Keep the selected directory immutable for its lifetime.

Implement `IDheRuntimeStreamingAssetProvider.OpenRead` to hash large files without
loading them wholly into managed memory. The fallback provider loads one file at
a time; explicit asset reads return a byte array and therefore allocate its full
size. Code metadata is retained for consistent configuration. This is a correctness
implementation; delivery memory and throughput have not yet been benchmarked.

A stale handle cannot activate after Reset/reconfiguration. A rejected asset before
native effects can be restored and retried. Native preparation/initialization
failures retain the existing `RestartRequired` semantics. After any committed
native update, use a process restart to select another delivery. An already-built
Base without this package bootstrap API cannot acquire it from a resource update.

Managed tests cover selection and failure behavior; Windows Player qualification
must be recorded against the exact package/runtime identity before project use.
