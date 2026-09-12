# DHE tool maintenance source

This is the authoritative build source for the DLL distributed in `Tools~/DHE`.
Unity ignores directories ending in `~`; this source is not a Player assembly.
Projects execute the published DLL and never need an SDK or a Lab checkout.

Build from this repository with a .NET 6 SDK:

```text
dotnet build ToolsSource~/DHE/HybridCLR.DheTool.csproj -c Release
dotnet ToolsSource~/DHE/bin/Release/net6.0/HybridCLR.DheTool.dll publish-unity-tool -PackageSourceRoot <clean package repository> -OutputRoot <new external output>
```

Review and commit source before publication. Copy the verified output into
`Tools~/DHE` and commit that distribution separately. The bundle provenance pins
the preceding source commit; project migration locks pin the final distribution
commit. They are intentionally different identities. Runtime tags need not
change for package-only changes.

Project installation receipts pin the exact installed package tree, bundle ID,
runtime release and compiler. Their `package-build-source` commit records the
bundle's source provenance, not the enclosing distribution commit. Never invent
a migration commit when consuming a vendored copy without Git metadata.

The Lab's fixture projects import these production sources. Lab owns tests,
workloads, historical reports and device qualification. No test fixture is
compiled into this source project. Some maintenance-only CLI commands share
helpers with the project CLI; the `DHE_PACKAGE_TOOL` build admits only its
explicit project command list and cannot run Lab-only commands.

Release publication still requires complete source-bound evidence. This
working release targets Windows Unity 2022 project trials; missing mobile
qualification must not be bypassed by editing the bundle manifest.
