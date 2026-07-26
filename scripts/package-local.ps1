[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipBuild,

    [ValidateSet('fixture-tested-live-validation-pending', 'live-validated')]
    [string]$ValidationStatus = 'fixture-tested-live-validation-pending'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if (-not $projectRoot.StartsWith('D:\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Local packages must be produced from D:. Resolved root: $projectRoot"
}

$versionPath = Join-Path $projectRoot 'VERSION'
if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf)) {
    throw "Version file is missing: $versionPath"
}
$version = (Get-Content -LiteralPath $versionPath -Encoding utf8 -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+-local$') {
    throw "VERSION must use the private local format 0.0.0-local. Found: $version"
}

if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot 'test-engine.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw "Fixture tests failed with exit code $LASTEXITCODE."
    }
}

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw "Release build failed with exit code $LASTEXITCODE."
    }
}

$releaseExecutable = Join-Path $projectRoot 'artifacts\Release\HarnessProfileSwitcher.exe'
if (-not (Test-Path -LiteralPath $releaseExecutable -PathType Leaf)) {
    throw "Release executable is missing: $releaseExecutable"
}
$releaseVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($releaseExecutable).ProductVersion
if (-not [string]::Equals($releaseVersion, $version, [StringComparison]::Ordinal)) {
    throw "Release executable version '$releaseVersion' does not match VERSION '$version'."
}

$packageRoot = Join-Path $projectRoot 'artifacts\Packages'
$timestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$packageName = "HarnessProfileSwitcher-$version-$timestamp"
$packageDirectory = Join-Path $packageRoot $packageName
$zipPath = Join-Path $packageRoot ($packageName + '.zip')
$zipChecksumPath = $zipPath + '.sha256'

foreach ($path in @($packageDirectory, $zipPath, $zipChecksumPath)) {
    if (Test-Path -LiteralPath $path) {
        throw "Immutable package output already exists: $path"
    }
}

New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
New-Item -ItemType Directory -Path $packageDirectory | Out-Null
New-Item -ItemType Directory -Path (Join-Path $packageDirectory 'docs') | Out-Null
New-Item -ItemType Directory -Path (Join-Path $packageDirectory 'tools') | Out-Null

Copy-Item -LiteralPath $releaseExecutable -Destination (Join-Path $packageDirectory 'HarnessProfileSwitcher.exe')
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $packageDirectory 'README.md')
Copy-Item -LiteralPath $versionPath -Destination (Join-Path $packageDirectory 'VERSION')
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\operator-guide.md') -Destination (Join-Path $packageDirectory 'docs\operator-guide.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\current-chatgpt-boundary.md') -Destination (Join-Path $packageDirectory 'docs\current-chatgpt-boundary.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'scripts\inspect-chatgpt-boundary.ps1') -Destination (Join-Path $packageDirectory 'tools\inspect-chatgpt-boundary.ps1')

$payload = @(Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relativePath = $_.FullName.Substring($packageDirectory.Length).TrimStart('\').Replace('\', '/')
    [ordered]@{
        path = $relativePath
        bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})

$manifest = [ordered]@{
    schemaVersion = 1
    product = 'Harness Profile Switcher'
    version = $version
    packageKind = 'private-local-portable'
    createdAtUtc = [DateTime]::UtcNow.ToString('o')
    validationStatus = $ValidationStatus
    targetPlatform = 'windows-x64'
    files = $payload
}
$manifestPath = Join-Path $packageDirectory 'PACKAGE-MANIFEST.json'
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8

$checksumFiles = @(Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | Sort-Object FullName)
$checksumLines = @($checksumFiles | ForEach-Object {
    $relativePath = $_.FullName.Substring($packageDirectory.Length).TrimStart('\').Replace('\', '/')
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash *$relativePath"
})
$checksumPath = Join-Path $packageDirectory 'SHA256SUMS.txt'
$checksumLines | Set-Content -LiteralPath $checksumPath -Encoding ascii

Compress-Archive -LiteralPath $packageDirectory -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$zipHash *$(Split-Path -Leaf $zipPath)" | Set-Content -LiteralPath $zipChecksumPath -Encoding ascii

[pscustomobject]@{
    Version = $version
    PackageDirectory = $packageDirectory
    Archive = $zipPath
    ArchiveSha256 = $zipHash
    ArchiveChecksumFile = $zipChecksumPath
} | Format-List
