using System;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

class Dumper
{
    static ModuleDefMD mod;

    static void Main(string[] args)
    {
        mod = ModuleDefMD.Load(args[0]);
        string typeName = args[1];
        string methodName = args.Length > 2 ? args[2] : null;
        foreach (var type in mod.GetTypes())
        {
            if (!string.Equals(type.Name, typeName, StringComparison.Ordinal)) continue;
            foreach (var m in type.Methods)
            {
                if (methodName != null && !string.Equals(m.Name, methodName, StringComparison.Ordinal)) continue;
                if (!m.HasBody) continue;
                Console.WriteLine("=== " + type.FullName + "::" + m.Name + " " + m.FullName.Split(new[] { "::" }, StringSplitOptions.None).Last());
                int i = 0;
                foreach (var ins in m.Body.Instructions)
                {
                    Console.WriteLine(String.Format("{0:D4} IL_{1:X4} {2}", i, ins.Offset, Fmt(ins)));
                    i++;
                }
                Console.WriteLine();
            }
        }
    }

    static string Fmt(Instruction ins)
    {
        var op = ins.Operand;
        string s;
        if (op == null) s = "";
        else if (op is Instruction) s = "-> IL_" + ((Instruction)op).Offset.ToString("X4");
        else if (op is Instruction[]) s = "-> [" + string.Join(",", ((Instruction[])op).Select(x => "IL_" + x.Offset.ToString("X4"))) + "]";
        else if (op is string) s = "\"" + ((string)op).Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        else s = op.ToString();
        return ins.OpCode.Name + " " + s;
    }
}
