[CmdletBinding(PositionalBinding = $false)]
param(
    [ValidateSet("swift", "swift-format", "sourcekit-lsp", "lldb", "lldb-dap")]
    [string] $Tool = "swift",

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $SwiftArguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$originalProcessEnvironment = @(
    [Environment]::GetEnvironmentVariables().GetEnumerator() |
        ForEach-Object { [PSCustomObject]@{ Name = [string] $_.Key; Value = [string] $_.Value } }
)

try {
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$toolchainLockPath = Join-Path $repositoryRoot "toolchains/orchard-toolchain.lock.json"
$toolchainLock = Get-Content -Raw -LiteralPath $toolchainLockPath | ConvertFrom-Json
$swiftLock = $toolchainLock.swift
$visualStudioLock = $toolchainLock.visualStudio

if ($swiftLock.status -ne "selected-for-native-vertical-slice") {
    throw "The Swift toolchain lock has not passed the native vertical-slice selection gate."
}

$swiftInstallRoot = Join-Path $env:LOCALAPPDATA "Programs/Swift"
$toolchainBin = Join-Path $swiftInstallRoot "Toolchains/$($swiftLock.toolchainDirectory)/usr/bin"
$runtimeBin = Join-Path $swiftInstallRoot "Runtimes/$($swiftLock.runtimeDirectory)/usr/bin"
$swiftPlatformSdk = Join-Path $swiftInstallRoot "Platforms/$($swiftLock.platformSdkDirectory)"
$pythonBin = Join-Path $swiftInstallRoot "$($swiftLock.bundledPythonDirectory)/usr/bin"
$toolFileName = "$Tool.exe"
$toolExecutable = Join-Path $toolchainBin $toolFileName

foreach ($requiredPath in @($toolchainBin, $runtimeBin, $swiftPlatformSdk, $pythonBin, $toolExecutable)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "The pinned Swift 6.3.3 installation is incomplete; '$requiredPath' is missing."
    }
}

$expectedToolHash = if ($Tool -eq "swift") {
    $swiftLock.swiftBinarySha256
}
else {
    $swiftLock.toolBinarySha256.$toolFileName
}
$actualToolHash = (Get-FileHash -LiteralPath $toolExecutable -Algorithm SHA256).Hash
if ($actualToolHash -ne $expectedToolHash) {
    throw "The installed $toolFileName hash does not match toolchains/orchard-toolchain.lock.json."
}

# Some hosts inject both Path and PATH into the process environment. Swift's
# process-environment decoder correctly rejects that ambiguous input. Replace
# both variants with one deterministic PATH before discovering the C++ tools.
$machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
Remove-Item Env:Path -ErrorAction SilentlyContinue
Remove-Item Env:PATH -ErrorAction SilentlyContinue
$env:PATH = @($machinePath, $userPath) -join [System.IO.Path]::PathSeparator

$programFilesX86 = [Environment]::GetFolderPath("ProgramFilesX86")
$vswherePath = Join-Path $programFilesX86 "Microsoft Visual Studio/Installer/vswhere.exe"
if (-not (Test-Path -LiteralPath $vswherePath -PathType Leaf)) {
    throw "vswhere.exe is required to locate the pinned Visual Studio C++ toolchain."
}

$vswhereArguments = @(
    "-latest",
    "-version",
    $visualStudioLock.versionRange,
    "-products",
    "*"
)
foreach ($component in $visualStudioLock.requiredComponents) {
    $vswhereArguments += @("-requires", $component)
}
$vswhereArguments += @("-property", "installationPath")

$vswhereOutput = @(& $vswherePath @vswhereArguments)
$vswhereExitCode = $LASTEXITCODE
$visualStudioPath = ([string] ($vswhereOutput |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1)).Trim()
if ($vswhereExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($visualStudioPath)) {
    throw "No Visual Studio installation satisfies the pinned native Swift prerequisites."
}

$vsDevCmdPath = Join-Path $visualStudioPath "Common7/Tools/VsDevCmd.bat"
if (-not (Test-Path -LiteralPath $vsDevCmdPath -PathType Leaf)) {
    throw "The selected Visual Studio installation has no VsDevCmd.bat."
}

$developerEnvironmentCommand =
    '"' + $vsDevCmdPath + '" -no_logo -arch=amd64 -host_arch=amd64 >nul && set'
$developerEnvironment = & $env:ComSpec /d /s /c $developerEnvironmentCommand
if ($LASTEXITCODE -ne 0) {
    throw "Visual Studio failed to initialize the x64 native build environment."
}

foreach ($line in $developerEnvironment) {
    if ($line -match "^([^=]+)=(.*)$") {
        [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], "Process")
    }
}

if ($env:WindowsSDKVersion.TrimEnd('\') -ne $visualStudioLock.windowsSdkVersion) {
    throw "Visual Studio selected Windows SDK '$($env:WindowsSDKVersion.TrimEnd('\'))'; Orchard pins '$($visualStudioLock.windowsSdkVersion)'."
}

$env:SDKROOT = $swiftPlatformSdk
$developerPath = $env:Path
Remove-Item Env:Path -ErrorAction SilentlyContinue
Remove-Item Env:PATH -ErrorAction SilentlyContinue
$env:PATH = @(
    $toolchainBin
    $runtimeBin
    $pythonBin
    $developerPath
) -join [System.IO.Path]::PathSeparator

# SwiftPM's build-tools compiler needs the Swift Windows platform SDK
# explicitly. This is not WindowsSdkDir (the C/Windows Kits SDK).
if ($Tool -eq "swift" -and
    $SwiftArguments.Count -gt 0 -and
    $SwiftArguments[0] -in @("build", "run", "test") -and
    $SwiftArguments -notcontains $swiftPlatformSdk) {
    $SwiftArguments += @(
        "-Xbuild-tools-swiftc",
        "-sdk",
        "-Xbuild-tools-swiftc",
        $swiftPlatformSdk
    )
}

& $toolExecutable @SwiftArguments
if ($LASTEXITCODE -ne 0) {
    throw "The pinned $Tool tool exited with code $LASTEXITCODE."
}
}
finally {
    foreach ($entry in [Environment]::GetEnvironmentVariables().GetEnumerator()) {
        [Environment]::SetEnvironmentVariable([string] $entry.Key, $null, "Process")
    }

    foreach ($entry in $originalProcessEnvironment) {
        [Environment]::SetEnvironmentVariable($entry.Name, $entry.Value, "Process")
    }
}
