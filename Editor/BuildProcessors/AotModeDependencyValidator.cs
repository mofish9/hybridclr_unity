using System;
using System.Collections.Generic;
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
            foreach (string path in assemblyPaths)
            using (var module = ModuleDefMD.Load(path))
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
                    foreach (var method in type.Methods)
                    foreach (var attribute in method.CustomAttributes)
                        if (attribute.TypeFullName == "UnityEngine.RuntimeInitializeOnLoadMethodAttribute" ||
                            attribute.TypeFullName == "AOT.MonoPInvokeCallbackAttribute")
                            throw new InvalidOperationException($"AOT mode selection: deferred method '{method.FullName}' has a native startup/callback registration. Register callbacks from ordinary AOT after choosing the mode and loading hotfix code.");
                }
            }
        }

    }
}
