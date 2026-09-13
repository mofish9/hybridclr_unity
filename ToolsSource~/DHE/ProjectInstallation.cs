using System.Text.Json;

namespace HybridCLR.DheTool;

internal static partial class Program
{
    private const string InstallationContract = "package-installed-runtime-v1";
    private static readonly string[] RuntimeGeneratedPaths =
    {
        "hybridclr/generated/AssemblyManifest.cpp", "hybridclr/generated/MethodBridge.cpp",
        "hybridclr/generated/UnityVersion.h", "hybridclr/generated/libil2cpp-version.txt"
    };
    private static readonly string[] PackageGeneratedPaths =
    {
        "Editor/BuildProcessors/AddLil2cppSourceCodeToXcodeproj2023OrNewer.cs.meta"
    };

    private static string InstalledRuntimePath(string project) => RuntimeSourceBinding.InstalledRoot(project,
        OperatingSystem.IsWindows() ? "WindowsEditor" : OperatingSystem.IsMacOS() ? "OSXEditor" : "LinuxEditor");

    private static bool IsProjectInstallation(JsonElement runtime) =>
        GetString(runtime, "installationContract") == InstallationContract;

    private static string PackageRelativePath(string project, string path)
    {
        string relative = Path.GetRelativePath(project, Path.GetFullPath(path)).Replace('\\', '/');
        if (!IsPortableRelativePath(relative)) throw new DheException("Package must be installed inside the Unity project.");
        return relative;
    }

    private static JsonElement ReadRuntimeRelease(string package)
    {
        string file = RequireFile(Path.Combine(package, "Data~/dhe-runtime-release.json"), "Package runtime release");
        JsonElement release = ReadJson<JsonElement>(file);
        if (GetInt(release, "schemaVersion") != 1 || GetString(release, "format") != "hybridclr.dhe-runtime-release.json" ||
            GetString(release, "engineWorkflow") != "Unity2022Fgs" ||
            GetString(release, "runtimeContract") != ResourceUpdateCompatibility.CurrentNativeRuntimeContract ||
            !IsHex(GetString(release, "nativeSourceCanonicalSha256"), 64, 64) || GetInt(release, "nativeSourceFileCount") <= 0)
            throw new DheException("Package has no approved Unity 2022 runtime release.");
        JsonElement versions = ReadJson<JsonElement>(Path.Combine(package, "Data~/hybridclr_version.json"));
        JsonElement selected = versions.GetProperty("versions").EnumerateArray().Single(row => GetString(row, "unity_version") == "2022");
        foreach (string repo in new[] { "hybridclr", "il2cpp_plus" })
        {
            JsonElement locked = release.GetProperty("repositories").GetProperty(repo);
            if (!IsHex(GetString(locked, "commit"), 40, 40) ||
                GetString(selected.GetProperty(repo), "branch") != GetString(locked, "tag"))
                throw new DheException("Installer ref differs from the package runtime release: " + repo);
        }
        return release;
    }

    private static RuntimeSourceBinding.Result RequireApprovedInstalledRuntime(string project, JsonElement release)
    {
        string installed = InstalledRuntimePath(project);
        var identity = RuntimeSourceBinding.Validate(installed, installed);
        string canonical = LabCommands.CanonicalSourceTreeHash(installed, false, RuntimeGeneratedPaths);
        if (!identity.Passed || identity.SourceFileCount != GetInt(release, "nativeSourceFileCount") ||
            !canonical.Equals(GetString(release, "nativeSourceCanonicalSha256"), StringComparison.OrdinalIgnoreCase))
            throw new DheException("Installed native sources do not match the package's approved runtime release. Run HybridCLR Installer.");
        foreach (string generated in RuntimeGeneratedPaths.Take(3))
            RequireFile(Path.Combine(installed, generated), "Installed generator input");
        return identity;
    }

    private static int CaptureProjectInstallation(Cli cli)
    {
        string project = Path.GetFullPath(RequireDirectory(cli.Require("projectpath"), "Unity project"));
        string package = Path.GetFullPath(RequireDirectory(cli.Require("packageroot"), "Unity package"));
        string packagePath = PackageRelativePath(project, package);
        string editorContents = Path.GetFullPath(RequireDirectory(cli.Require("editorcontents"), "Editor contents"));
        string editorVersion = cli.Require("editorversion");
        string projectVersion = File.ReadLines(Path.Combine(project, "ProjectSettings/ProjectVersion.txt"))
            .Select(line => line.Trim()).Single(line => line.StartsWith("m_EditorVersion:", StringComparison.Ordinal)).Split(':', 2)[1].Trim();
        if (!editorVersion.StartsWith("2022.", StringComparison.Ordinal) || editorVersion != projectVersion)
            throw new DheException("Project and running Editor must use the same supported Unity 2022 version.");
        JsonElement release = ReadRuntimeRelease(package);
        var source = RequireApprovedInstalledRuntime(project, release);
        string originalCompiler = RequireFile(Path.Combine(editorContents, "il2cpp/build/deploy/Unity.IL2CPP.dll"), "Editor compiler");
        string originalDataModel = RequireFile(Path.Combine(Path.GetDirectoryName(originalCompiler)!, "Unity.IL2CPP.DataModel.dll"), "Editor data model");
        string installedCompiler = RequireFile(Path.Combine(Path.GetDirectoryName(source.InstalledRoot)!, "build/deploy/Unity.IL2CPP.dll"), "Installed compiler");
        string installedDataModel = RequireFile(Path.Combine(Path.GetDirectoryName(installedCompiler)!, "Unity.IL2CPP.DataModel.dll"), "Installed data model");
        if (Sha256File(originalCompiler) != Sha256File(installedCompiler) || Sha256File(originalDataModel) != Sha256File(installedDataModel))
            throw new DheException("Installed compiler is not the running Editor's original compiler. Complete recovery or reinstall.");
        string external = RequireDirectory(Path.Combine(editorContents, "il2cpp/external"), "Actual Editor external headers");
        string toolRoot = Path.Combine(package, "Tools~/DHE");
        var tool = InspectPackage(toolRoot, null, false);
        if (!tool.Passed) throw new DheException("Installed package tool is incomplete: " + string.Join("; ", tool.Errors));
        JsonElement provenance = ReadJson<JsonElement>(Path.Combine(toolRoot, "build-provenance.json"));
        if (GetString(provenance, "sourceRepository") != "https://github.com/mofish9/hybridclr_unity.git" ||
            !IsHex(GetString(provenance, "sourceCommit"), 40, 40))
            throw new DheException("Tool must be built from the package-owned source.");
        string buildSourceCommit = GetString(provenance, "sourceCommit")!;
        string packageTree = LabCommands.CanonicalSourceTreeHash(package, true, PackageGeneratedPaths);
        string releasePath = Path.Combine(package, "Data~/dhe-runtime-release.json");
        string publishedLock = RequireFile(Path.Combine(toolRoot, "manifests/dhe-runtime-lock.json"), "Bundled runtime release");
        if (Sha256File(publishedLock) != Sha256File(releasePath)) throw new DheException("Package/runtime tool release locks differ.");
        string SettingsUrl(string name)
        {
            string key = name == "hybridclr" ? "hybridclrRepoURL" : "il2cppPlusRepoURL";
            string settings = File.ReadAllText(Path.Combine(project, "ProjectSettings/HybridCLRSettings.asset"));
            var match = System.Text.RegularExpressions.Regex.Match(settings, @"(?m)^\s*" + key + @":\s*(.+)$");
            return match.Success ? match.Groups[1].Value.Trim() : "project-default-repository";
        }
        object Repo(string name) => new
        {
            url = SettingsUrl(name), path = source.InstalledRoot,
            commit = GetString(release.GetProperty("repositories").GetProperty(name), "commit"), dirty = false,
            treeSha256 = GetString(release, "nativeSourceCanonicalSha256"), treeKind = "combined-installed-runtime"
        };
        string output = Path.Combine(project, "HybridCLRData/DHE");
        ProtectUnityToolOutput(output);
        Directory.CreateDirectory(output);
        var versionParts = System.Text.RegularExpressions.Regex.Match(editorVersion, @"^(\d+)\.(\d+)\.(\d+)");
        int versionNumber = int.Parse(versionParts.Groups[1].Value) * 10000 + int.Parse(versionParts.Groups[2].Value) * 100 + int.Parse(versionParts.Groups[3].Value);
        var manifest = new
        {
            schemaVersion = 1, format = "hybridclr.dhe-runtime-manifest.json", profile = "DHE-Unity2022",
            installationContract = InstallationContract, dheEnabled = true, pathSemantics = "workspace-absolute-v1",
            createdAtUtc = DateTimeOffset.UtcNow, engineWorkflow = "Unity2022Fgs", fullGenericSharingDiagnostics = false,
            engine = new { family = "Unity", version = editorVersion, unityVersion = editorVersion,
                unityVersionNumber = versionNumber, tuanjieVersionNumber = 0, executablePath = cli.Optional("editorexecutable") },
            externalHeaders = new { sourcePath = external, stagedPath = external, stagedTreeSha256 = TreeHashForRelease(external, Array.Empty<string>()),
                surrogate = false, editorAvailable = true, explicitlyAllowed = false },
            source = new { hybridclr = Repo("hybridclr"), il2cpp_plus = Repo("il2cpp_plus"),
                hybridclr_unity = new { url = "https://github.com/mofish9/hybridclr_unity.git", path = package,
                    commit = buildSourceCommit, dirty = false, treeSha256 = packageTree, treeHashIgnoredPaths = PackageGeneratedPaths,
                    commitKind = "package-build-source" } },
            stagedLibil2cpp = source.InstalledRoot, stagedRuntimeSha256 = TreeHashForRelease(source.InstalledRoot, Array.Empty<string>()),
            dheRuntimeLock = releasePath, dheRuntimeLockSha256 = Sha256File(releasePath), dheRuntimeSourceMode = "integrated", dhePatches = Array.Empty<object>(),
            installation = new { packagePath, packageTreeSha256 = packageTree, packageBuildSourceCommit = buildSourceCommit,
                nativeSourceSha256 = source.SourceSha256, nativeSourceFileCount = source.SourceFileCount,
                nativeCanonicalSha256 = GetString(release, "nativeSourceCanonicalSha256"), editorContents,
                compilerSha256 = Sha256File(originalCompiler), dataModelSha256 = Sha256File(originalDataModel),
                toolPackageId = GetString(ReadJson<JsonElement>(Path.Combine(toolRoot, "dhe-toolchain-manifest.json")), "packageId") }
        };
        WriteJson(Path.Combine(output, "package-lock.json"), new
        {
            schemaVersion = 1, format = "hybridclr.dhe-package-lock.json", sourceMode = "integrated",
            repository = "hybridclr_unity", baseCommit = "623073baafd5a1d12ea46df8145de9fddc899fac",
            integratedCommit = buildSourceCommit, pathBase = "project-root-v1", packagePath, treeSha256 = packageTree,
            treeHashKind = "canonical-source-v1", commitKind = "package-build-source",
            resolverSourceSha256 = Sha256File(Path.Combine(package, "Editor/Commands/DheBuildPipeline.cs")),
            treeHashIgnoredPaths = PackageGeneratedPaths, patches = new[] { "package-owned-dhe" }
        });
        WriteJson(Path.Combine(output, "runtime-manifest.json"), manifest);
        Console.WriteLine("DHE installed runtime identity captured: " + Path.Combine(output, "runtime-manifest.json"));
        return 0;
    }

    private static void ValidateProjectInstallation(JsonElement runtime, string project)
    {
        if (!IsProjectInstallation(runtime)) throw new DheException("Missing package installation contract.");
        JsonElement install = runtime.GetProperty("installation");
        string packagePath = GetString(install, "packagePath") ?? "";
        if (!IsPortableRelativePath(packagePath)) throw new DheException("Unsafe installed package path.");
        string package = Path.Combine(project, packagePath);
        JsonElement release = ReadRuntimeRelease(package);
        var source = RequireApprovedInstalledRuntime(project, release);
        if (GetString(runtime, "format") != "hybridclr.dhe-runtime-manifest.json" || GetInt(runtime, "schemaVersion") != 1 ||
            GetString(runtime, "engineWorkflow") != "Unity2022Fgs" || !GetBool(runtime, "dheEnabled") ||
            GetString(runtime, "dheRuntimeSourceMode") != "integrated" ||
            Path.GetFullPath(GetString(runtime, "stagedLibil2cpp") ?? "") != Path.GetFullPath(source.InstalledRoot))
            throw new DheException("Installation manifest does not describe this project's runtime.");
        if (source.SourceSha256 != GetString(install, "nativeSourceSha256") || source.SourceFileCount != GetInt(install, "nativeSourceFileCount"))
            throw new DheException("Installed runtime source identity changed since Install.");
        if (!LabCommands.CanonicalSourceTreeHash(package, true, PackageGeneratedPaths).Equals(GetString(install, "packageTreeSha256"), StringComparison.OrdinalIgnoreCase))
            throw new DheException("Package changed since Install. Reinstall before building DHE.");
        string releaseHash = Sha256File(Path.Combine(package, "Data~/dhe-runtime-release.json"));
        if (releaseHash != GetString(runtime, "dheRuntimeLockSha256") ||
            releaseHash != Sha256File(Path.Combine(package, "Tools~/DHE/manifests/dhe-runtime-lock.json")))
            throw new DheException("Installed release lock is not the package release.");
        string bundle = Path.Combine(package, "Tools~/DHE");
        if (!InspectPackage(bundle, GetString(install, "toolPackageId"), false).Passed)
            throw new DheException("Installed tool identity differs from the installation receipt.");
        ValidateProjectRuntimeSourceRecords(runtime, release, bundle);
        string compilerRoot = Path.Combine(GetString(install, "editorContents") ?? "", "il2cpp/build/deploy");
        if (Sha256File(RequireFile(Path.Combine(compilerRoot, "Unity.IL2CPP.dll"), "Editor compiler")) != GetString(install, "compilerSha256") ||
            Sha256File(RequireFile(Path.Combine(compilerRoot, "Unity.IL2CPP.DataModel.dll"), "Editor data model")) != GetString(install, "dataModelSha256"))
            throw new DheException("Editor compiler changed since Install.");
        string projectVersion = File.ReadLines(Path.Combine(project, "ProjectSettings/ProjectVersion.txt"))
            .Select(line => line.Trim()).Single(line => line.StartsWith("m_EditorVersion:", StringComparison.Ordinal)).Split(':', 2)[1].Trim();
        if (projectVersion != GetString(runtime.GetProperty("engine"), "unityVersion"))
            throw new DheException("Project Editor version changed since Install.");
        JsonElement headers = runtime.GetProperty("externalHeaders");
        string actualHeaders = Path.Combine(GetString(install, "editorContents") ?? "", "il2cpp/external");
        if (GetBool(headers, "surrogate") || GetBool(headers, "explicitlyAllowed") || !GetBool(headers, "editorAvailable") ||
            Path.GetFullPath(GetString(headers, "stagedPath") ?? "") != Path.GetFullPath(actualHeaders) ||
            !TreeHashForRelease(actualHeaders, Array.Empty<string>()).Equals(GetString(headers, "stagedTreeSha256"), StringComparison.OrdinalIgnoreCase))
            throw new DheException("Editor external headers changed since Install.");
    }

    private static void ValidateProjectRuntimeSourceRecords(JsonElement runtime, JsonElement release, string toolRoot)
    {
        JsonElement install = runtime.GetProperty("installation");
        JsonElement sources = runtime.GetProperty("source");
        foreach (string name in new[] { "hybridclr", "il2cpp_plus" })
        {
            JsonElement record = sources.GetProperty(name);
            if (GetBool(record, "dirty") || GetString(record, "commit") != GetString(release.GetProperty("repositories").GetProperty(name), "commit") ||
                GetString(record, "treeSha256") != GetString(release, "nativeSourceCanonicalSha256"))
                throw new DheException("Installation runtime source record is not release-bound: " + name);
        }
        JsonElement package = sources.GetProperty("hybridclr_unity");
        JsonElement provenance = ReadJson<JsonElement>(Path.Combine(toolRoot, "build-provenance.json"));
        if (GetBool(package, "dirty") || GetString(package, "commitKind") != "package-build-source" ||
            GetString(package, "commit") != GetString(provenance, "sourceCommit") ||
            GetString(install, "packageBuildSourceCommit") != GetString(provenance, "sourceCommit") ||
            GetString(package, "treeSha256") != GetString(install, "packageTreeSha256") ||
            GetString(install, "nativeCanonicalSha256") != GetString(release, "nativeSourceCanonicalSha256"))
            throw new DheException("Installation package source provenance is not bound to its tool.");
    }

    private static int VerifyProjectInstallation(Cli cli)
    {
        string project = Path.GetFullPath(RequireDirectory(cli.Require("projectpath"), "Unity project"));
        string manifest = cli.Optional("runtimemanifestpath") ?? Path.Combine(project, "HybridCLRData/DHE/runtime-manifest.json");
        ValidateProjectInstallation(ReadJson<JsonElement>(RequireFile(manifest, "DHE installation receipt")), project);
        Console.WriteLine("DHE package and installed runtime identity verified.");
        return 0;
    }
}
