using dnlib.DotNet;
using dnlib.DotNet.Writer;

namespace HybridCLR.DheTool;

// UnityLinker can reorder its compiler assembly attributes on a second pass.
// This comparison is only for hotfix Base IL versus its captured DHE AOT image.
// It never substitutes for the raw hashes of ordinary frozen AOT sources.
internal static class DheAotBaselineIdentity
{
    private static readonly HashSet<string> CompilerAttributes = new(StringComparer.Ordinal)
    {
        "System.Runtime.CompilerServices.CompilationRelaxationsAttribute",
        "System.Runtime.CompilerServices.RuntimeCompatibilityAttribute",
        "System.Runtime.Versioning.TargetFrameworkAttribute",
        "System.Diagnostics.DebuggableAttribute",
        "System.Reflection.AssemblyCompanyAttribute",
        "System.Reflection.AssemblyConfigurationAttribute",
        "System.Reflection.AssemblyFileVersionAttribute",
        "System.Reflection.AssemblyInformationalVersionAttribute",
        "System.Reflection.AssemblyProductAttribute",
        "System.Reflection.AssemblyTitleAttribute",
    };

    internal static bool Matches(byte[] baseline, byte[] captured)
    {
        if (baseline.SequenceEqual(captured)) return true;
        using var before = ModuleDefMD.Load(baseline);
        using var after = ModuleDefMD.Load(captured);
        if (!before.IsILOnly || !after.IsILOnly || before.Assembly == null || after.Assembly == null ||
            !DefinitionTokens(before).SequenceEqual(DefinitionTokens(after), StringComparer.Ordinal)) return false;
        byte[]? first = Normalize(before), second = Normalize(after);
        return first != null && second != null && first.SequenceEqual(second);
    }

    private static IEnumerable<string> DefinitionTokens(ModuleDef module)
    {
        foreach (TypeDef type in module.GetTypes())
        {
            yield return type.MDToken.Raw + ":" + type.FullName;
            foreach (var parameter in type.GenericParameters) yield return parameter.MDToken.Raw + ":" + parameter.Number;
            foreach (FieldDef field in type.Fields) yield return field.MDToken.Raw + ":" + field.FullName;
            foreach (MethodDef method in type.Methods)
            {
                yield return method.MDToken.Raw + ":" + method.FullName;
                foreach (var parameter in method.ParamDefs) yield return parameter.MDToken.Raw + ":" + parameter.Sequence;
                foreach (var parameter in method.GenericParameters) yield return parameter.MDToken.Raw + ":" + parameter.Number;
            }
            foreach (PropertyDef property in type.Properties) yield return property.MDToken.Raw + ":" + property.FullName;
            foreach (EventDef item in type.Events) yield return item.MDToken.Raw + ":" + item.FullName;
        }
    }

    private static bool IsCompilerAttribute(CustomAttribute attribute) =>
        CompilerAttributes.Contains(attribute.TypeFullName) && attribute.AttributeType.DefinitionAssembly?.Name == "mscorlib";

    private static byte[]? Normalize(ModuleDef module)
    {
        var attributes = module.Assembly.CustomAttributes;
        var compiler = attributes.Where(IsCompilerAttribute).ToArray();
        if (compiler.Select(attribute => attribute.TypeFullName).Distinct(StringComparer.Ordinal).Count() != compiler.Length)
            return null;
        var ordered = compiler.OrderBy(attribute => attribute.TypeFullName, StringComparer.Ordinal).ToArray();
        int next = 0;
        for (int index = 0; index < attributes.Count; ++index)
            if (IsCompilerAttribute(attributes[index])) attributes[index] = ordered[next++];
        module.Mvid = Guid.Empty; module.EncId = Guid.Empty; module.EncBaseId = Guid.Empty;
        var options = new ModuleWriterOptions(module);
        options.PEHeadersOptions.TimeDateStamp = 0;
        using var output = new MemoryStream();
        module.Write(output, options);
        return output.ToArray();
    }
}
