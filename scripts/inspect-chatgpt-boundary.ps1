[CmdletBinding()]
param(
    [switch]$RequireRunning,
    [switch]$FailOnIncompatible,
    [switch]$AsJson
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Add-Issue {
    param(
        [Collections.Generic.List[string]]$Issues,
        [string]$Message
    )

    $Issues.Add($Message)
}

function Get-UserDataDirectories {
    param([object[]]$Processes)

    $results = New-Object 'Collections.Generic.List[string]'
    foreach ($process in $Processes) {
        $commandLine = [string]$process.CommandLine
        if ($commandLine -match '--user-data-dir=("[^"]+"|[^\s]+)') {
            $value = $matches[1].Trim('"')
            if (-not $results.Contains($value)) {
                $results.Add($value)
            }
        }
    }
    return @($results | Sort-Object)
}

$issues = New-Object 'Collections.Generic.List[string]'
$package = Get-AppxPackage -Name 'OpenAI.Codex' -ErrorAction SilentlyContinue
if ($null -eq $package) {
    throw 'The Microsoft Store OpenAI.Codex package is not installed for the current user.'
}

$manifest = Get-AppxPackageManifest -Package $package
$applications = @($manifest.Package.Applications.Application)
$application = @($applications | Where-Object { $_.Id -eq 'App' } | Select-Object -First 1)
if ($application.Count -eq 0) {
    Add-Issue $issues 'The package manifest does not contain application ID App.'
    $application = @($applications | Select-Object -First 1)
}

$applicationId = if ($application.Count -gt 0) { [string]$application[0].Id } else { '' }
$manifestExecutable = if ($application.Count -gt 0) { [string]$application[0].Executable } else { '' }
$manifestEntryPoint = if ($application.Count -gt 0) { [string]$application[0].EntryPoint } else { '' }
$normalizedExecutable = $manifestExecutable.Replace('/', '\')
$executablePath = if ([string]::IsNullOrWhiteSpace($normalizedExecutable)) { '' } else { Join-Path $package.InstallLocation $normalizedExecutable }
$appUserModelId = if ([string]::IsNullOrWhiteSpace($applicationId)) { '' } else { $package.PackageFamilyName + '!' + $applicationId }

if ($manifestExecutable -notmatch '(^|[\\/])ChatGPT\.exe$') {
    Add-Issue $issues "The manifest executable is no longer ChatGPT.exe: $manifestExecutable"
}
if ($manifestEntryPoint -ne 'Windows.FullTrustApplication') {
    Add-Issue $issues "The manifest entry point changed: $manifestEntryPoint"
}
if ([string]::IsNullOrWhiteSpace($executablePath) -or -not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    Add-Issue $issues "The manifest executable does not exist: $executablePath"
}

$startApplications = @(Get-StartApps | Where-Object { $_.AppID -eq $appUserModelId })
if ($startApplications.Count -eq 0) {
    Add-Issue $issues "The stable AppUserModelID is not registered: $appUserModelId"
}

$packageProcesses = @(Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -and $_.ExecutablePath.StartsWith($package.InstallLocation, [StringComparison]::OrdinalIgnoreCase)
})
$chatGptProcesses = @($packageProcesses | Where-Object { $_.Name -ieq 'ChatGPT.exe' })
$mainProcesses = @($chatGptProcesses | Where-Object { ([string]$_.CommandLine) -notmatch '\s--type=' })
$helperProcesses = @($packageProcesses | Where-Object { $_.Name -ieq 'codex.exe' })
$userDataDirectories = @(Get-UserDataDirectories -Processes $packageProcesses)

if ($RequireRunning -and $mainProcesses.Count -ne 1) {
    Add-Issue $issues "Expected exactly one running main ChatGPT.exe process; found $($mainProcesses.Count)."
}

$configuredCodexHome = [Environment]::GetEnvironmentVariable('CODEX_HOME', 'Process')
if ([string]::IsNullOrWhiteSpace($configuredCodexHome)) {
    $configuredCodexHome = [Environment]::GetEnvironmentVariable('CODEX_HOME', 'User')
}
if ([string]::IsNullOrWhiteSpace($configuredCodexHome)) {
    $configuredCodexHome = [Environment]::GetEnvironmentVariable('CODEX_HOME', 'Machine')
}

$codexHomeSource = 'default-user-profile'
$codexHome = Join-Path $env:USERPROFILE '.codex'
if (-not [string]::IsNullOrWhiteSpace($configuredCodexHome)) {
    $codexHomeSource = 'CODEX_HOME'
    $codexHome = [IO.Path]::GetFullPath($configuredCodexHome)
    Add-Issue $issues "CODEX_HOME is configured. The current-PC integration contract expects the default .codex location: $codexHome"
}

$authPath = Join-Path $codexHome 'auth.json'
$browserStatePath = Join-Path $env:APPDATA 'Codex\web\Codex'
if (-not (Test-Path -LiteralPath $codexHome -PathType Container)) {
    Add-Issue $issues "The shared Codex home does not exist: $codexHome"
}
if (-not (Test-Path -LiteralPath $authPath -PathType Leaf)) {
    Add-Issue $issues "The live authentication file does not exist: $authPath"
}
if (-not (Test-Path -LiteralPath $browserStatePath -PathType Container)) {
    Add-Issue $issues "The live browser-state directory does not exist: $browserStatePath"
}

$browserPathObserved = $userDataDirectories.Count -eq 0
foreach ($observedPath in $userDataDirectories) {
    if ([IO.Path]::GetFullPath($observedPath).TrimEnd('\') -eq [IO.Path]::GetFullPath($browserStatePath).TrimEnd('\')) {
        $browserPathObserved = $true
    }
}
if ($mainProcesses.Count -gt 0 -and -not $browserPathObserved) {
    Add-Issue $issues "Running package processes use a different browser-state directory: $($userDataDirectories -join ', ')"
}

$result = [pscustomobject]@{
    SchemaVersion = 1
    CheckedAtUtc = [DateTime]::UtcNow.ToString('o')
    Compatible = ($issues.Count -eq 0)
    Issues = @($issues)
    Package = [pscustomobject]@{
        Name = [string]$package.Name
        Version = [string]$package.Version
        FamilyName = [string]$package.PackageFamilyName
        InstallLocation = [string]$package.InstallLocation
    }
    Application = [pscustomobject]@{
        Id = $applicationId
        AppUserModelId = $appUserModelId
        ManifestExecutable = $manifestExecutable
        ExecutablePath = $executablePath
        EntryPoint = $manifestEntryPoint
    }
    Processes = [pscustomobject]@{
        PackageProcessCount = $packageProcesses.Count
        ChatGptProcessCount = $chatGptProcesses.Count
        MainProcessCount = $mainProcesses.Count
        CodexHelperProcessCount = $helperProcesses.Count
        UserDataDirectories = $userDataDirectories
    }
    State = [pscustomobject]@{
        CodexHomeSource = $codexHomeSource
        SharedCodexHome = $codexHome
        SharedCodexHomeExists = (Test-Path -LiteralPath $codexHome -PathType Container)
        AuthPath = $authPath
        AuthExists = (Test-Path -LiteralPath $authPath -PathType Leaf)
        BrowserStatePath = $browserStatePath
        BrowserStateExists = (Test-Path -LiteralPath $browserStatePath -PathType Container)
        SessionsShared = (Test-Path -LiteralPath (Join-Path $codexHome 'sessions') -PathType Container)
        SkillsShared = (Test-Path -LiteralPath (Join-Path $codexHome 'skills') -PathType Container)
        ConfigurationShared = (Test-Path -LiteralPath (Join-Path $codexHome 'config.toml') -PathType Leaf)
    }
}

if ($AsJson) {
    $result | ConvertTo-Json -Depth 5
} else {
    $result
}

if ($FailOnIncompatible -and -not $result.Compatible) {
    exit 2
}
