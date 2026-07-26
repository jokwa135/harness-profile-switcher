[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if (-not $projectRoot.StartsWith('D:\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Non-live validation must run from D:. Resolved root: $projectRoot"
}

function Get-PathKind {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return 'missing'
    }

    $item = Get-Item -LiteralPath $Path -Force
    $reparse = (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
    if ($item.PSIsContainer) {
        if ($reparse) { return 'directory-reparse-point' }
        return 'directory'
    }
    if ($reparse) { return 'file-reparse-point' }
    return 'file'
}

function Get-LiveBoundarySnapshot {
    $userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    $roaming = [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)
    return [ordered]@{
        ProductionRuntime = Get-PathKind 'D:\AI\Data\HarnessProfileSwitcher'
        LiveAuth = Get-PathKind (Join-Path $userProfile '.codex\auth.json')
        AuthStaging = Get-PathKind (Join-Path $userProfile '.codex\.hps-auth')
        LiveBrowser = Get-PathKind (Join-Path $roaming 'Codex\web\Codex')
        BrowserStaging = Get-PathKind (Join-Path $roaming 'Codex\web\.hps-links')
    }
}

function Assert-BoundaryUnchanged {
    param(
        [Parameter(Mandatory = $true)]$Before,
        [Parameter(Mandatory = $true)]$After
    )

    foreach ($name in $Before.Keys) {
        if (-not [string]::Equals([string]$Before[$name], [string]$After[$name], [StringComparison]::Ordinal)) {
            throw "Live boundary changed during non-live validation: $name was '$($Before[$name])' and is now '$($After[$name])'."
        }
    }
}

function Assert-Png {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Preview was not created: $Path"
    }
    $signature = @(Get-Content -LiteralPath $Path -Encoding Byte -TotalCount 8)
    $signatureHex = -join @($signature | ForEach-Object { $_.ToString('x2') })
    if ($signatureHex -ne '89504e470d0a1a0a') {
        throw "Preview is not a valid PNG: $Path"
    }

    Add-Type -AssemblyName System.Drawing
    $image = [Drawing.Image]::FromFile($Path)
    try {
        if ($image.Width -lt 400 -or $image.Height -lt 300) {
            throw "Preview dimensions are unexpectedly small: $($image.Width)x$($image.Height) at $Path"
        }
        return [pscustomobject]@{
            Path = $Path
            Width = $image.Width
            Height = $image.Height
            Bytes = (Get-Item -LiteralPath $Path).Length
        }
    } finally {
        $image.Dispose()
    }
}

function Assert-PackageChecksums {
    param([Parameter(Mandatory = $true)][string]$PackageDirectory)

    $checksumPath = Join-Path $PackageDirectory 'SHA256SUMS.txt'
    if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
        throw "Package checksum list is missing: $checksumPath"
    }
    foreach ($line in @(Get-Content -LiteralPath $checksumPath -Encoding ascii)) {
        if ($line -notmatch '^([0-9a-fA-F]{64}) \*(.+)$') {
            throw "Malformed package checksum line: $line"
        }
        $expected = $Matches[1].ToLowerInvariant()
        $relative = $Matches[2].Replace('/', '\')
        $candidate = [IO.Path]::GetFullPath((Join-Path $PackageDirectory $relative))
        $packagePrefix = $PackageDirectory.TrimEnd('\') + '\'
        if (-not $candidate.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Package checksum path escapes its package directory: $relative"
        }
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            throw "Checksummed package file is missing: $relative"
        }
        $actual = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) {
            throw "Package checksum mismatch: $relative"
        }
    }
}

$boundaryBefore = Get-LiveBoundarySnapshot
$previewResults = @()
$packageDirectory = $null
$zipHash = $null
$validationRoot = $null

try {
    & (Join-Path $PSScriptRoot 'test-engine.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw "Regression fixtures failed with exit code $LASTEXITCODE."
    }

    $testExecutable = Join-Path $projectRoot 'artifacts\Tests\HarnessProfileSwitcher.EngineTests.exe'
    & $testExecutable '--non-live-e2e'
    if ($LASTEXITCODE -ne 0) {
        throw "Isolated end-to-end journey failed with exit code $LASTEXITCODE."
    }

    $packageRoot = Join-Path $projectRoot 'artifacts\Packages'
    $packages = @(Get-ChildItem -LiteralPath $packageRoot -Directory | Sort-Object LastWriteTimeUtc -Descending)
    if ($packages.Count -eq 0) {
        throw "No local package was found under $packageRoot."
    }
    $packageDirectory = $packages[0].FullName
    $packageExecutable = Join-Path $packageDirectory 'HarnessProfileSwitcher.exe'
    $manifestPath = Join-Path $packageDirectory 'PACKAGE-MANIFEST.json'
    if (-not (Test-Path -LiteralPath $packageExecutable -PathType Leaf)) {
        throw "Packaged executable is missing: $packageExecutable"
    }
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Package manifest is missing: $manifestPath"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Encoding utf8 -Raw | ConvertFrom-Json
    if ($manifest.product -ne 'Harness Profile Switcher' -or $manifest.packageKind -ne 'private-local-portable') {
        throw 'The latest package does not identify itself as the private local Harness Profile Switcher package.'
    }
    Assert-PackageChecksums $packageDirectory

    $zipPath = $packageDirectory + '.zip'
    $zipChecksumPath = $zipPath + '.sha256'
    if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf) -or -not (Test-Path -LiteralPath $zipChecksumPath -PathType Leaf)) {
        throw 'The latest package archive or its checksum file is missing.'
    }
    $checksumLine = (Get-Content -LiteralPath $zipChecksumPath -Encoding ascii -Raw).Trim()
    if ($checksumLine -notmatch '^([0-9a-fA-F]{64}) \*[^\\/]+\.zip$') {
        throw "Malformed archive checksum file: $zipChecksumPath"
    }
    $expectedZipHash = $Matches[1].ToLowerInvariant()
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($zipHash -ne $expectedZipHash) {
        throw "Package archive checksum mismatch: $zipPath"
    }

    $validationName = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $validationRoot = Join-Path (Join-Path $projectRoot 'artifacts\NonLiveE2E') $validationName
    New-Item -ItemType Directory -Path $validationRoot | Out-Null

    $processIdsBefore = @(Get-Process -Name 'HarnessProfileSwitcher' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    foreach ($mode in @('active', 'pending', 'recovery')) {
        $previewPath = Join-Path $validationRoot ($mode + '.png')
        $previewProcess = Start-Process -FilePath $packageExecutable `
            -ArgumentList @('--render-preview', $mode, ('"' + $previewPath + '"')) `
            -PassThru -Wait -WindowStyle Hidden
        if ($previewProcess.ExitCode -ne 0) {
            throw "Packaged preview '$mode' failed with exit code $($previewProcess.ExitCode)."
        }
        $previewResults += Assert-Png $previewPath
    }
    $processIdsAfter = @(Get-Process -Name 'HarnessProfileSwitcher' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    $lingering = @($processIdsAfter | Where-Object { $processIdsBefore -notcontains $_ })
    if ($lingering.Count -gt 0) {
        throw "Packaged preview left HarnessProfileSwitcher process IDs running: $($lingering -join ', ')"
    }

    $dimensions = @($previewResults | ForEach-Object { "$($_.Width)x$($_.Height)" } | Select-Object -Unique)
    if ($dimensions.Count -ne 1) {
        throw "Packaged preview modes rendered at inconsistent dimensions: $($dimensions -join ', ')"
    }
} finally {
    $boundaryAfter = Get-LiveBoundarySnapshot
    Assert-BoundaryUnchanged $boundaryBefore $boundaryAfter
}

Write-Output 'PASS: non-live end-to-end validation'
[pscustomobject]@{
    Package = Split-Path -Leaf $packageDirectory
    ArchiveSha256 = $zipHash
    PreviewModes = ($previewResults | ForEach-Object { Split-Path -Leaf $_.Path }) -join ', '
    PreviewDimensions = if ($previewResults.Count -gt 0) { "$($previewResults[0].Width)x$($previewResults[0].Height)" } else { '' }
    PreviewOutput = $validationRoot
    LiveBoundary = 'unchanged'
} | Format-List
