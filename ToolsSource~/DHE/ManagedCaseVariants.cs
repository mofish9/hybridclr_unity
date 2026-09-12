using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace HybridCLR.DheTool;

internal static class ManagedCaseVariants
{
    internal static void WriteBase2CurrentAssembly(string source, string destination)
        => WriteNextCurrentAssembly(source, destination);

    internal static void WriteNextCurrentAssembly(string source, string destination,
        bool advanceObservableResult = false)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (!File.Exists(source)) throw new FileNotFoundException(source);
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Managed case variant must not overwrite its seed assembly.");

        using var module = ModuleDefMD.Load(source);
        TypeDef probeType = module.GetTypes().Single(type =>
            type.FullName == "HybridCLR.Lab.ManagedCasesAot.DheMultiBaseProbe");
        MethodDef probe = probeType.Methods.Single(method => method.Name == "CurrentValue" &&
            method.IsStatic && method.Parameters.Count == 0);
        int probeValue = ReadReturnedInt32(probe);
        if (probeValue == int.MaxValue)
            throw new InvalidOperationException("Managed case generation value overflowed.");
        probe.Body = new CilBody
        {
            Instructions =
            {
                Instruction.Create(OpCodes.Ldc_I4, probeValue + 1),
                Instruction.Create(OpCodes.Ret),
            },
        };

        TypeDef calculatorType = module.GetTypes().Single(type =>
            type.FullName == "HybridCLR.Lab.ManagedCasesAot.DheDemoCalculator");
        MethodDef constructor = calculatorType.Methods.Single(method => method.IsInstanceConstructor &&
            method.Parameters.Count == 1);
        FieldDef touchValue = calculatorType.Fields.Single(field =>
            field.Name == "TouchValue" && field.IsStatic);
        Instruction? sentinel = FindGenerationSentinel(constructor, touchValue);
        if (sentinel != null)
        {
            int value = sentinel.GetLdcI4Value();
            if (value == int.MaxValue)
                throw new InvalidOperationException("Managed case constructor generation overflowed.");
            SetLdcI4(sentinel, value + 1);
        }
        else
        {
            Instruction existingReturn = constructor.Body.Instructions.Last(instruction =>
                instruction.OpCode == OpCodes.Ret);
            int returnIndex = constructor.Body.Instructions.IndexOf(existingReturn);
            constructor.Body.Instructions.Insert(returnIndex++,
                Instruction.Create(OpCodes.Ldsfld, touchValue));
            constructor.Body.Instructions.Insert(returnIndex++,
                Instruction.Create(OpCodes.Ldc_I4, int.MinValue));
            constructor.Body.Instructions.Insert(returnIndex++,
                Instruction.Create(OpCodes.Bne_Un_S, existingReturn));
            constructor.Body.Instructions.Insert(returnIndex++,
                Instruction.Create(OpCodes.Ldc_I4_0));
            constructor.Body.Instructions.Insert(returnIndex,
                Instruction.Create(OpCodes.Stsfld, touchValue));
        }
        constructor.Body.OptimizeBranches();

        if (advanceObservableResult)
        {
            MethodDef add = calculatorType.Methods.Single(method => method.Name == "Add" &&
                method.IsStatic && method.Parameters.Count == 1);
            if (add.MethodSig.RetType.ElementType != ElementType.I4 || add.Body == null ||
                add.Body.ExceptionHandlers.Count != 0)
                throw new InvalidOperationException("Unexpected observable generation fixture.");
            foreach (Instruction ret in add.Body.Instructions.Where(instruction =>
                         instruction.OpCode == OpCodes.Ret).ToArray())
            {
                // Keep branch targets pointing at the start of the increment.
                ret.OpCode = OpCodes.Ldc_I4_1;
                ret.Operand = null;
                int index = add.Body.Instructions.IndexOf(ret);
                add.Body.Instructions.Insert(index + 1, Instruction.Create(OpCodes.Add));
                add.Body.Instructions.Insert(index + 2, Instruction.Create(OpCodes.Ret));
            }
            add.Body.OptimizeBranches();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        module.Write(destination);
    }

    private static int ReadReturnedInt32(MethodDef method)
    {
        IList<Instruction> instructions = method.Body?.Instructions ??
            throw new InvalidOperationException("Managed case generation probe has no method body.");
        int returnIndex = instructions.ToList().FindLastIndex(instruction =>
            instruction.OpCode == OpCodes.Ret);
        if (returnIndex < 1 || !instructions[returnIndex - 1].IsLdcI4())
            throw new InvalidOperationException(
                "Managed case generation probe is not a constant Int32 return.");
        return instructions[returnIndex - 1].GetLdcI4Value();
    }

    private static Instruction? FindGenerationSentinel(MethodDef constructor,
        FieldDef touchValue)
    {
        IList<Instruction> instructions = constructor.Body.Instructions;
        for (int index = 0; index <= instructions.Count - 6; index++)
        {
            if (instructions[index].OpCode != OpCodes.Ldsfld ||
                !ReferenceEquals(instructions[index].Operand, touchValue) ||
                !instructions[index + 1].IsLdcI4() ||
                (instructions[index + 2].OpCode != OpCodes.Bne_Un &&
                 instructions[index + 2].OpCode != OpCodes.Bne_Un_S) ||
                !instructions[index + 3].IsLdcI4() ||
                instructions[index + 3].GetLdcI4Value() != 0 ||
                instructions[index + 4].OpCode != OpCodes.Stsfld ||
                !ReferenceEquals(instructions[index + 4].Operand, touchValue) ||
                instructions[index + 5].OpCode != OpCodes.Ret ||
                !ReferenceEquals(instructions[index + 2].Operand, instructions[index + 5]))
                continue;
            return instructions[index + 1];
        }
        return null;
    }

    private static void SetLdcI4(Instruction instruction, int value)
    {
        instruction.OpCode = OpCodes.Ldc_I4;
        instruction.Operand = value;
    }
}
