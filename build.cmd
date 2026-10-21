@echo off
rem ============================================================
rem  PCL2 2.99 下载功能补丁 - 一键编译脚本
rem  依赖：任意 Windows + .NET Framework 4.x（系统自带，无需安装 VS）
rem  输出：Releases\PCL2一键补丁.exe（单文件，dnlib 已内嵌）
rem ============================================================
setlocal
cd /d "%~dp0"

rem --- 自动定位 csc（64 位优先，兼容 32 位系统） ---
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (echo [错误] 未找到 .NET Framework 4.x 的 csc.exe & pause & exit /b 1)

rem --- 自动定位 WPF GAC 引用 ---
set GA=%WINDIR%\Microsoft.NET\assembly
set PF=%GA%\GAC_MSIL\PresentationFramework\v4.0_4.0.0.0__31bf3856ad364e35\PresentationFramework.dll
set WB=%GA%\GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll
set PC=%GA%\GAC_64\PresentationCore\v4.0_4.0.0.0__31bf3856ad364e35\PresentationCore.dll
if not exist "%PC%" set PC=%GA%\GAC_32\PresentationCore\v4.0_4.0.0.0__31bf3856ad364e35\PresentationCore.dll
if not exist "%PF%" (echo [错误] 未找到 PresentationFramework.dll & pause & exit /b 1)
if not exist "%WB%" (echo [错误] 未找到 WindowsBase.dll & pause & exit /b 1)
if not exist "%PC%" (echo [错误] 未找到 PresentationCore.dll & pause & exit /b 1)
if not exist lib\dnlib.dll (echo [错误] 缺少 lib\dnlib.dll & pause & exit /b 1)

if not exist Releases mkdir Releases

"%CSC%" /nologo /target:winexe /platform:anycpu /r:System.dll /r:System.Xaml.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:"%PF%" /r:"%PC%" /r:"%WB%" /r:lib\dnlib.dll /win32manifest:src\app.manifest /resource:lib\dnlib.dll /out:"Releases\PCL2一键补丁.exe" src\PatchUI.cs

if %ERRORLEVEL%==0 (echo. & echo 编译成功：Releases\PCL2一键补丁.exe) else (echo. & echo 编译失败，请检查上方错误信息。)
pause
