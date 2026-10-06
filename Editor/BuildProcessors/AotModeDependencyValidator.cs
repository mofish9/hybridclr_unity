using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;

namespace HybridCLR.Editor.BuildProcessors
{
    // No Unity dependency: the same validation is exercised on compiler-produced
    // assemblies by the lab gate and on unstripped final Player inputs here.
    public static class AotModeDependencyValidator
    {
        public static void Validate(IEnumerable<string> assemblyPaths, IEnumerable<string> deferredNames)
        {
            var deferred = new HashSet<string>(deferredNames, StringComparer.OrdinalIgnoreCase);
            var paths = assemblyPaths.ToArray();
            var resolver = new AssemblyResolver();
            var context = new ModuleContext(resolver);
            foreach (string directory in paths.Select(path => Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new InvalidOperationException(path)).Distinct())
                resolver.PreSearchPaths.Add(directory);
            foreach (string path in paths)
            using (var module = ModuleDefMD.Load(path, context))
            {
                string name = module.Assembly.Name.String;
                if (!deferred.Contains(name))
                {
                    foreach (var reference in module.GetAssemblyRefs())
                        if (deferred.Contains(reference.Name.String))
                            throw new InvalidOperationException($"AOT mode selection: ordinary AOT assembly '{name}' references deferred hotfix assembly '{reference.Name}'. Move shared contracts to an ordinary AOT assembly and invoke hotfix entry through reflection or an AOT interface.");
                    continue;
                }
                foreach (var type in module.GetTypes())
                {
                    // Unity serializes native script identities separately from
                    // Assembly.Load. Until that path has its own gate, reject it.
                    if (IsUnityScript(type))
                        throw new InvalidOperationException($"AOT mode selection: deferred Unity script '{type.FullName}' needs native serialization validation; keep the Unity component in ordinary AOT.");
                    foreach (var method in type.Methods)
                    foreach (var attribute in method.CustomAttributes)
                        if (attribute.TypeFullName == "UnityEngine.RuntimeInitializeOnLoadMethodAttribute" ||
                            attribute.TypeFullName == "AOT.MonoPInvokeCallbackAttribute")
                            throw new InvalidOperationException($"AOT mode selection: deferred method '{method.FullName}' has a native startup/callback registration. Register callbacks from ordinary AOT after choosing the mode and loading hotfix code.");
                }
            }
        }

        static bool IsUnityScript(TypeDef type)
        {
            var visited = new HashSet<string>();
            for (ITypeDefOrRef parent = type.BaseType; parent != null;)
            {
                if (parent.FullName == "UnityEngine.MonoBehaviour" || parent.FullName == "UnityEngine.ScriptableObject") return true;
                if (parent.FullName == "System.Object" || parent.FullName == "System.ValueType" ||
                    parent.FullName == "System.Enum" || parent.FullName == "System.MulticastDelegate") return false;
                if (!visited.Add(parent.AssemblyQualifiedName))
                    throw new InvalidOperationException("AOT mode selection: cyclic base type on " + type.FullName);
                var definition = parent.ResolveTypeDef();
                if (definition == null)
                    throw new InvalidOperationException("AOT mode selection: cannot validate base type " + parent.FullName + " of " + type.FullName);
                parent = definition.BaseType;
            }
            return false;
        }
    }
}
