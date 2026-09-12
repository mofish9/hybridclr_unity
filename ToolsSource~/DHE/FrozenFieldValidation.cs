using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace HybridCLR.DheTool;

// This is an execution adaptation of authenticated Base IL, not a replacement
// mscorlib payload. Keep this prefix contract aligned with FrozenFieldValidation.h.
internal static class FrozenFieldValidation
{
    internal const string Capability = "frozen-field-object-validation-v1";
    internal const string Reason = "physical-parent-field-validation";
    internal const string Getter = "System.Object System.Reflection.RuntimeFieldInfo::GetValue(System.Object)";
    internal const string Setter = "System.Void System.Reflection.RuntimeFieldInfo::SetValue(System.Object,System.Object,System.Reflection.BindingFlags,System.Reflection.Binder,System.Globalization.CultureInfo)";

    internal static bool HasParentChange(IEnumerable<MetaVersionSnapshot> before, IEnumerable<MetaVersionSnapshot> current)
    {
        var latest = current.ToDictionary(mv => mv.AssemblyName, StringComparer.Ordinal);
        return before.Any(mv => latest.TryGetValue(mv.AssemblyName, out var after) &&
            mv.TypeParents.Any(row => after.TypeParents.TryGetValue(row.Key, out var parent) && row.Value != parent));
    }

    internal static MethodDef[] Select(ModuleDefMD module)
    {
        if (module.Assembly.Name != "mscorlib") throw new InvalidDataException("Frozen field validation requires Base mscorlib.");
        var owner = module.Find("System.Reflection.RuntimeFieldInfo", false)
            ?? throw new InvalidDataException("Base RuntimeFieldInfo is missing.");
        var methods = new[] { Getter, Setter }.Select(signature => owner.Methods.SingleOrDefault(method => method.FullName == signature)
            ?? throw new InvalidDataException("Base field accessor is missing: " + signature)).ToArray();
        foreach (var method in methods) Validate(method);
        var check = module.Find("System.Type", false)?.Methods.SingleOrDefault(method =>
            method.FullName == "System.Boolean System.Type::IsInstanceOfType(System.Object)");
        if (check == null || !check.IsVirtual || check.IsStatic)
            throw new InvalidDataException("Base object-based Type validation is missing.");
        return methods;
    }

    internal static void Validate(MethodDef method)
    {
        if (method.Module.Assembly.Name != "mscorlib" || method.IsStatic || !method.IsIL || !method.HasBody ||
            method.Body.ExceptionHandlers.Count != 0 || method.FullName != Getter && method.FullName != Setter)
            throw new InvalidDataException("Unsupported frozen field accessor: " + method.FullName);
        var il = method.Body.Instructions;
        uint[] offsets = { 0, 1, 6, 8, 9, 11, 16, 21, 22, 23, 28, 29, 34, 39 };
        var codes = new[] { Code.Ldarg_0, Code.Call, Code.Brtrue_S, Code.Ldarg_1, Code.Brtrue_S, Code.Ldstr,
            Code.Newobj, Code.Throw, Code.Ldarg_0, Code.Callvirt, Code.Ldarg_1, Code.Callvirt, Code.Callvirt, Code.Brtrue_S };
        bool Call(int index, string signature) => il[index].Operand is IMethod target && target.FullName == signature &&
            target.DeclaringType.DefinitionAssembly?.Name == "mscorlib";
        bool Branch(int index, uint target) => il[index].Operand is Instruction instruction && instruction.Offset == target;
        bool valid = il.Count >= 14 && Enumerable.Range(0, 14).All(index => il[index].Offset == offsets[index] && il[index].OpCode.Code == codes[index]) &&
            Call(1, "System.Boolean System.Reflection.FieldInfo::get_IsStatic()") &&
            Branch(2, 80) && Branch(4, 22) && Branch(13, 80) &&
            Call(6, "System.Void System.Reflection.TargetException::.ctor(System.String)") &&
            Call(9, "System.Type System.Reflection.MemberInfo::get_DeclaringType()") &&
            Call(11, "System.Type System.Object::GetType()") &&
            Call(12, "System.Boolean System.Type::IsAssignableFrom(System.Type)");
        bool EntersExpression(Instruction target) => target.Offset > 22 && target.Offset < 39;
        valid &= !il.Any(instruction => instruction.Operand is Instruction target && EntersExpression(target) ||
            instruction.Operand is IList<Instruction> targets && targets.Any(EntersExpression));
        if (!valid) throw new InvalidDataException("Unsupported Base reflected-field validation IL: " + method.FullName);
    }
}
