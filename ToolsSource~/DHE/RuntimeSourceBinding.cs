#nullable enable

using System.Security.Cryptography;
using System.Text;

namespace HybridCLR.DheTool;

internal static class RuntimeSourceBinding
{
    // These exact files are owned by the package generators, not runtime source.
    private static readonly HashSet<string> GeneratedFiles = new(StringComparer.Ordinal)
    {
        "hybridclr/generated/AssemblyManifest.cpp",
        "hybridclr/generated/MethodBridge.cpp",
        "hybridclr/generated/UnityVersion.h",
    };
    private const string InstallerReceipt = "hybridclr/generated/libil2cpp-version.txt";

    internal sealed record Result(string SourceRoot, string InstalledRoot,
        string SourceSha256, string InstalledSourceSha256, int SourceFileCount,
        IReadOnlyDictionary<string, string> GeneratedFileHashes, string[] Errors)
    {
        public bool Passed => Errors.Length == 0;
    }

    internal static string InstalledRoot(string project, string editorPlatform) =>
        editorPlatform is "WindowsEditor" or "OSXEditor" or "LinuxEditor"
            ? Path.Combine(Path.GetFullPath(project), "HybridCLRData",
                "LocalIl2CppData-" + editorPlatform, "il2cpp", "libil2cpp")
            : throw new ArgumentException("Unsupported Editor host platform: " + editorPlatform);

    internal static Result Validate(string sourceRoot, string installedRoot)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        installedRoot = Path.GetFullPath(installedRoot);
        var source = ReadTree(sourceRoot);
        var installed = ReadTree(installedRoot);
        var errors = new List<string>();
        foreach (string required in new[] { "hybridclr/DheRuntime.cpp", "hybridclr/DheRuntime.h", "vm/Class.cpp" })
            if (!source.ContainsKey(required)) errors.Add("Runtime source is missing: " + required);
        foreach (var file in source)
        {
            if (file.Key == InstallerReceipt) continue;
            if (!installed.TryGetValue(file.Key, out string? actual))
                errors.Add("Installed runtime is missing: " + file.Key);
            else if (!GeneratedFiles.Contains(file.Key) && file.Value != actual)
                errors.Add("Installed runtime source differs: " + file.Key);
        }
        foreach (string file in installed.Keys)
            if (file != InstallerReceipt && !source.ContainsKey(file))
                errors.Add("Installed runtime contains an unexpected file: " + file);
        var generated = installed.Where(file => GeneratedFiles.Contains(file.Key))
            .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
        return new Result(sourceRoot, installedRoot, SourceHash(source), SourceHash(installed),
            source.Keys.Count(IsSource), generated, errors.ToArray());
    }

    private static bool IsSource(string path) => !GeneratedFiles.Contains(path) && path != InstallerReceipt;

    private static string SourceHash(SortedDictionary<string, string> files)
    {
        string canonical = string.Join("\n", files.Where(file => IsSource(file.Key))
            .Select(file => file.Key + "\t" + file.Value));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static SortedDictionary<string, string> ReadTree(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("DHE runtime directory not found: " + root);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            string directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("DHE runtime source cannot use a linked directory: " + directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("DHE runtime source cannot use a linked entry: " + path);
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else
                {
                    using var stream = File.OpenRead(path);
                    using var hash = SHA256.Create();
                    files.Add(Path.GetRelativePath(root, path).Replace('\\', '/'),
                        Convert.ToHexString(hash.ComputeHash(stream)));
                }
            }
        }
        return files;
    }
}
