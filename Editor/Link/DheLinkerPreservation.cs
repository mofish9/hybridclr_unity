using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using dnlib.DotNet;

namespace HybridCLR.Editor.Link
{
    public static class DheLinkerPreservation
    {
        public static void Write(string inputDirectory, IEnumerable<string> assemblyNames,
            string outputPath)
        {
            var roots = new HashSet<string>(assemblyNames, StringComparer.Ordinal);
            if (roots.Count == 0) throw new ArgumentException("DHE assembly set is empty.");
            var modules = new Dictionary<string, ModuleDefMD>(StringComparer.Ordinal);
            var context = ModuleDef.CreateModuleContext();
            var resolver = (AssemblyResolver)context.AssemblyResolver;
            resolver.UseGAC = false;
            resolver.EnableFrameworkRedirect = false;
            resolver.EnableTypeDefCache = true;
            try
            {
                // Resolve facade forwarders using this Player's actual linker
                // inputs, never the Editor's or host CLR's framework assemblies.
                foreach (string path in Directory.GetFiles(inputDirectory, "*.dll")
                    .OrderBy(path => path, StringComparer.Ordinal))
                {
                    var module = ModuleDefMD.Load(File.ReadAllBytes(path), context);
                    if (module.Assembly == null)
                    {
                        module.Dispose();
                        continue;
                    }
                    string name = module.Assembly.Name.String;
                    if (modules.ContainsKey(name))
                    {
                        module.Dispose();
                        throw new InvalidDataException("Duplicate linker input assembly: " + name);
                    }
                    modules.Add(name, module);
                    resolver.AddToCache(module);
                }

                var preserved = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
                foreach (string root in roots.OrderBy(name => name, StringComparer.Ordinal))
                {
                    if (!modules.TryGetValue(root, out ModuleDefMD module))
                        throw new FileNotFoundException("DHE linker input is missing: " + root);
                    foreach (TypeRef reference in module.GetTypeRefs())
                    {
                        TypeDef definition = reference.ResolveTypeDef();
                        if (definition == null || definition.Module.Assembly == null ||
                            !modules.Values.Contains(definition.Module))
                            throw new InvalidDataException("Unresolved DHE linker type: " +
                                root + ":" + reference.FullName + " in " + reference.DefinitionAssembly);
                        string owner = definition.Module.Assembly.Name.String;
                        if (roots.Contains(owner)) continue;
                        if (!preserved.TryGetValue(owner, out SortedSet<string> types))
                            preserved.Add(owner, types = new SortedSet<string>(StringComparer.Ordinal));
                        types.Add(definition.FullName);
                    }
                }

                var linker = new XElement("linker");
                foreach (string root in roots.OrderBy(name => name, StringComparer.Ordinal))
                    linker.Add(new XElement("assembly", new XAttribute("fullname", root),
                        new XAttribute("preserve", "all")));
                foreach (var assembly in preserved)
                    linker.Add(new XElement("assembly", new XAttribute("fullname", assembly.Key),
                        assembly.Value.Select(type => new XElement("type",
                            new XAttribute("fullname", type), new XAttribute("preserve", "all")))));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));
                new XDocument(linker).Save(outputPath);
            }
            finally
            {
                foreach (ModuleDefMD module in modules.Values) module.Dispose();
            }
        }
    }
}
