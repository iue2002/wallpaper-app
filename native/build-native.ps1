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

# XAML 诊断的中介 DLL。
#
# InitializeXamlDiagnosticsEx 的第三个参数是 XamlDiagnostics.dll 的路径；传 nullptr
# 时系统会去 C:\Windows 下找，而 Win11 26200 的 System32 里没有这个文件，于是直接
# 返回 0x80070490(ERROR_NOT_FOUND)，注入永远起不来。这个 DLL 由 Windows SDK 分发，
# 自 Win10 起版本就没变过（10.0.14393），依赖只有 msvcrt/CoreMessaging/OLEAUT32。
$kitsBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$xamlDiagnostics = $null
foreach ($arch in @('x64', 'x86', 'arm64')) {
    $candidate = Join-Path $kitsBin "$arch\XamlDiagnostics\xamldiagnostics.dll"
    if (Test-Path $candidate) {
        $xamlDiagnostics = $candidate
        break
    }
}

if ($xamlDiagnostics) {
    Copy-Item $xamlDiagnostics (Join-Path $root 'dist\xamldiagnostics.dll') -Force
    Write-Host "已复制：$xamlDiagnostics"
} else {
    Write-Warning '没找到 xamldiagnostics.dll（Windows SDK 的 XamlDiagnostics 目录），任务栏透明会因 0x80070490 失败'
}
