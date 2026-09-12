# Unity 2022 DHE project trial

This package is based on upstream v8.13.0 and maintained on optimize/v8.13.0.
It includes the full DHE execution plan, Delivery, asset provenance, AOT compiler
and loading workflow. The runtime contract is dhe-runtime-v33. Package 65581c1
was an incomplete rollback and must not be used for DHE trials.

Install through the default Installer. Unity 2022 pins the fork repositories,
HybridCLR v8.13.0-opt5 (b0fe826f071332d109d2bde87c0aa2cc18b9f3c7) and
IL2CPP v2022-8.14.0-opt5 (658aa64923e568a497e11640b340f316704d02f9).
These existing annotated tags are immutable. The IL2CPP tag retains its published
version name; this is not a package upgrade. Package consumers pin a commit,
never a package opt tag. Unity 2021 stays official; Tuanjie selection is unchanged.

Use the synchronized C# tools and locks from hybridclr-lab branch
optimize/dhe-unity2022-project-trial-v8.13.0. See its
docs/DHE-Unity2022-Project-Team-Checklist.md for adapter, Base and Current steps.
Keep hotUpdateAssemblies as the hotfix set and set dheAotAssemblies to that same
set for the initial trial; ordinary AOT assemblies remain outside DHE.
Archive every Base identity. Never mix a package or native runtime from another
combination into a Base, or relabel an older Base to accept a new contract.

Windows trial evidence is reported separately from Android/iOS qualification.
Runtime modifications require rebuilding the Base; existing Players can roll
back only to a compatible archived delivery with a process restart.
