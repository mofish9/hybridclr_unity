# Unity 2022 DHE project trial

This package is based on upstream v8.13.0 and maintained on optimize/v8.13.0.
It includes the full DHE execution plan, Delivery, asset provenance, AOT compiler
and loading workflow. The runtime contract is dhe-runtime-v35. Opt5 is retired;
its Players cannot acquire the native receiver fix through a managed update.

Install through the default Installer. Project HybridCLRSettings supplies the repository URLs;
`Data~/hybridclr_version.json` is the only runtime version selection list.
Published annotated runtime tags are immutable. Upstream remains 8.13.0 / IL2CPP 8.11.0.
Package consumers pin an audited distribution commit,
never a package opt tag. Unity 2021 stays official; Tuanjie selection is unchanged.
Export tracked files from that commit (for example, git archive); do not copy a
maintenance worktree containing ignored bin/obj files. Keep the complete Tools~
bundle and record the distribution commit/tree in the project's build manifest.

The C# tool ships in Tools~/DHE. Source and reproducible
tool publication live in ToolsSource~/DHE in this package repository. Lab is for
fixtures and validation evidence; it is not a dependency of project builds.
Users are responsible for running Installer after runtime changes. DHE does not
generate or verify installation receipts. Source evidence for Lab qualification
is supplied explicitly, separately from ordinary project builds.
Keep hotUpdateAssemblies as the hotfix set and set dheAotAssemblies to that same
set for the initial trial; ordinary AOT assemblies remain outside DHE.
Archive every Base identity. Never mix a package or native runtime from another
combination into a Base, or relabel an older Base to accept a new contract.

Windows trial evidence is reported separately from Android/iOS qualification.
Runtime modifications require rebuilding the Base; existing Players can roll
back only to a compatible archived delivery with a process restart.

The v35 candidate keeps guard registries of up to four assemblies on a bounded
short scan. Larger registries cache direct AOT guard token decisions, including
misses, against an immutable publication snapshot. It adds a fixed 64-entry thread cache
(about 3.5 KiB per thread on Windows x64, plus owned long-name buffers). Hits do
not allocate or acquire a shared lock. New publications invalidate both positive
and negative decisions. Production builds keep dispatch diagnostics disabled
and do not include the smoke workload.

This candidate requires a new Base; do not relabel a v34 build identity. To roll
back the implementation, restore the audited v34 package distribution and its
matching native runtime, then rebuild the Base. Resource-only rollback still
requires a compatible archived resource and a fresh process. The candidate
runtime commit in the version list must be published on the maintenance branch
and replaced by its immutable runtime tag before public package delivery.

Lab qualification can pass -ValidationSourceRoot explicitly through
resource-release-qualify. Ordinary project builds do not require a Lab checkout
or an installation receipt. Windows validation is conditional; ARM64 device,
performance, memory and P99 qualification remain separate release gates.
