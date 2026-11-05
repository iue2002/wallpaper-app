param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('x64')]
    [string]$Platform = 'x64',

    [string]$Version,

    [string]$OutputDirectory = "$PSScriptRoot\..\artifacts\store"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$appProjectRoot = Join-Path $PSScriptRoot '..'
$projectFile = Get-ChildItem -Path $appProjectRoot -Filter *.csproj -File | Select-Object -First 1 -ExpandProperty FullName
$manifestFile = Join-Path $appProjectRoot 'Package.appxmanifest'
$vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuildPath = $null

if ([string]::IsNullOrWhiteSpace($projectFile)) {
    throw "No .csproj file was found in: $appProjectRoot"
}

if (-not (Test-Path $manifestFile)) {
    throw "Manifest file not found: $manifestFile"
}

if (Test-Path $vswherePath) {
    $msbuildPath = & $vswherePath -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
}

if (-not (Test-Path $OutputDirectory)) {
    New-Item -Path $OutputDirectory -ItemType Directory -Force | Out-Null
}

if (-not [string]::IsNullOrWhiteSpace($Version)) {
    if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw "Version must be in format Major.Minor.Build.Revision, e.g. 1.0.1.0"
    }

    [xml]$manifestXml = Get-Content -Path $manifestFile -Raw -Encoding UTF8
    $identityNode = $manifestXml.Package.Identity

    if ($null -eq $identityNode) {
        throw "Identity node was not found in manifest: $manifestFile"
    }

    $identityNode.Version = $Version
    $manifestXml.Save($manifestFile)
    Write-Host "Updated manifest version to: $Version"
}

Write-Host 'Restoring project dependencies...'
dotnet restore $projectFile

if ($LASTEXITCODE -ne 0) {
    throw 'dotnet restore failed.'
}

$resolvedOutputDir = (Resolve-Path $OutputDirectory).Path
if (-not $resolvedOutputDir.EndsWith('\')) {
    $resolvedOutputDir = "$resolvedOutputDir\"
}

Write-Host 'Building MSIX upload package for Microsoft Store...'
if (-not [string]::IsNullOrWhiteSpace($msbuildPath)) {
    Write-Host "Using MSBuild: $msbuildPath"
    & $msbuildPath $projectFile `
        /restore `
        /t:Build `
        /p:Configuration=$Configuration `
        /p:Platform=$Platform `
        /p:RuntimeIdentifier=win-x64 `
        /p:SelfContained=true `
        /p:GenerateAppxPackageOnBuild=true `
        /p:AppxBundle=Never `
        /p:UapAppxPackageBuildMode=StoreUpload `
        /p:AppxPackageSigningEnabled=false `
        /p:AppxPackageDir="$resolvedOutputDir"
}
else {
    Write-Warning 'Visual Studio MSBuild.exe was not found. Falling back to dotnet msbuild.'
    dotnet msbuild $projectFile `
        /t:Build `
        /p:Configuration=$Configuration `
        /p:Platform=$Platform `
        /p:RuntimeIdentifier=win-x64 `
        /p:SelfContained=true `
        /p:GenerateAppxPackageOnBuild=true `
        /p:AppxBundle=Never `
        /p:UapAppxPackageBuildMode=StoreUpload `
        /p:AppxPackageSigningEnabled=false `
        /p:AppxPackageDir="$resolvedOutputDir"
}

if ($LASTEXITCODE -ne 0) {
    throw 'MSIX build failed.'
}

$msixuploadFiles = Get-ChildItem -Path $resolvedOutputDir -Filter *.msixupload -File -Recurse
if (-not $msixuploadFiles) {
    Write-Warning "No .msixupload file found under: $resolvedOutputDir"
    Write-Host 'Package output may still exist as .msix or .appxbundle depending on your SDK/tooling settings.'
} else {
    Write-Host 'Generated Store upload package(s):'
    $msixuploadFiles | ForEach-Object { Write-Host " - $($_.FullName)" }
}

Write-Host "Done. Output directory: $resolvedOutputDir"
