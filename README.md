# PCL2 2.99 版本下载功能补丁

针对 **Plain Craft Launcher 2（正式版 2.99.0.0）** 的 IL 级二进制补丁器，解除开源版中被作者主动限制的 Minecraft 原版下载功能，并注入来源说明与首次启动公告。**同时兼容 PCL-CE 与 HMCL 的同类下载限制解除**（基于方法指纹识别，无版本白名单）。

> ⚠️ **免责声明**：本工具仅供学习交流使用。PCL 作者出于合规考虑在开源版中加入了该限制（`RestrictMcDownload`），使用者请自行承担使用责任且仍需遵守[《PCL 分发有限许可》](https://shimo.im/docs/rGrd8pY8xWkt6ryW#anchor-X0bo)；本项目在遵守[《PCL 分发有限许可》](https://shimo.im/docs/rGrd8pY8xWkt6ryW#anchor-X0bo)的情况下使用GPL-3.0许可证。使用补丁版遇到的任何 BUG **请勿反馈至官方仓库**。本项目的许可证仅适用于补丁器自身的源代码，与 PCL2 主程序无关。

## 支持的启动器与识别方式

补丁器会自动识别目标文件类型，并按对应策略处理：

| 启动器 | 识别方式 | 补丁策略 |
|---|---|---|
| **官方 PCL 2.99.0.0** | 程序集名 `Plain Craft Launcher 2` + 版本号 `2.99.0.0` + 锚点硬断言 | **锚点补丁（A~G）优先**；若锚点不匹配（如已被部分修改），自动回退到**方法指纹回填** |
| **官方 PCL 其它版本** | 程序集名 + 方法指纹（SHA256） | 方法指纹回填（无版本白名单，指纹匹配才动手） |
| **PCL-CE** | PE 内含 `RestrictedFeaturePolicy` 特征；或 .NET 单文件容器（Bundle）内嵌 PCL-CE 程序集 | 将限制判断方法（如 `get_IsRestricted`/`Check`）直接改写为返回放行值；支持 PE 与单文件容器两种形态 |
| **HMCL** | ZIP/Jar 内含 `org/jackhuang/hmcl` 条目（支持带 EXE 前缀的发行版） | 重写 `RegionUtils.isIsChinaMainlandNetwork()` 方法体为固定返回值 |

所有策略均带安全校验：**识别不出、指纹不匹配、形态不符时一律中止且不写文件**，不会写坏目标。

## 使用方法（小白向）

1. 打开 `Releases` 文件夹，双击 **PCL2一键补丁.exe**（会请求管理员权限，用于写入 Program Files）。
2. 选择目标启动器主程序，三种方式任选：
   - 在路径框填入完整路径（可带引号），例如 `D:\Program Files\PCL2\Plain Craft Launcher 2.exe`
   - 点击「浏览…」按钮选择
   - **直接把启动器 exe（或其桌面快捷方式 .lnk）拖进窗口**——快捷方式会自动解析出真实目标路径
3. 识别成功后路径框旁会显示胶囊徽章（如「官方 PCL 2.99」「PCL-CE」「HMCL」）。
4. 点击「一键补丁」，等待日志显示完成。
5. 首次运行会自动把原文件备份为 `同名.exe.bak`；需要还原时点击「一键还原」即可（会校验备份与目标匹配后才覆盖）。

注意事项：

- 官方 PCL 2.99.0.0 使用锚点补丁（A~G），适配性最强；其它版本/启动器走方法指纹，**指纹不匹配即中止**，不会强写。
- PCL 自动更新会覆盖补丁，更新后重跑一次补丁器即可。
- 若目标启动器正在运行导致文件被占用，补丁器会列出占用进程并询问是否结束后重试。
- 补丁器为单文件 exe（dnlib 与 Mono.Cecil 均已内嵌），拷到任何 Windows 机器（.NET Framework 4.x，Win10/11 自带）均可直接使用。

## 校验与信任

发布版 exe 的哈希如下（Releases 目录附 `SHA256SUMS.txt`；GitHub Release 附件名因平台 ASCII 规范化显示为 `PCL2-Patch.exe`，与本目录 `PCL2一键补丁.exe` 为同一文件、哈希相同）：

| 算法 | 值 |
|---|---|
| SHA256 | `13EF8BCBB2513601DC5D02869C9FD972E0C6770002C6127F64B808344ECFDD24` |
| SHA1 | `5FDA27937AEEDF2D5EC7543251761EAF0E3BAEB2` |
| MD5 | `BD327759A22CC189C48E100D40D7162F` |
| 文件大小 | 1,894,912 字节 |

下载后可用以下任一方式验证（哈希一致即为未被篡改的官方构建）：

```powershell
# PowerShell
Get-FileHash ".\PCL2一键补丁.exe" -Algorithm SHA256
```

```bat
:: CMD
certutil -hashfile "PCL2一键补丁.exe" SHA256
```

> 提示：自行用 `build.cmd` 重新编译得到的 exe 哈希会与此不同（编译时间戳等因素），这是正常现象；不信任发布二进制时可自行从源码构建使用。

## 补丁内容（官方 PCL 2.99 锚点补丁 A~G）

| 补丁 | 位置 | 作用 |
|---|---|---|
| A | `PageDownloadInstall.LoaderInit` | 下载页加载器从"抛异常任务"接回 `DlClientListLoader`，恢复原版 MC 下载 |
| B | `PageDownloadModify` | 修改页重新显示 Minecraft 卡片 |
| C | `ModModpack.ModpackInstall(File,…)` | 重建被常量折叠删掉的 `Select Case` 整合包安装分发（含分支/异常表修复） |
| D | `ModModpack.ModpackInstall()` | 重建整合包安装入口方法体（选文件→安装→异常提示） |
| E | `PageDownloadResourceDetail` | 恢复资源详情页整合包的"安装"按钮分支 |
| F | `PageOtherAbout_Loaded` 末尾 | 关于与鸣谢页底部追加红字说明（含《PCL 分发有限许可》超链接） |
| G | 启动后台线程（EULA lambda） | 首次启动按原版公告形式弹窗提示（注册表 `HKCU\Software\PCL2Patch` 判一次） |

## 原理

### 官方 PCL：锚点补丁（A~G）

`RestrictMcDownload` 是 VB `Const` 编译期常量，所有 `If RestrictMcDownload Then …` 在编译时被**常量折叠**，被禁用的分支直接从 IL 中删除，因此无法通过"改字段值"解除，必须逐点重写 IL：

1. 用 [dnlib](https://github.com/0xd4d/dnlib) 读取程序集（.NET exe 是 MSIL + 元数据，非机器码）。
2. 通过锚点（特征字符串、方法名、字段名）定位 7 个修改点，逐点重写/重建 IL。
3. 被删代码引用的功能本体（下载加载器、安装方法等）仍完整存在于程序集中，补丁相当于把"被剪掉的入口"接回去；F/G 的逻辑则放进新建的桥接类型 `PCLPatchBridge`，WPF 类型引用经 dnlib Importer 复用程序集现有 AssemblyRef，不引入新依赖。
4. 写回前对所有修改过的方法体执行 `SimplifyBranches/OptimizeBranches` 重算分支偏移；exe 无强签名/自校验，CLR 正常 JIT。
5. 每个补丁前后均有硬断言（锚点、布局、异常表边界、无悬空分支），任一不匹配即中止且不写文件。

### 官方 PCL 其它版本 / 兜底：方法指纹回填

锚点补丁仅适配 2.99.0.0 的 IL 形态。其它版本改用**方法指纹**识别（参照项目二方案）：

1. 预先用未受限的 PCL 构建（donor）提取 5 个被限制方法的完整 IL（含闭包/lambda 依赖），压缩内嵌于补丁器（`PCL.Methods.gz`）。
2. 对目标程序集逐方法计算指纹（方法全名 + 局部变量签名 + 逐指令 opcode/操作数 + 异常表 的 SHA256），与内嵌的受限版指纹清单（`PCL.Methods.json`）比对。
3. 指纹匹配即把 donor 方法体回填到目标；全部方法均不匹配则判定"未发现可修补的下载限制"并中止。**无版本白名单**，指纹不符绝不动手。

### PCL-CE

PCL-CE 的限制由 `RestrictedFeaturePolicy` 等运行时判断控制（非常量折叠），因此直接将限制判断方法改写为返回放行值：

- 普通 .NET PE：用 Mono.Cecil 定位目标方法，改写方法头为 `ldc.i4 + ret`（处理 tiny/fat 方法头、清 ReadyToRun 标记与 Authenticode 签名）。
- .NET 单文件容器（Bundle v2/v6）：解析容器清单，定位内嵌的 PCL-CE 程序集条目，原位替换（含 Deflate 压缩条目与 bundle ID 更新）。

### HMCL

HMCL 是 Java 程序（发行版为带 EXE 前缀的 Jar）。补丁器解析 ZIP 结构定位 `RegionUtils.class`，按 Java class 大端序格式重写 `isIsChinaMainlandNetwork()Z` 方法体为 `iconst_0 + ireturn`，再原位更新 ZIP 条目并保留 EXE 前缀。

## 目录结构

```
PCL2-2.99版本下载补丁/
├── README.md               本文件
├── UPSTREAM-LICENCE.txt    《PCL 分发有限许可》及合理使用指南
├── PCL2Patch.csproj        WPF 项目文件（MSBuild 编译）
├── build.cmd               一键编译脚本（调用系统自带 MSBuild）
├── src/
│   ├── App.xaml            应用入口 + PCL2 颜色资源表（ColorBrush1~8 等）
│   ├── App.xaml.cs         App 构造函数：注册内嵌程序集解析（dnlib/Cecil）
│   ├── MainWindow.xaml     主窗口：照搬 PCL2 标题栏 + MyButton/MyIconButton
│   ├── MainWindow.xaml.cs  窗口逻辑：拖拽移动、拖放文件、补丁/还原调度
│   ├── PatchCore.cs        补丁核心：Program 入口/TargetDetect/PatchRunner A~G
│   ├── LauncherCecil.cs    Mono.Cecil 引擎：PCL-CE、HMCL、官方 PCL 指纹回填
│   ├── app.manifest        管理员权限清单
│   ├── Controls/
│   │   ├── MyButton.xaml(.cs)        照搬 PCL2 MyButton（ColorAnimation 替代自研动画）
│   │   └── MyIconButton.xaml(.cs)    照搬 PCL2 MyIconButton（圆形图标按钮）
│   ├── Patcher.cs          早期控制台版补丁器（A~E，存档参考）
│   ├── Dumper.cs           IL 转储工具：dumper.exe <exe> <类型名> <方法名>
│   ├── Probe.cs            编译形态探针：probe.exe <exe> <类型名>
│   └── Harness.cs          无头测试工具：反射调用 GUI 内的补丁核心
├── lib/
│   ├── dnlib.dll           dnlib 4.x（net45，编译引用 + 内嵌资源）
│   ├── Mono.Cecil.dll      Mono.Cecil 0.11.6（MIT，编译引用 + 内嵌资源）
│   ├── Mono.Cecil.LICENSE.txt
│   ├── PCL.Methods.json    受限版方法指纹清单
│   └── PCL.Methods.gz      donor 方法体数据（Deflate 压缩）
└── Releases/
    ├── PCL2一键补丁.exe     编译好的成品
    └── SHA256SUMS.txt
```

## 自行编译

无需安装 Visual Studio，直接运行 `build.cmd` 即可。整个项目文件夹（约 2 MB）拷贝到**任意 Windows 设备**上都能复现构建：

- 编译器：系统自带 `MSBuild.exe`（脚本自动探测 Framework64/Framework 的 v4.0.30319）
- WPF 引用：GAC 中的 PresentationFramework/PresentationCore/WindowsBase（MSBuild 自动解析）
- dnlib / Mono.Cecil / 指纹数据：已附带在 `lib\`，无需联网还原包

## 测试方式

`src\Harness.cs` 编译后可对任意副本无头执行完整补丁流程并输出逐项日志：

```
csc /out:harness.exe src\Harness.cs
harness.exe "Releases\PCL2一键补丁.exe" "测试用启动器副本.exe"
```

> 注意：不要在 %TEMP% 目录下直接运行 PCL 测试副本，会触发 PCL 自带的"需要解压！"保护而退出。

## 致谢与参考

- **UI 风格参考 [PCL2（Plain Craft Launcher 2）](https://github.com/Meloong-Git/PCL)** 主界面设计。
- PCL-CE / HMCL 补丁与方法指纹回填的部分代码实现参考 **[Jelly10086/PCL-Re.Download_PATCH](https://github.com/Jelly10086/PCL-Re.Download_PATCH)**；donor 指纹数据（`lib/PCL.Methods.*`）来源于该项目。
- IL 读写库：[dnlib](https://github.com/0xd4d/dnlib)（MIT）、[Mono.Cecil](https://github.com/jbevain/cecil)（MIT，许可文本见 `lib/Mono.Cecil.LICENSE.txt`）。
- PCL 本体及上游内容的权利与许可不变，见 `UPSTREAM-LICENCE.txt`。
