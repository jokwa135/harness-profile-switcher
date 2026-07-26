[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if (-not $projectRoot.StartsWith('D:\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Engine tests must run from D:. Resolved root: $projectRoot"
}

$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "Required Windows C# compiler was not found: $compiler"
}

$frameworkDirectory = Split-Path -Parent $compiler
$references = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Management.dll',
    'System.Runtime.Serialization.dll',
    'System.Security.dll',
    'System.Windows.Forms.dll',
    'System.Xml.dll',
    'Microsoft.VisualBasic.dll'
) | ForEach-Object { Join-Path $frameworkDirectory $_ }

$missingReferences = @($references | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
if ($missingReferences.Count -gt 0) {
    throw "Required framework references are missing: $($missingReferences -join ', ')"
}

$sourceFiles = @(
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src\HarnessProfileSwitcher') -Recurse -File -Filter '*.cs'
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests\HarnessProfileSwitcher.Tests') -File -Filter '*.cs'
) | Sort-Object FullName
if ($sourceFiles.Count -eq 0) {
    throw 'No engine test source files were found.'
}

$outputDirectory = Join-Path $projectRoot 'artifacts\Tests'
$temporaryDirectory = 'D:\AI\Temp\HarnessProfileSwitcher\Compiler'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null
$outputPath = Join-Path $outputDirectory 'HarnessProfileSwitcher.EngineTests.exe'
$fakeSourcePath = Join-Path $projectRoot 'tests\HarnessProfileSwitcher.Tests\FakeChatGptProcess.cs'
$fakePackageDirectory = Join-Path $outputDirectory 'FakePackage'
$outsidePackageDirectory = Join-Path $outputDirectory 'OutsidePackage'
$fakePackagePath = Join-Path $fakePackageDirectory 'ChatGPT.exe'
$outsideCodexPath = Join-Path $outsidePackageDirectory 'codex.exe'
New-Item -ItemType Directory -Path $fakePackageDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $outsidePackageDirectory -Force | Out-Null

if (-not (Test-Path -LiteralPath $fakeSourcePath -PathType Leaf)) {
    throw "Fake process source was not found: $fakeSourcePath"
}

$arguments = @(
    '/nologo',
    '/target:exe',
    '/platform:x64',
    '/checked+',
    '/warn:4',
    '/utf8output',
    "/out:$outputPath",
    '/main:HarnessProfileSwitcher.Tests.EngineFixtureTests'
)
foreach ($reference in $references) {
    $arguments += "/reference:$reference"
}
$arguments += @($sourceFiles | Select-Object -ExpandProperty FullName)

$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$previousFakePackagePath = $env:HPS_TEST_FAKE_PACKAGE_EXE
$previousOutsideCodexPath = $env:HPS_TEST_OUTSIDE_CODEX_EXE
try {
    $env:TEMP = $temporaryDirectory
    $env:TMP = $temporaryDirectory

    foreach ($helperOutput in @($fakePackagePath, $outsideCodexPath)) {
        $helperArguments = @(
            '/nologo',
            '/target:exe',
            '/platform:x64',
            '/checked+',
            '/warn:4',
            '/utf8output',
            "/out:$helperOutput",
            '/main:HarnessProfileSwitcher.Tests.FakeChatGptProcess',
            $fakeSourcePath
        )
        & $compiler @helperArguments
        if ($LASTEXITCODE -ne 0) {
            throw "Fake process compilation failed with exit code $LASTEXITCODE."
        }
    }

    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Engine test compilation failed with exit code $LASTEXITCODE."
    }

    $env:HPS_TEST_FAKE_PACKAGE_EXE = $fakePackagePath
    $env:HPS_TEST_OUTSIDE_CODEX_EXE = $outsideCodexPath
    & $outputPath
    if ($LASTEXITCODE -ne 0) {
        throw "Engine fixture tests failed with exit code $LASTEXITCODE."
    }
} finally {
    $env:HPS_TEST_FAKE_PACKAGE_EXE = $previousFakePackagePath
    $env:HPS_TEST_OUTSIDE_CODEX_EXE = $previousOutsideCodexPath
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
}
