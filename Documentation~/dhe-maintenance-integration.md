# Unity 2022 DHE project trial

This package is based on upstream v8.13.0 and maintained on optimize/v8.13.0.
It includes the full DHE execution plan, Delivery, asset provenance, AOT compiler
and loading workflow. The runtime contract is dhe-runtime-v34. Opt5 is retired;
its Players cannot acquire the native receiver fix through a managed update.

Install through the default Installer. Project HybridCLRSettings supplies the repository URLs;
the Unity 2022 version list selects
HybridCLR v8.13.0-opt6 (04bd9b5cd5153a31e76815d65bc1a9b5655a5a05) and
IL2CPP v2022-8.11.0-opt6 (57f3a065e84e3b1e7e13c1012752e27f23e4c041).
These annotated tags are immutable. Upstream remains 8.13.0 / IL2CPP 8.11.0.
Package consumers pin an audited distribution commit,
never a package opt tag. Unity 2021 stays official; Tuanjie selection is unchanged.
Export tracked files from that commit (for example, git archive); do not copy a
maintenance worktree containing ignored bin/obj files. Keep the complete Tools~
bundle and record the distribution commit/tree in the project's build manifest.

The C# tool and runtime release lock ship in Tools~/DHE. Source and reproducible
tool publication live in ToolsSource~/DHE in this package repository. Lab is for
fixtures and validation evidence; it is not a dependency of project builds.
Installer generates HybridCLRData/DHE/runtime-manifest.json and package-lock.json
from this machine's actual installation. See dhe-project-integration.md.
Keep hotUpdateAssemblies as the hotfix set and set dheAotAssemblies to that same
set for the initial trial; ordinary AOT assemblies remain outside DHE.
Archive every Base identity. Never mix a package or native runtime from another
combination into a Base, or relabel an older Base to accept a new contract.

Windows trial evidence is reported separately from Android/iOS qualification.
Runtime modifications require rebuilding the Base; existing Players can roll
back only to a compatible archived delivery with a process restart.
