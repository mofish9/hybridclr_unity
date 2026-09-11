using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using dnlib.DotNet;
using UnityEngine;

namespace HybridCLR.Editor.Commands
{
    [Serializable]
    public sealed class DheAssetBuildProvenance
    {
        public int schemaVersion = 1;
        public string format = "hybridclr.dhe-asset-build.json";
        public string target, engineWorkflow, engineVersion, currentAssemblySetSha256;
        public AssemblyRecord[] assemblies;
        public AssetRecord[] assets;
        [Serializable] public sealed class AssemblyRecord
        {
            public string assemblyName, currentSha256, editorSha256, editorMvid, schemaSha256;
        }
        [Serializable] public sealed class AssetRecord { public string id, file, sha256; public long length; }

        /// <summary>Checks serialized declarations, allowing method-only Editor/Player differences.</summary>
        public static string CompareSchemas(string currentFile, string editorFile)
        {
            using (var current = ModuleDefMD.Load(File.ReadAllBytes(currentFile)))
            using (var editor = ModuleDefMD.Load(File.ReadAllBytes(editorFile)))
            {
                if (current.Assembly.FullName != editor.Assembly.FullName)
                    throw new InvalidDataException("Asset authoring assembly identity differs: " + current.Assembly.Name);
                string left = Schema(current), right = Schema(editor);
                if (left != right) throw new InvalidDataException("Editor/Current serialization schema differs: " + current.Assembly.Name);
                return Hash(Encoding.UTF8.GetBytes(left));
            }
        }

        private static string Schema(ModuleDef module)
        {
            string Sig(TypeSig type)
            {
                if (type == null) return "";
                if (type is GenericInstSig generic) return Sig(generic.GenericType) + "<" + string.Join(",", generic.GenericArguments.Select(Sig)) + ">";
                if (type is TypeDefOrRefSig named) return named.FullName + "@" + named.TypeDefOrRef.DefinitionAssembly?.Name;
                return type.FullName + (type.Next == null ? "" : "(" + Sig(type.Next) + ")");
            }
            bool Attr(IHasCustomAttribute owner, string name) => owner.CustomAttributes.Any(a => a.TypeFullName == name);
            bool Field(FieldDef field) => !field.IsStatic && !field.IsLiteral && !field.IsNotSerialized &&
                (field.IsPublic || Attr(field, "UnityEngine.SerializeField") || Attr(field, "UnityEngine.SerializeReference"));
            var records = new List<string>();
            foreach (var type in module.GetTypes().Where(type => !type.IsGlobalModuleType).OrderBy(type => type.FullName, StringComparer.Ordinal))
            {
                var fields = type.Fields.Where(Field).ToArray();
                bool engineType = type.BaseType != null && type.BaseType.FullName.StartsWith("UnityEngine.", StringComparison.Ordinal);
                if (!type.IsSerializable && !type.IsEnum && fields.Length == 0 && !engineType) continue;
                records.Add("type|" + type.FullName + "|" + Sig(type.BaseType?.ToTypeSig()) + "|" + type.IsSerializable + "|" + type.IsValueType + "|" + type.GenericParameters.Count);
                foreach (var field in fields)
                {
                    string[] formerNames = field.CustomAttributes.Where(a => a.TypeFullName == "UnityEngine.Serialization.FormerlySerializedAsAttribute")
                        .Select(a => a.ConstructorArguments.Count == 1 ? a.ConstructorArguments[0].Value?.ToString() : "<invalid>")
                        .OrderBy(value => value, StringComparer.Ordinal).ToArray();
                    records.Add("field|" + field.Name + "|" + Sig(field.FieldSig.Type) + "|" + Attr(field, "UnityEngine.SerializeReference") + "|" +
                        string.Join(";", formerNames.Select(value => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? "")))));
                }
                if (type.IsEnum)
                    foreach (var value in type.Fields.Where(field => field.IsLiteral))
                        records.Add("enum|" + value.Name + "|" + Convert.ToString(value.Constant?.Value, System.Globalization.CultureInfo.InvariantCulture));
            }
            return string.Join("\n", records);
        }

        public static string CurrentSetHash(IEnumerable<string> assemblyFiles)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var sha = SHA256.Create())
            {
                foreach (string path in assemblyFiles.OrderBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.Ordinal))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (!names.Add(name)) throw new InvalidDataException("Duplicate Current assembly: " + name);
                    byte[] bytes = File.ReadAllBytes(path);
                    using (var module = ModuleDefMD.Load(bytes))
                        if (module.Assembly.Name != name) throw new InvalidDataException("Current assembly filename/identity mismatch.");
                    byte[] prefix = Encoding.UTF8.GetBytes(name + "\n"), end = { (byte)'\n' };
                    sha.TransformBlock(prefix, 0, prefix.Length, prefix, 0);
                    sha.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
                    sha.TransformBlock(end, 0, end.Length, end, 0);
                }
                if (names.Count == 0) throw new InvalidDataException("Current assembly set is empty.");
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0); return Hex(sha.Hash);
            }
        }

        public static string Validate(string recordPath, string currentSetHash, IDictionary<string, string> assetFiles,
            string expectedTarget, string expectedWorkflow)
        {
            byte[] bytes = File.ReadAllBytes(recordPath);
            var record = JsonUtility.FromJson<DheAssetBuildProvenance>(new UTF8Encoding(false, true).GetString(bytes));
            if (record == null || record.schemaVersion != 1 || record.format != "hybridclr.dhe-asset-build.json" ||
                record.target != expectedTarget || record.engineWorkflow != expectedWorkflow || string.IsNullOrWhiteSpace(record.engineVersion) ||
                !string.Equals(record.currentAssemblySetSha256, currentSetHash, StringComparison.OrdinalIgnoreCase) ||
                record.assemblies == null || record.assemblies.Length == 0 || record.assets == null || assetFiles == null)
                throw new InvalidDataException("Asset build provenance does not match this Current/target.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in record.assemblies)
                if (assembly == null || string.IsNullOrWhiteSpace(assembly.assemblyName) || !names.Add(assembly.assemblyName) ||
                    !IsHash(assembly.currentSha256) || !IsHash(assembly.editorSha256) || !IsHash(assembly.schemaSha256) || !Guid.TryParse(assembly.editorMvid, out _))
                    throw new InvalidDataException("Asset provenance assembly record is invalid.");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in record.assets)
            {
                if (asset == null) throw new InvalidDataException("Missing asset record.");
                DheDeliveryManifest.RequirePath(asset.id); DheDeliveryManifest.RequirePath(asset.file);
                if (!ids.Add(asset.id) || !IsHash(asset.sha256) || asset.length < 0 || !assetFiles.TryGetValue(asset.id, out string file) ||
                    new FileInfo(file).Length != asset.length || !string.Equals(FileHash(file), asset.sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Asset files do not match build provenance: " + asset.id);
            }
            if (!ids.SetEquals(assetFiles.Keys)) throw new InvalidDataException("Asset provenance inventory differs.");
            return Hash(bytes);
        }

        internal static bool IsHash(string hash) => hash != null && hash.Length == 64 && hash.All(c => Uri.IsHexDigit(c));
        internal static string FileHash(string file) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(file)) return Hex(sha.ComputeHash(stream)); }
        internal static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes)); }
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");
    }
}
