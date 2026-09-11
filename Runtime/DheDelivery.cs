using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace HybridCLR
{
    /// <summary>Optional streaming verification for large delivery files.</summary>
    public interface IDheRuntimeStreamingAssetProvider : IDheRuntimeAssetProvider
    {
        Stream OpenRead(string assetPath);
    }

    [Serializable]
    public sealed class DheDeliveryFile
    {
        public string path, kind, sha256;
        public long length;
    }

    [Serializable]
    public sealed class DheDeliveryAsset
    {
        public string id, path;
    }

    [Serializable]
    public sealed class DheDeliveryManifest
    {
        public int schemaVersion;
        public string format, target, engineWorkflow, currentAssemblySetSha256, runtimeAssetRoot;
        public string resourceManifest;
        public DheDeliveryFile[] files;
        public DheDeliveryAsset[] assets;

        public void Validate()
        {
            if (schemaVersion != 1 || format != "hybridclr.dhe-delivery.json" ||
                string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(engineWorkflow) ||
                !IsHash(currentAssemblySetSha256) || resourceManifest != "code/dhe-resource-update.json" ||
                string.IsNullOrEmpty(runtimeAssetRoot) || !runtimeAssetRoot.EndsWith("/", StringComparison.Ordinal) ||
                files == null || files.Length == 0 || assets == null)
                throw new InvalidDataException("DHE delivery manifest is invalid.");
            RequirePath(runtimeAssetRoot.TrimEnd('/'));
            var paths = new Dictionary<string, DheDeliveryFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                if (file == null) throw new InvalidDataException("Null delivery file.");
                RequirePath(file.path);
                if ((file.kind != "code" && file.kind != "asset") || !IsHash(file.sha256) || file.length < 0 ||
                    !file.path.StartsWith(file.kind == "code" ? "code/" : "assets/", StringComparison.Ordinal) ||
                    paths.ContainsKey(file.path)) throw new InvalidDataException("Invalid or duplicate delivery file: " + file.path);
                paths.Add(file.path, file);
            }
            foreach (string path in paths.Keys)
                for (int slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                    if (paths.ContainsKey(path.Substring(0, slash))) throw new InvalidDataException("Delivery file/directory collision: " + path);
            if (!paths.ContainsKey(resourceManifest)) throw new InvalidDataException("Delivery code manifest is missing.");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var assetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in assets)
            {
                if (asset == null) throw new InvalidDataException("Null delivery asset.");
                RequirePath(asset.id); RequirePath(asset.path);
                if (!ids.Add(asset.id) || !assetPaths.Add(asset.path) || !paths.TryGetValue(asset.path, out var file) || file.kind != "asset")
                    throw new InvalidDataException("Invalid or duplicate delivery asset: " + asset.id);
            }
            if (!assetPaths.SetEquals(files.Where(file => file.kind == "asset").Select(file => file.path)))
                throw new InvalidDataException("Delivery asset inventory is incomplete.");
        }

        internal static bool IsHash(string value) => value != null && value.Length == 64 &&
            value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F');

        public static void RequirePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Any(c => char.IsControl(c) || "\\:<>\"|?*".Contains(c)) ||
                path.Split('/').Any(part => part.Length == 0 || part == "." || part == ".." ||
                    part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) ||
                    IsReserved(part.Split('.')[0])))
                throw new InvalidDataException("Unsafe delivery path: " + path);
        }

        private static bool IsReserved(string part)
        {
            string name = part.ToUpperInvariant();
            return name == "CON" || name == "PRN" || name == "AUX" || name == "NUL" ||
                name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
                name[3] >= '1' && name[3] <= '9';
        }
    }

    /// <summary>A verified code/asset selection. Assets remain inaccessible until its native load succeeds.</summary>
    public sealed class DheDelivery
    {
        [Serializable] private sealed class CodeHeader
        {
            public int schemaVersion;
            public string format, currentAssemblySetSha256, runtimeAssetRoot;
        }

        internal readonly DeliveryProvider Provider;
        internal readonly string[] AssemblyNames;
        internal readonly string[] CurrentPaths;
        private readonly Dictionary<string, string> assets;
        private int ready, loadBusy;
        public string ManifestSha256 { get; }
        public string[] AssetIds => assets.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        public bool IsReady => Volatile.Read(ref ready) != 0 && DheRuntime.IsActiveDelivery(this);

        internal DheDelivery(DeliveryProvider provider, string hash, string[] names, string[] paths)
        {
            Provider = provider; ManifestSha256 = hash;
            AssemblyNames = (string[])names.Clone(); CurrentPaths = (string[])paths.Clone();
            assets = provider.Manifest.assets.ToDictionary(asset => asset.id, asset => asset.path, StringComparer.OrdinalIgnoreCase);
        }

        public bool LoadCurrentAssemblies(out LoadImageErrorCode code, out string error)
        {
            code = LoadImageErrorCode.DHE_LOAD_IN_PROGRESS; error = "Delivery load is already in progress.";
            if (Interlocked.CompareExchange(ref loadBusy, 1, 0) != 0) return false;
            try
            {
                bool loaded = DheRuntime.LoadPreparedDelivery(this, out code, out error);
                if (loaded) Volatile.Write(ref ready, 1);
                return loaded;
            }
            finally { Volatile.Write(ref loadBusy, 0); }
        }

        public byte[] LoadAssetBytes(string assetId)
        {
            if (!IsReady) throw new InvalidOperationException("DHE delivery is not active.");
            if (assetId == null || !assets.TryGetValue(assetId, out string path))
                throw new FileNotFoundException("Asset is not part of the active DHE delivery: " + assetId);
            return Provider.ReadDeliveryFile(path);
        }

        internal sealed class DeliveryProvider : IDheRuntimeAssetProvider
        {
            internal readonly DheDeliveryManifest Manifest;
            private readonly IDheRuntimeAssetProvider source, embedded;
            private readonly string directory, baseRoot;
            private readonly Dictionary<string, DheDeliveryFile> files;
            private readonly Dictionary<string, byte[]> codeCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            internal DeliveryProvider(IDheRuntimeAssetProvider source, IDheRuntimeAssetProvider embedded,
                DheRuntimeIdentity identity, string manifestPath, string expectedHash)
            {
                if (source == null || embedded == null || identity == null || !DheDeliveryManifest.IsHash(expectedHash))
                    throw new ArgumentException("Delivery providers, Base identity and expected manifest hash are required.");
                DheDeliveryManifest.RequirePath(manifestPath);
                this.source = source; this.embedded = embedded;
                int separator = manifestPath.LastIndexOf('/'); directory = separator < 0 ? "" : manifestPath.Substring(0, separator + 1);
                byte[] bytes = CopyBytes(source.LoadBytes(manifestPath));
                if (!SameHash(bytes, expectedHash)) throw new InvalidDataException("DHE delivery manifest hash mismatch.");
                Manifest = JsonUtility.FromJson<DheDeliveryManifest>(new UTF8Encoding(false, true).GetString(bytes));
                if (Manifest == null) throw new InvalidDataException("Missing DHE delivery manifest.");
                Manifest.Validate();
                if (Manifest.target != identity.Target || Manifest.engineWorkflow != identity.EngineWorkflow ||
                    !string.Equals(Manifest.runtimeAssetRoot, identity.RuntimeAssetRoot, StringComparison.Ordinal))
                    throw new InvalidDataException("DHE delivery target or runtime root does not match this Base.");
                baseRoot = identity.BaseMetaVersionAssetRoot;
                if (string.IsNullOrEmpty(baseRoot) || !baseRoot.StartsWith(Manifest.runtimeAssetRoot, StringComparison.Ordinal) ||
                    !baseRoot.EndsWith("/", StringComparison.Ordinal)) throw new InvalidDataException("Invalid embedded Base MV root.");
                DheDeliveryManifest.RequirePath(baseRoot.TrimEnd('/'));
                files = Manifest.files.ToDictionary(file => file.path, StringComparer.OrdinalIgnoreCase);
                string reserved = "code/" + baseRoot.Substring(Manifest.runtimeAssetRoot.Length);
                if (files.Keys.Any(path => path.StartsWith(reserved, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Delivery cannot override embedded Base MV.");
                foreach (var file in Manifest.files) Verify(file);
                var header = JsonUtility.FromJson<CodeHeader>(new UTF8Encoding(false, true).GetString(ReadCodeFile(Manifest.resourceManifest)));
                if (header == null || header.schemaVersion != 1 || header.format != "hybridclr.dhe-resource-update.json" ||
                    !string.Equals(header.currentAssemblySetSha256, Manifest.currentAssemblySetSha256, StringComparison.OrdinalIgnoreCase) ||
                    header.runtimeAssetRoot != Manifest.runtimeAssetRoot)
                    throw new InvalidDataException("Delivery is not bound to its Current code manifest.");
            }

            internal void VerifyAssets()
            {
                foreach (var file in Manifest.files.Where(file => file.kind == "asset")) Verify(file);
            }

            private void Verify(DheDeliveryFile file)
            {
                if (source is IDheRuntimeStreamingAssetProvider streaming)
                {
                    using (Stream stream = streaming.OpenRead(directory + file.path))
                    using (var sha = SHA256.Create())
                    {
                        if (stream == null) throw new FileNotFoundException(file.path);
                        byte[] buffer = new byte[64 * 1024]; long length = 0; int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            length = checked(length + read);
                            if (length > file.length) throw new InvalidDataException("Delivery file length mismatch: " + file.path);
                            sha.TransformBlock(buffer, 0, read, buffer, 0);
                        }
                        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        if (length != file.length || !HashText(sha.Hash).Equals(file.sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Delivery file hash/length mismatch: " + file.path);
                    }
                }
                else ReadDeliveryFile(file.path);
            }

            internal byte[] ReadDeliveryFile(string path)
            {
                if (!files.TryGetValue(path, out var file)) throw new FileNotFoundException("Unlisted delivery file: " + path);
                byte[] bytes = CopyBytes(source.LoadBytes(directory + file.path));
                if (bytes.LongLength != file.length || !SameHash(bytes, file.sha256))
                    throw new InvalidDataException("Delivery file hash/length mismatch: " + path);
                return bytes;
            }

            private byte[] ReadCodeFile(string path)
            {
                if (!codeCache.TryGetValue(path, out byte[] bytes)) codeCache.Add(path, bytes = ReadDeliveryFile(path));
                return (byte[])bytes.Clone();
            }

            private string CodePath(string path)
            {
                DheDeliveryManifest.RequirePath(path);
                if (!path.StartsWith(Manifest.runtimeAssetRoot, StringComparison.Ordinal))
                    throw new InvalidDataException("Code read is outside the delivery runtime root: " + path);
                return "code/" + path.Substring(Manifest.runtimeAssetRoot.Length);
            }

            public bool Exists(string path)
            {
                string codePath = CodePath(path);
                return path.StartsWith(baseRoot, StringComparison.Ordinal) ? embedded.Exists(path) : files.ContainsKey(codePath);
            }
            public byte[] LoadBytes(string path)
            {
                string codePath = CodePath(path);
                return path.StartsWith(baseRoot, StringComparison.Ordinal) ? CopyBytes(embedded.LoadBytes(path)) : ReadCodeFile(codePath);
            }
            public string LoadText(string path) => new UTF8Encoding(false, true).GetString(LoadBytes(path));
        }

        private static byte[] CopyBytes(byte[] value) => value == null ? throw new FileNotFoundException("Missing delivery bytes.") : (byte[])value.Clone();
        private static string HashText(byte[] hash) => BitConverter.ToString(hash).Replace("-", "");
        private static bool SameHash(byte[] bytes, string expected)
        {
            using (var sha = SHA256.Create()) return HashText(sha.ComputeHash(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
    }
}
