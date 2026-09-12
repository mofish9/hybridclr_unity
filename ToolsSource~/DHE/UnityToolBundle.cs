using System.Diagnostics;
using System.Text.Json;

namespace HybridCLR.DheTool;

internal static partial class Program
{
    private static readonly string[] UnityToolCommands =
    {
        "version", "mv", "metaversion", "batch", "base-registry",
        "resource-release-build", "resource-release-plan", "resource-release-qualify",
        "resource-update", "stage-resource-update", "android-device-smoke",
        "resource-player-evidence", "resource-release-gate", "channel-state",
        "baseline-manifest", "aot-metadata-manifest", "preflight", "workflow",
        "release-gate", "schema-validate", "schema-gate", "validate", "archive",
        "doctor", "verify-package", "new-adapter", "new-config", "tree-hash", "file-hash"
        , "capture-installation", "verify-installation"
    };

    // Compiled into the project distribution. Research commands remain available
    // only in the Lab host; removing the marker file cannot re-enable them.
    private static void PrepareUnityTool(Cli cli)
    {
#if DHE_PACKAGE_TOOL
        if (cli.Command is "help" or "")
        {
            Console.WriteLine("HybridCLR package DHE tool (.NET 6). Commands: " + string.Join(", ", UnityToolCommands));
            return;
        }
        if (!UnityToolCommands.Contains(cli.Command, StringComparer.OrdinalIgnoreCase))
            throw new DheException("This command belongs to the Lab, not the Unity package: " + cli.Command);
        string root = Path.GetFullPath(AppContext.BaseDirectory);
        var inspection = InspectPackage(root, null, false);
        if (!inspection.Passed)
            throw new DheException("Bundled tool integrity check failed: " + string.Join("; ", inspection.Errors));
        // CLI values win over configuration; defaults are applied only after
        // configuration has been read. ProductionWorkflow may read it again,
        // but its merge is idempotent and preserves the resolved values.
        if (cli.Command.Equals("workflow", StringComparison.OrdinalIgnoreCase)) ApplyWorkflowConfig(cli);
        if (!cli.Has("root")) cli.Values["root"] = root;
        if (!cli.Has("toolchainroot")) cli.Values["toolchainroot"] = root;
        ProtectUnityToolOutputOptions(cli);
#endif
    }

    private static int PublishUnityTool(Cli cli)
    {
        string root = Path.GetFullPath(RequireDirectory(cli.Require("packagesourceroot"), "Package source"));
        string toolSource = Path.Combine(root, "ToolsSource~", "DHE");
        string projectFile = RequireFile(Path.Combine(toolSource, "HybridCLR.DheTool.csproj"), "Package tool source");
        string head = GitValue(root, "rev-parse", "HEAD");
        string tree = GitValue(root, "rev-parse", "HEAD^{tree}");
        if (!IsHex(head, 40, 64) || !IsHex(tree, 40, 64) ||
            !string.IsNullOrWhiteSpace(GitValue(root, "status", "--porcelain")))
            throw new DheException("Unity tool publication requires a clean committed package tree.");
        string output = Path.GetFullPath(cli.Require("outputroot"));
        EnsureOutputNotAncestor(output, root);
        if (output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            Directory.Exists(output) || File.Exists(output))
            throw new DheException("OutputRoot must be a new directory outside the source tree.");
        ToolPublicationPolicy publication = ResolveToolPublicationPolicy(cli, toolSource, head, tree, true, true);
        // Never accept arbitrary prebuilt binaries with a claimed source commit.
        string temporary = Path.Combine(Path.GetTempPath(), "hybridclr-dhe-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            string[] buildArgs = { "publish", projectFile, "-c", "Release", "-o", temporary,
                "-p:DefineConstants=DHE_PACKAGE_TOOL", "-p:UseAppHost=false", "-p:DebugType=None",
                "-p:CheckEolTargetFramework=false", "--nologo" };
            foreach (string argument in buildArgs) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new DheException("Cannot start tool compiler.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(300000)) { process.Kill(true); throw new DheException("Tool compiler timed out."); }
            Task.WaitAll(stdout, stderr);
            if (process.ExitCode != 0) throw new DheException(stdout.Result + stderr.Result);
            if (GitValue(root, "rev-parse", "HEAD") != head ||
                !string.IsNullOrWhiteSpace(GitValue(root, "status", "--porcelain")))
                throw new DheException("Package source changed during compilation.");
            // Evidence is external input. Revalidate it after the build before
            // creating any distributable output or marking the manifest Release.
            if (ResolveToolPublicationPolicy(cli, toolSource, head, tree, true, true) != publication)
                throw new DheException("Toolchain release evidence changed during compilation.");

            Directory.CreateDirectory(output);
            string[] binaries = { "HybridCLR.DheTool.dll", "dnlib.dll", "HybridCLR.DheTool.deps.json", "HybridCLR.DheTool.runtimeconfig.json" };
            foreach (string file in binaries) CopyRelative(temporary, output, file);
            var sourceLayout = ReadJson<JsonElement>(Path.Combine(toolSource, "manifests/dhe-toolchain-layout.json"));
            foreach (string relative in sourceLayout.GetProperty("exactPaths").EnumerateArray().Select(e => e.GetString()!))
                if (relative.StartsWith("schemas/", StringComparison.Ordinal) || relative.StartsWith("templates/", StringComparison.Ordinal))
                    CopyRelative(toolSource, output, relative);
            CopyRelative(toolSource, output, "manifests/runtime-workflows.json");
            string runtimeRelease = Path.Combine(root, "Data~/dhe-runtime-release.json");
            File.Copy(runtimeRelease, Path.Combine(output, "manifests/dhe-runtime-lock.json"));
            CopyRelative(toolSource, output, "manifests/dhe-toolchain-evidence-authorities.json");
            CopyRelative(toolSource, output, "docs/THIRD-PARTY-NOTICES.md");
            CopyRelative(toolSource, output, "docs/DHE-Tool-Binary-Notices.md");
            WriteJson(Path.Combine(output, "build-provenance.json"), new
            {
                sourceRepository = "https://github.com/mofish9/hybridclr_unity.git", sourceCommit = head, sourceTree = tree,
                project = "ToolsSource~/DHE/HybridCLR.DheTool.csproj", projectSha256 = Sha256File(projectFile),
                dnlibSha256 = Sha256File(Path.Combine(temporary, "dnlib.dll")),
                arguments = buildArgs.Where(a => a != temporary).Select(a => a == projectFile ? "ToolsSource~/DHE/HybridCLR.DheTool.csproj" : a).ToArray(),
                targetFramework = "net6.0", profile = "DHE_PACKAGE_TOOL",
                publicationMode = publication.Mode, releaseEvidenceSha256 = publication.EvidenceSha256
            });
            string version = GetString(sourceLayout, "toolchainVersion")!;
            string layoutPath = Path.Combine(output, "manifests/dhe-toolchain-layout.json");
            WriteJson(layoutPath, new
            {
                schemaVersion = 1, format = "hybridclr.dhe-toolchain-layout.json", toolchainVersion = version, contractVersion = 1,
                exactPaths = binaries.Concat(new[] { "build-provenance.json", "manifests/dhe-toolchain-layout.json" }).ToArray(),
                prefixes = new[] { "schemas/", "templates/", "manifests/", "docs/" },
                generatedPaths = new[] { "dhe-source-boundary.json", "dhe-toolchain-manifest.json" }, commands = UnityToolCommands
            });
            WriteJson(Path.Combine(output, "dhe-source-boundary.json"), new
            {
                schemaVersion = 1, format = "hybridclr.dhe-source-boundary.json", pathBase = "manifest-directory-v1",
                exactPaths = binaries.Concat(new[] { "build-provenance.json", "dhe-source-boundary.json", "dhe-toolchain-manifest.json" }).ToArray(),
                prefixes = new[] { "schemas/", "templates/", "manifests/", "docs/" }, generatedPrefixes = Array.Empty<string>()
            });
            var files = Directory.GetFiles(output, "*", SearchOption.AllDirectories)
                .Select(p => new PackageFileEntry(Path.GetRelativePath(output, p).Replace('\\', '/'), new FileInfo(p).Length, Sha256File(p)))
                .OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
            string layoutHash = Sha256File(layoutPath);
            string packageId = CalculatePackageId(version, 1, publication.Mode, publication.ReleaseReady,
                head, tree, true, true, layoutHash, UnityToolCommands, files);
            WriteJson(Path.Combine(output, "dhe-toolchain-manifest.json"), new
            {
                schemaVersion = 1, format = "hybridclr.dhe-toolchain-manifest.json", generatedAtUtc = DateTimeOffset.UtcNow,
                toolchainVersion = version, contractVersion = 1, mode = publication.Mode, releaseReady = publication.ReleaseReady,
                pathSemantics = "package-relative-v1", packageIdAlgorithm = PackageIdAlgorithm, packageId,
                entryPoint = "HybridCLR.DheTool.dll", commands = UnityToolCommands, layoutSha256 = layoutHash,
                sourceIdentity = new { head, tree, clean = true, tracked = true }, fileCount = files.Length, files
            });
            var inspection = InspectPackage(output, packageId, publication.ReleaseReady);
            if (!inspection.Passed) throw new DheException(string.Join("; ", inspection.Errors));
            Console.WriteLine($"Unity DHE tool: {files.Length + 1} files, packageId={packageId}, output={output}");
            return 0;
        }
        finally { Directory.Delete(temporary, true); }
    }
}
