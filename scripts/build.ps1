[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if (-not $projectRoot.StartsWith('D:\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Harness Profile Switcher must build from D:. Resolved root: $projectRoot"
}

$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "Required Windows C# compiler was not found: $compiler"
}

$frameworkDirectory = Split-Path -Parent $compiler
$referenceNames = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Management.dll',
    'System.Runtime.Serialization.dll',
    'System.Security.dll',
    'System.Windows.Forms.dll',
    'System.Xml.dll',
    'Microsoft.VisualBasic.dll'
)
$references = @($referenceNames | ForEach-Object { Join-Path $frameworkDirectory $_ })
$missingReferences = @($references | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
if ($missingReferences.Count -gt 0) {
    throw "Required framework references are missing: $($missingReferences -join ', ')"
}

$compilerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($compiler).FileVersion
if ($VerifyOnly) {
    Write-Host "Toolchain verified: C# compiler $compilerVersion"
    Write-Host "Compiler: $compiler"
    Write-Host "Workspace: $projectRoot"
    exit 0
}

$sourceRoot = Join-Path $projectRoot 'src\HarnessProfileSwitcher'
$sourceFiles = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Filter '*.cs' | Sort-Object FullName)
if ($sourceFiles.Count -eq 0) {
    throw "No C# source files were found under $sourceRoot"
}

$outputDirectory = Join-Path $projectRoot (Join-Path 'artifacts' $Configuration)
$temporaryDirectory = Join-Path 'D:\AI\Temp\HarnessProfileSwitcher' $Configuration
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null

$outputPath = Join-Path $outputDirectory 'HarnessProfileSwitcher.exe'
$compilerArguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:x64',
    '/checked+',
    '/warn:4',
    '/utf8output',
    "/out:$outputPath"
)

$applicationIcon = Join-Path $sourceRoot 'assets\app.ico'
if (Test-Path -LiteralPath $applicationIcon -PathType Leaf) {
    $compilerArguments += "/win32icon:$applicationIcon"
}

if ($Configuration -eq 'Release') {
    $compilerArguments += '/optimize+'
    $compilerArguments += '/debug:pdbonly'
    $compilerArguments += '/define:TRACE'
} else {
    $compilerArguments += '/optimize-'
    $compilerArguments += '/debug:full'
    $compilerArguments += '/define:DEBUG;TRACE'
}

foreach ($reference in $references) {
    $compilerArguments += "/reference:$reference"
}
$compilerArguments += @($sourceFiles | Select-Object -ExpandProperty FullName)

$previousTemp = $env:TEMP
$previousTmp = $env:TMP
try {
    $env:TEMP = $temporaryDirectory
    $env:TMP = $temporaryDirectory
    & $compiler @compilerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "C# compilation failed with exit code $LASTEXITCODE."
    }
} finally {
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
}

if (-not (Test-Path -LiteralPath $outputPath -PathType Leaf)) {
    throw "Compilation completed without producing the expected executable: $outputPath"
}

Write-Host "Built: $outputPath"
