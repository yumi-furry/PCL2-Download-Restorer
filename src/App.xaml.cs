using System;
using System.Reflection;
using System.Windows;

public partial class App : Application
{
    public App()
    {
        // dnlib / Mono.Cecil 均为内嵌资源，解析时从资源加载（必须在任何相关类型被 JIT 前注册）
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string resName = null;
            if (e.Name.StartsWith("dnlib,", StringComparison.Ordinal)) resName = "dnlib.dll";
            else if (e.Name.StartsWith("Mono.Cecil,", StringComparison.Ordinal)) resName = "Mono.Cecil.dll";
            if (resName == null) return null;
            var self = Assembly.GetExecutingAssembly();
            using (var st = self.GetManifestResourceStream(resName))
            {
                if (st == null) return null;
                var buf = new byte[st.Length];
                st.Read(buf, 0, buf.Length);
                return Assembly.Load(buf);
            }
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        new MainWindow().Show();
    }
}
