using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

// ============ 目标识别（轻量嗅探，精细判定由各补丁引擎完成） ============
static class TargetDetect
{
    public enum Kind { Unknown, Pcl2, PclCE, Hmcl }

    public class Info
    {
        public Kind Kind = Kind.Unknown;
        public string AsmName = "";
        public string Version = "";
        public string Desc = "未识别";
    }

    public static Info Identify(byte[] bytes)
    {
        var info = new Info();
        // HMCL：ZIP（可带 EXE 前缀）内含 hmcl 条目
        if (LooksLikeHmcl(bytes))
        {
            info.Kind = Kind.Hmcl;
            info.Desc = "HMCL（Java 启动器）";
            return info;
        }
        // .NET 程序集
        try
        {
            using (var m = dnlib.DotNet.ModuleDefMD.Load(bytes))
            {
                string asm = m.Assembly != null ? m.Assembly.Name.String : "";
                string ver = m.Assembly != null ? m.Assembly.Version.ToString() : "";
                info.AsmName = asm;
                info.Version = ver;
                bool ce = false, pcl = false;
                foreach (var t in m.GetTypes())
                {
                    if (t.FullName == "PCL.RestrictedFeaturePolicy") ce = true;
                    else if (t.FullName == "PCL.ModModpack") pcl = true;
                }
                if (ce)
                {
                    info.Kind = Kind.PclCE;
                    info.Desc = "PCL-CE（托管程序集）";
                    return info;
                }
                if (asm == "Plain Craft Launcher 2" || pcl)
                {
                    info.Kind = Kind.Pcl2;
                    info.Desc = "官方 PCL " + ver + (ver == "2.99.0.0" ? "（锚点模式）" : "（指纹模式）");
                    return info;
                }
            }
        }
        catch { }
        // PCL-CE 的 .NET 单文件容器（native PE 壳，dnlib 无法直接加载）
        if (bytes.Length > 2 && bytes[0] == 'M' && bytes[1] == 'Z' && CecilPatcher.LooksLikeBundle(bytes))
        {
            info.Kind = Kind.PclCE;
            info.Desc = "PCL-CE（.NET 单文件容器）";
            return info;
        }
        return info;
    }

    static bool LooksLikeHmcl(byte[] b)
    {
        try
        {
            int end = -1;
            for (int i = b.Length - 22; i >= Math.Max(0, b.Length - 65557); i--)
                if (BitConverter.ToUInt32(b, i) == 0x06054b50 && i + 22 + BitConverter.ToUInt16(b, i + 20) == b.Length) { end = i; break; }
            if (end < 0) return false;
            uint cdSize = BitConverter.ToUInt32(b, end + 12), cdOffset = BitConverter.ToUInt32(b, end + 16);
            long prefix = (long)end - cdSize - cdOffset;
            if (prefix < 0) return false;
            using (var zip = new ZipArchive(new MemoryStream(b, (int)prefix, b.Length - (int)prefix), ZipArchiveMode.Read))
                return zip.GetEntry("org/jackhuang/hmcl/Main.class") != null
                    || zip.GetEntry("org/jackhuang/hmcl/util/RegionUtils.class") != null;
        }
        catch { return false; }
    }

    // 解析 .lnk 快捷方式目标（经 WScript.Shell COM 晚绑定，避免额外引用）
    public static string ResolveShortcut(string lnkPath)
    {
        object shell = null, sc = null;
        try
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return null;
            shell = Activator.CreateInstance(t);
            sc = t.InvokeMember("CreateShortcut", BindingFlags.Public | BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            string target = t.InvokeMember("TargetPath", BindingFlags.Public | BindingFlags.GetProperty, null, sc, null) as string;
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch { return null; }
        finally
        {
            if (sc != null) try { Marshal.FinalReleaseComObject(sc); } catch { }
            if (shell != null) try { Marshal.FinalReleaseComObject(shell); } catch { }
        }
    }
}

// ============ 补丁核心（PCL2 2.99 锚点补丁 A~G + 多启动器分派） ============
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

        byte[] original = File.ReadAllBytes(path);
        var info = TargetDetect.Identify(original);
        Log("识别结果：" + info.Desc);

        byte[] patched = null;
        if (info.Kind == TargetDetect.Kind.Pcl2 && info.Version == "2.99.0.0")
        {
            // 优先：程序集名 + 版本号 + 锚点硬断言（A~G，含 F/G 注入）
            try
            {
                patched = AnchorPatch(original);
                Log("锚点补丁完成（内存重写成功，尚未落盘）。");
            }
            catch (Exception ex)
            {
                Log("锚点补丁未通过安全检查：" + ex.Message);
                Log("回退：尝试方法指纹回填（仅恢复下载功能，不含 F/G）……");
                patched = null;
            }
        }
        else if (info.Kind == TargetDetect.Kind.Pcl2)
        {
            Log("官方 PCL 非 2.99.0.0，直接使用方法指纹回填（无版本白名单；不含 F/G）。");
        }

        if (patched == null)
        {
            var result = CecilPatcher.Patch(original);
            Log("引擎判定启动器：" + result.Launcher);
            if (result.Status == "AlreadyPatched") { Log("限制入口已关闭，无需重复修补。"); return; }
            if (result.Status == "NoRestriction") { Log("未发现可修补的下载限制，未修改文件。"); return; }
            if (result.Status != "Patched" || result.Changes.Length < 1) Fail("补丁未生成有效结果。");
            foreach (var c in result.Changes) Log("  修补点：" + c);
            patched = result.Bytes;
        }

        EnsureBackup(path, original);
        WriteWithRetry(path, patched, confirm);
        Log("已写入：" + path);
    }

    // PCL2 2.99 锚点补丁：返回补丁后的完整文件字节（安全检查失败抛异常，不写任何文件）
    static byte[] AnchorPatch(byte[] fileBytes)
    {
        // 重置静态状态，避免跨次运行污染（bridgeType/touched 等静态字段在第一次补丁后仍持有旧模块引用）
        mod = null;
        touched.Clear();
        bridgeType = null;
        bridgeLicClick = null;
        bridgeAnnounce = null;

        mod = dnlib.DotNet.ModuleDefMD.Load(fileBytes);
        string asmName = mod.Assembly != null ? mod.Assembly.Name.String : "";
        string ver = mod.Assembly != null ? mod.Assembly.Version.ToString() : "";
        Log("程序集：" + asmName + "  版本：" + ver);
        if (asmName != "Plain Craft Launcher 2") Fail("这不是 PCL2 主程序（程序集名：" + asmName + "）。");
        if (ver != "2.99.0.0") Fail("锚点补丁仅适配 2.99.0.0（当前：" + ver + "）。");

        PatchA(); PatchB(); PatchE(); PatchC(); PatchD(); PatchF(); PatchG();

        foreach (var b in touched) { b.SimplifyBranches(); b.OptimizeBranches(); }
        var ms = new MemoryStream();
        mod.Write(ms);
        Log("IL 重写完成。");
        return ms.ToArray();
    }

    // ---------- 备份与原子写入 ----------
    static void EnsureBackup(string path, byte[] original)
    {
        string bak = path + ".bak";
        if (File.Exists(bak)) { Log("备份已存在，跳过：" + bak); return; }
        WriteAtomic(bak, original);
        Log("已备份原文件：" + bak);
    }

    static void WriteAtomic(string path, byte[] bytes)
    {
        string dir = Path.GetDirectoryName(path);
        string tmp = Path.Combine(dir, ".pcl2patch-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        finally { TryDelete(tmp); }
    }

    static void WriteWithRetry(string path, byte[] bytes, Func<string, bool> confirm)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                WriteAtomic(path, bytes);
                return;
            }
            catch (IOException)
            {
                if (attempt == 3) Fail("目标文件被占用，写入失败。请关闭启动器后重试。");
                var lockers = FindLockers(path);
                if (lockers.Count == 0 || !confirm("启动器正在运行，需要结束进程才能写入。是否立即结束？"))
                    Fail("文件被占用，用户取消。");
                foreach (var p in lockers) { try { p.Kill(); p.WaitForExit(5000); } catch { } }
                Thread.Sleep(800);
            }
        }
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
        byte[] bakBytes = File.ReadAllBytes(bak);
        var info = TargetDetect.Identify(bakBytes);
        if (info.Kind == TargetDetect.Kind.Unknown) Fail("备份文件不是受支持的启动器主程序，已中止：" + bak);
        if (info.Kind == TargetDetect.Kind.Pcl2 && info.AsmName != "" && info.AsmName != "Plain Craft Launcher 2")
            Fail("备份文件不是 PCL2 主程序，已中止：" + bak);
        Log("备份校验通过（" + info.Desc + "）：" + bak);
        WriteWithRetry(path, bakBytes, confirm);
        Log("已用备份覆盖：" + path);
    }

    // 按主模块路径匹配所有进程；Java 启动器额外匹配 -jar 命令行
    static List<Process> FindLockers(string path)
    {
        var list = new List<Process>();
        string fileName = Path.GetFileName(path);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string mp = p.MainModule.FileName;
                if (string.Equals(mp, path, StringComparison.OrdinalIgnoreCase)) { list.Add(p); continue; }
            }
            catch { }
            if (!p.ProcessName.Equals("java", StringComparison.OrdinalIgnoreCase) && !p.ProcessName.Equals("javaw", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + p.Id))
                foreach (ManagementObject o in searcher.Get())
                {
                    string cl = o["CommandLine"] as string;
                    if (string.IsNullOrEmpty(cl)) continue;
                    var m = Regex.Match(cl, "(?i)(?:^|\\s)-jar\\s+(?:\"([^\"]+)\"|(\\S+))");
                    if (!m.Success) continue;
                    string jar = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    if (string.Equals(jar, path, StringComparison.OrdinalIgnoreCase) ||
                        (!Path.IsPathRooted(jar) && string.Equals(Path.GetFileName(jar), fileName, StringComparison.OrdinalIgnoreCase)))
                    {
                        list.Add(p);
                        break;
                    }
                }
            }
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
