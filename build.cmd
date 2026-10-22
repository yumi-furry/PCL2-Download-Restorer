@echo off
rem ============================================================
rem  PCL 下载功能补丁 - 一键编译脚本（WPF）
rem  依赖：任意 Windows + .NET Framework 4.x（系统自带，无需安装 VS）
rem  输出：Releases\PCL2一键补丁.exe（单文件，dnlib/Mono.Cecil/方法数据已内嵌）
rem ============================================================
setlocal
cd /d "%~dp0"

rem --- 自动定位 MSBuild（64 位优先，兼容 32 位系统） ---
set MSBUILD=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe
if not exist "%MSBUILD%" set MSBUILD=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe
if not exist "%MSBUILD%" (echo [错误] 未找到 .NET Framework 4.x 的 MSBuild.exe & pause & exit /b 1)

if not exist lib\dnlib.dll (echo [错误] 缺少 lib\dnlib.dll & pause & exit /b 1)
if not exist lib\Mono.Cecil.dll (echo [错误] 缺少 lib\Mono.Cecil.dll & pause & exit /b 1)
if not exist lib\PCL.Methods.gz (echo [错误] 缺少 lib\PCL.Methods.gz & pause & exit /b 1)
if not exist lib\PCL.Methods.json (echo [错误] 缺少 lib\PCL.Methods.json & pause & exit /b 1)

if not exist Releases mkdir Releases

"%MSBUILD%" PCL2Patch.csproj /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU /v:minimal /nologo

if %ERRORLEVEL%==0 (echo. & echo 编译成功：Releases\PCL2一键补丁.exe) else (echo. & echo 编译失败，请检查上方错误信息。)
pause
