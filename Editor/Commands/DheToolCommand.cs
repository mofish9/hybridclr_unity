using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;

namespace HybridCLR.Editor.Commands
{
    /// <summary>Package-owned build-time DHE tool. Never included in a Player.</summary>
    public static class DheToolCommand
    {
        // Commands that launch another Editor must be run externally, after this
        // project closes. Do not allow a nested workflow to wait on its own lock.
        private static readonly HashSet<string> EditorCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "version", "mv", "metaversion", "batch", "base-registry", "resource-update",
            "stage-resource-update", "resource-release-plan", "resource-release-gate", "channel-state",
            "baseline-manifest", "aot-metadata-manifest", "preflight", "release-gate",
            "schema-validate", "schema-gate", "validate", "archive", "doctor", "verify-package",
            "new-adapter", "new-config", "tree-hash", "file-hash"
        };

        public static string ToolRoot
        {
            get
            {
                var package = PackageInfo.FindForAssembly(typeof(DheToolCommand).Assembly);
                string root = package != null ? package.resolvedPath :
                    Path.Combine(SettingsUtil.ProjectDir, SettingsUtil.PackagePathInProject);
                return Path.GetFullPath(Path.Combine(root, "Tools~", "DHE"));
            }
        }

        /// <summary>Runs a build operation, throws on timeout/nonzero exit, returns stdout.</summary>
        public static string Run(string command, params string[] arguments)
        {
            return RunWithTimeout(command, arguments, 300000);
        }

        public static string RunWithTimeout(string command, string[] arguments, int timeoutMilliseconds)
        {
            if (command == null || !EditorCommands.Contains(command))
                throw new BuildFailedException("Unsupported in-Editor DHE command. Full workflows must run outside Unity: " + command);
            if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
            string root = ToolRoot;
            VerifyBundle(root);
            string host = ResolveDotnetHost();
            var args = new List<string> { Path.Combine(root, "HybridCLR.DheTool.dll"), command };
            if (arguments != null) args.AddRange(arguments);
            var start = new ProcessStartInfo(host)
            {
                WorkingDirectory = SettingsUtil.ProjectDir,
                Arguments = string.Join(" ", args.Select(QuoteArgument)),
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = Process.Start(start))
            {
                if (process == null) throw new BuildFailedException("Cannot start the package DHE tool.");
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMilliseconds))
                {
                    process.Kill();
                    process.WaitForExit(10000);
                    throw new BuildFailedException("DHE tool timed out: " + command);
                }
                Task.WaitAll(stdout, stderr);
                if (process.ExitCode != 0)
                    throw new BuildFailedException("DHE tool failed (" + process.ExitCode + "): " + command + "\n" + stderr.Result + stdout.Result);
                if (!string.IsNullOrWhiteSpace(stderr.Result)) UnityEngine.Debug.LogWarning(stderr.Result);
                return stdout.Result;
            }
        }

        /// <summary>Unity batch entry: -dheToolRequest absolute/path/request.json.</summary>
        public static void RunBatch()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.FindIndex(args, a => string.Equals(a, "-dheToolRequest", StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= args.Length) throw new BuildFailedException("Missing -dheToolRequest JSON path.");
            var request = JsonUtility.FromJson<ToolRequest>(File.ReadAllText(args[index + 1]));
            if (request == null) throw new BuildFailedException("Invalid DHE tool request.");
            UnityEngine.Debug.Log(Run(request.command, request.arguments));
        }

        [MenuItem("HybridCLR/DHE/Verify Bundled Tool")]
        public static void VerifyInstallation()
        {
            UnityEngine.Debug.Log(Run("verify-package"));
        }

        public static string ResolveDotnetHost()
        {
            // applicationContentsPath is Editor/Data on Windows/Linux and
            // Unity.app/Contents on macOS. Do not depend on an SDK or PATH.
            string executable = Application.platform == RuntimePlatform.WindowsEditor ? "dotnet.exe" : "dotnet";
            string host = Path.Combine(EditorApplication.applicationContentsPath, "NetCoreRuntime", executable);
            if (File.Exists(host)) return host;
            throw new BuildFailedException("Unity's bundled .NET runtime was not found: " + host +
                ". Use a Unity 2022 installation containing NetCoreRuntime, or run the DLL externally with a .NET 6 runtime.");
        }

        private static string QuoteArgument(string value)
        {
            if (value == null || value.IndexOf('\0') >= 0) throw new ArgumentException("Invalid process argument.");
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                result.Append(c);
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            return result.Append('"').ToString();
        }

        // Validate before executing either DLL. The enclosing package commit is
        // the trust anchor; this inventory detects corruption/partial migration.
        private static void VerifyBundle(string root)
        {
            if (!Directory.Exists(root)) throw new BuildFailedException("Missing package DHE tool: " + root);
            RejectLinks(root);
            var manifest = JsonUtility.FromJson<BundleManifest>(File.ReadAllText(Path.Combine(root, "dhe-toolchain-manifest.json")));
            if (manifest == null || manifest.format != "hybridclr.dhe-toolchain-manifest.json" ||
                manifest.entryPoint != "HybridCLR.DheTool.dll" || manifest.files == null ||
                manifest.fileCount != manifest.files.Length || manifest.fileCount < 4)
                throw new BuildFailedException("Invalid DHE tool inventory.");
            var declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in manifest.files)
            {
                if (file == null || string.IsNullOrEmpty(file.path) || file.path.Contains('\\') ||
                    file.path.Contains(':') || file.path.Split('/').Any(p => p == "" || p == "." || p == "..") ||
                    !declared.Add(file.path)) throw new BuildFailedException("Unsafe DHE tool inventory path.");
                string full = Path.Combine(root, file.path);
                if (!File.Exists(full) || new FileInfo(full).Length != file.size)
                    throw new BuildFailedException("Missing or damaged DHE tool file: " + file.path);
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(full))
                {
                    string hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                    if (!string.Equals(hash, file.sha256, StringComparison.OrdinalIgnoreCase))
                        throw new BuildFailedException("DHE tool hash mismatch: " + file.path);
                }
            }
            string[] required = { "HybridCLR.DheTool.dll", "dnlib.dll", "HybridCLR.DheTool.deps.json", "HybridCLR.DheTool.runtimeconfig.json" };
            if (required.Any(p => !declared.Contains(p))) throw new BuildFailedException("Incomplete DHE tool inventory.");
            string[] actual = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(p => p.Substring(root.TrimEnd('/', '\\').Length + 1).Replace('\\', '/'))
                .Where(p => p != "dhe-toolchain-manifest.json").ToArray();
            if (!declared.SetEquals(actual)) throw new BuildFailedException("Unexpected files in DHE tool bundle.");
        }

        private static void RejectLinks(string directory)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new BuildFailedException("DHE tool bundle cannot contain symbolic links.");
            foreach (string path in Directory.GetFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new BuildFailedException("DHE tool bundle cannot contain symbolic links.");
                if ((attributes & FileAttributes.Directory) != 0) RejectLinks(path);
            }
        }

        [Serializable] private sealed class ToolRequest { public string command; public string[] arguments; }
        [Serializable] private sealed class BundleManifest
        {
            public string format; public string entryPoint; public int fileCount; public BundleFile[] files;
        }
        [Serializable] private sealed class BundleFile { public string path; public long size; public string sha256; }
    }
}
