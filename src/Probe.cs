using System;
using System.Linq;

class Probe
{
    static void Main(string[] args)
    {
        var mod = dnlib.DotNet.ModuleDefMD.Load(args[0]);
        var t = mod.GetTypes().First(x => x.Name.String == args[1]);
        Console.WriteLine("TYPE: " + t.FullName);
        foreach (var f in t.Fields) Console.WriteLine("FIELD: " + f.FieldSig.Type.FullName + " " + f.Name.String);
        foreach (var m in t.Methods)
        {
            var ps = m.MethodSig != null ? m.MethodSig.Params.Count : -1;
            Console.WriteLine("METHOD: " + m.Name.String + " params(sig)=" + ps + " params(def)=" + m.Parameters.Count + " static=" + m.IsStatic + " body=" + m.HasBody);
        }
        // 也列出基类
        Console.WriteLine("BASE: " + (t.BaseType != null ? t.BaseType.FullName : "?"));
    }
}
