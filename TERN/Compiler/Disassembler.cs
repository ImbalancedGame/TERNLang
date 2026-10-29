using System;
using System.IO;
using TERN.Runtime;

namespace TERN.Compiler
{
    /// <summary>Human readable bytecode listing, handy when tuning the compiler output.</summary>
    public static class Disassembler
    {
        public static void Dump(FunctionProto proto, TextWriter output) => Dump(proto, output, 0);

        private static void Dump(FunctionProto proto, TextWriter output, int depth)
        {
            var pad = new string(' ', depth * 2);

            output.WriteLine($"{pad}=== {proto.Name ?? "<anonymous>"} " +
                             $"(params={proto.ParamCount}, slots={proto.LocalCount}, stack={proto.MaxStack}, " +
                             $"constants={proto.Constants.Length}) ===");

            for (var i = 0; i < proto.Instructions.Length; i++)
            {
                var instruction = proto.Instructions[i];
                var line = i < proto.Lines.Length ? proto.Lines[i] : 0;
                output.WriteLine($"{pad}  {i,4}  L{line,-4} {Describe(instruction, proto)}");
            }

            foreach (var child in proto.Prototypes)
            {
                if (child is not null)
                {
                    Dump(child, output, depth + 1);
                }
            }
        }

        private static string Describe(Instr instruction, FunctionProto proto)
        {
            var operand = instruction.Op switch
            {
                Op.LoadConst or Op.LoadGlobal or Op.AddLocalConst or Op.SubLocalConst or Op.MulLocalConst
                    or Op.DivLocalConst or Op.ModLocalConst or Op.NewInstance or Op.LoadField or Op.StoreField
                    or Op.Invoke or Op.LoadModule => $"#{instruction.A} {Constant(proto, instruction.A)}",
                _ => string.Empty,
            };

            var text = instruction.ToString();

            return operand.Length > 0 ? $"{text,-28} ; {operand}" : text;
        }

        private static string Constant(FunctionProto proto, int index)
        {
            if (index < 0 || index >= proto.Constants.Length)
            {
                return string.Empty;
            }

            var value = proto.Constants[index];
            var display = value.ToDebugString();
            return display.Length > 40 ? display[..40] + "..." : display;
        }
    }
}
