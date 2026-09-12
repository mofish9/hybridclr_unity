# DHE code and asset delivery (candidate)

Build a delivery after producing a validated DHE resource update and rebuilding
the assets for its Current assemblies. `DheDeliveryBuilder.Build(resourceDirectory,
assetFiles, target, engineWorkflow, newOutputDirectory, assetBuildProvenance)` copies code and assets into
one new directory and returns the SHA-256 of `dhe-delivery.json`. `assetFiles` maps
project-chosen asset IDs to authored files, including required dependencies/catalogs.
Use the same directory on all supported Bases of that target/engine workflow.

First call `DheAssetBuild.Build(currentAssemblyFiles, target, engineWorkflow,
newOutputDirectory, buildCallback)` in the asset-authoring Editor. The callback
receives a fresh output directory and returns asset IDs mapped to built files
inside it. The API checks the loaded Editor assembly files/MVIDs and compares
serialization-relevant declarations to the Current assemblies before calling the
builder; afterward it verifies inputs are unchanged and writes `asset-build.json`.
Pass this record to the delivery builder. Its Current hash, individual assembly
hashes, target and complete asset inventory must match. The record is embedded as
the reserved asset ID `dhe/asset-build.json`.

Method-only Editor/Player differences are allowed. The schema comparison is
conservative: it considers public or explicitly serialized instance fields,
Serializable types, enum values and base/type identity, even for types a particular
bundle might not use. Current assemblies must be loaded in the Editor unambiguously;
Editor-stripped declarations or conditionally different serialized fields are
rejected. This does not limit runtime method hotfixes, but more selective
dependency-based asset schema checking may be needed for some project layouts.

This provenance is produced by a trusted build pipeline, not a signature. The
callback remains responsible for actually rebuilding the listed assets; it must
not copy unrelated pre-existing bundles into the fresh output. The API does not
implement content download, signing, save-data migration or an asset framework.

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
