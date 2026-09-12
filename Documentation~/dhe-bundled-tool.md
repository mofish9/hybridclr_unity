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

The tool manifest records its Lab build source and exact payload hashes; the
project source lock records the containing package commit. These identities
serve different purposes. Package/repository locks and device evidence for a
particular qualified build remain build inputs, not baked-in references to an
older package commit. Pass the matching `ValidationSourceRoot` when running
Release qualification. Missing evidence continues to fail the existing gates.
The current distribution is Exploratory, not mobile production-qualified;
`verify-package -RequireRelease` must fail until that qualification is available.

Maintainers rebuild with the Lab C# `publish-unity-tool -LabRoot <clean source>
-OutputRoot <new external directory>` command, copy that verified output into
`Tools~/DHE`, and commit it with the Editor/package change. No runtime tag change
is needed for tooling-only packaging. Never edit bundled files individually.
