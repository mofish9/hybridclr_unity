using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using UnityEditor;
using UnityEngine;

namespace HybridCLR.Editor.Commands
{
    public static class DheAssetBuild
    {
        /// <summary>Runs asset authoring against checked Editor/Current schemas and records immutable outputs.</summary>
        public static string Build(IEnumerable<string> currentAssemblyFiles, BuildTarget target, string engineWorkflow,
            string newOutputDirectory, Func<string, IDictionary<string, string>> build)
        {
            if (build == null || EditorApplication.isCompiling || EditorUserBuildSettings.activeBuildTarget != target)
                throw new InvalidOperationException("Asset build needs a settled Editor on the target platform.");
            string output = Path.GetFullPath(newOutputDirectory);
            if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Asset output must be new.");
            string[] currentFiles = currentAssemblyFiles.Select(Path.GetFullPath).ToArray();
            string currentHash = DheAssetBuildProvenance.CurrentSetHash(currentFiles);
            var inputHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var records = new List<DheAssetBuildProvenance.AssemblyRecord>();
            foreach (string file in currentFiles)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                var matches = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == name && !assembly.IsDynamic).ToArray();
                if (matches.Length != 1 || string.IsNullOrEmpty(matches[0].Location))
                    throw new InvalidDataException("Current assembly must be loaded unambiguously in the authoring Editor: " + name);
                var loaded = matches[0]; string editorFile = loaded.Location;
                using (var editorModule = ModuleDefMD.Load(File.ReadAllBytes(editorFile)))
                    if (editorModule.Mvid != loaded.ManifestModule.ModuleVersionId)
                        throw new InvalidDataException("Editor assembly changed on disk without reload: " + name);
                string schema = DheAssetBuildProvenance.CompareSchemas(file, editorFile);
                inputHashes[file] = DheAssetBuildProvenance.FileHash(file);
                inputHashes[editorFile] = DheAssetBuildProvenance.FileHash(editorFile);
                records.Add(new DheAssetBuildProvenance.AssemblyRecord { assemblyName = name, currentSha256 = inputHashes[file],
                    editorSha256 = inputHashes[editorFile], editorMvid = loaded.ManifestModule.ModuleVersionId.ToString(), schemaSha256 = schema });
            }
            Directory.CreateDirectory(output);
            var files = build(output) ?? throw new InvalidDataException("Asset builder returned no inventory.");
            if (files.Count == 0 || EditorApplication.isCompiling || EditorUserBuildSettings.activeBuildTarget != target ||
                inputHashes.Any(pair => DheAssetBuildProvenance.FileHash(pair.Key) != pair.Value) ||
                DheAssetBuildProvenance.CurrentSetHash(currentFiles) != currentHash)
                throw new InvalidDataException("Asset build inputs changed while building.");
            var assets = new List<DheAssetBuildProvenance.AssetRecord>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                DheDeliveryManifest.RequirePath(pair.Key); string path = Path.GetFullPath(pair.Value);
                if (!ids.Add(pair.Key) || !path.StartsWith(output.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Asset output must be inside the fresh build directory.");
                for (FileSystemInfo item = new FileInfo(path); item != null; item = item is FileInfo info ? info.Directory : ((DirectoryInfo)item).Parent)
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Asset output cannot use links.");
                string relative = path.Substring(output.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/');
                DheDeliveryManifest.RequirePath(relative);
                assets.Add(new DheAssetBuildProvenance.AssetRecord { id = pair.Key, file = relative,
                    length = new FileInfo(path).Length, sha256 = DheAssetBuildProvenance.FileHash(path) });
            }
            var record = new DheAssetBuildProvenance { target = target.ToString(), engineWorkflow = engineWorkflow,
                engineVersion = Application.unityVersion, currentAssemblySetSha256 = currentHash,
                assemblies = records.OrderBy(row => row.assemblyName, StringComparer.Ordinal).ToArray(), assets = assets.ToArray() };
            string result = Path.Combine(output, "asset-build.json");
            if (File.Exists(result)) throw new IOException("Asset callback cannot provide its own build provenance.");
            File.WriteAllText(result, JsonUtility.ToJson(record, true));
            DheAssetBuildProvenance.Validate(result, currentHash, files, target.ToString(), engineWorkflow);
            return result;
        }
    }
}
