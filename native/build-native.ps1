# 编译任务栏透明用的注入模块（x64 Release），产物固定输出到 native\dist\ExplorerTap.dll。
#
# 为什么要单独编：vcxproj 只有完整版 MSBuild 能编，dotnet build 带不动它。
# 所以改了 native\ExplorerTap 下的 C++ 之后，手动跑一次这个脚本，
# 再正常 dotnet build 主程序即可（csproj 会把 dist 里的 DLL 复制到输出目录）。
#
# 用法：powershell -ExecutionPolicy Bypass -File native\build-native.ps1

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$project = Join-Path $root 'ExplorerTap\ExplorerTap.vcxproj'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    throw '找不到 vswhere.exe，请确认已安装 Visual Studio 2022（含 C++ 桌面开发工作负载）'
}

$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
    Select-Object -First 1

if (-not $msbuild) {
    throw '找不到 MSBuild.exe，请确认 Visual Studio 2022 已安装'
}

Write-Host "MSBuild: $msbuild"
& $msbuild $project /p:Configuration=Release /p:Platform=x64 /v:m /nologo

if ($LASTEXITCODE -ne 0) {
    throw "编译失败，退出码 $LASTEXITCODE"
}

$dll = Join-Path $root 'dist\ExplorerTap.dll'
if (-not (Test-Path $dll)) {
    throw "编译完成但没找到产物：$dll"
}

Write-Host "已生成：$dll"

# XAML 诊断的中介 DLL —— 已停用，不要再自动分发。
#
# 曾按 b9c8750 的思路把 SDK 里的 xamldiagnostics.dll（10.0.14393，2016 年版）随程序
# 分发并显式传给 InitializeXamlDiagnosticsEx。实测结果（Win11 26200）：
# 1. 传完整路径仍然 60 次全 0x80070490，问题不在"系统找不到这个文件"；
# 2. 注入后 explorer 崩溃重启过一次 —— 这份老 DLL 与 26100 的 XAML 不兼容，
#    带进 explorer 有实际风险。
# 结论：XAML 诊断在这类系统上根本起不来（TranslucentTB 在 Win11 24H2+ 上
# 大量用户报同样的 0x80070490，是系统层问题）。保持停用，恢复前先想清楚。
# dist\xamldiagnostics.disabled.dll 留作证据，改名回 xamldiagnostics.dll 即恢复旧行为。
