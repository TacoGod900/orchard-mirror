[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [switch] $SkipFormat
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FilePath,

        [Parameter(Mandatory = $true)]
        [string[]] $ArgumentList
    )

    Write-Host "> $FilePath $($ArgumentList -join ' ')"
    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Command '$FilePath' failed with exit code $LASTEXITCODE."
    }
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$solutionPath = Join-Path $repositoryRoot "Orchard.Mirror.slnx"
$globalJsonPath = Join-Path $repositoryRoot "global.json"
$toolchainLockPath = Join-Path $repositoryRoot "toolchains/orchard-toolchain.lock.json"
$mirrorTestProjectPath = Join-Path $repositoryRoot "tests/Orchard.Mirror.Tests/Orchard.Mirror.Tests.csproj"

if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    throw "Could not locate Orchard.Mirror.slnx at '$solutionPath'."
}

# Orchard Mirror is a separate deliverable from the Orchard product: it depends on a Python
# control-plane agent and a media stack that the deliberately dependency-free core must not
# inherit. It therefore has its own solution and this gate, rather than joining eng/check.ps1.
# See docs/decisions/0014-orchard-mirror-coredevice-architecture.md.

$dotnetCommand = Get-Command dotnet -ErrorAction Stop
$globalJson = Get-Content -Raw -LiteralPath $globalJsonPath | ConvertFrom-Json
$toolchainLock = Get-Content -Raw -LiteralPath $toolchainLockPath | ConvertFrom-Json

$pinnedSdkVersion = $globalJson.sdk.version
if ($pinnedSdkVersion -ne $toolchainLock.dotnet.sdkVersion) {
    throw "global.json pins .NET SDK '$pinnedSdkVersion' but the toolchain lock records '$($toolchainLock.dotnet.sdkVersion)'."
}

$installedSdkVersion = (& $dotnetCommand.Source --version).Trim()
if ($installedSdkVersion -ne $pinnedSdkVersion) {
    throw "The installed .NET SDK is '$installedSdkVersion' but '$pinnedSdkVersion' is pinned."
}

Write-Host "Orchard Mirror verification"
Write-Host "  configuration : $Configuration"
Write-Host "  solution      : $solutionPath"
Write-Host "  .NET SDK      : $installedSdkVersion"

Invoke-CheckedCommand -FilePath $dotnetCommand.Source -ArgumentList @(
    "restore", $solutionPath
)

if (-not $SkipFormat) {
    Invoke-CheckedCommand -FilePath $dotnetCommand.Source -ArgumentList @(
        "format", $solutionPath, "--verify-no-changes", "--no-restore"
    )
}

Invoke-CheckedCommand -FilePath $dotnetCommand.Source -ArgumentList @(
    "build", $solutionPath,
    "--configuration", $Configuration,
    "--no-restore",
    "--property:TreatWarningsAsErrors=true"
)

# The suite self-skips green when the hardware it needs is absent (no iPhone attached, no GPU,
# no recorded RTP capture), so this gate runs unattended. Device-dependent assertions must skip
# loudly rather than silently pass.
Invoke-CheckedCommand -FilePath $dotnetCommand.Source -ArgumentList @(
    "run", "--project", $mirrorTestProjectPath,
    "--configuration", $Configuration,
    "--no-build", "--no-restore"
)

Write-Host "Orchard Mirror verification passed."
