param(
    [ValidateSet("win-x64", "win-x86", "win-arm64")]
    [string]$Runtime = "win-x64",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "小K壁纸.csproj"
$outDir = Join-Path $PSScriptRoot "artifacts\msi-input\$Runtime"

if ($Clean -and (Test-Path $outDir)) {
    Remove-Item $outDir -Recurse -Force
}

Write-Host "[MSI] 开始发布自包含版本: $Runtime"
Write-Host "[MSI] 输出目录: $outDir"

dotnet publish $project `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -o $outDir

Write-Host "[MSI] 发布完成。"
Write-Host "[MSI] 请把以下目录中的全部文件打进 MSI（不要只放 exe）："
Write-Host "      $outDir"
