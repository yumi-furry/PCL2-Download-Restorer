using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

static class Program
{
    [STAThread]
    static void Main()
    {
        // dnlib 嵌入资源，解析时从资源加载（必须在任何 dnlib 类型被 JIT 前注册）
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            if (!e.Name.StartsWith("dnlib,", StringComparison.Ordinal)) return null;
            var self = Assembly.GetExecutingAssembly();
            using (var st = self.GetManifestResourceStream("dnlib.dll"))
            {
                if (st == null) return null;
                var buf = new byte[st.Length];
                st.Read(buf, 0, buf.Length);
                return Assembly.Load(buf);
            }
        };
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}

class MainForm : Form
{
    TextBox txtPath;
    TextBox txtLog;
    Button btnPatch;
    Button btnRestore;

    public MainForm()
    {
        Text = "PCL2 下载功能一键补丁";
        Size = new Size(640, 460);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);

        var lab1 = new Label { Text = "PCL2 主程序路径（可带引号粘贴）：", Left = 12, Top = 14, Width = 300, AutoSize = false };
        txtPath = new TextBox { Left = 12, Top = 36, Width = 500, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        var btnBrowse = new Button { Text = "浏览…", Left = 520, Top = 34, Width = 90, Anchor = AnchorStyles.Right | AnchorStyles.Top };
        btnBrowse.Click += (s, e) =>
        {
            using (var dlg = new OpenFileDialog { Filter = "PCL2 主程序|*.exe", Title = "选择 Plain Craft Launcher 2.exe" })
                if (dlg.ShowDialog() == DialogResult.OK) txtPath.Text = dlg.FileName;
        };
        btnPatch = new Button { Text = "一键补丁", Left = 12, Top = 70, Width = 294, Height = 34, Anchor = AnchorStyles.Left | AnchorStyles.Top, Font = new Font(Font, FontStyle.Bold) };
        btnPatch.Click += BtnPatch_Click;
        btnRestore = new Button { Text = "一键还原", Left = 316, Top = 70, Width = 294, Height = 34, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Font = new Font(Font, FontStyle.Bold) };
        btnRestore.Click += BtnRestore_Click;
        txtLog = new TextBox { Left = 12, Top = 114, Width = 598, Height = 270, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom, Font = new Font("Consolas", 9F) };
        var lab2 = new Label { Text = "说明：补丁前自动备份原文件为 同名.exe.bak；仅适配 PCL2 2.99.0.0，其它版本会被安全检查拦截且不会写坏文件。", Left = 12, Top = 392, Width = 598, Height = 32, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom, ForeColor = Color.Gray };

        Controls.Add(lab1); Controls.Add(txtPath); Controls.Add(btnBrowse);
        Controls.Add(btnPatch); Controls.Add(btnRestore); Controls.Add(txtLog); Controls.Add(lab2);

        string def = @"D:\Program Files\PCL2\Plain Craft Launcher 2.exe";
        if (File.Exists(def)) txtPath.Text = def;
    }

    void Log(string s)
    {
        txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s + Environment.NewLine);
    }

    void BtnPatch_Click(object sender, EventArgs e)
    {
        txtLog.Clear();
        btnPatch.Enabled = false;
        Cursor = Cursors.WaitCursor;
        try
        {
            string path = txtPath.Text.Trim().Trim('"', '\'');
            PatchRunner.Run(path, Log, msg => MessageBox.Show(this, msg, "需要确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes);
            Log("全部完成！可以启动 PCL2 验证下载功能。");
        }
        catch (Exception ex)
        {
            string msg;
            try { msg = ex.Message; } catch { msg = ex.GetType().Name; }
            Log("失败：" + msg);
            Log("未写入任何内容，原文件未受影响。");
            MessageBox.Show(this, "补丁失败：" + msg, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            btnPatch.Enabled = true;
            Cursor = Cursors.Default;
        }
    }

    void BtnRestore_Click(object sender, EventArgs e)
    {
        txtLog.Clear();
        btnPatch.Enabled = false;
        btnRestore.Enabled = false;
        Cursor = Cursors.WaitCursor;
        try
        {
            string path = txtPath.Text.Trim().Trim('"', '\'');
            PatchRunner.Restore(path, Log, msg => MessageBox.Show(this, msg, "需要确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes);
            Log("还原完成！PCL2 已恢复为官方原版。");
        }
        catch (Exception ex)
        {
            string msg;
            try { msg = ex.Message; } catch { msg = ex.GetType().Name; }
            Log("失败：" + msg);
            MessageBox.Show(this, "还原失败：" + msg, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            btnPatch.Enabled = true;
            btnRestore.Enabled = true;
            Cursor = Cursors.Default;
        }
    }
}

// ============ 补丁核心（与验证过的控制台版逻辑一致） ============
static class PatchRunner
{
    static dnlib.DotNet.ModuleDefMD mod;
    static List<dnlib.DotNet.Emit.CilBody> touched = new List<dnlib.DotNet.Emit.CilBody>();
    const string LAW = "为遵守相关法律法规，PCL 暂时不再提供 MC 下载功能，非常抱歉 qwq……";
    static Action<string> log;

    public static void Run(string path, Action<string> logger, Func<string, bool> confirm)
    {
        log = logger;
        if (string.IsNullOrWhiteSpace(path)) Fail("路径为空。");
        path = Environment.ExpandEnvironmentVariables(path);
        if (!File.Exists(path)) Fail("文件不存在：" + path);
        Log("目标文件：" + path);

        // 重置静态状态，避免跨次运行污染（bridgeType/touched 等静态字段在第一次补丁后仍持有旧模块引用）
        mod = null;
        touched.Clear();
        bridgeType = null;
        bridgeLicClick = null;
        bridgeAnnounce = null;

        mod = dnlib.DotNet.ModuleDefMD.Load(File.ReadAllBytes(path));
        string asmName = mod.Assembly != null ? mod.Assembly.Name.String : "";
        string ver = mod.Assembly != null ? mod.Assembly.Version.ToString() : "";
        Log("程序集：" + asmName + "  版本：" + ver);
        if (asmName != "Plain Craft Launcher 2") Fail("这不是 PCL2 主程序（程序集名：" + asmName + "）。");
        if (ver != "2.99.0.0") Log("警告：当前补丁针对 2.99.0.0 验证，此版本可能不匹配；若失败会自动中止且不会写坏文件。");

        PatchA(); PatchB(); PatchE(); PatchC(); PatchD(); PatchF(); PatchG();

        foreach (var b in touched) { b.SimplifyBranches(); b.OptimizeBranches(); }
        string tmp = Path.Combine(Path.GetTempPath(), "pcl2patch_" + Guid.NewGuid().ToString("N") + ".exe");
        mod.Write(tmp);
        Log("IL 重写完成，临时文件已生成。");

        string bak = path + ".bak";
        if (!File.Exists(bak)) { File.Copy(path, bak); Log("已备份原文件：" + bak); }
        else Log("备份已存在，跳过：" + bak);

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                File.Copy(tmp, path, true);
                Log("已写入：" + path);
                break;
            }
            catch (IOException)
            {
                if (attempt == 3) { TryDelete(tmp); Fail("目标文件被占用，写入失败。请关闭 PCL2 后重试。"); }
                var lockers = FindLockers(path);
                if (lockers.Count == 0 || !confirm("PCL2 正在运行，需要结束进程才能写入。是否立即结束？"))
                    Fail("文件被占用，用户取消。");
                foreach (var p in lockers) { try { p.Kill(); p.WaitForExit(5000); } catch { } }
                Thread.Sleep(800);
            }
        }
        TryDelete(tmp);
    }

    // ---------- 一键还原 ----------
    public static void Restore(string path, Action<string> logger, Func<string, bool> confirm)
    {
        log = logger;
        if (string.IsNullOrWhiteSpace(path)) Fail("路径为空。");
        path = Environment.ExpandEnvironmentVariables(path);
        if (!File.Exists(path)) Fail("文件不存在：" + path);
        string bak = path + ".bak";
        if (!File.Exists(bak)) Fail("未找到备份文件，无法还原：\r\n" + bak + "\r\n（备份在首次打补丁时自动创建）");
        string name;
        using (var m2 = dnlib.DotNet.ModuleDefMD.Load(File.ReadAllBytes(bak)))
            name = m2.Assembly != null ? m2.Assembly.Name.String : "";
        if (name != "Plain Craft Launcher 2") Fail("备份文件不是 PCL2 主程序，已中止：" + bak);
        Log("备份校验通过：" + bak);
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                File.Copy(bak, path, true);
                Log("已用备份覆盖：" + path);
                break;
            }
            catch (IOException)
            {
                if (attempt == 3) Fail("目标文件被占用，还原失败。请关闭 PCL2 后重试。");
                var lockers = FindLockers(path);
                if (lockers.Count == 0 || !confirm("PCL2 正在运行，需要结束进程才能还原。是否立即结束？"))
                    Fail("文件被占用，用户取消。");
                foreach (var p in lockers) { try { p.Kill(); p.WaitForExit(5000); } catch { } }
                Thread.Sleep(800);
            }
        }
    }

    static List<Process> FindLockers(string path)
    {
        var list = new List<Process>();
        foreach (var p in Process.GetProcessesByName("Plain Craft Launcher 2"))
        {
            try { if (string.Equals(p.MainModule.FileName, path, StringComparison.OrdinalIgnoreCase)) list.Add(p); }
            catch { }
        }
        return list;
    }

    static void TryDelete(string f) { try { File.Delete(f); } catch { } }
    static void Fail(string msg) { throw new Exception(msg); }
    static void Log(string s) { log(s); }

    // ---------- Patch A ----------
    static void PatchA()
    {
        var m = FindType("PageDownloadInstall").Methods.First(x => S(x.Name) == "LoaderInit");
        var ins = m.Body.Instructions;
        int i0 = IndexOf(ins, x => x.OpCode == dnlib.DotNet.Emit.OpCodes.Ldstr && (string)x.Operand == "关闭下载");
        Assert(i0 >= 0, "A: 未找到锚点字符串");
        int i1 = -1;
        for (int i = i0; i < ins.Count; i++)
            if (ins[i].OpCode == dnlib.DotNet.Emit.OpCodes.Newobj && S(((dnlib.DotNet.IMethod)ins[i].Operand).DeclaringType.Name).StartsWith("LoaderTask")) { i1 = i; break; }
        Assert(i1 > i0, "A: 未找到 LoaderTask 构造");
        AssertNoRefInto(m, i0, i1, "A");
        var fld = FindType("ModDownload").Fields.First(f => S(f.Name) == "DlClientListLoader");
        ins[i0] = new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldsfld, fld);
        for (int i = i1; i >= i0 + 1; i--) ins.RemoveAt(i);
        m.Body.MaxStack = 16;
        touched.Add(m.Body);
        Log("A OK：下载页加载器已接回 DlClientListLoader");
    }

    // ---------- Patch B ----------
    static void PatchB()
    {
        int found = 0;
        foreach (var m in FindType("PageDownloadModify").Methods)
        {
            if (!m.HasBody) continue;
            var ins = m.Body.Instructions;
            for (int i = 0; i < ins.Count; i++)
            {
                if (!IsCallTo(ins[i], "get_CardMinecraft")) continue;
                for (int j = i + 1; j + 1 < ins.Count && j <= i + 4; j++)
                {
                    if (ins[j].OpCode == dnlib.DotNet.Emit.OpCodes.Ldc_I4_2 && IsCallTo(ins[j + 1], "set_Visibility"))
                    {
                        ins[j] = new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldc_I4_0);
                        found++;
                        break;
                    }
                }
            }
        }
        Assert(found == 1, "B: 预期 1 处可见性补丁，实际 " + found);
        Log("B OK：修改页 MC 卡片恢复显示");
    }

    // ---------- Patch E ----------
    static void PatchE()
    {
        var t = FindType("PageDownloadResourceDetail");
        var m = t.Methods.First(x => S(x.Name) == "InitializeStackResourceVersions");
        var ins = m.Body.Instructions;
        int c = IndexOf(ins, x => IsCallTo(x, "ToListItem"));
        Assert(c >= 0, "E: 未找到 ToListItem 调用");
        int s0 = -1;
        for (int i = c; i > c - 15 && i >= 0; i--)
            if (ins[i].OpCode == dnlib.DotNet.Emit.OpCodes.Ldarg_1 && IsCallTo(ins[i + 1], "get_Children")) { s0 = i; break; }
        Assert(s0 >= 0, "E: 未找到分支起点");
        Assert(c == s0 + 8, "E: 代码布局不符");
        Assert(ins[c + 2].OpCode == dnlib.DotNet.Emit.OpCodes.Pop, "E: 预期 pop 指令");
        AssertNoRefInto(m, s0, c + 2, "E");
        var trt = t.Fields.First(f => S(f.Name) == "TargetResourceType");
        var installClick = t.Methods.First(x => S(x.Name) == "Install_Click");
        var saveClick = t.Methods.First(x => S(x.Name) == "Save_Click");
        var toListItem = FindType("ResourceVersion").Methods.First(x => S(x.Name) == "ToListItem" && x.Parameters.Count >= 3);
        var iconClickRef = toListItem.MethodSig.Params[1].ToTypeDefOrRef();
        var iconCtor = new dnlib.DotNet.MemberRefUser(mod, ".ctor",
            dnlib.DotNet.MethodSig.CreateInstance(mod.CorLibTypes.Void, mod.CorLibTypes.Object, mod.CorLibTypes.IntPtr), iconClickRef);
        var elseStart = ins[s0];
        var endNext = ins[c + 3];
        var block = new List<dnlib.DotNet.Emit.Instruction>();
        block.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldarg_0));
        block.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldfld, trt));
        block.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldc_I4_2));
        block.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Bne_Un, elseStart));
        block.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldarg_1));
        block.Add(Clone(ins[s0 + 1]));
        block.Add(Clone(ins[s0 + 2]));
        block.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldarg_0));
        block.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldftn, installClick));
        block.Add(Clone(ins[s0 + 5]));
        block.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldarg_0));
        block.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldftn, saveClick));
        block.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Newobj, iconCtor));
        block.Add(Clone(ins[s0 + 7]));
        block.Add(Clone(ins[c]));
        block.Add(Clone(ins[c + 1]));
        block.Add(Clone(ins[c + 2]));
        block.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Br, endNext));
        for (int k = block.Count - 1; k >= 0; k--) ins.Insert(s0, block[k]);
        m.Body.MaxStack = 16;
        touched.Add(m.Body);
        Log("E OK：资源页整合包安装按钮恢复");
    }

    // ---------- Patch C ----------
    static void PatchC()
    {
        var mt = FindType("ModModpack");
        var m = mt.Methods.First(x => S(x.Name) == "ModpackInstall" && x.Parameters.Count == 3);
        var body = m.Body;
        var ins = body.Instructions;
        dnlib.DotNet.Emit.Local archive = null, abf = null, packType = null;
        for (int i = 0; i + 1 < ins.Count; i++)
        {
            if (IsCallTo(ins[i], "OpenZip") && IsStloc(ins[i + 1])) archive = StlocLocal(ins[i + 1], body.Variables);
            if (abf == null && ins[i].OpCode == dnlib.DotNet.Emit.OpCodes.Ldstr && (string)ins[i].Operand == "" && IsStloc(ins[i + 1])) abf = StlocLocal(ins[i + 1], body.Variables);
            if (packType == null && ins[i].OpCode == dnlib.DotNet.Emit.OpCodes.Ldc_I4_M1 && IsStloc(ins[i + 1])) packType = StlocLocal(ins[i + 1], body.Variables);
        }
        Assert(archive != null && abf != null && packType != null, "C: 局部变量解析失败");
        int[] caseVals = { 0, 1, 2, 3, 4, 9 };
        string[] caseNames = { "InstallPackCurseForge", "InstallPackHMCL", "InstallPackMMC", "InstallPackMCBBS", "InstallPackModrinth", "InstallPackLauncherPack" };
        int[] caseArgs = { 5, 3, 3, 4, 5, 3 };
        var caseMethods = new dnlib.DotNet.MethodDef[caseNames.Length];
        for (int i = 0; i < caseNames.Length; i++)
        {
            caseMethods[i] = mt.Methods.First(x => S(x.Name) == caseNames[i]);
            Assert(caseMethods[i].Parameters.Count == caseArgs[i], "C: " + caseNames[i] + " 参数数不符");
        }
        var compress = mt.Methods.First(x => S(x.Name) == "InstallPackCompress");
        Assert(compress.Parameters.Count == 2, "C: InstallPackCompress 参数数不符");
        var retSig = caseMethods[0].ReturnType;
        foreach (var cm in caseMethods) Assert(cm.ReturnType.FullName == retSig.FullName, "C: 返回类型不一致");
        Assert(compress.ReturnType.FullName == retSig.FullName, "C: Compress 返回类型不一致");
        int r0 = IndexOf(ins, x => x.OpCode == dnlib.DotNet.Emit.OpCodes.Ldstr && (string)x.Operand == LAW);
        Assert(r0 >= 0, "C: 未找到限制语句");
        Assert(ins[r0 + 1].OpCode == dnlib.DotNet.Emit.OpCodes.Newobj, "C: 预期 newobj");
        Assert(ins[r0 + 2].OpCode == dnlib.DotNet.Emit.OpCodes.Throw, "C: 预期 throw");
        var rTarget = ins[r0]; var rMid = ins[r0 + 1]; var rEnd = ins[r0 + 2];
        var retV = new dnlib.DotNet.Emit.Local(retSig);
        body.Variables.Add(retV);
        var RET = new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldloc, retV);
        Func<string, int, List<dnlib.DotNet.Emit.Instruction>> buildBody = (name, argn) =>
        {
            var l = new List<dnlib.DotNet.Emit.Instruction>();
            l.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldarg, m.Parameters[0]));
            l.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldloc, archive));
            if (argn >= 3) l.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldloc, abf));
            if (argn >= 4) l.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldarg, m.Parameters[1]));
            if (argn >= 5) l.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldarg, m.Parameters[2]));
            l.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Call, name == "InstallPackCompress" ? (dnlib.DotNet.MethodDef)compress : caseMethods[Array.IndexOf(caseNames, name)]));
            l.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Stloc, retV));
            l.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Leave, RET));
            return l;
        };
        var cmpStarts = new dnlib.DotNet.Emit.Instruction[caseVals.Length];
        var bodies = new List<dnlib.DotNet.Emit.Instruction>[caseVals.Length];
        for (int i = 0; i < caseVals.Length; i++)
        {
            cmpStarts[i] = new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldloc, packType);
            bodies[i] = buildBody(caseNames[i], caseArgs[i]);
        }
        var defBody = buildBody("InstallPackCompress", 2);
        var code = new List<dnlib.DotNet.Emit.Instruction>();
        for (int i = 0; i < caseVals.Length; i++)
        {
            code.Add(cmpStarts[i]);
            code.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldc_I4, caseVals[i]));
            code.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Bne_Un, i + 1 < caseVals.Length ? cmpStarts[i + 1] : defBody[0]));
            code.AddRange(bodies[i]);
        }
        code.AddRange(defBody);
        for (int i = 0; i < ins.Count; i++)
        {
            if (i >= r0 && i <= r0 + 2) continue;
            var op = ins[i].Operand;
            if (op is dnlib.DotNet.Emit.Instruction)
            {
                var tgt = (dnlib.DotNet.Emit.Instruction)op;
                Assert(!ReferenceEquals(tgt, rMid) && !ReferenceEquals(tgt, rEnd), "C: 存在指向 throw 中部的分支");
                if (ReferenceEquals(tgt, rTarget)) ins[i].Operand = code[0];
            }
            else if (op is dnlib.DotNet.Emit.Instruction[])
            {
                var arr = (dnlib.DotNet.Emit.Instruction[])op;
                for (int k = 0; k < arr.Length; k++)
                {
                    Assert(!ReferenceEquals(arr[k], rMid) && !ReferenceEquals(arr[k], rEnd), "C: 存在指向 throw 中部的 switch");
                    if (ReferenceEquals(arr[k], rTarget)) arr[k] = code[0];
                }
            }
        }
        foreach (var eh in body.ExceptionHandlers)
        {
            if (ReferenceEquals(eh.TryStart, rTarget)) eh.TryStart = code[0];
            if (ReferenceEquals(eh.TryEnd, rTarget)) eh.TryEnd = code[0];
            if (ReferenceEquals(eh.HandlerStart, rTarget)) eh.HandlerStart = code[0];
            if (ReferenceEquals(eh.HandlerEnd, rTarget)) eh.HandlerEnd = code[0];
            Assert(!ReferenceEquals(eh.TryStart, rMid) && !ReferenceEquals(eh.TryEnd, rMid)
                && !ReferenceEquals(eh.HandlerStart, rMid) && !ReferenceEquals(eh.HandlerEnd, rMid)
                && !ReferenceEquals(eh.TryStart, rEnd) && !ReferenceEquals(eh.TryEnd, rEnd)
                && !ReferenceEquals(eh.HandlerStart, rEnd) && !ReferenceEquals(eh.HandlerEnd, rEnd), "C: 异常表边界异常");
        }
        ins.RemoveAt(r0 + 2);
        ins.RemoveAt(r0 + 1);
        ins[r0] = code[0];
        for (int k = code.Count - 1; k >= 1; k--) ins.Insert(r0 + 1, code[k]);
        ins.Add(RET);
        ins.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ret));
        body.MaxStack = 16;
        touched.Add(body);
        Log("C OK：整合包安装分发重建（" + code.Count + " 条指令）");
    }

    // ---------- Patch D ----------
    static void PatchD()
    {
        var mt = FindType("ModModpack");
        var sub = mt.Methods.First(x => S(x.Name) == "ModpackInstall" && x.Parameters.Count == 0);
        var func3 = mt.Methods.First(x => S(x.Name) == "ModpackInstall" && x.Parameters.Count == 3);
        dnlib.DotNet.IMethod selectFile = null, oneOf = null, nullableCtor = null, tupleCtor = null, firstOrDefault = null, isNullOrEmpty = null, getMessage = null;
        dnlib.DotNet.ITypeDefOrRef tupleType = null, oceRef = null, exRef = null;
        foreach (var t in mod.GetTypes())
        foreach (var m2 in t.Methods)
        {
            if (!m2.HasBody) continue;
            var ins2 = m2.Body.Instructions;
            for (int i = 0; i < ins2.Count; i++)
            {
                var op = ins2[i].Operand;
                if (selectFile == null && IsCallTo(ins2[i], "SelectFile") && i >= 4 && i + 4 < ins2.Count
                    && IsCallTo(ins2[i - 2], "op_Implicit") && ins2[i - 1].OpCode == dnlib.DotNet.Emit.OpCodes.Newobj
                    && ins2[i - 3].OpCode == dnlib.DotNet.Emit.OpCodes.Stelem && ins2[i - 4].OpCode == dnlib.DotNet.Emit.OpCodes.Newobj
                    && S(((dnlib.DotNet.IMethod)ins2[i - 4].Operand).DeclaringType.FullName).Contains("ValueTuple")
                    && IsCallTo(ins2[i + 1], "FirstOrDefault") && IsCallTo(ins2[i + 4], "IsNullOrEmpty"))
                {
                    selectFile = (dnlib.DotNet.IMethod)op;
                    oneOf = (dnlib.DotNet.IMethod)ins2[i - 2].Operand;
                    nullableCtor = (dnlib.DotNet.IMethod)ins2[i - 1].Operand;
                    tupleType = (dnlib.DotNet.ITypeDefOrRef)ins2[i - 3].Operand;
                    tupleCtor = (dnlib.DotNet.IMethod)ins2[i - 4].Operand;
                    firstOrDefault = (dnlib.DotNet.IMethod)ins2[i + 1].Operand;
                    isNullOrEmpty = (dnlib.DotNet.IMethod)ins2[i + 4].Operand;
                }
                if (ins2[i].OpCode == dnlib.DotNet.Emit.OpCodes.Newobj && op is dnlib.DotNet.IMethod)
                {
                    var dt = ((dnlib.DotNet.IMethod)op).DeclaringType;
                    if (oceRef == null && S(dt.FullName) == "System.OperationCanceledException") oceRef = dt;
                    if (exRef == null && S(dt.FullName) == "System.Exception") exRef = dt;
                }
                if (getMessage == null && (ins2[i].OpCode == dnlib.DotNet.Emit.OpCodes.Callvirt || ins2[i].OpCode == dnlib.DotNet.Emit.OpCodes.Call)
                    && op is dnlib.DotNet.IMethod && S(((dnlib.DotNet.IMethod)op).Name) == "get_Message" && S(((dnlib.DotNet.IMethod)op).DeclaringType.FullName) == "System.Exception")
                    getMessage = (dnlib.DotNet.IMethod)op;
            }
        }
        Assert(selectFile != null, "D: 未找到 SelectFile 供体");
        Assert(oceRef != null && exRef != null, "D: 未找到异常类型引用");
        Assert(getMessage != null, "D: 未找到 Exception.get_Message");
        var hint = FindType("ModMain").Methods.First(x => S(x.Name) == "Hint" && x.Parameters.Count == 3);
        var body = sub.Body;
        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        var fileL = new dnlib.DotNet.Emit.Local(mod.CorLibTypes.String);
        var exL = new dnlib.DotNet.Emit.Local(exRef.ToTypeSig());
        body.Variables.Add(fileL);
        body.Variables.Add(exL);
        var END = new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ret);
        var TRY = new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldloc, fileL);
        var HANDLER = new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Stloc, exL);
        var OCEEXIT = new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Leave, END);
        var list = body.Instructions;
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldstr, "选择整合包压缩文件"));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldc_I4_0));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldnull));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldc_I4_1));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Newarr, tupleType));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Dup));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldc_I4_0));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldc_I4_3));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Newarr, mod.CorLibTypes.String.TypeDefOrRef));
        string[] exts = { "rar", "zip", "mrpack" };
        for (int i = 0; i < exts.Length; i++)
        {
            list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Dup));
            list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldc_I4, i));
            list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldstr, exts[i]));
            list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Stelem_Ref));
        }
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldstr, "整合包文件"));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Newobj, tupleCtor));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Stelem, tupleType));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Call, oneOf));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Newobj, nullableCtor));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Call, selectFile));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Call, firstOrDefault));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Stloc, fileL));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldloc, fileL));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Call, isNullOrEmpty));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Brtrue, END));
        list.Add(TRY);
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldnull));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldnull));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Call, func3));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Pop));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Leave, END));
        list.Add(HANDLER);
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldloc, exL));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Isinst, oceRef));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Brtrue, OCEEXIT));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldloc, exL));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Callvirt, getMessage));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldc_I4_2));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldc_I4_1));
        list.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Call, hint));
        list.Add(OCEEXIT);
        list.Add(END);
        body.ExceptionHandlers.Add(new dnlib.DotNet.Emit.ExceptionHandler(dnlib.DotNet.Emit.ExceptionHandlerType.Catch)
        {
            TryStart = TRY,
            TryEnd = HANDLER,
            HandlerStart = HANDLER,
            HandlerEnd = END,
            CatchType = exRef
        });
        body.MaxStack = 16;
        body.InitLocals = true;
        touched.Add(body);
        Log("D OK：整合包安装入口重建");
    }

    // ---------- Patch F/G：新桥接类型 ----------
    const string LIC_URL = "https://shimo.im/docs/rGrd8pY8xWkt6ryW#anchor-X0bo";
    const string ABOUT_L1 = "程序已通过注入脚本解锁Minecraft下载，使用时如有其他BUG请勿反馈至官方仓库。";
    const string ABOUT_L2A = "本脚本仅供学习使用，使用者自行承担使用责任，并遵守《";
    const string ABOUT_LINK = "PCL 分发有限许可";
    const string ABOUT_L2B = "》";
    const string ANNOUNCE = "PCL程序已通过脚本解锁Minecraft下载等功能，使用时如有其他BUG请勿反馈至官方仓库。脚本仅供学习使用，使用者自行承担使用责任。脚本使用、下载均完全免费，如果花了钱，就去骂他！";
    static dnlib.DotNet.Importer imp;
    static dnlib.DotNet.TypeDef bridgeType;
    static dnlib.DotNet.MethodDef bridgeLicClick, bridgeAnnounce;

    static void EnsureBridge()
    {
        if (bridgeType != null) return;
        imp = new dnlib.DotNet.Importer(mod, dnlib.DotNet.ImporterOptions.TryToUseExistingAssemblyRefs);
        bridgeType = new dnlib.DotNet.TypeDefUser("", "PCLPatchBridge", mod.CorLibTypes.Object.TypeDefOrRef);
        bridgeType.Attributes = dnlib.DotNet.TypeAttributes.Public | dnlib.DotNet.TypeAttributes.Abstract | dnlib.DotNet.TypeAttributes.Sealed;
        mod.Types.Add(bridgeType);
        // OnLicClick(sender, e) : OpenWebsite(LIC_URL)
        var openWebsite = FindMethodByName("OpenWebsite", 1);
        var sig1 = dnlib.DotNet.MethodSig.CreateStatic(mod.CorLibTypes.Void, mod.CorLibTypes.Object, imp.ImportAsTypeSig(typeof(System.Windows.RoutedEventArgs)));
        bridgeLicClick = new dnlib.DotNet.MethodDefUser("OnLicClick", sig1,
            dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static | dnlib.DotNet.MethodAttributes.HideBySig);
        bridgeLicClick.Body = new dnlib.DotNet.Emit.CilBody();
        var b1 = bridgeLicClick.Body.Instructions;
        b1.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldstr, LIC_URL));
        b1.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Call, openWebsite));
        b1.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ret));
        bridgeLicClick.Body.MaxStack = 8;
        bridgeType.Methods.Add(bridgeLicClick);
        // ShowAnnounceOnce() : 注册表守卫 + MyMsgBox 公告
        var regGet = imp.Import(typeof(Microsoft.Win32.Registry).GetMethod("GetValue", new Type[] { typeof(string), typeof(string), typeof(object) }));
        var regSet = imp.Import(typeof(Microsoft.Win32.Registry).GetMethod("SetValue", new Type[] { typeof(string), typeof(string), typeof(object) }));
        var msgbox = FindMethodByName("MyMsgBox", 11);
        bridgeAnnounce = new dnlib.DotNet.MethodDefUser("ShowAnnounceOnce", dnlib.DotNet.MethodSig.CreateStatic(mod.CorLibTypes.Void),
            dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static | dnlib.DotNet.MethodAttributes.HideBySig);
        bridgeAnnounce.Body = new dnlib.DotNet.Emit.CilBody();
        var b = bridgeAnnounce.Body.Instructions;
        var RET = new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ret);
        const string REGKEY = "HKEY_CURRENT_USER\\Software\\PCL2Patch";
        const string REGNAME = "AnnounceShownV1";
        b.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldstr, REGKEY));
        b.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldstr, REGNAME));
        b.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldnull));
        b.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Call, regGet));
        b.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Brtrue, RET));
        b.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldstr, REGKEY));
        b.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldstr, REGNAME));
        b.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldstr, "1"));
        b.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Call, regSet));
        b.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldstr, ANNOUNCE));
        b.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldstr, "公告"));
        b.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldstr, "确定"));
        b.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldstr, ""));
        b.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldstr, ""));
        b.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldc_I4_0));
        b.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldc_I4_1));
        b.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldc_I4_0));
        b.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldnull));
        b.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldnull));
        b.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Ldnull));
        b.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Call, msgbox));
        b.Add(new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Pop));
        b.Add(RET);
        bridgeAnnounce.Body.MaxStack = 16;
        bridgeType.Methods.Add(bridgeAnnounce);
    }

    // ---------- Patch F：关于页底部红字说明 ----------
    static void PatchF()
    {
        EnsureBridge();
        var t = FindType("PageOtherAbout");
        var m = t.Methods.First(x => S(x.Name) == "PageOtherAbout_Loaded");
        Assert(m.MethodSig != null && m.MethodSig.Params.Count == 2, "F: 方法签名不符");
        var pan = t.Fields.FirstOrDefault(f => S(f.Name) == "_PanMain");
        Assert(pan != null, "F: 未找到 PanMain 字段");
        var body = m.Body; var ins = body.Instructions;
        Assert(ins[ins.Count - 1].OpCode == dnlib.DotNet.Emit.OpCodes.Ret, "F: 末尾非 ret");
        var tbT = typeof(System.Windows.Controls.TextBlock);
        var tb = new dnlib.DotNet.Emit.Local(imp.ImportAsTypeSig(tbT));
        var link = new dnlib.DotNet.Emit.Local(imp.ImportAsTypeSig(typeof(System.Windows.Documents.Hyperlink)));
        body.Variables.Add(tb); body.Variables.Add(link);
        var iTbCtor = imp.Import(tbT.GetConstructor(Type.EmptyTypes));
        var iFore = imp.Import(tbT.GetProperty("Foreground").GetSetMethod());
        var iRed = imp.Import(typeof(System.Windows.Media.Brushes).GetProperty("Red").GetGetMethod());
        var iWrap = imp.Import(tbT.GetProperty("TextWrapping").GetSetMethod());
        var iAlign = imp.Import(tbT.GetProperty("TextAlignment").GetSetMethod());
        var iMargin = imp.Import(typeof(System.Windows.FrameworkElement).GetProperty("Margin").GetSetMethod());
        var iThick = imp.Import(typeof(System.Windows.Thickness).GetConstructor(new Type[] { typeof(double), typeof(double), typeof(double), typeof(double) }));
        var iInlines = imp.Import(tbT.GetProperty("Inlines").GetGetMethod());
        var iInlineAdd = imp.Import(typeof(System.Windows.Documents.InlineCollection).GetMethod("Add", new Type[] { typeof(System.Windows.Documents.Inline) }));
        var iRunS = imp.Import(typeof(System.Windows.Documents.Run).GetConstructor(new Type[] { typeof(string) }));
        var iBrCtor = imp.Import(typeof(System.Windows.Documents.LineBreak).GetConstructor(Type.EmptyTypes));
        var iLinkCtor = imp.Import(typeof(System.Windows.Documents.Hyperlink).GetConstructor(new Type[] { typeof(System.Windows.Documents.Inline) }));
        var iNavUri = imp.Import(typeof(System.Windows.Documents.Hyperlink).GetProperty("NavigateUri").GetSetMethod());
        var iUriCtor = imp.Import(typeof(Uri).GetConstructor(new Type[] { typeof(string) }));
        var iClickAdd = imp.Import(typeof(System.Windows.Documents.Hyperlink).GetEvent("Click").GetAddMethod());
        var iHandlerCtor = imp.Import(typeof(System.Windows.RoutedEventHandler).GetConstructors()[0]);
        var iChildren = imp.Import(typeof(System.Windows.Controls.Panel).GetProperty("Children").GetGetMethod());
        var iUiAdd = imp.Import(typeof(System.Windows.Controls.UIElementCollection).GetMethod("Add", new Type[] { typeof(System.Windows.UIElement) }));
        var c = new List<dnlib.DotNet.Emit.Instruction>();
        Action<dnlib.DotNet.Emit.OpCode> E0 = oc => c.Add(new dnlib.DotNet.Emit.Instruction(oc));
        Action<dnlib.DotNet.Emit.OpCode, object> E1 = (oc, o) => c.Add(new dnlib.DotNet.Emit.Instruction(oc, o));
        E1(dnlib.DotNet.Emit.OpCodes.Newobj, iTbCtor); E1(dnlib.DotNet.Emit.OpCodes.Stloc, tb);
        E1(dnlib.DotNet.Emit.OpCodes.Ldloc, tb); E1(dnlib.DotNet.Emit.OpCodes.Call, iRed); E1(dnlib.DotNet.Emit.OpCodes.Callvirt, iFore);
        E1(dnlib.DotNet.Emit.OpCodes.Ldloc, tb); E1(dnlib.DotNet.Emit.OpCodes.Ldc_I4, (int)System.Windows.TextWrapping.Wrap); E1(dnlib.DotNet.Emit.OpCodes.Callvirt, iWrap);
        E1(dnlib.DotNet.Emit.OpCodes.Ldloc, tb); E1(dnlib.DotNet.Emit.OpCodes.Ldc_I4, (int)System.Windows.TextAlignment.Center); E1(dnlib.DotNet.Emit.OpCodes.Callvirt, iAlign);
        E1(dnlib.DotNet.Emit.OpCodes.Ldloc, tb); E1(dnlib.DotNet.Emit.OpCodes.Ldc_R8, 25.0); E1(dnlib.DotNet.Emit.OpCodes.Ldc_R8, 8.0); E1(dnlib.DotNet.Emit.OpCodes.Ldc_R8, 25.0); E1(dnlib.DotNet.Emit.OpCodes.Ldc_R8, 4.0); E1(dnlib.DotNet.Emit.OpCodes.Newobj, iThick); E1(dnlib.DotNet.Emit.OpCodes.Callvirt, iMargin);
        E1(dnlib.DotNet.Emit.OpCodes.Ldloc, tb); E1(dnlib.DotNet.Emit.OpCodes.Callvirt, iInlines); E1(dnlib.DotNet.Emit.OpCodes.Ldstr, ABOUT_L1); E1(dnlib.DotNet.Emit.OpCodes.Newobj, iRunS); E1(dnlib.DotNet.Emit.OpCodes.Callvirt, iInlineAdd);
        E1(dnlib.DotNet.Emit.OpCodes.Ldloc, tb); E1(dnlib.DotNet.Emit.OpCodes.Callvirt, iInlines); E1(dnlib.DotNet.Emit.OpCodes.Newobj, iBrCtor); E1(dnlib.DotNet.Emit.OpCodes.Callvirt, iInlineAdd);
        E1(dnlib.DotNet.Emit.OpCodes.Ldloc, tb); E1(dnlib.DotNet.Emit.OpCodes.Callvirt, iInlines); E1(dnlib.DotNet.Emit.OpCodes.Ldstr, ABOUT_L2A); E1(dnlib.DotNet.Emit.OpCodes.Newobj, iRunS); E1(dnlib.DotNet.Emit.OpCodes.Callvirt, iInlineAdd);
        E1(dnlib.DotNet.Emit.OpCodes.Ldstr, ABOUT_LINK); E1(dnlib.DotNet.Emit.OpCodes.Newobj, iRunS); E1(dnlib.DotNet.Emit.OpCodes.Newobj, iLinkCtor); E1(dnlib.DotNet.Emit.OpCodes.Stloc, link);
        E1(dnlib.DotNet.Emit.OpCodes.Ldloc, link); E1(dnlib.DotNet.Emit.OpCodes.Ldstr, LIC_URL); E1(dnlib.DotNet.Emit.OpCodes.Newobj, iUriCtor); E1(dnlib.DotNet.Emit.OpCodes.Callvirt, iNavUri);
        E1(dnlib.DotNet.Emit.OpCodes.Ldloc, link); E0(dnlib.DotNet.Emit.OpCodes.Ldnull); E1(dnlib.DotNet.Emit.OpCodes.Ldftn, bridgeLicClick); E1(dnlib.DotNet.Emit.OpCodes.Newobj, iHandlerCtor); E1(dnlib.DotNet.Emit.OpCodes.Callvirt, iClickAdd);
        E1(dnlib.DotNet.Emit.OpCodes.Ldloc, tb); E1(dnlib.DotNet.Emit.OpCodes.Callvirt, iInlines); E1(dnlib.DotNet.Emit.OpCodes.Ldloc, link); E1(dnlib.DotNet.Emit.OpCodes.Callvirt, iInlineAdd);
        E1(dnlib.DotNet.Emit.OpCodes.Ldloc, tb); E1(dnlib.DotNet.Emit.OpCodes.Callvirt, iInlines); E1(dnlib.DotNet.Emit.OpCodes.Ldstr, ABOUT_L2B); E1(dnlib.DotNet.Emit.OpCodes.Newobj, iRunS); E1(dnlib.DotNet.Emit.OpCodes.Callvirt, iInlineAdd);
        E0(dnlib.DotNet.Emit.OpCodes.Ldarg_0); E1(dnlib.DotNet.Emit.OpCodes.Ldfld, pan); E1(dnlib.DotNet.Emit.OpCodes.Callvirt, iChildren); E1(dnlib.DotNet.Emit.OpCodes.Ldloc, tb); E1(dnlib.DotNet.Emit.OpCodes.Callvirt, iUiAdd); E0(dnlib.DotNet.Emit.OpCodes.Pop);
        int pos = ins.Count - 1;
        for (int k = c.Count - 1; k >= 0; k--) ins.Insert(pos, c[k]);
        body.MaxStack = 16;
        body.InitLocals = true;
        touched.Add(body);
        Log("F OK：关于页底部红字说明已注入");
    }

    // ---------- Patch G：首次启动公告 ----------
    static void PatchG()
    {
        EnsureBridge();
        dnlib.DotNet.MethodDef host = null;
        foreach (var tt in mod.GetTypes())
        foreach (var mm in tt.Methods)
        {
            if (!mm.HasBody) continue;
            foreach (var i2 in mm.Body.Instructions)
                if (i2.OpCode == dnlib.DotNet.Emit.OpCodes.Ldstr && "SystemEulaVersion".Equals(i2.Operand)) host = mm;
        }
        Assert(host != null, "G: 未找到启动线程注入点（SystemEulaVersion）");
        host.Body.Instructions.Insert(0, new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Call, bridgeAnnounce));
        host.Body.MaxStack = 16;
        touched.Add(host.Body);
        Log("G OK：首次启动公告已注入（宿主方法：" + S(host.Name) + "）");
    }

    static dnlib.DotNet.MethodDef FindMethodByName(string name, int pc)
    {
        foreach (var tt in mod.GetTypes())
        foreach (var mm in tt.Methods)
            if (S(mm.Name) == name && mm.MethodSig != null && mm.MethodSig.Params.Count == pc) return mm;
        throw new Exception("安全检查未通过：找不到方法 " + name + "/" + pc);
    }

    // ---------- 工具 ----------
    static void Assert(bool c, string msg) { if (!c) throw new Exception("安全检查未通过：" + msg); }
    static string S(dnlib.DotNet.UTF8String u) { return Object.ReferenceEquals(u, null) ? null : u.String; }
    static dnlib.DotNet.TypeDef FindType(string name)
    {
        var t = mod.GetTypes().FirstOrDefault(x => S(x.Name) == name);
        Assert(t != null, "找不到类型 " + name);
        return t;
    }
    static bool IsCallTo(dnlib.DotNet.Emit.Instruction ins, string name)
    {
        if (ins.OpCode != dnlib.DotNet.Emit.OpCodes.Call && ins.OpCode != dnlib.DotNet.Emit.OpCodes.Callvirt && ins.OpCode != dnlib.DotNet.Emit.OpCodes.Newobj) return false;
        var m = ins.Operand as dnlib.DotNet.IMethod;
        return m != null && S(m.Name) == name;
    }
    static bool IsStloc(dnlib.DotNet.Emit.Instruction ins)
    {
        switch (ins.OpCode.Code)
        {
            case dnlib.DotNet.Emit.Code.Stloc:
            case dnlib.DotNet.Emit.Code.Stloc_S:
            case dnlib.DotNet.Emit.Code.Stloc_0:
            case dnlib.DotNet.Emit.Code.Stloc_1:
            case dnlib.DotNet.Emit.Code.Stloc_2:
            case dnlib.DotNet.Emit.Code.Stloc_3:
                return true;
        }
        return false;
    }
    static dnlib.DotNet.Emit.Local StlocLocal(dnlib.DotNet.Emit.Instruction ins, IList<dnlib.DotNet.Emit.Local> locals)
    {
        switch (ins.OpCode.Code)
        {
            case dnlib.DotNet.Emit.Code.Stloc_0: return locals[0];
            case dnlib.DotNet.Emit.Code.Stloc_1: return locals[1];
            case dnlib.DotNet.Emit.Code.Stloc_2: return locals[2];
            case dnlib.DotNet.Emit.Code.Stloc_3: return locals[3];
            default: return (dnlib.DotNet.Emit.Local)ins.Operand;
        }
    }
    static int IndexOf(IList<dnlib.DotNet.Emit.Instruction> ins, Func<dnlib.DotNet.Emit.Instruction, bool> pred)
    {
        for (int i = 0; i < ins.Count; i++) if (pred(ins[i])) return i;
        return -1;
    }
    static dnlib.DotNet.Emit.Instruction Clone(dnlib.DotNet.Emit.Instruction ins) { return new dnlib.DotNet.Emit.Instruction(ins.OpCode, ins.Operand); }
    static void AssertNoRefInto(dnlib.DotNet.MethodDef m, int i0, int i1, string tag)
    {
        var ins = m.Body.Instructions;
        var region = new HashSet<dnlib.DotNet.Emit.Instruction>();
        for (int i = i0; i <= i1; i++) region.Add(ins[i]);
        for (int i = 0; i < ins.Count; i++)
        {
            if (i >= i0 && i <= i1) continue;
            var op = ins[i].Operand;
            if (op is dnlib.DotNet.Emit.Instruction) Assert(!region.Contains((dnlib.DotNet.Emit.Instruction)op), tag + ": 存在指向修改区的分支");
            if (op is dnlib.DotNet.Emit.Instruction[]) foreach (var t in (dnlib.DotNet.Emit.Instruction[])op) Assert(!region.Contains(t), tag + ": 存在指向修改区的 switch");
        }
        foreach (var eh in m.Body.ExceptionHandlers)
        {
            Assert(!region.Contains(eh.TryStart), tag + ": 异常表 TryStart 越界");
            Assert(!region.Contains(eh.TryEnd), tag + ": 异常表 TryEnd 越界");
            Assert(!region.Contains(eh.HandlerStart), tag + ": 异常表 HandlerStart 越界");
            Assert(!region.Contains(eh.HandlerEnd), tag + ": 异常表 HandlerEnd 越界");
        }
    }
}
