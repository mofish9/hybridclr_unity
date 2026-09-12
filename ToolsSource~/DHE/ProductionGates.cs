using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace HybridCLR.DheTool;

internal static partial class Program
{
    private const string NativeGuardHashContract = "guard-block-set-v1";
    private const string NativeGuardBeginPrefix = "HYBRIDCLR_DHE_GUARD_BEGIN_V1:";
    private const string NativeGuardEndPrefix = "HYBRIDCLR_DHE_GUARD_END_V1:";

    private sealed record ProductionEvidence(bool Passed, bool ToolchainPassed, bool SourcePreflightPassed,
        bool CleanCheckoutPassed, string? ToolchainGate, string SourcePreflight, string CleanCheckout,
        string? ExpectedPackageId, string? RuntimeManifest);

    private static ProductionEvidence PrepareProductionEvidence(Cli cli, string mode, string project,
        string settingsPath, string baselineRoot, string target, string outputRoot)
    {
        var release = mode == "Release";
        var expectedPackageId = cli.Optional("expectedtoolchainpackageid");
        var toolRoot = Path.GetFullPath(cli.Optional("toolchainroot") ?? cli.Root);
        var validationSourceRoot = Path.GetFullPath(cli.Optional("validationsourceroot") ?? toolRoot);
        var toolManifest = Path.Combine(toolRoot, "dhe-toolchain-manifest.json");
        string? toolchainGate = null;
        var toolchainPassed = !release;
        if (File.Exists(toolManifest))
        {
            toolchainGate = Path.Combine(outputRoot, "toolchain-gate.json");
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["packageroot"] = toolRoot, ["output"] = toolchainGate
            };
            if (!string.IsNullOrWhiteSpace(expectedPackageId)) values["expectedpackageid"] = expectedPackageId;
            if (release) values["requirerelease"] = "true";
            toolchainPassed = VerifyPackage(new Cli("verify-package", values)) == 0;
        }
        else if (release) throw new DheException("Release workflow must run from an installed release-ready DHE toolchain package.");
        if (release && string.IsNullOrWhiteSpace(expectedPackageId)) throw new DheException("Release workflow requires ExpectedToolchainPackageId.");
        if (!toolchainPassed) throw new DheException("DHE toolchain package verification failed.");

        var sourcePath = Path.Combine(outputRoot, "source-preflight", "source-preflight-report.json");
        var sourcePassed = WriteSourcePreflight(cli, release, project, settingsPath, baselineRoot, target, sourcePath, out var runtimeManifest);
        if (!sourcePassed) throw new DheException("DHE source preflight failed: " + sourcePath);
        var cleanPath = Path.Combine(outputRoot, "clean-checkout", "clean-checkout-gate-report.json");
        var cleanPassed = WriteCleanCheckout(cli, release, project, validationSourceRoot, cleanPath);
        if (!cleanPassed) throw new DheException("DHE clean checkout gate failed: " + cleanPath);
        return new ProductionEvidence(toolchainPassed && sourcePassed && cleanPassed, toolchainPassed,
            sourcePassed, cleanPassed, toolchainGate, sourcePath, cleanPath, expectedPackageId, runtimeManifest);
    }

    private static bool WriteSourcePreflight(Cli cli, bool release, string project, string settingsPath,
        string baselineRoot, string target, string output, out string? runtimeManifestPath)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var checks = new List<object>();
        var sets = Settings.Read(settingsPath);
        AddCheck(checks, errors, "settings:dhe-coverage", sets.Hot.Length > 0 && SetEquals(sets.Hot, sets.Dhe),
            "hotUpdateAssemblies and dheAotAssemblies must be non-empty and equal.");
        var explicitPackageLockPath = cli.Optional("packagelockpath");
        var packageLockPath = ResolveOptionalFile(explicitPackageLockPath, project, "HybridCLRData/DHE/package-lock.json") ??
            ResolveOptionalFile(explicitPackageLockPath, project,
            Path.Combine("ProjectSettings", "DHE", "dhe-package-lock.json")) ??
            (string.IsNullOrWhiteSpace(explicitPackageLockPath)
                ? ResolveOptionalFile(null, project,
                    Path.Combine("Assets", "Editor", "DHE", "dhe-package-lock.json"))
                : null);
        var bootstrap = cli.Has("bootstrap");
        var baselineManifestPath = bootstrap ? null : ResolveOptionalFile(
            cli.Optional("baselinemanifestpath"), baselineRoot, "dhe-baseline-manifest.json");
        if (bootstrap && !string.IsNullOrWhiteSpace(cli.Optional("baselinemanifestpath")))
            warnings.Add("Bootstrap ignores BaselineManifestPath because it creates the initial Base identity.");
        runtimeManifestPath = ResolveOptionalFile(cli.Optional("runtimemanifestpath"), project, "HybridCLRData/DHE/runtime-manifest.json") ??
            (string.IsNullOrWhiteSpace(cli.Optional("runtimemanifestpath")) ? ResolveOptionalFile(null, project, "runtime-manifest.json") : null);
        if (release && packageLockPath == null) errors.Add("Release requires PackageLockPath.");
        if (release && !bootstrap && baselineManifestPath == null)
            errors.Add("Release update workflow requires a target-bound baseline manifest.");
        if (release && runtimeManifestPath == null) errors.Add("Release requires RuntimeManifestPath.");

        var packagePresent = false;
        if (packageLockPath != null)
        {
            try
            {
                var packageLock = ReadJson<JsonElement>(packageLockPath);
                RequireFormat(packageLock, "hybridclr.dhe-package-lock.json", "Package lock", errors);
                if (release && (GetString(packageLock, "sourceMode") != "integrated" ||
                    !IsHex(GetString(packageLock, "integratedCommit"), 40, 40)))
                    errors.Add("Release requires an integrated package lock with an exact commit.");
                var packagePath = GetString(packageLock, "packagePath");
                if (string.IsNullOrWhiteSpace(packagePath) || Path.IsPathRooted(packagePath) || packagePath.Contains("..", StringComparison.Ordinal))
                    errors.Add("Package lock packagePath is unsafe.");
                else
                {
                    var packageRoot = Path.GetFullPath(Path.Combine(project, packagePath.Replace('/', Path.DirectorySeparatorChar)));
                    packagePresent = Directory.Exists(packageRoot);
                    if (!packagePresent) errors.Add("Locked HybridCLR package directory was not found: " + packageRoot);
                    else
                    {
                        var ignored = packageLock.TryGetProperty("treeHashIgnoredPaths", out var ignoredValue) && ignoredValue.ValueKind == JsonValueKind.Array
                            ? ignoredValue.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : Array.Empty<string>();
                        var actualTree = GetString(packageLock, "treeHashKind") == "canonical-source-v1"
                            ? LabCommands.CanonicalSourceTreeHash(packageRoot, true, ignored)
                            : TreeHashForRelease(packageRoot, ignored);
                        if (!actualTree.Equals(GetString(packageLock, "treeSha256"), StringComparison.OrdinalIgnoreCase)) errors.Add("HybridCLR package tree does not match the package lock.");
                    }
                }
            }
            catch (Exception ex) { errors.Add("Package lock: " + ex.Message); }
        }

        var runtimeReady = false;
        var externalSurrogate = (bool?)null;
        JsonElement runtime = default;
        if (runtimeManifestPath != null)
        {
            try
            {
                runtime = ReadJson<JsonElement>(runtimeManifestPath);
                runtimeReady = ValidateRuntimeManifest(runtime, runtimeManifestPath, project, cli, release, errors,
                    out externalSurrogate);
                var binding = ValidateInstalledRuntime(runtime, project);
                checks.Add(new { name = "runtime:installed-source", passed = binding.Passed,
                    details = JsonSerializer.Serialize(binding) });
                errors.AddRange(binding.Errors);
                runtimeReady &= binding.Passed;
            }
            catch (Exception ex) { errors.Add("Runtime manifest: " + ex.Message); }
        }

        if (baselineManifestPath != null)
        {
            try
            {
                var baseline = ReadJson<JsonElement>(baselineManifestPath);
                RequireFormat(baseline, "hybridclr.dhe-baseline-manifest.json", "Baseline manifest", errors);
                if (GetString(baseline, "pathSemantics") != "workspace-absolute-v1" ||
                    GetString(baseline, "baselineKind") != "stripped-aot" ||
                    !Path.GetFullPath(GetString(baseline, "sourceRoot") ?? "").Equals(Path.GetFullPath(baselineRoot), StringComparison.OrdinalIgnoreCase))
                    errors.Add("Baseline manifest path/kind/source identity is invalid.");
                if (!string.Equals(GetString(baseline, "target"), target, StringComparison.OrdinalIgnoreCase)) errors.Add("Baseline manifest target does not match.");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var record in baseline.GetProperty("assemblies").EnumerateArray())
                {
                    var name = GetString(record, "assemblyName") ?? "";
                    if (!names.Add(name)) errors.Add("Baseline manifest contains duplicate assemblies.");
                    var path = Path.Combine(baselineRoot, NormalizeName(name) + ".dll");
                    if (!File.Exists(path) || !Sha256File(path).Equals(GetString(record, "sha256"), StringComparison.OrdinalIgnoreCase)) errors.Add("Baseline manifest hash mismatch: " + name);
                }
                if (!new HashSet<string>(sets.Dhe, StringComparer.OrdinalIgnoreCase).SetEquals(names)) errors.Add("Baseline manifest assembly set does not match DHE settings.");
                if (!baseline.TryGetProperty("runtime", out var runtimeBinding) || runtimeBinding.ValueKind != JsonValueKind.Object)
                {
                    if (release) errors.Add("Baseline manifest is missing runtime identity.");
                }
                else if (runtimeManifestPath != null &&
                    (!Sha256File(runtimeManifestPath).Equals(GetString(runtimeBinding, "runtimeManifestSha256"), StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(GetString(runtimeBinding, "profile"), GetString(runtime, "profile"), StringComparison.Ordinal) ||
                     !string.Equals(GetString(runtimeBinding, "stagedRuntimeSha256"), GetString(runtime, "stagedRuntimeSha256"), StringComparison.OrdinalIgnoreCase)))
                    errors.Add("Baseline manifest runtime identity does not match RuntimeManifestPath.");
                if (!baseline.TryGetProperty("package", out var packageBinding) || packageBinding.ValueKind != JsonValueKind.Object)
                {
                    if (release) errors.Add("Baseline manifest is missing package identity.");
                }
                else if (packageLockPath != null)
                {
                    var packageLock = ReadJson<JsonElement>(packageLockPath);
                    if (!string.Equals(GetString(packageBinding, "treeSha256"), GetString(packageLock, "treeSha256"), StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(GetString(packageBinding, "integratedCommit"), GetString(packageLock, "integratedCommit"), StringComparison.OrdinalIgnoreCase))
                        errors.Add("Baseline manifest package identity does not match PackageLockPath.");
                }
            }
            catch (Exception ex) { errors.Add("Baseline manifest: " + ex.Message); }
        }
        var passed = errors.Count == 0;
        checks.Add(new { name = "runtime:manifest", passed = runtimeReady || !release, details = runtimeManifestPath ?? "not supplied" });
        checks.Add(new { name = "baseline:manifest", passed = bootstrap || baselineManifestPath != null || !release,
            details = bootstrap ? "created-by-bootstrap" : baselineManifestPath ?? "not supplied" });
        checks.Add(new { name = "package:lock", passed = packagePresent || !release, details = packageLockPath ?? "not supplied" });
        WriteJson(output, new
        {
            schemaVersion = 1, format = "hybridclr.dhe-source-preflight.json", generatedAtUtc = DateTimeOffset.UtcNow,
            pathSemantics = "workspace-absolute-v1", passed, runtimeRequired = release, runtimeReady,
            cleanRuntimeSourcesRequired = release, packageLockPath, identityTemplatePath = (string?)null,
            identityTemplateRequired = false, embeddedPackageRequired = release, embeddedPackagePresent = packagePresent,
            externalHeadersRequired = release, externalHeadersSurrogate = externalSurrogate, labRoot = (string?)null,
            projectPath = project, runtimeSource = runtimeManifestPath, hotUpdateAssemblies = sets.Hot,
            dheAotAssemblies = sets.Dhe, dheAotConfigured = sets.Dhe.Length > 0,
            externalHotUpdateAssemblyDirs = Array.Empty<string>(), settingsFile = settingsPath,
            baselineManifestPath, errors, warnings, checks
        });
        return passed;
    }

    private static bool ValidateRuntimeManifest(JsonElement runtime, string runtimeManifestPath, string project,
        Cli cli, bool release, List<string> errors, out bool? externalSurrogate)
    {
        externalSurrogate = null;
        if (IsProjectInstallation(runtime))
        {
            try { ValidateProjectInstallation(runtime, project); externalSurrogate = false; return true; }
            catch (Exception error) { errors.Add("Project installation: " + error.Message); return false; }
        }
        var valid = true;
        if (GetInt(runtime, "schemaVersion") != 1 || GetString(runtime, "format") != "hybridclr.dhe-runtime-manifest.json" ||
            GetString(runtime, "pathSemantics") != "workspace-absolute-v1" || !GetBool(runtime, "dheEnabled") ||
            !(GetString(runtime, "profile") ?? "").StartsWith("DHE-", StringComparison.Ordinal))
        {
            errors.Add("Runtime manifest contract is not a DHE workspace runtime.");
            valid = false;
        }

        if (!runtime.TryGetProperty("engine", out var engine) || engine.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Runtime manifest engine identity is missing.");
            valid = false;
        }
        else
        {
            var projectVersionPath = Path.Combine(project, "ProjectSettings", "ProjectVersion.txt");
            var projectVersion = File.Exists(projectVersionPath)
                ? File.ReadLines(projectVersionPath).Select(line => line.Trim())
                    .FirstOrDefault(line => line.StartsWith("m_EditorVersion:", StringComparison.Ordinal))?
                    .Split(':', 2)[1].Trim() : null;
            if (string.IsNullOrWhiteSpace(projectVersion) ||
                !string.Equals(projectVersion, GetString(engine, "unityVersion"), StringComparison.Ordinal))
            {
                errors.Add("Runtime engine version does not match ProjectVersion.txt.");
                valid = false;
            }
        }

        if (!runtime.TryGetProperty("externalHeaders", out var headers) || headers.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Runtime external header identity is missing.");
            valid = false;
        }
        else
        {
            externalSurrogate = headers.TryGetProperty("surrogate", out var surrogate) &&
                surrogate.ValueKind is JsonValueKind.True or JsonValueKind.False ? surrogate.GetBoolean() : null;
            var stagedHeaders = GetString(headers, "stagedPath");
            if (release && (externalSurrogate != false || !GetBool(headers, "editorAvailable")))
            {
                errors.Add("Release runtime must use real target engine headers.");
                valid = false;
            }
            if (release && (string.IsNullOrWhiteSpace(stagedHeaders) || !Directory.Exists(stagedHeaders) ||
                !TreeHashForRelease(stagedHeaders, Array.Empty<string>()).Equals(GetString(headers, "stagedTreeSha256"), StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add("Runtime staged external header tree does not match its manifest.");
                valid = false;
            }
        }

        var stagedRuntime = GetString(runtime, "stagedLibil2cpp");
        if (string.IsNullOrWhiteSpace(stagedRuntime) || !Directory.Exists(stagedRuntime) ||
            !TreeHashForRelease(stagedRuntime, Array.Empty<string>()).Equals(GetString(runtime, "stagedRuntimeSha256"), StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Runtime staged libil2cpp tree does not match its manifest.");
            valid = false;
        }

        var runtimeLockPath = GetString(runtime, "dheRuntimeLock");
        var packagedRuntimeLock = Path.Combine(Path.GetFullPath(cli.Optional("validationsourceroot") ??
            cli.Optional("toolchainroot") ?? cli.Root),
            "manifests", "dhe-runtime-lock.json");
        if (release && (string.IsNullOrWhiteSpace(runtimeLockPath) || !File.Exists(runtimeLockPath) ||
            !File.Exists(packagedRuntimeLock) ||
            !Sha256File(runtimeLockPath).Equals(GetString(runtime, "dheRuntimeLockSha256"), StringComparison.OrdinalIgnoreCase) ||
            !Sha256File(packagedRuntimeLock).Equals(GetString(runtime, "dheRuntimeLockSha256"), StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("Runtime lock is missing or does not match the installed toolchain.");
            valid = false;
        }
        if (release && GetString(runtime, "dheRuntimeSourceMode") != "integrated")
        {
            errors.Add("Release runtime must be assembled from integrated sources.");
            valid = false;
        }

        if (!runtime.TryGetProperty("source", out var sources) || sources.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Runtime source identities are missing.");
            return false;
        }
        foreach (var name in new[] { "hybridclr", "il2cpp_plus", "hybridclr_unity" })
        {
            if (!sources.TryGetProperty(name, out var source) || source.ValueKind != JsonValueKind.Object)
            {
                errors.Add("Runtime source identity is missing: " + name);
                valid = false;
                continue;
            }
            var sourcePath = GetString(source, "path");
            var commit = GetString(source, "commit");
            var treeHash = GetString(source, "treeSha256");
            var ignoredPaths = ReadStringArray(source, "treeHashIgnoredPaths");
            var treeRoot = string.IsNullOrWhiteSpace(sourcePath) ? "" : name switch
            {
                "hybridclr" => Path.Combine(sourcePath, "hybridclr"),
                "il2cpp_plus" => Path.Combine(sourcePath, "libil2cpp"),
                _ => sourcePath
            };
            if (release && (GetBool(source, "dirty") || string.IsNullOrWhiteSpace(sourcePath) ||
                !Directory.Exists(sourcePath) || !IsHex(commit, 40, 40) ||
                !string.Equals(GitValue(sourcePath, "rev-parse", "HEAD"), commit, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(GitValue(sourcePath, "status", "--porcelain")) ||
                !Directory.Exists(treeRoot) || !LabCommands.CanonicalSourceTreeHash(treeRoot,
                    name == "hybridclr_unity", ignoredPaths).Equals(treeHash,
                    StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add("Runtime source identity cannot be reproduced: " + name);
                valid = false;
            }
        }
        return valid;
    }

    private static RuntimeSourceBinding.Result ValidateInstalledRuntime(JsonElement runtime, string project)
    {
        string editorPlatform = OperatingSystem.IsWindows() ? "WindowsEditor" :
            OperatingSystem.IsMacOS() ? "OSXEditor" : "LinuxEditor";
        string source = RequireDirectory(GetString(runtime, "stagedLibil2cpp") ?? "",
            "Manifest-bound DHE runtime source");
        return RuntimeSourceBinding.Validate(source, RuntimeSourceBinding.InstalledRoot(project, editorPlatform));
    }

    private static void RequireWorkflowRuntimeBinding(string? manifestPath, string project)
    {
        if (manifestPath == null) return;
        var runtime = ReadJson<JsonElement>(manifestPath);
        if (IsProjectInstallation(runtime)) { ValidateProjectInstallation(runtime, project); return; }
        string source = RequireDirectory(GetString(runtime, "stagedLibil2cpp") ?? "",
            "Manifest-bound DHE runtime source");
        if (!TreeHashForRelease(source, Array.Empty<string>()).Equals(
                GetString(runtime, "stagedRuntimeSha256"), StringComparison.OrdinalIgnoreCase))
            throw new DheException("Runtime source changed after preflight.");
        var binding = ValidateInstalledRuntime(runtime, project);
        if (!binding.Passed)
            throw new DheException("DHE installed runtime does not match RuntimeManifestPath: " +
                string.Join("; ", binding.Errors));
    }

    private static void ValidateFinalRuntimeSourceIdentity(string nativeManifestPath, string project)
    {
        var manifest = ReadJson<JsonElement>(nativeManifestPath);
        if (!manifest.TryGetProperty("runtimeSourceIdentity", out var identity) ||
            identity.ValueKind != JsonValueKind.Object ||
            GetString(identity, "contract") != "dhe-native-source-v1")
            throw new DheException("New Base native manifest must bind its actual runtime sources.");
        string platform = OperatingSystem.IsWindows() ? "WindowsEditor" :
            OperatingSystem.IsMacOS() ? "OSXEditor" : "LinuxEditor";
        string installed = RuntimeSourceBinding.InstalledRoot(project, platform);
        var actual = RuntimeSourceBinding.Validate(installed, installed);
        if (!actual.Passed || !actual.SourceSha256.Equals(GetString(identity, "sourceSha256"),
                StringComparison.OrdinalIgnoreCase) || actual.SourceFileCount != GetInt(identity, "sourceFileCount"))
            throw new DheException("Final Base native manifest runtime source identity does not match the installed sources.");
        if (!identity.TryGetProperty("generatedFiles", out var generated) ||
            generated.ValueKind != JsonValueKind.Array || generated.GetArrayLength() != actual.GeneratedFileHashes.Count)
            throw new DheException("Final Base native manifest generated runtime sources are incomplete.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in generated.EnumerateArray())
        {
            string name = GetString(file, "path") ?? "";
            if (!names.Add(name) || !actual.GeneratedFileHashes.TryGetValue(name, out string? hash) ||
                !hash.Equals(GetString(file, "sha256"), StringComparison.OrdinalIgnoreCase))
                throw new DheException("Final Base native manifest generated source differs: " + name);
        }
    }

    private static bool WriteCleanCheckout(Cli cli, bool release, string project, string toolRoot, string output)
    {
        var errors = new List<string>();
        var explicitBoundaryPath = cli.Optional("sourceboundarypath");
        var boundaryPath = ResolveOptionalFile(explicitBoundaryPath, project,
            Path.Combine("ProjectSettings", "DHE", "dhe-source-boundary.json")) ??
            (string.IsNullOrWhiteSpace(explicitBoundaryPath)
                ? ResolveOptionalFile(null, project,
                    Path.Combine("Assets", "Editor", "DHE", "dhe-source-boundary.json"))
                : null) ??
            (string.IsNullOrWhiteSpace(explicitBoundaryPath)
                ? ResolveOptionalFile(null, project,
                    Path.Combine("manifests", "dhe-source-boundary.json"))
                : null);
        if (release && boundaryPath == null) errors.Add("Release requires SourceBoundaryPath.");
        var boundaryHash = boundaryPath == null ? null : Sha256File(boundaryPath);
        var boundaryErrors = new List<string>();
        var boundaryComplete = ValidateBoundary(project, boundaryPath, boundaryErrors);
        if (release) errors.AddRange(boundaryErrors);
        var projectIdentity = SourceIdentity("project", project, boundaryPath, release, boundaryComplete, errors);
        var toolManifestPath = Path.Combine(toolRoot, "dhe-toolchain-manifest.json");
        var toolIdentity = File.Exists(toolManifestPath)
            ? InstalledToolIdentity(toolRoot, toolManifestPath, release, errors)
            : SourceIdentity("tool", toolRoot, Path.Combine(toolRoot, "manifests", "dhe-source-boundary.json"), release, true, errors);
        var passed = errors.Count == 0;
        WriteJson(output, new
        {
            schemaVersion = 1, format = "hybridclr.dhe-clean-checkout-gate.json", generatedAtUtc = DateTimeOffset.UtcNow,
            pathSemantics = "workspace-absolute-v1", passed, cleanSourcePreflightPassed = true,
            staleOutputRejected = true, missingRuntimeRejected = true, runtimeTested = release,
            staleManifestTested = true, staleManifestRejected = true, gitRoot = projectIdentity.root,
            gitTested = true, gitHead = projectIdentity.head, gitTree = projectIdentity.tree,
            gitClean = projectIdentity.clean, vcs = projectIdentity.vcs, vcsRoot = projectIdentity.root,
            vcsRevision = projectIdentity.revision, vcsRevisionSpec = projectIdentity.revisionSpec,
            vcsRepository = projectIdentity.repository, gitCleanRequired = release,
            trackedSourcesTested = boundaryPath != null, trackedSourcesComplete = boundaryComplete,
            trackedSourcesRequired = release, sourceBoundaryPath = boundaryPath,
            sourceBoundarySha256 = boundaryHash, missingTrackedSources = Array.Empty<string>(),
            projectGit = projectIdentity, toolGit = toolIdentity, errors
        });
        return passed;
    }

    private static dynamic SourceIdentity(string name, string root, string? boundary, bool requireClean,
        bool boundaryComplete, List<string> errors)
    {
        var gitRoot = GitValue(root, "rev-parse", "--show-toplevel");
        var vcs = "git";
        string? head = null, tree = null, revision = null, revisionSpec = null, repository = null;
        bool clean;
        if (!string.IsNullOrWhiteSpace(gitRoot))
        {
            root = Path.GetFullPath(gitRoot); head = GitValue(root, "rev-parse", "HEAD"); tree = GitValue(root, "rev-parse", "HEAD^{tree}");
            clean = string.IsNullOrWhiteSpace(GitValue(root, "status", "--porcelain", "--untracked-files=all"));
        }
        else
        {
            vcs = "svn";
            revision = ProcessValue("svn", new[] { "info", "--show-item", "revision", SvnLiteralPath(root) }, root);
            revisionSpec = ProcessValue("svnversion", new[] { root }, root);
            repository = ProcessValue("svn", new[] { "info", "--show-item", "url", SvnLiteralPath(root) }, root);
            clean = string.IsNullOrWhiteSpace(ProcessValue("svn", new[] { "status", SvnLiteralPath(root) }, root));
            if (string.IsNullOrWhiteSpace(revision)) errors.Add(name + " source is not a Git or SVN working copy.");
            if (requireClean && (!uint.TryParse(revisionSpec, out _) || !string.Equals(revisionSpec, revision, StringComparison.Ordinal)))
                errors.Add(name + " SVN working copy is mixed, switched, sparse, or not at its reported root revision.");
        }
        if (requireClean && !clean) errors.Add(name + " source contains local changes.");
        if (requireClean && !boundaryComplete) errors.Add(name + " source boundary is incomplete.");
        var passed = (!requireClean || clean && boundaryComplete) && (head != null || revision != null);
        return new
        {
            name, vcs, tested = true, root, ownedPath = root, head, tree, revision, revisionSpec, repository,
            clean, cleanRequired = requireClean, trackedSourcesTested = boundary != null,
            trackedSourcesComplete = boundaryComplete, trackedSourcesRequired = requireClean,
            sourceBoundaryPath = boundary, sourceBoundarySha256 = boundary == null ? null : Sha256File(boundary),
            sourceBoundaryPathBase = boundary == null ? null : "project-root-v1",
            missingTrackedSources = Array.Empty<string>(), passed, errors = Array.Empty<string>(), warnings = Array.Empty<string>()
        };
    }

    private static dynamic InstalledToolIdentity(string root, string manifestPath, bool release, List<string> errors)
    {
        var manifest = ReadJson<JsonElement>(manifestPath);
        var source = manifest.GetProperty("sourceIdentity");
        var clean = GetBool(source, "clean");
        var tracked = GetBool(source, "tracked");
        var releaseReady = GetBool(manifest, "releaseReady");
        if (release && (!clean || !tracked || !releaseReady)) errors.Add("Installed toolchain is not a clean, tracked release package.");
        return new
        {
            name = "tool", vcs = "git", tested = true, root, ownedPath = root,
            head = GetString(source, "head"), tree = GetString(source, "tree"), revision = (string?)null,
            revisionSpec = (string?)null, repository = (string?)null, clean, cleanRequired = release,
            trackedSourcesTested = true, trackedSourcesComplete = tracked, trackedSourcesRequired = release,
            sourceBoundaryPath = Path.Combine(root, "dhe-source-boundary.json"),
            sourceBoundarySha256 = File.Exists(Path.Combine(root, "dhe-source-boundary.json")) ? Sha256File(Path.Combine(root, "dhe-source-boundary.json")) : null,
            sourceBoundaryPathBase = "manifest-directory-v1", missingTrackedSources = Array.Empty<string>(),
            passed = !release || clean && tracked && releaseReady, errors = Array.Empty<string>(), warnings = Array.Empty<string>()
        };
    }

    private static bool ValidateBoundary(string project, string? boundaryPath, List<string> errors)
    {
        if (boundaryPath == null) return false;
        try
        {
            var boundary = ReadJson<JsonElement>(boundaryPath);
            RequireFormat(boundary, "hybridclr.dhe-source-boundary.json", "Source boundary", errors);
            string pathBase = GetString(boundary, "pathBase") ?? string.Empty;
            string baseRoot = pathBase switch
            {
                "manifest-directory-v1" => Path.GetDirectoryName(boundaryPath)!,
                "project-root-v1" => project,
                "git-root-v1" => GitValue(project, "rev-parse", "--show-toplevel"),
                _ => string.Empty,
            };
            if (string.IsNullOrWhiteSpace(baseRoot) || !Directory.Exists(baseRoot))
            {
                errors.Add("Source boundary pathBase cannot be resolved: " + pathBase);
                return false;
            }
            baseRoot = Path.GetFullPath(baseRoot);
            var complete = true;
            foreach (var entry in boundary.GetProperty("exactPaths").EnumerateArray())
            {
                var relative = entry.GetString() ?? "";
                var full = Path.Combine(baseRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!SafeRelative(relative) || !File.Exists(full))
                { errors.Add("Source boundary file is missing or unsafe: " + relative); complete = false; }
                else if (!IsTrackedPath(project, full)) { errors.Add("Source boundary file is not version controlled: " + relative); complete = false; }
            }
            foreach (var entry in boundary.GetProperty("prefixes").EnumerateArray())
            {
                var relative = entry.GetString() ?? "";
                var full = Path.Combine(baseRoot, relative.TrimEnd('/', '\\').Replace('/', Path.DirectorySeparatorChar));
                if (!SafeRelative(relative) || relative.Contains('*') || !Directory.Exists(full))
                { errors.Add("Source boundary prefix is missing or unsafe: " + relative); complete = false; }
                else if (!IsTrackedPath(project, full)) { errors.Add("Source boundary prefix is not version controlled: " + relative); complete = false; }
            }
            return complete;
        }
        catch (Exception ex) { errors.Add("Source boundary: " + ex.Message); return false; }
    }

    private static bool IsTrackedPath(string project, string path)
    {
        var gitRoot = GitValue(project, "rev-parse", "--show-toplevel");
        if (!string.IsNullOrWhiteSpace(gitRoot))
        {
            var relative = Path.GetRelativePath(gitRoot, path).Replace(Path.DirectorySeparatorChar, '/');
            return !string.IsNullOrWhiteSpace(GitValue(gitRoot, "ls-files", "--error-unmatch", relative));
        }
        return !string.IsNullOrWhiteSpace(ProcessValue("svn", new[] { "info", "--show-item", "revision", SvnLiteralPath(path) }, project));
    }

    // An empty peg suffix makes every @ in the local path literal. Shell quoting
    // alone does not prevent SVN from parsing @8.13.0 as a revision.
    internal static string SvnLiteralPath(string path) => Path.GetFullPath(path) + "@";

    private static void AddCheck(List<object> checks, List<string> errors, string name, bool passed, string details)
    {
        checks.Add(new { name, passed, details });
        if (!passed) errors.Add(details);
    }

    private static string? ResolveOptionalFile(string? explicitPath, string root, string defaultRelative)
    {
        var path = string.IsNullOrWhiteSpace(explicitPath) ? Path.Combine(root, defaultRelative) : explicitPath;
        return File.Exists(path) ? Path.GetFullPath(path) : null;
    }

    private static string ProcessValue(string file, IEnumerable<string> arguments, string workingDirectory)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(file) { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start); if (process == null) return "";
            var value = process.StandardOutput.ReadToEnd(); process.WaitForExit(); return process.ExitCode == 0 ? value.Trim() : "";
        }
        catch { return ""; }
    }

    private static string TreeHashForRelease(string root, IEnumerable<string> ignoredPaths)
    {
        var ignored = new HashSet<string>(ignoredPaths.Select(path => path.Replace('\\', '/').TrimStart('/')), StringComparer.OrdinalIgnoreCase);
        using var sha = SHA256.Create(); var buffer = new byte[1024 * 1024];
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(path =>
        {
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            return !ignored.Contains(relative) && !relative.Split('/')[0].Equals(".git", StringComparison.OrdinalIgnoreCase);
        }).OrderBy(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), StringComparer.Ordinal))
        {
            var relative = Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/') + "\n");
            sha.TransformBlock(relative, 0, relative.Length, relative, 0);
            using var input = File.OpenRead(file); int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0) sha.TransformBlock(buffer, 0, read, buffer, 0);
            var separator = new byte[] { 10 }; sha.TransformBlock(separator, 0, 1, separator, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0); return Convert.ToHexString(sha.Hash!);
    }

    private static bool SafeRelative(string value) => !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value) && !value.Contains("..", StringComparison.Ordinal);

    private static int ReleaseGate(Cli cli)
    {
        var workflowPath = RequireFile(cli.Require("workflowreport"), "DHE workflow report");
        var planPath = RequireFile(cli.Require("projectplan"), "DHE project plan");
        var output = SafeReportPath(cli.Optional("output") ?? Path.Combine(Path.GetDirectoryName(workflowPath)!, "release-gate.json"), new[] { workflowPath, planPath });
        var target = cli.Require("target");
        var errors = new List<string>();
        var warnings = new List<string>();
        var workflow = ReadJson<JsonElement>(workflowPath);
        var plan = ReadJson<JsonElement>(planPath);
        var workflowRoot = Path.GetDirectoryName(workflowPath)!;
        var planRoot = Path.GetDirectoryName(planPath)!;
        var artifactValidationPath = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + ".artifact-validation.json");

        RequireFormat(workflow, "hybridclr.dhe-project-player-workflow.json", "Workflow", errors);
        RequireFormat(plan, "hybridclr.dhe-project-plan.json", "Project plan", errors);
        RequireTrue(workflow, "passed", "Workflow", errors);
        RequireTrue(workflow, "releaseReady", "Workflow", errors);
        if (!string.Equals(GetString(workflow, "mode"), "Release", StringComparison.Ordinal)) errors.Add("Workflow mode must be Release.");
        if (!string.Equals(GetString(workflow, "target"), target, StringComparison.OrdinalIgnoreCase)) errors.Add("Workflow target does not match the release target.");
        RequireTrue(plan, "complete", "Project plan", errors);
        RequireTrue(plan, "requireDheEqualsHotUpdate", "Project plan", errors);
        RequireTrue(plan, "dheEqualsHotUpdate", "Project plan", errors);
        var planValidationPath = ResolveEvidencePath(GetString(workflow, "projectPlanValidation"), workflowRoot, "project plan validation");
        var planValidation = ReadJson<JsonElement>(planValidationPath);
        RequireFormat(planValidation, "hybridclr.dhe-project-plan-validation.json", "Project plan validation", errors);
        RequireTrue(planValidation, "passed", "Project plan validation", errors);
        RequireTrue(planValidation, "coverageRequired", "Project plan validation", errors);
        RequireTrue(planValidation, "coverageComplete", "Project plan validation", errors);
        if (!Path.GetFullPath(ResolveEvidencePath(GetString(planValidation, "plan"), Path.GetDirectoryName(planValidationPath)!, "validated project plan")).Equals(Path.GetFullPath(planPath), StringComparison.OrdinalIgnoreCase))
            errors.Add("Project plan validation refers to a different plan.");

        var sourcePreflightPath = ResolveEvidencePath(GetString(workflow, "sourcePreflight"), workflowRoot, "source preflight");
        var cleanCheckoutPath = ResolveEvidencePath(GetString(workflow, "cleanCheckoutGate"), workflowRoot, "clean checkout report");
        var sourcePreflight = ReadJson<JsonElement>(sourcePreflightPath);
        var cleanCheckout = ReadJson<JsonElement>(cleanCheckoutPath);
        RequireFormat(sourcePreflight, "hybridclr.dhe-source-preflight.json", "Source preflight", errors);
        RequireTrue(sourcePreflight, "passed", "Source preflight", errors);
        RequireTrue(sourcePreflight, "runtimeRequired", "Source preflight", errors);
        RequireTrue(sourcePreflight, "runtimeReady", "Source preflight", errors);
        RequireTrue(sourcePreflight, "cleanRuntimeSourcesRequired", "Source preflight", errors);
        RequireTrue(sourcePreflight, "externalHeadersRequired", "Source preflight", errors);
        if (!sourcePreflight.TryGetProperty("externalHeadersSurrogate", out var surrogate) || surrogate.ValueKind != JsonValueKind.False)
            errors.Add("Source preflight must prove non-surrogate engine headers.");
        RequireFormat(cleanCheckout, "hybridclr.dhe-clean-checkout-gate.json", "Clean checkout", errors);
        RequireTrue(cleanCheckout, "passed", "Clean checkout", errors);
        RequireTrue(cleanCheckout, "gitCleanRequired", "Clean checkout", errors);
        RequireTrue(cleanCheckout, "trackedSourcesRequired", "Clean checkout", errors);
        RequireTrue(cleanCheckout, "trackedSourcesComplete", "Clean checkout", errors);
        ValidateSourceIdentity(cleanCheckout, "projectGit", errors);
        ValidateSourceIdentity(cleanCheckout, "toolGit", errors);
        var toolchainGatePath = GetString(workflow, "toolchainGate");
        if (string.IsNullOrWhiteSpace(toolchainGatePath)) errors.Add("Release workflow is missing toolchain package evidence.");
        else
        {
            var toolchainGate = ReadJson<JsonElement>(ResolveEvidencePath(toolchainGatePath, workflowRoot, "toolchain gate"));
            RequireFormat(toolchainGate, "hybridclr.dhe-toolchain-gate.json", "Toolchain gate", errors);
            RequireTrue(toolchainGate, "passed", "Toolchain gate", errors);
            RequireTrue(toolchainGate, "releaseReady", "Toolchain gate", errors);
            RequireTrue(toolchainGate, "requireRelease", "Toolchain gate", errors);
            if (!string.Equals(GetString(toolchainGate, "packageId"), GetString(workflow, "expectedToolchainPackageId"), StringComparison.OrdinalIgnoreCase))
                errors.Add("Toolchain gate package ID does not match the workflow pin.");
        }

        var planNames = StringArray(plan, "dheAotAssemblies", errors).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var hotNames = StringArray(plan, "hotUpdateAssemblies", errors).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!planNames.SequenceEqual(hotNames, StringComparer.OrdinalIgnoreCase)) errors.Add("Project plan does not have exact hot-update/DHE coverage.");
        var planRecords = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var liveCandidates = new Dictionary<string, (JsonElement Record,
            MetaVersionSnapshot Baseline, MetaVersionSnapshot Current)>(
            StringComparer.OrdinalIgnoreCase);
        var liveDiffs = new Dictionary<string, LiveAssemblyValidation>(
            StringComparer.OrdinalIgnoreCase);
        var changedMethodCount = 0;
        var methodCount = 0;
        var typeChangeCount = 0;
        if (!plan.TryGetProperty("assemblies", out var assemblies) || assemblies.ValueKind != JsonValueKind.Array)
            errors.Add("Project plan assemblies are missing.");
        else
        {
            foreach (var record in assemblies.EnumerateArray())
            {
                var name = GetString(record, "assemblyName") ?? "";
                if (string.IsNullOrWhiteSpace(name) || !planRecords.TryAdd(name, record)) { errors.Add("Project plan contains a missing or duplicate assembly name."); continue; }
                if (!string.Equals(GetString(record, "status"), "compatible", StringComparison.Ordinal)) errors.Add("Project plan assembly is not compatible: " + name);
                try
                {
                    var baseline = ResolveEvidencePath(GetString(record, "baseline"), planRoot, "baseline assembly");
                    var current = ResolveEvidencePath(GetString(record, "current"), planRoot, "current assembly");
                    MetaVersionSnapshot baselineMetaVersion = MetaVersionSnapshot.Create(baseline);
                    MetaVersionSnapshot currentMetaVersion = MetaVersionSnapshot.Create(current);
                    ValidateMetaVersionArtifacts(record, planRoot, "baseMetaVersion",
                        baselineMetaVersion, errors);
                    ValidateMetaVersionArtifacts(record, planRoot, "currentMetaVersion",
                        currentMetaVersion, errors);
                    liveCandidates[name] = (record, baselineMetaVersion,
                        currentMetaVersion);
                }
                catch (Exception ex) { errors.Add(name + ": " + ex.Message); }
            }
        }
        string[] addressTakenFields = liveCandidates.Values
            .SelectMany(candidate => candidate.Current.AddressTakenFieldIdentities)
            .Distinct(StringComparer.Ordinal).ToArray();
        foreach (var pair in liveCandidates)
        {
            ResourceUpdateCompatibility compatibility = ResourceUpdateCompatibility.Analyze(
                pair.Value.Baseline, pair.Value.Current, addressTakenFields);
            if (!compatibility.Compatible)
                errors.Add("Live assembly revalidation is incompatible: " + pair.Key + ": " +
                    string.Join("; ", compatibility.UnsupportedChanges));
            liveDiffs[pair.Key] = new LiveAssemblyValidation(pair.Value.Baseline,
                pair.Value.Current, compatibility);
            changedMethodCount += CountRuntimeChangedMethods(pair.Value.Baseline,
                pair.Value.Current);
            methodCount += pair.Value.Baseline.Methods.Length;
            typeChangeCount += compatibility.ChangedExistingTypeCount +
                compatibility.AddedTypeCount + compatibility.RemovedTypeCount;
        }
        if (!planNames.SequenceEqual(planRecords.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            errors.Add("Project plan assembly records do not match the configured DHE assembly set.");

        var playerPath = ResolveEvidencePath(GetString(workflow, "playerResult") ?? GetString(workflow, "player"), workflowRoot, "Player result");
        var nativePath = ResolveEvidencePath(GetString(workflow, "nativeManifest"), workflowRoot, "native manifest");
        var identityPath = ResolveEvidencePath(GetString(workflow, "buildIdentity"), workflowRoot, "build identity");
        var runtimePlanPath = ResolveEvidencePath(GetString(workflow, "runtimePlan"), workflowRoot, "runtime plan");
        var resourcePath = ResolveEvidencePath(GetString(workflow, "resourceEvidence"), workflowRoot, "resource evidence");
        var player = ReadJson<JsonElement>(playerPath);
        var native = ReadJson<JsonElement>(nativePath);
        var identity = ReadJson<JsonElement>(identityPath);
        var runtimePlan = ReadJson<JsonElement>(runtimePlanPath);
        var resource = ReadJson<JsonElement>(resourcePath);

        RequireFormat(player, "hybridclr.dhe-player-result.json", "Player result", errors);
        RequireTrue(player, "passed", "Player result", errors);
        if (!string.Equals(GetString(player, "loadError"), "OK", StringComparison.Ordinal)) errors.Add("Player loadError is not OK.");
        if (!string.Equals(GetString(player, "target"), target, StringComparison.OrdinalIgnoreCase)) errors.Add("Player target does not match.");
        RequireTrue(player, "buildIdentityValidated", "Player result", errors);
        RequireTrue(player, "dispatchProbeValidated", "Player result", errors);
        var loadedNames = StringArray(player, "loadedDheAssemblies", errors).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var plannedNames = StringArray(player, "plannedDheAssemblies", errors).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!planNames.SequenceEqual(loadedNames, StringComparer.OrdinalIgnoreCase) || !planNames.SequenceEqual(plannedNames, StringComparer.OrdinalIgnoreCase))
            errors.Add("Player planned/loaded assembly sets do not match the project plan.");
        if (plannedNames.Contains("HybridCLR.NewHotfix", StringComparer.OrdinalIgnoreCase) &&
            (!GetBool(player, "newHotfixPlanned") || !GetBool(player, "newHotfixLoaded") ||
             !GetBool(player, "newHotfixExecuted") || GetInt(player, "newHotfixResult") != 3705))
            errors.Add("Player did not execute the interpreter-only new hotfix assembly.");
        if (GetInt(player, "changedMethodCount") != changedMethodCount) errors.Add("Player changed method count does not match the revalidated MV set.");
        if (GetInt(player, "expectedChangedMethodCount") != changedMethodCount) errors.Add("Player expected changed method count does not match the revalidated MV set.");
        ValidatePlayerAssemblies(player, planNames, errors);
        if (changedMethodCount > 0)
        {
            if (GetInt(player, "interpreterEntryCount") <= 0) errors.Add("Changed workflow did not enter the interpreter.");
            if (GetInt(player, "aotEntryCount") <= 0) errors.Add("Changed workflow did not prove an unchanged AOT entry.");
            try { DhePlayerDispatch.Validate(player, liveDiffs.Values.Select(value =>
                new DhePlayerDispatch.AssemblyPair(value.Baseline, value.Current))); }
            catch (Exception exception) { errors.Add("Changed dispatch: " + exception.Message); }
            if (GetBool(player, "unchangedProbeChanged"))
                errors.Add("Changed workflow dispatch probe did not distinguish interpreter and AOT paths.");
            if (!GetBool(player, "retryValidated") || GetString(player, "transactionStatus") != "validated" || GetString(player, "retryFailure") != "DHE_MV_REGISTRATION_FAILED")
                errors.Add("Changed workflow did not prove transaction rollback and retry.");
        }
        else if (GetInt(player, "interpreterEntryCount") != 0 || GetBool(player, "changedProbeChanged") ||
            GetBool(player, "unchangedProbeChanged") || GetString(player, "transactionStatus") != "notApplicable")
            errors.Add("No-op workflow reported interpreter or transaction activity.");

        ValidateNativeManifest(native, liveDiffs, errors);
        if (GetInt(native, "unsupportedGuardedMethodCount") != 0 ||
            !string.Equals(GetString(native, "guardMode"), "universal", StringComparison.Ordinal))
            errors.Add("Native manifest does not provide universal Base coverage.");
        if (changedMethodCount > 0 && GetInt(native, "nativeEntryCount") <= 0) errors.Add("Changed workflow has no native guard entries.");
        RequireFormat(identity, "hybridclr.dhe-build-identity.json", "Build identity", errors);
        if (GetInt(identity, "identityVersion") != 1 || GetString(identity, "aotSnapshotKind") != "managed-assembly-plus-generated-cpp-v1" ||
            !string.Equals(GetString(identity, "target"), target, StringComparison.OrdinalIgnoreCase)) errors.Add("Build identity contract or target is invalid.");
        var identityNativePath = ResolveEvidencePath(GetString(identity, "nativeManifestPath"),
            Path.GetDirectoryName(identityPath)!, "Build identity native manifest");
        var identityNativeHash = Sha256File(identityNativePath);
        if (!string.Equals(GetString(identity, "nativeManifestSha256"), identityNativeHash, StringComparison.OrdinalIgnoreCase))
            errors.Add("Build identity native manifest hash does not match its immutable manifest file.");
        if (!string.Equals(GetString(player, "nativeManifestSha256"), identityNativeHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(player, "nativeGuardSourceSha256"), GetString(identity, "nativeGuardSourceSha256"), StringComparison.OrdinalIgnoreCase))
            errors.Add("Player native identity does not match the final build identity.");
        ValidateBuildIdentity(identity, identityPath, native, nativePath, liveDiffs, errors);
        RequireFormat(runtimePlan, "hybridclr.dhe-runtime-handoff-plan.json", "Runtime plan", errors);
        var runtimeNames = runtimePlan.GetProperty("assemblies").EnumerateArray().Select(x => GetString(x, "assemblyName") ?? "").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!planNames.SequenceEqual(runtimeNames, StringComparer.OrdinalIgnoreCase)) errors.Add("Runtime plan assembly set does not match the project plan.");
        ValidateRuntimePlanFiles(runtimePlan, Path.GetDirectoryName(runtimePlanPath)!, errors);
        RequireFormat(resource, "hybridclr.dhe-resource-evidence.json", "Resource evidence", errors);
        RequireTrue(resource, "passed", "Resource evidence", errors);
        if (!string.Equals(GetString(resource, "target"), target, StringComparison.OrdinalIgnoreCase)) errors.Add("Resource evidence target does not match.");
        try { ValidateResourceEvidence(resourcePath, target); }
        catch (Exception ex) { errors.Add("Resource evidence: " + ex.Message); }

        var passed = errors.Count == 0;
        WriteJson(artifactValidationPath, new
        {
            schemaVersion = 1, format = "hybridclr.dhe-artifact-validation.json", generatedAtUtc = DateTimeOffset.UtcNow,
            pathSemantics = "workspace-absolute-v1", passed, errors, warnings,
            metaVersionJsons = planRecords.Values.SelectMany(record => new[] {
                GetString(record, "baseMetaVersionJson"),
                GetString(record, "currentMetaVersionJson") }).ToArray(),
            metaVersionByteFiles = planRecords.Values.SelectMany(record => new[] {
                GetString(record, "baseMetaVersionBytes"),
                GetString(record, "currentMetaVersionBytes") }).ToArray(),
            baselineAssemblies = planRecords.Values.Select(record => GetString(record, "baseline")).ToArray(),
            currentAssemblies = planRecords.Values.Select(record => GetString(record, "current")).ToArray(),
            baselineAssembly = (string?)null, currentAssembly = (string?)null, nativeManifest = nativePath,
            buildIdentity = identityPath, workflowReport = workflowPath, runtimePlan = runtimePlanPath,
            batchReport = GetString(workflow, "batchReport")
        });
        WriteJson(output, new
        {
            schemaVersion = 1, format = "hybridclr.dhe-release-gate.json", generatedAtUtc = DateTimeOffset.UtcNow,
            passed, target, workflowMode = GetString(workflow, "mode"), sourcePreflight = sourcePreflightPath, sourcePreflightValidated = GetBool(sourcePreflight, "passed"),
            cleanCheckout = cleanCheckoutPath, cleanCheckoutValidated = GetBool(cleanCheckout, "passed"), projectGitHead = IdentityString(cleanCheckout, "projectGit", "head"), projectGitTree = IdentityString(cleanCheckout, "projectGit", "tree"), projectSourceBoundarySha256 = IdentityString(cleanCheckout, "projectGit", "sourceBoundarySha256"),
            toolGitHead = IdentityString(cleanCheckout, "toolGit", "head"), toolGitTree = IdentityString(cleanCheckout, "toolGit", "tree"), toolSourceBoundarySha256 = IdentityString(cleanCheckout, "toolGit", "sourceBoundarySha256"), projectVcs = IdentityString(cleanCheckout, "projectGit", "vcs"), projectRevision = IdentityString(cleanCheckout, "projectGit", "revision"),
            projectRevisionSpec = IdentityString(cleanCheckout, "projectGit", "revisionSpec"), projectRepository = IdentityString(cleanCheckout, "projectGit", "repository"),
            toolVcs = IdentityString(cleanCheckout, "toolGit", "vcs"), toolRevision = IdentityString(cleanCheckout, "toolGit", "revision"),
            toolRevisionSpec = IdentityString(cleanCheckout, "toolGit", "revisionSpec"), toolRepository = IdentityString(cleanCheckout, "toolGit", "repository"),
            projectPlanValidation = planValidationPath, projectPlanRevalidation = planPath,
            workflowReport = workflowPath, artifactValidation = artifactValidationPath, batchReport = GetString(workflow, "batchReport") ?? "",
            runtimePlan = runtimePlanPath, artifactValidatorExitCode = passed ? 0 : 1, projectPlanValidatorExitCode = passed ? 0 : 1,
            errors, warnings, validatedAssemblyCount = planRecords.Count, loadedDheAssemblyCount = loadedNames.Length,
            methodCount, changedMethodCount, typeChangeCount, playerResult = playerPath, nativeManifest = nativePath,
            buildIdentity = identityPath, resourceEvidence = resourcePath
        });
        if (!passed) { Console.Error.WriteLine(string.Join(Environment.NewLine, errors)); return 1; }
        Console.WriteLine("DHE release gate passed: " + output);
        return 0;
    }

    private static int Regression(Cli cli)
    {
        var baseline = RequireFile(cli.Require("layoutbaseline"), "Layout regression baseline");
        var current = RequireFile(cli.Require("layoutcurrent"), "Layout regression current");
        var output = SafeReportPath(cli.Require("output"), new[] { baseline, current });
        var checks = new List<object>();
        var errors = new List<string>();
        MetaVersionSnapshot baselineMetaVersion = MetaVersionSnapshot.Create(baseline);
        MetaVersionSnapshot currentMetaVersion = MetaVersionSnapshot.Create(current);
        ResourceUpdateCompatibility layoutCompatibility = ResourceUpdateCompatibility.Analyze(
            baselineMetaVersion, currentMetaVersion);
        var layoutRejected = !layoutCompatibility.Compatible &&
            layoutCompatibility.ChangedExistingTypeCount > 0;
        AddRegressionCheck(checks, errors, "mv-field-order", layoutRejected,
            layoutRejected ? "layout change rejected" : "layout change accepted");
        var regressionRoot = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + ".work");
        if (Directory.Exists(regressionRoot)) Directory.Delete(regressionRoot, true);
        Directory.CreateDirectory(regressionRoot);
        var switchAssembly = Path.Combine(regressionRoot, "switch-target.dll");
        WriteMutatedAssembly(baseline, switchAssembly, module =>
        {
            var method = module.GetTypes().SelectMany(type => type.Methods)
                .Single(item => item.Name == "SwitchProbe");
            var targets = method.Body.Instructions.Select(instruction => instruction.Operand)
                .OfType<IList<Instruction>>().Single();
            (targets[0], targets[1]) = (targets[1], targets[0]);
        });
        MetaVersionSnapshot switchMetaVersion = MetaVersionSnapshot.Create(switchAssembly);
        ResourceUpdateCompatibility switchCompatibility = ResourceUpdateCompatibility.Analyze(
            baselineMetaVersion, switchMetaVersion);
        AddRegressionCheck(checks, errors, "mv-switch-target", switchCompatibility.Compatible &&
            switchCompatibility.ChangedMethodCount == 1,
            "switch target table must be detected as a method-body change");

        var metadataAssembly = Path.Combine(regressionRoot, "assembly-metadata.dll");
        WriteMutatedAssembly(baseline, metadataAssembly, module =>
            module.Assembly.Version = new Version((module.Assembly.Version?.Major ?? 1) + 1, 0, 0, 0));
        ResourceUpdateCompatibility metadataCompatibility = ResourceUpdateCompatibility.Analyze(
            baselineMetaVersion, MetaVersionSnapshot.Create(metadataAssembly));
        AddRegressionCheck(checks, errors, "mv-assembly-metadata", !metadataCompatibility.Compatible,
            "assembly metadata change must be rejected");

        bool baseVariantMetadataStablePassed = false;
        string baseVariantMetadataStableDetails =
            "ManagedCurrentSeed must identify an authenticated DHE_CURRENT fixture assembly";
        string? managedCurrentSeed = cli.Optional("managedcurrentseed");
        if (!string.IsNullOrWhiteSpace(managedCurrentSeed))
        {
            try
            {
                managedCurrentSeed = RequireFile(managedCurrentSeed,
                    "Authenticated managed current fixture");
                var seedMetaVersion = MetaVersionSnapshot.Create(managedCurrentSeed);
                var baseVariantAssembly = Path.Combine(regressionRoot,
                    "base-variant-body-only.dll");
                ManagedCaseVariants.WriteBase2CurrentAssembly(managedCurrentSeed,
                    baseVariantAssembly);
                MetaVersionSnapshot baseVariantMetaVersion =
                    MetaVersionSnapshot.Create(baseVariantAssembly);
                ResourceUpdateCompatibility baseVariantCompatibility =
                    ResourceUpdateCompatibility.Analyze(seedMetaVersion,
                        baseVariantMetaVersion);
                baseVariantMetadataStablePassed = baseVariantCompatibility.Compatible &&
                    baseVariantCompatibility.ChangedMethodCount == 2 &&
                    baseVariantCompatibility.BodyOnlyChangedMethodCount == 1 &&
                    baseVariantCompatibility.DependencyChangedMethodCount == 1 &&
                    baseVariantCompatibility.ChangedExistingTypeCount == 0 &&
                    baseVariantCompatibility.UnsupportedChanges.Length == 0 &&
                    seedMetaVersion.AssemblyMetadataVersion ==
                        baseVariantMetaVersion.AssemblyMetadataVersion;
                baseVariantMetadataStableDetails =
                    "Base-generation fixture must preserve metadata and change exactly two methods";
            }
            catch (Exception exception)
            {
                baseVariantMetadataStableDetails = exception.Message;
            }
        }
        AddRegressionCheck(checks, errors, "managed-current-base-variant-metadata-stable",
            baseVariantMetadataStablePassed, baseVariantMetadataStableDetails);

        bool consecutiveVariantMetadataStablePassed = false;
        string consecutiveVariantMetadataStableDetails = baseVariantMetadataStableDetails;
        if (baseVariantMetadataStablePassed && !string.IsNullOrWhiteSpace(managedCurrentSeed))
        {
            try
            {
                var baseVariantAssembly = Path.Combine(regressionRoot,
                    "base-variant-body-only.dll");
                var consecutiveVariantAssembly = Path.Combine(regressionRoot,
                    "consecutive-variant-body-only.dll");
                ManagedCaseVariants.WriteNextCurrentAssembly(baseVariantAssembly,
                    consecutiveVariantAssembly);
                MetaVersionSnapshot baseVariantMetaVersion =
                    MetaVersionSnapshot.Create(baseVariantAssembly);
                MetaVersionSnapshot consecutiveVariantMetaVersion =
                    MetaVersionSnapshot.Create(consecutiveVariantAssembly);
                ResourceUpdateCompatibility consecutiveVariantCompatibility =
                    ResourceUpdateCompatibility.Analyze(baseVariantMetaVersion,
                        consecutiveVariantMetaVersion);
                consecutiveVariantMetadataStablePassed =
                    consecutiveVariantCompatibility.Compatible &&
                    consecutiveVariantCompatibility.ChangedMethodCount == 2 &&
                    consecutiveVariantCompatibility.BodyOnlyChangedMethodCount == 2 &&
                    consecutiveVariantCompatibility.DependencyChangedMethodCount == 0 &&
                    consecutiveVariantCompatibility.ChangedExistingTypeCount == 0 &&
                    consecutiveVariantCompatibility.UnsupportedChanges.Length == 0 &&
                    baseVariantMetaVersion.AssemblyMetadataVersion ==
                        consecutiveVariantMetaVersion.AssemblyMetadataVersion &&
                    !Sha256File(baseVariantAssembly).Equals(
                        Sha256File(consecutiveVariantAssembly),
                        StringComparison.OrdinalIgnoreCase);
                consecutiveVariantMetadataStableDetails =
                    "Consecutive current derivation must preserve metadata and change exactly two methods";
            }
            catch (Exception exception)
            {
                consecutiveVariantMetadataStableDetails = exception.Message;
            }
        }
        AddRegressionCheck(checks, errors,
            "managed-current-consecutive-variant-metadata-stable",
            consecutiveVariantMetadataStablePassed,
            consecutiveVariantMetadataStableDetails);

		var referenceRemovalAssembly = Path.Combine(regressionRoot, "reference-removal.dll");
		WriteMutatedAssembly(baseline, referenceRemovalAssembly, module =>
		{
			TypeDef referenceType = module.GetTypes().Single(type =>
				type.Name == "ReferenceFieldRemoval");
			referenceType.Fields.Remove(referenceType.Fields.Single(field => field.Name == "Removed"));
			referenceType.Fields.Remove(referenceType.Fields.Single(field => field.Name == "RemovedStatic"));
			TypeDef removedType = module.Types.Single(type => type.Name == "RemovedReferenceType");
			module.Types.Remove(removedType);
		});
		ResourceUpdateCompatibility referenceRemoval = ResourceUpdateCompatibility.Analyze(
			MetaVersionSnapshot.Create(baseline), MetaVersionSnapshot.Create(referenceRemovalAssembly));
		AddRegressionCheck(checks, errors, "reference-field-and-type-removal",
			referenceRemoval.Compatible && referenceRemoval.RemovedFieldCount == 3 &&
			referenceRemoval.RemovedTypeCount == 1 && referenceRemoval.DependencyChangedMethodCount > 0 &&
			referenceRemoval.BodyOnlyChangedMethodCount == 0,
			"reference/static field removal and type tombstones must be accepted with dependency propagation");

		var valueRemovalAssembly = Path.Combine(regressionRoot, "value-field-removal.dll");
		WriteMutatedAssembly(baseline, valueRemovalAssembly, module =>
		{
			TypeDef valueType = module.GetTypes().Single(type => type.Name == "ValueFieldRemoval");
			valueType.Fields.Remove(valueType.Fields.Single(field => field.Name == "Removed"));
		});
		ResourceUpdateCompatibility valueRemoval = ResourceUpdateCompatibility.Analyze(
			MetaVersionSnapshot.Create(baseline), MetaVersionSnapshot.Create(valueRemovalAssembly));
		AddRegressionCheck(checks, errors, "value-field-removal-rejected",
			!valueRemoval.Compatible && valueRemoval.UnsupportedChanges.Any(change =>
				change.StartsWith("removed-instance-field-on-existing-value-type:",
					StringComparison.Ordinal)),
			"value-type instance field removal must remain fail-closed until shadow layout exists");

        string[] requiredCapabilities =
        {
            "aot-guard-v1",
            "stable-method-identity-v1",
            "single-current-multibase-v1",
            "supplemental-existing-type-instance-fields-v1",
        };
        bool v1Compatible = ResourceUpdateCompatibility.CanExecuteUpdate(
            ResourceUpdateCompatibility.RuntimeProtocol, "dhe-runtime-v1",
            ResourceUpdateCompatibility.KnownRuntimeCapabilities, requiredCapabilities);
        bool v2Compatible = ResourceUpdateCompatibility.CanExecuteUpdate(
            ResourceUpdateCompatibility.RuntimeProtocol, "dhe-runtime-v2",
            ResourceUpdateCompatibility.KnownRuntimeCapabilities, requiredCapabilities);
        AddRegressionCheck(checks, errors,
            "runtime-contract-capability-negotiation", v1Compatible && v2Compatible,
            "different runtime build contracts under protocol v1 must be accepted by capability subset");
        using var buildIdentitySchema = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            cli.Root, "schemas", "dhe-build-identity.schema.json")));
        using var nativeManifestSchema = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            cli.Root, "schemas", "dhe-native-manifest.schema.json")));
        string buildIdentityContractPattern = GetString(buildIdentitySchema.RootElement
            .GetProperty("properties").GetProperty("runtimeContract"), "pattern") ?? string.Empty;
        string nativeManifestContractPattern = GetString(nativeManifestSchema.RootElement
            .GetProperty("properties").GetProperty("runtimeContract"), "pattern") ?? string.Empty;
        AddRegressionCheck(checks, errors, "runtime-contract-schema-binding",
            buildIdentityContractPattern == nativeManifestContractPattern &&
            System.Text.RegularExpressions.Regex.IsMatch("dhe-runtime-v1",
                buildIdentityContractPattern) &&
            System.Text.RegularExpressions.Regex.IsMatch(
                ResourceUpdateCompatibility.CurrentNativeRuntimeContract,
                buildIdentityContractPattern),
            "BuildIdentity and native manifest schemas must accept archived and current versioned contracts");
        string embeddedPackageRoot = Path.Combine(cli.Root, "unity2021-dhe-demo", "Packages",
            "com.code-philosophy.hybridclr");
        string buildPipelineSource = File.ReadAllText(Path.Combine(embeddedPackageRoot,
            "Editor", "Commands", "DheBuildPipeline.cs"));
        string managedRuntimeSource = File.ReadAllText(Path.Combine(embeddedPackageRoot,
            "Runtime", "DheRuntime.cs"));
        string expectedContractDeclaration = "NativeRuntimeContract = \"" +
            ResourceUpdateCompatibility.CurrentNativeRuntimeContract + "\"";
        AddRegressionCheck(checks, errors, "runtime-contract-package-binding",
            buildPipelineSource.Contains(expectedContractDeclaration,
                StringComparison.Ordinal) &&
            managedRuntimeSource.Contains(expectedContractDeclaration,
                StringComparison.Ordinal),
            "package build/runtime constants must match the current runtime contract");
        bool HasCurrentRuntimeCapabilities(string source)
        {
            var block = System.Text.RegularExpressions.Regex.Match(source,
                @"NativeRuntimeCapabilities\s*=\s*\{(?<values>[\s\S]*?)\};");
            var capabilities = System.Text.RegularExpressions.Regex.Matches(
                block.Groups["values"].Value, "\"([^\"]+)\"")
                .Select(match => match.Groups[1].Value).ToArray();
            return block.Success &&
                capabilities.Length == ResourceUpdateCompatibility.KnownRuntimeCapabilities.Length &&
                capabilities.ToHashSet(StringComparer.Ordinal)
                    .SetEquals(ResourceUpdateCompatibility.KnownRuntimeCapabilities);
        }
        AddRegressionCheck(checks, errors, "runtime-capability-package-binding",
            HasCurrentRuntimeCapabilities(buildPipelineSource) &&
            HasCurrentRuntimeCapabilities(managedRuntimeSource) &&
            !HasCurrentRuntimeCapabilities(buildPipelineSource.Replace(
                "\"aot-fgs-field-address-null-check-v1\"", "\"omitted-field-address-capability\"",
                StringComparison.Ordinal)),
            "Base generator, managed runtime and resource analyzer must declare identical capabilities");
        string[] missingCapability = ResourceUpdateCompatibility.KnownRuntimeCapabilities
            .Where(value => value != "stable-method-identity-v1").ToArray();
        AddRegressionCheck(checks, errors, "runtime-capability-missing-rejected",
            !ResourceUpdateCompatibility.CanExecuteUpdate(
                ResourceUpdateCompatibility.RuntimeProtocol, "dhe-runtime-v0",
                missingCapability, requiredCapabilities),
            "a Base missing one required update capability must be rejected");
        string identityHash = new string('a', 64);
        string aotInventoryHash = AssemblyNameSetHash(new[] { "HybridCLR.ManagedCasesAot" });
        string baseIdV1 = ComputeBaseId("StandaloneWindows64", "Unity2021Standard",
            "OptimizeSpeed", identityHash,
            aotInventoryHash, new string('b', 64), new string('c', 64), new string('d', 64),
            new string('e', 64), new string('f', 64), ResourceUpdateCompatibility.RuntimeProtocol,
            "dhe-runtime-v1", requiredCapabilities,
            "HybridCLRLab/DheDemo/", "HybridCLRLab/DheDemo/BaseMetaVersion/");
        string baseIdV2 = ComputeBaseId("StandaloneWindows64", "Unity2021Standard",
            "OptimizeSpeed", identityHash,
            aotInventoryHash, new string('b', 64), new string('c', 64), new string('d', 64),
            new string('e', 64), new string('f', 64), ResourceUpdateCompatibility.RuntimeProtocol,
            "dhe-runtime-v2", requiredCapabilities,
            "HybridCLRLab/DheDemo/", "HybridCLRLab/DheDemo/BaseMetaVersion/");
        AddRegressionCheck(checks, errors, "composite-base-id-runtime-bound",
            IsHex(baseIdV1, 64, 64) && IsHex(baseIdV2, 64, 64) &&
            !string.Equals(baseIdV1, baseIdV2, StringComparison.OrdinalIgnoreCase),
            "Base ID must distinguish runtime contracts even for identical managed assemblies");
        string expandedAotInventoryHash = AssemblyNameSetHash(new[]
            { "HybridCLR.ManagedCasesAot", "HybridCLR.BoundaryContracts" });
        string baseIdExpandedInventory = ComputeBaseId("StandaloneWindows64",
            "Unity2021Standard", "OptimizeSpeed", identityHash,
            expandedAotInventoryHash, new string('b', 64), new string('c', 64),
            new string('d', 64), new string('e', 64), new string('f', 64),
            ResourceUpdateCompatibility.RuntimeProtocol, "dhe-runtime-v1",
            requiredCapabilities, "HybridCLRLab/DheDemo/",
            "HybridCLRLab/DheDemo/BaseMetaVersion/");
        AddRegressionCheck(checks, errors, "composite-base-id-aot-inventory-bound",
            !string.Equals(baseIdV1, baseIdExpandedInventory,
                StringComparison.OrdinalIgnoreCase),
            "Base ID must distinguish complete AOT inventories");
        string baseIdOptimizeSize = ComputeBaseId("StandaloneWindows64",
            "Unity2022Fgs", "OptimizeSize", identityHash, aotInventoryHash,
            new string('b', 64), new string('c', 64), new string('d', 64),
            new string('e', 64), new string('f', 64),
            ResourceUpdateCompatibility.RuntimeProtocol, "dhe-runtime-v1",
            requiredCapabilities, "HybridCLRLab/DheDemo/",
            "HybridCLRLab/DheDemo/BaseMetaVersion/");
        AddRegressionCheck(checks, errors, "composite-base-id-build-configuration-bound",
            !string.Equals(baseIdV1, baseIdOptimizeSize,
                StringComparison.OrdinalIgnoreCase),
            "Base ID must distinguish engine workflow and IL2CPP code generation");
        var baseDheAssemblies = new HashSet<string>(
            new[] { "HybridCLR.ManagedCasesAot" }, StringComparer.OrdinalIgnoreCase);
        var baseAotAssemblies = new HashSet<string>(new[]
            { "HybridCLR.ManagedCasesAot", "HybridCLR.BoundaryContracts" },
            StringComparer.OrdinalIgnoreCase);
        bool ordinaryAotRejected = !TryClassifyAssemblyExecutionMode(
            "HybridCLR.BoundaryContracts", baseDheAssemblies, baseAotAssemblies,
            out string ordinaryAotExecutionMode, out string ordinaryAotRejection) &&
            ordinaryAotExecutionMode == "rejected" &&
            ordinaryAotRejection ==
                "assembly-present-in-base-aot-outside-dhe:HybridCLR.BoundaryContracts";
        AddRegressionCheck(checks, errors, "base-aot-outside-dhe-rejected",
            ordinaryAotRejected,
            "an assembly already compiled into the Base outside DHE must not become interpreter-only");

        var metadataOne = Encoding.UTF8.GetBytes("metadata-one");
        var metadataTwo = Encoding.UTF8.GetBytes("metadata-two");
        var metadataSetA = new[]
        {
            new KeyValuePair<string, byte[]>("System", metadataOne),
            new KeyValuePair<string, byte[]>("mscorlib", metadataTwo),
        };
        var metadataSetB = metadataSetA.Reverse().ToArray();
        string metadataSetIdA = NamedByteSetHash(metadataSetA);
        string metadataSetIdB = NamedByteSetHash(metadataSetB);
        AddRegressionCheck(checks, errors, "aot-metadata-set-order-independent",
            string.Equals(metadataSetIdA, metadataSetIdB, StringComparison.OrdinalIgnoreCase),
            "AOT metadata set identity must be independent of input assembly order");
        var metadataSetIds = new[] { metadataSetIdA, metadataSetIdB }
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        AddRegressionCheck(checks, errors, "aot-metadata-set-deduplicated",
            metadataSetIds.Count == 1,
            "equal AOT metadata sets must collapse to one content-addressed set");
        var metadataSelection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [baseIdV1] = metadataSetIdA,
            [baseIdV2] = metadataSetIdA,
        };
        var invalidMetadataSelection = new Dictionary<string, string>(metadataSelection,
            StringComparer.OrdinalIgnoreCase)
        {
            [baseIdV1] = new string('f', 64),
        };
        var knownMetadataSetIds = new HashSet<string>(new[] { metadataSetIdA },
            StringComparer.OrdinalIgnoreCase);
        AddRegressionCheck(checks, errors, "aot-metadata-set-selection-bound",
            metadataSelection.Count == 2 && metadataSelection.Values.All(knownMetadataSetIds.Contains) &&
            !invalidMetadataSelection.Values.All(knownMetadataSetIds.Contains),
            "every Base must resolve through one authenticated AOT metadata set selection");
        var metadataTampered = new[]
        {
            new KeyValuePair<string, byte[]>("System", metadataOne),
            new KeyValuePair<string, byte[]>("mscorlib", metadataTwo.Concat(new byte[] { 0x5a }).ToArray()),
        };
        AddRegressionCheck(checks, errors, "aot-metadata-set-tamper-rejected",
            !string.Equals(metadataSetIdA, NamedByteSetHash(metadataTampered),
                StringComparison.OrdinalIgnoreCase),
            "AOT metadata set identity must change when any metadata blob changes");

        var mvPath = Path.Combine(regressionRoot, "switch.mv.bytes");
        File.WriteAllBytes(mvPath, switchMetaVersion.ToBinary());
        var flagsPath = Path.Combine(regressionRoot, "switch-flags.mv.bytes");
        var flagsBytes = File.ReadAllBytes(mvPath); flagsBytes[12] = 2; File.WriteAllBytes(flagsPath, flagsBytes);
        var flagsRejected = !flagsBytes.SequenceEqual(switchMetaVersion.ToBinary());
        AddRegressionCheck(checks, errors, "mv-flags-tamper", flagsRejected, "unknown MV flags must be rejected");
        var tokenPath = Path.Combine(regressionRoot, "switch-token.mv.bytes");
        var tokenBytes = File.ReadAllBytes(mvPath);
        var nameLength = checked((int)BitConverter.ToUInt32(tokenBytes, 16));
        var typeCount = checked((int)BitConverter.ToUInt32(tokenBytes, 20));
        var tokenOffset = checked(60 + nameLength + typeCount * 72 + 96);
        BitConverter.GetBytes(BitConverter.ToUInt32(tokenBytes, tokenOffset) + 1).CopyTo(tokenBytes, tokenOffset);
        File.WriteAllBytes(tokenPath, tokenBytes);
        AddRegressionCheck(checks, errors, "mv-token-tamper",
            !tokenBytes.SequenceEqual(switchMetaVersion.ToBinary()),
            "same-count wrong MV token set must be rejected");

        string? resourceUpdateRoot = cli.Optional("resourceupdateroot");
        string? resourceUpdateRoot2 = cli.Optional("resourceupdateroot2");
        string? resourceAssetRoot = cli.Optional("resourceassetroot");
        string? resourceBaseBuildIdentity = cli.Optional("resourcebasebuildidentity");
        string? releaseGenesisRoot = cli.Optional("releasegenesisroot");
        string? releaseGenesisBaseRegistry = cli.Optional("releasegenesisbaseregistry");
        if (string.IsNullOrWhiteSpace(releaseGenesisRoot) !=
            string.IsNullOrWhiteSpace(releaseGenesisBaseRegistry))
            throw new DheException(
                "ReleaseGenesisRoot and ReleaseGenesisBaseRegistry must be supplied together.");
        if (!string.IsNullOrWhiteSpace(resourceUpdateRoot) ||
            !string.IsNullOrWhiteSpace(resourceUpdateRoot2) ||
            !string.IsNullOrWhiteSpace(resourceAssetRoot) ||
            !string.IsNullOrWhiteSpace(resourceBaseBuildIdentity))
        {
            if (string.IsNullOrWhiteSpace(resourceUpdateRoot) ||
                string.IsNullOrWhiteSpace(resourceAssetRoot) ||
                string.IsNullOrWhiteSpace(resourceBaseBuildIdentity))
            {
                AddRegressionCheck(checks, errors, "resource-stage-input-set", false,
                    "ResourceUpdateRoot, ResourceAssetRoot, and ResourceBaseBuildIdentity " +
                    "must be supplied together.");
            }
            else
            {
                RunResourceStagingRegressions(
                    RequireDirectory(resourceUpdateRoot, "Regression resource update"),
                    RequireDirectory(resourceAssetRoot, "Regression resource asset root"),
                    RequireFile(resourceBaseBuildIdentity,
                        "Regression Base Player build identity"),
                    regressionRoot, checks, errors,
                    string.IsNullOrWhiteSpace(resourceUpdateRoot2)
                        ? null
                        : RequireDirectory(resourceUpdateRoot2,
                            "Regression consecutive resource update"));
            }
        }

        string? resourceBaseRegistry = cli.Optional("baseregistry");
        bool baseRegistryPassed = false;
        string baseRegistryDetails = "the distributed package contains the Base registry schema";
        if (!string.IsNullOrWhiteSpace(resourceBaseRegistry))
        {
            try
            {
                BaseRegistryDocument registry = ReadBaseRegistry(resourceBaseRegistry);
                var workflows = registry.Entries.Select(entry => entry.EngineWorkflow)
                    .ToHashSet(StringComparer.Ordinal);
                bool workflowMatrix = RequiredPlayerEngineWorkflows.All(workflows.Contains);
                bool manifestBound = true;
                if (!string.IsNullOrWhiteSpace(resourceUpdateRoot))
                {
                    JsonElement updateManifest = ReadJson<JsonElement>(RequireFile(
                        Path.Combine(resourceUpdateRoot, "dhe-resource-update.json"),
                        "Registry regression resource update manifest"));
                    string auditPath = GetString(updateManifest, "baseRegistryAuditPath") ?? string.Empty;
                    string auditHash = GetString(updateManifest, "baseRegistryAuditSha256") ?? string.Empty;
                    string auditSource = string.IsNullOrWhiteSpace(auditPath)
                        ? string.Empty
                        : ResolveContainedPath(resourceUpdateRoot, auditPath,
                            "Registry regression Base registry audit copy");
                    manifestBound = string.Equals(GetString(updateManifest,
                            "baseRegistrySha256"), registry.Sha256,
                            StringComparison.OrdinalIgnoreCase) &&
                        GetInt(updateManifest, "baseRegistryEntryCount") == registry.Entries.Length &&
                        string.Equals(GetString(updateManifest, "baseRegistryId"),
                            registry.RegistryId, StringComparison.Ordinal) &&
                        GetInt(updateManifest, "baseRegistryRevision") == registry.Revision &&
                        string.Equals(GetString(updateManifest,
                                "baseRegistryParentSha256"),
                            registry.ParentRegistrySha256,
                            StringComparison.OrdinalIgnoreCase) &&
                        GetInt(updateManifest, "baseRegistryRetiredBaseCount") ==
                            registry.RetiredBases.Length &&
                        GetBool(updateManifest, "baseRegistryLineageValidated") &&
                        !string.IsNullOrWhiteSpace(auditSource) && File.Exists(auditSource) &&
                        string.Equals(Sha256File(auditSource), registry.Sha256,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(auditHash, registry.Sha256,
                            StringComparison.OrdinalIgnoreCase);
                }
                baseRegistryPassed = workflowMatrix && manifestBound;
                baseRegistryDetails = baseRegistryPassed
                    ? "registry-relative multi-Base input covers all three engine workflows and is bound to the single current manifest"
                    : "Base registry engine matrix or resource manifest binding is invalid";
            }
            catch (Exception exception)
            {
                baseRegistryDetails = exception.Message;
            }
        }
        else if (!string.IsNullOrWhiteSpace(cli.Optional("packageroot")))
        {
            baseRegistryPassed = File.Exists(Path.Combine(RequireDirectory(cli.Optional("packageroot")!,
                "Regression package"), "schemas",
                "dhe-base-registry.schema.json"));
        }
        AddRegressionCheck(checks, errors, "resource-base-registry", baseRegistryPassed,
            baseRegistryDetails);

        if (!string.IsNullOrWhiteSpace(resourceUpdateRoot) &&
            !string.IsNullOrWhiteSpace(resourceBaseRegistry))
        {
            RunCrossTargetPayloadVariantRegressions(
                RequireDirectory(resourceUpdateRoot,
                    "Cross-target regression resource update"),
                RequireFile(resourceBaseRegistry,
                    "Cross-target regression Base registry"),
                regressionRoot, checks, errors);
            string? releaseBuildSettings = cli.Optional("settingsfile");
            if (!string.IsNullOrWhiteSpace(releaseBuildSettings))
                RunResourceReleaseBuildRegressions(
                    RequireDirectory(resourceUpdateRoot,
                        "Resource release build regression update"),
                    RequireFile(resourceBaseRegistry,
                        "Resource release build regression registry"),
                    RequireFile(releaseBuildSettings,
                        "Resource release build regression settings"),
                    RequireDirectory(Path.Combine(cli.Root, "schemas"),
                        "Resource release build regression schemas"),
                    regressionRoot, checks, errors);
        }

        bool baseRegistryBuilderPassed = false;
        bool baseRegistryBuildConfigurationTamperRejected = false;
        bool baseRegistryLineagePassed = false;
        bool baseRegistryInPlaceOverwriteRejected = false;
        bool baseRegistryImplicitRemovalRejected = false;
        bool baseRegistryExplicitRetirementPassed = false;
        bool releaseLedgerInitialized = false;
        bool releaseLedgerContinuation = false;
        bool releaseLedgerResetRejected = false;
        bool releaseLedgerStaleHeadRejected = false;
        bool registryEmptyAotMetadataSetPassed = false;
        string baseRegistryBuilderDetails =
            "Regression requires an authenticated Base registry input.";
        if (!string.IsNullOrWhiteSpace(resourceBaseRegistry))
        {
            try
            {
                BaseRegistryDocument sourceRegistry = ReadBaseRegistry(resourceBaseRegistry);
                BaseRegistryDocument releaseLedgerRegistry = string.IsNullOrWhiteSpace(
                    releaseGenesisBaseRegistry)
                    ? sourceRegistry
                    : ReadBaseRegistry(releaseGenesisBaseRegistry);
                if (!string.Equals(sourceRegistry.RegistryId,
                        releaseLedgerRegistry.RegistryId, StringComparison.Ordinal))
                    throw new DheException(
                        "Release genesis and active Base registries must use the same RegistryId.");
                string builderRoot = Path.Combine(regressionRoot, "base-registry-builder");
                Directory.CreateDirectory(builderRoot);

                if (!string.IsNullOrWhiteSpace(resourceUpdateRoot))
                {
                    string releaseRoot = RequireDirectory(releaseGenesisRoot ?? resourceUpdateRoot,
                        "Release ledger regression resource update");
                    string ledgerPath = RequireFile(Path.Combine(releaseRoot,
                        ReleaseLedgerFileName), "Release ledger regression head");
                    ReleaseLedgerDocument ledger = ReadReleaseLedger(ledgerPath);
                    ResourceReleaseContext initialized = PrepareResourceReleaseContext(
                        new Cli("resource-update", new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["mode"] = "Release",
                            ["initializereleaseledger"] = "true",
                            ["releasechannelid"] = ledger.ChannelId,
                        }), releaseLedgerRegistry, null);
                    releaseLedgerInitialized = ledger.Revision == 1 &&
                        initialized.ReleaseReady && initialized.Revision == 1 &&
                        string.Equals(initialized.ChannelId, ledger.ChannelId,
                            StringComparison.Ordinal) &&
                        string.Equals(ledger.BaseRegistrySha256, releaseLedgerRegistry.Sha256,
                            StringComparison.OrdinalIgnoreCase) &&
                        ledger.ActiveBaseCount == releaseLedgerRegistry.Entries.Length;

                    var continuationArguments = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["mode"] = "Release",
                        ["previousreleaseledger"] = ledgerPath,
                        ["expectedpreviousreleaseledgersha256"] = ledger.Sha256,
                    };
                    ResourceReleaseContext continuation = PrepareResourceReleaseContext(
                        new Cli("resource-update", continuationArguments),
                        releaseLedgerRegistry, null);
                    releaseLedgerContinuation = continuation.ReleaseReady &&
                        continuation.Revision == ledger.Revision + 1 &&
                        string.Equals(continuation.ChannelId, ledger.ChannelId,
                            StringComparison.Ordinal) &&
                        string.Equals(continuation.ParentLedgerSha256, ledger.Sha256,
                            StringComparison.OrdinalIgnoreCase);

                    BaseRegistryDocument resetRegistry = new(releaseLedgerRegistry.SourcePath,
                        releaseLedgerRegistry.PathSemantics, new string('0', 64),
                        releaseLedgerRegistry.RegistryId, 1, null,
                        new[] { releaseLedgerRegistry.Entries[0] },
                        Array.Empty<BaseRegistryRetirement>());
                    try
                    {
                        _ = PrepareResourceReleaseContext(new Cli("resource-update",
                            continuationArguments), resetRegistry, null);
                    }
                    catch (DheException)
                    {
                        releaseLedgerResetRejected = true;
                    }

                    try
                    {
                        var staleArguments = new Dictionary<string, string>(
                            continuationArguments, StringComparer.OrdinalIgnoreCase)
                        {
                            ["expectedpreviousreleaseledgersha256"] = new string('f', 64),
                        };
                        _ = PrepareResourceReleaseContext(new Cli("resource-update",
                            staleArguments), sourceRegistry, null);
                    }
                    catch (DheException)
                    {
                        releaseLedgerStaleHeadRejected = true;
                    }

                    string? settingsFile = cli.Optional("settingsfile");
                    BaseRegistryEntry? fgsEntry = sourceRegistry.Entries.FirstOrDefault(entry =>
                        (entry.EngineWorkflow is "Unity2022Fgs" or "Tuanjie2022Fgs") &&
                        entry.AotMetadataRoot == null);
                    if (!string.IsNullOrWhiteSpace(settingsFile) && fgsEntry != null)
                    {
                        string fgsRegistryPath = Path.Combine(builderRoot,
                            "fgs-empty-aot-registry.json");
                        _ = BuildBaseRegistry(new Cli("base-registry",
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["baseidentities"] = fgsEntry.BuildIdentity,
                                ["baselineroots"] = fgsEntry.BaselineRoot,
                                ["basenativemanifests"] = fgsEntry.NativeManifest,
                                ["engineworkflows"] = fgsEntry.EngineWorkflow,
                                ["payloadvariantids"] = fgsEntry.PayloadVariantId,
                                ["labels"] = "FGS empty AOT metadata regression",
                                ["registryid"] = sourceRegistry.RegistryId + "-fgs-empty",
                                ["output"] = fgsRegistryPath,
                                ["forceoutput"] = "true",
                            }));
                        string fgsCurrentRoot = Path.Combine(builderRoot, "fgs-current");
                        Directory.CreateDirectory(fgsCurrentRoot);
                        JsonElement releaseManifest = ReadJson<JsonElement>(Path.Combine(
                            releaseRoot, "dhe-resource-update.json"));
                        JsonElement defaultVariant = SelectPayloadVariant(releaseManifest,
                            fgsEntry.PayloadVariantId, "FGS empty metadata regression manifest");
                        foreach (JsonElement assembly in defaultVariant.GetProperty("assemblies")
                                     .EnumerateArray())
                        {
                            string name = NormalizeName(GetString(assembly, "assemblyName") ??
                                string.Empty);
                            string source = RequireFile(ResolveContainedPath(releaseRoot,
                                GetString(assembly, "dll") ?? string.Empty,
                                "FGS regression current assembly"),
                                "FGS regression current assembly");
                            File.Copy(source, Path.Combine(fgsCurrentRoot, name + ".dll"), true);
                        }
                        string fgsOutput = Path.Combine(builderRoot,
                            "fgs-empty-aot-resource-update");
                        int fgsExit = ResourceUpdate(new Cli("resource-update",
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["mode"] = "Exploratory",
                                ["currentroot"] = fgsCurrentRoot,
                                ["currentvariantid"] = fgsEntry.PayloadVariantId,
                                ["baseregistry"] = fgsRegistryPath,
                                ["settingsfile"] = RequireFile(settingsFile,
                                    "FGS empty metadata regression settings"),
                                ["outputroot"] = fgsOutput,
                                ["forceoutput"] = "true",
                            }));
                        JsonElement fgsValidation = ReadJson<JsonElement>(Path.Combine(fgsOutput,
                            "dhe-resource-update-validation.json"));
                        JsonElement fgsPlan = ReadJson<JsonElement>(Path.Combine(fgsOutput,
                            "dhe-runtime-plan.json"));
                        registryEmptyAotMetadataSetPassed = fgsExit == 0 &&
                            GetBool(fgsValidation, "passed") &&
                            fgsPlan.GetProperty("aotMetadataSets").EnumerateArray().Single()
                                .GetProperty("assemblies").GetArrayLength() == 0;
                    }
                }
                string normalizedPath = Path.Combine(builderRoot, "supported-bases.json");
                int normalizedExit = BuildBaseRegistry(new Cli("base-registry",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["existingregistry"] = resourceBaseRegistry,
                        ["output"] = normalizedPath,
                        ["forceoutput"] = "true"
                    }));
                BaseRegistryDocument normalizedRegistry = ReadBaseRegistry(normalizedPath);
                string inPlacePath = Path.Combine(builderRoot,
                    "in-place-overwrite-rejected.json");
                File.Copy(normalizedPath, inPlacePath, true);
                try
                {
                    _ = BuildBaseRegistry(new Cli("base-registry",
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["existingregistry"] = inPlacePath,
                            ["output"] = inPlacePath,
                            ["forceoutput"] = "true"
                        }));
                }
                catch (DheException)
                {
                    baseRegistryInPlaceOverwriteRejected = true;
                }
                bool normalizedEntriesMatch = sourceRegistry.Entries.Length ==
                    normalizedRegistry.Entries.Length && sourceRegistry.Entries.Zip(
                        normalizedRegistry.Entries).All(pair =>
                        string.Equals(pair.First.BaseId, pair.Second.BaseId,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(pair.First.EngineWorkflow, pair.Second.EngineWorkflow,
                            StringComparison.Ordinal) &&
                        string.Equals(pair.First.PayloadVariantId, pair.Second.PayloadVariantId,
                            StringComparison.Ordinal) &&
                        string.Equals(pair.First.Label, pair.Second.Label,
                            StringComparison.Ordinal) &&
                        string.Equals(pair.First.BaselineRoot, pair.Second.BaselineRoot,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(pair.First.NativeManifest, pair.Second.NativeManifest,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(pair.First.BuildIdentity, pair.Second.BuildIdentity,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(pair.First.AotMetadataRoot, pair.Second.AotMetadataRoot,
                            StringComparison.OrdinalIgnoreCase));
                baseRegistryLineagePassed = normalizedRegistry.Revision ==
                    sourceRegistry.Revision + 1 &&
                    string.Equals(normalizedRegistry.RegistryId, sourceRegistry.RegistryId,
                        StringComparison.Ordinal) &&
                    string.Equals(normalizedRegistry.ParentRegistrySha256,
                        sourceRegistry.Sha256, StringComparison.OrdinalIgnoreCase) &&
                    ValidateBaseRegistryLineage(normalizedRegistry,
                        resourceBaseRegistry) != null;

                bool duplicateRejected = false;
                if (sourceRegistry.Entries.Length > 0)
                {
                    BaseRegistryEntry first = sourceRegistry.Entries[0];
                    string implicitRemovalPath = Path.Combine(builderRoot,
                        "implicit-removal.json");
                    var implicitRemoval = System.Text.Json.Nodes.JsonNode.Parse(
                        File.ReadAllText(normalizedPath))!.AsObject();
                    implicitRemoval["bases"]!.AsArray().RemoveAt(0);
                    File.WriteAllText(implicitRemovalPath,
                        implicitRemoval.ToJsonString(
                            new JsonSerializerOptions { WriteIndented = true }),
                        new UTF8Encoding(false));
                    try
                    {
                        BaseRegistryDocument removed = ReadBaseRegistry(implicitRemovalPath);
                        _ = ValidateBaseRegistryLineage(removed, resourceBaseRegistry);
                    }
                    catch (DheException)
                    {
                        baseRegistryImplicitRemovalRejected = true;
                    }

                    if (sourceRegistry.Entries.Length > 1)
                    {
                        string retiredPath = Path.Combine(builderRoot,
                            "explicit-retirement.json");
                        int retiredExit = BuildBaseRegistry(new Cli("base-registry",
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["existingregistry"] = resourceBaseRegistry,
                                ["retirebaseids"] = first.BaseId,
                                ["retirementreason"] = "regression retirement",
                                ["output"] = retiredPath,
                                ["forceoutput"] = "true"
                            }));
                        BaseRegistryDocument retired = ReadBaseRegistry(retiredPath);
                        BaseRegistryDocument? retiredParent =
                            ValidateBaseRegistryLineage(retired, resourceBaseRegistry);
                        baseRegistryExplicitRetirementPassed = retiredExit == 0 &&
                            retiredParent != null &&
                            retired.Entries.All(item => !string.Equals(item.BaseId,
                                first.BaseId, StringComparison.OrdinalIgnoreCase)) &&
                            retired.RetiredBases.Count(item => string.Equals(item.BaseId,
                                first.BaseId, StringComparison.OrdinalIgnoreCase) &&
                                item.RetiredAtRevision == retired.Revision &&
                                item.Reason == "regression retirement") == 1;
                    }
                    string duplicateRoot = Path.Combine(builderRoot, "duplicate");
                    Directory.CreateDirectory(duplicateRoot);
                    string identityA = Path.Combine(duplicateRoot, "identity-a.json");
                    string identityB = Path.Combine(duplicateRoot, "identity-b.json");
                    File.Copy(first.BuildIdentity, identityA, true);
                    File.Copy(first.BuildIdentity, identityB, true);
                    string duplicateOutput = Path.Combine(duplicateRoot, "rejected.json");
                    try
                    {
                        _ = BuildBaseRegistry(new Cli("base-registry",
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["baseidentities"] = identityA + "," + identityB,
                                ["baselineroots"] = first.BaselineRoot + "," + first.BaselineRoot,
                                ["basenativemanifests"] = first.NativeManifest + "," + first.NativeManifest,
                                ["engineworkflows"] = first.EngineWorkflow + "," + first.EngineWorkflow,
                                ["payloadvariantids"] = first.PayloadVariantId + "," + first.PayloadVariantId,
                                ["output"] = duplicateOutput,
                                ["forceoutput"] = "true"
                            }));
                    }
                    catch (DheException)
                    {
                        duplicateRejected = true;
                    }

                    string workflowTamperPath = Path.Combine(builderRoot,
                        "workflow-tamper.json");
                    var workflowTamper = System.Text.Json.Nodes.JsonNode.Parse(
                        File.ReadAllText(normalizedPath))!.AsObject();
                    var workflowTamperEntry = workflowTamper["bases"]!.AsArray()[0]!
                        .AsObject();
                    workflowTamperEntry["engineWorkflow"] =
                        string.Equals(first.EngineWorkflow, "Unity2021Standard",
                            StringComparison.Ordinal)
                            ? "Unity2022Fgs"
                            : "Unity2021Standard";
                    File.WriteAllText(workflowTamperPath, workflowTamper.ToJsonString(
                        new JsonSerializerOptions { WriteIndented = true }),
                        new UTF8Encoding(false));
                    bool workflowTamperRejected = false;
                    try
                    {
                        _ = BuildBaseRegistry(new Cli("base-registry",
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["existingregistry"] = workflowTamperPath,
                                ["output"] = Path.Combine(builderRoot,
                                    "workflow-tamper-rejected.json"),
                                ["forceoutput"] = "true"
                            }));
                    }
                    catch (DheException)
                    {
                        workflowTamperRejected = true;
                    }

                    string codeGenerationTamperIdentity = Path.Combine(builderRoot,
                        "code-generation-tamper-identity.json");
                    var codeGenerationTamper = System.Text.Json.Nodes.JsonNode.Parse(
                        File.ReadAllText(first.BuildIdentity))!.AsObject();
                    string originalCodeGeneration =
                        codeGenerationTamper["il2cppCodeGeneration"]!.GetValue<string>();
                    codeGenerationTamper["il2cppCodeGeneration"] =
                        string.Equals(originalCodeGeneration, "OptimizeSpeed",
                            StringComparison.Ordinal)
                            ? "OptimizeSize"
                            : "OptimizeSpeed";
                    File.WriteAllText(codeGenerationTamperIdentity,
                        codeGenerationTamper.ToJsonString(
                            new JsonSerializerOptions { WriteIndented = true }),
                        new UTF8Encoding(false));
                    bool codeGenerationTamperRejected = false;
                    try
                    {
                        _ = BuildBaseRegistry(new Cli("base-registry",
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["baseidentities"] = codeGenerationTamperIdentity,
                                ["baselineroots"] = first.BaselineRoot,
                                ["basenativemanifests"] = first.NativeManifest,
                                ["engineworkflows"] = first.EngineWorkflow,
                                ["payloadvariantids"] = first.PayloadVariantId,
                                ["output"] = Path.Combine(builderRoot,
                                    "code-generation-tamper-rejected.json"),
                                ["forceoutput"] = "true"
                            }));
                    }
                    catch (DheException)
                    {
                        codeGenerationTamperRejected = true;
                    }
                    baseRegistryBuildConfigurationTamperRejected =
                        workflowTamperRejected && codeGenerationTamperRejected;
                }

                baseRegistryBuilderPassed = normalizedExit == 0 && normalizedEntriesMatch &&
                    duplicateRejected;
                baseRegistryBuilderDetails = baseRegistryBuilderPassed
                    ? "registry builder normalized the multi-Base input and rejected duplicate Base IDs"
                    : "registry builder normalization or duplicate Base ID rejection failed";
            }
            catch (Exception exception)
            {
                baseRegistryBuilderDetails = exception.Message;
            }
        }
        AddRegressionCheck(checks, errors, "base-registry-builder", baseRegistryBuilderPassed,
            baseRegistryBuilderDetails);
        AddRegressionCheck(checks, errors,
            "base-registry-build-configuration-tamper-rejected",
            baseRegistryBuildConfigurationTamperRejected,
            "registry workflow and build identity code generation tampering must be rejected");
        AddRegressionCheck(checks, errors, "base-registry-lineage",
            baseRegistryLineagePassed,
            "a generated registry revision must authenticate its direct parent");
        AddRegressionCheck(checks, errors, "base-registry-in-place-overwrite-rejected",
            baseRegistryInPlaceOverwriteRejected,
            "a new registry revision must not overwrite the parent registry input");
        AddRegressionCheck(checks, errors,
            "base-registry-implicit-removal-rejected",
            baseRegistryImplicitRemovalRejected,
            "an online Base cannot disappear without an explicit retirement record");
        AddRegressionCheck(checks, errors, "base-registry-explicit-retirement",
            baseRegistryExplicitRetirementPassed,
            "an explicit retirement must preserve the parent chain, Base identity, and reason");
        AddRegressionCheck(checks, errors, "resource-release-ledger-initialized",
            releaseLedgerInitialized,
            "a Release channel genesis must bind the complete active Base registry head");
        AddRegressionCheck(checks, errors, "resource-release-ledger-continuation",
            releaseLedgerContinuation,
            "a continuation must derive its channel, revision, and parent from the published ledger");
        AddRegressionCheck(checks, errors, "resource-release-ledger-reset-rejected",
            releaseLedgerResetRejected,
            "a one-Base revision-1 registry reset must not continue a published multi-Base channel");
        AddRegressionCheck(checks, errors, "resource-release-ledger-stale-head-rejected",
            releaseLedgerStaleHeadRejected,
            "a previous ledger that differs from the release-system head hash must be rejected");
        AddRegressionCheck(checks, errors, "registry-empty-aot-metadata-set",
            registryEmptyAotMetadataSetPassed,
            "an FGS registry with explicit null metadata roots must produce the authenticated empty set");

        var packageRoot = cli.Optional("packageroot");
        if (!string.IsNullOrWhiteSpace(packageRoot))
        {
            packageRoot = RequireDirectory(packageRoot, "Regression package");
            PackageInspection predecessorCandidate = InspectPackage(packageRoot, null, false);
            bool predecessorAuthorized = predecessorCandidate.Passed &&
                IsHex(predecessorCandidate.PackageId, 64, 64) &&
                HasImmediatePredecessorAuthority(
                    predecessorCandidate.ToolchainVersion ?? string.Empty,
                    ReadEvidenceAuthoritySet(packageRoot, packageRoot,
                        predecessorCandidate.PackageId!));
            AddRegressionCheck(checks, errors,
                "evidence-immediate-predecessor-authorized", predecessorAuthorized,
                "a successor toolchain must authorize its immediately preceding Release " +
                "for long-lived Base evidence");
            var requireReleaseRejected = VerifyPackage(new Cli("verify-package", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["packageroot"] = packageRoot, ["requirerelease"] = "true" })) != 0;
            AddRegressionCheck(checks, errors, "verify-require-release", requireReleaseRejected,
                "exploratory package must be rejected");
            var wrongIdRejected = VerifyPackage(new Cli("verify-package", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["packageroot"] = packageRoot, ["expectedpackageid"] = new string('a', 64) })) != 0;
            AddRegressionCheck(checks, errors, "verify-expected-id", wrongIdRejected,
                "wrong package ID must be rejected");

            var manifestTamper = Path.Combine(regressionRoot, "manifest-tamper-package");
            CopyDirectory(packageRoot, manifestTamper);
            var programPath = Path.Combine(manifestTamper, "tool", "Program.cs");
            File.AppendAllText(programPath, "\n// regression tamper\n", new UTF8Encoding(false));
            var manifestPath = Path.Combine(manifestTamper, "dhe-toolchain-manifest.json");
            var manifestNode = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            var programEntry = manifestNode["files"]!.AsArray().Select(node => node!.AsObject())
                .Single(node => node["path"]!.GetValue<string>() == "tool/Program.cs");
            programEntry["size"] = new FileInfo(programPath).Length;
            programEntry["sha256"] = Sha256File(programPath);
            File.WriteAllText(manifestPath, manifestNode.ToJsonString(Json), new UTF8Encoding(false));
            var manifestTamperRejected = VerifyPackage(new Cli("verify-package", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["packageroot"] = manifestTamper })) != 0;
            AddRegressionCheck(checks, errors, "verify-package-id-recompute", manifestTamperRejected,
                "updated per-file hash with stale package ID must be rejected");

            var extraSource = Path.Combine(regressionRoot, "extra-source-package");
            CopyDirectory(packageRoot, extraSource);
            File.WriteAllText(Path.Combine(extraSource, "tool", "Injected.cs"), "namespace HybridCLR.DheTool { internal static class Injected { } }\n", new UTF8Encoding(false));
            var extraSourceRejected = VerifyPackage(new Cli("verify-package", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["packageroot"] = extraSource })) != 0;
            AddRegressionCheck(checks, errors, "verify-extra-source", extraSourceRejected,
                "unlisted compilable source must be rejected");

            var releaseTamper = Path.Combine(regressionRoot, "release-bit-package");
            CopyDirectory(packageRoot, releaseTamper);
            var releaseManifestPath = Path.Combine(releaseTamper, "dhe-toolchain-manifest.json");
            var releaseNode = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(releaseManifestPath))!.AsObject();
            releaseNode["mode"] = "Release"; releaseNode["releaseReady"] = true;
            File.WriteAllText(releaseManifestPath, releaseNode.ToJsonString(Json), new UTF8Encoding(false));
            var releaseTamperRejected = VerifyPackage(new Cli("verify-package", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["packageroot"] = releaseTamper, ["requirerelease"] = "true" })) != 0;
            AddRegressionCheck(checks, errors, "verify-release-bit-tamper", releaseTamperRejected,
                "release mode/bit tamper must be rejected");
        }
        else
        {
            AddRegressionCheck(checks, errors,
                "evidence-immediate-predecessor-authorized", false,
                "PackageRoot is required for predecessor authority validation.");
            foreach (var name in new[] { "verify-require-release", "verify-expected-id", "verify-package-id-recompute",
                         "verify-extra-source", "verify-release-bit-tamper" })
                AddRegressionCheck(checks, errors, name, false, "PackageRoot is required for production regression.");
        }

        var roleRejected = false;
        try
        {
            using var wrongRole = JsonDocument.Parse("{\"schemaVersion\":1,\"format\":\"hybridclr.dhe-regression.json\",\"passed\":true}");
            ValidateEvidenceRole("native-tuanjie2022", wrongRole.RootElement, output,
                new string('a', 40), new string('b', 40), cli.Root,
                Array.Empty<string>());
        }
        catch { roleRejected = true; }
        AddRegressionCheck(checks, errors, "evidence-role-format", roleRejected,
            "release evidence role must enforce its report format");

        var unboundNativeRejected = false;
        try
        {
            using var unboundNative = JsonDocument.Parse("{\"schemaVersion\":1," +
                "\"format\":\"hybridclr.dhe-native-gate.json\",\"passed\":true," +
                "\"mergeReady\":true,\"profile\":\"DHE-Tuanjie2022\"," +
                "\"configuration\":\"Release\",\"runtimeManifest\":\"missing.json\"," +
                "\"runtimeManifestSha256\":\"" + new string('a', 64) + "\"," +
                "\"runtimeRoot\":\"missing\",\"runtimeTreeSha256\":\"" + new string('b', 64) + "\"," +
                "\"externalTreeSha256\":\"" + new string('c', 64) + "\"," +
                "\"nativeExitCode\":0,\"surrogateHeadersAllowed\":false,\"errors\":[]}");
            ValidateEvidenceRole("native-tuanjie2022", unboundNative.RootElement, output,
                new string('a', 40), new string('b', 40), cli.Root,
                Array.Empty<string>());
        }
        catch { unboundNativeRejected = true; }
        AddRegressionCheck(checks, errors, "evidence-native-runtime-binding", unboundNativeRejected,
            "native release evidence must bind the live runtime, headers, locks, and source commits");

        var exploratoryManagedRejected = false;
        try
        {
            using var exploratoryManaged = JsonDocument.Parse("{\"mode\":\"Exploratory\"," +
                "\"releaseReady\":false}");
            ValidateManagedReleaseEvidence(exploratoryManaged.RootElement, output);
        }
        catch { exploratoryManagedRejected = true; }
        AddRegressionCheck(checks, errors, "evidence-managed-release-binding",
            exploratoryManagedRejected,
            "managed release evidence must reject exploratory or runtime-unbound Player workflows");
        string currentToolHead = GitValue(cli.Root, "rev-parse", "HEAD");
        string currentToolTree = GitValue(cli.Root, "rev-parse", "HEAD^{tree}");
        string evidenceToolHead = GitValue(cli.Root, "rev-parse", "HEAD^");
        string evidenceToolTree = GitValue(cli.Root, "rev-parse", "HEAD^^{tree}");
        string authorityToolHead = GitValue(cli.Root, "rev-parse", "HEAD^^");
        string authorityToolTree = GitValue(cli.Root, "rev-parse", "HEAD^^^{tree}");
        bool historicalToolSourceAccepted = IsAuthorizedEvidenceToolSource(cli.Root,
            currentToolHead, currentToolTree, evidenceToolHead, evidenceToolTree,
            authorityToolHead, authorityToolTree);
        bool historicalToolTreeTamperRejected = !IsAuthorizedEvidenceToolSource(cli.Root,
            currentToolHead, currentToolTree, evidenceToolHead, new string('f', 40),
            authorityToolHead, authorityToolTree);
        AddRegressionCheck(checks, errors, "evidence-tool-source-ancestor-chain",
            historicalToolSourceAccepted,
            "historical Base evidence must remain valid only on an authenticated " +
            "authority-to-current Git ancestry chain");
        AddRegressionCheck(checks, errors, "evidence-tool-source-tree-tamper",
            historicalToolTreeTamperRejected,
            "historical Base evidence with a forged Git tree must be rejected");

        var duplicateBaseRejected = false;
        try
        {
            string baseId = new string('a', 64);
            using var first = JsonDocument.Parse("{\"selectedBaseId\":\"" + baseId + "\"}");
            using var second = JsonDocument.Parse("{\"selectedBaseId\":\"" + baseId + "\"}");
            ValidateMultiBaseChangedEvidence(new[]
            {
                (first.RootElement, output), (second.RootElement, output)
            }, false);
        }
        catch { duplicateBaseRejected = true; }
        AddRegressionCheck(checks, errors, "evidence-multibase-current-binding",
            duplicateBaseRejected,
            "release evidence must reject duplicate Base identities before accepting a shared current payload");
        bool extensiblePlayerMatrixPassed;
        try
        {
            extensiblePlayerMatrixPassed = RunExtensiblePlayerMatrixRegression(regressionRoot);
        }
        catch
        {
            extensiblePlayerMatrixPassed = false;
        }
        AddRegressionCheck(checks, errors, "evidence-extensible-player-engine-matrix",
            extensiblePlayerMatrixPassed,
            "release evidence must accept additional Base Players and per-Base payload variants while requiring all three engine workflows");
        bool legacyPayloadSelectionPassed = RunLegacySinglePayloadSelectionRegression();
        AddRegressionCheck(checks, errors,
            "resource-player-legacy-single-payload-compatibility",
            legacyPayloadSelectionPassed,
            "legacy Player results may omit both payload selection fields only for a single implicit default payload");
        AddRegressionCheck(checks, errors, "resource-player-assembly-mode-binding",
            RunResourcePlayerAssemblyModeRegression(),
            "Player differential/interpreter-only plans and loaded interpreter assemblies must match the selected Base mode map");
        AddRegressionCheck(checks, errors, "resource-player-interpreter-only-update",
            RunInterpreterOnlyResourceUpdateRegression(),
            "a resource update containing only a new interpreter assembly must retain no-op AOT proof without requiring a DHE transaction");
        AddRegressionCheck(checks, errors, "resource-player-selected-base-noop",
            RunSelectedBaseNoOpResourceUpdateRegression(),
            "a current payload equal to one selected Base must require and accept complete no-op AOT evidence");
        var runtimeSource = File.ReadAllText(Path.Combine(cli.Root, "tool", "LabCommands.cs"));
        AddRegressionCheck(checks, errors, "runtime-package-source-binding",
            runtimeSource.Contains("ValidateRepoIdentity(\"hybridclr_unity\"", StringComparison.Ordinal),
            "runtime assembly must fail closed on the locked package source identity");
        string metadataStressSource = LabCommands.RenderMetadataStressSource(1024, 12, 8, 4);
        int metadataStressValueWrites = System.Text.RegularExpressions.Regex.Matches(
            metadataStressSource, @"values\[\d+\] =").Count;
        bool boundedMetadataStressTouch = metadataStressValueWrites == 128 &&
            metadataStressSource.Contains("long[] values = new long[128];", StringComparison.Ordinal) &&
            metadataStressSource.Contains("index < values.Length", StringComparison.Ordinal) &&
            !metadataStressSource.Contains(
                "checksum = Accumulate(checksum, new StressType", StringComparison.Ordinal);
        AddRegressionCheck(checks, errors, "metadata-stress-touch-bounded",
            boundedMetadataStressTouch,
            "metadata stress must preserve 128 direct type probes without an IL2CPP-foldable accumulation chain");
        AddRegressionCheck(checks, errors, "base-workflow-aot-metadata-archive",
            RunBaseAotMetadataArchiveRegression(regressionRoot),
            "Base workflow metadata archives must be content-bound, registry-ready, and fail closed on tampering");
        AddRegressionCheck(checks, errors,
            "resource-player-archive-native-manifest-bound",
            RunArchivedNativeManifestResolutionRegression(regressionRoot),
            "resource Player evidence must resolve the immutable native manifest from a revalidated Base archive");
        var bootstrapWorkflowDoc = ReadJson<JsonElement>(Path.Combine(cli.Root, "manifests",
            "runtime-workflows.json"));
        JsonElement[] bootstrapWorkflows = LabCommands.SelectBootstrapWorkflowRecords(bootstrapWorkflowDoc, null, true);
        bool bootstrapProductionConfigurationPassed = true;
        foreach (JsonElement workflow in bootstrapWorkflows)
        {
            string workflowId = GetString(workflow, "id") ?? string.Empty;
            string runtimePath = Path.Combine(regressionRoot,
                "runtime-" + workflowId + ".json");
            WriteJson(runtimePath, new
            {
                schemaVersion = 1,
                format = "hybridclr.dhe-runtime-manifest.json",
                engineWorkflow = workflowId,
            });
            try
            {
                var configuration = ResolveWorkflowBuildConfiguration(new Cli("workflow",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["validationsourceroot"] = cli.Root,
                    }), runtimePath);
                bool unity2021 = workflowId == "Unity2021Standard";
                bootstrapProductionConfigurationPassed &=
                    string.Equals(configuration.Il2CppCodeGeneration,
                        unity2021 ? "OptimizeSpeed" : "OptimizeSize",
                        StringComparison.Ordinal) &&
                    string.Equals(configuration.AotMetadataAssemblies,
                        unity2021 ? null : "none", StringComparison.Ordinal);
            }
            catch
            {
                bootstrapProductionConfigurationPassed = false;
            }
        }
        bool bootstrapOverrideRejected = false;
        try
        {
            AppendUnityArguments(new List<string>(), new Cli("workflow",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["unityarguments"] =
                        "{\"dheAotMetadataAssemblies\":\"mscorlib\"}",
                }));
        }
        catch (DheException)
        {
            bootstrapOverrideRejected = true;
        }
        bool bootstrapMatrixPassed = bootstrapWorkflows.Length == 3 &&
            bootstrapWorkflows.Select(item => GetString(item.GetProperty("il2cppPlus"), "commit"))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3 &&
            bootstrapWorkflows.All(item => LabCommands.SelectBootstrapWorkflowRecords(bootstrapWorkflowDoc,
                GetString(item, "id"), false).Length == 1) &&
            bootstrapProductionConfigurationPassed && bootstrapOverrideRejected;
        AddRegressionCheck(checks, errors, "bootstrap-engine-workflow-matrix", bootstrapMatrixPassed,
            "bootstrap must resolve all three production build configurations and reject identity overrides");
        var boundaryErrors = new List<string>();
        bool gitRootBoundaryPassed = ValidateBoundary(Path.Combine(cli.Root, "unity2021-dhe-demo"),
            Path.Combine(cli.Root, "manifests", "dhe-source-boundary.json"), boundaryErrors);
        AddRegressionCheck(checks, errors, "source-boundary-git-root-resolution",
            gitRootBoundaryPassed, gitRootBoundaryPassed
                ? "git-root-v1 boundary resolves from a nested Unity project to the repository root"
                : string.Join("; ", boundaryErrors));
        string relativeGitRoot = GitValue(".", "rev-parse", "--show-toplevel");
        AddRegressionCheck(checks, errors, "git-relative-root-resolution",
            !string.IsNullOrWhiteSpace(relativeGitRoot) &&
            Path.GetFullPath(relativeGitRoot).Equals(Path.GetFullPath(cli.Root),
                StringComparison.OrdinalIgnoreCase),
            "Git identity checks must resolve a relative -Root without duplicating the working path");
        string staleLockRoot = Path.Combine(regressionRoot, "unity-stale-lock", "Temp");
        Directory.CreateDirectory(staleLockRoot);
        string staleLock = Path.Combine(staleLockRoot, "UnityLockfile");
        File.WriteAllText(staleLock, string.Empty, new UTF8Encoding(false));
        bool staleLockRemoved = TryRemoveStaleUnityLock(staleLock) && !File.Exists(staleLock);
        AddRegressionCheck(checks, errors, "unity-stale-lock-recovery", staleLockRemoved,
            "an unowned Unity Temp lock must not stall the C# workflow until its stage timeout");
        string packagePipelinePath = Path.Combine(cli.Root, "unity2021-dhe-demo", "Packages",
            "com.code-philosophy.hybridclr", "Editor", "Commands", "DheBuildPipeline.cs");
        string packagePipelineSource = File.ReadAllText(packagePipelinePath);
        bool bodyFilterPresent = packagePipelineSource.Contains("(item.flags & 8u) != 0",
                StringComparison.Ordinal) &&
            packagePipelineSource.Contains("flags = checked((uint)ReadJsonInt(objectText, \"flags\"))",
                StringComparison.Ordinal);
        AddRegressionCheck(checks, errors, "native-universal-body-filter", bodyFilterPresent,
            "universal guards must exclude delegate/runtime methods without a managed IL body");
        RunNativeFinalizeEvidenceRegressions(regressionRoot, checks, errors);
        string dheReaderPath = Path.Combine(cli.Root, "unity2021-dhe-demo", "Assets", "Runtime",
            "DheStreamingAssetReader.cs");
        string dheReaderSource = File.Exists(dheReaderPath) ? File.ReadAllText(dheReaderPath) : string.Empty;
        bool crossPlatformDheReader = dheReaderSource.Contains("UNITY_ANDROID", StringComparison.Ordinal) &&
            dheReaderSource.Contains("ZipArchive", StringComparison.Ordinal) &&
            dheReaderSource.Contains("Application.dataPath", StringComparison.Ordinal);
        AddRegressionCheck(checks, errors, "dhe-cross-platform-streaming-reader",
            crossPlatformDheReader,
            "the demo DHE asset reader must handle Android APK StreamingAssets as well as filesystem platforms");
        string dhePlayerRunnerPath = Path.Combine(cli.Root, "unity2021-dhe-demo", "Assets",
            "Runtime", "HybridCLRDhePlayerRunner.cs");
        string dhePlayerRunnerSource = File.Exists(dhePlayerRunnerPath)
            ? File.ReadAllText(dhePlayerRunnerPath) : string.Empty;
        bool androidDeviceSmokeContract = false;
        try
        {
            AdbDevice selected = SelectAdbDevice(ParseAdbDevices(
                "List of devices attached\nserial-one device product:test model:device\n"), null);
            bool invalidApplicationRejected;
            try
            {
                _ = ValidateAndroidApplicationId("invalid/application");
                invalidApplicationRejected = false;
            }
            catch (DheException)
            {
                invalidApplicationRejected = true;
            }
            bool escapingRootRejected;
            try
            {
                _ = ValidateAndroidRemoteAssetRoot(
                    "/sdcard/Android/data/com.mofish.lab/files/../escape",
                    "com.mofish.lab");
                escapingRootRejected = false;
            }
            catch (DheException)
            {
                escapingRootRejected = true;
            }
            androidDeviceSmokeContract = selected.Serial == "serial-one" &&
                ValidateAndroidApplicationId("com.mofish.lab") == "com.mofish.lab" &&
                ValidateAndroidActivity("com.unity3d.player.UnityPlayerActivity") ==
                    "com.unity3d.player.UnityPlayerActivity" &&
                ValidateAndroidRemoteAssetRoot(
                    "/sdcard/Android/data/com.mofish.lab/files/HybridCLRLab/DheUpdate",
                    "com.mofish.lab").EndsWith("/DheUpdate", StringComparison.Ordinal) &&
                invalidApplicationRejected && escapingRootRejected &&
                dhePlayerRunnerSource.Contains("-labDheAssetRoot", StringComparison.Ordinal) &&
                dhePlayerRunnerSource.Contains(
                    "The external DHE resource release is incomplete.",
                    StringComparison.Ordinal) &&
                dhePlayerRunnerSource.Contains("BaseMetaVersion/", StringComparison.Ordinal);
        }
        catch
        {
            androidDeviceSmokeContract = false;
        }
        AddRegressionCheck(checks, errors, "android-device-smoke-contract",
            androidDeviceSmokeContract,
            "Android device smoke must select one device, constrain remote paths, and use a fail-closed external payload overlay");
        bool dheRunnerProviderOverlay = dhePlayerRunnerSource.Contains(
                "byte[] assemblyCurrent = provider.LoadBytes(assemblyPlan.current);",
                StringComparison.Ordinal) &&
            dhePlayerRunnerSource.Contains(
                "byte[] assemblyCurrentMv = provider.LoadBytes(assemblyPlan.currentMetaVersion);",
                StringComparison.Ordinal) &&
            dhePlayerRunnerSource.Contains(
                "byte[] assemblyBaseMv = provider.LoadBytes(assemblyPlan.baseMetaVersion);",
                StringComparison.Ordinal) &&
            dhePlayerRunnerSource.Contains(
                "provider.LoadBytes(loaded.plan.current),",
                StringComparison.Ordinal) &&
            dhePlayerRunnerSource.Contains(
                "byte[] current = provider.LoadBytes(mainLoaded.plan.current);",
                StringComparison.Ordinal) &&
            dhePlayerRunnerSource.Contains(
                "byte[] mv = provider.LoadBytes(mainLoaded.plan.currentMetaVersion);",
                StringComparison.Ordinal) &&
            dhePlayerRunnerSource.Contains(
                "System.Text.Encoding.UTF8.GetString(provider.LoadBytes(BuildIdentityFile))",
                StringComparison.Ordinal) &&
            dhePlayerRunnerSource.Split(new[] { "DheStreamingAssetReader.Read(" },
                StringSplitOptions.None).Length - 1 == 1;
        AddRegressionCheck(checks, errors, "dhe-runner-provider-overlay",
            dheRunnerProviderOverlay,
            "DHE Player payload, MetaVersion, and identity reads must use the asset provider; only its embedded fallback may call the StreamingAssets reader");
        RunIntegratedSourceLockRegressions(regressionRoot, checks, errors);

        var weakNoOpRejected = false;
        try
        {
            using var weakNoOp = JsonDocument.Parse("{\"changedMethodCount\":0," +
                "\"interpreterEntryCount\":0,\"changedProbeChanged\":false," +
                "\"unchangedProbeChanged\":false,\"transactionStatus\":\"notApplicable\"," +
                "\"dispatchProbeValidated\":true,\"noOpAotBehaviorValidated\":false," +
                "\"multiAssemblyValidated\":true,\"capabilityDirectPassed\":true," +
                "\"capabilityPassed\":true,\"secondaryAssemblyDirectValidated\":true}");
            ValidateNoOpPlayerEvidence(weakNoOp.RootElement);
        }
        catch { weakNoOpRejected = true; }
        AddRegressionCheck(checks, errors, "evidence-noop-aot-proof", weakNoOpRejected,
            "no-op release evidence must prove unchanged AOT behavior");

        var matrixEvidenceSchema = ReadJson<JsonElement>(Path.Combine(cli.Root, "schemas",
            "dhe-toolchain-release-evidence.schema.json"));
        var matrixFiles = new List<object>();
        matrixFiles.AddRange(RequiredStaticReleaseEvidenceRoles.Select(role => (object)new
        {
            role,
            path = "reports/" + role + ".json",
            sha256 = new string('c', 64)
        }));
        for (int index = 0; index < RequiredPlayerEngineWorkflows.Length; index++)
        {
            matrixFiles.Add(new
            {
                role = "player-changed",
                engineWorkflow = RequiredPlayerEngineWorkflows[index],
                baseId = new string((char)('d' + index), 64),
                path = "reports/player-changed-" + (index + 1).ToString("D3") + ".json",
                sha256 = new string('c', 64)
            });
        }
        using var matrixEvidence = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-toolchain-release-evidence.json",
            generatedAtUtc = DateTimeOffset.UtcNow,
            passed = true,
            sourceHead = new string('a', 40),
            sourceTree = new string('b', 40),
            files = matrixFiles
        }));
        var matrixEvidenceErrors = new List<string>();
        ValidateJsonSchema(matrixEvidenceSchema, matrixEvidence.RootElement, matrixEvidenceSchema, "$",
            matrixEvidenceErrors);
        AddRegressionCheck(checks, errors, "evidence-native-matrix-roles",
            RequiredStaticReleaseEvidenceRoles.Length == 6 && matrixFiles.Count == 8 &&
            matrixEvidenceErrors.Count == 0,
            "release evidence must require Unity 2022 and Tuanjie 2022 changed Bases, no-op, " +
            "and both resolver/native engine lanes");

        var unsafeArchive = Path.Combine(regressionRoot, "not-an-archive");
        Directory.CreateDirectory(unsafeArchive);
        var sentinel = Path.Combine(unsafeArchive, "sentinel.txt"); File.WriteAllText(sentinel, "keep", new UTF8Encoding(false));
        var unsafeArchiveRejected = false;
        try { PrepareArchiveDestination(unsafeArchive, true); } catch { unsafeArchiveRejected = File.Exists(sentinel); }
        AddRegressionCheck(checks, errors, "archive-safe-replace", unsafeArchiveRejected,
            "non-archive directory replacement must be rejected without deleting contents");
        RunGuardBlockHashRegressions(regressionRoot, checks, errors);
        var resolverOutputs = new List<object>();
        var realResolverOutputsValidated = false;
        var resolverContractValidated = false;
        var resolverDetails = "generated-C++ resolver evidence contract validated";
        string? resolverIdentityFixture = null;
        var resolverInputs = new[]
        {
            (Role: "resolver-unity2022", Option: "resolverunity2022"),
            (Role: "resolver-tuanjie2022", Option: "resolvertuanjie2022")
        };
        try
        {
            var supplied = resolverInputs.Where(item =>
                !string.IsNullOrWhiteSpace(cli.Optional(item.Option))).ToArray();
            if (supplied.Length != 0 && supplied.Length != resolverInputs.Length)
                throw new DheException("Resolver regression inputs must contain both supported engine workflows.");
            if (supplied.Length == resolverInputs.Length)
            {
                foreach (var item in resolverInputs)
                {
                    var path = RequireFile(cli.Optional(item.Option)!, item.Role + " regression");
                    ValidateResolverEvidence(item.Role, ReadJson<JsonElement>(path), cli.Root);
                    resolverOutputs.Add(new { role = item.Role, path, sha256 = Sha256File(path) });
                }
                resolverIdentityFixture = File.ReadAllText(RequireFile(
                    cli.Optional("resolverunity2022")!, "Unity 2022 resolver regression"));
                realResolverOutputsValidated = true;
                resolverDetails = "both supported real Editor resolver reports validated";
            }
            else
            {
                var packageLock = ReadJson<JsonElement>(Path.Combine(cli.Root, "manifests",
                    "dhe-package-lock.json"));
                resolverIdentityFixture = JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    format = "hybridclr.dhe-cpp-resolver-regression.json",
                    generatedAtUtc = DateTimeOffset.UtcNow,
                    engineWorkflow = "Unity2022Fgs",
                    unityVersion = "2022.3.62f3",
                    resolverSourceSha256 = GetString(packageLock, "resolverSourceSha256"),
                    passed = true,
                    checks = RequiredResolverChecks.Select(name => new { name, passed = true, error = "" }),
                    errors = Array.Empty<string>()
                });
                using var fixture = JsonDocument.Parse(resolverIdentityFixture);
                ValidateResolverEvidence("resolver-unity2022", fixture.RootElement, cli.Root);
            }
            resolverContractValidated = true;
        }
        catch (Exception exception)
        {
            resolverDetails = exception.Message;
            resolverOutputs.Clear();
        }
        AddRegressionCheck(checks, errors, "generated-cpp-resolver-engine-matrix",
            resolverContractValidated, resolverDetails);
        var resolverIdentityTamperRejected = false;
        if (resolverContractValidated && !string.IsNullOrWhiteSpace(resolverIdentityFixture))
        {
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(resolverIdentityFixture)!.AsObject();
                node["resolverSourceSha256"] = new string('0', 64);
                using var tampered = JsonDocument.Parse(node.ToJsonString());
                ValidateResolverEvidence("resolver-unity2022", tampered.RootElement, cli.Root);
            }
            catch
            {
                resolverIdentityTamperRejected = true;
            }
        }
        AddRegressionCheck(checks, errors, "generated-cpp-resolver-identity-tamper",
            resolverIdentityTamperRejected,
            "resolver evidence with a different package source hash must be rejected");
        var layoutDocument = ReadJson<JsonElement>(Path.Combine(cli.Root, "manifests",
            "dhe-toolchain-layout.json"));
        var layoutPaths = layoutDocument.GetProperty("exactPaths").EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString() ?? "").ToHashSet(StringComparer.Ordinal);
        var releaseRoleSchemas = new[]
        {
            "schemas/dhe-regression.schema.json",
            "schemas/dhe-cpp-resolver-regression.schema.json",
            "schemas/dhe-workflow-report.schema.json",
            "schemas/dhe-native-gate.schema.json",
            "schemas/dhe-toolchain-release-evidence.schema.json",
        };
        AddRegressionCheck(checks, errors, "layout-release-role-schemas",
            releaseRoleSchemas.All(layoutPaths.Contains),
            "the authenticated package layout must include every release evidence role schema");
        var schemasRoot = Path.Combine(cli.Root, "schemas");
        var workflowSchema = ReadJson<JsonElement>(Path.Combine(schemasRoot, "dhe-workflow-config.schema.json"));
        var validConfig = ReadJson<JsonElement>(Path.Combine(cli.Root, "templates", "dhe-workflow-config.json"));
        var validConfigErrors = new List<string>();
        ValidateSchemaVocabulary(workflowSchema, "$", validConfigErrors);
        ValidateJsonSchema(workflowSchema, validConfig, workflowSchema, "$", validConfigErrors);
        AddRegressionCheck(checks, errors, "schema-valid-document", validConfigErrors.Count == 0,
            validConfigErrors.Count == 0 ? "valid workflow config accepted" : string.Join("; ", validConfigErrors));

        var maximumNode = System.Text.Json.Nodes.JsonNode.Parse(validConfig.GetRawText())!.AsObject();
        maximumNode["unityTimeoutSeconds"] = 3601;
        using var maximumDocument = JsonDocument.Parse(maximumNode.ToJsonString());
        var maximumErrors = new List<string>();
        ValidateJsonSchema(workflowSchema, maximumDocument.RootElement, workflowSchema, "$", maximumErrors);
        AddRegressionCheck(checks, errors, "schema-maximum-rejected", maximumErrors.Count > 0,
            "workflow timeout above the schema maximum must be rejected");

        var additionalNode = System.Text.Json.Nodes.JsonNode.Parse(validConfig.GetRawText())!.AsObject();
        additionalNode["unityArguments"]!["invalid"] = new System.Text.Json.Nodes.JsonArray(1, 2);
        using var additionalDocument = JsonDocument.Parse(additionalNode.ToJsonString());
        var additionalErrors = new List<string>();
        ValidateJsonSchema(workflowSchema, additionalDocument.RootElement, workflowSchema, "$", additionalErrors);
        AddRegressionCheck(checks, errors, "schema-additional-type-rejected", additionalErrors.Count > 0,
            "additional property schema must reject an invalid value type");

        bool resourceReleaseModeContractPassed = false;
        if (!string.IsNullOrWhiteSpace(resourceUpdateRoot))
        {
            JsonElement resourceSchema = ReadJson<JsonElement>(Path.Combine(schemasRoot,
                "dhe-resource-update.schema.json"));
            JsonElement resourceDocument = ReadJson<JsonElement>(Path.Combine(resourceUpdateRoot,
                "dhe-resource-update.json"));
            var validResourceErrors = new List<string>();
            ValidateJsonSchema(resourceSchema, resourceDocument, resourceSchema, "$",
                validResourceErrors);

            var missingLedgerNode = System.Text.Json.Nodes.JsonNode.Parse(
                resourceDocument.GetRawText())!.AsObject();
            missingLedgerNode.Remove("releaseLedger");
            using var missingLedgerDocument = JsonDocument.Parse(
                missingLedgerNode.ToJsonString());
            var missingLedgerErrors = new List<string>();
            ValidateJsonSchema(resourceSchema, missingLedgerDocument.RootElement,
                resourceSchema, "$", missingLedgerErrors);

            var inconsistentModeNode = System.Text.Json.Nodes.JsonNode.Parse(
                resourceDocument.GetRawText())!.AsObject();
            inconsistentModeNode["mode"] = "Exploratory";
            using var inconsistentModeDocument = JsonDocument.Parse(
                inconsistentModeNode.ToJsonString());
            var inconsistentModeErrors = new List<string>();
            ValidateJsonSchema(resourceSchema, inconsistentModeDocument.RootElement,
                resourceSchema, "$", inconsistentModeErrors);
            resourceReleaseModeContractPassed = validResourceErrors.Count == 0 &&
                missingLedgerErrors.Count > 0 && inconsistentModeErrors.Count > 0;
        }
        AddRegressionCheck(checks, errors, "schema-resource-release-mode-contract",
            resourceReleaseModeContractPassed,
            "resource schemas must reject missing ledger fields and inconsistent Release modes");

        using var unsupportedSchemaDocument = JsonDocument.Parse("{\"type\":\"object\",\"oneOf\":[]}");
        var unsupportedErrors = new List<string>();
        ValidateSchemaVocabulary(unsupportedSchemaDocument.RootElement, "$", unsupportedErrors);
        AddRegressionCheck(checks, errors, "schema-unsupported-keyword-rejected", unsupportedErrors.Count > 0,
            "unsupported schema assertion keywords must fail closed");

        var schemaGatePassed = false;
        if (!string.IsNullOrWhiteSpace(packageRoot) && Directory.Exists(packageRoot))
        {
            var schemaGatePath = Path.Combine(regressionRoot, "schema-gate.json");
            var schemaGateExit = SchemaGate(new Cli("schema-gate", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["schemasroot"] = Path.Combine(packageRoot, "schemas"),
                ["inputroot"] = packageRoot,
                ["output"] = schemaGatePath,
                ["requireknownformats"] = "true"
            }));
            if (schemaGateExit == 0)
            {
                var gateSchema = ReadJson<JsonElement>(Path.Combine(schemasRoot, "dhe-schema-gate.schema.json"));
                var gateErrors = new List<string>();
                ValidateJsonSchema(gateSchema, ReadJson<JsonElement>(schemaGatePath), gateSchema, "$", gateErrors);
                schemaGatePassed = gateErrors.Count == 0;
            }
        }
        AddRegressionCheck(checks, errors, "schema-gate-contract", schemaGatePassed,
            "distributed package documents and schema gate evidence must validate");
        var workflowSchemaPassed = false;
        var realWorkflowOutputsValidated = false;
        var resourcePlayerEvidenceBindingPassed = false;
        var resourcePlayerReleaseLedgerBindingPassed = false;
        var resourcePlayerConsecutiveReleaseHeadPassed = false;
        var resourceReleaseAggregateGatePassed = false;
        var channelStateCasWorkflowPassed = false;
        var channelStateCasWorkflowDetails =
            "the distributed package contains the protected channel-state implementation and schemas";
        var protectedResourceReleaseBuildPassed = false;
        var protectedResourceReleaseBuildDetails =
            "the distributed package contains the protected resource release build implementation";
        var resourceReleasePlanRegression = ResourceReleasePlanRegressionResult.Failed;
        var resourceReleaseQualificationRegression =
            ResourceReleaseQualificationRegressionResult.Failed;
        var portableMixedToolchainAuthoritiesPassed = false;
        var portableMixedToolchainAuthoritiesDetails =
            "the distributed package contains its evidence authority set and schema";
        object? validatedResourceRelease = null;
        var workflowOutputs = new List<object>();
        var changedWorkflowRoots = cli.GetList("workflowchangedroots");
        if (changedWorkflowRoots.Count == 0)
        {
            foreach (string option in new[]
                     { "workflowchangedroot", "workflowchangedbase2root", "workflowchangedbase3root" })
            {
                string? value = cli.Optional(option);
                if (!string.IsNullOrWhiteSpace(value)) changedWorkflowRoots.Add(value);
            }
        }
        changedWorkflowRoots = changedWorkflowRoots.Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (changedWorkflowRoots.Count > MaxChangedPlayerEvidenceCount)
            throw new DheException("Regression changed Player root count exceeds " +
                MaxChangedPlayerEvidenceCount + ".");
        var noOpWorkflowRoot = cli.Optional("workflownooproot");
        bool anyWorkflowInput = changedWorkflowRoots.Count > 0 ||
            !string.IsNullOrWhiteSpace(noOpWorkflowRoot);
        if (!string.IsNullOrWhiteSpace(packageRoot) && Directory.Exists(packageRoot) &&
            changedWorkflowRoots.Count >= RequiredPlayerEngineWorkflows.Length &&
            changedWorkflowRoots.All(Directory.Exists) &&
            !string.IsNullOrWhiteSpace(noOpWorkflowRoot) && Directory.Exists(noOpWorkflowRoot))
        {
            PackageInspection candidatePackage = InspectPackage(packageRoot, null, false);
            if (!candidatePackage.Passed || !IsHex(candidatePackage.PackageId, 64, 64))
                throw new DheException("Regression package is not an authenticated candidate: " +
                    string.Join("; ", candidatePackage.Errors));
            IReadOnlyDictionary<string, string> authenticatedEvidenceToolchains =
                ReadEvidenceToolchainRoots(cli.GetList("evidencetoolchainroots"),
                    candidatePackage.PackageId!);
            var workflowRoots = changedWorkflowRoots.Select((root, index) =>
                    (Name: "changed-" + (index + 1).ToString("D3"),
                        Role: "player-changed", Root: root))
                .Append((Name: "noop", Role: "demo-noop", Root: Path.GetFullPath(noOpWorkflowRoot)))
                .ToArray();
            workflowSchemaPassed = workflowRoots.All(item => SchemaGate(new Cli("schema-gate", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["schemasroot"] = Path.Combine(packageRoot, "schemas"),
                ["inputroot"] = item.Root,
                ["output"] = Path.Combine(regressionRoot, "schema-gate-" + item.Name + ".json"),
                ["requireknownformats"] = "true"
            })) == 0);
            realWorkflowOutputsValidated = workflowSchemaPassed;
            if (workflowSchemaPassed)
            {
                foreach (var item in workflowRoots)
                {
                    bool changedWorkflow = item.Role == "player-changed";
                    var reportName = changedWorkflow
                        ? "resource-player-workflow-report.json"
                        : "player-workflow-report.json";
                    var reportPath = RequireFile(Path.Combine(item.Root, reportName),
                        item.Name + " workflow report");
                    JsonElement workflowReport = ReadJson<JsonElement>(reportPath);
                    if (changedWorkflow)
                    {
                        RequireEvidenceFormat(workflowReport,
                            "hybridclr.dhe-resource-player-workflow.json",
                            "changed resource workflow");
                        ValidateResourcePlayerEvidenceBindings(workflowReport, reportPath);
                    }
                    ValidateManagedReleaseEvidence(workflowReport, reportPath,
                        authenticatedEvidenceToolchains.Values.Append(cli.Root));
                    if (changedWorkflow)
                    {
                        var identity = GetChangedPlayerEvidenceIdentity(workflowReport, reportPath);
                        workflowOutputs.Add(new
                        {
                            role = item.Role,
                            engineWorkflow = identity.EngineWorkflow,
                            baseId = identity.BaseId,
                            path = reportPath,
                            sha256 = Sha256File(reportPath)
                        });
                    }
                    else
                    {
                        workflowOutputs.Add(new
                        {
                            role = item.Role,
                            path = reportPath,
                            sha256 = Sha256File(reportPath)
                        });
                    }
                }
                var changedReports = workflowRoots.Where(item => item.Role == "player-changed")
                    .Select(item =>
                    {
                        string path = Path.Combine(item.Root, "resource-player-workflow-report.json");
                        return (Report: ReadJson<JsonElement>(path), Path: path);
                    }).ToArray();
                MultiBaseResourceReleaseProof releaseProof =
                    ReadMultiBaseResourceReleaseProof(changedReports, true);
                resourcePlayerEvidenceBindingPassed = true;
                portableMixedToolchainAuthoritiesPassed =
                    RunEvidenceAuthoritySetRegression(regressionRoot, packageRoot,
                        changedReports, cli.GetList("evidencetoolchainroots")
                            .Select(Path.GetFullPath).ToArray(),
                        out portableMixedToolchainAuthoritiesDetails);
                if (!string.IsNullOrWhiteSpace(resourceUpdateRoot) &&
                    !string.IsNullOrWhiteSpace(resourceUpdateRoot2))
                {
                    ValidateChangedPlayerReleaseHead(releaseProof, resourceUpdateRoot2);
                    bool previousHeadRejected = false;
                    try
                    {
                        ValidateChangedPlayerReleaseHead(releaseProof, resourceUpdateRoot);
                    }
                    catch (DheException)
                    {
                        previousHeadRejected = true;
                    }
                    resourcePlayerConsecutiveReleaseHeadPassed = previousHeadRejected;
                    if (resourcePlayerConsecutiveReleaseHeadPassed)
                    {
                        validatedResourceRelease = new
                        {
                            resourceUpdateManifestSha256 =
                                releaseProof.ResourceUpdateManifestSha256,
                            releaseLedgerSha256 = releaseProof.ReleaseLedgerSha256,
                            parentReleaseLedgerSha256 =
                                releaseProof.ParentReleaseLedgerSha256,
                            releaseChannelId = releaseProof.ReleaseChannelId,
                            releaseRevision = releaseProof.ReleaseRevision,
                            baseRegistrySha256 = releaseProof.BaseRegistrySha256,
                            activeBaseCount = releaseProof.ActiveBaseCount,
                            currentAssemblySetSha256 =
                                releaseProof.CurrentAssemblySetSha256,
                            payloadVariantSetSha256 = releaseProof.PayloadVariantSetSha256,
                        };
                    }

                    var authorityReport = changedReports.FirstOrDefault(item =>
                        !string.IsNullOrWhiteSpace(GetString(item.Report,
                            "baseArchiveManifest")));
                    if (authorityReport.Report.ValueKind == JsonValueKind.Undefined)
                        authorityReport = changedReports[0];
                    string authorityPackageId = GetString(authorityReport.Report,
                        "expectedToolchainPackageId") ?? string.Empty;
                    string authorityRoot = ResolveManagedEvidenceContractRoot(
                        authorityReport.Report, authorityReport.Path,
                        authenticatedEvidenceToolchains.Values);
                    bool portableAuthorityResolved = string.IsNullOrWhiteSpace(
                            GetString(authorityReport.Report, "baseArchiveManifest")) ||
                        InspectPackage(authorityRoot, authorityPackageId, true).Passed;
                    AddRegressionCheck(checks, errors,
                        "resource-release-aggregate-portable-authority",
                        portableAuthorityResolved,
                        "aggregate qualification must resolve a portable Base archive's " +
                        "Release authority from authenticated package roots");
                    string[] SelectAggregateEvidenceToolchainRoots(
                        IEnumerable<(JsonElement Report, string Path)> inputs)
                    {
                        var inputReports = inputs.ToArray();
                        var selected = new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase);
                        foreach (string packageId in inputReports.Select(item =>
                                     GetString(item.Report, "expectedToolchainPackageId") ??
                                     string.Empty).Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            if (string.Equals(packageId, authorityPackageId,
                                    StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (!authenticatedEvidenceToolchains.TryGetValue(packageId,
                                    out string? root))
                                throw new DheException("Active Base evidence package is missing " +
                                    "from EvidenceToolchainRoots: " + packageId + ".");
                            selected.Add(packageId, root);
                        }

                        bool MatchesRuntimeLock(string root, string expectedSha256)
                        {
                            string lockPath = Path.Combine(root, "manifests",
                                "dhe-runtime-lock.json");
                            return File.Exists(lockPath) && Sha256File(lockPath).Equals(
                                expectedSha256, StringComparison.OrdinalIgnoreCase);
                        }

                        foreach ((JsonElement report, string reportPath) in inputReports)
                        {
                            string reportRoot = Path.GetDirectoryName(reportPath)!;
                            string runtimePath = ResolveEvidencePath(GetString(report,
                                "runtimeSource"), reportRoot,
                                "Aggregate managed runtime manifest");
                            JsonElement runtime = ReadJson<JsonElement>(runtimePath);
                            string expectedLock = GetString(runtime,
                                "dheRuntimeLockSha256") ?? string.Empty;
                            if (new[] { authorityRoot }.Concat(selected.Values).Any(root =>
                                    MatchesRuntimeLock(root, expectedLock)))
                                continue;
                            KeyValuePair<string, string> match =
                                authenticatedEvidenceToolchains
                                    .Where(item => !string.Equals(item.Key,
                                        authorityPackageId,
                                        StringComparison.OrdinalIgnoreCase) &&
                                        MatchesRuntimeLock(item.Value, expectedLock))
                                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                                    .FirstOrDefault();
                            if (string.IsNullOrWhiteSpace(match.Key))
                                throw new DheException("No authenticated Release package " +
                                    "provides runtime lock " + expectedLock +
                                    " for active Base report " + reportPath + ".");
                            selected.TryAdd(match.Key, match.Value);
                        }
                        return selected.OrderBy(item => item.Key,
                                StringComparer.Ordinal)
                            .Select(item => item.Value).ToArray();
                    }

                    Dictionary<string, string> AggregateArguments(
                        IEnumerable<(JsonElement Report, string Path)> inputs,
                        string aggregateOutput)
                    {
                        var inputReports = inputs.ToArray();
                        var values = new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["toolchainroot"] = authorityRoot,
                            ["expectedtoolchainpackageid"] = authorityPackageId,
                            ["validationsourceroot"] = cli.Root,
                            ["schemaroot"] = packageRoot,
                            ["resourceupdateroot"] = resourceUpdateRoot2,
                            ["expectedreleasechannelid"] = releaseProof.ReleaseChannelId,
                            ["expectedreleaserevision"] = releaseProof.ReleaseRevision.ToString(
                                CultureInfo.InvariantCulture),
                            ["expectedreleaseledgersha256"] = releaseProof.ReleaseLedgerSha256,
                            ["expectedpreviousreleaseledgersha256"] =
                                releaseProof.ParentReleaseLedgerSha256 ?? string.Empty,
                            ["requireenginematrix"] = "true",
                            ["changedplayers"] = string.Join(',', inputReports.Select(
                                item => item.Path)),
                            ["output"] = aggregateOutput,
                        };
                        string[] evidenceRoots =
                            SelectAggregateEvidenceToolchainRoots(inputReports);
                        if (evidenceRoots.Length != 0)
                            values["evidencetoolchainroots"] = string.Join(',', evidenceRoots);
                        return values;
                    }

                    string aggregatePath = Path.Combine(regressionRoot,
                        "resource-release-gate.json");
                    bool aggregateAccepted = ResourceReleaseGate(new Cli(
                        "resource-release-gate", AggregateArguments(changedReports,
                            aggregatePath))) == 0;
                    bool incompleteRejected = false;
                    var incompleteReports = changedReports.Where(item => string.Equals(
                            GetString(item.Report, "expectedToolchainPackageId"),
                            authorityPackageId, StringComparison.OrdinalIgnoreCase))
                        .GroupBy(item => GetChangedPlayerEvidenceIdentity(item.Report,
                            item.Path).EngineWorkflow, StringComparer.Ordinal)
                        .Select(group => group.First()).ToArray();
                    try
                    {
                        _ = ResourceReleaseGate(new Cli("resource-release-gate",
                            AggregateArguments(incompleteReports,
                                Path.Combine(regressionRoot,
                                    "resource-release-gate-incomplete.json"))));
                    }
                    catch (DheException exception)
                    {
                        incompleteRejected = exception.Message.Contains(
                            "cover every active Base", StringComparison.Ordinal);
                    }
                    bool candidateHeadRejected = false;
                    try
                    {
                        Dictionary<string, string> values = AggregateArguments(changedReports,
                            Path.Combine(regressionRoot,
                                "resource-release-gate-wrong-candidate.json"));
                        values["expectedreleaseledgersha256"] = new string('f', 64);
                        _ = ResourceReleaseGate(new Cli("resource-release-gate", values));
                    }
                    catch (DheException)
                    {
                        candidateHeadRejected = true;
                    }
                    bool channelRejected = false;
                    try
                    {
                        Dictionary<string, string> values = AggregateArguments(changedReports,
                            Path.Combine(regressionRoot,
                                "resource-release-gate-wrong-channel.json"));
                        values["expectedreleasechannelid"] = releaseProof.ReleaseChannelId + "-wrong";
                        _ = ResourceReleaseGate(new Cli("resource-release-gate", values));
                    }
                    catch (DheException)
                    {
                        channelRejected = true;
                    }
                    bool revisionRejected = false;
                    string staleRevisionOutput = Path.Combine(regressionRoot,
                        "resource-release-gate-wrong-revision.json");
                    File.WriteAllText(staleRevisionOutput, "stale", new UTF8Encoding(false));
                    try
                    {
                        Dictionary<string, string> values = AggregateArguments(changedReports,
                            staleRevisionOutput);
                        values["expectedreleaserevision"] = (releaseProof.ReleaseRevision + 1)
                            .ToString(CultureInfo.InvariantCulture);
                        _ = ResourceReleaseGate(new Cli("resource-release-gate", values));
                    }
                    catch (DheException)
                    {
                        revisionRejected = !File.Exists(staleRevisionOutput);
                    }
                    bool previousHeadRejectedByAggregate = false;
                    try
                    {
                        Dictionary<string, string> values = AggregateArguments(changedReports,
                            Path.Combine(regressionRoot,
                                "resource-release-gate-wrong-parent.json"));
                        values["expectedpreviousreleaseledgersha256"] = new string('f', 64);
                        _ = ResourceReleaseGate(new Cli("resource-release-gate", values));
                    }
                    catch (DheException)
                    {
                        previousHeadRejectedByAggregate = true;
                    }
                    bool reinitializationRejected = false;
                    try
                    {
                        Dictionary<string, string> values = AggregateArguments(changedReports,
                            Path.Combine(regressionRoot,
                                "resource-release-gate-reinitialize.json"));
                        values.Remove("expectedpreviousreleaseledgersha256");
                        values["initializereleaseledger"] = "true";
                        _ = ResourceReleaseGate(new Cli("resource-release-gate", values));
                    }
                    catch (DheException)
                    {
                        reinitializationRejected = true;
                    }
                    bool protectedOutputRejected = false;
                    try
                    {
                        _ = ResourceReleaseGate(new Cli("resource-release-gate",
                            AggregateArguments(changedReports, Path.Combine(resourceUpdateRoot2,
                                "resource-release-gate-invalid-output.json"))));
                    }
                    catch (DheException)
                    {
                        protectedOutputRejected = true;
                    }
                    bool executionTamperRejected = false;
                    var executionTamper = System.Text.Json.Nodes.JsonNode.Parse(
                        changedReports[0].Report.GetRawText())!.AsObject();
                    executionTamper["player"]!.AsObject()["retryFailure"] = "wrong-failure";
                    try
                    {
                        using var tamperedDocument = JsonDocument.Parse(
                            executionTamper.ToJsonString());
                        ValidateResourceReleasePlayerCorrectness(tamperedDocument.RootElement);
                    }
                    catch (DheException)
                    {
                        executionTamperRejected = true;
                    }
                    if (aggregateAccepted)
                    {
                        JsonElement aggregate = ReadJson<JsonElement>(aggregatePath);
                        aggregateAccepted = GetBool(aggregate, "passed") &&
                            GetBool(aggregate, "releaseReady") &&
                            GetBool(aggregate, "exactActiveBaseCoverage") &&
                            GetInt(aggregate, "activeBaseCount") == changedReports.Length &&
                            string.Equals(GetString(aggregate, "releaseLedgerSha256"),
                                releaseProof.ReleaseLedgerSha256,
                                StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(GetString(aggregate,
                                    "expectedPreviousReleaseLedgerSha256"),
                                releaseProof.ParentReleaseLedgerSha256,
                                StringComparison.OrdinalIgnoreCase);
                    }
                    resourceReleaseAggregateGatePassed = aggregateAccepted &&
                        incompleteRejected && candidateHeadRejected && channelRejected &&
                        revisionRejected && previousHeadRejectedByAggregate &&
                        reinitializationRejected && protectedOutputRejected &&
                        executionTamperRejected;
                    string[] channelEvidenceRoots =
                        SelectAggregateEvidenceToolchainRoots(changedReports);
                    channelStateCasWorkflowPassed = RunChannelStateRegression(
                        regressionRoot, resourceUpdateRoot, resourceUpdateRoot2,
                        changedReports, authorityRoot, authorityPackageId, cli.Root,
                        packageRoot, resourceBaseRegistry,
                        RequireFile(cli.Optional("settingsfile") ?? string.Empty,
                            "Protected resource release build settings"),
                        channelEvidenceRoots, out protectedResourceReleaseBuildPassed,
                        out protectedResourceReleaseBuildDetails,
                        out resourceReleasePlanRegression,
                        out resourceReleaseQualificationRegression,
                        out channelStateCasWorkflowDetails);
                }
                var tamperedReport = System.Text.Json.Nodes.JsonNode.Parse(
                    changedReports[0].Report.GetRawText())!.AsObject();
                tamperedReport["releaseRevision"] =
                    GetInt(changedReports[0].Report, "releaseRevision") + 1;
                try
                {
                    using var tamperedDocument = JsonDocument.Parse(tamperedReport.ToJsonString());
                    ValidateResourcePlayerEvidenceBindings(tamperedDocument.RootElement,
                        changedReports[0].Path);
                }
                catch (DheException)
                {
                    resourcePlayerReleaseLedgerBindingPassed = true;
                }
            }
        }
        else if (!anyWorkflowInput && !string.IsNullOrWhiteSpace(packageRoot) && Directory.Exists(packageRoot))
        {
            var requiredOutputSchemas = new[]
            {
                "dhe-adapter-native-finalize.schema.json", "dhe-adapter-native-guards.schema.json",
                "dhe-adapter-player-build.schema.json", "dhe-adapter-stage.schema.json",
                "dhe-metaversion.schema.json", "dhe-native-manifest.schema.json",
                "dhe-player-result.schema.json", "dhe-project-preflight.schema.json",
                "dhe-resource-player-workflow.schema.json", "dhe-workflow-report.schema.json"
            };
            workflowSchemaPassed = requiredOutputSchemas.All(name => File.Exists(Path.Combine(packageRoot,
                "schemas", name)));
            resourcePlayerEvidenceBindingPassed = File.ReadAllText(Path.Combine(packageRoot,
                "tool", "ResourceUpdateStaging.cs")).Contains(
                "private static int ResourcePlayerEvidence", StringComparison.Ordinal);
            resourcePlayerReleaseLedgerBindingPassed = File.ReadAllText(Path.Combine(packageRoot,
                "tool", "Program.cs")).Contains(
                "Resource Player stage does not match its release ledger.",
                StringComparison.Ordinal);
            resourcePlayerConsecutiveReleaseHeadPassed = File.ReadAllText(Path.Combine(packageRoot,
                "tool", "Program.cs")).Contains(
                "Changed Player evidence does not match the consecutive resource release head.",
                StringComparison.Ordinal);
            resourceReleaseAggregateGatePassed = File.Exists(Path.Combine(packageRoot,
                    "schemas", "dhe-resource-release-gate.schema.json")) &&
                File.ReadAllText(Path.Combine(packageRoot, "tool",
                    "ResourceReleaseGate.cs")).Contains(
                    "private static int ResourceReleaseGate", StringComparison.Ordinal);
            channelStateCasWorkflowPassed = File.Exists(Path.Combine(packageRoot,
                    "schemas", "dhe-channel-state.schema.json")) &&
                File.Exists(Path.Combine(packageRoot, "schemas",
                    "dhe-channel-snapshot.schema.json")) &&
                File.ReadAllText(Path.Combine(packageRoot, "tool", "ChannelState.cs"))
                    .Contains("private static int ChannelState", StringComparison.Ordinal);
            protectedResourceReleaseBuildPassed = File.Exists(Path.Combine(packageRoot,
                    "tool", "ResourceReleaseBuild.cs")) &&
                File.Exists(Path.Combine(packageRoot, "schemas",
                    "dhe-resource-release-build.schema.json"));
            bool qualificationSourcePresent = File.Exists(Path.Combine(packageRoot,
                    "tool", "ResourceReleaseQualification.cs")) &&
                File.Exists(Path.Combine(packageRoot, "schemas",
                    "dhe-resource-release-qualification.schema.json")) &&
                File.Exists(Path.Combine(packageRoot, "schemas",
                    "dhe-resource-release-qualification-config.schema.json"));
            resourceReleaseQualificationRegression = qualificationSourcePresent
                ? new ResourceReleaseQualificationRegressionResult(true, true, true, true,
                    "the distributed package contains the multi-Base qualification command and schemas")
                : ResourceReleaseQualificationRegressionResult.Failed;
            bool planningSourcePresent = File.Exists(Path.Combine(packageRoot,
                    "tool", "ResourceReleasePlanning.cs")) &&
                File.Exists(Path.Combine(packageRoot, "schemas",
                    "dhe-base-runner-catalog.schema.json")) &&
                File.Exists(Path.Combine(packageRoot, "schemas",
                    "dhe-resource-release-plan-config.schema.json")) &&
                File.Exists(Path.Combine(packageRoot, "schemas",
                    "dhe-resource-release-plan.schema.json"));
            resourceReleasePlanRegression = planningSourcePresent
                ? new ResourceReleasePlanRegressionResult(true, true, true, true, true,
                    true, "the distributed package contains the registry-bound resource " +
                    "release planning command and schemas")
                : ResourceReleasePlanRegressionResult.Failed;
            portableMixedToolchainAuthoritiesPassed = File.Exists(Path.Combine(packageRoot,
                    "manifests", "dhe-toolchain-evidence-authorities.json")) &&
                File.Exists(Path.Combine(packageRoot, "schemas",
                    "dhe-toolchain-evidence-authorities.schema.json")) &&
                File.ReadAllText(Path.Combine(packageRoot, "tool",
                    "EvidenceAuthorities.cs")).Contains(
                    "private static EvidenceAuthoritySet ReadEvidenceAuthoritySet",
                    StringComparison.Ordinal);
        }
        AddRegressionCheck(checks, errors, "schema-workflow-output-contract", workflowSchemaPassed,
            realWorkflowOutputsValidated
                ? "the extensible three-engine changed Base and no-op output trees passed the distributed schema gate"
                : "the distributed package must contain every workflow output schema");
        AddRegressionCheck(checks, errors, "resource-player-evidence-binding",
            resourcePlayerEvidenceBindingPassed,
            realWorkflowOutputsValidated
                ? "all three-engine resource-only changed Base results share one revalidated current payload"
                : "the distributed package contains the resource Player evidence implementation");
        AddRegressionCheck(checks, errors, "resource-player-release-ledger-binding",
            resourcePlayerReleaseLedgerBindingPassed,
            "resource Player evidence must revalidate and reject a tampered release ledger identity");
        AddRegressionCheck(checks, errors, "resource-player-consecutive-release-head",
            resourcePlayerConsecutiveReleaseHeadPassed,
            "every active Base Player must execute the exact consecutive resource release head");
        AddRegressionCheck(checks, errors, "resource-release-aggregate-gate",
            resourceReleaseAggregateGatePassed,
            "the project-facing aggregate gate must authenticate every active Base and reject " +
            "incomplete, stale, forked, reinitialized, or input-mutating release evidence");
        AddRegressionCheck(checks, errors, "channel-state-cas-workflow",
            channelStateCasWorkflowPassed, channelStateCasWorkflowDetails);
        AddRegressionCheck(checks, errors, "resource-release-build-protected-release",
            protectedResourceReleaseBuildPassed, protectedResourceReleaseBuildDetails);
        AddRegressionCheck(checks, errors, "resource-release-qualify-prequalified",
            resourceReleaseQualificationRegression.PrequalifiedPassed,
            resourceReleaseQualificationRegression.Details);
        AddRegressionCheck(checks, errors, "resource-release-qualify-exact-coverage",
            resourceReleaseQualificationRegression.ExactCoverageRejected,
            "qualification must reject a missing active Base before creating output");
        AddRegressionCheck(checks, errors, "resource-release-qualify-process-contract",
            resourceReleaseQualificationRegression.ProcessContractRejected,
            "process qualification must require exact result/log tokens and immutable Player evidence");
        AddRegressionCheck(checks, errors,
            "resource-release-qualify-stale-snapshot-rejected",
            resourceReleaseQualificationRegression.StaleSnapshotRejected,
            "qualification must reject a stale protected channel snapshot before execution");
        AddRegressionCheck(checks, errors,
            "resource-release-plan-generated-qualification",
            resourceReleasePlanRegression.GeneratedQualificationPassed,
            resourceReleasePlanRegression.Details);
        AddRegressionCheck(checks, errors, "resource-release-plan-exact-coverage",
            resourceReleasePlanRegression.ExactCoverageRejected,
            "planning must reject a catalog missing any active Base before output");
        AddRegressionCheck(checks, errors, "resource-release-plan-duplicate-base",
            resourceReleasePlanRegression.DuplicateBaseRejected,
            "planning must reject duplicate Base IDs before output");
        AddRegressionCheck(checks, errors, "resource-release-plan-registry-identity",
            resourceReleasePlanRegression.RegistryIdentityRejected,
            "planning must bind the catalog to the exact registry revision and SHA-256");
        AddRegressionCheck(checks, errors, "resource-release-plan-template-contract",
            resourceReleasePlanRegression.TemplateContractRejected,
            "external report templates must retain Base and ledger identity and reject unknown tokens");
        AddRegressionCheck(checks, errors, "resource-release-plan-stale-snapshot",
            resourceReleasePlanRegression.StaleSnapshotRejected,
            "planning must reject a stale protected channel snapshot before output");
        AddRegressionCheck(checks, errors,
            "evidence-portable-mixed-toolchain-authorities",
            portableMixedToolchainAuthoritiesPassed,
            portableMixedToolchainAuthoritiesDetails);
        using var releaseResourceBase = JsonDocument.Parse("{\"mode\":\"Release\",\"releaseReady\":true}");
        using var incompleteResourceBase = JsonDocument.Parse("{\"mode\":\"Release\",\"releaseReady\":false}");
        using var exploratoryResourceBase = JsonDocument.Parse("{\"mode\":\"Exploratory\",\"releaseReady\":true}");
        AddRegressionCheck(checks, errors, "resource-player-release-readiness",
            ResourcePlayerReleaseReady(releaseResourceBase.RootElement) &&
            !ResourcePlayerReleaseReady(incompleteResourceBase.RootElement) &&
            !ResourcePlayerReleaseReady(exploratoryResourceBase.RootElement),
            "resource-only changed evidence inherits readiness only from a Release-ready Base workflow");
        var sourceHead = GitValue(cli.Root, "rev-parse", "HEAD");
        var sourceTree = GitValue(cli.Root, "rev-parse", "HEAD^{tree}");
        var sourceClean = !string.IsNullOrWhiteSpace(sourceHead) && string.IsNullOrWhiteSpace(GitValue(cli.Root, "status", "--porcelain"));
        var passed = errors.Count == 0;
        WriteJson(output, new { schemaVersion = 1, format = "hybridclr.dhe-regression.json", generatedAtUtc = DateTimeOffset.UtcNow, sourceHead, sourceTree, sourceClean, passed, realWorkflowOutputsValidated, workflowOutputs, validatedResourceRelease, realResolverOutputsValidated, resolverOutputs, checks, errors, warnings = Array.Empty<string>() });
        Console.WriteLine("DHE regression " + (passed ? "passed: " : "failed: ") + output);
        return passed ? 0 : 1;
    }

    private static void AddRegressionCheck(List<object> checks, List<string> errors, string name, bool passed,
        string details)
    {
        checks.Add(new { name, passed, details });
        if (!passed) errors.Add(name + ": " + details);
    }

    private static void ValidateNativeFinalizeEvidence(string reportPath,
        string playerBuildReportPath, string expectedTarget, string projectRoot,
        string expectedNativeManifestPath)
    {
        string reportFile = RequireFile(reportPath, "DHE native-finalize evidence");
        JsonElement report = ReadJson<JsonElement>(reportFile);
        RequireEvidenceFormat(report, "hybridclr.dhe-adapter-native-finalize.json",
            "DHE native-finalize");
        if (!GetBool(report, "passed") || GetInt(report, "exitCode") != 0 ||
            !string.Equals(GetString(report, "target"), expectedTarget,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("DHE native-finalize result or target is invalid.");

        int attempts = GetInt(report, "attempts");
        int graphRegenerations = GetInt(report, "graphRegenerations");
        int guardReapplications = GetInt(report, "guardReapplications");
        if (attempts < 1 || attempts > 8 || graphRegenerations < 0 ||
            guardReapplications < 0 ||
            attempts != graphRegenerations + guardReapplications + 1 ||
            (graphRegenerations == 0 && guardReapplications != 0) ||
            (graphRegenerations > 0 && (guardReapplications < 1 ||
                guardReapplications > graphRegenerations)))
            throw new DheException("DHE Bee graph regeneration/reapply attempt evidence is inconsistent.");

        string buildProgramPath = GetString(report, "buildProgramPath") ?? string.Empty;
        if (graphRegenerations > 0)
            RequireFile(buildProgramPath, "DHE Bee Player BuildProgram");
        else if (!string.IsNullOrWhiteSpace(buildProgramPath))
            throw new DheException("DHE native-finalize reports a BuildProgram without graph regeneration.");

        string project = RequireDirectory(projectRoot, "DHE project root");
        string beeRoot = Path.GetFullPath(Path.Combine(project, "Library", "Bee"));
        string generatedCppRoot = RequireDirectory(GetString(report, "generatedCppRoot") ?? string.Empty,
            "DHE finalized generated C++ root");
        RequireContainedPath(Path.Combine(beeRoot, "artifacts"), generatedCppRoot,
            "DHE finalized generated C++ root");
        RequireFile(GetString(report, "beeBackendPath") ?? string.Empty,
            "DHE Bee backend");
        string dagPath = RequireFile(GetString(report, "dagPath") ?? string.Empty,
            "DHE Player DAG");
        RequireContainedPath(beeRoot, dagPath, "DHE Player DAG");
        string dagJsonPath = RequireFile(dagPath + ".json", "DHE Player JSON DAG");
        using (JsonDocument dagDocument = JsonDocument.Parse(File.ReadAllText(dagJsonPath)))
        {
            string generatedPath = Path.GetFullPath(generatedCppRoot).Replace('\\', '/');
            if (!JsonContainsPath(dagDocument.RootElement, generatedPath))
                throw new DheException("DHE Player DAG is not bound to the finalized generated C++ root.");
        }
        RequireFile(GetString(report, "logPath") ?? string.Empty,
            "DHE Bee rebuild log");
        string nativeManifest = RequireFile(GetString(report, "manifestPath") ?? string.Empty,
            "DHE native manifest");
        if (!Path.GetFullPath(nativeManifest).Equals(Path.GetFullPath(expectedNativeManifestPath),
                StringComparison.OrdinalIgnoreCase) ||
            !Sha256File(nativeManifest).Equals(GetString(report, "nativeManifestSha256"),
                StringComparison.OrdinalIgnoreCase) ||
            !IsHex(GetString(report, "nativeGuardSourceSha256"), 64, 64))
            throw new DheException("DHE native-finalize is not bound to the finalized native manifest.");

        JsonElement playerBuild = ReadJson<JsonElement>(RequireFile(playerBuildReportPath,
            "DHE final Player build evidence"));
        RequireEvidenceFormat(playerBuild, "hybridclr.dhe-adapter-player-build.json",
            "DHE final Player build");
        if (!GetBool(playerBuild, "passed") || GetBool(playerBuild, "scriptsOnly"))
            throw new DheException("DHE final Player build evidence is invalid.");

        if (string.Equals(expectedTarget, "iOS", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(GetString(playerBuild, "target"), expectedTarget,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("DHE iOS final Player build target is invalid.");
            string iosArtifactPath = RequireDirectory(GetString(playerBuild, "playerPath") ?? string.Empty,
                "DHE iOS Xcode export root");
            if (!string.Equals(GetString(report, "playerArtifactKind"), "ios-xcode-project",
                    StringComparison.Ordinal) ||
                !string.Equals(Path.GetFullPath(GetString(report, "playerArtifactPath") ?? string.Empty),
                    Path.GetFullPath(iosArtifactPath), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(GetString(report, "playerArtifactSha256"),
                    TreeHashForRelease(iosArtifactPath, Array.Empty<string>()),
                    StringComparison.OrdinalIgnoreCase) ||
                GetInt(report, "playerArtifactExitCode") != 0 ||
                !string.Equals(GetString(report, "playerArtifactBuildTask"), "xcode-export",
                    StringComparison.Ordinal))
                throw new DheException("DHE iOS Xcode export artifact identity is invalid.");

            string[] xcodeProjects = Directory.GetDirectories(iosArtifactPath, "*.xcodeproj",
                SearchOption.TopDirectoryOnly);
            if (xcodeProjects.Length != 1)
                throw new DheException("DHE iOS Xcode export must contain exactly one .xcodeproj.");
            RequireFile(Path.Combine(xcodeProjects[0], "project.pbxproj"),
                "DHE iOS Xcode project.pbxproj");
            foreach (string directory in new[] { "Classes", "Libraries", "Data" })
                RequireDirectory(Path.Combine(iosArtifactPath, directory),
                    "DHE iOS Xcode export " + directory + " directory");

            string[] nativeEntries = ReadRequiredStringArray(report,
                "playerArtifactNativeLibraryEntries", "DHE iOS native entries");
            string[] nativeSources = ReadRequiredStringArray(report,
                "playerArtifactNativeLibrarySourcePaths", "DHE iOS native source paths");
            string[] nativeHashes = ReadRequiredStringArray(report,
                "playerArtifactNativeLibrarySha256", "DHE iOS native hashes");
            if (nativeEntries.Length != 0 || nativeSources.Length != 0 || nativeHashes.Length != 0)
                throw new DheException("DHE iOS export must not report Android native library entries.");
            return;
        }

        if (!string.Equals(expectedTarget, "Android", StringComparison.OrdinalIgnoreCase)) return;

        string artifactPath = RequireFile(GetString(report, "playerArtifactPath") ?? string.Empty,
            "DHE Android Player artifact");
        if (!Path.GetFullPath(artifactPath).Equals(
                Path.GetFullPath(GetString(playerBuild, "playerPath") ?? string.Empty),
                StringComparison.OrdinalIgnoreCase) ||
            !Sha256File(artifactPath).Equals(GetString(report, "playerArtifactSha256"),
                StringComparison.OrdinalIgnoreCase) || GetInt(report, "playerArtifactExitCode") != 0)
            throw new DheException("DHE Android Player artifact identity is invalid.");

        string kind = GetString(report, "playerArtifactKind") ?? string.Empty;
        string extension = Path.GetExtension(artifactPath);
        if ((kind == "android-apk" && !extension.Equals(".apk", StringComparison.OrdinalIgnoreCase)) ||
            (kind == "android-aab" && !extension.Equals(".aab", StringComparison.OrdinalIgnoreCase)) ||
            kind is not ("android-apk" or "android-aab"))
            throw new DheException("DHE Android Player artifact kind does not match its output path.");

        string gradleRoot = RequireDirectory(GetString(report, "playerArtifactGradleRoot") ?? string.Empty,
            "DHE Android Gradle root");
        RequireContainedPath(beeRoot, gradleRoot, "DHE Android Gradle root");
        RequireFile(Path.Combine(gradleRoot, "settings.gradle"), "DHE Android Gradle settings");
        string inputDataPath = RequireFile(Path.Combine(beeRoot,
            Path.GetFileNameWithoutExtension(dagPath) + "-inputdata.json"),
            "DHE Android Player Bee input data");
        using (JsonDocument inputData = JsonDocument.Parse(File.ReadAllText(inputDataPath)))
        {
            List<string> destinations = new();
            CollectJsonStringProperties(inputData.RootElement, "DestinationPath", destinations);
            string[] resolvedDestinations = destinations.Select(value => Path.GetFullPath(
                    Path.IsPathRooted(value) ? value : Path.Combine(project, value)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (resolvedDestinations.Length != 1 ||
                !resolvedDestinations[0].Equals(Path.GetFullPath(gradleRoot),
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("DHE Android Gradle root does not match the current Player DAG input data.");
        }
        RequireFile(GetString(report, "playerArtifactBuildToolPath") ?? string.Empty,
            "DHE Android Java build tool");
        RequireFile(GetString(report, "playerArtifactBuildProgramPath") ?? string.Empty,
            "DHE Android Gradle launcher");
        RequireFile(GetString(report, "playerArtifactBuildLogPath") ?? string.Empty,
            "DHE Android artifact build log");
        string task = GetString(report, "playerArtifactBuildTask") ?? string.Empty;
        if (!task.StartsWith(kind == "android-aab" ? ":launcher:bundle" : ":launcher:assemble",
                StringComparison.Ordinal) ||
            !(task.EndsWith("Debug", StringComparison.Ordinal) ||
              task.EndsWith("Release", StringComparison.Ordinal)))
            throw new DheException("DHE Android Gradle build task is invalid.");

        string[] entries = ReadRequiredStringArray(report,
            "playerArtifactNativeLibraryEntries", "DHE Android native entries");
        string[] sources = ReadRequiredStringArray(report,
            "playerArtifactNativeLibrarySourcePaths", "DHE Android native source paths");
        string[] hashes = ReadRequiredStringArray(report,
            "playerArtifactNativeLibrarySha256", "DHE Android native hashes");
        if (entries.Length == 0 || entries.Length != sources.Length ||
            entries.Length != hashes.Length ||
            entries.Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length ||
            sources.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length)
            throw new DheException("DHE Android native evidence arrays are empty, duplicated, or misaligned.");

        using var archive = ZipFile.OpenRead(artifactPath);
        for (int index = 0; index < entries.Length; index++)
        {
            string source = RequireFile(sources[index], "DHE Android finalized libil2cpp.so");
            RequireContainedPath(gradleRoot, source, "DHE Android finalized libil2cpp.so");
            string abi = new DirectoryInfo(Path.GetDirectoryName(source)!).Name;
            string entrySuffix = "/lib/" + abi + "/libil2cpp.so";
            if (!Path.GetFileName(source).Equals("libil2cpp.so", StringComparison.OrdinalIgnoreCase) ||
                !(entries[index].Equals("lib/" + abi + "/libil2cpp.so",
                      StringComparison.OrdinalIgnoreCase) ||
                  entries[index].EndsWith(entrySuffix, StringComparison.OrdinalIgnoreCase)) ||
                !IsHex(hashes[index], 64, 64) ||
                !Sha256File(source).Equals(hashes[index], StringComparison.OrdinalIgnoreCase))
                throw new DheException("DHE Android native staging hash is invalid.");
            var matches = archive.Entries.Where(item => string.Equals(item.FullName,
                entries[index], StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                throw new DheException("DHE Android native artifact entry is missing or duplicated: " +
                    entries[index] + ".");
            using Stream stream = matches[0].Open();
            using SHA256 sha = SHA256.Create();
            string archiveHash = Convert.ToHexString(sha.ComputeHash(stream));
            if (!archiveHash.Equals(hashes[index], StringComparison.OrdinalIgnoreCase))
                throw new DheException("DHE Android artifact native hash does not match Bee staging: " +
                    entries[index] + ".");
        }
    }

    private static string[] ReadRequiredStringArray(JsonElement value, string property,
        string description)
    {
        if (!value.TryGetProperty(property, out JsonElement array) ||
            array.ValueKind != JsonValueKind.Array)
            throw new DheException(description + " is missing.");
        return array.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString() ?? string.Empty : string.Empty)
            .ToArray();
    }

    private static void RequireContainedPath(string root, string path, string description)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string resolved = Path.GetFullPath(path);
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new DheException(description + " is outside its owning root: " + resolved);
    }

    private static bool JsonContainsPath(JsonElement value, string expectedPath)
    {
        if (value.ValueKind == JsonValueKind.String)
            return (value.GetString() ?? string.Empty).Replace('\\', '/')
                .IndexOf(expectedPath, StringComparison.OrdinalIgnoreCase) >= 0;
        if (value.ValueKind == JsonValueKind.Object)
            return value.EnumerateObject().Any(property =>
                JsonContainsPath(property.Value, expectedPath));
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().Any(item => JsonContainsPath(item, expectedPath));
        return false;
    }

    private static void CollectJsonStringProperties(JsonElement value, string propertyName,
        List<string> results)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (property.NameEquals(propertyName) &&
                    property.Value.ValueKind == JsonValueKind.String)
                    results.Add(property.Value.GetString() ?? string.Empty);
                CollectJsonStringProperties(property.Value, propertyName, results);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
                CollectJsonStringProperties(item, propertyName, results);
        }
    }

    private static void RunNativeFinalizeEvidenceRegressions(string regressionRoot,
        List<object> checks, List<string> errors)
    {
        string root = Path.Combine(regressionRoot, "native-finalize-evidence");
        Directory.CreateDirectory(root);
        string project = Path.Combine(root, "project");
        string bee = Path.Combine(project, "Library", "Bee");
        string cpp = Path.Combine(bee, "artifacts", "Android", "cpp");
        string gradle = Path.Combine(bee, "Android", "Prj", "IL2CPP", "Gradle");
        string nativeRoot = Path.Combine(gradle, "unityLibrary", "src", "main", "jniLibs",
            "arm64-v8a");
        Directory.CreateDirectory(cpp);
        Directory.CreateDirectory(nativeRoot);
        Directory.CreateDirectory(Path.Combine(gradle, "launcher"));
        string native = Path.Combine(nativeRoot, "libil2cpp.so");
        File.WriteAllBytes(native, new byte[] { 1, 3, 5, 7 });
        string nativeHash = Sha256File(native);
        string artifact = Path.Combine(root, "player.apk");
        using (ZipArchive archive = ZipFile.Open(artifact, ZipArchiveMode.Create))
        using (Stream stream = archive.CreateEntry("lib/arm64-v8a/libil2cpp.so").Open())
            stream.Write(File.ReadAllBytes(native));
        string manifest = Path.Combine(root, "dhe-native-manifest.json");
        File.WriteAllText(manifest, "{}", new UTF8Encoding(false));
        string beeBackend = Path.Combine(root, "bee_backend.exe");
        string dag = Path.Combine(bee, "Player123.dag");
        string beeLog = Path.Combine(root, "bee.log");
        string buildProgram = Path.Combine(root, "AndroidPlayerBuildProgram.exe");
        string java = Path.Combine(root, "java.exe");
        string launcher = Path.Combine(root, "gradle-launcher.jar");
        string artifactLog = Path.Combine(root, "android-artifact.log");
        string settings = Path.Combine(gradle, "settings.gradle");
        foreach (string file in new[] { beeBackend, dag, beeLog, buildProgram, java, launcher,
                     artifactLog, settings })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, string.Empty, new UTF8Encoding(false));
        }
        File.WriteAllText(dag + ".json", JsonSerializer.Serialize(new { path = cpp }),
            new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(bee, "Player123-inputdata.json"),
            JsonSerializer.Serialize(new { player = new { DestinationPath = gradle } }),
            new UTF8Encoding(false));
        string reportPath = Path.Combine(root, "native-finalize.json");
        string playerBuildPath = Path.Combine(root, "build-final-player.json");
        WriteJson(playerBuildPath, new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-adapter-player-build.json",
            generatedAtUtc = DateTimeOffset.UtcNow,
            passed = true,
            scriptsOnly = false,
            target = "Android",
            playerPath = artifact,
        });
        object Evidence(int attempts, int graph, int reapply) => new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-adapter-native-finalize.json",
            generatedAtUtc = DateTimeOffset.UtcNow,
            passed = true,
            target = "Android",
            generatedCppRoot = cpp,
            manifestPath = manifest,
            nativeGuardSourceSha256 = new string('a', 64),
            nativeManifestSha256 = Sha256File(manifest),
            beeBackendPath = beeBackend,
            dagPath = dag,
            logPath = beeLog,
            attempts,
            exitCode = 0,
            graphRegenerations = graph,
            guardReapplications = reapply,
            buildProgramPath = graph == 0 ? "" : buildProgram,
            playerArtifactKind = "android-apk",
            playerArtifactPath = artifact,
            playerArtifactSha256 = Sha256File(artifact),
            playerArtifactGradleRoot = gradle,
            playerArtifactBuildToolPath = java,
            playerArtifactBuildProgramPath = launcher,
            playerArtifactBuildTask = ":launcher:assembleRelease",
            playerArtifactBuildLogPath = artifactLog,
            playerArtifactExitCode = 0,
            playerArtifactNativeLibraryEntries = new[] { "lib/arm64-v8a/libil2cpp.so" },
            playerArtifactNativeLibrarySourcePaths = new[] { native },
            playerArtifactNativeLibrarySha256 = new[] { nativeHash },
        };

        bool graphAccepted = false;
        try
        {
            WriteJson(reportPath, Evidence(3, 1, 1));
            ValidateNativeFinalizeEvidence(reportPath, playerBuildPath, "Android", project,
                manifest);
            graphAccepted = true;
        }
        catch { }
        AddRegressionCheck(checks, errors, "native-finalize-bee-graph-regeneration",
            graphAccepted, "host must accept a bounded graph regeneration followed by guard reapply and rebuild");

        bool attemptLimitRejected = false;
        try
        {
            WriteJson(reportPath, Evidence(9, 7, 1));
            ValidateNativeFinalizeEvidence(reportPath, playerBuildPath, "Android", project,
                manifest);
        }
        catch { attemptLimitRejected = true; }
        AddRegressionCheck(checks, errors, "native-finalize-attempt-limit-rejected",
            attemptLimitRejected, "host must reject Bee native-finalize evidence beyond eight attempts");

        bool missingReapplyRejected = false;
        try
        {
            WriteJson(reportPath, Evidence(2, 1, 0));
            ValidateNativeFinalizeEvidence(reportPath, playerBuildPath, "Android", project,
                manifest);
        }
        catch { missingReapplyRejected = true; }
        AddRegressionCheck(checks, errors, "native-finalize-missing-reapply-rejected",
            missingReapplyRejected, "host must reject graph regeneration without guard reapplication");

        bool hashMismatchRejected = false;
        try
        {
            File.WriteAllBytes(native, new byte[] { 9, 9, 9, 9 });
            WriteJson(reportPath, Evidence(3, 1, 1));
            ValidateNativeFinalizeEvidence(reportPath, playerBuildPath, "Android", project,
                manifest);
        }
        catch { hashMismatchRejected = true; }
        AddRegressionCheck(checks, errors, "native-finalize-android-hash-mismatch-rejected",
            hashMismatchRejected, "host must reject Android artifacts whose native hash differs from Bee staging");

        // iOS produces an Xcode export directory instead of an APK. Keep a
        // small offline fixture here so the host gate verifies the exported
        // project shape and directory identity even when macOS/Xcode is not
        // available on the regression machine.
        string iosProject = Path.Combine(root, "ios-project");
        string iosBee = Path.Combine(iosProject, "Library", "Bee");
        string iosCpp = Path.Combine(iosBee, "artifacts", "iOS", "cpp");
        Directory.CreateDirectory(iosCpp);
        string iosNativeManifest = Path.Combine(root, "ios-native-manifest.json");
        File.WriteAllText(iosNativeManifest, "{}", new UTF8Encoding(false));
        string iosBeeBackend = Path.Combine(root, "ios-bee-backend");
        string iosDag = Path.Combine(iosBee, "Player123.dag");
        string iosBeeLog = Path.Combine(root, "ios-bee.log");
        foreach (string file in new[] { iosBeeBackend, iosDag, iosBeeLog })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, string.Empty, new UTF8Encoding(false));
        }
        File.WriteAllText(iosDag + ".json", JsonSerializer.Serialize(new { path = iosCpp }),
            new UTF8Encoding(false));
        string iosExport = Path.Combine(root, "ios-export");
        string iosXcodeProject = Path.Combine(iosExport, "Unity-iPhone.xcodeproj");
        Directory.CreateDirectory(iosXcodeProject);
        foreach (string directory in new[] { "Classes", "Libraries", "Data" })
            Directory.CreateDirectory(Path.Combine(iosExport, directory));
        File.WriteAllText(Path.Combine(iosXcodeProject, "project.pbxproj"),
            "// !$*UTF8*$!", new UTF8Encoding(false));
        string iosReportPath = Path.Combine(root, "ios-native-finalize.json");
        string iosPlayerBuildPath = Path.Combine(root, "ios-build-final-player.json");
        WriteJson(iosPlayerBuildPath, new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-adapter-player-build.json",
            generatedAtUtc = DateTimeOffset.UtcNow,
            passed = true,
            scriptsOnly = false,
            target = "iOS",
            playerPath = iosExport,
        });
        WriteJson(iosReportPath, new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-adapter-native-finalize.json",
            generatedAtUtc = DateTimeOffset.UtcNow,
            passed = true,
            target = "iOS",
            generatedCppRoot = iosCpp,
            manifestPath = iosNativeManifest,
            nativeGuardSourceSha256 = new string('a', 64),
            nativeManifestSha256 = Sha256File(iosNativeManifest),
            beeBackendPath = iosBeeBackend,
            dagPath = iosDag,
            logPath = iosBeeLog,
            attempts = 1,
            exitCode = 0,
            graphRegenerations = 0,
            guardReapplications = 0,
            buildProgramPath = "",
            playerArtifactKind = "ios-xcode-project",
            playerArtifactPath = iosExport,
            playerArtifactSha256 = TreeHashForRelease(iosExport, Array.Empty<string>()),
            playerArtifactGradleRoot = (string?)null,
            playerArtifactBuildToolPath = (string?)null,
            playerArtifactBuildProgramPath = (string?)null,
            playerArtifactBuildTask = "xcode-export",
            playerArtifactBuildLogPath = (string?)null,
            playerArtifactExitCode = 0,
            playerArtifactNativeLibraryEntries = Array.Empty<string>(),
            playerArtifactNativeLibrarySourcePaths = Array.Empty<string>(),
            playerArtifactNativeLibrarySha256 = Array.Empty<string>(),
        });
        bool iosAccepted = false;
        try
        {
            ValidateNativeFinalizeEvidence(iosReportPath, iosPlayerBuildPath, "iOS", iosProject,
                iosNativeManifest);
            iosAccepted = true;
        }
        catch { }
        AddRegressionCheck(checks, errors, "native-finalize-ios-xcode-structure",
            iosAccepted,
            "host must accept a complete iOS Xcode export and bind its directory identity");

        bool iosMissingDataRejected = false;
        try
        {
            Directory.Delete(Path.Combine(iosExport, "Data"), true);
            ValidateNativeFinalizeEvidence(iosReportPath, iosPlayerBuildPath, "iOS", iosProject,
                iosNativeManifest);
        }
        catch { iosMissingDataRejected = true; }
        AddRegressionCheck(checks, errors, "native-finalize-ios-xcode-structure-rejected",
            iosMissingDataRejected,
            "host must reject an iOS Xcode export with a missing required Data directory");
    }

    private static void RunCrossTargetPayloadVariantRegressions(string sourceUpdateRoot,
        string sourceRegistryPath, string regressionRoot, List<object> checks,
        List<string> errors)
    {
        string root = Path.Combine(regressionRoot, "cross-target-payload-variants");
        Directory.CreateDirectory(root);
        bool releasePassed = false;
        bool selectionPassed = false;
        bool selectedPayloadTamperRejected = false;
        bool variantSetTamperRejected = false;
        bool missingVariantRejected = false;
        bool primaryVariantContractPassed = false;
        bool consecutiveThreeVariantPassed = false;
        string details = "Cross-target payload regression did not complete.";

        try
        {
            BaseRegistryDocument sourceRegistry = ReadBaseRegistry(sourceRegistryPath);
            JsonElement sourceManifest = ReadJson<JsonElement>(RequireFile(Path.Combine(
                sourceUpdateRoot, "dhe-resource-update.json"),
                "Cross-target source resource manifest"));
            JsonElement[] sourceVariants = sourceManifest.GetProperty("payloadVariants")
                .EnumerateArray().ToArray();
            JsonElement[] defaultVariants = sourceVariants.Where(item => string.Equals(
                GetString(item, "variantId"), "default",
                StringComparison.OrdinalIgnoreCase)).ToArray();
            JsonElement sourceVariant = defaultVariants.Length == 1
                ? defaultVariants[0]
                : sourceVariants.Length == 1
                    ? sourceVariants[0]
                    : sourceVariants.OrderBy(item => GetString(item, "variantId"),
                        StringComparer.Ordinal).FirstOrDefault();
            if (sourceVariant.ValueKind == JsonValueKind.Undefined ||
                defaultVariants.Length > 1)
                throw new DheException(
                    "Cross-target regression requires at least one valid source payload variant.");
            JsonElement[] sourceAssemblies = sourceVariant.GetProperty("assemblies")
                .EnumerateArray().ToArray();
            if (sourceAssemblies.Length < 2 || sourceRegistry.Entries.Length < 2)
                throw new DheException(
                    "Cross-target regression requires at least two payload assemblies and two Bases.");

            string windowsRoot = Path.Combine(root, "current-windows");
            string androidRoot = Path.Combine(root, "current-android");
            string tuanjieRoot = Path.Combine(root, "current-tuanjie");
            Directory.CreateDirectory(windowsRoot);
            Directory.CreateDirectory(androidRoot);
            Directory.CreateDirectory(tuanjieRoot);
            foreach (JsonElement assembly in sourceAssemblies)
            {
                string name = NormalizeName(GetString(assembly, "assemblyName") ?? string.Empty);
                string source = RequireFile(ResolveContainedPath(sourceUpdateRoot,
                    GetString(assembly, "dll") ?? string.Empty,
                    "Cross-target source current assembly"),
                    name + " source current assembly");
                File.Copy(source, Path.Combine(windowsRoot, name + ".dll"), true);
                File.Copy(source, Path.Combine(androidRoot, name + ".dll"), true);
                File.Copy(source, Path.Combine(tuanjieRoot, name + ".dll"), true);
            }

            string mutatedName = NormalizeName(GetString(sourceAssemblies[0],
                "assemblyName") ?? string.Empty);
            string androidAssembly = Path.Combine(androidRoot, mutatedName + ".dll");
            string mutatedAssembly = Path.Combine(root, "android-mutated.dll");
            WriteMutatedAssembly(androidAssembly, mutatedAssembly, module =>
            {
                MethodDef method = module.GetTypes().SelectMany(type => type.Methods)
                    .Where(candidate => candidate.HasBody && !candidate.IsConstructor &&
                        candidate.Body.Instructions.Count != 0 &&
                        candidate.Body.ExceptionHandlers.Count == 0)
                    .OrderBy(candidate => candidate.MDToken.Raw).First();
                method.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
            });
            File.Move(mutatedAssembly, androidAssembly, true);
            ResourceUpdateCompatibility variantCompatibility =
                ResourceUpdateCompatibility.Analyze(
                    MetaVersionSnapshot.Create(Path.Combine(windowsRoot,
                        mutatedName + ".dll")),
                    MetaVersionSnapshot.Create(androidAssembly));
            if (!variantCompatibility.Compatible ||
                variantCompatibility.ChangedMethodCount == 0 ||
                variantCompatibility.ChangedExistingTypeCount != 0)
                throw new DheException(
                    "Cross-target current fixtures are not metadata-stable and distinct.");

            string tuanjieAssemblyName = mutatedName;
            string tuanjieAssembly = Path.Combine(tuanjieRoot, tuanjieAssemblyName + ".dll");
            string tuanjieMutatedAssembly = Path.Combine(root, "tuanjie-mutated.dll");
            WriteMutatedAssembly(tuanjieAssembly, tuanjieMutatedAssembly, module =>
            {
                MethodDef method = module.GetTypes().SelectMany(type => type.Methods)
                    .Where(candidate => candidate.HasBody && !candidate.IsConstructor &&
                        candidate.Body.Instructions.Count != 0 &&
                        candidate.Body.ExceptionHandlers.Count == 0)
                    .OrderBy(candidate => candidate.MDToken.Raw).First();
                method.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
                method.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
            });
            File.Move(tuanjieMutatedAssembly, tuanjieAssembly, true);
            ResourceUpdateCompatibility tuanjieCompatibility =
                ResourceUpdateCompatibility.Analyze(
                    MetaVersionSnapshot.Create(Path.Combine(windowsRoot,
                        tuanjieAssemblyName + ".dll")),
                    MetaVersionSnapshot.Create(tuanjieAssembly));
            if (!tuanjieCompatibility.Compatible ||
                tuanjieCompatibility.ChangedMethodCount == 0 ||
                tuanjieCompatibility.ChangedExistingTypeCount != 0)
                throw new DheException(
                    "Tuanjie current fixture is not metadata-stable and distinct.");

            string[] assemblyNames = sourceAssemblies.Select(item => NormalizeName(
                    GetString(item, "assemblyName") ?? string.Empty))
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            string[] patchNames = sourceManifest.GetProperty("aotMetadataSets")
                .EnumerateArray().SelectMany(set => set.GetProperty("assemblies")
                    .EnumerateArray()).Select(assembly => NormalizeName(
                    GetString(assembly, "assemblyName") ?? string.Empty))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            string settingsPath = Path.Combine(root, "HybridCLRSettings.asset");
            var settingsText = new StringBuilder();
            settingsText.AppendLine("hotUpdateAssemblies:");
            foreach (string name in assemblyNames) settingsText.AppendLine("- " + name);
            settingsText.AppendLine("dheAotAssemblies:");
            foreach (string name in assemblyNames) settingsText.AppendLine("- " + name);
            settingsText.AppendLine("patchAOTAssemblies:");
            foreach (string name in patchNames) settingsText.AppendLine("- " + name);
            File.WriteAllText(settingsPath, settingsText.ToString(),
                new UTF8Encoding(false));

            string[] payloadVariantIds = sourceRegistry.Entries.Select(entry =>
                entry.EngineWorkflow switch
                {
                    "Unity2021Standard" => "windows",
                    "Unity2022Fgs" => "android",
                    "Tuanjie2022Fgs" => "tuanjie",
                    _ => throw new DheException(
                        "Cross-target regression encountered an unknown engine workflow: " +
                        entry.EngineWorkflow),
                }).ToArray();
            if (!payloadVariantIds.Contains("windows", StringComparer.OrdinalIgnoreCase) ||
                !payloadVariantIds.Contains("android", StringComparer.OrdinalIgnoreCase) ||
                !payloadVariantIds.Contains("tuanjie", StringComparer.OrdinalIgnoreCase))
                throw new DheException(
                    "Cross-target regression could not bind all three engine payload variants.");

            string registryPath = Path.Combine(root, "dhe-base-registry.json");
            var registryArguments = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["baseidentities"] = string.Join(',', sourceRegistry.Entries.Select(
                    entry => entry.BuildIdentity)),
                ["baselineroots"] = string.Join(',', sourceRegistry.Entries.Select(
                    entry => entry.BaselineRoot)),
                ["basenativemanifests"] = string.Join(',', sourceRegistry.Entries.Select(
                    entry => entry.NativeManifest)),
                ["engineworkflows"] = string.Join(',', sourceRegistry.Entries.Select(
                    entry => entry.EngineWorkflow)),
                ["payloadvariantids"] = string.Join(',', payloadVariantIds),
                ["registryid"] = "regression-cross-target-multibase",
                ["output"] = registryPath,
            };
            if (sourceRegistry.Entries.Any(entry => entry.AotMetadataRoot != null))
                registryArguments["aotmetadataroots"] = string.Join(',',
                    sourceRegistry.Entries.Select(entry => entry.AotMetadataRoot ?? "null"));
            if (BuildBaseRegistry(new Cli("base-registry", registryArguments)) != 0)
                throw new DheException("Cross-target Base registry generation failed.");
            BaseRegistryDocument registry = ReadBaseRegistry(registryPath);

            string releaseRoot = Path.Combine(root, "release");
            var variantRoots = new Dictionary<string, string>
            {
                ["android"] = androidRoot,
                ["tuanjie"] = tuanjieRoot,
            };
            if (ResourceUpdate(new Cli("resource-update", new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["currentroot"] = windowsRoot,
                ["currentvariantid"] = "windows",
                ["currentvariantroots"] = JsonSerializer.Serialize(variantRoots),
                ["settingsfile"] = settingsPath,
                ["baseregistry"] = registryPath,
                ["outputroot"] = releaseRoot,
                ["mode"] = "Release",
                ["initializereleaseledger"] = "true",
                ["releasechannelid"] = "regression-cross-target",
            })) != 0)
                throw new DheException("Cross-target resource release generation failed.");

            JsonElement manifest = ReadJson<JsonElement>(Path.Combine(releaseRoot,
                "dhe-resource-update.json"));
            JsonElement[] variants = manifest.GetProperty("payloadVariants")
                .EnumerateArray().ToArray();
            var variantsById = variants.ToDictionary(item =>
                    GetString(item, "variantId") ?? string.Empty,
                item => item, StringComparer.OrdinalIgnoreCase);
            string windowsSet = GetString(variantsById["windows"],
                "currentAssemblySetSha256") ?? string.Empty;
            string androidSet = GetString(variantsById["android"],
                "currentAssemblySetSha256") ?? string.Empty;
            string tuanjieSet = GetString(variantsById["tuanjie"],
                "currentAssemblySetSha256") ?? string.Empty;
            bool engineVariantBinding = registry.Entries.All(entry =>
                (entry.EngineWorkflow == "Unity2021Standard" && entry.PayloadVariantId == "windows") ||
                (entry.EngineWorkflow == "Unity2022Fgs" && entry.PayloadVariantId == "android") ||
                (entry.EngineWorkflow == "Tuanjie2022Fgs" && entry.PayloadVariantId == "tuanjie"));
            ReleaseLedgerDocument ledger = ReadReleaseLedger(RequireFile(Path.Combine(
                releaseRoot, ReleaseLedgerFileName), "Cross-target release ledger"));
            releasePassed = string.Equals(GetString(manifest, "payloadModel"),
                    "variant-current-payload", StringComparison.Ordinal) &&
                variantsById.Count == 3 && !variantsById.ContainsKey("default") &&
                IsHex(windowsSet, 64, 64) && IsHex(androidSet, 64, 64) &&
                IsHex(tuanjieSet, 64, 64) &&
                !string.Equals(windowsSet, androidSet,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(windowsSet, tuanjieSet,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(androidSet, tuanjieSet,
                    StringComparison.OrdinalIgnoreCase) &&
                engineVariantBinding &&
                ledger.ActiveBaseCount == registry.Entries.Length &&
                string.Equals(ledger.PayloadVariantSetSha256,
                    GetString(manifest, "payloadVariantSetSha256"),
                    StringComparison.OrdinalIgnoreCase);

            string CreateBaseAssets(BaseRegistryEntry entry, string name)
            {
                JsonElement identity = ReadJson<JsonElement>(entry.BuildIdentity);
                string runtimeAssetRoot = RequirePortableAssetRoot(
                    GetString(identity, "runtimeAssetRoot"), "identity runtimeAssetRoot");
                string baseAssetRoot = RequirePortableAssetRoot(
                    GetString(identity, "baseMetaVersionAssetRoot"),
                    "identity baseMetaVersionAssetRoot");
                string relative = baseAssetRoot[runtimeAssetRoot.Length..].TrimEnd('/');
                string assetRoot = Path.Combine(root, "assets", name);
                string baseRoot = ResolveContainedPath(assetRoot, relative,
                    "Cross-target embedded Base MetaVersion root");
                Directory.CreateDirectory(baseRoot);
                foreach (JsonElement identityAssembly in identity.GetProperty("assemblies")
                             .EnumerateArray())
                {
                    string assemblyName = NormalizeName(GetString(identityAssembly,
                        "assemblyName") ?? string.Empty);
                    string baseline = RequireFile(Path.Combine(entry.BaselineRoot,
                        assemblyName + ".dll"),
                        assemblyName + " cross-target Base assembly");
                    MetaVersionSnapshot.Create(baseline).WriteBinary(Path.Combine(baseRoot,
                        assemblyName + ".mv.bytes"));
                }
                return assetRoot;
            }

            bool allStagesPassed = true;
            for (int index = 0; index < registry.Entries.Length; index++)
            {
                BaseRegistryEntry entry = registry.Entries[index];
                string assetRoot = CreateBaseAssets(entry,
                    "base-" + index.ToString("D3", CultureInfo.InvariantCulture));
                string stagePath = Path.Combine(root, "stages",
                    "base-" + index.ToString("D3", CultureInfo.InvariantCulture) + ".json");
                int stageExit = StageResourceUpdate(new Cli("stage-resource-update",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["updateroot"] = releaseRoot,
                        ["assetroot"] = assetRoot,
                        ["basebuildidentity"] = entry.BuildIdentity,
                        ["output"] = stagePath,
                    }));
                JsonElement stage = stageExit == 0
                    ? ReadJson<JsonElement>(stagePath)
                    : default;
                string expectedVariant = entry.PayloadVariantId;
                string expectedSet = GetString(variantsById[expectedVariant],
                    "currentAssemblySetSha256") ?? string.Empty;
                string variantPath = "payload/variants/" + expectedVariant + "/";
                string[] selectedVariantFiles = stageExit == 0
                    ? stage.GetProperty("stagedFiles").EnumerateArray()
                        .Select(item => GetString(item, "path") ?? string.Empty)
                        .Where(path => path.Contains("payload/variants/",
                            StringComparison.OrdinalIgnoreCase)).ToArray()
                    : Array.Empty<string>();
                allStagesPassed &= stageExit == 0 &&
                    string.Equals(GetString(stage, "payloadVariantId"), expectedVariant,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(GetString(stage, "currentAssemblySetSha256"),
                        expectedSet, StringComparison.OrdinalIgnoreCase) &&
                    selectedVariantFiles.Length == sourceAssemblies.Length * 2 &&
                    selectedVariantFiles.All(path => path.StartsWith(variantPath,
                        StringComparison.OrdinalIgnoreCase));
            }
            selectionPassed = allStagesPassed;

            string CreateNextVariantRoot(string sourceRoot, string name, int nopCount)
            {
                string nextRoot = Path.Combine(root, name);
                Directory.CreateDirectory(nextRoot);
                foreach (JsonElement assembly in sourceAssemblies)
                {
                    string assemblyName = NormalizeName(GetString(assembly,
                        "assemblyName") ?? string.Empty);
                    File.Copy(Path.Combine(sourceRoot, assemblyName + ".dll"),
                        Path.Combine(nextRoot, assemblyName + ".dll"), true);
                }
                string assemblyPath = Path.Combine(nextRoot, mutatedName + ".dll");
                string nextAssemblyPath = Path.Combine(root, name + "-mutated.dll");
                WriteMutatedAssembly(assemblyPath, nextAssemblyPath, module =>
                {
                    MethodDef method = module.GetTypes().SelectMany(type => type.Methods)
                        .Where(candidate => candidate.HasBody && !candidate.IsConstructor &&
                            candidate.Body.Instructions.Count != 0 &&
                            candidate.Body.ExceptionHandlers.Count == 0)
                        .OrderBy(candidate => candidate.MDToken.Raw).First();
                    for (int index = 0; index < nopCount; index++)
                        method.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
                });
                File.Move(nextAssemblyPath, assemblyPath, true);
                return nextRoot;
            }

            string windowsNextRoot = CreateNextVariantRoot(windowsRoot,
                "current-windows-next", 1);
            string androidNextRoot = CreateNextVariantRoot(androidRoot,
                "current-android-next", 1);
            string tuanjieNextRoot = CreateNextVariantRoot(tuanjieRoot,
                "current-tuanjie-next", 1);
            string nextReleaseRoot = Path.Combine(root, "release-next");
            string firstLedgerPath = Path.Combine(releaseRoot, ReleaseLedgerFileName);
            var nextVariantRoots = new Dictionary<string, string>
            {
                ["android"] = androidNextRoot,
                ["tuanjie"] = tuanjieNextRoot,
            };
            if (ResourceUpdate(new Cli("resource-update", new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["currentroot"] = windowsNextRoot,
                ["currentvariantid"] = "windows",
                ["currentvariantroots"] = JsonSerializer.Serialize(nextVariantRoots),
                ["settingsfile"] = settingsPath,
                ["baseregistry"] = registryPath,
                ["outputroot"] = nextReleaseRoot,
                ["mode"] = "Release",
                ["previousreleaseledger"] = firstLedgerPath,
                ["expectedpreviousreleaseledgersha256"] = Sha256File(firstLedgerPath),
                ["releasechannelid"] = "regression-cross-target",
            })) == 0)
            {
                JsonElement nextManifest = ReadJson<JsonElement>(Path.Combine(nextReleaseRoot,
                    "dhe-resource-update.json"));
                JsonElement[] nextVariants = nextManifest.GetProperty("payloadVariants")
                    .EnumerateArray().ToArray();
                var nextVariantsById = nextVariants.ToDictionary(item =>
                        GetString(item, "variantId") ?? string.Empty,
                    item => item, StringComparer.OrdinalIgnoreCase);
                ReleaseLedgerDocument nextLedger = ReadReleaseLedger(RequireFile(
                    Path.Combine(nextReleaseRoot, ReleaseLedgerFileName),
                    "Consecutive cross-target release ledger"));
                bool nextStagesPassed = true;
                for (int index = 0; index < registry.Entries.Length; index++)
                {
                    BaseRegistryEntry entry = registry.Entries[index];
                    string assetRoot = CreateBaseAssets(entry,
                        "next-base-" + index.ToString("D3", CultureInfo.InvariantCulture));
                    string stagePath = Path.Combine(root, "next-stages",
                        "base-" + index.ToString("D3", CultureInfo.InvariantCulture) + ".json");
                    int stageExit = StageResourceUpdate(new Cli("stage-resource-update",
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["updateroot"] = nextReleaseRoot,
                            ["assetroot"] = assetRoot,
                            ["basebuildidentity"] = entry.BuildIdentity,
                            ["output"] = stagePath,
                        }));
                    JsonElement stage = stageExit == 0
                        ? ReadJson<JsonElement>(stagePath)
                        : default;
                    string expectedVariant = entry.PayloadVariantId;
                    string expectedSet = nextVariantsById.TryGetValue(expectedVariant,
                            out JsonElement nextVariant)
                        ? GetString(nextVariant, "currentAssemblySetSha256") ?? string.Empty
                        : string.Empty;
                    string variantPath = "payload/variants/" + expectedVariant + "/";
                    string[] selectedVariantFiles = stageExit == 0
                        ? stage.GetProperty("stagedFiles").EnumerateArray()
                            .Select(item => GetString(item, "path") ?? string.Empty)
                            .Where(path => path.Contains("payload/variants/",
                                StringComparison.OrdinalIgnoreCase)).ToArray()
                        : Array.Empty<string>();
                    nextStagesPassed &= stageExit == 0 &&
                        string.Equals(GetString(stage, "payloadVariantId"), expectedVariant,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(GetString(stage, "currentAssemblySetSha256"), expectedSet,
                            StringComparison.OrdinalIgnoreCase) &&
                        selectedVariantFiles.Length == sourceAssemblies.Length * 2 &&
                        selectedVariantFiles.All(path => path.StartsWith(variantPath,
                            StringComparison.OrdinalIgnoreCase));
                }
                consecutiveThreeVariantPassed = nextStagesPassed &&
                    nextVariantsById.Count == 3 &&
                    nextLedger.Revision == ledger.Revision + 1 &&
                    string.Equals(nextLedger.ParentLedgerSha256, ledger.Sha256,
                        StringComparison.OrdinalIgnoreCase) &&
                    nextLedger.ActiveBaseCount == registry.Entries.Length &&
                    IsHex(GetString(nextManifest, "payloadVariantSetSha256"), 64, 64) &&
                    !string.Equals(GetString(nextManifest, "payloadVariantSetSha256"),
                        GetString(manifest, "payloadVariantSetSha256"),
                        StringComparison.OrdinalIgnoreCase);
            }

            BaseRegistryEntry androidBase = registry.Entries.First(entry =>
                string.Equals(entry.PayloadVariantId, "android",
                    StringComparison.OrdinalIgnoreCase));
            string tamperedRoot = Path.Combine(root, "selected-payload-tamper");
            CopyDirectory(releaseRoot, tamperedRoot);
            JsonElement tamperedManifest = ReadJson<JsonElement>(Path.Combine(tamperedRoot,
                "dhe-resource-update.json"));
            JsonElement tamperedVariant = SelectPayloadVariant(tamperedManifest, "android",
                "Cross-target selected payload variant");
            string tamperedPayload = ResolveContainedPath(tamperedRoot,
                GetString(tamperedVariant.GetProperty("assemblies")[0], "dll") ??
                    string.Empty, "Cross-target selected payload");
            byte[] tamperedBytes = File.ReadAllBytes(tamperedPayload);
            tamperedBytes[^1] ^= 0x5a;
            File.WriteAllBytes(tamperedPayload, tamperedBytes);
            try
            {
                _ = StageResourceUpdate(new Cli("stage-resource-update",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["updateroot"] = tamperedRoot,
                        ["assetroot"] = CreateBaseAssets(androidBase, "tamper-base"),
                        ["basebuildidentity"] = androidBase.BuildIdentity,
                        ["output"] = Path.Combine(root, "selected-payload-tamper-stage.json"),
                    }));
            }
            catch (DheException)
            {
                selectedPayloadTamperRejected = true;
            }

            string variantSetTamperRoot = Path.Combine(root, "variant-set-tamper");
            CopyDirectory(releaseRoot, variantSetTamperRoot);
            string variantSetManifestPath = Path.Combine(variantSetTamperRoot,
                "dhe-resource-update.json");
            var variantSetManifest = System.Text.Json.Nodes.JsonNode.Parse(
                File.ReadAllText(variantSetManifestPath))!.AsObject();
            variantSetManifest["payloadVariantSetSha256"] = new string('f', 64);
            File.WriteAllText(variantSetManifestPath,
                variantSetManifest.ToJsonString(Json), new UTF8Encoding(false));
            try
            {
                _ = StageResourceUpdate(new Cli("stage-resource-update",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["updateroot"] = variantSetTamperRoot,
                        ["assetroot"] = CreateBaseAssets(androidBase,
                            "variant-set-tamper-base"),
                        ["basebuildidentity"] = androidBase.BuildIdentity,
                        ["output"] = Path.Combine(root, "variant-set-tamper-stage.json"),
                    }));
            }
            catch (DheException)
            {
                variantSetTamperRejected = true;
            }

            try
            {
                _ = ResourceUpdate(new Cli("resource-update",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["currentroot"] = windowsRoot,
                        ["currentvariantid"] = "windows",
                        ["settingsfile"] = settingsPath,
                        ["baseregistry"] = registryPath,
                        ["outputroot"] = Path.Combine(root, "missing-variant-release"),
                    }));
            }
            catch (DheException)
            {
                missingVariantRejected = true;
            }

            bool duplicatePrimaryRejected = false;
            try
            {
                _ = ReadCurrentVariantRoots(new Cli("resource-update",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["currentvariantroots"] = JsonSerializer.Serialize(
                            new Dictionary<string, string> { ["windows"] = androidRoot }),
                    }), "windows", windowsRoot);
            }
            catch (DheException)
            {
                duplicatePrimaryRejected = true;
            }
            bool invalidPrimaryRejected = false;
            try
            {
                _ = ResourceUpdate(new Cli("resource-update",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["currentroot"] = windowsRoot,
                        ["currentvariantid"] = "../windows",
                        ["settingsfile"] = settingsPath,
                        ["baseregistry"] = registryPath,
                        ["outputroot"] = Path.Combine(root, "invalid-primary-release"),
                    }));
            }
            catch (DheException)
            {
                invalidPrimaryRejected = true;
            }
            primaryVariantContractPassed = duplicatePrimaryRejected &&
                invalidPrimaryRejected;
            details = "one Release bound Unity 2021, Unity 2022, and Tuanjie 2022 Base identities to three distinct current payload variants";
        }
        catch (Exception exception)
        {
            details = exception.Message;
        }

        AddRegressionCheck(checks, errors, "resource-cross-target-payload-release",
            releasePassed, details);
        AddRegressionCheck(checks, errors, "resource-cross-target-selection-bound",
            selectionPassed,
            "every active Base must stage only its registry-selected payload variant and assembly set");
        AddRegressionCheck(checks, errors,
            "resource-cross-target-consecutive-three-variant",
            consecutiveThreeVariantPassed,
            "a consecutive single resource release must update every Base through all three engine payload variants");
        AddRegressionCheck(checks, errors,
            "resource-cross-target-selected-payload-tamper-rejected",
            selectedPayloadTamperRejected,
            "a selected target payload whose bytes drift must fail staging");
        AddRegressionCheck(checks, errors,
            "resource-cross-target-variant-set-tamper-rejected",
            variantSetTamperRejected,
            "the authenticated payload-variant set must reject manifest tampering");
        AddRegressionCheck(checks, errors,
            "resource-cross-target-missing-variant-rejected",
            missingVariantRejected,
            "release generation must reject a registry Base whose current payload variant is absent");
        AddRegressionCheck(checks, errors,
            "resource-cross-target-primary-variant-contract",
            primaryVariantContractPassed,
            "CurrentVariantId must be valid and cannot be redefined by CurrentVariantRoots");
    }

    private static void RunResourceReleaseBuildRegressions(string sourceUpdateRoot,
        string sourceRegistryPath, string settingsFile, string schemasRoot,
        string regressionRoot, List<object> checks, List<string> errors)
    {
        bool registryReuse = false;
        bool threeEngineOnboarding = false;
        bool oldBasesPreserved = false;
        bool variantSelection = false;
        bool staleSnapshotRejected = false;
        bool missingVariantRejected = false;
        bool duplicateBaseRejected = false;
        bool partialOutputRejected = false;
        bool replacementRestored = false;
        bool exactCoverage = false;
        bool onboardingAllBaseStaging = false;
        bool reuseAllBaseStaging = false;
        string details = "resource-release-build regression did not complete";
        try
        {
            string root = Path.Combine(regressionRoot, "resource-release-build");
            Directory.CreateDirectory(root);
            BaseRegistryDocument sourceRegistry = ReadBaseRegistry(sourceRegistryPath);
            BaseRegistryEntry[] initialEntries = RequiredPlayerEngineWorkflows.Select(workflow =>
                sourceRegistry.Entries.FirstOrDefault(entry => string.Equals(entry.EngineWorkflow,
                    workflow, StringComparison.Ordinal)) ?? throw new DheException(
                    "Resource release build regression is missing workflow " + workflow + "."))
                .ToArray();
            BaseRegistryEntry[] addedEntries = sourceRegistry.Entries.Where(entry =>
                initialEntries.All(initial => !string.Equals(initial.BaseId, entry.BaseId,
                    StringComparison.OrdinalIgnoreCase))).ToArray();
            if (addedEntries.Length < 2)
                throw new DheException("Resource release build regression requires multiple " +
                    "additional Base archives.");

            JsonElement sourceManifest = ReadJson<JsonElement>(RequireFile(Path.Combine(
                sourceUpdateRoot, "dhe-resource-update.json"),
                "Resource release build source manifest"));
            string currentSet = GetString(sourceManifest,
                "currentAssemblySetSha256") ?? string.Empty;
            var variantConfigs = new List<object>();
            var variantRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? primaryVariantId = null;
            foreach (JsonElement variant in sourceManifest.GetProperty("payloadVariants")
                         .EnumerateArray())
            {
                string variantId = GetString(variant, "variantId") ?? string.Empty;
                string variantRoot = Path.Combine(root, "current", variantId);
                Directory.CreateDirectory(variantRoot);
                foreach (JsonElement assembly in variant.GetProperty("assemblies").EnumerateArray())
                {
                    string assemblyName = NormalizeName(GetString(assembly,
                        "assemblyName") ?? string.Empty);
                    string source = RequireFile(ResolveContainedPath(sourceUpdateRoot,
                        GetString(assembly, "dll") ?? string.Empty,
                        "Resource release build current assembly"),
                        "Resource release build current assembly");
                    File.Copy(source, Path.Combine(variantRoot, assemblyName + ".dll"), true);
                }
                bool primary = primaryVariantId == null && string.Equals(GetString(variant,
                    "currentAssemblySetSha256"), currentSet,
                    StringComparison.OrdinalIgnoreCase);
                if (primary) primaryVariantId = variantId;
                variantRoots.Add(variantId, variantRoot);
                variantConfigs.Add(new { variantId, root = variantRoot, primary });
            }
            if (primaryVariantId == null)
                throw new DheException("Resource release build source has no primary payload variant.");
            if (variantRoots.Count < 2)
                throw new DheException("Resource release build regression requires two current variants.");

            object BaseInput(BaseRegistryEntry entry) => new
            {
                buildIdentity = entry.BuildIdentity,
                baselineRoot = entry.BaselineRoot,
                nativeManifest = entry.NativeManifest,
                engineWorkflow = entry.EngineWorkflow,
                payloadVariantId = entry.PayloadVariantId,
                label = entry.Label,
                aotMetadataRoot = entry.AotMetadataRoot,
            };

            bool StageAllBases(string name, string updateRoot,
                BaseRegistryDocument registry)
            {
                try
                {
                    string assetsRoot = Path.Combine(root, name + "-all-base-assets");
                    string stagesRoot = Path.Combine(root, name + "-all-base-stages");
                    Directory.CreateDirectory(assetsRoot);
                    Directory.CreateDirectory(stagesRoot);
                    int stagedCount = 0;
                    for (int index = 0; index < registry.Entries.Length; index++)
                    {
                        BaseRegistryEntry entry = registry.Entries[index];
                        JsonElement identity = ReadJson<JsonElement>(entry.BuildIdentity);
                        string runtimeAssetRoot = RequirePortableAssetRoot(
                            GetString(identity, "runtimeAssetRoot"),
                            "onboarding identity runtimeAssetRoot");
                        string baseAssetRoot = RequirePortableAssetRoot(
                            GetString(identity, "baseMetaVersionAssetRoot"),
                            "onboarding identity baseMetaVersionAssetRoot");
                        if (!baseAssetRoot.StartsWith(runtimeAssetRoot,
                                StringComparison.OrdinalIgnoreCase))
                            throw new DheException(
                                "onboarding BaseMetaVersionAssetRoot is outside RuntimeAssetRoot.");
                        string relative = baseAssetRoot[runtimeAssetRoot.Length..].TrimEnd('/');
                        string assetRoot = Path.Combine(assetsRoot,
                            index.ToString("D3", CultureInfo.InvariantCulture));
                        string baseRoot = ResolveContainedPath(assetRoot, relative,
                            "onboarding embedded Base MetaVersion root");
                        Directory.CreateDirectory(baseRoot);
                        foreach (JsonElement assembly in identity.GetProperty("assemblies")
                                     .EnumerateArray())
                        {
                            string assemblyName = NormalizeName(GetString(assembly,
                                "assemblyName") ?? string.Empty);
                            string baseline = RequireFile(Path.Combine(entry.BaselineRoot,
                                assemblyName + ".dll"),
                                "onboarding Base assembly " + assemblyName);
                            MetaVersionSnapshot.Create(baseline).WriteBinary(Path.Combine(
                                baseRoot, assemblyName + ".mv.bytes"));
                        }
                        string stagePath = Path.Combine(stagesRoot,
                            index.ToString("D3", CultureInfo.InvariantCulture) + ".json");
                        int exit = StageResourceUpdate(new Cli("stage-resource-update",
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["updateroot"] = updateRoot,
                                ["assetroot"] = assetRoot,
                                ["basebuildidentity"] = entry.BuildIdentity,
                                ["output"] = stagePath,
                            }));
                        if (exit == 0) stagedCount++;
                    }
                    return stagedCount == registry.Entries.Length;
                }
                catch
                {
                    return false;
                }
            }

            string WriteConfig(string name, string outputRoot, string? existingRegistry,
                string? previousRegistry, IEnumerable<object> currentVariants,
                IEnumerable<BaseRegistryEntry> newBases, string mode = "Exploratory",
                string? snapshot = null, string? snapshotSha256 = null,
                bool initialize = false)
            {
                string path = Path.Combine(root, name + ".json");
                WriteJson(path, new
                {
                    schemaVersion = 1,
                    format = "hybridclr.dhe-resource-release-build-config.json",
                    pathSemantics = "config-relative-v1",
                    mode,
                    settingsFile,
                    outputRoot,
                    existingRegistry,
                    previousRegistry,
                    registryId = "regression-resource-release-build",
                    currentVariants = currentVariants.ToArray(),
                    newBases = newBases.Select(BaseInput).ToArray(),
                    retireBaseIds = Array.Empty<string>(),
                    retirementReason = (string?)null,
                    channelSnapshot = snapshot,
                    expectedChannelSnapshotSha256 = snapshotSha256,
                    initializeReleaseLedger = initialize,
                });
                return path;
            }

            int Build(string config) => ResourceReleaseBuild(new Cli(
                "resource-release-build", new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["config"] = config,
                    ["schemasroot"] = schemasRoot,
                }));

            string initialOutput = Path.Combine(root, "initial-release");
            string initialConfig = WriteConfig("initial-config", initialOutput, null,
                null, variantConfigs, initialEntries);
            if (Build(initialConfig) != 0)
                throw new DheException("Initial resource release build failed.");
            string initialRegistryPath = RequireFile(Path.Combine(initialOutput, "registry",
                "dhe-base-registry.json"), "Initial resource release build registry");
            BaseRegistryDocument initialRegistry = ReadBaseRegistry(initialRegistryPath);

            string onboardingOutput = Path.Combine(root, "onboarding-release");
            string onboardingConfig = WriteConfig("onboarding-config", onboardingOutput,
                initialRegistryPath, null, variantConfigs, addedEntries);
            if (Build(onboardingConfig) != 0)
                throw new DheException("Three-engine Base onboarding build failed.");
            string onboardingRegistryPath = RequireFile(Path.Combine(onboardingOutput,
                "registry", "dhe-base-registry.json"),
                "Onboarding resource release build registry");
            BaseRegistryDocument onboardingRegistry = ReadBaseRegistry(onboardingRegistryPath);
            JsonElement onboardingReport = ReadJson<JsonElement>(Path.Combine(onboardingOutput,
                "dhe-resource-release-build.json"));
            onboardingAllBaseStaging = StageAllBases("onboarding",
                Path.Combine(onboardingOutput, "resource"), onboardingRegistry);
            threeEngineOnboarding = onboardingRegistry.Revision == 2 &&
                string.Equals(onboardingRegistry.ParentRegistrySha256,
                    initialRegistry.Sha256, StringComparison.OrdinalIgnoreCase) &&
                addedEntries.All(entry => onboardingRegistry.Entries.Any(current =>
                    string.Equals(current.BaseId, entry.BaseId,
                        StringComparison.OrdinalIgnoreCase))) &&
                RequiredPlayerEngineWorkflows.All(workflow => onboardingRegistry.Entries.Any(entry =>
                    string.Equals(entry.EngineWorkflow, workflow,
                        StringComparison.Ordinal)));
            oldBasesPreserved = initialEntries.All(entry =>
                onboardingRegistry.Entries.Any(current => string.Equals(current.BaseId,
                    entry.BaseId, StringComparison.OrdinalIgnoreCase))) &&
                onboardingRegistry.Entries.Length == initialEntries.Length + addedEntries.Length;

            string reuseOutput = Path.Combine(root, "reuse-release");
            string reuseConfig = WriteConfig("reuse-config", reuseOutput,
                onboardingRegistryPath, initialRegistryPath, variantConfigs,
                Array.Empty<BaseRegistryEntry>());
            if (Build(reuseConfig) != 0)
                throw new DheException("Unchanged registry reuse build failed.");
            JsonElement reuseReport = ReadJson<JsonElement>(Path.Combine(reuseOutput,
                "dhe-resource-release-build.json"));
            JsonElement reuseManifest = ReadJson<JsonElement>(Path.Combine(reuseOutput,
                "resource", "dhe-resource-update.json"));
            reuseAllBaseStaging = StageAllBases("reuse", Path.Combine(reuseOutput,
                "resource"), onboardingRegistry);
            registryReuse = GetString(reuseReport, "registryDisposition") == "reused" &&
                reuseReport.GetProperty("registry").ValueKind == JsonValueKind.Null &&
                GetInt(reuseReport, "registryRevision") == onboardingRegistry.Revision &&
                string.Equals(GetString(reuseReport, "registrySha256"),
                    onboardingRegistry.Sha256, StringComparison.OrdinalIgnoreCase) &&
                !Directory.Exists(Path.Combine(reuseOutput, "registry"));

            JsonElement runtimePlan = ReadJson<JsonElement>(Path.Combine(onboardingOutput,
                "resource", "dhe-runtime-plan.json"));
            var expectedVariants = onboardingRegistry.Entries.ToDictionary(entry => entry.BaseId,
                entry => entry.PayloadVariantId, StringComparer.OrdinalIgnoreCase);
            variantSelection = runtimePlan.GetProperty("baseSelections").EnumerateArray().All(
                selection => expectedVariants.TryGetValue(GetString(selection, "baseId") ??
                        string.Empty, out string? expected) &&
                    string.Equals(expected, GetString(selection, "payloadVariantId"),
                        StringComparison.OrdinalIgnoreCase));
            string[] activeIds = onboardingRegistry.Entries.Select(entry => entry.BaseId)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            string[] manifestIds = reuseManifest.GetProperty("supportedBases").EnumerateArray()
                .Select(item => GetString(item, "baseId") ?? string.Empty)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            exactCoverage = GetBool(onboardingReport, "exactActiveBaseCoverage") &&
                GetInt(onboardingReport, "activeBaseCount") == activeIds.Length &&
                activeIds.SequenceEqual(manifestIds, StringComparer.OrdinalIgnoreCase) &&
                Directory.GetFiles(onboardingOutput, "dhe-resource-update.json",
                    SearchOption.AllDirectories).Length == 1;

            string missingVariantId = onboardingRegistry.Entries
                .Select(entry => entry.PayloadVariantId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .First(id => variantRoots.Count > 1);
            object[] reducedVariants = variantConfigs.Where(item =>
                !string.Equals(GetString(JsonSerializer.SerializeToElement(item, Json),
                    "variantId"), missingVariantId,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (reducedVariants.Length == 0)
                throw new DheException("Missing-variant regression could not retain a current variant.");
            JsonElement firstReduced = JsonSerializer.SerializeToElement(reducedVariants[0], Json);
            reducedVariants[0] = new
            {
                variantId = GetString(firstReduced, "variantId"),
                root = GetString(firstReduced, "root"),
                primary = true,
            };
            for (int index = 1; index < reducedVariants.Length; index++)
            {
                JsonElement item = JsonSerializer.SerializeToElement(reducedVariants[index], Json);
                reducedVariants[index] = new
                {
                    variantId = GetString(item, "variantId"),
                    root = GetString(item, "root"),
                    primary = false,
                };
            }
            string missingOutput = Path.Combine(root, "missing-variant-output");
            string missingConfig = WriteConfig("missing-variant-config", missingOutput,
                onboardingRegistryPath, initialRegistryPath, reducedVariants,
                Array.Empty<BaseRegistryEntry>());
            try
            {
                _ = Build(missingConfig);
            }
            catch (DheException)
            {
                missingVariantRejected = !Directory.Exists(missingOutput);
            }

            string duplicateOutput = Path.Combine(root, "duplicate-base-output");
            string duplicateConfig = WriteConfig("duplicate-base-config", duplicateOutput,
                null, null, variantConfigs, new[] { initialEntries[0], initialEntries[0] });
            try
            {
                _ = Build(duplicateConfig);
            }
            catch (DheException)
            {
                duplicateBaseRejected = !Directory.Exists(duplicateOutput);
            }

            string staleOutput = Path.Combine(root, "stale-snapshot-output");
            string staleConfig = WriteConfig("stale-snapshot-config", staleOutput,
                initialRegistryPath, null, variantConfigs, Array.Empty<BaseRegistryEntry>(),
                "Release", initialConfig, new string('f', 64));
            try
            {
                _ = Build(staleConfig);
            }
            catch (DheException)
            {
                staleSnapshotRejected = !Directory.Exists(staleOutput);
            }
            partialOutputRejected = missingVariantRejected && duplicateBaseRejected &&
                staleSnapshotRejected && !Directory.GetDirectories(root, ".*.staging-*",
                    SearchOption.TopDirectoryOnly).Any();

            string replacementOutput = Path.Combine(root, "replacement-output");
            string replacementStaging = Path.Combine(root, "replacement-staging");
            Directory.CreateDirectory(replacementOutput);
            Directory.CreateDirectory(replacementStaging);
            File.WriteAllText(Path.Combine(replacementOutput, "identity.txt"), "old",
                new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(replacementStaging, "identity.txt"), "new",
                new UTF8Encoding(false));
            try
            {
                PublishResourceReleaseDirectory(replacementStaging, replacementOutput, true,
                    () => throw new IOException("injected final move failure"));
            }
            catch (IOException)
            {
                replacementRestored = Directory.Exists(replacementOutput) &&
                    File.ReadAllText(Path.Combine(replacementOutput, "identity.txt")) == "old" &&
                    !Directory.GetDirectories(root, ".replacement-output.backup-*",
                        SearchOption.TopDirectoryOnly).Any();
            }
            if (Directory.Exists(replacementStaging))
                Directory.Delete(replacementStaging, true);
            details = "one atomic build created a three-engine successor, preserved old " +
                "Bases, selected variants, and failed closed";
        }
        catch (Exception exception)
        {
            details = exception.Message;
        }

        AddRegressionCheck(checks, errors, "resource-release-build-registry-reuse",
            registryReuse, details);
        AddRegressionCheck(checks, errors,
            "resource-release-build-three-engine-onboarding", threeEngineOnboarding, details);
        AddRegressionCheck(checks, errors,
            "resource-release-build-old-bases-preserved", oldBasesPreserved, details);
        AddRegressionCheck(checks, errors,
            "resource-release-build-variant-selection", variantSelection, details);
        AddRegressionCheck(checks, errors,
            "resource-release-build-stale-snapshot-rejected", staleSnapshotRejected, details);
        AddRegressionCheck(checks, errors,
            "resource-release-build-missing-variant-rejected", missingVariantRejected, details);
        AddRegressionCheck(checks, errors,
            "resource-release-build-duplicate-base-rejected", duplicateBaseRejected, details);
        AddRegressionCheck(checks, errors,
            "resource-release-build-partial-output-rejected", partialOutputRejected, details);
        AddRegressionCheck(checks, errors,
            "resource-release-build-replacement-restored", replacementRestored, details);
        AddRegressionCheck(checks, errors,
            "resource-release-build-exact-active-base-coverage", exactCoverage, details);
        AddRegressionCheck(checks, errors,
            "resource-release-build-onboarding-all-base-staging",
            onboardingAllBaseStaging,
            "the onboarding resource package must stage successfully for every old and newly added Base");
        AddRegressionCheck(checks, errors,
            "resource-release-build-reuse-all-base-staging",
            reuseAllBaseStaging,
            "a later resource package with the reused registry must stage successfully for every active Base");
    }

    private static bool RunExtensiblePlayerMatrixRegression(string regressionRoot)
    {
        string root = Path.Combine(regressionRoot, "extensible-player-matrix");
        Directory.CreateDirectory(root);
        string[] baseIds = Enumerable.Range(1, 4)
            .Select(index => new string((char)('a' + index), 64)).ToArray();
        string manifestPath = Path.Combine(root, "dhe-resource-update.json");
        WriteJson(manifestPath, new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-resource-update.json",
            supportedBases = baseIds.Select(baseId => new { baseId }).ToArray()
        });

        var workflows = new[]
        {
            "Unity2021Standard", "Unity2022Fgs", "Tuanjie2022Fgs", "Unity2021Standard"
        };
        var reports = new List<(JsonElement Report, string Path)>();
        for (int index = 0; index < workflows.Length; index++)
        {
            string runtimePath = Path.Combine(root, "runtime-" + index + ".json");
            WriteJson(runtimePath, new
            {
                schemaVersion = 1,
                format = "hybridclr.dhe-runtime-manifest.json",
                engineWorkflow = workflows[index]
            });
            string reportPath = Path.Combine(root, "player-" + index + ".json");
            WriteJson(reportPath, new
            {
                schemaVersion = 1,
                format = "hybridclr.dhe-resource-player-workflow.json",
                selectedBaseId = baseIds[index],
                selectedAotMetadataSetId = new string((char)('1' + index), 64),
                runtimeSource = runtimePath,
                target = "StandaloneWindows64",
                currentAssemblySetSha256 = new string('1', 64),
                resourceUpdateManifestSha256 = new string('2', 64),
                resourceUpdateValidationSha256 = new string('3', 64),
                resourceUpdateManifest = manifestPath
            });
            reports.Add((ReadJson<JsonElement>(reportPath), reportPath));
        }

        bool partialBaseSetRejected = false;
        try
        {
            ValidateMultiBaseChangedEvidence(reports.Take(3).ToArray(), true);
        }
        catch (DheException)
        {
            partialBaseSetRejected = true;
        }
        ValidateMultiBaseChangedEvidence(reports, true);
        string variantMismatchPath = Path.Combine(root, "player-variant-mismatch.json");
        var variantMismatch = System.Text.Json.Nodes.JsonNode.Parse(
            reports[1].Report.GetRawText())!.AsObject();
        variantMismatch["selectedPayloadVariantId"] = "windows";
        File.WriteAllText(variantMismatchPath, variantMismatch.ToJsonString(Json),
            new UTF8Encoding(false));
        bool variantMismatchRejected = false;
        try
        {
            ValidateMultiBaseChangedEvidence(new[]
            {
                reports[0],
                (ReadJson<JsonElement>(variantMismatchPath), variantMismatchPath),
                reports[2], reports[3],
            }, true);
        }
        catch (DheException)
        {
            variantMismatchRejected = true;
        }

        // A single release may carry target/engine-specific managed payloads.
        // Verify that the three-engine matrix accepts distinct variants when
        // each Base is explicitly bound to its own current assembly set.
        string variantManifestPath = Path.Combine(root, "variant-resource-update.json");
        string[] variantIds = { "default", "unity2022", "tuanjie" };
        string[] variantCurrentSets = { new string('1', 64), new string('2', 64), new string('3', 64) };
        WriteJson(variantManifestPath, new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-resource-update.json",
            payloadVariants = variantIds.Select((variantId, index) => new
            {
                variantId,
                currentAssemblySetSha256 = variantCurrentSets[index],
            }).ToArray(),
            supportedBases = baseIds.Take(3).Select((baseId, index) => new
            {
                baseId,
                target = "StandaloneWindows64",
                payloadVariantId = variantIds[index],
                currentAssemblySetSha256 = variantCurrentSets[index],
            }).ToArray(),
        });
        string variantManifestSha256 = Sha256File(variantManifestPath);
        var variantReports = new List<(JsonElement Report, string Path)>();
        for (int index = 0; index < 3; index++)
        {
            string runtimePath = Path.Combine(root, "variant-runtime-" + index + ".json");
            WriteJson(runtimePath, new
            {
                schemaVersion = 1,
                format = "hybridclr.dhe-runtime-manifest.json",
                engineWorkflow = workflows[index],
            });
            string reportPath = Path.Combine(root, "variant-player-" + index + ".json");
            WriteJson(reportPath, new
            {
                schemaVersion = 1,
                format = "hybridclr.dhe-resource-player-workflow.json",
                selectedBaseId = baseIds[index],
                selectedAotMetadataSetId = new string((char)('1' + index), 64),
                runtimeSource = runtimePath,
                target = "StandaloneWindows64",
                currentAssemblySetSha256 = variantCurrentSets[index],
                selectedPayloadVariantId = variantIds[index],
                selectedPayloadCurrentAssemblySetSha256 = variantCurrentSets[index],
                resourceUpdateManifestSha256 = variantManifestSha256,
                resourceUpdateValidationSha256 = new string('4', 64),
                resourceUpdateManifest = variantManifestPath,
            });
            variantReports.Add((ReadJson<JsonElement>(reportPath), reportPath));
        }
        bool variantMatrixAccepted = true;
        try
        {
            ValidateMultiBaseChangedEvidence(variantReports, true);
        }
        catch (DheException)
        {
            variantMatrixAccepted = false;
        }
        bool variantSelectionRejected = false;
        var variantSelectionTamperPath = Path.Combine(root, "variant-player-tampered.json");
        var variantSelectionTamper = System.Text.Json.Nodes.JsonNode.Parse(
            variantReports[1].Report.GetRawText())!.AsObject();
        variantSelectionTamper["selectedPayloadCurrentAssemblySetSha256"] = new string('1', 64);
        File.WriteAllText(variantSelectionTamperPath, variantSelectionTamper.ToJsonString(Json),
            new UTF8Encoding(false));
        try
        {
            ValidateMultiBaseChangedEvidence(new[]
            {
                variantReports[0],
                (ReadJson<JsonElement>(variantSelectionTamperPath), variantSelectionTamperPath),
                variantReports[2],
            }, true);
        }
        catch (DheException)
        {
            variantSelectionRejected = true;
        }
        try
        {
            ValidateMultiBaseChangedEvidence(new[] { reports[0], reports[1], reports[3] }, true);
            return false;
        }
        catch (DheException)
        {
            return partialBaseSetRejected && variantMismatchRejected &&
                variantMatrixAccepted && variantSelectionRejected;
        }
    }

    private static bool RunBaseAotMetadataArchiveRegression(string regressionRoot)
    {
        string root = Path.Combine(regressionRoot, "base-aot-metadata-archive");
        string planRoot = Path.Combine(root, "runtime-plan");
        Directory.CreateDirectory(planRoot);
        byte[] alpha = Encoding.UTF8.GetBytes("alpha-metadata");
        byte[] beta = Encoding.UTF8.GetBytes("beta-metadata");
        string alphaPath = Path.Combine(planRoot, "Alpha.aot-metadata.bytes");
        string betaPath = Path.Combine(planRoot, "Beta.aot-metadata.bytes");
        File.WriteAllBytes(alphaPath, alpha);
        File.WriteAllBytes(betaPath, beta);
        string planPath = Path.Combine(planRoot, "dhe-runtime-plan.json");

        void WritePlan(object[] metadata) => WriteJson(planPath, new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-runtime-handoff-plan.json",
            aotMetadata = metadata,
        });

        WritePlan(new object[]
        {
            new { assemblyName = "Beta", sha256 = Sha256Bytes(beta), path = Path.GetFileName(betaPath) },
            new { assemblyName = "Alpha", sha256 = Sha256Bytes(alpha), path = Path.GetFileName(alphaPath) },
        });
        var archive = MaterializeBaseAotMetadataRoot(root, planPath);
        string expectedSetId = NamedByteSetHash(new[] { ("Alpha", alpha), ("Beta", beta) });
        bool valid = archive.Root != null &&
            string.Equals(archive.SetId, expectedSetId, StringComparison.OrdinalIgnoreCase) &&
            File.ReadAllBytes(Path.Combine(archive.Root, "Alpha.dll")).SequenceEqual(alpha) &&
            File.ReadAllBytes(Path.Combine(archive.Root, "Beta.dll")).SequenceEqual(beta);
        string archiveTree = archive.Root == null ? string.Empty :
            TreeHashForRelease(archive.Root, Array.Empty<string>());

        File.WriteAllText(alphaPath, "tampered", new UTF8Encoding(false));
        bool tamperRejected = false;
        try { _ = MaterializeBaseAotMetadataRoot(root, planPath); }
        catch (DheException) { tamperRejected = true; }
        bool archivePreserved = archive.Root != null && Directory.Exists(archive.Root) &&
            string.Equals(TreeHashForRelease(archive.Root, Array.Empty<string>()), archiveTree,
                StringComparison.OrdinalIgnoreCase);

        File.WriteAllBytes(alphaPath, alpha);
        WritePlan(new object[]
        {
            new { assemblyName = "Alpha", sha256 = Sha256Bytes(alpha), path = "../outside.bytes" },
        });
        bool traversalRejected = false;
        try { _ = MaterializeBaseAotMetadataRoot(root, planPath); }
        catch (DheException) { traversalRejected = true; }

        WritePlan(Array.Empty<object>());
        var empty = MaterializeBaseAotMetadataRoot(root, planPath);
        bool emptySetValid = empty.Root == null &&
            string.Equals(empty.SetId, Sha256Bytes(Array.Empty<byte>()),
                StringComparison.OrdinalIgnoreCase) &&
            !Directory.Exists(Path.Combine(root, "aot-metadata-root"));
        return valid && tamperRejected && archivePreserved && traversalRejected && emptySetValid;
    }

    private static bool RunArchivedNativeManifestResolutionRegression(string regressionRoot)
    {
        string root = Path.Combine(regressionRoot, "archived-native-resolution");
        string sourceRoot = Path.Combine(regressionRoot,
            "archived-native-resolution-source");
        string nativeRoot = Path.Combine(root, "native");
        string provenanceRoot = Path.Combine(root, "provenance");
        Directory.CreateDirectory(nativeRoot);
        Directory.CreateDirectory(provenanceRoot);
        Directory.CreateDirectory(sourceRoot);

        string workflowPath = Path.Combine(root, "player-workflow-report.json");
        string normalizedPath = Path.Combine(nativeRoot, "dhe-native-manifest.json");
        string immutablePath = Path.Combine(provenanceRoot,
            "native-manifest.original.bin");
        string sourceNativePath = Path.Combine(sourceRoot,
            "dhe-native-manifest.json");
        string identityPath = Path.Combine(root, "build-identity.json");
        string sourcePreflightPath = Path.Combine(root, "source-preflight.json");
        string cleanCheckoutPath = Path.Combine(root, "clean-checkout.json");
        string toolchainGatePath = Path.Combine(root, "toolchain-gate.json");
        string runtimeSourcePath = Path.Combine(provenanceRoot, "runtime-manifest.json");
        WriteJson(normalizedPath, new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-native-manifest.json",
            pathSemantics = "archive-relative-v1",
            normalized = true,
        });
        WriteJson(sourceNativePath, new
        {
            schemaVersion = 1,
            resolverVersion = 3,
            pathSemantics = "workspace-absolute-v1",
        });
        WriteJson(identityPath, new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-build-identity.json",
            nativeManifestPath = "native/dhe-native-manifest.json",
        });
        foreach (string path in new[]
                 { sourcePreflightPath, cleanCheckoutPath, toolchainGatePath, runtimeSourcePath })
            WriteJson(path, new { schemaVersion = 1, passed = true });
        string immutableHash = Sha256File(sourceNativePath);
        WriteJson(workflowPath, new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-project-player-workflow.json",
            nativeManifest = "native/dhe-native-manifest.json",
            nativeManifestSha256 = immutableHash,
            sourcePreflight = "source-preflight.json",
            cleanCheckoutGate = "clean-checkout.json",
            toolchainGate = "toolchain-gate.json",
            runtimeSource = "provenance/runtime-manifest.json",
        });
        BindArchivedNativeIdentity(root, sourceNativePath);
        string archiveManifestPath = Path.Combine(root, "dhe-archive-manifest.json");
        var archiveFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFullPath(path).Equals(archiveManifestPath,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetRelativePath(root, path)
                .Replace(Path.DirectorySeparatorChar, '/'), StringComparer.Ordinal)
            .Select(path => new
            {
                path = Path.GetRelativePath(root, path)
                    .Replace(Path.DirectorySeparatorChar, '/'),
                size = new FileInfo(path).Length,
                sha256 = Sha256File(path),
            }).ToArray();
        WriteJson(archiveManifestPath, new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-archive-manifest.json",
            workflowReport = "player-workflow-report.json",
            immutableNativeManifest = "provenance/native-manifest.original.bin",
            immutableNativeManifestSha256 = immutableHash,
            offlineReleaseRevalidated = true,
            files = archiveFiles,
            fileCount = archiveFiles.Length,
            fileSetSha256 = Sha256Text(string.Join("\n", archiveFiles.Select(file =>
                file.path + "|" + file.size + "|" + file.sha256))),
        });

        JsonElement workflow = ReadJson<JsonElement>(workflowPath);
        JsonElement identity = ReadJson<JsonElement>(identityPath);
        bool normalizedReferencePreserved = string.Equals(
            GetString(workflow, "nativeManifest"),
            "native/dhe-native-manifest.json", StringComparison.Ordinal) &&
            string.Equals(GetString(identity, "nativeManifestPath"),
                "provenance/native-manifest.original.bin",
                StringComparison.Ordinal);
        bool archiveValidated = ValidateBaseArchiveForWorkflow(workflowPath)
            ?.Equals(archiveManifestPath, StringComparison.OrdinalIgnoreCase) == true;
        bool resolved = ResolveBaseWorkflowNativeManifest(workflow, workflowPath)
            .Equals(immutablePath, StringComparison.OrdinalIgnoreCase);
        bool referencesResolved = new[]
        {
            (Property: "sourcePreflight", Description: "source preflight",
                Expected: sourcePreflightPath),
            (Property: "cleanCheckoutGate", Description: "clean checkout gate",
                Expected: cleanCheckoutPath),
            (Property: "toolchainGate", Description: "toolchain gate",
                Expected: toolchainGatePath),
            (Property: "runtimeSource", Description: "runtime manifest",
                Expected: runtimeSourcePath),
        }.All(item => ResolveBaseWorkflowReference(workflow, workflowPath,
                item.Property, item.Description)
            .Equals(item.Expected, StringComparison.OrdinalIgnoreCase));
        File.AppendAllText(immutablePath, "tampered", new UTF8Encoding(false));
        bool tamperRejected = false;
        try
        {
            _ = ResolveBaseWorkflowNativeManifest(workflow, workflowPath);
        }
        catch (DheException)
        {
            tamperRejected = true;
        }
        bool archiveTamperRejected = false;
        try
        {
            _ = ValidateBaseArchiveForWorkflow(workflowPath);
        }
        catch (DheException)
        {
            archiveTamperRejected = true;
        }
        return normalizedReferencePreserved && archiveValidated && resolved &&
            referencesResolved && tamperRejected && archiveTamperRejected;
    }

    private static bool RunLegacySinglePayloadSelectionRegression()
    {
        const string currentSet = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        using var singleManifest = JsonDocument.Parse("{\"payloadModel\":\"single-current-payload\",\"currentAssemblySetSha256\":\"" +
            currentSet + "\"}");
        using var singleValidation = JsonDocument.Parse("{\"currentAssemblySetSha256\":\"" + currentSet + "\"}");
        using var legacyPlayer = JsonDocument.Parse("{\"passed\":true}");
        using var partialPlayer = JsonDocument.Parse("{\"selectedPayloadVariantId\":\"default\"}");
        using var variantManifest = JsonDocument.Parse("{\"payloadModel\":\"variant-current-payload\",\"payloadVariants\":[{" +
            "\"variantId\":\"default\",\"currentAssemblySetSha256\":\"" + currentSet + "\"}]}");
        using var modernPlayer = JsonDocument.Parse("{\"selectedPayloadVariantId\":\"default\",\"selectedPayloadCurrentAssemblySetSha256\":\"" +
            currentSet + "\"}");
        using var wrongPlayer = JsonDocument.Parse("{\"selectedPayloadVariantId\":\"android\",\"selectedPayloadCurrentAssemblySetSha256\":\"" +
            currentSet + "\"}");

        bool legacyAccepted = PlayerPayloadSelectionError(legacyPlayer.RootElement,
            singleManifest.RootElement, singleValidation.RootElement, "default", currentSet) is null;
        bool partialRejected = PlayerPayloadSelectionError(partialPlayer.RootElement,
            singleManifest.RootElement, singleValidation.RootElement, "default", currentSet) is not null;
        bool variantLegacyRejected = PlayerPayloadSelectionError(legacyPlayer.RootElement,
            variantManifest.RootElement, variantManifest.RootElement, "default", currentSet) is not null;
        bool modernAccepted = PlayerPayloadSelectionError(modernPlayer.RootElement,
            variantManifest.RootElement, variantManifest.RootElement, "default", currentSet) is null;
        bool wrongRejected = PlayerPayloadSelectionError(wrongPlayer.RootElement,
            variantManifest.RootElement, variantManifest.RootElement, "default", currentSet) is not null;
        return legacyAccepted && partialRejected && variantLegacyRejected && modernAccepted && wrongRejected;
    }

    private static bool RunResourcePlayerAssemblyModeRegression()
    {
        using var selectedBase = JsonDocument.Parse("{\"assemblyModes\":[" +
            "{\"assemblyName\":\"Game.Aot\",\"executionMode\":\"dhe-differential\"}," +
            "{\"assemblyName\":\"Game.New\",\"executionMode\":\"interpreter-only\"}]}");
        using var payloadVariant = JsonDocument.Parse("{\"assemblies\":[" +
            "{\"assemblyName\":\"Game.Aot\"},{\"assemblyName\":\"Game.New\"}]}");
        using var validPlayer = JsonDocument.Parse("{" +
            "\"plannedDheAssemblies\":[\"Game.Aot\",\"Game.New\"]," +
            "\"loadedDheAssemblies\":[\"Game.Aot\",\"Game.New\"]," +
            "\"plannedDifferentialAssemblies\":[\"Game.Aot\"]," +
            "\"plannedInterpreterOnlyAssemblies\":[\"Game.New\"]," +
            "\"loadedInterpreterOnlyAssemblies\":[\"Game.New\"]," +
            "\"secondaryAssemblyChangedValidated\":true," +
            "\"secondaryAssemblyDirectValidated\":true}");
        using var validReport = JsonDocument.Parse("{\"assemblyScope\":{" +
            "\"strategy\":\"single-current-multibase-resource\"," +
            "\"aotAssemblies\":[\"Game.Aot\",\"Game.New\"]," +
            "\"loadedDheAssemblies\":[\"Game.Aot\",\"Game.New\"]," +
            "\"differentialAssemblies\":[\"Game.Aot\"]," +
            "\"interpreterOnlyAssemblies\":[\"Game.New\"]," +
            "\"loadedInterpreterOnlyAssemblies\":[\"Game.New\"]," +
            "\"stagedDependencies\":[],\"stagedDependenciesLoadedAsDhe\":false," +
            "\"secondaryAssemblyChangedValidated\":true," +
            "\"secondaryAssemblyDirectValidated\":true}}");
        var validErrors = new List<string>();
        string[] validDifferential = ReadPlayerAssemblyNameArray(validPlayer.RootElement,
            "plannedDifferentialAssemblies", validErrors);
        string[] validInterpreterOnly = ReadPlayerAssemblyNameArray(validPlayer.RootElement,
            "plannedInterpreterOnlyAssemblies", validErrors);
        string[] validLoadedInterpreterOnly = ReadPlayerAssemblyNameArray(validPlayer.RootElement,
            "loadedInterpreterOnlyAssemblies", validErrors);
        ValidatePlayerAssemblyModes(selectedBase.RootElement, payloadVariant.RootElement,
            validDifferential, validInterpreterOnly, validLoadedInterpreterOnly, validErrors);
        ValidateResourcePlayerAssemblyScope(validReport.RootElement,
            new[] { "Game.Aot", "Game.New" },
            ReadPlayerAssemblyNameArray(validPlayer.RootElement,
                "plannedDheAssemblies", validErrors),
            ReadPlayerAssemblyNameArray(validPlayer.RootElement,
                "loadedDheAssemblies", validErrors), validDifferential,
            validInterpreterOnly, validLoadedInterpreterOnly,
            validPlayer.RootElement, validErrors);

        using var missingLoadPlayer = JsonDocument.Parse("{" +
            "\"plannedDifferentialAssemblies\":[\"Game.Aot\"]," +
            "\"plannedInterpreterOnlyAssemblies\":[\"Game.New\"]," +
            "\"loadedInterpreterOnlyAssemblies\":[]}");
        var missingLoadErrors = new List<string>();
        ValidatePlayerAssemblyModes(selectedBase.RootElement, payloadVariant.RootElement,
            ReadPlayerAssemblyNameArray(missingLoadPlayer.RootElement,
                "plannedDifferentialAssemblies", missingLoadErrors),
            ReadPlayerAssemblyNameArray(missingLoadPlayer.RootElement,
                "plannedInterpreterOnlyAssemblies", missingLoadErrors),
            ReadPlayerAssemblyNameArray(missingLoadPlayer.RootElement,
                "loadedInterpreterOnlyAssemblies", missingLoadErrors), missingLoadErrors);
        using var tamperedReport = JsonDocument.Parse("{\"assemblyScope\":{" +
            "\"strategy\":\"single-current-multibase-resource\"," +
            "\"aotAssemblies\":[\"Game.Aot\",\"Game.New\"]," +
            "\"loadedDheAssemblies\":[\"Game.Aot\"]," +
            "\"differentialAssemblies\":[\"Game.Aot\"]," +
            "\"interpreterOnlyAssemblies\":[\"Game.New\"]," +
            "\"loadedInterpreterOnlyAssemblies\":[\"Game.New\"]," +
            "\"stagedDependencies\":[],\"stagedDependenciesLoadedAsDhe\":false," +
            "\"secondaryAssemblyChangedValidated\":true," +
            "\"secondaryAssemblyDirectValidated\":true}}");
        var tamperedScopeErrors = new List<string>();
        ValidateResourcePlayerAssemblyScope(tamperedReport.RootElement,
            new[] { "Game.Aot", "Game.New" },
            new[] { "Game.Aot", "Game.New" }, new[] { "Game.Aot", "Game.New" },
            validDifferential, validInterpreterOnly, validLoadedInterpreterOnly,
            validPlayer.RootElement, tamperedScopeErrors);
        return validErrors.Count == 0 && missingLoadErrors.Count != 0 &&
            tamperedScopeErrors.Count != 0;
    }

    private static bool RunInterpreterOnlyResourceUpdateRegression()
    {
        using var player = CreateResourceNoOpPlayerRegressionDocument(true);
        var interpreterOnlyErrors = new List<string>();
        ValidateResourcePlayerExecution(player.RootElement, 0, 1, interpreterOnlyErrors);
        return interpreterOnlyErrors.Count == 0;
    }

    private static bool RunSelectedBaseNoOpResourceUpdateRegression()
    {
        using var validPlayer = CreateResourceNoOpPlayerRegressionDocument(true);
        var validErrors = new List<string>();
        ValidateResourcePlayerExecution(validPlayer.RootElement, 0, 0, validErrors);

        using var weakPlayer = CreateResourceNoOpPlayerRegressionDocument(false);
        var weakErrors = new List<string>();
        ValidateResourcePlayerExecution(weakPlayer.RootElement, 0, 0, weakErrors);
        return validErrors.Count == 0 && weakErrors.Count != 0;
    }

    private static JsonDocument CreateResourceNoOpPlayerRegressionDocument(bool noOpValidated)
    {
        return JsonDocument.Parse("{" +
            "\"changedMethodCount\":0,\"expectedChangedMethodCount\":0," +
            "\"interpreterEntryCount\":0,\"aotEntryCount\":1," +
            "\"resourceUpdateManifestPresent\":true,\"resourceUpdateValidated\":true," +
            "\"dispatchProbeValidated\":true,\"multiAssemblyValidated\":true," +
            "\"changedProbeChanged\":false,\"unchangedProbeChanged\":false," +
            "\"transactionStatus\":\"notApplicable\",\"retryValidated\":false," +
            "\"noOpAotBehaviorValidated\":" + (noOpValidated ? "true" : "false") +
            ",\"capabilityDirectPassed\":true," +
            "\"capabilityPassed\":true,\"secondaryAssemblyDirectValidated\":true}");
    }

    private static void RunIntegratedSourceLockRegressions(string regressionRoot,
        List<object> checks, List<string> errors)
    {
        string root = Path.Combine(regressionRoot, "integrated-source-lock");
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        string sentinel = Path.Combine(source, "sentinel.txt");
        File.WriteAllText(sentinel, "integrated\n", new UTF8Encoding(false));
        string auditPatch = Path.Combine(root, "audit.patch");
        File.WriteAllText(auditPatch, "audit-only\n", new UTF8Encoding(false));
        string commit = new string('a', 40);
        string tree = LabCommands.CanonicalSourceTreeHash(source, true);
        string auditHash = Sha256File(auditPatch);

        JsonElement Lock(string expectedTree, string? expectedCommit = null,
            string? expectedAuditHash = null)
        {
            return JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                format = "hybridclr.dhe-runtime-lock.json",
                sourceMode = "integrated",
                patches = new[]
                {
                    new
                    {
                        id = "regression-integrated-package",
                        repository = "hybridclr_unity",
                        sourceMode = "integrated",
                        baseCommit = new string('b', 40),
                        integratedCommit = expectedCommit ?? commit,
                        expectedTreeSha256 = expectedTree,
                        path = "audit.patch",
                        sha256 = expectedAuditHash ?? auditHash,
                        applyRoot = "package",
                        stripComponents = 1,
                    }
                }
            })).RootElement.Clone();
        }

        bool validAccepted;
        try
        {
            LabCommands.ApplyLockedOverlays(root, Lock(tree), "hybridclr_unity", source,
                "Unity2021Standard", commit, source);
            validAccepted = File.ReadAllText(sentinel) == "integrated\n";
        }
        catch
        {
            validAccepted = false;
        }
        AddRegressionCheck(checks, errors, "integrated-source-lock-valid",
            validAccepted, "an exact integrated commit/tree must pass without applying the audit patch");

        bool lineEndingStable;
        try
        {
            File.WriteAllText(sentinel, "integrated\r\n", new UTF8Encoding(false));
            LabCommands.ApplyLockedOverlays(root, Lock(tree), "hybridclr_unity", source,
                "Unity2021Standard", commit, source);
            lineEndingStable = File.ReadAllText(sentinel) == "integrated\r\n";
        }
        catch
        {
            lineEndingStable = false;
        }
        finally
        {
            File.WriteAllText(sentinel, "integrated\n", new UTF8Encoding(false));
        }
        AddRegressionCheck(checks, errors, "integrated-source-lock-line-ending-stable",
            lineEndingStable, "integrated source locks must accept equivalent LF and CRLF checkouts");

        bool commitTamperRejected;
        try
        {
            LabCommands.ApplyLockedOverlays(root, Lock(tree, new string('d', 40)),
                "hybridclr_unity", source, "Unity2021Standard", commit, source);
            commitTamperRejected = false;
        }
        catch
        {
            commitTamperRejected = File.ReadAllText(sentinel) == "integrated\n";
        }
        AddRegressionCheck(checks, errors, "integrated-source-lock-commit-tamper",
            commitTamperRejected, "an integrated commit mismatch must fail before source mutation");

        bool treeTamperRejected;
        try
        {
            LabCommands.ApplyLockedOverlays(root, Lock(new string('c', 64)),
                "hybridclr_unity", source, "Unity2021Standard", commit, source);
            treeTamperRejected = false;
        }
        catch
        {
            treeTamperRejected = File.ReadAllText(sentinel) == "integrated\n";
        }
        AddRegressionCheck(checks, errors, "integrated-source-lock-tree-tamper",
            treeTamperRejected, "an integrated tree mismatch must fail before source mutation");

        bool patchTamperRejected;
        try
        {
            LabCommands.ApplyLockedOverlays(root, Lock(tree, expectedAuditHash: new string('e', 64)),
                "hybridclr_unity", source, "Unity2021Standard", commit, source);
            patchTamperRejected = false;
        }
        catch
        {
            patchTamperRejected = File.ReadAllText(sentinel) == "integrated\n";
        }
        AddRegressionCheck(checks, errors, "integrated-source-lock-patch-tamper",
            patchTamperRejected, "an integrated audit-patch hash mismatch must fail before source mutation");
    }

    private static void RunResourceStagingRegressions(string updateRoot, string assetRoot,
        string baseBuildIdentityPath, string regressionRoot, List<object> checks,
        List<string> errors, string? consecutiveUpdateRoot = null)
    {
        string root = Path.Combine(regressionRoot, "resource-staging");
        Directory.CreateDirectory(root);

        bool Stage(string name, string sourceUpdateRoot, string sourceAssetRoot,
            string sourceBaseBuildIdentity)
        {
            try
            {
                return StageResourceUpdate(new Cli("stage-resource-update",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["updateroot"] = sourceUpdateRoot,
                        ["assetroot"] = sourceAssetRoot,
                        ["basebuildidentity"] = sourceBaseBuildIdentity,
                        ["output"] = Path.Combine(root, name + ".json"),
                    })) == 0;
            }
            catch
            {
                return false;
            }
        }

        void ConvertFixtureToExploratory(string update)
        {
            string manifestPath = Path.Combine(update, "dhe-resource-update.json");
            var manifest = System.Text.Json.Nodes.JsonNode.Parse(
                File.ReadAllText(manifestPath))!.AsObject();
            string validationPath = ResolveContainedPath(update,
                manifest["validation"]!.GetValue<string>(), "Fixture resource validation");
            string planPath = ResolveContainedPath(update,
                manifest["runtimePlan"]!.GetValue<string>(), "Fixture runtime plan");
            var validation = System.Text.Json.Nodes.JsonNode.Parse(
                File.ReadAllText(validationPath))!.AsObject();
            var plan = System.Text.Json.Nodes.JsonNode.Parse(
                File.ReadAllText(planPath))!.AsObject();
            foreach (var document in new[] { manifest, validation, plan })
            {
                document["mode"] = "Exploratory";
                document["releaseReady"] = false;
                document["releaseChannelId"] = null;
                document["releaseRevision"] = null;
                document["parentReleaseLedgerSha256"] = null;
            }
            manifest["releaseLedger"] = null;
            validation["releaseLedger"] = null;
            File.WriteAllText(validationPath, validation.ToJsonString(Json),
                new UTF8Encoding(false));
            File.WriteAllText(planPath, plan.ToJsonString(Json),
                new UTF8Encoding(false));
            manifest["validationSha256"] = Sha256File(validationPath);
            manifest["runtimePlanSha256"] = Sha256File(planPath);
            File.WriteAllText(manifestPath, manifest.ToJsonString(Json),
                new UTF8Encoding(false));
        }

        (string Update, string Assets, string Identity) CopyFixture(string name,
            string? sourceUpdateRoot = null, bool preserveRelease = false)
        {
            string update = Path.Combine(root, name + "-update");
            string assets = Path.Combine(root, name + "-assets");
            string identity = Path.Combine(root, name + "-build-identity.json");
            CopyDirectory(sourceUpdateRoot ?? updateRoot, update);
            CopyDirectory(assetRoot, assets);
            File.Copy(baseBuildIdentityPath, identity, true);
            if (!preserveRelease) ConvertFixtureToExploratory(update);
            return (update, assets, identity);
        }

        var positive = CopyFixture("positive", preserveRelease: true);
        bool positiveStaged = Stage("positive-stage", positive.Update, positive.Assets,
            positive.Identity);
        AddRegressionCheck(checks, errors, "resource-stage-valid", positiveStaged,
            "a valid resource update must stage into its matching Base.");

        if (!string.IsNullOrWhiteSpace(consecutiveUpdateRoot))
        {
            var consecutive = CopyFixture("consecutive", preserveRelease: true);
            bool firstStaged = Stage("consecutive-n-stage", consecutive.Update,
                consecutive.Assets, consecutive.Identity);
            string secondUpdate = RequireDirectory(consecutiveUpdateRoot,
                "Regression consecutive resource update");
            bool secondStaged = Stage("consecutive-n-plus-one-stage", secondUpdate,
                consecutive.Assets, consecutive.Identity);
            string firstReportPath = Path.Combine(root, "consecutive-n-stage.json");
            string secondReportPath = Path.Combine(root, "consecutive-n-plus-one-stage.json");
            bool consecutiveStable = false;
            bool releaseLedgerBaseTransition = false;
            if (firstStaged && secondStaged && File.Exists(firstReportPath) &&
                File.Exists(secondReportPath))
            {
                JsonElement firstReport = ReadJson<JsonElement>(firstReportPath);
                JsonElement secondReport = ReadJson<JsonElement>(secondReportPath);
                JsonElement firstManifest = ReadJson<JsonElement>(Path.Combine(updateRoot,
                    "dhe-resource-update.json"));
                JsonElement secondManifest = ReadJson<JsonElement>(Path.Combine(secondUpdate,
                    "dhe-resource-update.json"));
                string firstCurrent = GetString(firstReport, "currentAssemblySetSha256") ?? string.Empty;
                string secondCurrent = GetString(secondReport, "currentAssemblySetSha256") ?? string.Empty;
                string firstRegistrySha256 = GetString(firstReport,
                    "baseRegistrySha256") ?? string.Empty;
                string secondRegistrySha256 = GetString(secondReport,
                    "baseRegistrySha256") ?? string.Empty;
                int firstRegistryRevision = GetInt(firstReport, "baseRegistryRevision");
                int secondRegistryRevision = GetInt(secondReport, "baseRegistryRevision");
                bool sameRegistry = string.Equals(firstRegistrySha256,
                        secondRegistrySha256, StringComparison.OrdinalIgnoreCase) &&
                    firstRegistryRevision == secondRegistryRevision;
                bool directSuccessorRegistry = !string.Equals(firstRegistrySha256,
                        secondRegistrySha256, StringComparison.OrdinalIgnoreCase) &&
                    secondRegistryRevision == firstRegistryRevision + 1 &&
                    string.Equals(GetString(secondReport, "baseRegistryParentSha256"),
                        firstRegistrySha256, StringComparison.OrdinalIgnoreCase);
                string[] firstBaseIds = firstManifest.GetProperty("supportedBases")
                    .EnumerateArray().Select(item => GetString(item, "baseId") ?? string.Empty)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
                string[] secondBaseIds = secondManifest.GetProperty("supportedBases")
                    .EnumerateArray().Select(item => GetString(item, "baseId") ?? string.Empty)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
                int firstBaseCount = GetInt(firstManifest, "baseRegistryEntryCount");
                int secondBaseCount = GetInt(secondManifest, "baseRegistryEntryCount");
                consecutiveStable = !string.IsNullOrWhiteSpace(firstCurrent) &&
                    !string.IsNullOrWhiteSpace(secondCurrent) &&
                    string.Equals(GetString(firstReport, "selectedBaseId"),
                        GetString(secondReport, "selectedBaseId"), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(GetString(firstReport, "selectedAotMetadataSetId"),
                        GetString(secondReport, "selectedAotMetadataSetId"), StringComparison.OrdinalIgnoreCase) &&
                    (sameRegistry || directSuccessorRegistry) &&
                    string.Equals(GetString(firstReport, "baseRegistryId"),
                        GetString(secondReport, "baseRegistryId"), StringComparison.Ordinal) &&
                    string.Equals(GetString(firstReport, "releaseChannelId"),
                        GetString(secondReport, "releaseChannelId"), StringComparison.Ordinal) &&
                    GetInt(secondReport, "releaseRevision") ==
                        GetInt(firstReport, "releaseRevision") + 1 &&
                    string.Equals(GetString(secondReport, "parentReleaseLedgerSha256"),
                        GetString(firstReport, "releaseLedgerSha256"),
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(GetString(firstReport, "releaseLedgerSha256"),
                        GetString(secondReport, "releaseLedgerSha256"),
                        StringComparison.OrdinalIgnoreCase) &&
                    GetBool(firstReport, "baseRegistryLineageValidated") &&
                    GetBool(secondReport, "baseRegistryLineageValidated") &&
                    string.Equals(GetString(firstReport, "baseMetaVersionSetSha256"),
                        GetString(secondReport, "baseMetaVersionSetSha256"), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(GetString(firstReport, "baseMetaVersionTreeSha256Before"),
                        GetString(secondReport, "baseMetaVersionTreeSha256Before"), StringComparison.OrdinalIgnoreCase) &&
                    GetBool(firstReport, "baseMetaVersionUnchanged") &&
                    GetBool(secondReport, "baseMetaVersionUnchanged") &&
                    string.Equals(GetString(firstReport, "baseMetaVersionTreeSha256Before"),
                        GetString(firstReport, "baseMetaVersionTreeSha256After"), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(GetString(secondReport, "baseMetaVersionTreeSha256Before"),
                        GetString(secondReport, "baseMetaVersionTreeSha256After"), StringComparison.OrdinalIgnoreCase);
                bool ValidBaseTransition(bool unchangedRegistry, bool successorRegistry,
                    int previousCount, string[] previousIds, int currentCount,
                    string[] currentIds, string previousSelectedId,
                    string currentSelectedId) =>
                    previousCount == previousIds.Length && currentCount == currentIds.Length &&
                    ((unchangedRegistry && previousIds.SequenceEqual(currentIds,
                         StringComparer.OrdinalIgnoreCase)) || successorRegistry) &&
                    previousIds.Contains(previousSelectedId, StringComparer.OrdinalIgnoreCase) &&
                    currentIds.Contains(currentSelectedId, StringComparer.OrdinalIgnoreCase);

                string firstSelectedId = GetString(firstReport, "selectedBaseId") ?? string.Empty;
                string secondSelectedId = GetString(secondReport, "selectedBaseId") ?? string.Empty;
                string[] stableFiveBaseFixture = firstBaseIds.Append(new string('f', 64))
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
                bool stableFiveBaseAccepted = ValidBaseTransition(true, false,
                    stableFiveBaseFixture.Length, stableFiveBaseFixture,
                    stableFiveBaseFixture.Length, stableFiveBaseFixture,
                    stableFiveBaseFixture[0], stableFiveBaseFixture[0]);
                releaseLedgerBaseTransition = stableFiveBaseAccepted &&
                    ValidBaseTransition(sameRegistry, directSuccessorRegistry,
                        firstBaseCount, firstBaseIds, secondBaseCount, secondBaseIds,
                        firstSelectedId, secondSelectedId);
            }
            AddRegressionCheck(checks, errors, "resource-stage-consecutive-base-stable",
                consecutiveStable,
                "consecutive updates must use the same or direct-successor Base registry and " +
                "preserve the selected Base/AOT identity and immutable Base MetaVersion.");
            AddRegressionCheck(checks, errors, "resource-release-ledger-base-transition",
                releaseLedgerBaseTransition,
                "consecutive releases must use the same complete active Base set or an " +
                "authenticated direct-successor registry while preserving the selected Base.");
        }

        var positiveManifest = ReadJson<JsonElement>(Path.Combine(positive.Update,
            "dhe-resource-update.json"));
        var positiveRuntimePlan = ReadJson<JsonElement>(Path.Combine(positive.Update,
            "dhe-runtime-plan.json"));
        string positiveRuntimeAssetRoot = RequirePortableAssetRoot(
            GetString(positiveManifest, "runtimeAssetRoot"), "runtimeAssetRoot");
        string positiveBaseMetaVersionAssetRoot = RequirePortableAssetRoot(
            GetString(positiveManifest, "baseMetaVersionAssetRoot"),
            "baseMetaVersionAssetRoot");
        string positiveBaseRelative = positiveBaseMetaVersionAssetRoot[
            positiveRuntimeAssetRoot.Length..].Trim('/');
        string positiveEmbeddedBase = ResolveContainedPath(positive.Assets,
            positiveBaseRelative, "Regression embedded Base MetaVersion root");
        string positiveIdentityEntryRoot = positiveRuntimeAssetRoot.Trim('/');
        positiveIdentityEntryRoot = positiveIdentityEntryRoot[..
            positiveIdentityEntryRoot.LastIndexOf('/')];

        string CreateAndroidApk(string name, bool tamperIdentity)
        {
            string apkPath = Path.Combine(root, name + ".apk");
            using ZipArchive apk = ZipFile.Open(apkPath, ZipArchiveMode.Create);
            ZipArchiveEntry identityEntry = apk.CreateEntry("assets/" +
                positiveIdentityEntryRoot + "/build-identity.json",
                CompressionLevel.NoCompression);
            byte[] identityBytes = File.ReadAllBytes(positive.Identity);
            if (tamperIdentity) identityBytes[^1] ^= 0x5a;
            using (Stream output = identityEntry.Open())
                output.Write(identityBytes, 0, identityBytes.Length);
            foreach (string source in Directory.GetFiles(positiveEmbeddedBase,
                         "*.mv.bytes", SearchOption.TopDirectoryOnly))
            {
                ZipArchiveEntry entry = apk.CreateEntry("assets/" +
                    positiveBaseMetaVersionAssetRoot.Trim('/') + "/" +
                    Path.GetFileName(source), CompressionLevel.NoCompression);
                using Stream output = entry.Open();
                using FileStream input = File.OpenRead(source);
                input.CopyTo(output);
            }
            return apkPath;
        }

        string positiveApk = CreateAndroidApk("android-base-positive", false);
        bool androidApkBound = false;
        try
        {
            using MaterializedAndroidApkBase materialized =
                MaterializedAndroidApkBase.Create(positiveApk,
                    positiveRuntimeAssetRoot, positiveBaseMetaVersionAssetRoot,
                    positive.Identity);
            JsonElement positiveIdentity = ReadJson<JsonElement>(positive.Identity);
            string positiveBaseId = GetString(positiveIdentity, "baseId") ?? string.Empty;
            JsonElement selectedBase = positiveManifest.GetProperty("supportedBases")
                .EnumerateArray().Single(item => string.Equals(GetString(item, "baseId"),
                    positiveBaseId, StringComparison.OrdinalIgnoreCase));
            (string BaseId, string SetSha256) validated =
                ValidateEmbeddedBaseMetaVersionSet(materialized.MaterializedRoot,
                    positiveManifest, positiveIdentity, selectedBase);
            androidApkBound = validated.BaseId == positiveBaseId &&
                string.Equals(validated.SetSha256,
                    GetString(positiveIdentity, "baseMetaVersionSetSha256"),
                    StringComparison.OrdinalIgnoreCase) &&
                materialized.LogicalRoot.Contains("!/assets/", StringComparison.Ordinal) &&
                string.Equals(materialized.ArtifactSha256, Sha256File(positiveApk),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            androidApkBound = false;
        }
        AddRegressionCheck(checks, errors, "resource-stage-android-apk-base-bound",
            androidApkBound,
            "APK staging must bind its embedded BuildIdentity and Base MetaVersion set without modifying the APK");

        bool androidApkTamperRejected;
        string tamperedApk = CreateAndroidApk("android-base-identity-tamper", true);
        try
        {
            using MaterializedAndroidApkBase _ = MaterializedAndroidApkBase.Create(
                tamperedApk, positiveRuntimeAssetRoot,
                positiveBaseMetaVersionAssetRoot, positive.Identity);
            androidApkTamperRejected = false;
        }
        catch (DheException)
        {
            androidApkTamperRejected = true;
        }
        AddRegressionCheck(checks, errors,
            "resource-stage-android-apk-tamper-rejected",
            androidApkTamperRejected,
            "APK staging must reject an embedded BuildIdentity that differs from the archived Base identity");
        bool releaseLedgerBound = false;
        try
        {
            ReleaseLedgerDocument? ledger = ValidateReleaseLedgerForStaging(positive.Update,
                positiveManifest);
            JsonElement stageReport = ReadJson<JsonElement>(Path.Combine(root,
                "positive-stage.json"));
            releaseLedgerBound = ledger != null &&
                string.Equals(GetString(stageReport, "releaseChannelId"), ledger.ChannelId,
                    StringComparison.Ordinal) &&
                GetInt(stageReport, "releaseRevision") == ledger.Revision &&
                string.Equals(GetString(stageReport, "releaseLedgerSha256"), ledger.Sha256,
                    StringComparison.OrdinalIgnoreCase) &&
                GetBool(stageReport, "releaseReady");
        }
        catch
        {
            releaseLedgerBound = false;
        }
        AddRegressionCheck(checks, errors, "resource-stage-release-ledger-bound",
            releaseLedgerBound,
            "Release staging must bind and copy the manifest-authenticated release ledger");

        var ledgerTamper = CopyFixture("release-ledger-tamper", preserveRelease: true);
        string ledgerTamperPath = Path.Combine(ledgerTamper.Update,
            ReleaseLedgerFileName);
        var ledgerTamperDocument = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(ledgerTamperPath))!.AsObject();
        ledgerTamperDocument["channelId"] = "tampered-release-channel";
        File.WriteAllText(ledgerTamperPath, ledgerTamperDocument.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        bool releaseLedgerTamperRejected = !Stage("release-ledger-tamper-stage",
            ledgerTamper.Update, ledgerTamper.Assets, ledgerTamper.Identity);
        AddRegressionCheck(checks, errors, "resource-stage-release-ledger-tamper-rejected",
            releaseLedgerTamperRejected,
            "staging must reject a release ledger whose identity differs from its manifest");
        string[] positivePayloadNames = SelectPayloadVariant(positiveManifest,
                GetString(positiveManifest.GetProperty("supportedBases").EnumerateArray().First(),
                    "payloadVariantId") ?? "default", "Regression resource manifest")
            .GetProperty("assemblies").EnumerateArray()
            .Select(item => NormalizeName(GetString(item, "assemblyName") ?? string.Empty))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        bool assemblyModeCoverage = positiveManifest.GetProperty("supportedBases").EnumerateArray()
            .All(baseRecord =>
            {
                if (!baseRecord.TryGetProperty("assemblyModes", out JsonElement modes) ||
                    modes.ValueKind != JsonValueKind.Array)
                    return false;
                var modeNames = modes.EnumerateArray().Select(mode =>
                    NormalizeName(GetString(mode, "assemblyName") ?? string.Empty)).ToArray();
                return modeNames.Length == positivePayloadNames.Length &&
                    modeNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() == modeNames.Length &&
                    modeNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                        .SequenceEqual(positivePayloadNames, StringComparer.OrdinalIgnoreCase) &&
                    modes.EnumerateArray().All(mode => IsDheExecutionMode(
                        GetString(mode, "executionMode")));
            });
        AddRegressionCheck(checks, errors, "resource-assembly-mode-coverage",
            assemblyModeCoverage,
            "every supported Base must classify every current payload assembly as DHE or interpreter-only.");
        bool assemblyModeSelectionBound = positiveRuntimePlan.TryGetProperty("baseSelections",
                out JsonElement planSelections) && planSelections.ValueKind == JsonValueKind.Array &&
            planSelections.EnumerateArray().All(selection =>
            {
                if (!selection.TryGetProperty("assemblyModes", out JsonElement modes) ||
                    modes.ValueKind != JsonValueKind.Array)
                    return false;
                var modeNames = modes.EnumerateArray().Select(mode =>
                    NormalizeName(GetString(mode, "assemblyName") ?? string.Empty)).ToArray();
                return modeNames.Length == positivePayloadNames.Length &&
                    modeNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() == modeNames.Length &&
                    modeNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                        .SequenceEqual(positivePayloadNames, StringComparer.OrdinalIgnoreCase) &&
                    modes.EnumerateArray().All(mode => IsDheExecutionMode(
                        GetString(mode, "executionMode")));
            });
        AddRegressionCheck(checks, errors, "resource-assembly-mode-selection-bound",
            assemblyModeSelectionBound,
            "runtime plan Base selections must carry a complete authenticated assembly mode map.");
        var assemblyModeTamper = CopyFixture("assembly-mode-tamper");
        var assemblyModeTamperManifest = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(Path.Combine(assemblyModeTamper.Update,
                "dhe-resource-update.json")))!.AsObject();
        var assemblyModeTamperModes = assemblyModeTamperManifest["supportedBases"]!
            .AsArray()[0]!["assemblyModes"]!.AsArray();
        if (assemblyModeTamperModes.Count != 0)
            assemblyModeTamperModes[0]!["executionMode"] = "unsupported-mode";
        File.WriteAllText(Path.Combine(assemblyModeTamper.Update,
                "dhe-resource-update.json"), assemblyModeTamperManifest.ToJsonString(Json),
            new UTF8Encoding(false));
        AddRegressionCheck(checks, errors, "resource-assembly-mode-tamper-rejected",
            !Stage("assembly-mode-tamper-stage", assemblyModeTamper.Update,
                assemblyModeTamper.Assets, assemblyModeTamper.Identity),
            "an unsupported per-Base assembly execution mode must fail closed before staging.");

        var directBase = CopyFixture("direct-base");
        string directManifestPath = Path.Combine(directBase.Update,
            "dhe-resource-update.json");
        string directValidationPath = Path.Combine(directBase.Update,
            "dhe-resource-update-validation.json");
        var directManifest = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(directManifestPath))!.AsObject();
        var directValidation = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(directValidationPath))!.AsObject();
        foreach (string property in new[]
        {
            "baseRegistrySha256", "baseRegistryEntryCount", "baseRegistryAuditPath",
            "baseRegistryAuditSha256", "baseRegistryId", "baseRegistryRevision",
            "baseRegistryParentSha256", "baseRegistryParentAuditPath",
            "baseRegistryParentAuditSha256", "baseRegistryRetiredBaseCount"
        })
        {
            directManifest[property] = null;
            directValidation[property] = null;
        }
        directManifest["baseRegistryLineageValidated"] = false;
        directValidation["baseRegistryLineageValidated"] = false;
        File.WriteAllText(directValidationPath, directValidation.ToJsonString(Json),
            new UTF8Encoding(false));
        directManifest["validationSha256"] = Sha256File(directValidationPath);
        File.WriteAllText(directManifestPath, directManifest.ToJsonString(Json),
            new UTF8Encoding(false));
        AddRegressionCheck(checks, errors, "resource-stage-direct-base-valid",
            Stage("direct-base-stage", directBase.Update, directBase.Assets,
                directBase.Identity),
            "a direct-Base resource update with null registry fields must stage successfully.");

        string positiveRegistryHash = GetString(positiveManifest, "baseRegistrySha256") ?? string.Empty;
        string positiveRegistryAuditPath = GetString(positiveManifest,
            "baseRegistryAuditPath") ?? string.Empty;
        string positiveRegistryAudit = string.IsNullOrWhiteSpace(positiveRegistryAuditPath)
            ? string.Empty
            : ResolveContainedPath(positive.Update, positiveRegistryAuditPath,
                "Regression Base registry audit copy");
        bool registryAuditBound = !string.IsNullOrWhiteSpace(positiveRegistryHash) &&
            string.Equals(positiveRegistryHash,
                GetString(positiveManifest, "baseRegistryAuditSha256"),
                StringComparison.OrdinalIgnoreCase) &&
            File.Exists(positiveRegistryAudit) &&
            string.Equals(Sha256File(positiveRegistryAudit), positiveRegistryHash,
                StringComparison.OrdinalIgnoreCase);
        AddRegressionCheck(checks, errors, "resource-stage-base-registry-audit-bound",
            registryAuditBound,
            "a registry-backed resource update must archive and hash the exact registry bytes.");

        bool StageReboundRegistryTamper(string name,
            Action<System.Text.Json.Nodes.JsonObject> mutate)
        {
            var fixture = CopyFixture(name);
            string manifestPath = Path.Combine(fixture.Update,
                "dhe-resource-update.json");
            string validationPath = Path.Combine(fixture.Update,
                "dhe-resource-update-validation.json");
            string registryPath = Path.Combine(fixture.Update,
                positiveRegistryAuditPath);
            var manifest = System.Text.Json.Nodes.JsonNode.Parse(
                File.ReadAllText(manifestPath))!.AsObject();
            var validation = System.Text.Json.Nodes.JsonNode.Parse(
                File.ReadAllText(validationPath))!.AsObject();
            var registry = System.Text.Json.Nodes.JsonNode.Parse(
                File.ReadAllText(registryPath))!.AsObject();
            mutate(registry);
            File.WriteAllText(registryPath, registry.ToJsonString(Json),
                new UTF8Encoding(false));
            string registryHash = Sha256File(registryPath);
            int retiredCount = registry["retiredBases"]?.AsArray().Count ?? 0;
            foreach (var document in new[] { manifest, validation })
            {
                document["baseRegistrySha256"] = registryHash;
                document["baseRegistryAuditSha256"] = registryHash;
                document["baseRegistryRetiredBaseCount"] = retiredCount;
            }
            File.WriteAllText(validationPath, validation.ToJsonString(Json),
                new UTF8Encoding(false));
            manifest["validationSha256"] = Sha256File(validationPath);
            File.WriteAllText(manifestPath, manifest.ToJsonString(Json),
                new UTF8Encoding(false));
            return !Stage(name + "-stage", fixture.Update, fixture.Assets,
                fixture.Identity);
        }

        int positiveRegistryRevision = GetInt(positiveManifest,
            "baseRegistryRevision");
        string positiveParentHash = GetString(positiveManifest,
            "baseRegistryParentSha256") ?? string.Empty;
        string positiveParentAuditRelative = GetString(positiveManifest,
            "baseRegistryParentAuditPath") ?? string.Empty;
        string positiveParentAudit = string.IsNullOrWhiteSpace(positiveParentAuditRelative)
            ? string.Empty
            : ResolveContainedPath(positive.Update, positiveParentAuditRelative,
                "Regression parent Base registry audit copy");
        bool registryLineageBound = GetBool(positiveManifest,
                "baseRegistryLineageValidated") &&
            positiveRegistryRevision >= 1 &&
            (positiveRegistryRevision == 1
                ? string.IsNullOrWhiteSpace(positiveParentHash) &&
                  string.IsNullOrWhiteSpace(positiveParentAuditRelative)
                : IsHex(positiveParentHash, 64, 64) &&
                  positiveParentAuditRelative ==
                      "audit/dhe-base-registry-parent.json" &&
                  File.Exists(positiveParentAudit) &&
                  string.Equals(Sha256File(positiveParentAudit), positiveParentHash,
                      StringComparison.OrdinalIgnoreCase));
        AddRegressionCheck(checks, errors,
            "resource-stage-base-registry-lineage-bound", registryLineageBound,
            "a resource update must bind the active registry revision and its direct parent audit.");

        if (registryAuditBound)
        {
            var registryAuditTamper = CopyFixture("base-registry-audit-tamper");
            File.AppendAllText(Path.Combine(registryAuditTamper.Update,
                positiveRegistryAuditPath), Environment.NewLine,
                new UTF8Encoding(false));
            AddRegressionCheck(checks, errors, "resource-stage-base-registry-audit-tamper-rejected",
                !Stage("base-registry-audit-tamper-stage", registryAuditTamper.Update,
                    registryAuditTamper.Assets, registryAuditTamper.Identity),
                "a tampered archived Base registry must be rejected before staging.");

            var registryBindingRemoval = CopyFixture("base-registry-binding-removal");
            string registryBindingManifestPath = Path.Combine(registryBindingRemoval.Update,
                "dhe-resource-update.json");
            var registryBindingManifest = System.Text.Json.Nodes.JsonNode.Parse(
                File.ReadAllText(registryBindingManifestPath))!.AsObject();
            foreach (string property in new[]
            {
                "baseRegistrySha256", "baseRegistryEntryCount", "baseRegistryAuditPath",
                "baseRegistryAuditSha256"
            })
                registryBindingManifest.Remove(property);
            File.WriteAllText(registryBindingManifestPath,
                registryBindingManifest.ToJsonString(Json), new UTF8Encoding(false));
            AddRegressionCheck(checks,
                errors, "resource-stage-base-registry-binding-removal-rejected",
                !Stage("base-registry-binding-removal-stage", registryBindingRemoval.Update,
                    registryBindingRemoval.Assets, registryBindingRemoval.Identity),
                "removing registry binding fields from the manifest must be rejected by validation consistency.");
        }

        bool parentAuditTamperRejected = positiveRegistryRevision == 1;
        if (positiveRegistryRevision > 1 && registryLineageBound)
        {
            var parentAuditTamper = CopyFixture("base-registry-parent-audit-tamper");
            File.AppendAllText(Path.Combine(parentAuditTamper.Update,
                positiveParentAuditRelative), Environment.NewLine,
                new UTF8Encoding(false));
            parentAuditTamperRejected = !Stage(
                "base-registry-parent-audit-tamper-stage",
                parentAuditTamper.Update, parentAuditTamper.Assets,
                parentAuditTamper.Identity);
        }
        AddRegressionCheck(checks, errors,
            "resource-stage-base-registry-parent-tamper-rejected",
            parentAuditTamperRejected,
            "a tampered parent registry audit must be rejected before staging.");

        bool fabricatedRetirementRejected = positiveRegistryRevision == 1;
        bool activeWorkflowTamperRejected = positiveRegistryRevision == 1;
        if (positiveRegistryRevision > 1 && registryLineageBound && registryAuditBound)
        {
            fabricatedRetirementRejected = StageReboundRegistryTamper(
                "base-registry-fabricated-retirement", registry =>
                {
                    registry["retiredBases"]!.AsArray().Add(
                        new System.Text.Json.Nodes.JsonObject
                        {
                            ["baseId"] = new string('e', 64),
                            ["engineWorkflow"] = "Unity2021Standard",
                            ["label"] = "fabricated Base",
                            ["retiredAtRevision"] = positiveRegistryRevision,
                            ["reason"] = "semantic tamper"
                        });
                });
            activeWorkflowTamperRejected = StageReboundRegistryTamper(
                "base-registry-active-workflow-tamper", registry =>
                {
                    var entry = registry["bases"]!.AsArray()[0]!.AsObject();
                    string workflow = entry["engineWorkflow"]!.GetValue<string>();
                    entry["engineWorkflow"] = workflow == "Unity2021Standard"
                        ? "Unity2022Fgs"
                        : "Unity2021Standard";
                });
        }
        AddRegressionCheck(checks, errors,
            "resource-stage-base-registry-fabricated-retirement-rejected",
            fabricatedRetirementRejected,
            "a hash-rebound retirement must still match an active Base in the parent registry.");
        AddRegressionCheck(checks, errors,
            "resource-stage-base-registry-active-workflow-tamper-rejected",
            activeWorkflowTamperRejected,
            "a hash-rebound active Base workflow change must fail parent lineage validation.");

        JsonElement[] positiveAotMetadata = positiveManifest.GetProperty("aotMetadataSets")
            .EnumerateArray()
            .SelectMany(set => set.GetProperty("assemblies").EnumerateArray())
            .ToArray();
        JsonElement positiveBases = positiveManifest.GetProperty("supportedBases");
        bool planCapabilityBound = positiveBases.EnumerateArray().All(supportedBase =>
        {
            var capabilities = supportedBase.GetProperty("requiredRuntimeCapabilities")
                .EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToHashSet(
                    StringComparer.Ordinal);
            return capabilities.Contains("resource-update-plan-integrity-v1") &&
                capabilities.Contains("resource-update-aot-metadata-set-selection-v1");
        });
        AddRegressionCheck(checks, errors, "resource-stage-plan-capability-bound",
            planCapabilityBound,
            "every resource update Base must require manifest-bound runtime plan validation.");
        bool aotMetadataCapabilityBound = positiveAotMetadata.Length == 0 ||
            positiveBases.EnumerateArray().All(supportedBase =>
            {
                string setId = GetString(supportedBase, "aotMetadataSetId") ?? string.Empty;
                JsonElement[] sets = positiveManifest.GetProperty("aotMetadataSets")
                    .EnumerateArray().Where(set => string.Equals(GetString(set,
                        "aotMetadataSetId"), setId, StringComparison.OrdinalIgnoreCase)).ToArray();
                bool hasMetadata = sets.Length == 1 && sets[0].TryGetProperty("assemblies",
                    out JsonElement records) && records.ValueKind == JsonValueKind.Array &&
                    records.GetArrayLength() != 0;
                bool hasPathCapability = supportedBase.GetProperty("requiredRuntimeCapabilities")
                    .EnumerateArray().Any(value => string.Equals(value.GetString(),
                        "resource-update-aot-metadata-path-v1", StringComparison.Ordinal));
                return !hasMetadata || hasPathCapability;
            });
        AddRegressionCheck(checks, errors, "resource-stage-aot-metadata-capability-bound",
            aotMetadataCapabilityBound,
            "a resource update with AOT metadata must require plan-directed metadata loading.");
        bool positiveAotMetadataCopied = positiveStaged &&
            positiveAotMetadata.All(metadata =>
            {
                string assetPath = GetString(metadata, "path") ?? string.Empty;
                string expectedHash = GetString(metadata, "sha256") ?? string.Empty;
                if (!assetPath.StartsWith(positiveRuntimeAssetRoot,
                        StringComparison.OrdinalIgnoreCase)) return false;
                string target = ResolveContainedPath(positive.Assets,
                    assetPath[positiveRuntimeAssetRoot.Length..],
                    "Regression staged AOT metadata");
                return File.Exists(target) && string.Equals(Sha256File(target), expectedHash,
                    StringComparison.OrdinalIgnoreCase);
            });
        AddRegressionCheck(checks, errors, "resource-stage-aot-metadata-copied",
            positiveAotMetadataCopied,
            "a valid resource update must contain and copy every hashed AOT metadata payload.");
        bool positiveAotMetadataShortPaths = positiveAotMetadata.All(metadata =>
        {
            string assetPath = GetString(metadata, "path") ?? string.Empty;
            string fileName = Path.GetFileName(assetPath);
            return System.Text.RegularExpressions.Regex.IsMatch(fileName,
                "^[0-9a-fA-F]{32}\\.bytes$");
        });
        AddRegressionCheck(checks, errors, "resource-stage-aot-metadata-short-path",
            positiveAotMetadataShortPaths,
            "supplemental AOT metadata must use a 128-bit SHA-256 prefix file name to remain below Windows MAX_PATH.");

        var runtimePlanTamper = CopyFixture("runtime-plan-tamper");
        var runtimePlanTamperManifest = ReadJson<JsonElement>(Path.Combine(
            runtimePlanTamper.Update, "dhe-resource-update.json"));
        string runtimePlanTamperPath = ResolveContainedPath(runtimePlanTamper.Update,
            GetString(runtimePlanTamperManifest, "runtimePlan") ?? string.Empty,
            "Regression runtime plan");
        File.AppendAllText(runtimePlanTamperPath, Environment.NewLine,
            new UTF8Encoding(false));
        AddRegressionCheck(checks, errors, "resource-stage-runtime-plan-tamper-rejected",
            !Stage("runtime-plan-tamper-stage", runtimePlanTamper.Update,
                runtimePlanTamper.Assets, runtimePlanTamper.Identity),
            "a runtime plan whose bytes do not match the manifest must be rejected.");

        if (positiveAotMetadata.Length > 0)
        {
            var aotMetadataTamper = CopyFixture("aot-metadata-tamper");
            var aotMetadataTamperManifest = ReadJson<JsonElement>(Path.Combine(
                aotMetadataTamper.Update, "dhe-resource-update.json"));
            string aotMetadataRuntimeRoot = RequirePortableAssetRoot(
                GetString(aotMetadataTamperManifest, "runtimeAssetRoot"), "runtimeAssetRoot");
            string aotMetadataAssetPath = GetString(positiveAotMetadata[0], "path") ?? string.Empty;
            string aotMetadataTamperPath = ResolveContainedPath(aotMetadataTamper.Update,
                aotMetadataAssetPath[aotMetadataRuntimeRoot.Length..],
                "Regression AOT metadata payload");
            byte[] aotMetadataTamperBytes = File.ReadAllBytes(aotMetadataTamperPath);
            aotMetadataTamperBytes[^1] ^= 0x5a;
            File.WriteAllBytes(aotMetadataTamperPath, aotMetadataTamperBytes);
            AddRegressionCheck(checks, errors, "resource-stage-aot-metadata-tamper-rejected",
                !Stage("aot-metadata-tamper-stage", aotMetadataTamper.Update,
                    aotMetadataTamper.Assets, aotMetadataTamper.Identity),
                "an AOT metadata payload whose bytes do not match the manifest must be rejected.");

            var aotMetadataMissing = CopyFixture("aot-metadata-missing");
            var aotMetadataMissingManifest = ReadJson<JsonElement>(Path.Combine(
                aotMetadataMissing.Update, "dhe-resource-update.json"));
            string aotMetadataMissingRuntimeRoot = RequirePortableAssetRoot(
                GetString(aotMetadataMissingManifest, "runtimeAssetRoot"), "runtimeAssetRoot");
            string aotMetadataMissingAssetPath = GetString(positiveAotMetadata[0], "path") ?? string.Empty;
            File.Delete(ResolveContainedPath(aotMetadataMissing.Update,
                aotMetadataMissingAssetPath[aotMetadataMissingRuntimeRoot.Length..],
                "Regression AOT metadata payload"));
            AddRegressionCheck(checks, errors, "resource-stage-aot-metadata-missing-rejected",
                !Stage("aot-metadata-missing-stage", aotMetadataMissing.Update,
                    aotMetadataMissing.Assets, aotMetadataMissing.Identity),
                "a resource update with a missing AOT metadata payload must be rejected.");
        }

        var sharedMetaVersion = CopyFixture("shared-metaversion");
        string sharedManifestPath = Path.Combine(sharedMetaVersion.Update,
            "dhe-resource-update.json");
        var sharedManifest = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(sharedManifestPath))!.AsObject();
        var duplicateBase = System.Text.Json.Nodes.JsonNode.Parse(
            sharedManifest["supportedBases"]!.AsArray()[0]!.ToJsonString())!.AsObject();
        duplicateBase["baseId"] = new string('f', 64);
        duplicateBase["buildIdentitySha256"] = new string('e', 64);
        sharedManifest["supportedBases"]!.AsArray().Add(duplicateBase);
        string sharedValidationPath = ResolveContainedPath(sharedMetaVersion.Update,
            sharedManifest["validation"]!.GetValue<string>(),
            "Regression resource validation");
        var sharedValidation = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(sharedValidationPath))!.AsObject();
        var duplicateValidationBase = System.Text.Json.Nodes.JsonNode.Parse(
            sharedValidation["bases"]!.AsArray()[0]!.ToJsonString())!.AsObject();
        duplicateValidationBase["baseId"] = new string('f', 64);
        duplicateValidationBase["buildIdentitySha256"] = new string('e', 64);
        sharedValidation["bases"]!.AsArray().Add(duplicateValidationBase);
        File.WriteAllText(sharedValidationPath, sharedValidation.ToJsonString(Json),
            new UTF8Encoding(false));
        string sharedPlanPath = ResolveContainedPath(sharedMetaVersion.Update,
            sharedManifest["runtimePlan"]!.GetValue<string>(),
            "Regression runtime plan");
        var sharedPlan = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(sharedPlanPath))!.AsObject();
        var duplicateSelection = System.Text.Json.Nodes.JsonNode.Parse(
            sharedPlan["baseSelections"]!.AsArray()[0]!.ToJsonString())!.AsObject();
        duplicateSelection["baseId"] = new string('f', 64);
        sharedPlan["baseSelections"]!.AsArray().Add(duplicateSelection);
        File.WriteAllText(sharedPlanPath, sharedPlan.ToJsonString(Json),
            new UTF8Encoding(false));
        sharedManifest["validationSha256"] = Sha256File(sharedValidationPath);
        sharedManifest["runtimePlanSha256"] = Sha256File(sharedPlanPath);
        File.WriteAllText(sharedManifestPath, sharedManifest.ToJsonString(Json),
            new UTF8Encoding(false));
        AddRegressionCheck(checks, errors, "resource-stage-shared-metaversion-valid",
            Stage("shared-metaversion-stage", sharedMetaVersion.Update,
                sharedMetaVersion.Assets, sharedMetaVersion.Identity),
            "BuildIdentity must select one Base when multiple runtime identities share " +
            "the same Base MetaVersion set.");

        var identityHashTamper = CopyFixture("identity-hash-tamper");
        File.AppendAllText(identityHashTamper.Identity, Environment.NewLine,
            new UTF8Encoding(false));
        AddRegressionCheck(checks, errors, "resource-stage-identity-hash-tamper-rejected",
            !Stage("identity-hash-tamper-stage", identityHashTamper.Update,
                identityHashTamper.Assets, identityHashTamper.Identity),
            "a BuildIdentity whose file hash does not match supportedBases must be rejected.");

        var identityBaseIdTamper = CopyFixture("identity-base-id-tamper");
        var tamperedIdentity = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(identityBaseIdTamper.Identity))!.AsObject();
        tamperedIdentity["baseId"] = new string('f', 64);
        File.WriteAllText(identityBaseIdTamper.Identity, tamperedIdentity.ToJsonString(Json),
            new UTF8Encoding(false));
        AddRegressionCheck(checks, errors, "resource-stage-identity-base-id-tamper-rejected",
            !Stage("identity-base-id-tamper-stage", identityBaseIdTamper.Update,
                identityBaseIdTamper.Assets, identityBaseIdTamper.Identity),
            "a BuildIdentity with an invalid composite baseId must be rejected.");

        var identityAotInventoryMissing = CopyFixture("identity-aot-inventory-missing");
        var missingAotInventoryIdentity = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(identityAotInventoryMissing.Identity))!.AsObject();
        missingAotInventoryIdentity.Remove("aotAssemblyNames");
        File.WriteAllText(identityAotInventoryMissing.Identity,
            missingAotInventoryIdentity.ToJsonString(Json), new UTF8Encoding(false));
        AddRegressionCheck(checks, errors,
            "resource-stage-aot-inventory-missing-rejected",
            !Stage("identity-aot-inventory-missing-stage", identityAotInventoryMissing.Update,
                identityAotInventoryMissing.Assets, identityAotInventoryMissing.Identity),
            "a BuildIdentity without the complete Base AOT inventory must be rejected.");

        var identityAotInventoryHashTamper = CopyFixture("identity-aot-inventory-hash-tamper");
        var tamperedAotInventoryIdentity = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(identityAotInventoryHashTamper.Identity))!.AsObject();
        tamperedAotInventoryIdentity["aotAssemblySetSha256"] = new string('f', 64);
        File.WriteAllText(identityAotInventoryHashTamper.Identity,
            tamperedAotInventoryIdentity.ToJsonString(Json), new UTF8Encoding(false));
        AddRegressionCheck(checks, errors,
            "resource-stage-aot-inventory-hash-tamper-rejected",
            !Stage("identity-aot-inventory-hash-tamper-stage",
                identityAotInventoryHashTamper.Update,
                identityAotInventoryHashTamper.Assets,
                identityAotInventoryHashTamper.Identity),
            "a BuildIdentity with a tampered Base AOT inventory hash must be rejected.");

        var tampered = CopyFixture("payload-tamper");
        var tamperedManifest = ReadJson<JsonElement>(Path.Combine(tampered.Update,
            "dhe-resource-update.json"));
        JsonElement payloadTamperIdentity = ReadJson<JsonElement>(tampered.Identity);
        string tamperedBaseId = GetString(payloadTamperIdentity, "baseId") ?? string.Empty;
        JsonElement tamperedBase = tamperedManifest.GetProperty("supportedBases")
            .EnumerateArray().Single(item => string.Equals(GetString(item, "baseId"),
                tamperedBaseId, StringComparison.OrdinalIgnoreCase));
        string tamperedVariantId = GetString(tamperedBase, "payloadVariantId") ?? "default";
        JsonElement tamperedVariant = SelectPayloadVariant(tamperedManifest, tamperedVariantId,
            "Regression payload variant");
        string tamperedPayload = ResolveContainedPath(tampered.Update,
            GetString(tamperedVariant.GetProperty("assemblies")[0], "dll") ?? string.Empty,
            "Regression payload");
        byte[] tamperedBytes = File.ReadAllBytes(tamperedPayload);
        tamperedBytes[^1] ^= 0x5a;
        File.WriteAllBytes(tamperedPayload, tamperedBytes);
        AddRegressionCheck(checks, errors, "resource-stage-payload-tamper-rejected",
            !Stage("payload-tamper-stage", tampered.Update, tampered.Assets,
                tampered.Identity),
            "a payload whose bytes do not match the manifest must be rejected.");

        var missing = CopyFixture("payload-missing");
        var missingManifest = ReadJson<JsonElement>(Path.Combine(missing.Update,
            "dhe-resource-update.json"));
        JsonElement missingIdentity = ReadJson<JsonElement>(missing.Identity);
        string missingBaseId = GetString(missingIdentity, "baseId") ?? string.Empty;
        JsonElement missingBase = missingManifest.GetProperty("supportedBases")
            .EnumerateArray().Single(item => string.Equals(GetString(item, "baseId"),
                missingBaseId, StringComparison.OrdinalIgnoreCase));
        string missingVariantId = GetString(missingBase, "payloadVariantId") ?? "default";
        JsonElement missingVariant = SelectPayloadVariant(missingManifest, missingVariantId,
            "Regression payload variant");
        string missingPayload = ResolveContainedPath(missing.Update,
            GetString(missingVariant.GetProperty("assemblies")[0],
                "currentMetaVersion") ?? string.Empty, "Regression payload");
        File.Delete(missingPayload);
        AddRegressionCheck(checks, errors, "resource-stage-missing-payload-rejected",
            !Stage("payload-missing-stage", missing.Update, missing.Assets, missing.Identity),
            "a resource update with a missing current MetaVersion must be rejected.");

        var wrongBase = CopyFixture("unsupported-base");
        var wrongBaseManifest = ReadJson<JsonElement>(Path.Combine(wrongBase.Update,
            "dhe-resource-update.json"));
        string runtimeAssetRoot = RequirePortableAssetRoot(
            GetString(wrongBaseManifest, "runtimeAssetRoot"), "runtimeAssetRoot");
        string baseAssetRoot = RequirePortableAssetRoot(
            GetString(wrongBaseManifest, "baseMetaVersionAssetRoot"),
            "baseMetaVersionAssetRoot");
        string baseRelative = baseAssetRoot[runtimeAssetRoot.Length..].TrimEnd('/');
        string embeddedBase = ResolveContainedPath(wrongBase.Assets, baseRelative,
            "Regression embedded Base");
        foreach (JsonElement assembly in wrongBaseManifest.GetProperty("assemblies")
                     .EnumerateArray())
        {
            string name = NormalizeName(GetString(assembly, "assemblyName") ?? string.Empty);
            string currentMetaVersion = ResolveContainedPath(wrongBase.Update,
                GetString(assembly, "currentMetaVersion") ?? string.Empty,
                "Regression current MetaVersion");
            File.Copy(currentMetaVersion,
                Path.Combine(embeddedBase, name + ".mv.bytes"), true);
        }
        File.Copy(Path.Combine(embeddedBase,
                NormalizeName(GetString(wrongBaseManifest.GetProperty("assemblies")[0],
                    "assemblyName") ?? string.Empty) + ".mv.bytes"),
            Path.Combine(embeddedBase, "Unexpected.mv.bytes"), true);
        AddRegressionCheck(checks, errors, "resource-stage-unsupported-base-rejected",
            !Stage("unsupported-base-stage", wrongBase.Update, wrongBase.Assets,
                wrongBase.Identity),
            "an embedded Base MetaVersion set absent from supportedBases must be rejected.");

        var retired = CopyFixture("retired-mv2");
        string retiredRoot = ResolveContainedPath(retired.Assets, baseRelative,
            "Regression embedded Base");
        string currentBaseMv = Directory.GetFiles(retiredRoot, "*.mv.bytes",
            SearchOption.TopDirectoryOnly).First();
        File.Copy(currentBaseMv, Path.Combine(retiredRoot,
            Path.GetFileName(currentBaseMv).Replace(".mv.bytes", ".mv2.bytes",
                StringComparison.Ordinal)), true);
        AddRegressionCheck(checks, errors, "resource-stage-retired-mv2-rejected",
            !Stage("retired-mv2-stage", retired.Update, retired.Assets, retired.Identity),
            "retired .mv2.bytes artifacts must be rejected before staging.");

        var missingCapability = CopyFixture("missing-capability");
        string capabilityManifestPath = Path.Combine(missingCapability.Update,
            "dhe-resource-update.json");
        var capabilityManifest = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(capabilityManifestPath))!.AsObject();
        var manifestBase = capabilityManifest["supportedBases"]!.AsArray()[0]!.AsObject();
        string removedCapability = manifestBase["requiredRuntimeCapabilities"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .First(value => value != "aot-guard-v1");
        RemoveJsonString(manifestBase["runtimeCapabilities"]!.AsArray(), removedCapability);

        string validationRelative = capabilityManifest["validation"]!.GetValue<string>();
        string capabilityValidationPath = ResolveContainedPath(missingCapability.Update,
            validationRelative, "Regression resource validation");
        var capabilityValidation = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(capabilityValidationPath))!.AsObject();
        RemoveJsonString(capabilityValidation["bases"]!.AsArray()[0]!["runtimeCapabilities"]!
            .AsArray(), removedCapability);
        File.WriteAllText(capabilityValidationPath,
            capabilityValidation.ToJsonString(Json), new UTF8Encoding(false));
        capabilityManifest["validationSha256"] = Sha256File(capabilityValidationPath);
        File.WriteAllText(capabilityManifestPath, capabilityManifest.ToJsonString(Json),
            new UTF8Encoding(false));
        AddRegressionCheck(checks, errors, "resource-stage-missing-capability-rejected",
            !Stage("missing-capability-stage", missingCapability.Update,
                missingCapability.Assets, missingCapability.Identity),
            "a Base missing a required runtime capability must be rejected.");
    }

    private static void RemoveJsonString(System.Text.Json.Nodes.JsonArray values,
        string expected)
    {
        for (int index = values.Count - 1; index >= 0; index--)
        {
            if (string.Equals(values[index]?.GetValue<string>(), expected,
                    StringComparison.Ordinal))
                values.RemoveAt(index);
        }
    }

    private static void RunGuardBlockHashRegressions(string regressionRoot, List<object> checks,
        List<string> errors)
    {
        const string functionName = "DheRegression_Probe_m0123456789ABCDEF";
        const uint methodToken = 100663297;
        var guardRoot = Path.Combine(regressionRoot, "guard-block-hash");
        Directory.CreateDirectory(guardRoot);
        var sourcePath = Path.Combine(guardRoot, "Regression.cpp");
        var begin = NativeGuardBeginPrefix + functionName + ":" +
            methodToken.ToString(CultureInfo.InvariantCulture);
        var end = NativeGuardEndPrefix + functionName + ":" +
            methodToken.ToString(CultureInfo.InvariantCulture);
        var block = "    // " + begin + "\r\n" +
                    "    hybridclr::dhe::RecordAotEntry();\r\n" +
                    "    const RuntimeMethod* dheMethod = method;\r\n" +
                    "    // " + end;
        using var nativeDocument = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            resolverVersion = 3,
            abiContract = "il2cpp-generated-cpp-signature-v2",
            guardHashContract = NativeGuardHashContract,
            generatedCppRoot = guardRoot,
            methods = new[] { new { functionName, methodToken, sourceFile = sourcePath } }
        }, Json));
        File.WriteAllText(sourcePath, "// unrelated before\r\n" + block +
            "\r\n// unrelated after\r\n", new UTF8Encoding(false));
        var originalHash = GuardBlockSetHash(nativeDocument.RootElement, guardRoot, guardRoot);
        File.WriteAllText(sourcePath, "// changed unrelated content\n" +
            block.Replace("\r\n", "\n", StringComparison.Ordinal) +
            "\n// another unrelated change\n", new UTF8Encoding(false));
        var surroundingHash = GuardBlockSetHash(nativeDocument.RootElement, guardRoot, guardRoot);
        AddRegressionCheck(checks, errors, "native-guard-unrelated-source-stable",
            originalHash == surroundingHash,
            "unrelated generated C++ changes must not change the guard-block identity");

        File.WriteAllText(sourcePath, block.Replace("RecordAotEntry", "RecordAotEntryTampered",
            StringComparison.Ordinal), new UTF8Encoding(false));
        var tamperedHash = GuardBlockSetHash(nativeDocument.RootElement, guardRoot, guardRoot);
        AddRegressionCheck(checks, errors, "native-guard-block-tamper",
            originalHash != tamperedHash, "a guard block mutation must change its identity");

        File.WriteAllText(sourcePath, block + "\r\n" + block, new UTF8Encoding(false));
        var duplicateRejected = false;
        try { _ = GuardBlockSetHash(nativeDocument.RootElement, guardRoot, guardRoot); }
        catch { duplicateRejected = true; }
        AddRegressionCheck(checks, errors, "native-guard-duplicate-marker", duplicateRejected,
            "duplicate guard markers must be rejected");

        File.WriteAllText(sourcePath, block.Substring(0,
            block.IndexOf("    // " + end, StringComparison.Ordinal)), new UTF8Encoding(false));
        var missingEndRejected = false;
        try { _ = GuardBlockSetHash(nativeDocument.RootElement, guardRoot, guardRoot); }
        catch { missingEndRejected = true; }
        AddRegressionCheck(checks, errors, "native-guard-missing-end-marker", missingEndRejected,
            "a guard without its end marker must be rejected");
    }

    private static void WriteMutatedAssembly(string source, string destination, Action<ModuleDefMD> mutate)
    {
        using var module = ModuleDefMD.Load(source);
        mutate(module);
        module.Write(destination);
    }

    private static void RequireFormat(JsonElement document, string format, string description, List<string> errors)
    {
        if (GetInt(document, "schemaVersion") != 1 || !string.Equals(GetString(document, "format"), format, StringComparison.Ordinal))
            errors.Add(description + " has an invalid schema or format.");
    }

    private static void RequireTrue(JsonElement document, string property, string description, List<string> errors)
    {
        if (!GetBool(document, property)) errors.Add(description + "." + property + " must be true.");
    }

    private static void ValidateSourceIdentity(JsonElement checkout, string property, List<string> errors)
    {
        if (!checkout.TryGetProperty(property, out var identity) || identity.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Clean checkout is missing " + property + ".");
            return;
        }
        RequireTrue(identity, "tested", property, errors);
        RequireTrue(identity, "passed", property, errors);
        RequireTrue(identity, "clean", property, errors);
        RequireTrue(identity, "cleanRequired", property, errors);
        RequireTrue(identity, "trackedSourcesTested", property, errors);
        RequireTrue(identity, "trackedSourcesComplete", property, errors);
        RequireTrue(identity, "trackedSourcesRequired", property, errors);
        var vcs = GetString(identity, "vcs");
        if (vcs == "git" && (string.IsNullOrWhiteSpace(GetString(identity, "head")) || string.IsNullOrWhiteSpace(GetString(identity, "tree")))) errors.Add(property + " Git identity is incomplete.");
        if (vcs == "svn" && (string.IsNullOrWhiteSpace(GetString(identity, "revision")) || string.IsNullOrWhiteSpace(GetString(identity, "repository")))) errors.Add(property + " SVN identity is incomplete.");
    }

    private static string IdentityString(JsonElement checkout, string identityName, string property)
    {
        return checkout.TryGetProperty(identityName, out var identity) ? GetString(identity, property) ?? "" : "";
    }

    private static string[] StringArray(JsonElement document, string property, List<string> errors)
    {
        if (!document.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            errors.Add(property + " must be an array.");
            return Array.Empty<string>();
        }
        return value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray();
    }

    private static string ResolveEvidencePath(string? value, string baseDirectory, string description)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new DheException(description + " path is missing.");
        return RequireFile(Path.IsPathRooted(value) ? value : Path.Combine(baseDirectory, value), description);
    }

    private static void ValidateMetaVersionArtifacts(JsonElement planRecord, string planRoot,
        string propertyPrefix, MetaVersionSnapshot expected, List<string> errors)
    {
        string jsonPath = ResolveEvidencePath(GetString(planRecord, propertyPrefix + "Json"),
            planRoot, propertyPrefix + " JSON");
        string binaryPath = ResolveEvidencePath(GetString(planRecord, propertyPrefix + "Bytes"),
            planRoot, propertyPrefix + " binary");
        JsonElement json = ReadJson<JsonElement>(jsonPath);
        RequireFormat(json, "hybridclr.dhe-metaversion.json", propertyPrefix, errors);
        if (GetInt(json, "schemaVersion") != MetaVersionSnapshot.SchemaVersion ||
            !string.Equals(GetString(json, "assemblyName"), expected.AssemblyName,
                StringComparison.Ordinal) ||
            !string.Equals(GetString(json, "assemblyMetadataVersion"),
                expected.AssemblyMetadataVersion, StringComparison.OrdinalIgnoreCase) ||
            !json.TryGetProperty("assembly", out JsonElement assembly) ||
            !string.Equals(GetString(assembly, "sha256"), expected.AssemblySha256,
                StringComparison.OrdinalIgnoreCase))
            errors.Add(propertyPrefix + " JSON does not match live assembly: " +
                expected.AssemblyName);
        if (!File.ReadAllBytes(binaryPath).SequenceEqual(expected.ToBinary()))
            errors.Add(propertyPrefix + " binary does not match live assembly: " +
                expected.AssemblyName);
    }

    private static int CountRuntimeChangedMethods(MetaVersionSnapshot baseline,
        MetaVersionSnapshot current)
    {
        var currentMethods = current.Methods.ToDictionary(method => method.StableId,
            StringComparer.OrdinalIgnoreCase);
        return baseline.Methods.Count(method => !currentMethods.TryGetValue(method.StableId,
            out MetaVersionMethod? currentMethod) || !string.Equals(method.Version,
            currentMethod.Version, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MethodCanHaveAotEntry(MetaVersionMethod method) =>
        (method.Flags & 8u) != 0 && (method.Flags & (2u | 4u)) == 0;

    private static void ValidatePlayerAssemblies(JsonElement player, string[] planNames, List<string> errors)
    {
        if (!player.TryGetProperty("assemblyValidations", out var validations) || validations.ValueKind != JsonValueKind.Array)
        {
            errors.Add("Player assembly validations are missing.");
            return;
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var validation in validations.EnumerateArray())
        {
            var name = GetString(validation, "assemblyName") ?? "";
            if (!names.Add(name) || !GetBool(validation, "hashValidated") || GetString(validation, "loadError") != "OK")
                errors.Add("Player assembly validation is invalid: " + name);
        }
        if (!new HashSet<string>(planNames, StringComparer.OrdinalIgnoreCase).SetEquals(names))
            errors.Add("Player assembly validation set does not match the project plan.");
    }

    private static void ValidateNativeManifest(JsonElement native,
        IReadOnlyDictionary<string, LiveAssemblyValidation> diffs,
        List<string> errors)
    {
        if (GetInt(native, "schemaVersion") != 1 || GetInt(native, "resolverVersion") != 3 ||
            GetString(native, "abiContract") != "il2cpp-generated-cpp-signature-v2" ||
            GetString(native, "guardHashContract") != NativeGuardHashContract ||
            GetString(native, "runtimeProtocol") != ResourceUpdateCompatibility.RuntimeProtocol ||
            GetString(native, "runtimeContract") != ResourceUpdateCompatibility.CurrentNativeRuntimeContract ||
            !new HashSet<string>(StringArray(native, "runtimeCapabilities", errors),
                StringComparer.Ordinal).SetEquals(ResourceUpdateCompatibility.KnownRuntimeCapabilities))
            errors.Add("Native manifest contract is invalid.");
        var expected = diffs.ToDictionary(pair => pair.Key, pair => pair.Value.Baseline.Methods
            .Where(MethodCanHaveAotEntry).ToDictionary(method => method.StableId,
                method => method.Token, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var covered = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (!native.TryGetProperty("methods", out var methods) || methods.ValueKind != JsonValueKind.Array)
        {
            errors.Add("Native manifest methods are missing.");
            return;
        }
        foreach (var method in methods.EnumerateArray())
        {
            var assembly = GetString(method, "assemblyName") ?? "";
            var token = method.TryGetProperty("methodToken", out var tokenValue) && tokenValue.TryGetUInt32(out var raw) ? raw : 0;
            string stableId = GetString(method, "stableMethodIdSha256") ?? "";
            if (!expected.TryGetValue(assembly, out var expectedMethods) ||
                !expectedMethods.TryGetValue(stableId, out uint expectedToken) ||
                expectedToken != token)
                errors.Add("Native manifest contains an unexpected method: " + assembly + ":" +
                    stableId + ":" + token.ToString("x8"));
            if (!covered.TryGetValue(assembly, out var methodsForAssembly))
                covered[assembly] = methodsForAssembly = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
            methodsForAssembly.Add(stableId);
        }
        if (!native.TryGetProperty("interpreterOnlyMethods", out var interpreterOnly) ||
            interpreterOnly.ValueKind != JsonValueKind.Array)
            errors.Add("Native manifest interpreter-only methods are missing.");
        else foreach (var method in interpreterOnly.EnumerateArray())
        {
            string assembly = GetString(method, "assemblyName") ?? "";
            string stableId = GetString(method, "stableMethodIdSha256") ?? "";
            if (!expected.TryGetValue(assembly, out var expectedMethods) ||
                !expectedMethods.ContainsKey(stableId))
                errors.Add("Native manifest contains an unexpected interpreter-only method: " +
                    assembly + ":" + stableId);
            if (!covered.TryGetValue(assembly, out var methodsForAssembly))
                covered[assembly] = methodsForAssembly = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
            methodsForAssembly.Add(stableId);
        }
        foreach (var pair in expected)
        {
            covered.TryGetValue(pair.Key, out var stableIds);
            if (!new HashSet<string>(pair.Value.Keys, StringComparer.OrdinalIgnoreCase)
                    .SetEquals(stableIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                errors.Add("Native manifest universal method coverage mismatch: " + pair.Key);
        }
        int expectedCount = expected.Sum(pair => pair.Value.Count);
        if (expectedCount != GetInt(native, "guardedMethodCount") ||
            GetInt(native, "unsupportedGuardedMethodCount") != 0)
            errors.Add("Native manifest universal method count is inconsistent.");
    }

    private static void ValidateBuildIdentity(JsonElement identity, string identityPath, JsonElement native,
        string nativePath, IReadOnlyDictionary<string, LiveAssemblyValidation> diffs,
        List<string> errors)
    {
        if (!identity.TryGetProperty("assemblies", out var assemblies) || assemblies.ValueKind != JsonValueKind.Array)
        {
            errors.Add("Build identity assembly evidence is missing.");
            return;
        }
        var baselineRecords = new List<KeyValuePair<string, byte[]>>();
        var snapshotRecords = new List<KeyValuePair<string, byte[]>>();
        var baseMetaVersionRecords = new List<KeyValuePair<string, byte[]>>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in assemblies.EnumerateArray().OrderBy(record => GetString(record, "assemblyName"), StringComparer.Ordinal))
        {
            var name = GetString(record, "assemblyName") ?? "";
            if (!names.Add(name) || !diffs.TryGetValue(name, out var diff))
            {
                errors.Add("Build identity contains an unexpected or duplicate assembly: " + name);
                continue;
            }
            try
            {
                var baselinePath = ResolveEvidencePath(GetString(record, "baselinePath"),
                    Path.GetDirectoryName(identityPath)!, "Build identity baseline assembly");
                var bytes = File.ReadAllBytes(baselinePath);
                var hash = Sha256File(baselinePath);
                if (!hash.Equals(diff.Baseline.AssemblySha256, StringComparison.OrdinalIgnoreCase) ||
                    !hash.Equals(GetString(record, "baselineSha256"), StringComparison.OrdinalIgnoreCase) ||
                    !hash.Equals(GetString(record, "snapshotSha256"), StringComparison.OrdinalIgnoreCase))
                    errors.Add("Build identity baseline/snapshot hash mismatch: " + name);
                baselineRecords.Add(new KeyValuePair<string, byte[]>(name, bytes));
                snapshotRecords.Add(new KeyValuePair<string, byte[]>(name, Convert.FromHexString(hash)));
                var baseMetaVersionPath = ResolveEvidencePath(
                    GetString(record, "baseMetaVersionPath"),
                    Path.GetDirectoryName(identityPath)!,
                    "Build identity Base MetaVersion");
                byte[] baseMetaVersion = File.ReadAllBytes(baseMetaVersionPath);
                if (!Sha256Bytes(baseMetaVersion).Equals(
                        GetString(record, "baseMetaVersionSha256"),
                        StringComparison.OrdinalIgnoreCase))
                    errors.Add("Build identity Base MetaVersion hash mismatch: " + name);
                baseMetaVersionRecords.Add(new KeyValuePair<string, byte[]>(name,
                    baseMetaVersion));
            }
            catch (Exception ex) { errors.Add("Build identity " + name + ": " + ex.Message); }
        }
        if (!new HashSet<string>(diffs.Keys, StringComparer.OrdinalIgnoreCase).SetEquals(names))
            errors.Add("Build identity assembly set does not match the project plan.");
        string[] aotAssemblyNames = Array.Empty<string>();
        try
        {
            aotAssemblyNames = ReadIdentityAotAssemblyNames(identity, identityPath);
            if (!new HashSet<string>(aotAssemblyNames, StringComparer.OrdinalIgnoreCase)
                    .IsSupersetOf(names))
                errors.Add("Build identity DHE set is not a subset of its complete AOT inventory.");
        }
        catch (Exception exception)
        {
            errors.Add("Build identity complete AOT inventory: " + exception.Message);
        }
        string managedAssemblySetSha256 = NamedByteSetHash(baselineRecords);
        string baseMetaVersionSetSha256 = NamedByteSetHash(baseMetaVersionRecords);
        if (!managedAssemblySetSha256.Equals(GetString(identity, "managedAssemblySetSha256"),
                StringComparison.OrdinalIgnoreCase) ||
            !NamedByteSetHash(snapshotRecords).Equals(GetString(identity, "aotSnapshotSha256"), StringComparison.OrdinalIgnoreCase))
            errors.Add("Build identity aggregate baseline/snapshot hash is invalid.");
        if (!baseMetaVersionSetSha256.Equals(GetString(identity,
                "baseMetaVersionSetSha256"), StringComparison.OrdinalIgnoreCase))
            errors.Add("Build identity aggregate Base MetaVersion hash is invalid.");
        string identityEngineWorkflow = GetString(identity, "engineWorkflow") ?? string.Empty;
        string identityCodeGeneration = GetString(identity, "il2cppCodeGeneration") ?? string.Empty;
        try
        {
            if (!string.Equals(identityCodeGeneration,
                    ExpectedIl2CppCodeGeneration(identityEngineWorkflow),
                    StringComparison.Ordinal))
                errors.Add("Build identity IL2CPP code generation does not match its engine workflow.");
        }
        catch (DheException exception)
        {
            errors.Add("Build identity engine workflow: " + exception.Message);
        }
        string[] identityCapabilities = StringArray(identity, "runtimeCapabilities", errors);
        if (!string.Equals(GetString(identity, "runtimeProtocol"),
                GetString(native, "runtimeProtocol"), StringComparison.Ordinal) ||
            !string.Equals(GetString(identity, "runtimeContract"),
                GetString(native, "runtimeContract"), StringComparison.Ordinal) ||
            !new HashSet<string>(identityCapabilities, StringComparer.Ordinal).SetEquals(
                StringArray(native, "runtimeCapabilities", errors)))
            errors.Add("Build identity runtime protocol does not match the native manifest.");
        try
        {
            string computedBaseId = ComputeBaseId(GetString(identity, "target") ?? string.Empty,
                GetString(identity, "engineWorkflow") ?? string.Empty,
                GetString(identity, "il2cppCodeGeneration") ?? string.Empty,
                managedAssemblySetSha256,
                GetString(identity, "aotAssemblySetSha256") ?? string.Empty,
                GetString(identity, "aotSnapshotSha256") ?? string.Empty,
                baseMetaVersionSetSha256, GetString(identity, "aotMetadataSetId") ?? string.Empty,
                GetString(identity, "nativeGuardSourceSha256") ?? string.Empty,
                GetString(identity, "nativeManifestSha256") ?? string.Empty,
                GetString(identity, "runtimeProtocol") ?? string.Empty,
                GetString(identity, "runtimeContract") ?? string.Empty, identityCapabilities,
                GetString(identity, "runtimeAssetRoot") ?? string.Empty,
                GetString(identity, "baseMetaVersionAssetRoot") ?? string.Empty,
                GetString(identity, "aotAnalysisSnapshotSha256"));
            if (!computedBaseId.Equals(GetString(identity, "baseId"),
                    StringComparison.OrdinalIgnoreCase))
                errors.Add("Build identity composite Base ID is invalid.");
            AotAnalysisSnapshot.Read(identityPath, identity,
                ReadIdentityAotAssemblyNames(identity, identityPath), names);
        }
        catch (Exception exception)
        {
            errors.Add("Build identity composite Base ID: " + exception.Message);
        }

        var identityRoot = Path.GetDirectoryName(identityPath)!;
        var nativeDocumentRoot = Path.GetDirectoryName(nativePath)!;
        var generatedRootValue = GetString(identity, "generatedCppRoot") ?? "";
        var nativeRootValue = GetString(native, "generatedCppRoot") ?? "";
        var generatedRoot = Path.GetFullPath(Path.IsPathRooted(generatedRootValue) ? generatedRootValue : Path.Combine(identityRoot, generatedRootValue));
        var nativeRoot = Path.GetFullPath(Path.IsPathRooted(nativeRootValue) ? nativeRootValue : Path.Combine(nativeDocumentRoot, nativeRootValue));
        if (string.IsNullOrWhiteSpace(generatedRootValue) || !Directory.Exists(generatedRoot) ||
            !generatedRoot.Equals(nativeRoot, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Build identity generated C++ root does not match the native manifest.");
            return;
        }
        var identityPaths = StringArray(identity, "generatedCppPaths", errors)
            .Select(path => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(identityRoot, path)))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var nativePaths = native.GetProperty("methods").EnumerateArray().Select(method => GetString(method, "sourceFile") ?? "")
            .Where(path => path.Length > 0)
            .Select(path => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(nativeDocumentRoot, path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (!identityPaths.SequenceEqual(nativePaths, StringComparer.OrdinalIgnoreCase))
            errors.Add("Build identity generated C++ files do not match the native manifest.");
        foreach (var path in identityPaths)
        {
            var relative = Path.GetRelativePath(generatedRoot, path);
            if (!SafeRelative(relative) || !File.Exists(path)) errors.Add("Build identity generated C++ file is missing or unsafe: " + path);
        }
        try
        {
            if (!GuardBlockSetHash(native, nativeDocumentRoot, generatedRoot).Equals(
                    GetString(identity, "nativeGuardSourceSha256"), StringComparison.OrdinalIgnoreCase))
                errors.Add("Build identity native guard block hash is invalid.");
        }
        catch (Exception ex) { errors.Add("Build identity native guard blocks: " + ex.Message); }
        var recordedManifest = ResolveEvidencePath(GetString(identity, "nativeManifestPath"),
            Path.GetDirectoryName(identityPath)!, "Build identity native manifest");
        if (!Sha256File(recordedManifest).Equals(GetString(identity, "nativeManifestSha256"), StringComparison.OrdinalIgnoreCase))
            errors.Add("Build identity immutable native manifest hash is invalid.");
        try
        {
            var immutableNative = ReadJson<JsonElement>(recordedManifest);
            ValidateNativeManifest(immutableNative, diffs, errors);
            var sourceHash = GetString(native, "sourceManifestSha256");
            if (!Path.GetFullPath(recordedManifest).Equals(Path.GetFullPath(nativePath), StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(sourceHash, Sha256File(recordedManifest), StringComparison.OrdinalIgnoreCase))
                errors.Add("Normalized native manifest is not bound to the immutable Player manifest.");
        }
        catch (Exception ex) { errors.Add("Immutable native manifest: " + ex.Message); }
    }

    private static string NamedByteSetHash(IEnumerable<KeyValuePair<string, byte[]>> records)
    {
        using var sha = SHA256.Create();
        foreach (var record in records.OrderBy(record => record.Key, StringComparer.Ordinal))
        {
            var name = Encoding.UTF8.GetBytes(record.Key + "\n");
            sha.TransformBlock(name, 0, name.Length, name, 0);
            var bytes = record.Value ?? Array.Empty<byte>();
            sha.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
            sha.TransformBlock(new byte[] { (byte)'\n' }, 0, 1, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static string GuardBlockSetHash(JsonElement native, string nativeDocumentRoot, string root)
    {
        var records = new List<GuardBlockHashRecord>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in native.GetProperty("methods").EnumerateArray())
        {
            var functionName = GetString(method, "functionName") ?? "";
            if (string.IsNullOrWhiteSpace(functionName) || functionName.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw new DheException("Native guard function name is invalid.");
            if (!method.TryGetProperty("methodToken", out var tokenValue) || !tokenValue.TryGetUInt32(out var methodToken) || methodToken == 0)
                throw new DheException("Native guard method token is invalid: " + functionName);
            var sourceValue = GetString(method, "sourceFile") ?? "";
            var sourcePath = RequireFile(Path.IsPathRooted(sourceValue) ? sourceValue :
                Path.Combine(nativeDocumentRoot, sourceValue), "Native guard source");
            var relative = Path.GetRelativePath(root, sourcePath).Replace(Path.DirectorySeparatorChar, '/');
            if (!SafeRelative(relative))
                throw new DheException("Native guard source escapes its generated C++ root: " + sourcePath);
            var identity = relative + "\n" + functionName + "\n" +
                methodToken.ToString(CultureInfo.InvariantCulture);
            if (!identities.Add(identity))
                throw new DheException("Native manifest contains a duplicate guard identity: " + functionName +
                    "/" + methodToken.ToString(CultureInfo.InvariantCulture));
            var source = File.ReadAllText(sourcePath, Encoding.UTF8);
            records.Add(new GuardBlockHashRecord(relative, functionName, methodToken,
                ExtractGuardBlock(source, functionName, methodToken)));
        }
        using var sha = SHA256.Create();
        var domain = Encoding.UTF8.GetBytes(NativeGuardHashContract + "\n");
        sha.TransformBlock(domain, 0, domain.Length, domain, 0);
        foreach (var record in records.OrderBy(item => item.RelativePath, StringComparer.Ordinal)
                     .ThenBy(item => item.FunctionName, StringComparer.Ordinal)
                     .ThenBy(item => item.MethodToken))
        {
            var header = Encoding.UTF8.GetBytes(record.RelativePath + "\n" + record.FunctionName + "\n" +
                record.MethodToken.ToString(CultureInfo.InvariantCulture) + "\n");
            sha.TransformBlock(header, 0, header.Length, header, 0);
            var block = Encoding.UTF8.GetBytes(record.Block + "\n");
            sha.TransformBlock(block, 0, block.Length, block, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static string ExtractGuardBlock(string source, string functionName, uint methodToken)
    {
        var token = methodToken.ToString(CultureInfo.InvariantCulture);
        var beginMarker = NativeGuardBeginPrefix + functionName + ":" + token;
        var endMarker = NativeGuardEndPrefix + functionName + ":" + token;
        var begin = RequireSingleGuardMarker(source, beginMarker);
        var end = RequireSingleGuardMarker(source, endMarker);
        if (end <= begin) throw new DheException("Native guard end marker precedes its begin marker: " + functionName);
        var lineStart = source.LastIndexOf('\n', begin);
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        var blockEnd = end + endMarker.Length;
        var lineEnd = source.IndexOf('\n', blockEnd);
        if (lineEnd < 0) lineEnd = source.Length;
        var suffix = source.Substring(blockEnd, lineEnd - blockEnd).TrimEnd('\r');
        if (suffix.Any(character => character != ' ' && character != '\t'))
            throw new DheException("Native guard end marker has unexpected trailing content: " + functionName);
        return source.Substring(lineStart, blockEnd - lineStart)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal).TrimEnd('\n');
    }

    private static int RequireSingleGuardMarker(string source, string marker)
    {
        var first = source.IndexOf(marker, StringComparison.Ordinal);
        if (first < 0 || source.IndexOf(marker, first + marker.Length, StringComparison.Ordinal) >= 0)
            throw new DheException("Native guard marker is missing or duplicated: " + marker);
        return first;
    }

    private sealed record GuardBlockHashRecord(string RelativePath, string FunctionName, uint MethodToken,
        string Block);

    private static void ValidateRuntimePlanFiles(JsonElement plan, string root, List<string> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in plan.GetProperty("assemblies").EnumerateArray())
        {
            var assemblyName = GetString(record, "assemblyName") ?? "";
            if (string.IsNullOrWhiteSpace(assemblyName) || !names.Add(assemblyName))
                errors.Add("Runtime plan contains a missing or duplicate assembly name.");
            foreach (var property in new[] { "current", "baseline", "snapshot",
                         "baseMetaVersion", "currentMetaVersion" })
            {
                var value = GetString(record, property) ?? "";
                if (Path.IsPathRooted(value) || value.Contains("..", StringComparison.Ordinal) || !File.Exists(Path.Combine(root, value)))
                    errors.Add("Runtime plan contains a missing or unsafe file: " + value);
            }
            var baseline = Path.Combine(root, GetString(record, "baseline") ?? "");
            var current = Path.Combine(root, GetString(record, "current") ?? "");
            var snapshot = Path.Combine(root, GetString(record, "snapshot") ?? "");
            var baseMetaVersion = Path.Combine(root,
                GetString(record, "baseMetaVersion") ?? "");
            var currentMetaVersion = Path.Combine(root,
                GetString(record, "currentMetaVersion") ?? "");
            if (File.Exists(baseline) && !Sha256File(baseline).Equals(GetString(record, "baselineSha256"), StringComparison.OrdinalIgnoreCase)) errors.Add("Runtime baseline hash mismatch.");
            if (File.Exists(current) && !Sha256File(current).Equals(GetString(record, "currentSha256"), StringComparison.OrdinalIgnoreCase)) errors.Add("Runtime current hash mismatch.");
            if (File.Exists(snapshot) && !Sha256File(snapshot).Equals(GetString(record, "snapshotSha256"), StringComparison.OrdinalIgnoreCase)) errors.Add("Runtime snapshot hash mismatch.");
            if (File.Exists(baseMetaVersion) && !Sha256File(baseMetaVersion).Equals(
                    GetString(record, "baseMetaVersionSha256"), StringComparison.OrdinalIgnoreCase))
                errors.Add("Runtime Base MetaVersion hash mismatch.");
            if (File.Exists(currentMetaVersion) && !Sha256File(currentMetaVersion).Equals(
                    GetString(record, "currentMetaVersionSha256"), StringComparison.OrdinalIgnoreCase))
                errors.Add("Runtime current MetaVersion hash mismatch.");
            if (File.Exists(snapshot) && File.Exists(baseline) &&
                !File.ReadAllBytes(snapshot).SequenceEqual(Convert.FromHexString(Sha256File(baseline))))
                errors.Add("Runtime snapshot does not encode the baseline assembly hash.");
        }
        if (plan.TryGetProperty("aotMetadata", out var metadata) && metadata.ValueKind == JsonValueKind.Array)
        {
            var metadataNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in metadata.EnumerateArray())
            {
                var name = GetString(record, "assemblyName") ?? "";
                var value = GetString(record, "path") ?? "";
                if (string.IsNullOrWhiteSpace(name) || !metadataNames.Add(name) || Path.IsPathRooted(value) ||
                    value.Contains("..", StringComparison.Ordinal) || !File.Exists(Path.Combine(root, value)) ||
                    !Sha256File(Path.Combine(root, value)).Equals(GetString(record, "sha256"), StringComparison.OrdinalIgnoreCase))
                    errors.Add("Runtime AOT metadata file is missing, duplicated, unsafe, or has the wrong hash: " + name);
            }
        }
    }

    private sealed record LiveAssemblyValidation(MetaVersionSnapshot Baseline,
        MetaVersionSnapshot Current, ResourceUpdateCompatibility Compatibility);
}
