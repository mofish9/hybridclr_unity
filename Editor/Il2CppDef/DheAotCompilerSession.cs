using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace HybridCLR.Editor.Il2CppDef
{
    public sealed class DheAotCompilerSession : IDisposable
    {
        private const string JournalFormat = "hybridclr.dhe-compiler-transaction.json";
        private const string DivideOption = "--enable-divide-by-zero-check";
        private readonly string localCompiler;
        private readonly string localDataModel;
        private readonly string cacheRoot;
        private readonly string journalPath;
        private readonly Func<string> getArguments;
        private readonly Action<string> setArguments;
        private readonly FileStream processLock;
        private bool disposed;

        public DheAotCompilerIdentity Identity { get; private set; }

        public DheAotCompilerSession(string projectRoot, string originalCompiler,
            string installedCompiler, string engineVersion, Func<string> getArguments, Action<string> setArguments)
        {
            this.getArguments = getArguments ?? throw new ArgumentNullException(nameof(getArguments));
            this.setArguments = setArguments ?? throw new ArgumentNullException(nameof(setArguments));
            projectRoot = Path.GetFullPath(projectRoot);
            localCompiler = RequireChild(projectRoot, installedCompiler);
            originalCompiler = Path.GetFullPath(originalCompiler);
            if (SamePath(originalCompiler, localCompiler))
                throw new InvalidDataException("DHE must not patch the Editor installation.");
            cacheRoot = RequireChild(projectRoot, Path.Combine(projectRoot, "Library", "HybridCLR", "DHE", "Compiler"));
            Directory.CreateDirectory(cacheRoot);
            journalPath = Path.Combine(cacheRoot, "transaction.json");
            localDataModel = Path.Combine(Path.GetDirectoryName(localCompiler), "Unity.IL2CPP.DataModel.dll");
            RequireChild(projectRoot, localDataModel);
            string lockPath = Path.Combine(cacheRoot, "compiler.lock");
            CheckNoLinks(lockPath);
            processLock = new FileStream(lockPath, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            try
            {
                Recover();
                byte[] original = File.ReadAllBytes(originalCompiler);
                byte[] dataModel = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(originalCompiler), "Unity.IL2CPP.DataModel.dll"));
                if (HashFile(localCompiler) != DheAotCompilerPatch.Hash(original) ||
                    HashFile(localDataModel) != DheAotCompilerPatch.Hash(dataModel))
                    throw new InvalidDataException("Project compiler differs from the Editor; run HybridCLR Installer before building DHE.");
                DheAotCompilerPatchResult patch = DheAotCompilerPatch.Create(original, dataModel);
                string previousArguments = getArguments() ?? string.Empty;
                string effectiveArguments = WithDivideChecks(previousArguments);
                Identity = new DheAotCompilerIdentity
                {
                    contract = DheAotCompilerPatch.Contract, engineVersion = engineVersion,
                    originalCompilerSha256 = patch.OriginalSha256, compilerSha256 = patch.PatchedSha256,
                    dataModelSha256 = patch.DataModelSha256, additionalIl2CppArgs = effectiveArguments,
                    divideByZeroChecks = true,
                };
                string backup = Path.Combine(cacheRoot, patch.OriginalSha256 + ".original.dll");
                CheckNoLinks(backup);
                if (!File.Exists(backup)) WriteNew(backup, original);
                if (HashFile(backup) != patch.OriginalSha256)
                    throw new InvalidDataException("DHE compiler recovery backup hash mismatch: " + backup);
                var journal = new Journal
                {
                    format = JournalFormat, schemaVersion = 1, compilerPath = localCompiler,
                    originalSha256 = patch.OriginalSha256, patchedSha256 = patch.PatchedSha256,
                    originalArguments = previousArguments, effectiveArguments = effectiveArguments,
                };
                WriteNew(journalPath, Encoding.UTF8.GetBytes(JsonUtility.ToJson(journal, true)));
                try
                {
                    ReplaceBytes(localCompiler, patch.Bytes);
                    setArguments(effectiveArguments);
                    Validate();
                }
                catch
                {
                    Recover();
                    throw;
                }
            }
            catch
            {
                processLock.Dispose();
                throw;
            }
        }

        public void Validate()
        {
            if (disposed || Identity == null || HashFile(localCompiler) != Identity.compilerSha256 ||
                HashFile(localDataModel) != Identity.dataModelSha256 ||
                (getArguments() ?? string.Empty) != Identity.additionalIl2CppArgs)
                throw new InvalidDataException("DHE compiler or generation options changed during the build.");
        }

        public void RecordGeneration(string generatedCppRoot)
        {
            Validate();
            var generation = new Generation
            {
                format = "hybridclr.dhe-compiler-generation.json", schemaVersion = 1,
                generatedCppRoot = Path.GetFullPath(generatedCppRoot), compilerIdentity = Identity,
                sourceSha256 = HashSources(generatedCppRoot),
            };
            string path = Path.Combine(cacheRoot, "generation.json");
            byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(generation, true));
            if (File.Exists(path)) ReplaceBytes(path, bytes);
            else WriteNew(path, bytes);
        }

        public void RequireGeneration(string generatedCppRoot)
        {
            Validate();
            string path = Path.Combine(cacheRoot, "generation.json");
            if (!File.Exists(path)) throw new InvalidDataException("DHE compiler generation evidence is missing; rebuild the Base scripts.");
            CheckNoLinks(path);
            Generation generation = JsonUtility.FromJson<Generation>(File.ReadAllText(path));
            if (generation == null || generation.format != "hybridclr.dhe-compiler-generation.json" ||
                generation.schemaVersion != 1 || !SamePath(generation.generatedCppRoot, generatedCppRoot) ||
                JsonUtility.ToJson(generation.compilerIdentity) != JsonUtility.ToJson(Identity) ||
                generation.sourceSha256 != HashSources(generatedCppRoot))
                throw new InvalidDataException("DHE generated source or compiler identity no longer matches its successful build.");
        }

        public void Dispose()
        {
            if (disposed) return;
            try { Recover(); }
            finally { disposed = true; processLock.Dispose(); }
        }

        private void Recover()
        {
            if (!File.Exists(journalPath)) return;
            CheckNoLinks(journalPath);
            Journal journal = JsonUtility.FromJson<Journal>(File.ReadAllText(journalPath));
            if (journal == null || journal.format != JournalFormat || journal.schemaVersion != 1 ||
                !SamePath(journal.compilerPath, localCompiler) || !IsHash(journal.originalSha256) ||
                !IsHash(journal.patchedSha256) || journal.originalArguments == null ||
                journal.effectiveArguments != WithDivideChecks(journal.originalArguments))
                throw new InvalidDataException("DHE compiler recovery journal is invalid; it has been preserved.");
            string backup = Path.Combine(cacheRoot, journal.originalSha256 + ".original.dll");
            CheckNoLinks(backup);
            if (HashFile(backup) != journal.originalSha256)
                throw new InvalidDataException("DHE compiler recovery backup is missing or changed.");
            string currentHash = HashFile(localCompiler);
            string arguments = getArguments() ?? string.Empty;
            if (currentHash != journal.originalSha256 && currentHash != journal.patchedSha256 ||
                arguments != journal.originalArguments && arguments != journal.effectiveArguments)
                throw new InvalidDataException("DHE recovery found unknown compiler/options changes; nothing was overwritten.");
            if (currentHash == journal.patchedSha256) ReplaceBytes(localCompiler, File.ReadAllBytes(backup));
            setArguments(journal.originalArguments);
            if (HashFile(localCompiler) != journal.originalSha256 || (getArguments() ?? string.Empty) != journal.originalArguments)
                throw new IOException("DHE compiler state restoration failed; recovery journal retained.");
            File.Delete(journalPath);
        }

        public static string WithDivideChecks(string arguments)
        {
            string[] tokens = (arguments ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Any(token => token.Contains(DivideOption) && token != DivideOption))
                throw new InvalidDataException("DHE requires the unqualified --enable-divide-by-zero-check option.");
            return tokens.Contains(DivideOption) ? arguments :
                (string.IsNullOrWhiteSpace(arguments) ? DivideOption : arguments.TrimEnd() + " " + DivideOption);
        }

        private static string HashSources(string root)
        {
            root = Path.GetFullPath(root);
            string[] paths = Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetExtension(path) == ".cpp" || Path.GetExtension(path) == ".h")
                .OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
            if (paths.Length == 0) throw new InvalidDataException("DHE compiler generated-source snapshot is empty.");
            foreach (string path in paths) CheckNoLinks(path);
            return DheAotCompilerPatch.Hash(Encoding.UTF8.GetBytes(string.Join("\n",
                paths.Select(path => Path.GetFileName(path) + "=" + HashFile(path)))));
        }

        private static string RequireChild(string root, string path)
        {
            string full = Path.GetFullPath(path);
            string parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!full.StartsWith(parent + Path.DirectorySeparatorChar, PathComparison))
                throw new InvalidDataException("DHE compiler writes must remain inside the project: " + full);
            CheckNoLinks(full);
            return full;
        }

        private static void CheckNoLinks(string path)
        {
            for (string current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("DHE compiler transaction does not follow links: " + current);
        }

        private static StringComparison PathComparison => Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private static bool SamePath(string left, string right) => !string.IsNullOrWhiteSpace(left) &&
            !string.IsNullOrWhiteSpace(right) && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), PathComparison);
        private static bool IsHash(string value) => value != null && value.Length == 64 &&
            value.All(character => character >= '0' && character <= '9' || character >= 'a' && character <= 'f');
        private static string HashFile(string path) => DheAotCompilerPatch.Hash(File.ReadAllBytes(path));

        private static void WriteNew(string path, byte[] bytes)
        {
            CheckNoLinks(path);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }

        private static void ReplaceBytes(string path, byte[] bytes)
        {
            CheckNoLinks(path);
            string temporary = path + ".dhe-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteNew(temporary, bytes);
                File.Replace(temporary, path, null);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        [Serializable]
        private sealed class Journal
        {
            public string format;
            public int schemaVersion;
            public string compilerPath;
            public string originalSha256;
            public string patchedSha256;
            public string originalArguments;
            public string effectiveArguments;
        }

        [Serializable]
        private sealed class Generation
        {
            public string format;
            public int schemaVersion;
            public string generatedCppRoot;
            public DheAotCompilerIdentity compilerIdentity;
            public string sourceSha256;
        }
    }

    [Serializable]
    public sealed class DheAotCompilerIdentity
    {
        public string contract;
        public string engineVersion;
        public string originalCompilerSha256;
        public string compilerSha256;
        public string dataModelSha256;
        public string additionalIl2CppArgs;
        public bool divideByZeroChecks;
    }
}
