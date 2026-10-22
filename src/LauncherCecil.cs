// ============================================================================
// LauncherCecil.cs — PCL-CE / HMCL / 官方 PCL 指纹回填 补丁引擎
//
// 本文件的补丁引擎代码移植自 Jelly10086 的 PCL-Re.Download_PATCH 项目：
//   https://github.com/Jelly10086/PCL-Re.Download_PATCH
// 对应上游文件：source/LauncherPatch.cs、source/RestoreDownload.cs
// 移植遵循上游 PolyForm Noncommercial 1.0.0 许可要求，署名见 README.md。
// 本项目改动：
//   - 移除 PowerShell 外壳依赖，改为纯 C# 引擎入口（CecilPatcher.Patch）
//   - PCL 方法回填数据（donor / 方法指纹）从内嵌资源加载并校验 SHA256
// ============================================================================
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

public sealed class LauncherAssemblyResolver : DefaultAssemblyResolver {
    readonly Dictionary<string,byte[]> images=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string,AssemblyDefinition> loaded=new Dictionary<string,AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);
    public LauncherAssemblyResolver(byte[] bytes) {
        using(var module=ModuleDefinition.ReadModule(new MemoryStream(bytes))) {
            foreach(var resource in module.Resources.OfType<EmbeddedResource>().Where(r=>r.Name.EndsWith(".resources",StringComparison.OrdinalIgnoreCase))) {
                using(var reader=new System.Resources.ResourceReader(resource.GetResourceStream())) {
                    var keys=reader.GetEnumerator();
                    while(keys.MoveNext()) {
                        string type;byte[] raw;reader.GetResourceData((string)keys.Key,out type,out raw);
                        if(type!="ResourceTypeCode.ByteArray"||raw.Length<6)continue;
                        int size=BitConverter.ToInt32(raw,0);
                        if(size<2||size>raw.Length-4||raw[4]!=0x4d||raw[5]!=0x5a)continue;
                        var image=new byte[size];Buffer.BlockCopy(raw,4,image,0,size);
                        try {using(var dependency=ModuleDefinition.ReadModule(new MemoryStream(image))) {images[dependency.Assembly.Name.Name]=image;}}
                        catch(BadImageFormatException) { }
                    }
                }
            }
        }
    }
    public override AssemblyDefinition Resolve(AssemblyNameReference name,ReaderParameters parameters) {
        AssemblyDefinition cached;if(loaded.TryGetValue(name.Name,out cached))return cached;
        byte[] bytes;
        if(!images.TryGetValue(name.Name,out bytes))return base.Resolve(name,parameters);
        cached=AssemblyDefinition.ReadAssembly(new MemoryStream(bytes),new ReaderParameters {AssemblyResolver=this});loaded[name.Name]=cached;return cached;
    }
    protected override void Dispose(bool disposing) {
        if(disposing)foreach(var a in loaded.Values)a.Dispose();base.Dispose(disposing);
    }
}

public sealed class LauncherPatchResult {
    public string Launcher;
    public string Status;
    public byte[] Bytes;
    public string[] Changes;
}

public static class LauncherPatchEngine {
    static readonly byte[] BundleSignature = {0x8b,0x12,0x02,0xb9,0x6a,0x61,0x20,0x38,0x72,0x7b,0x93,0x02,0x14,0xd7,0xa0,0x32,0x13,0xf5,0xb9,0xe6,0xef,0xae,0x33,0x18,0xee,0x3b,0x2d,0xce,0x24,0xb3,0x6a,0xae};
    public static string Hash(byte[] bytes) {
        using(var h=SHA256.Create()) return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
    static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> roots) {
        foreach(var t in roots) {yield return t;foreach(var n in Types(t.NestedTypes))yield return n;}
    }
    public static string Fingerprint(MethodDefinition m) {
        var s=new StringBuilder(m.FullName).Append('\n');
        if(!m.HasBody)return Hash(Encoding.UTF8.GetBytes(s.ToString()));
        var instructions=m.Body.Instructions;
        foreach(var v in m.Body.Variables)s.Append("local:").Append(v.VariableType.FullName).Append('\n');
        s.Append("init:").Append(m.Body.InitLocals).Append('\n');
        foreach(var i in instructions) {
            object o=i.Operand;
            if(o is Instruction)o="jump:"+instructions.IndexOf((Instruction)o);
            else if(o is Instruction[])o="switch:"+String.Join(",",((Instruction[])o).Select(x=>instructions.IndexOf(x)));
            else if(o is MemberReference)o=((MemberReference)o).FullName;
            else if(o is VariableDefinition)o="local:"+((VariableDefinition)o).Index;
            else if(o is ParameterDefinition)o="arg:"+((ParameterDefinition)o).Index;
            else if(o is IFormattable)o=((IFormattable)o).ToString(null,System.Globalization.CultureInfo.InvariantCulture);
            s.Append(i.OpCode.Name).Append(' ').Append(o).Append('\n');
        }
        foreach(var e in m.Body.ExceptionHandlers) {
            s.Append(e.HandlerType).Append(':').Append(e.CatchType==null?"":e.CatchType.FullName)
                .Append(':').Append(instructions.IndexOf(e.TryStart)).Append(':').Append(instructions.IndexOf(e.TryEnd))
                .Append(':').Append(instructions.IndexOf(e.HandlerStart)).Append(':').Append(instructions.IndexOf(e.HandlerEnd))
                .Append(':').Append(instructions.IndexOf(e.FilterStart)).Append('\n');
        }
        return Hash(Encoding.UTF8.GetBytes(s.ToString()));
    }
    static LauncherPatchResult Result(string launcher,string status,byte[] bytes,IEnumerable<string> changes) {
        return new LauncherPatchResult {Launcher=launcher,Status=status,Bytes=bytes,Changes=changes.ToArray()};
    }
    static void Bounds(byte[] b,long offset,long length) {
        if(offset<0||length<0||offset>b.LongLength-length)throw new InvalidDataException("文件范围无效");
    }
    static int Rva(byte[] b,uint rva) {
        Bounds(b,0x3c,4);int pe=BitConverter.ToInt32(b,0x3c);Bounds(b,pe,24);
        if(BitConverter.ToUInt32(b,pe)!=0x4550)throw new InvalidDataException("PE 标头无效");
        int count=BitConverter.ToUInt16(b,pe+6),sections=pe+24+BitConverter.ToUInt16(b,pe+20);
        for(int i=0;i<count;i++) {
            int s=sections+i*40;Bounds(b,s,40);
            uint start=BitConverter.ToUInt32(b,s+12),size=Math.Max(BitConverter.ToUInt32(b,s+8),BitConverter.ToUInt32(b,s+16));
            if(rva>=start&&(long)rva-start<size) {
                long pos=(long)BitConverter.ToUInt32(b,s+20)+rva-start;
                Bounds(b,pos,1);return checked((int)pos);
            }
        }
        throw new InvalidDataException("无法映射方法 RVA");
    }
    static void Put(byte[] b,int offset,byte[] value) {Bounds(b,offset,value.Length);Buffer.BlockCopy(value,0,b,offset,value.Length);}
    static void ClearSignature(byte[] b) {
        if(b.Length<64||b[0]!=0x4d||b[1]!=0x5a)return;
        int pe=BitConverter.ToInt32(b,0x3c);Bounds(b,pe,24);int optional=pe+24;
        int directories=optional+(BitConverter.ToUInt16(b,optional)==0x20b?112:96);
        Bounds(b,directories,40);Array.Clear(b,directories+32,8);
        Array.Clear(b,optional+64,4);
    }
    static void DisableNativeImage(byte[] b) {
        int pe=BitConverter.ToInt32(b,0x3c),optional=pe+24;
        int directories=optional+(BitConverter.ToUInt16(b,optional)==0x20b?112:96);
        Bounds(b,directories+14*8,8);uint clrRva=BitConverter.ToUInt32(b,directories+14*8);
        if(clrRva==0)return;
        int clr=Rva(b,clrRva);Bounds(b,clr,72);
        Array.Clear(b,clr+64,8);
        Put(b,clr+16,BitConverter.GetBytes((BitConverter.ToUInt32(b,clr+16)|1u)&~8u));
        ClearSignature(b);
    }
    static bool Constant(MethodDefinition m,bool expected) {
        if(!m.HasBody)return false;
        var code=m.Body.Instructions.Where(x=>x.OpCode!=OpCodes.Nop).ToArray();
        return code.Length==2&&code[0].OpCode==(expected?OpCodes.Ldc_I4_1:OpCodes.Ldc_I4_0)&&code[1].OpCode==OpCodes.Ret;
    }
    static void ReturnBool(byte[] bytes,MethodDefinition method,bool value) {
        int offset=Rva(bytes,checked((uint)method.RVA));byte header=bytes[offset];int code;
        if((header&3)==2) {Bounds(bytes,offset,3);bytes[offset]=10;code=offset+1;}
        else if((header&3)==3) {
            Bounds(bytes,offset,12);int size=(BitConverter.ToUInt16(bytes,offset)>>12)*4;
            if(size<12)throw new InvalidDataException("方法标头无效");
            Bounds(bytes,offset,size+2);Put(bytes,offset,BitConverter.GetBytes((ushort)((size/4<<12)|3)));
            Put(bytes,offset+2,BitConverter.GetBytes((ushort)1));Put(bytes,offset+4,BitConverter.GetBytes(2));
            Put(bytes,offset+8,new byte[4]);code=offset+size;
        } else throw new InvalidDataException("未知方法标头");
        if(method.Body.CodeSize<2)throw new InvalidDataException("方法体空间不足");
        bytes[code]=(byte)(value?0x17:0x16);bytes[code+1]=0x2a;
    }
    static LauncherPatchResult Managed(byte[] original,byte[] donor,string[] names,string[] fingerprints,bool onlyCE) {
        using(var module=ModuleDefinition.ReadModule(new MemoryStream(original))) {
            var policy=module.GetType("PCL.RestrictedFeaturePolicy");
            if(policy!=null) {
                var changes=new List<string>();byte[] output=(byte[])original.Clone();
                var hooks=policy.Methods.Where(m=>m.IsStatic&&m.ReturnType.FullName=="System.Boolean"&&
                    (m.Name=="get_IsRestricted"&&m.Parameters.Count==0||m.Name=="Check"&&m.Parameters.Count>=1&&m.Parameters[0].ParameterType.FullName=="System.String")).ToArray();
                if(!hooks.Any(x=>x.Name=="get_IsRestricted")||!hooks.Any(x=>x.Name=="Check"))
                    throw new InvalidDataException("已发现 PCL-CE 限制入口，但方法签名无法匹配");
                foreach(var m in hooks) {
                    bool value=m.Name=="Check";
                    if(Constant(m,value))continue;
                    if(!m.HasBody||!m.Body.Instructions.Any(i=>i.Operand is MethodReference&&
                        (((MethodReference)i.Operand).Name=="Assess"||((MethodReference)i.Operand).DeclaringType.FullName.StartsWith("PCL.Core.App.Region."))))
                        throw new InvalidDataException("限制入口结构无法匹配："+m.FullName);
                    ReturnBool(output,m,value);changes.Add(m.FullName);
                }
                if(changes.Count>0)DisableNativeImage(output);
                using(var verify=ModuleDefinition.ReadModule(new MemoryStream(output))) {
                    foreach(var m in verify.GetType(policy.FullName).Methods.Where(x=>hooks.Any(h=>h.FullName==x.FullName)))
                        if(!Constant(m,m.Name=="Check"))throw new InvalidDataException("方法回读验证失败："+m.FullName);
                }
                return Result("PCL-CE",changes.Count>0?"Patched":"AlreadyPatched",output,changes);
            }
            if(onlyCE)return null;
            if(module.GetType("PCL.ModModpack")==null||module.GetType("PCL.PageDownloadInstall")==null)return null;
            var methods=Types(module.Types).SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName);
            bool restricted=methods.Values.Any(m=>names!=null&&names.Contains(m.FullName)&&m.HasBody&&m.Body.Instructions.Any(i=>i.OpCode==OpCodes.Ldstr&&
                ((string)i.Operand=="关闭下载"||((string)i.Operand).Contains("不再提供 MC 下载功能"))));
            if(!restricted)return Result("PCL","NoRestriction",original,new string[0]);
            if(donor==null||names==null||fingerprints==null||names.Length!=fingerprints.Length)throw new InvalidDataException("缺少官方 PCL 方法回填数据");
            var mismatches=new List<string>();
            for(int i=0;i<names.Length;i++) {
                MethodDefinition m;
                if(!methods.TryGetValue(names[i],out m)||Fingerprint(m)!=fingerprints[i])mismatches.Add(names[i]);
            }
            if(mismatches.Count>0)throw new InvalidDataException("已发现官方 PCL 下载限制，但这些方法的结构无法匹配，未修改文件：\n"+String.Join("\n",mismatches));
            byte[] restored=RestoreDownload.Run(donor,original,names);
            using(var check=ModuleDefinition.ReadModule(new MemoryStream(restored))) {
                if(Types(check.Types).SelectMany(t=>t.Methods).Any(m=>names.Contains(m.FullName)&&m.HasBody&&m.Body.Instructions.Any(i=>i.OpCode==OpCodes.Ldstr&&
                    ((string)i.Operand=="关闭下载"||((string)i.Operand).Contains("不再提供 MC 下载功能")))))throw new InvalidDataException("回填后仍存在已知下载限制");
            }
            return Result("PCL","Patched",restored,names);
        }
    }
    sealed class BundleEntry {
        public long Offset,Size,Compressed;public byte Type;public string Name;public int Record;
    }
    static int Find(byte[] b,byte[] needle) {
        for(int i=8;i<=b.Length-needle.Length;i++) {int j=0;while(j<needle.Length&&b[i+j]==needle[j])j++;if(j==needle.Length)return i;}
        return -1;
    }
    static LauncherPatchResult Bundle(byte[] original,int marker) {
        long offset=BitConverter.ToInt64(original,marker-8);Bounds(original,offset,12);
        var entries=new List<BundleEntry>();int idPosition,idLength;
        using(var reader=new BinaryReader(new MemoryStream(original),Encoding.UTF8)) {
            reader.BaseStream.Position=offset;uint major=reader.ReadUInt32(),minor=reader.ReadUInt32();int count=reader.ReadInt32();
            if((major!=2&&major!=6)||minor!=0||count<1||count>10000)throw new InvalidDataException("不支持的 .NET 单文件容器格式");
            long idStart=reader.BaseStream.Position;string id=reader.ReadString();idLength=Encoding.UTF8.GetByteCount(id);idPosition=checked((int)(reader.BaseStream.Position-idLength));
            if(idLength<1||idLength>64)throw new InvalidDataException("单文件容器标识无效");
            reader.BaseStream.Position+=40;
            for(int i=0;i<count;i++) {
                var e=new BundleEntry {Record=checked((int)reader.BaseStream.Position)};
                e.Offset=reader.ReadInt64();e.Size=reader.ReadInt64();e.Compressed=major>=6?reader.ReadInt64():0;
                e.Type=reader.ReadByte();e.Name=reader.ReadString();Bounds(original,e.Offset,e.Compressed>0?e.Compressed:e.Size);
                if(e.Size<0||e.Size>268435456||e.Compressed<0)throw new InvalidDataException("单文件条目大小无效");entries.Add(e);
            }
        }
        byte[] output=(byte[])original.Clone();var changes=new List<string>();bool found=false;
        using(var append=new MemoryStream()) {
            append.Write(output,0,output.Length);
            foreach(var e in entries.Where(x=>x.Type==1&&(x.Name.IndexOf("PCL",StringComparison.OrdinalIgnoreCase)>=0||x.Name.IndexOf("Launcher",StringComparison.OrdinalIgnoreCase)>=0))) {
                byte[] bytes=new byte[checked((int)e.Size)];
                if(e.Compressed>0) {
                    using(var compressed=new DeflateStream(new MemoryStream(original,checked((int)e.Offset),checked((int)e.Compressed)),CompressionMode.Decompress)) {
                        int read=0,n;while(read<bytes.Length&&(n=compressed.Read(bytes,read,bytes.Length-read))>0)read+=n;
                        if(read!=bytes.Length||compressed.ReadByte()!=-1)throw new InvalidDataException("单文件条目解压长度不符");
                    }
                } else Buffer.BlockCopy(original,checked((int)e.Offset),bytes,0,bytes.Length);
                var result=Managed(bytes,null,null,null,true);if(result==null)continue;found=true;
                if(result.Status!="Patched")continue;
                changes.AddRange(result.Changes.Select(x=>e.Name+" :: "+x));
                if(e.Compressed==0) {append.Position=e.Offset;append.Write(result.Bytes,0,result.Bytes.Length);}
                else {
                    append.Position=append.Length;long dataOffset=append.Position;append.Write(result.Bytes,0,result.Bytes.Length);
                    append.Position=e.Record;using(var writer=new BinaryWriter(append,Encoding.UTF8,true)) {writer.Write(dataOffset);writer.Write((long)result.Bytes.Length);writer.Write(0L);}
                }
            }
            if(!found)throw new InvalidDataException("未在单文件容器中找到支持的下载限制入口");
            output=append.ToArray();
        }
        if(changes.Count>0) {
            Put(output,idPosition,Encoding.ASCII.GetBytes(Hash(output).Substring(0,idLength)));ClearSignature(output);
            var verify=Bundle(output,marker);
            if(verify.Status!="AlreadyPatched")throw new InvalidDataException("单文件容器回读验证失败");
        }
        return Result("PCL-CE",changes.Count>0?"Patched":"AlreadyPatched",output,changes);
    }
    // JVM 多字节字段采用大端序，跳过未知属性时保留其原始字节
    sealed class ClassReader {
        public byte[] Bytes;public int P;
        public ClassReader(byte[] bytes) {Bytes=bytes;}
        public int U1() {Bounds(Bytes,P,1);return Bytes[P++];}
        public int U2() {return U1()<<8|U1();}
        public uint U4() {return (uint)U2()<<16|(uint)U2();}
        public void Skip(long n) {Bounds(Bytes,P,n);P=checked((int)(P+n));}
    }
    static void BE2(Stream s,int value) {s.WriteByte((byte)(value>>8));s.WriteByte((byte)value);}
    static void BE4(Stream s,uint value) {BE2(s,(int)(value>>16));BE2(s,(int)value);}
    static byte[] JavaClass(byte[] original,out bool changed) {
        changed=false;var r=new ClassReader(original);if(r.U4()!=0xcafebabe)throw new InvalidDataException("Java 类文件无效");
        r.Skip(4);int count=r.U2();var utf=new string[count];
        for(int i=1;i<count;i++) {
            int tag=r.U1();
            switch(tag) {
                case 1:int length=r.U2();Bounds(original,r.P,length);utf[i]=Encoding.UTF8.GetString(original,r.P,length);r.Skip(length);break;
                case 3:case 4:case 9:case 10:case 11:case 12:case 17:case 18:r.Skip(4);break;
                case 5:case 6:r.Skip(8);i++;break;
                case 7:case 8:case 16:case 19:case 20:r.Skip(2);break;
                case 15:r.Skip(3);break;
                default:throw new InvalidDataException("未知 Java 常量池项："+tag);
            }
        }
        r.Skip(6);int interfaces=r.U2();r.Skip(interfaces*2);int fields=r.U2();
        for(int i=0;i<fields;i++) {r.Skip(6);int attrs=r.U2();for(int j=0;j<attrs;j++){r.Skip(2);r.Skip(r.U4());}}
        int methods=r.U2(),matched=0;
        using(var output=new MemoryStream()) {
            int copied=0;
            for(int i=0;i<methods;i++) {
                int flags=r.U2(),name=r.U2(),descriptor=r.U2(),attrs=r.U2();
                bool hook=utf[name]=="isIsChinaMainlandNetwork"&&utf[descriptor]=="()Z";
                if(hook&&(flags&8)==0)throw new InvalidDataException("HMCL 限制方法不是静态方法");
                for(int j=0;j<attrs;j++) {
                    int attrStart=r.P,attrName=r.U2();uint length=r.U4();int body=r.P;r.Skip(length);
                    if(!hook||utf[attrName]!="Code")continue;matched++;
                    if(length<12)throw new InvalidDataException("HMCL 方法体无效");
                    var code=new ClassReader(original) {P=body};code.U2();int locals=code.U2();uint codeLength=code.U4();Bounds(original,code.P,codeLength);
                    if(codeLength==2&&original[code.P]==3&&original[code.P+1]==0xac)continue;
                    if(!utf.Contains("checkChinaMainlandNetwork")||!utf.Contains("IS_CHINA_MAINLAND_MACHINE"))throw new InvalidDataException("HMCL 限制方法结构无法匹配");
                    output.Write(original,copied,attrStart-copied);BE2(output,attrName);BE4(output,14);
                    BE2(output,1);BE2(output,locals);BE4(output,2);output.WriteByte(3);output.WriteByte(0xac);BE2(output,0);BE2(output,0);
                    copied=r.P;changed=true;
                }
            }
            if(matched!=1)throw new InvalidDataException("HMCL 限制入口缺失或不唯一");
            output.Write(original,copied,original.Length-copied);return output.ToArray();
        }
    }
    static LauncherPatchResult Jar(byte[] original) {
        const string hook="org/jackhuang/hmcl/util/RegionUtils.class";
        int end=-1;
        for(int i=original.Length-22;i>=Math.Max(0,original.Length-65557);i--) {
            if(BitConverter.ToUInt32(original,i)==0x06054b50&&i+22+BitConverter.ToUInt16(original,i+20)==original.Length){end=i;break;}
        }
        if(end<0)return null;
        if(BitConverter.ToUInt16(original,end+4)!=0||BitConverter.ToUInt16(original,end+6)!=0)throw new InvalidDataException("不支持分卷 ZIP");
        uint cdSize=BitConverter.ToUInt32(original,end+12),cdOffset=BitConverter.ToUInt32(original,end+16);
        long prefix=(long)end-cdSize-cdOffset;if(prefix<0)throw new InvalidDataException("ZIP 目录偏移无效");
        var changes=new List<string>();byte[] classOutput;int start=checked((int)prefix);
        using(var zip=new ZipArchive(new MemoryStream(original,start,original.Length-start),ZipArchiveMode.Read)) {
            var entry=zip.GetEntry(hook);
            bool hmcl=entry!=null||zip.GetEntry("org/jackhuang/hmcl/Main.class")!=null;
            if(!hmcl)return null;
            if(entry==null)return Result("HMCL","NoRestriction",original,changes);
            if(entry.Length>16*1024*1024)throw new InvalidDataException("HMCL 类文件过大");
            bool changed;
            using(var s=entry.Open())using(var memory=new MemoryStream()) {s.CopyTo(memory);classOutput=JavaClass(memory.ToArray(),out changed);}
            if(!changed)return Result("HMCL","AlreadyPatched",original,changes);
            changes.Add(hook+" :: isIsChinaMainlandNetwork()Z");
            // 不重写已有条目的本地偏移，保留 EXE 前缀和所有其他 ZIP 数据
            if(zip.Entries.Any(e=>e.FullName.StartsWith("META-INF/",StringComparison.OrdinalIgnoreCase)&&
                (e.FullName.EndsWith(".SF",StringComparison.OrdinalIgnoreCase)||e.FullName.EndsWith(".RSA",StringComparison.OrdinalIgnoreCase)||e.FullName.EndsWith(".DSA",StringComparison.OrdinalIgnoreCase)||e.FullName.EndsWith(".EC",StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException("该 HMCL 使用 JAR 条目签名，不能按当前规则原位修补");
        }
        byte[] archive;
        using(var source=new MemoryStream(original,start,original.Length-start))using(var memory=new MemoryStream()) {
            source.CopyTo(memory);memory.Position=0;
            using(var zip=new ZipArchive(memory,ZipArchiveMode.Update,true)) {
                var entry=zip.GetEntry(hook);entry.Delete();entry=zip.CreateEntry(hook,CompressionLevel.Optimal);
                using(var s=entry.Open())s.Write(classOutput,0,classOutput.Length);
            }
            archive=memory.ToArray();
        }
        byte[] output=new byte[start+archive.Length];Buffer.BlockCopy(original,0,output,0,start);Buffer.BlockCopy(archive,0,output,start,archive.Length);
        ClearSignature(output);
        var verify=Jar(output);if(verify==null||verify.Status!="AlreadyPatched")throw new InvalidDataException("HMCL 回读验证失败");
        return Result("HMCL","Patched",output,changes);
    }
    public static LauncherPatchResult Patch(byte[] original,byte[] donor,string[] names,string[] fingerprints) {
        if(original==null||original.Length<64)throw new InvalidDataException("文件过小，无法识别启动器");
        var jar=Jar(original);if(jar!=null)return jar;
        int marker=Find(original,BundleSignature);if(marker>=0)return Bundle(original,marker);
        LauncherPatchResult result;
        try {result=Managed(original,donor,names,fingerprints,false);}
        catch(BadImageFormatException) {throw new InvalidDataException("无法识别启动器，支持 PCL、PCL-CE 和 HMCL");}
        if(result==null)throw new InvalidDataException("未找到支持的启动器或下载限制入口");
        return result;
    }
}

public sealed class RestoreDownload {
    readonly ModuleDefinition source, target;
    readonly Dictionary<string, TypeDefinition> types = new Dictionary<string, TypeDefinition>();
    readonly Dictionary<string, MethodDefinition> methods = new Dictionary<string, MethodDefinition>();
    readonly Dictionary<string, FieldDefinition> fields = new Dictionary<string, FieldDefinition>();
    readonly List<string> changed = new List<string>();
    readonly Dictionary<GenericParameter,GenericParameter> genericParameters = new Dictionary<GenericParameter,GenericParameter>();
    readonly Dictionary<string,TypeDefinition> generatedTypes = new Dictionary<string,TypeDefinition>();
    readonly Dictionary<string,MethodDefinition> generatedMethods = new Dictionary<string,MethodDefinition>();
    readonly Dictionary<string,FieldDefinition> generatedFields = new Dictionary<string,FieldDefinition>();
    RestoreDownload(ModuleDefinition s, ModuleDefinition t) {
        source=s; target=t;
        foreach(var x in All(t.Types)) {
            types[x.FullName]=x;
            foreach(var m in x.Methods) methods[m.FullName]=m;
            foreach(var f in x.Fields) fields[f.FullName]=f;
        }
    }
    static IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> roots) {
        foreach(var t in roots) { yield return t; foreach(var n in All(t.NestedTypes)) yield return n; }
    }
    static bool Closure(TypeReference type) {
        while(type is TypeSpecification)type=((TypeSpecification)type).ElementType;
        return type.Name.StartsWith("_Closure$",StringComparison.Ordinal);
    }
    static bool Generated(MethodDefinition method) {
        return Closure(method.DeclaringType)||method.Name.StartsWith("_Lambda$",StringComparison.Ordinal);
    }
    static string GeneratedName(string name) {
        return "__DownloadRestore_"+LauncherPatchEngine.Hash(System.Text.Encoding.UTF8.GetBytes(name)).Substring(0,16);
    }
    public static string[] RequiredMethods(ModuleDefinition module,string[] selected) {
        var all=All(module.Types).SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName);
        var required=new HashSet<string>(selected);
        var queue=new Queue<MethodDefinition>();
        foreach(var name in selected) {
            MethodDefinition method;
            if(!all.TryGetValue(name,out method))throw new InvalidOperationException("Missing donor method: "+name);
            queue.Enqueue(method);
        }
        while(queue.Count>0) {
            var method=queue.Dequeue();
            if(!method.HasBody)continue;
            foreach(var instruction in method.Body.Instructions) {
                var member=instruction.Operand as MemberReference;
                if(member==null)continue;
                var reference=member as MethodReference;
                MethodDefinition dependency;
                if(reference!=null&&all.TryGetValue(reference.FullName,out dependency)&&Generated(dependency)&&required.Add(dependency.FullName))queue.Enqueue(dependency);
                var type=member as TypeReference;
                if(type==null)type=member.DeclaringType;
                if(type==null||!Closure(type))continue;
                var closure=All(module.Types).SingleOrDefault(t=>t.FullName==type.FullName);
                if(closure==null)throw new InvalidOperationException("Missing donor closure: "+type.FullName);
                foreach(var child in closure.Methods)if(required.Add(child.FullName))queue.Enqueue(child);
            }
        }
        return required.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
    }
    static void AssertBody(MethodDefinition method) {
        if(!method.HasBody)return;
        var code=method.Body.Instructions.Where(i=>i.OpCode!=OpCodes.Nop).ToArray();
        if(code.Length==2&&code[0].OpCode==OpCodes.Ldnull&&code[1].OpCode==OpCodes.Throw)
            throw new InvalidOperationException("Donor dependency was stripped: "+method.FullName);
    }
    bool Local(TypeReference t) {
        while(t is TypeSpecification) t=((TypeSpecification)t).ElementType;
        return t.Scope == source || (t.Scope is AssemblyNameReference && t.Scope.Name==source.Assembly.Name.Name);
    }
    TypeReference T(TypeReference t) {
        var g=t as GenericInstanceType;
        if(g!=null) { var n=new GenericInstanceType(T(g.ElementType)); foreach(var a in g.GenericArguments)n.GenericArguments.Add(T(a)); return n; }
        var arr=t as ArrayType;
        if(arr!=null)return new ArrayType(T(arr.ElementType),arr.Rank);
        if(t is ByReferenceType)return new ByReferenceType(T(((ByReferenceType)t).ElementType));
        if(t is PointerType)return new PointerType(T(((PointerType)t).ElementType));
        var p=t as GenericParameter;
        if(p!=null) {
            GenericParameter mapped;
            if(genericParameters.TryGetValue(p,out mapped))return mapped;
            var owner=p.Owner as TypeReference;
            if(owner!=null) return T(owner).GenericParameters[p.Position];
            return p;
        }
        if(!Local(t))return target.ImportReference(t);
        TypeDefinition found;
        if(!Closure(t)) {
            if(types.TryGetValue(t.FullName,out found))return found;
            throw new Exception("Missing type: "+t.FullName);
        }
        if(generatedTypes.TryGetValue(t.FullName,out found))return found;
        var def=t.Resolve();
        if(def.HasGenericParameters)throw new Exception("Unsupported generic closure: "+t.FullName);
        var parent=(TypeDefinition)T(def.DeclaringType);
        var name=GeneratedName(def.FullName);
        if(parent.NestedTypes.Any(x=>x.Name==name))throw new Exception("Generated closure name already exists: "+name);
        found=new TypeDefinition(def.Namespace,name,def.Attributes,T(def.BaseType));
        parent.NestedTypes.Add(found); generatedTypes[def.FullName]=found;
        foreach(var f in def.Fields) {
            var nf=new FieldDefinition(f.Name,f.Attributes,T(f.FieldType));
            found.Fields.Add(nf); generatedFields[f.FullName]=nf;
        }
        foreach(var m in def.Methods) {
            var nm=new MethodDefinition(m.Name,m.Attributes,T(m.ReturnType));
            foreach(var a in m.Parameters) nm.Parameters.Add(new ParameterDefinition(a.Name,a.Attributes,T(a.ParameterType)));
            found.Methods.Add(nm); generatedMethods[m.FullName]=nm;
        }
        foreach(var m in def.Methods) if(m.HasBody) {AssertBody(m);Copy(m,generatedMethods[m.FullName]);}
        changed.Add("Restored closure "+def.FullName);
        return found;
    }
    FieldReference F(FieldReference f) {
        if(!Local(f.DeclaringType))return new FieldReference(f.Name,T(f.FieldType),T(f.DeclaringType));
        var owner=T(f.DeclaringType);
        FieldDefinition found;
        if(Closure(f.DeclaringType)) {
            if(generatedFields.TryGetValue(f.FullName,out found))return found;
            throw new Exception("Missing generated field: "+f.FullName);
        }
        if(fields.TryGetValue(f.FullName,out found))return found;
        if(f.DeclaringType is GenericInstanceType)return new FieldReference(f.Name,T(f.FieldType),owner);
        throw new Exception("Missing field: "+f.FullName);
    }
    MethodReference M(MethodReference m) {
        var gm=m as GenericInstanceMethod;
        if(gm!=null) {var n=new GenericInstanceMethod(M(gm.ElementMethod));foreach(var a in gm.GenericArguments)n.GenericArguments.Add(T(a));return n;}
        if(!Local(m.DeclaringType)) {
            var n=new MethodReference(m.Name,target.TypeSystem.Void,T(m.DeclaringType)) {HasThis=m.HasThis,ExplicitThis=m.ExplicitThis,CallingConvention=m.CallingConvention};
            foreach(var gp in m.GenericParameters) {var ng=new GenericParameter(gp.Name,n);n.GenericParameters.Add(ng);genericParameters[gp]=ng;}
            n.ReturnType=T(m.ReturnType);
            foreach(var a in m.Parameters)n.Parameters.Add(new ParameterDefinition(T(a.ParameterType)));
            return n;
        }
        var owner=T(m.DeclaringType);
        MethodDefinition found;
        if(generatedMethods.TryGetValue(m.FullName,out found))return found;
        bool generated=Closure(m.DeclaringType)||m.Name.StartsWith("_Lambda$",StringComparison.Ordinal);
        if(!generated&&methods.TryGetValue(m.FullName,out found))return found;
        if(m.DeclaringType is GenericInstanceType) {
            var n=new MethodReference(m.Name,T(m.ReturnType),owner) {HasThis=m.HasThis,ExplicitThis=m.ExplicitThis,CallingConvention=m.CallingConvention};
            foreach(var a in m.Parameters)n.Parameters.Add(new ParameterDefinition(T(a.ParameterType)));
            foreach(var gp in m.GenericParameters)n.GenericParameters.Add(new GenericParameter(gp.Name,n));
            return n;
        }
        if(!generated)throw new Exception("Missing method: "+m.FullName);
        var def=m.Resolve();
        AssertBody(def);
        if(def.HasGenericParameters)throw new Exception("Unsupported generic delegate: "+m.FullName);
        var name=GeneratedName(def.FullName);
        if(((TypeDefinition)owner).Methods.Any(x=>x.Name==name))throw new Exception("Generated method name already exists: "+name);
        found=new MethodDefinition(name,def.Attributes,T(def.ReturnType));
        foreach(var a in def.Parameters)found.Parameters.Add(new ParameterDefinition(a.Name,a.Attributes,T(a.ParameterType)));
        ((TypeDefinition)owner).Methods.Add(found); generatedMethods[m.FullName]=found;
        Copy(def,found); changed.Add("Restored delegate "+def.FullName);return found;
    }
    void Copy(MethodDefinition from,MethodDefinition to) {
        var body=new MethodBody(to) {InitLocals=from.Body.InitLocals,MaxStackSize=from.Body.MaxStackSize};to.Body=body;
        foreach(var v in from.Body.Variables)body.Variables.Add(new VariableDefinition(T(v.VariableType)));
        var map=new Dictionary<Instruction,Instruction>();
        foreach(var i in from.Body.Instructions){var n=Instruction.Create(OpCodes.Nop);n.OpCode=i.OpCode;body.Instructions.Add(n);map[i]=n;}
        foreach(var i in from.Body.Instructions) {
            object o=i.Operand;
            if(o is Instruction)o=map[(Instruction)o];
            else if(o is Instruction[])o=((Instruction[])o).Select(x=>map[x]).ToArray();
            else if(o is VariableDefinition)o=body.Variables[((VariableDefinition)o).Index];
            else if(o is ParameterDefinition)o=to.Parameters[((ParameterDefinition)o).Index];
            else if(o is TypeReference)o=T((TypeReference)o);
            else if(o is FieldReference)o=F((FieldReference)o);
            else if(o is MethodReference)o=M((MethodReference)o);
            else if(o is CallSite)throw new Exception("Unexpected calli");
            map[i].Operand=o;
        }
        foreach(var e in from.Body.ExceptionHandlers)body.ExceptionHandlers.Add(new ExceptionHandler(e.HandlerType) {
            CatchType=e.CatchType==null?null:T(e.CatchType),TryStart=map[e.TryStart],TryEnd=e.TryEnd==null?null:map[e.TryEnd],
            HandlerStart=map[e.HandlerStart],HandlerEnd=e.HandlerEnd==null?null:map[e.HandlerEnd],FilterStart=e.FilterStart==null?null:map[e.FilterStart]
        });
    }
    public static byte[] Run(byte[] donor,byte[] original,string[] selected) {
        using(var resolver=new LauncherAssemblyResolver(original)) {
            var parameters=new ReaderParameters {AssemblyResolver=resolver};
            using(var a=AssemblyDefinition.ReadAssembly(new System.IO.MemoryStream(donor),parameters))using(var b=AssemblyDefinition.ReadAssembly(new System.IO.MemoryStream(original),parameters)) {
                var p=new RestoreDownload(a.MainModule,b.MainModule);
                var required=new HashSet<string>(RequiredMethods(a.MainModule,selected));
                foreach(var dependency in All(a.MainModule.Types).SelectMany(t=>t.Methods).Where(m=>required.Contains(m.FullName)))AssertBody(dependency);
                foreach(var name in selected) {
                    var m=All(a.MainModule.Types).SelectMany(x=>x.Methods).Single(x=>x.FullName==name);
                    MethodDefinition dest;
                    if(!p.methods.TryGetValue(m.FullName,out dest))throw new Exception("Missing destination: "+m.FullName);
                    p.Copy(m,dest);p.changed.Add("Restored "+m.FullName);
                }
                if(b.MainModule.AssemblyReferences.Any(x=>x.Name==a.Name.Name))throw new Exception("Accidental self reference");
                using(var output=new System.IO.MemoryStream()) {b.Write(output);return output.ToArray();}
            }
        }
    }
}

// ============================================================================
// CecilPatcher — 引擎入口：从内嵌资源加载 PCL 方法回填数据并调用上游引擎
// ============================================================================
public static class CecilPatcher
{
    static byte[] donor;
    static string[] methodNames;
    static string[] methodHashes;
    static bool loaded;

    // 供目标识别层嗅探 .NET 单文件容器（native PE 壳）
    public static bool LooksLikeBundle(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 40) return false;
        var sig = new byte[] {0x8b,0x12,0x02,0xb9,0x6a,0x61,0x20,0x38,0x72,0x7b,0x93,0x02,0x14,0xd7,0xa0,0x32,0x13,0xf5,0xb9,0xe6,0xef,0xae,0x33,0x18,0xee,0x3b,0x2d,0xce,0x24,0xb3,0x6a,0xae};
        for (int i = 8; i <= bytes.Length - sig.Length; i++)
        {
            int j = 0;
            while (j < sig.Length && bytes[i + j] == sig[j]) j++;
            if (j == sig.Length) return true;
        }
        return false;
    }

    static byte[] GetResource(string name)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using (var st = asm.GetManifestResourceStream(name))
        {
            if (st == null) throw new InvalidDataException("缺少内嵌资源：" + name);
            var buf = new byte[st.Length];
            st.Read(buf, 0, buf.Length);
            return buf;
        }
    }

    static string BytesHash(byte[] bytes)
    {
        using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    // 简易 JSON 提取（数据文件结构固定，避免引入 JSON 库依赖）
    static int JsonValueStart(string json, int from)
    {
        int i = json.IndexOf(':', from) + 1;
        while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n')) i++;
        return i;
    }

    static string JsonString(string json, string key)
    {
        int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (i < 0) throw new InvalidDataException("PCL 方法数据缺少字段：" + key);
        i = JsonValueStart(json, i);
        if (json[i] == '"') return JsonDecode(json, ref i);
        int e = i;
        while (e < json.Length && (char.IsDigit(json[e]) || json[e] == '-')) e++;
        return json.Substring(i, e - i);
    }

    // i 指向起始引号，返回解码后的字符串并把 i 移到结束引号之后
    static string JsonDecode(string json, ref int i)
    {
        var sb = new StringBuilder();
        i++; // 跳过起始引号
        while (i < json.Length && json[i] != '"')
        {
            if (json[i] == '\\' && i + 1 < json.Length)
            {
                char c = json[i + 1];
                if (c == 'u' && i + 5 < json.Length)
                {
                    sb.Append((char)Convert.ToInt32(json.Substring(i + 2, 4), 16));
                    i += 6;
                    continue;
                }
                sb.Append(c == 'n' ? '\n' : c == 'r' ? '\r' : c == 't' ? '\t' : c);
                i += 2;
                continue;
            }
            sb.Append(json[i]);
            i++;
        }
        i++; // 跳过结束引号
        return sb.ToString();
    }

    static string[] JsonStringArrayValues(string json, string key)
    {
        // 提取每个对象中 "key":"value" 的 value（容忍冒号后空白与 \uXXXX 转义）
        var list = new List<string>();
        string pat = "\"" + key + "\"";
        int p = 0;
        while (true)
        {
            int i = json.IndexOf(pat, p, StringComparison.Ordinal);
            if (i < 0) break;
            int s = JsonValueStart(json, i);
            if (s < json.Length && json[s] == '"')
            {
                list.Add(JsonDecode(json, ref s));
                p = s;
            }
            else p = i + pat.Length;
        }
        return list.ToArray();
    }

    static void EnsureData()
    {
        if (loaded) return;
        string json = Encoding.UTF8.GetString(GetResource("PCL.Methods.json"));
        if (JsonString(json, "Format") != "1") throw new InvalidDataException("PCL 方法数据格式无效");
        byte[] gz = GetResource("PCL.Methods.gz");
        byte[] raw;
        using (var ms = new MemoryStream())
        {
            using (var gzip = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress))
            {
                var buf = new byte[8192];
                int n;
                while ((n = gzip.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > 16777216) throw new InvalidDataException("PCL 方法数据过大");
                }
            }
            raw = ms.ToArray();
        }
        if (BytesHash(raw) != JsonString(json, "DonorSha256")) throw new InvalidDataException("PCL 方法数据校验失败");
        donor = raw;
        methodNames = JsonStringArrayValues(json, "Method");
        methodHashes = JsonStringArrayValues(json, "RestrictedFingerprint");
        if (methodNames.Length == 0 || methodNames.Length != methodHashes.Length) throw new InvalidDataException("PCL 方法数据无效");
        loaded = true;
    }

    // 对目标文件字节执行 PCL-CE / HMCL / 官方 PCL 指纹回填 补丁
    public static LauncherPatchResult Patch(byte[] original)
    {
        EnsureData();
        return LauncherPatchEngine.Patch(original, donor, methodNames, methodHashes);
    }
}
