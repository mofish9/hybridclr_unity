using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using dnlib.DotNet;

namespace HybridCLR.Editor.Commands
{
    /// <summary>Guard requests from the exact stripped inputs of the current native build.</summary>
    public static class DheOrdinaryGuardInventory
    {
        [Serializable] public sealed class Method
        {
            public string identity, stableId, name, declaringType, returnType;
            public uint token, flags;
            public string[] parameterTypes;
            public bool isStatic, isAbstract, isPInvoke, hasThis, declaringTypeIsValueType;
            public int genericParameterCount, declaringTypeGenericParameterCount;
        }
        [Serializable] public sealed class Source
        {
            public string assemblyName, source, sourceSha256, guardMvJson, guardMvJsonSha256;
            public int methodCount, excludedIdentityMethodCount;
        }
        [Serializable] public sealed class Manifest
        {
            public int schemaVersion = 1;
            public string format = "hybridclr.dhe-ordinary-guard-inventory.json";
            public string sourceRoot, identityType;
            public string[] mutableAssemblyNames;
            public int ordinaryAssemblyCount;
            public Source[] sources;
        }
        [Serializable] public sealed class Request
        {
            public string assemblyName, assemblySha256;
            public Method[] methods;
        }
        public sealed class Result
        {
            public string ManifestPath;
            public string[] MvJsonPaths;
        }

        public static Result Generate(string aotRoot, IEnumerable<string> mutableAssemblies,
            string identityType, string outputRoot, Func<object, string> serialize)
        {
            if (serialize == null || string.IsNullOrWhiteSpace(identityType))
                throw new ArgumentException("Ordinary guards require an identity type and a serializer.");
            string root = Path.GetFullPath(aotRoot), destination = Path.GetFullPath(outputRoot);
            var mutable = new HashSet<string>(mutableAssemblies, StringComparer.Ordinal);
            string[] files = Directory.GetFiles(root, "*.dll").OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (files.Length == 0 || !mutable.IsSubsetOf(files.Select(path => Path.GetFileNameWithoutExtension(path))))
                throw new InvalidDataException("Ordinary guard inventory is missing stripped Base assemblies.");
            Directory.CreateDirectory(destination);
            var sources = new List<Source>(); var paths = new List<string>();
            int identityOwners = 0;
            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (mutable.Contains(name)) continue;
                byte[] bytes = File.ReadAllBytes(file);
                using var module = ModuleDefMD.Load(bytes);
                if (module.Assembly == null || module.Assembly.Name.String != name)
                    throw new InvalidDataException("Ordinary guard DLL identity mismatch: " + file);
                var methods = new List<Method>(); int excluded = 0;
                // Module methods are native Base entries too. Deferring a
                // hotfix initializer is selected separately by the primary
                // MV inventory; an ordinary module keeps eager execution.
                foreach (var type in module.GetTypes())
                {
                    if (type.FullName == identityType) { identityOwners++; excluded += type.Methods.Count; continue; }
                    foreach (var method in type.Methods)
                    {
                        string identity = type.FullName + "::" + method.Name + "|" + method.MethodSig;
                        uint flags = (method.IsStatic ? 1u : 0u) | (method.IsAbstract ? 2u : 0u) |
                            (method.IsPinvokeImpl ? 4u : 0u) | (method.HasBody ? 8u : 0u) |
                            (method.MethodSig.GenParamCount > 0 ? 16u : 0u) | (type.GenericParameters.Count > 0 ? 32u : 0u);
                        methods.Add(new Method { identity = identity, stableId = Hash(Encoding.UTF8.GetBytes("dhe-method-id\n" + identity)),
                            name = method.Name, declaringType = type.FullName, token = method.MDToken.Raw, flags = flags,
                            returnType = method.MethodSig.RetType.FullName, parameterTypes = method.MethodSig.Params.Select(parameter => parameter.FullName).ToArray(),
                            isStatic = method.IsStatic, isAbstract = method.IsAbstract, isPInvoke = method.IsPinvokeImpl,
                            hasThis = method.MethodSig.HasThis, declaringTypeIsValueType = type.IsValueType,
                            genericParameterCount = method.GenericParameters.Count, declaringTypeGenericParameterCount = type.GenericParameters.Count });
                    }
                }
                string path = Path.Combine(destination, name + ".mv.json");
                string hash = Hash(bytes);
                File.WriteAllText(path, serialize(new Request { assemblyName = name, assemblySha256 = hash,
                    methods = methods.OrderBy(method => method.token).ToArray() }), new UTF8Encoding(false));
                paths.Add(path);
                sources.Add(new Source { assemblyName = name, source = file, sourceSha256 = hash,
                    guardMvJson = Path.GetFileName(path), guardMvJsonSha256 = Hash(File.ReadAllBytes(path)),
                    methodCount = methods.Count, excludedIdentityMethodCount = excluded });
                if (Hash(File.ReadAllBytes(file)) != hash)
                    throw new InvalidDataException("Stripped source changed during ordinary guard generation: " + name);
            }
            if (identityOwners != 1) throw new InvalidDataException("Ordinary guards require exactly one generated identity owner.");
            string manifestPath = Path.Combine(destination, "ordinary-guard-inventory.json");
            File.WriteAllText(manifestPath, serialize(new Manifest { sourceRoot = root, identityType = identityType,
                mutableAssemblyNames = mutable.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                ordinaryAssemblyCount = sources.Count, sources = sources.ToArray() }), new UTF8Encoding(false));
            return new Result { ManifestPath = manifestPath, MvJsonPaths = paths.ToArray() };
        }

        private static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }
    }
}
