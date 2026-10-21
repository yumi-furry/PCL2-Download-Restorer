# PCL2 2.99 版本下载功能补丁

针对 **Plain Craft Launcher 2（正式版 2.99.0.0）** 的 IL 级二进制补丁器，解除开源版中被作者主动限制的 Minecraft 原版下载功能，并注入来源说明与首次启动公告。

> ⚠️ **免责声明**：本工具仅供学习交流使用。PCL 作者出于合规考虑在开源版中加入了该限制（`RestrictMcDownload`），使用者请自行承担使用责任且仍需遵守[《PCL 分发有限许可》](https://shimo.im/docs/rGrd8pY8xWkt6ryW#anchor-X0bo)；本项目在遵守[《PCL 分发有限许可》](https://shimo.im/docs/rGrd8pY8xWkt6ryW#anchor-X0bo)的情况下使用GPL-3.0许可证。使用补丁版遇到的任何 BUG **请勿反馈至官方仓库**。本项目的许可证仅适用于补丁器自身的源代码，与 PCL2 主程序无关。

## 使用方法（小白向）

1. 打开 `Releases` 文件夹，双击 **PCL2一键补丁.exe**（会请求管理员权限，用于写入 Program Files）。
2. 在路径框填入 PCL2 主程序完整路径（可带引号），例如：
   `D:\Program Files\PCL2\Plain Craft Launcher 2.exe`
   也可用「浏览…」按钮选择；若默认路径存在会自动预填。
3. 点击「一键补丁」，等待日志显示 A~G 全部 OK。
4. 首次运行会自动把原文件备份为 `同名.exe.bak`；需要还原时点击「一键还原」即可（会校验备份确为 PCL2 主程序后才覆盖）。

注意事项：

- **仅适配正式版 2.99.0.0**。其它版本会触发内置安全检查并中止（不会写坏文件）。
- PCL 自动更新会覆盖补丁，更新后重跑一次补丁器即可。
- 补丁器为单文件 exe（dnlib 已内嵌），拷到任何 Windows 机器（.NET Framework 4.x，Win10/11 自带）均可直接使用。

## 校验与信任

发布版 exe 的哈希如下（Releases 目录附 `SHA256SUMS.txt`；GitHub Release 附件名因平台 ASCII 规范化显示为 `PCL2-Patch.exe`，与本目录 `PCL2一键补丁.exe` 为同一文件、哈希相同）：

| 算法 | 值 |
|---|---|
| SHA256 | `F03C5BB86485035FFC5CC4F445346FE501A6D856E18D13D3AF2613CFC502EA37` |
| SHA1 | `143ECB5B85F420EB70E1552E4A4696B5C266D71F` |
| MD5 | `8001519633FECE4CC6915AE8022928F3` |
| 文件大小 | 1,218,048 字节 |

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

## 补丁内容（A~G）

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

`RestrictMcDownload` 是 VB `Const` 编译期常量，所有 `If RestrictMcDownload Then …` 在编译时被**常量折叠**，被禁用的分支直接从 IL 中删除，因此无法通过"改字段值"解除，必须逐点重写 IL：

1. 用 [dnlib](https://github.com/0xd4d/dnlib) 读取程序集（.NET exe 是 MSIL + 元数据，非机器码）。
2. 通过锚点（特征字符串、方法名、字段名）定位 7 个修改点，逐点重写/重建 IL。
3. 被删代码引用的功能本体（下载加载器、安装方法等）仍完整存在于程序集中，补丁相当于把"被剪掉的入口"接回去；F/G 的逻辑则放进新建的桥接类型 `PCLPatchBridge`，WPF 类型引用经 dnlib Importer 复用程序集现有 AssemblyRef，不引入新依赖。
4. 写回前对所有修改过的方法体执行 `SimplifyBranches/OptimizeBranches` 重算分支偏移；exe 无强签名/自校验，CLR 正常 JIT。
5. 每个补丁前后均有硬断言（锚点、布局、异常表边界、无悬空分支），任一不匹配即中止且不写文件。

## 目录结构

```
PCL2-2.99版本下载补丁/
├── README.md            本文件
├── build.cmd            一键编译脚本（仅需系统自带 .NET Framework 4.x）
├── src/
│   ├── PatchUI.cs       GUI 补丁器主源码（A~G 全部补丁）
│   ├── app.manifest     管理员权限清单
│   ├── Patcher.cs       早期控制台版补丁器（A~E，存档参考）
│   ├── Dumper.cs        IL 转储工具：dumper.exe <exe> <类型名> <方法名>
│   ├── Probe.cs         编译形态探针：probe.exe <exe> <类型名>
│   └── Harness.cs       无头测试工具：反射调用 GUI 内的补丁核心
├── lib/
│   └── dnlib.dll        dnlib 4.x（net45，编译引用 + 内嵌资源）
└── Releases/
    └── PCL2一键补丁.exe  编译好的成品
```

## 自行编译

无需安装 Visual Studio，直接运行 `build.cmd` 即可。整个项目文件夹（约 1.2 MB）拷贝到**任意 Windows 设备**上都能复现构建：

- 编译器：系统自带 `csc.exe`（脚本自动探测 Framework64/Framework）
- WPF 引用：GAC 中的 PresentationFramework/PresentationCore/WindowsBase（脚本自动探测 GAC_64/GAC_32）
- dnlib：已附带在 `lib\dnlib.dll`，无需联网还原包

## 测试方式

`src\Harness.cs` 编译后可对任意副本无头执行完整补丁流程并输出逐项日志：

```
csc /out:harness.exe src\Harness.cs
harness.exe "Releases\PCL2一键补丁.exe" "测试用PCL副本.exe"
```

> 注意：不要在 %TEMP% 目录下直接运行 PCL 测试副本，会触发 PCL 自带的"需要解压！"保护而退出。
