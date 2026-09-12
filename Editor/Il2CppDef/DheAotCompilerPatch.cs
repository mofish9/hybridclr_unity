using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace HybridCLR.Editor.Il2CppDef
{
    public static class DheAotCompilerPatch
    {
        public const string Contract = "dhe-aot-codegen-v1";

        public static DheAotCompilerPatchResult Create(byte[] original, byte[] dataModelBytes)
        {
            using var module = ModuleDefMD.Load(original);
            using var dataModel = ModuleDefMD.Load(dataModelBytes);
            TypeDef writer = module.Find("Unity.IL2CPP.MethodBodyWriter", false)
                ?? throw new InvalidDataException("Unsupported DHE compiler: no MethodBodyWriter.");
            MethodDef dispatch = writer.Methods.Single(method => method.Name == "ProcessInstruction");
            TypeDef stackInfo = module.Find("Unity.IL2CPP.StackInfo", false)
                ?? throw new InvalidDataException("Unsupported DHE compiler: no StackInfo.");
            FieldDef expression = stackInfo.Fields.Single(field => field.Name == "Expression" && field.FieldType.FullName == "System.String");
            FieldDef codeWriter = writer.Fields.Single(field => field.Name == "_writer");
            MethodDef writeStatement = module.Find("Unity.IL2CPP.CodeWriters.ICodeWriter", false).Methods.Single(method =>
                method.Name == "WriteStatement" && method.MethodSig.Params.Count == 1 && method.MethodSig.Params[0].FullName == "System.String");
            var instructions = dispatch.Body.Instructions;
            var candidates = Enumerable.Range(0, Math.Max(0, instructions.Count - 4)).Where(index =>
                instructions[index].OpCode.Code == Code.Ldarg_0 &&
                instructions[index + 1].OpCode.Code == Code.Ldfld &&
                instructions[index + 1].Operand is IField field && field.Name == "_valueStack" &&
                instructions[index + 2].OpCode.Code == Code.Callvirt &&
                instructions[index + 2].Operand is IMethod pop && pop.Name == "Pop" &&
                pop.MethodSig.RetType is GenericVar returnType && returnType.Number == 0 &&
                pop.DeclaringType is TypeSpec declaringType && declaringType.TypeSig is GenericInstSig stack &&
                stack.GenericType.FullName == "System.Collections.Generic.Stack`1" &&
                stack.GenericArguments.Count == 1 && stack.GenericArguments[0].FullName == stackInfo.FullName &&
                instructions[index + 3].OpCode.Code == Code.Pop &&
                instructions[index + 4].OpCode.Code == Code.Ret).ToArray();
            if (candidates.Length != 1)
                throw new InvalidDataException("Unsupported or already patched DHE compiler: ambiguous stack-pop branch.");
            int start = candidates[0];
            TypeRef codeReference = module.GetTypeRefs().Single(type => type.FullName == "Unity.IL2CPP.DataModel.Code");
            if (dataModel.Assembly.FullName != codeReference.DefinitionAssembly.FullName)
                throw new InvalidDataException("Compiler DataModel identity mismatch.");
            int popCode = Convert.ToInt32(dataModel.Find(codeReference.FullName, false).Fields
                .Single(field => field.Name == "Pop").Constant.Value);
            if (!instructions.Any(instruction => instruction.OpCode.Code == Code.Switch &&
                instruction.Operand is IList<Instruction> targets && popCode >= 0 && targets.Count > popCode &&
                ReferenceEquals(targets[popCode], instructions[start])))
                throw new InvalidDataException("Compiler stack-pop block is not the Code.Pop dispatch target.");
            uint offset = instructions[start].Offset;
            var unchangedBodies = module.GetTypes().SelectMany(type => type.Methods)
                .Where(method => method.HasBody && method != dispatch)
                .ToDictionary(method => method.FullName, BodyText);
            string[] references = module.GetAssemblyRefs().Select(reference => reference.FullName).ToArray();

            // Discarding a result must not discard its exception or volatile
            // semantics. Pure evaluations remain removable by the C++ compiler.
            var discarded = new Local(stackInfo.ToTypeSig());
            dispatch.Body.Variables.Add(discarded);
            dispatch.Body.SimplifyBranches();
            instructions[start + 3].OpCode = OpCodes.Stloc;
            instructions[start + 3].Operand = discarded;
            var concat = new MemberRefUser(module, "Concat", MethodSig.CreateStatic(module.CorLibTypes.String,
                module.CorLibTypes.String, module.CorLibTypes.String, module.CorLibTypes.String),
                module.CorLibTypes.String.TypeDefOrRef);
            Instruction[] emit =
            {
                Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, codeWriter),
                Instruction.Create(OpCodes.Ldstr, "(void)("),
                Instruction.Create(stackInfo.IsValueType ? OpCodes.Ldloca : OpCodes.Ldloc, discarded),
                Instruction.Create(OpCodes.Ldfld, expression), Instruction.Create(OpCodes.Ldstr, ")"),
                Instruction.Create(OpCodes.Call, concat), Instruction.Create(OpCodes.Callvirt, writeStatement),
            };
            for (int index = 0; index < emit.Length; index++) instructions.Insert(start + 4 + index, emit[index]);
            byte[] patched;
            using (var stream = new MemoryStream())
            {
                var options = new ModuleWriterOptions(module);
                options.MetadataOptions.Flags |= MetadataFlags.PreserveAll;
                module.Write(stream, options);
                patched = stream.ToArray();
            }
            using (var verified = ModuleDefMD.Load(patched))
            {
                if (!references.SequenceEqual(verified.GetAssemblyRefs().Select(reference => reference.FullName)))
                    throw new InvalidDataException("Compiler patch changed assembly dependencies.");
                foreach (MethodDef method in verified.GetTypes().SelectMany(type => type.Methods).Where(method => method.HasBody))
                    if (unchangedBodies.TryGetValue(method.FullName, out string before) && before != BodyText(method))
                        throw new InvalidDataException("Compiler patch changed another method: " + method.FullName);
                MethodDef changed = verified.Find(writer.FullName, false).Methods.Single(method => method.Name == dispatch.Name);
                if (changed.Body.Instructions.Count(instruction => instruction.OpCode.Code == Code.Ldstr &&
                        instruction.Operand is string value && value == "(void)(") != 1)
                    throw new InvalidDataException("Compiler patch emission marker mismatch.");
            }
            return new DheAotCompilerPatchResult
            {
                Bytes = patched, OriginalSha256 = Hash(original), PatchedSha256 = Hash(patched),
                DataModelSha256 = Hash(dataModelBytes), Method = dispatch.FullName,
                MethodToken = dispatch.MDToken.Raw, PopCode = popCode, OriginalOffset = offset,
                UnchangedMethodCount = unchangedBodies.Count,
            };
        }

        public static string Hash(byte[] bytes)
        {
            using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string BodyText(MethodDef method) =>
            method.Body.InitLocals + "\n" + string.Join(",", method.Body.Variables.Select(local => local.Type.FullName)) + "\n" +
            string.Join("\n", method.Body.Instructions.Select(instruction => instruction.ToString())) + "\n" +
            string.Join("\n", method.Body.ExceptionHandlers.Select(handler =>
                handler.HandlerType + "|" + handler.CatchType?.FullName + "|" + handler.TryStart?.Offset + "|" +
                handler.TryEnd?.Offset + "|" + handler.HandlerStart?.Offset + "|" + handler.HandlerEnd?.Offset + "|" + handler.FilterStart?.Offset));
    }

    public sealed class DheAotCompilerPatchResult
    {
        public byte[] Bytes;
        public string OriginalSha256;
        public string PatchedSha256;
        public string DataModelSha256;
        public string Method;
        public uint MethodToken;
        public int PopCode;
        public uint OriginalOffset;
        public int UnchangedMethodCount;
    }
}
