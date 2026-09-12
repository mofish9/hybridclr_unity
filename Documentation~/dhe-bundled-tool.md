# DHE build tool shipped with the package

`Tools~/DHE/HybridCLR.DheTool.dll` is a portable .NET 6 build tool. It is not a
Unity managed plug-in and is not included in Players. The package includes its
dnlib dependency, runtime configuration, validation schemas and templates.
Do not copy Lab `tool/`, test fixtures or historical patches into your project.
Projects do not need an SDK or to rebuild this tool.

From an Editor build adapter:

```csharp
using HybridCLR.Editor.Commands;

DheToolCommand.Run("mv",
    "-Assembly", absoluteDllPath,
    "-Output", absoluteJsonPath,
    "-Binary", absoluteMvPath);
```

`Run` also exposes preflight, batch/plan generation, resource-update, Base
registry, staging and validation operations. Arguments are passed separately,
not concatenated into shell commands. Relative inputs resolve against the Unity
project; internal schemas/templates resolve against the package. Output must
be outside the immutable tool bundle. Nonzero exits and the default five-minute
timeout throw `BuildFailedException`; use `RunWithTimeout` for a different bound.

`HybridCLR > DHE > Verify Bundled Tool` verifies the distribution and host.
The C# entry uses `EditorApplication.applicationContentsPath/NetCoreRuntime/dotnet`
(`dotnet.exe` on Windows). Windows Unity 2022 is the validation target; macOS path
resolution is implemented but still requires a real macOS run.

CI can use Unity `-executeMethod HybridCLR.Editor.Commands.DheToolCommand.RunBatch
-dheToolRequest <absolute JSON path>`. The request is, for example:

```json
{"command":"verify-package","arguments":[]}
```

For a full CLI `workflow` that itself launches Unity, close the project Editor
and invoke the bundled DLL externally using Unity's .NET host or a .NET 6 runtime:

```text
<dotnet-host> <package>/Tools~/DHE/HybridCLR.DheTool.dll help
<dotnet-host> <package>/Tools~/DHE/HybridCLR.DheTool.dll workflow <workflow arguments>
```

The in-Editor API intentionally rejects commands that launch another Editor.
It does not turn the project's ordinary build/loader into DHE: configure the
DHE assembly set and implement the project adapter/provider as described in
`dhe-project-integration.md`.

The tool manifest records its package-repository build source and exact payload hashes; the
project source lock records the containing package commit. These identities
serve different purposes. Package/repository locks and device evidence for a
particular qualified build remain build inputs, not baked-in references to an
older package commit. Pass the matching `ValidationSourceRoot` when running
Release qualification. Missing evidence continues to fail the existing gates.
The current distribution is Exploratory, not mobile production-qualified.
Maintainers can now publish a Release binary distribution with:

```text
dotnet <source-host>/HybridCLR.DheTool.dll publish-unity-tool -PackageSourceRoot <clean committed package> -OutputRoot <new external directory> -Mode Release -ReleaseEvidence <evidence.json>
```

This uses the same complete evidence validation policy, rechecks the package
source HEAD/tree and evidence after compilation, and records the evidence
SHA-256 in build provenance. Missing, failed, incomplete or foreign-source
evidence cannot produce a Release bundle. `ValidationSourceRoot` alone does
not certify an Exploratory bundle. Until real evidence passes and that Release
bundle is distributed, `verify-package -RequireRelease` must continue to fail.

Maintainers first build `ToolsSource~/DHE/HybridCLR.DheTool.csproj` with a .NET 6 SDK.
Then run its `publish-unity-tool -PackageSourceRoot <clean package checkout>
-OutputRoot <new external directory>` command, copy that verified output into
`Tools~/DHE`, and commit the distribution. Test fixtures remain in Lab and are not
copied into the package. No runtime tag change
is needed for tooling-only packaging. Never edit bundled files individually.

Workflow arguments resolve in this order: explicit CLI argument, JSON config,
package default. Defaults do not override a configured `toolchainRoot`.
Output files and directories cannot overlap the executing bundle or its parent
directories, including paths reached through existing symbolic links/junctions.
These checks apply regardless of `-Root` overrides; keep build artifacts outside
the package. This guards configuration mistakes, not concurrent hostile changes
to the filesystem during a build.
