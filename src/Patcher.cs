using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

class Patcher
{
    static ModuleDefMD mod;
    const string LAW = "为遵守相关法律法规，PCL 暂时不再提供 MC 下载功能，非常抱歉 qwq……";

    static void Main(string[] args)
    {
        string src = args[0];
        string dst = args[1];
        mod = ModuleDefMD.Load(File.ReadAllBytes(src));
        try
        {
            PatchA();
            PatchB();
            PatchE();
            PatchC();
            PatchD();
            mod.Write(dst);
        }
        catch (Exception ex)
        {
            string info;
            try { info = ex.GetType().AssemblyQualifiedName + "\n" + ex.StackTrace; }
            catch { info = "meta fail"; }
            try { info += "\nMSG: " + ex.Message; } catch { info += "\nMSG: <unprintable>"; }
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(dst), "patch_error.txt"), info);
            throw;
        }
        Log("WRITE OK -> " + dst);
    }

    // ---------------- Patch A: PageDownloadInstall.LoaderInit ----------------
    // 将 "关闭下载" 的 throw 任务替换为 ldsfld DlClientListLoader
    static void PatchA()
    {
        var m = FindType("PageDownloadInstall").Methods.First(x => S(x.Name) == "LoaderInit");
        var ins = m.Body.Instructions;
        int i0 = IndexOf(ins, x => x.OpCode == OpCodes.Ldstr && (string)x.Operand == "关闭下载");
        Assert(i0 >= 0, "A: ldstr 关闭下载 not found");
        int i1 = -1;
        for (int i = i0; i < ins.Count; i++)
            if (ins[i].OpCode == OpCodes.Newobj && S(((IMethod)ins[i].Operand).DeclaringType.Name).StartsWith("LoaderTask")) { i1 = i; break; }
        Assert(i1 > i0, "A: LoaderTask newobj not found");
        AssertNoRefInto(m, i0, i1, "A");
        var fld = FindType("ModDownload").Fields.First(f => S(f.Name) == "DlClientListLoader");
        ins[i0] = new Instruction(OpCodes.Ldsfld, fld);
        for (int i = i1; i >= i0 + 1; i--) ins.RemoveAt(i);
        m.Body.MaxStack = 16;
        Log("A OK: LoaderInit now passes DlClientListLoader");
    }

    // ---------------- Patch B: PageDownloadModify 的 CardMinecraft 可见性 ----------------
    // Collapsed(2) -> Visible(0)
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
                    if (ins[j].OpCode == OpCodes.Ldc_I4_2 && IsCallTo(ins[j + 1], "set_Visibility"))
                    {
                        ins[j] = new Instruction(OpCodes.Ldc_I4_0);
                        found++;
                        break;
                    }
                }
            }
        }
        Assert(found == 1, "B: expected 1 CardMinecraft Visibility patch, got " + found);
        Log("B OK: CardMinecraft Visibility -> Visible");
    }

    // ---------------- Patch E: PageDownloadResourceDetail 版本列表安装按钮 ----------------
    static void PatchE()
    {
        var t = FindType("PageDownloadResourceDetail");
        var m = t.Methods.First(x => S(x.Name) == "InitializeStackResourceVersions");
        var ins = m.Body.Instructions;
        int c = IndexOf(ins, x => IsCallTo(x, "ToListItem"));
        Assert(c >= 0, "E: ToListItem call not found");
        int s0 = -1;
        for (int i = c; i > c - 15 && i >= 0; i--)
            if (ins[i].OpCode == OpCodes.Ldarg_1 && IsCallTo(ins[i + 1], "get_Children")) { s0 = i; break; }
        Assert(s0 >= 0, "E: block start (ldarg.1 + get_Children) not found");
        // 结构校验：s0: ldarg.1 / s0+1: get_Children / s0+2: ldloc Version / s0+3: ldarg.0 / s0+4: ldftn Save_Click / s0+5: newobj ClickEventHandler / s0+6: ldnull / s0+7: ldloc HasDup / c: ToListItem / c+1: Add / c+2: pop
        Assert(c == s0 + 8, "E: unexpected block layout, c=" + c + " s0=" + s0);
        Assert(ins[c + 2].OpCode == OpCodes.Pop, "E: expected pop after Add");
        AssertNoRefInto(m, s0, c + 2, "E");
        var trt = t.Fields.First(f => S(f.Name) == "TargetResourceType");
        var installClick = t.Methods.First(x => S(x.Name) == "Install_Click");
        var saveClick = t.Methods.First(x => S(x.Name) == "Save_Click");
        var toListItem = FindType("ResourceVersion").Methods.First(x => S(x.Name) == "ToListItem" && x.Parameters.Count >= 3);
        var iconClickRef = toListItem.MethodSig.Params[1].ToTypeDefOrRef();
        var iconCtor = new MemberRefUser(mod, ".ctor",
            MethodSig.CreateInstance(mod.CorLibTypes.Void, mod.CorLibTypes.Object, mod.CorLibTypes.IntPtr), iconClickRef);
        var elseStart = ins[s0];
        var endNext = ins[c + 3];
        var block = new List<Instruction>();
        block.Add(new Instruction(OpCodes.Ldarg_0));
        block.Add(new Instruction(OpCodes.Ldfld, trt));
        block.Add(new Instruction(OpCodes.Ldc_I4_2)); // ResourceTypes.ModPack = 2
        block.Add(new Instruction(OpCodes.Bne_Un, elseStart));
        block.Add(new Instruction(OpCodes.Ldarg_1));
        block.Add(Clone(ins[s0 + 1])); // callvirt get_Children
        block.Add(Clone(ins[s0 + 2])); // ldloc Version
        block.Add(new Instruction(OpCodes.Ldarg_0));
        block.Add(new Instruction(OpCodes.Ldftn, installClick));
        block.Add(Clone(ins[s0 + 5])); // newobj MyListItem.ClickEventHandler
        block.Add(new Instruction(OpCodes.Ldarg_0));
        block.Add(new Instruction(OpCodes.Ldftn, saveClick));
        block.Add(new Instruction(OpCodes.Newobj, iconCtor));
        block.Add(Clone(ins[s0 + 7])); // ldloc HasDuplicates
        block.Add(Clone(ins[c]));      // callvirt ToListItem
        block.Add(Clone(ins[c + 1]));  // callvirt Add
        block.Add(Clone(ins[c + 2]));  // pop
        block.Add(new Instruction(OpCodes.Br, endNext));
        for (int k = block.Count - 1; k >= 0; k--) ins.Insert(s0, block[k]);
        m.Body.MaxStack = 16;
        Log("E OK: modpack install button branch restored");
    }

    // ---------------- Patch C: ModpackInstall(File,...) 的 Select Case 分发 ----------------
    static void PatchC()
    {
        var mt = FindType("ModModpack");
        var m = mt.Methods.First(x => S(x.Name) == "ModpackInstall" && x.Parameters.Count == 3);
        var body = m.Body;
        var ins = body.Instructions;
        // 解析局部变量
        Local archive = null, abf = null, packType = null;
        for (int i = 0; i + 1 < ins.Count; i++)
        {
            if (IsCallTo(ins[i], "OpenZip") && IsStloc(ins[i + 1])) archive = StlocLocal(ins[i + 1], body.Variables);
            if (abf == null && ins[i].OpCode == OpCodes.Ldstr && (string)ins[i].Operand == "" && IsStloc(ins[i + 1])) abf = StlocLocal(ins[i + 1], body.Variables);
            if (packType == null && ins[i].OpCode == OpCodes.Ldc_I4_M1 && IsStloc(ins[i + 1])) packType = StlocLocal(ins[i + 1], body.Variables);
        }
        Assert(archive != null && abf != null && packType != null, "C: locals not resolved");
        // 各 InstallPack 方法与参数个数
        int[] caseVals = { 0, 1, 2, 3, 4, 9 };
        string[] caseNames = { "InstallPackCurseForge", "InstallPackHMCL", "InstallPackMMC", "InstallPackMCBBS", "InstallPackModrinth", "InstallPackLauncherPack" };
        int[] caseArgs = { 5, 3, 3, 4, 5, 3 };
        var caseMethods = new MethodDef[caseNames.Length];
        for (int i = 0; i < caseNames.Length; i++)
        {
            caseMethods[i] = mt.Methods.First(x => S(x.Name) == caseNames[i]);
            Assert(caseMethods[i].Parameters.Count == caseArgs[i], "C: " + caseNames[i] + " param count mismatch");
        }
        var compress = mt.Methods.First(x => S(x.Name) == "InstallPackCompress");
        Assert(compress.Parameters.Count == 2, "C: InstallPackCompress param count mismatch");
        var retSig = caseMethods[0].ReturnType;
        foreach (var cm in caseMethods) Assert(cm.ReturnType.FullName == retSig.FullName, "C: return types differ");
        Assert(compress.ReturnType.FullName == retSig.FullName, "C: compress return type differs");
        // 定位限制 throw
        int r0 = IndexOf(ins, x => x.OpCode == OpCodes.Ldstr && (string)x.Operand == LAW);
        Assert(r0 >= 0, "C: LAW string not found");
        Assert(ins[r0 + 1].OpCode == OpCodes.Newobj, "C: expected newobj after LAW");
        Assert(ins[r0 + 2].OpCode == OpCodes.Throw, "C: expected throw");
        var rTarget = ins[r0]; var rMid = ins[r0 + 1]; var rEnd = ins[r0 + 2];
        // 构造分发代码
        var retV = new Local(retSig);
        body.Variables.Add(retV);
        var RET = new Instruction(OpCodes.Ldloc, retV);
        Func<string, int, List<Instruction>> buildBody = (name, argn) =>
        {
            var l = new List<Instruction>();
            l.Add(new Instruction(OpCodes.Ldarg, m.Parameters[0]));
            l.Add(new Instruction(OpCodes.Ldloc, archive));
            if (argn >= 3) l.Add(new Instruction(OpCodes.Ldloc, abf));
            if (argn >= 4) l.Add(new Instruction(OpCodes.Ldarg, m.Parameters[1]));
            if (argn >= 5) l.Add(new Instruction(OpCodes.Ldarg, m.Parameters[2]));
            l.Add(new Instruction(OpCodes.Call, name == "InstallPackCompress" ? (MethodDef)compress : caseMethods[Array.IndexOf(caseNames, name)]));
            l.Add(new Instruction(OpCodes.Stloc, retV));
            l.Add(new Instruction(OpCodes.Leave, RET));
            return l;
        };
        var cmpStarts = new Instruction[caseVals.Length];
        var bodies = new List<Instruction>[caseVals.Length];
        for (int i = 0; i < caseVals.Length; i++)
        {
            cmpStarts[i] = new Instruction(OpCodes.Ldloc, packType);
            bodies[i] = buildBody(caseNames[i], caseArgs[i]);
        }
        var defBody = buildBody("InstallPackCompress", 2);
        var code = new List<Instruction>();
        for (int i = 0; i < caseVals.Length; i++)
        {
            code.Add(cmpStarts[i]);
            code.Add(new Instruction(OpCodes.Ldc_I4, caseVals[i]));
            code.Add(new Instruction(OpCodes.Bne_Un, i + 1 < caseVals.Length ? cmpStarts[i + 1] : defBody[0]));
            code.AddRange(bodies[i]);
        }
        code.AddRange(defBody);
        // 将指向被删除区域（Exit Try 的 leave 目标）的分支/EH 边界重定向到分发代码首指令
        for (int i = 0; i < ins.Count; i++)
        {
            if (i >= r0 && i <= r0 + 2) continue;
            var op = ins[i].Operand;
            if (op is Instruction)
            {
                var tgt = (Instruction)op;
                Assert(!ReferenceEquals(tgt, rMid) && !ReferenceEquals(tgt, rEnd), "C: branch to mid of throw from " + i);
                if (ReferenceEquals(tgt, rTarget)) ins[i].Operand = code[0];
            }
            else if (op is Instruction[])
            {
                var arr = (Instruction[])op;
                for (int k = 0; k < arr.Length; k++)
                {
                    Assert(!ReferenceEquals(arr[k], rMid) && !ReferenceEquals(arr[k], rEnd), "C: switch to mid of throw from " + i);
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
                && !ReferenceEquals(eh.HandlerStart, rEnd) && !ReferenceEquals(eh.HandlerEnd, rEnd), "C: EH boundary at mid of throw");
        }
        // 替换限制 throw 区域
        ins.RemoveAt(r0 + 2);
        ins.RemoveAt(r0 + 1);
        ins[r0] = code[0];
        for (int k = code.Count - 1; k >= 1; k--) ins.Insert(r0 + 1, code[k]);
        ins.Add(RET);
        ins.Add(new Instruction(OpCodes.Ret));
        body.MaxStack = 16;
        Log("C OK: modpack install dispatch restored (" + code.Count + " instructions)");
    }

    // ---------------- Patch D: ModpackInstall() 无参入口重建 ----------------
    static void PatchD()
    {
        var mt = FindType("ModModpack");
        var sub = mt.Methods.First(x => S(x.Name) == "ModpackInstall" && x.Parameters.Count == 0);
        var func3 = mt.Methods.First(x => S(x.Name) == "ModpackInstall" && x.Parameters.Count == 3);
        // 从其它方法中偷取现成引用
        IMethod selectFile = null, oneOf = null, nullableCtor = null, tupleCtor = null, firstOrDefault = null, isNullOrEmpty = null, getMessage = null;
        ITypeDefOrRef tupleType = null, oceRef = null, exRef = null;
        foreach (var m2 in AllMethods())
        {
            if (!m2.HasBody) continue;
            var ins2 = m2.Body.Instructions;
            for (int i = 0; i < ins2.Count; i++)
            {
                var op = ins2[i].Operand;
                if (selectFile == null && IsCallTo(ins2[i], "SelectFile") && i >= 4 && i + 4 < ins2.Count
                    && IsCallTo(ins2[i - 2], "op_Implicit") && ins2[i - 1].OpCode == OpCodes.Newobj
                    && ins2[i - 3].OpCode == OpCodes.Stelem && ins2[i - 4].OpCode == OpCodes.Newobj
                    && S(((IMethod)ins2[i - 4].Operand).DeclaringType.FullName).Contains("ValueTuple")
                    && IsCallTo(ins2[i + 1], "FirstOrDefault") && IsCallTo(ins2[i + 4], "IsNullOrEmpty"))
                {
                    selectFile = (IMethod)op;
                    oneOf = (IMethod)ins2[i - 2].Operand;
                    nullableCtor = (IMethod)ins2[i - 1].Operand;
                    tupleType = (ITypeDefOrRef)ins2[i - 3].Operand;
                    tupleCtor = (IMethod)ins2[i - 4].Operand;
                    firstOrDefault = (IMethod)ins2[i + 1].Operand;
                    isNullOrEmpty = (IMethod)ins2[i + 4].Operand;
                }
                if (ins2[i].OpCode == OpCodes.Newobj && op is IMethod)
                {
                    var dt = ((IMethod)op).DeclaringType;
                    if (oceRef == null && S(dt.FullName) == "System.OperationCanceledException") oceRef = dt;
                    if (exRef == null && S(dt.FullName) == "System.Exception") exRef = dt;
                }
                if (getMessage == null && (ins2[i].OpCode == OpCodes.Callvirt || ins2[i].OpCode == OpCodes.Call)
                    && op is IMethod && S(((IMethod)op).Name) == "get_Message" && S(((IMethod)op).DeclaringType.FullName) == "System.Exception")
                    getMessage = (IMethod)op;
            }
        }
        Assert(selectFile != null, "D: SelectFile donor not found");
        Assert(oceRef != null && exRef != null, "D: OCE/Exception typeref not found");
        Assert(getMessage != null, "D: Exception.get_Message not found");
        var hint = FindType("ModMain").Methods.First(x => S(x.Name) == "Hint" && x.Parameters.Count == 3);
        // 重建方法体
        var body = sub.Body;
        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        var fileL = new Local(mod.CorLibTypes.String);
        var exL = new Local(exRef.ToTypeSig());
        body.Variables.Add(fileL);
        body.Variables.Add(exL);
        var END = new Instruction(OpCodes.Ret);
        var TRY = new Instruction(OpCodes.Ldloc, fileL);
        var HANDLER = new Instruction(OpCodes.Stloc, exL);
        var OCEEXIT = new Instruction(OpCodes.Leave, END);
        var list = body.Instructions;
        list.Add(new Instruction(OpCodes.Ldstr, "选择整合包压缩文件"));
        list.Add(new Instruction(OpCodes.Ldc_I4_0));
        list.Add(new Instruction(OpCodes.Ldnull));
        list.Add(new Instruction(OpCodes.Ldc_I4_1));
        list.Add(new Instruction(OpCodes.Newarr, tupleType));
        list.Add(new Instruction(OpCodes.Dup));
        list.Add(new Instruction(OpCodes.Ldc_I4_0));
        list.Add(new Instruction(OpCodes.Ldc_I4_3));
        list.Add(new Instruction(OpCodes.Newarr, mod.CorLibTypes.String.TypeDefOrRef));
        string[] exts = { "rar", "zip", "mrpack" };
        for (int i = 0; i < exts.Length; i++)
        {
            list.Add(new Instruction(OpCodes.Dup));
            list.Add(new Instruction(OpCodes.Ldc_I4, i));
            list.Add(new Instruction(OpCodes.Ldstr, exts[i]));
            list.Add(new Instruction(OpCodes.Stelem_Ref));
        }
        list.Add(new Instruction(OpCodes.Ldstr, "整合包文件"));
        list.Add(new Instruction(OpCodes.Newobj, tupleCtor));
        list.Add(new Instruction(OpCodes.Stelem, tupleType));
        list.Add(new Instruction(OpCodes.Call, oneOf));
        list.Add(new Instruction(OpCodes.Newobj, nullableCtor));
        list.Add(new Instruction(OpCodes.Call, selectFile));
        list.Add(new Instruction(OpCodes.Call, firstOrDefault));
        list.Add(new Instruction(OpCodes.Stloc, fileL));
        list.Add(new Instruction(OpCodes.Ldloc, fileL));
        list.Add(new Instruction(OpCodes.Call, isNullOrEmpty));
        list.Add(new Instruction(OpCodes.Brtrue, END));
        list.Add(TRY);
        list.Add(new Instruction(OpCodes.Ldnull));
        list.Add(new Instruction(OpCodes.Ldnull));
        list.Add(new Instruction(OpCodes.Call, func3));
        list.Add(new Instruction(OpCodes.Pop));
        list.Add(new Instruction(OpCodes.Leave, END));
        list.Add(HANDLER);
        list.Add(new Instruction(OpCodes.Ldloc, exL));
        list.Add(new Instruction(OpCodes.Isinst, oceRef));
        list.Add(new Instruction(OpCodes.Brtrue, OCEEXIT));
        list.Add(new Instruction(OpCodes.Ldloc, exL));
        list.Add(new Instruction(OpCodes.Callvirt, getMessage));
        list.Add(new Instruction(OpCodes.Ldc_I4_2)); // HintType.Red
        list.Add(new Instruction(OpCodes.Ldc_I4_1));
        list.Add(new Instruction(OpCodes.Call, hint));
        list.Add(OCEEXIT);
        list.Add(END);
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = TRY,
            TryEnd = HANDLER,
            HandlerStart = HANDLER,
            HandlerEnd = END,
            CatchType = exRef
        });
        body.MaxStack = 16;
        body.InitLocals = true;
        Log("D OK: ModpackInstall() entry rebuilt");
    }

    // ---------------- 工具 ----------------
    static void Log(string s) { Console.WriteLine(s); }
    static void Assert(bool c, string msg) { if (!c) throw new Exception("ASSERT FAILED: " + msg); }
    static string S(UTF8String u) { return Object.ReferenceEquals(u, null) ? null : u.String; }
    static TypeDef FindType(string name)
    {
        var t = mod.GetTypes().FirstOrDefault(x => S(x.Name) == name);
        Assert(t != null, "type not found: " + name);
        return t;
    }
    static IEnumerable<MethodDef> AllMethods()
    {
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods) yield return m;
    }
    static bool IsCallTo(Instruction ins, string name)
    {
        if (ins.OpCode != OpCodes.Call && ins.OpCode != OpCodes.Callvirt && ins.OpCode != OpCodes.Newobj) return false;
        var m = ins.Operand as IMethod;
        return m != null && S(m.Name) == name;
    }
    static bool IsStloc(Instruction ins)
    {
        switch (ins.OpCode.Code)
        {
            case Code.Stloc: case Code.Stloc_S: case Code.Stloc_0: case Code.Stloc_1: case Code.Stloc_2: case Code.Stloc_3:
                return true;
        }
        return false;
    }
    static Local StlocLocal(Instruction ins, IList<Local> locals)
    {
        switch (ins.OpCode.Code)
        {
            case Code.Stloc_0: return locals[0];
            case Code.Stloc_1: return locals[1];
            case Code.Stloc_2: return locals[2];
            case Code.Stloc_3: return locals[3];
            default: return (Local)ins.Operand;
        }
    }
    static int IndexOf(IList<Instruction> ins, Func<Instruction, bool> pred)
    {
        for (int i = 0; i < ins.Count; i++) if (pred(ins[i])) return i;
        return -1;
    }
    static Instruction Clone(Instruction ins) { return new Instruction(ins.OpCode, ins.Operand); }
    // 确保没有外部分支或异常表边界指向 [i0,i1] 区间内（允许区间内部互相跳转）
    static void AssertNoRefInto(MethodDef m, int i0, int i1, string tag)
    {
        var ins = m.Body.Instructions;
        var region = new HashSet<Instruction>();
        for (int i = i0; i <= i1; i++) region.Add(ins[i]);
        for (int i = 0; i < ins.Count; i++)
        {
            if (i >= i0 && i <= i1) continue;
            var op = ins[i].Operand;
            if (op is Instruction) Assert(!region.Contains((Instruction)op), tag + ": branch into region from " + i);
            if (op is Instruction[]) foreach (var t in (Instruction[])op) Assert(!region.Contains(t), tag + ": switch into region from " + i);
        }
        foreach (var eh in m.Body.ExceptionHandlers)
        {
            Assert(!region.Contains(eh.TryStart), tag + ": EH TryStart in region");
            Assert(!region.Contains(eh.TryEnd), tag + ": EH TryEnd in region");
            Assert(!region.Contains(eh.HandlerStart), tag + ": EH HandlerStart in region");
            Assert(!region.Contains(eh.HandlerEnd), tag + ": EH HandlerEnd in region");
            Assert(eh.FilterStart == null || !region.Contains(eh.FilterStart), tag + ": EH FilterStart in region");
        }
    }
    // Patch C 专用：若 EH 边界引用了被删除的指令则重定向
    static void FixEh(MethodDef m, Instruction[] removed, Instruction replacement)
    {
        var set = new HashSet<Instruction>(removed);
        foreach (var eh in m.Body.ExceptionHandlers)
        {
            if (eh.TryStart != null && set.Contains(eh.TryStart)) eh.TryStart = replacement;
            if (eh.TryEnd != null && set.Contains(eh.TryEnd)) eh.TryEnd = replacement;
            if (eh.HandlerStart != null && set.Contains(eh.HandlerStart)) eh.HandlerStart = replacement;
            if (eh.HandlerEnd != null && set.Contains(eh.HandlerEnd)) eh.HandlerEnd = replacement;
        }
    }
}
