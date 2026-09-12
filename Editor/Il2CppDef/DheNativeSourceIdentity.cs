using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HybridCLR.Editor.Il2CppDef
{
    [Serializable]
    public sealed class DheNativeSourceIdentity
    {
        public string contract = "dhe-native-source-v1";
        public string sourceSha256;
        public int sourceFileCount;
        public DheGeneratedSourceIdentity[] generatedFiles;

        public static DheNativeSourceIdentity Capture(string root)
        {
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("DHE native source root not found: " + root);
            var generatedPaths = new HashSet<string>(StringComparer.Ordinal)
            {
                "hybridclr/generated/AssemblyManifest.cpp",
                "hybridclr/generated/MethodBridge.cpp",
                "hybridclr/generated/UnityVersion.h",
            };
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count != 0)
            {
                string directory = pending.Pop();
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("DHE native source cannot use a linked directory: " + directory);
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("DHE native source cannot use a linked entry: " + path);
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                    else
                    {
                        string relative = path.Substring(root.Length + 1).Replace('\\', '/');
                        // Installer's receipt is not executable native source.
                        if (relative == "hybridclr/generated/libil2cpp-version.txt") continue;
                        using (SHA256 hash = SHA256.Create())
                        using (FileStream stream = File.OpenRead(path))
                            files.Add(relative, Hex(hash.ComputeHash(stream)));
                    }
                }
            }
            foreach (string required in generatedPaths.Concat(new[]
            {
                "hybridclr/DheRuntime.cpp", "hybridclr/DheRuntime.h", "vm/Class.cpp",
            }))
                if (!files.ContainsKey(required)) throw new IOException("DHE native source is missing: " + required);
            var source = files.Where(file => !generatedPaths.Contains(file.Key)).ToArray();
            string canonical = string.Join("\n", source.Select(file => file.Key + "\t" + file.Value));
            using (SHA256 hash = SHA256.Create())
                return new DheNativeSourceIdentity
                {
                    sourceSha256 = Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(canonical))),
                    sourceFileCount = source.Length,
                    generatedFiles = files.Where(file => generatedPaths.Contains(file.Key))
                        .Select(file => new DheGeneratedSourceIdentity { path = file.Key, sha256 = file.Value }).ToArray(),
                };
        }

        public static void RequireUnchanged(DheNativeSourceIdentity expected, DheNativeSourceIdentity actual)
        {
            if (expected == null || actual == null || expected.contract != actual.contract ||
                expected.sourceSha256 != actual.sourceSha256 || expected.sourceFileCount != actual.sourceFileCount ||
                expected.generatedFiles == null || actual.generatedFiles == null ||
                !expected.generatedFiles.Select(file => file.path + "\t" + file.sha256).SequenceEqual(
                    actual.generatedFiles.Select(file => file.path + "\t" + file.sha256), StringComparer.Ordinal))
                throw new InvalidOperationException("DHE native sources changed during Player finalization.");
        }

        private static string Hex(byte[] value) => BitConverter.ToString(value).Replace("-", string.Empty);
    }

    [Serializable]
    public sealed class DheGeneratedSourceIdentity
    {
        public string path;
        public string sha256;
    }
}
