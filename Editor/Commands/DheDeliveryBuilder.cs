using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

namespace HybridCLR.Editor.Commands
{
    /// <summary>Packages an existing DHE resource output and authored asset files in one immutable directory.</summary>
    public static class DheDeliveryBuilder
    {
        [Serializable] private sealed class CodeManifest
        {
            public string format, currentAssemblySetSha256, runtimeAssetRoot, baseMetaVersionAssetRoot;
            public bool compatibilityValidated, playerUpdateRequired;
            public SupportedBase[] supportedBases;
        }
        [Serializable] private sealed class SupportedBase { public string target, engineWorkflow; }

        /// <returns>The SHA-256 to use as the expected delivery manifest identity at runtime.</returns>
        public static string Build(string resourceDirectory, IDictionary<string, string> assetFiles,
            string target, string engineWorkflow, string newOutputDirectory)
        {
            string resource = Path.GetFullPath(resourceDirectory), output = Path.GetFullPath(newOutputDirectory);
            if (!Directory.Exists(resource) || Directory.Exists(output) || File.Exists(output))
                throw new IOException("Resource directory must exist and delivery output must be new.");
            if (assetFiles == null) throw new ArgumentNullException(nameof(assetFiles));
            if (output.StartsWith(resource.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Delivery output cannot be inside resource input.");
            var manifest = JsonUtility.FromJson<CodeManifest>(File.ReadAllText(Path.Combine(resource, "dhe-resource-update.json")));
            if (manifest == null || manifest.format != "hybridclr.dhe-resource-update.json" || !manifest.compatibilityValidated || manifest.playerUpdateRequired ||
                manifest.supportedBases == null || manifest.supportedBases.Length == 0 ||
                manifest.supportedBases.Any(item => item == null || item.target != target || item.engineWorkflow != engineWorkflow))
                throw new InvalidDataException("Code resource is not validated for this delivery target/workflow.");
            var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string input in Directory.GetFiles(resource, "*", SearchOption.AllDirectories))
            {
                RequireRegularFile(input);
                string relative = input.Substring(resource.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/');
                DheDeliveryManifest.RequirePath(relative);
                if (relative.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                mappings.Add("code/" + relative, input);
            }
            var assets = new List<DheDeliveryAsset>();
            foreach (var item in assetFiles.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                DheDeliveryManifest.RequirePath(item.Key); string input = Path.GetFullPath(item.Value);
                RequireRegularFile(input); string path = "assets/" + item.Key;
                mappings.Add(path, input); assets.Add(new DheDeliveryAsset { id = item.Key, path = path });
            }
            var files = mappings.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new DheDeliveryFile {
                path = item.Key, kind = item.Key.StartsWith("code/", StringComparison.Ordinal) ? "code" : "asset",
                length = new FileInfo(item.Value).Length, sha256 = HashFile(item.Value) }).ToArray();
            var delivery = new DheDeliveryManifest { schemaVersion = 1, format = "hybridclr.dhe-delivery.json",
                target = target, engineWorkflow = engineWorkflow, currentAssemblySetSha256 = manifest.currentAssemblySetSha256,
                runtimeAssetRoot = manifest.runtimeAssetRoot, resourceManifest = "code/dhe-resource-update.json", files = files, assets = assets.ToArray() };
            delivery.Validate();
            if (string.IsNullOrEmpty(manifest.baseMetaVersionAssetRoot) || !manifest.baseMetaVersionAssetRoot.StartsWith(manifest.runtimeAssetRoot, StringComparison.Ordinal))
                throw new InvalidDataException("Invalid Base MV root in resource.");
            string reserved = "code/" + manifest.baseMetaVersionAssetRoot.Substring(manifest.runtimeAssetRoot.Length);
            if (files.Any(file => file.path.StartsWith(reserved, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Package resource output, not a Base-specific stage containing Base MV.");
            Directory.CreateDirectory(output);
            foreach (var file in files)
            {
                string destination = Path.Combine(output, file.path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(mappings[file.path], destination, false);
                if (new FileInfo(destination).Length != file.length || HashFile(destination) != file.sha256)
                    throw new IOException("Delivery source changed while packaging: " + file.path);
            }
            string pathManifest = Path.Combine(output, "dhe-delivery.json");
            File.WriteAllText(pathManifest, JsonUtility.ToJson(delivery, true));
            return HashFile(pathManifest);
        }

        private static void RequireRegularFile(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException(path);
            for (FileSystemInfo item = new FileInfo(path); item != null; item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Delivery input uses a link: " + path);
        }
        private static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }
    }
}
