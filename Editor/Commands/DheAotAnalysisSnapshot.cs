using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace HybridCLR.Editor.Commands
{
    /// <summary>Immutable inputs for Base layout analysis, including ordinary AOT assemblies.</summary>
    public static class DheAotAnalysisSnapshot
    {
        public const string Format = "hybridclr.dhe-aot-analysis-snapshot.json";
        public const string Normalization = "dhe-aot-analysis-normalization-v1";

        [Serializable]
        public sealed class Manifest
        {
            public int schemaVersion;
            public string format;
            public string normalization;
            public string identityType;
            public string identityAssembly;
            public Image[] assemblies;
        }
        [Serializable]
        public sealed class Image
        {
            public string assemblyName;
            public string file;
            public string sha256;
            public string normalizedSha256;
            public bool dhe;
        }
        public sealed class CaptureResult
        {
            public string ManifestPath;
            public string ManifestSha256;
        }

        public static CaptureResult Capture(string aotRoot, string outputRoot, string identityType,
            IEnumerable<string> dheAssemblies, Func<Manifest, string> serialize)
        {
            if (serialize == null || string.IsNullOrWhiteSpace(identityType)) throw new ArgumentException("Snapshot configuration is missing.");
            var dhe = new HashSet<string>(dheAssemblies, StringComparer.Ordinal);
            var files = Inventory(aotRoot);
            if (!dhe.IsSubsetOf(files.Keys)) throw new InvalidDataException("Snapshot is missing DHE assemblies.");
            var images = new List<Image>();
            string identityAssembly = null;
            foreach (var file in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                byte[] bytes = File.ReadAllBytes(file.Value);
                byte[] canonical = Normalize(bytes, identityType, out string name, out bool containsIdentity);
                if (name != file.Key) throw new InvalidDataException("AOT file name does not match assembly identity: " + file.Key);
                if (containsIdentity)
                {
                    if (identityAssembly != null || dhe.Contains(name))
                        throw new InvalidDataException("Generated identity must have one ordinary AOT owner.");
                    identityAssembly = name;
                }
                images.Add(new Image { assemblyName = name, file = "assemblies/" + name + ".dll",
                    sha256 = Hash(bytes), normalizedSha256 = Hash(canonical), dhe = dhe.Contains(name) });
            }
            if (identityAssembly == null) throw new InvalidDataException("Generated identity type is absent from the AOT snapshot.");
            var manifest = new Manifest { schemaVersion = 1, format = Format, normalization = Normalization,
                identityType = identityType, identityAssembly = identityAssembly, assemblies = images.ToArray() };
            byte[] manifestBytes = new UTF8Encoding(false).GetBytes(serialize(manifest));
            string manifestHash = Hash(manifestBytes);
            string destination = Path.Combine(Path.GetFullPath(outputRoot), "aot-analysis", manifestHash);
            Directory.CreateDirectory(Path.Combine(destination, "assemblies"));
            foreach (Image image in images)
            {
                string target = Path.Combine(destination, image.file.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(target)) File.Copy(files[image.assemblyName], target);
                if (Hash(File.ReadAllBytes(target)) != image.sha256)
                    throw new InvalidDataException("AOT snapshot changed during capture: " + image.assemblyName);
            }
            string manifestPath = Path.Combine(destination, "manifest.json");
            if (!File.Exists(manifestPath)) File.WriteAllBytes(manifestPath, manifestBytes);
            if (Hash(File.ReadAllBytes(manifestPath)) != manifestHash) throw new InvalidDataException("Snapshot manifest collision.");
            return new CaptureResult { ManifestPath = manifestPath, ManifestSha256 = manifestHash };
        }

        public static Manifest Validate(string manifestPath, string expectedHash, string finalAotRoot,
            IEnumerable<string> expectedDheAssemblies, Func<string, Manifest> deserialize)
        {
            byte[] bytes = File.ReadAllBytes(manifestPath);
            if (Hash(bytes) != expectedHash) throw new InvalidDataException("AOT analysis manifest hash mismatch.");
            Manifest manifest = deserialize(Encoding.UTF8.GetString(bytes));
            if (manifest == null || manifest.schemaVersion != 1 || manifest.format != Format || manifest.normalization != Normalization ||
                string.IsNullOrWhiteSpace(manifest.identityType) || string.IsNullOrWhiteSpace(manifest.identityAssembly) || manifest.assemblies == null)
                throw new InvalidDataException("AOT analysis manifest format is invalid.");
            var files = Inventory(finalAotRoot);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var dhe = new HashSet<string>(StringComparer.Ordinal);
            string capturedRoot = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
            int identityOwners = 0;
            foreach (Image image in manifest.assemblies)
            {
                if (image == null || !ValidName(image.assemblyName) || !names.Add(image.assemblyName) ||
                    image.file != "assemblies/" + image.assemblyName + ".dll" || !files.ContainsKey(image.assemblyName))
                    throw new InvalidDataException("AOT snapshot inventory or path is invalid.");
                byte[] captured = File.ReadAllBytes(Path.Combine(capturedRoot, image.file.Replace('/', Path.DirectorySeparatorChar)));
                if (Hash(captured) != image.sha256) throw new InvalidDataException("Captured AOT DLL hash mismatch: " + image.assemblyName);
                foreach (byte[] input in new[] { captured, File.ReadAllBytes(files[image.assemblyName]) })
                {
                    string normalized = Hash(Normalize(input, manifest.identityType, out string name, out bool containsIdentity));
                    if (name != image.assemblyName || normalized != image.normalizedSha256 ||
                        containsIdentity != (image.assemblyName == manifest.identityAssembly))
                        throw new InvalidDataException("Final AOT semantics differ from the captured snapshot: " + image.assemblyName);
                }
                if (image.assemblyName == manifest.identityAssembly) identityOwners++;
                if (image.dhe) dhe.Add(image.assemblyName);
            }
            if (identityOwners != 1 || dhe.Contains(manifest.identityAssembly) || !names.SetEquals(files.Keys) ||
                !dhe.SetEquals(expectedDheAssemblies)) throw new InvalidDataException("AOT analysis inventory changed.");
            var capturedFiles = Inventory(Path.Combine(capturedRoot, "assemblies"));
            if (!names.SetEquals(capturedFiles.Keys)) throw new InvalidDataException("Captured AOT inventory has extra or missing files.");
            return manifest;
        }

        public static byte[] Normalize(byte[] bytes, string identityType, out string assemblyName, out bool containsIdentity)
        {
            using var module = ModuleDefMD.Load(bytes);
            assemblyName = module.Assembly?.Name.String ?? throw new InvalidDataException("AOT assembly identity is missing.");
            if (!module.IsILOnly) throw new InvalidDataException("Mixed native/managed DLLs are not supported in the AOT analysis snapshot.");
            TypeDef[] identities = module.GetTypes().Where(type => type.FullName == identityType).ToArray();
            if (identities.Length > 1) throw new InvalidDataException("Duplicate generated identity type.");
            containsIdentity = identities.Length == 1;
            if (containsIdentity) NormalizeIdentity(identities[0]);
            module.Mvid = Guid.Empty; module.EncId = Guid.Empty; module.EncBaseId = Guid.Empty;
            var options = new ModuleWriterOptions(module);
            options.PEHeadersOptions.TimeDateStamp = 0;
            using var output = new MemoryStream();
            module.Write(output, options);
            return output.ToArray();
        }

        private static void NormalizeIdentity(TypeDef type)
        {
            if (!type.IsAbstract || !type.IsSealed || type.GenericParameters.Count != 0 || type.NestedTypes.Count != 0 ||
                type.Properties.Count != 0 || type.Events.Count != 0 || type.Interfaces.Count != 0)
                throw new InvalidDataException("Unexpected generated identity type shape.");
            foreach (FieldDef field in type.Fields)
            {
                if (!field.IsStatic || field.HasFieldRVA ||
                    (field.FieldType.FullName != "System.String" && field.FieldType.FullName != "System.Int32" && field.FieldType.FullName != "System.String[]"))
                    throw new InvalidDataException("Unexpected generated identity field: " + field.FullName);
                if (field.HasConstant)
                    field.Constant = new ConstantUser(field.FieldType.FullName == "System.String" ? (object)"" : 0);
            }
            bool create = false;
            foreach (MethodDef method in type.Methods)
            {
                if (!method.IsStatic || method.Parameters.Count != 0 || method.GenericParameters.Count != 0 || !method.HasBody ||
                    (method.Name != ".cctor" && method.Name != "Create") ||
                    (method.Name == "Create" ? method.ReturnType.FullName != "HybridCLR.DheRuntimeIdentity" : method.ReturnType.ElementType != ElementType.Void))
                    throw new InvalidDataException("Unexpected generated identity method: " + method.FullName);
                method.Body = new CilBody();
                if (method.Name == "Create") { create = true; method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull)); }
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            }
            if (!create) throw new InvalidDataException("Generated identity has no Create method.");
        }

        private static Dictionary<string, string> Inventory(string root)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var caseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(Path.GetFullPath(root), "*.dll", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!ValidName(name) || !caseNames.Add(name)) throw new InvalidDataException("Invalid AOT assembly name: " + name);
                result.Add(name, file);
            }
            if (result.Count == 0) throw new InvalidDataException("AOT inventory is empty.");
            return result;
        }
        private static bool ValidName(string value) => !string.IsNullOrWhiteSpace(value) && value != "." && value != ".." &&
            value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && value.IndexOfAny(new[] { '/', '\\', ':' }) < 0;
        private static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
