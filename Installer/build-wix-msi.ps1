param(
    [ValidateSet("win-x64", "win-x86", "win-arm64")]
    [string]$Runtime = "win-x64",
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $projectRoot "artifacts\msi-input\$Runtime"
$outDir = Join-Path $projectRoot "artifacts\msi"

if (-not (Test-Path $publishDir)) {
    throw "����Ŀ¼������: $publishDir���������� .\build-msi-ready.ps1 -Runtime $Runtime"
}

New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$arch = switch ($Runtime) {
    "win-x64" { "x64" }
    "win-x86" { "x86" }
    "win-arm64" { "arm64" }
}

$productDisplayName = '小K壁纸'
$safeName = $productDisplayName -replace '[\\/:*?"<>| ]','_'
$msiPath = Join-Path $outDir ("{0}-{1}-{2}.msi" -f $safeName, $Version, $Runtime)
$wxsPath = Join-Path $PSScriptRoot "Product.wxs"

Write-Host "[WiX] build: $wxsPath"
Write-Host "[WiX] publishDir: $publishDir"
Write-Host "[WiX] output: $msiPath"

wix build $wxsPath `
  -arch $arch `
  -d PublishDir="$publishDir" `
  -d Version="$Version" `
  -o "$msiPath"

if ($LASTEXITCODE -ne 0) {
    throw "WiX ����ʧ�ܣ��˳���: $LASTEXITCODE"
}

Write-Host "[WiX] MSI ���ɳɹ�: $msiPath"
