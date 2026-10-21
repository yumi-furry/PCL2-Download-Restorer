using System;
using System.IO;
using System.Reflection;

class Harness
{
    static void Main(string[] args)
    {
        try
        {
            var asm = Assembly.LoadFrom(args[0]);
            var t = asm.GetType("PatchRunner");
            var run = t.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            Action<string> logger = s => { Console.WriteLine(s); File.AppendAllText("harness.log", s + "\r\n"); };
            Func<string, bool> confirm = s => false;
            run.Invoke(null, new object[] { args[1], logger, confirm });
            Console.WriteLine("HARNESS DONE");
        }
        catch (Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            for (var e = ex; e != null; e = e.InnerException)
            {
                string msg;
                try { msg = e.Message; } catch { msg = "<msg fail>"; }
                sb.AppendLine(e.GetType().FullName + ": " + msg);
                sb.AppendLine(e.StackTrace);
                sb.AppendLine("---");
            }
            File.WriteAllText("harness-err.txt", sb.ToString());
            Console.WriteLine("HARNESS FAIL, see harness-err.txt");
        }
    }
}
