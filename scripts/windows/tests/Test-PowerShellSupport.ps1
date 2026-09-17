#requires -Version 5.1

[CmdletBinding()]
param(
    [string]$HelperPath
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '../Sidey.PowerShell.psm1') -Force

$shell = (Get-Command powershell.exe -ErrorAction Stop).Source
Invoke-SideyNativeCommand `
    -FilePath $shell `
    -ArgumentList @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', 'exit 0') `
    -Description 'Successful native command test'

$failure = $null
try {
    Invoke-SideyNativeCommand `
        -FilePath $shell `
        -ArgumentList @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', 'exit 23') `
        -Description 'Expected native command test failure'
}
catch {
    $failure = $_
}

if ($null -eq $failure) {
    throw 'Invoke-SideyNativeCommand accepted a non-zero exit code.'
}
if ($failure.Exception.Message -cne 'Expected native command test failure failed with exit code 23.') {
    throw "Unexpected native command failure message: $($failure.Exception.Message)"
}

$repositoryRootPath = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$releaseManifestPath = Join-Path $repositoryRootPath 'release/windows.json'
$version = [string]((Get-Content -LiteralPath $releaseManifestPath -Raw -Encoding UTF8 |
    ConvertFrom-Json).version)
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Windows release version must contain three numeric parts: $version"
}
$fileVersion = "$version.0"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'SIDEY PowerShell process tests ' + [Guid]::NewGuid().ToString('N'))
$resultPath = Join-Path $testRoot 'process result with spaces.ini'
$logPath = Join-Path $testRoot 'process log with spaces.log'
try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    if ([string]::IsNullOrWhiteSpace($HelperPath)) {
        $HelperPath = Join-Path $testRoot 'Sidey.InstallerErrorHelper.exe'
        & (Join-Path $repositoryRootPath 'scripts/windows/New-SideyHelperExecutable.ps1') `
            -SourcePath (Join-Path $repositoryRootPath 'windows/installer/Sidey.Setup/InstallerErrorNormalizer.cs') `
            -OutputPath $HelperPath `
            -Version $version -FileVersion $fileVersion `
            -Title 'SIDEY Installer Error Helper' `
            -Description 'SIDEY installer error process probe' `
            -IconPath (Join-Path $repositoryRootPath 'windows/src/Sidey.App/Assets/Icons/SideyAppIcon.ico')
    }
    $HelperPath = (Resolve-Path -LiteralPath $HelperPath).Path
    $commandDescription = 'quote"inside C:\trailing\'
    $exitCode = Invoke-SideyWindowsProcess `
        -FilePath $HelperPath `
        -ArgumentList @(
            '--normalize-error', '--native-code', '0', '--source', 'TEST', '--stage', 'CHECK',
            '--target', 'target with spaces', '--command-description', $commandDescription,
            '--result-path', $resultPath, '--log-path', $logPath,
            '--installer-version', $version)
    if ($exitCode -ne 0) {
        throw "WinExe process success code was not preserved: $exitCode"
    }
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        throw 'WinExe process invocation returned before the completion marker was written.'
    }
    $result = [IO.File]::ReadAllText($resultPath, [Text.Encoding]::Unicode)
    if (-not $result.Contains("target=target with spaces") -or
        -not $result.Contains("command=$commandDescription")) {
        throw 'WinExe process arguments did not round-trip through the Windows command line.'
    }
    $exitCode = Invoke-SideyWindowsProcess `
        -FilePath $HelperPath `
        -ArgumentList @('--sidey-invalid-verification-argument')
    if ($exitCode -ne 64) {
        throw "WinExe process failure code was not preserved: $exitCode"
    }
    $exitCode = Invoke-SideyWindowsProcess -FilePath $HelperPath
    if ($exitCode -ne 64) {
        throw "WinExe process with no arguments returned an unexpected code: $exitCode"
    }

    $ordinaryLogDirectory = Join-Path $testRoot 'ordinary/SIDEY/Installer/Logs'
    [IO.Directory]::CreateDirectory($ordinaryLogDirectory) | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $ordinaryLogDirectory 'SIDEY-Setup-20260917-150000.log'),
        'setup failure')
    [IO.File]::WriteAllText(
        (Join-Path $ordinaryLogDirectory 'SIDEY-Uninstall-20260917-151000.log'),
        'uninstall failure')
    $cleanupType = [Reflection.Assembly]::Load(
        [IO.File]::ReadAllBytes($HelperPath)).GetType(
        'Sidey.Setup.Errors.InstallerLogCleanup',
        $true)
    $cleanupMethod = $cleanupType.GetMethod(
        'DeleteIfSafe',
        [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::NonPublic)
    if ($null -eq $cleanupMethod) {
        throw 'Installer diagnostic cleanup test seam was not found.'
    }
    $cleanupMethod.Invoke(
        $null,
        [object[]][string[]]@($ordinaryLogDirectory, $ordinaryLogDirectory)) | Out-Null
    if (Test-Path -LiteralPath $ordinaryLogDirectory) {
        throw 'Installer diagnostic cleanup did not remove an ordinary log directory.'
    }

    $unsafeLogDirectory = Join-Path $testRoot 'unsafe/SIDEY/Installer/Logs'
    $externalDirectory = Join-Path $testRoot 'external sentinel'
    $externalSentinel = Join-Path $externalDirectory 'must-survive.txt'
    [IO.Directory]::CreateDirectory($unsafeLogDirectory) | Out-Null
    [IO.Directory]::CreateDirectory($externalDirectory) | Out-Null
    [IO.File]::WriteAllText($externalSentinel, 'preserve')
    New-Item -ItemType Junction `
        -Path (Join-Path $unsafeLogDirectory 'SIDEY-Setup-20260917-152000.log') `
        -Target $externalDirectory | Out-Null
    $junctionFailure = $null
    try {
        $cleanupMethod.Invoke(
            $null,
            [object[]][string[]]@($unsafeLogDirectory, $unsafeLogDirectory)) | Out-Null
    }
    catch {
        $junctionFailure = $_
    }
    if ($null -eq $junctionFailure) {
        throw 'Installer diagnostic cleanup accepted a directory junction.'
    }
    if (-not (Test-Path -LiteralPath $unsafeLogDirectory -PathType Container) -or
        -not (Test-Path -LiteralPath $externalSentinel -PathType Leaf) -or
        [IO.File]::ReadAllText($externalSentinel) -cne 'preserve') {
        throw 'Installer diagnostic cleanup crossed a directory junction.'
    }

    $rootJunctionParent = Join-Path $testRoot 'root junction/SIDEY/Installer'
    $rootJunctionLogDirectory = Join-Path $rootJunctionParent 'Logs'
    $rootJunctionTarget = Join-Path $testRoot 'root junction sentinel'
    $rootJunctionSentinel = Join-Path $rootJunctionTarget 'must-survive.txt'
    [IO.Directory]::CreateDirectory($rootJunctionParent) | Out-Null
    [IO.Directory]::CreateDirectory($rootJunctionTarget) | Out-Null
    [IO.File]::WriteAllText($rootJunctionSentinel, 'preserve')
    New-Item -ItemType Junction `
        -Path $rootJunctionLogDirectory `
        -Target $rootJunctionTarget | Out-Null
    $rootJunctionFailure = $null
    try {
        $cleanupMethod.Invoke(
            $null,
            [object[]][string[]]@($rootJunctionLogDirectory, $rootJunctionLogDirectory)) | Out-Null
    }
    catch {
        $rootJunctionFailure = $_
    }
    if ($null -eq $rootJunctionFailure) {
        throw 'Installer diagnostic cleanup accepted a Logs directory junction.'
    }
    if (-not (Test-Path -LiteralPath $rootJunctionLogDirectory -PathType Container) -or
        -not (Test-Path -LiteralPath $rootJunctionSentinel -PathType Leaf) -or
        [IO.File]::ReadAllText($rootJunctionSentinel) -cne 'preserve') {
        throw 'Installer diagnostic cleanup crossed the Logs directory junction.'
    }
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if ([IO.Directory]::GetParent($resolvedTestRoot).FullName -cne $expectedParent -or
        [IO.Path]::GetFileName($resolvedTestRoot) -notlike 'SIDEY PowerShell process tests *') {
        throw 'Unsafe PowerShell process test cleanup path.'
    }
    if (Test-Path -LiteralPath $resolvedTestRoot) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}

Write-Host 'PowerShell native command helper tests passed.'
