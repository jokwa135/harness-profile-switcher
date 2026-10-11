[CmdletBinding()]
param([string]$SessionId = ('dashboard-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)), [switch]$BuildOnly)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($root -ne 'D:\AI\Tools\codex-profile-switcher' -or $SessionId -notmatch '^[a-zA-Z0-9-]+$') { throw 'Invalid isolated build boundary/session.' }
$out = Join-Path $root ('artifacts\Development\' + $SessionId)
if (Test-Path -LiteralPath $out) { throw 'Session output already exists; choose a fresh session, never overwrite artifacts.' }
New-Item -ItemType Directory -Path $out | Out-Null
$temp = Join-Path $out 'Temp'
New-Item -ItemType Directory -Path $temp | Out-Null
$previousTemp = $env:TEMP; $previousTmp = $env:TMP; $previousTestRoot = $env:HPS_TEST_ROOT
$previousPackage = $env:HPS_TEST_FAKE_PACKAGE_EXE; $previousOutside = $env:HPS_TEST_OUTSIDE_CODEX_EXE
$release = Join-Path $root 'artifacts\Release\HarnessProfileSwitcher.exe'
$releaseHash = (Get-FileHash -LiteralPath $release -Algorithm SHA256).Hash
# Only observe process identities; never send a message, stop, restart, or access credentials.
$existing = @(Get-Process | Where-Object { $_.ProcessName -in @('ChatGPT','codex','HarnessProfileSwitcher') } | ForEach-Object { [pscustomobject]@{ Id = $_.Id; Name = $_.ProcessName; Started = $_.StartTime } })
try {
    $env:TEMP = $temp; $env:TMP = $temp
    $env:HPS_TEST_ROOT = Join-Path $root ('artifacts\F\' + [Guid]::NewGuid().ToString('N').Substring(0,8))
    if (Test-Path -LiteralPath $env:HPS_TEST_ROOT) { throw 'Fixture directory already exists.' }
    New-Item -ItemType Directory -Path $env:HPS_TEST_ROOT | Out-Null
    Write-Output "SESSION: $SessionId"
    Write-Output "FIXTURES: $env:HPS_TEST_ROOT"
    $compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $framework = Split-Path -Parent $compiler
    $references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Management.dll','System.Runtime.Serialization.dll','System.Security.dll','System.Windows.Forms.dll','System.Xml.dll','Microsoft.VisualBasic.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
    $source = @(Get-ChildItem -LiteralPath (Join-Path $root 'src\HarnessProfileSwitcher') -Recurse -File -Filter '*.cs' | Sort-Object FullName | Select-Object -ExpandProperty FullName)
    $app = Join-Path $out 'HarnessProfileSwitcher.exe'
    & $compiler /nologo /target:winexe /platform:x64 /checked+ /warn:4 /utf8output /optimize+ /debug:pdbonly "/out:$app" ('/win32icon:' + (Join-Path $root 'src\HarnessProfileSwitcher\assets\app.ico')) @references @source
    if ($LASTEXITCODE -ne 0) { throw "Application compilation failed: $LASTEXITCODE" }
    if (-not $BuildOnly) {
        $fake = Join-Path $root 'tests\HarnessProfileSwitcher.Tests\FakeChatGptProcess.cs'
        $package = Join-Path $env:HPS_TEST_ROOT 'FakePackage'; $outside = Join-Path $env:HPS_TEST_ROOT 'OutsidePackage'
        New-Item -ItemType Directory -Path $package,$outside | Out-Null
        $env:HPS_TEST_FAKE_PACKAGE_EXE = Join-Path $package 'ChatGPT.exe'
        $env:HPS_TEST_OUTSIDE_CODEX_EXE = Join-Path $outside 'codex.exe'
        foreach ($helper in @($env:HPS_TEST_FAKE_PACKAGE_EXE,$env:HPS_TEST_OUTSIDE_CODEX_EXE)) {
            & $compiler /nologo /target:exe /platform:x64 "/out:$helper" /main:HarnessProfileSwitcher.Tests.FakeChatGptProcess $fake
            if ($LASTEXITCODE -ne 0) { throw 'Fake process compilation failed.' }
        }
        $tests = @(Get-ChildItem -LiteralPath (Join-Path $root 'tests\HarnessProfileSwitcher.Tests') -File -Filter '*.cs' | Sort-Object FullName | Select-Object -ExpandProperty FullName)
        $testExe = Join-Path $out 'HarnessProfileSwitcher.EngineTests.exe'
        & $compiler /nologo /target:exe /platform:x64 /checked+ /warn:4 /utf8output "/out:$testExe" /main:HarnessProfileSwitcher.Tests.EngineFixtureTests @references @source @tests
        if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
        & $testExe
        if ($LASTEXITCODE -ne 0) { throw 'Regression/dashboard fixture tests failed.' }
        & $testExe --non-live-e2e
        if ($LASTEXITCODE -ne 0) { throw 'Existing non-live E2E journey failed.' }
        $captures = Join-Path $out 'Screenshots'
        New-Item -ItemType Directory -Path $captures | Out-Null
        foreach ($mode in @('active','pending','recovery','dialog-rename','dialog-rename-validation','quota-cached','small-unicode','active-150','active-200','small-unicode-150','small-unicode-200','dialog-rename-150','dialog-rename-200')) {
            $capture = Join-Path $captures ($mode + '.png')
            $process = Start-Process -FilePath $app -ArgumentList @('--render-preview',$mode,('"'+$capture+'"')) -PassThru
            if (-not $process.WaitForExit(20000)) { throw 'Bounded isolated preview timed out; no other process will be stopped.' }
            if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $capture)) { throw "Preview failed: $mode" }
            Write-Output "CAPTURE: $capture"
        }
    }
    Write-Output "SESSION: $SessionId"
    Write-Output "EXE: $app"
    Write-Output ('SHA256: ' + (Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash.ToLowerInvariant())
} finally {
    if ((Get-FileHash -LiteralPath $release -Algorithm SHA256).Hash -ne $releaseHash) { throw 'Existing Release executable changed.' }
    foreach ($identity in $existing) {
        $process = Get-Process -Id $identity.Id -ErrorAction SilentlyContinue
        if ($null -eq $process -or $process.StartTime -ne $identity.Started) { throw ('Previously running process exited or changed during QA: ' + $identity.Name + ' PID ' + $identity.Id) }
    }
    Write-Output 'PRESERVATION: existing Release hash and observed existing ChatGPT/Codex/switcher process identities unchanged.'
    $env:TEMP = $previousTemp; $env:TMP = $previousTmp; $env:HPS_TEST_ROOT = $previousTestRoot
    $env:HPS_TEST_FAKE_PACKAGE_EXE = $previousPackage; $env:HPS_TEST_OUTSIDE_CODEX_EXE = $previousOutside
}
