[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [switch] $SkipFormat,

    [switch] $SkipSwift
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
$solutionPath = Join-Path $repositoryRoot "Orchard.slnx"
$globalJsonPath = Join-Path $repositoryRoot "global.json"
$toolchainLockPath = Join-Path $repositoryRoot "toolchains/orchard-toolchain.lock.json"
$schemaPath = Join-Path $repositoryRoot "schemas/orchard-ir-v0.1.schema.json"
$sampleManifestPath = Join-Path $repositoryRoot "samples/HelloOrchard/orchard.json"
$testProjectPath = Join-Path $repositoryRoot "tests/Orchard.Tests/Orchard.Tests.csproj"
$protocolTestProjectPath = Join-Path $repositoryRoot "tests/Orchard.Protocol.Tests/Orchard.Protocol.Tests.csproj"
$transportTestProjectPath = Join-Path $repositoryRoot "tests/Orchard.Transport.Windows.Tests/Orchard.Transport.Windows.Tests.csproj"
$nativeHostTestProjectPath = Join-Path $repositoryRoot "tests/Orchard.NativeHost.Windows.Tests/Orchard.NativeHost.Windows.Tests.csproj"
$runtimeTestProjectPath = Join-Path $repositoryRoot "tests/Orchard.Runtime.Windows.Tests/Orchard.Runtime.Windows.Tests.csproj"
$toolingProtocolTestProjectPath = Join-Path $repositoryRoot "tests/Orchard.Tooling.Protocol.Tests/Orchard.Tooling.Protocol.Tests.csproj"
$cliProjectPath = Join-Path $repositoryRoot "src/Orchard.Cli/Orchard.Cli.csproj"
$sampleProjectPath = Join-Path $repositoryRoot "samples/HelloOrchard"
$swiftLauncherPath = Join-Path $repositoryRoot "eng/invoke-swift.ps1"
$swiftPackagePath = Join-Path $repositoryRoot "sdk/swift"
$nativeSamplePath = Join-Path $repositoryRoot "samples/NativeHello"
$validationDirectory = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts/validation"))
$repositoryPrefix = $repositoryRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if (-not $validationDirectory.StartsWith($repositoryPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The validation output directory resolved outside the repository."
}

if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    throw "Could not locate Orchard.slnx at '$solutionPath'."
}

$dotnetCommand = Get-Command dotnet -ErrorAction Stop
$globalJson = Get-Content -Raw -LiteralPath $globalJsonPath | ConvertFrom-Json
$toolchainLock = Get-Content -Raw -LiteralPath $toolchainLockPath | ConvertFrom-Json
$schema = Get-Content -Raw -LiteralPath $schemaPath | ConvertFrom-Json
$sampleManifest = Get-Content -Raw -LiteralPath $sampleManifestPath | ConvertFrom-Json

if ($globalJson.sdk.version -ne $toolchainLock.dotnet.sdkVersion) {
    throw "global.json pins '$($globalJson.sdk.version)' but the toolchain lock pins '$($toolchainLock.dotnet.sdkVersion)'."
}

$activeDotnetVersion = (& $dotnetCommand.Source --version).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Unable to query the active .NET SDK."
}

if ($activeDotnetVersion -ne $toolchainLock.dotnet.sdkVersion) {
    throw "The active .NET SDK is '$activeDotnetVersion'; Orchard requires '$($toolchainLock.dotnet.sdkVersion)'."
}

if ($schema.'$schema' -ne "https://json-schema.org/draft/2020-12/schema") {
    throw "The Orchard IR schema is not declared as JSON Schema draft 2020-12."
}

if ($schema.properties.schemaVersion.const -ne "0.1.0") {
    throw "The checked-in Orchard IR schema does not describe version 0.1.0."
}

if ([string]::IsNullOrWhiteSpace([string] $sampleManifest.applicationId) -or
    [string]::IsNullOrWhiteSpace([string] $sampleManifest.entryPoint)) {
    throw "The sample orchard.json is missing its applicationId or entryPoint."
}

if (-not $SkipSwift -and $toolchainLock.swift.status -ne "selected-for-native-vertical-slice") {
    throw "The native Swift verification gate requires a selected toolchain."
}

Push-Location -LiteralPath $repositoryRoot
try {
    Write-Host "Verifying Project Orchard with .NET SDK $activeDotnetVersion."

    Invoke-CheckedCommand -FilePath $dotnetCommand.Source -ArgumentList @(
        "restore",
        $solutionPath
    )

    if (-not $SkipFormat) {
        Invoke-CheckedCommand -FilePath $dotnetCommand.Source -ArgumentList @(
            "format",
            $solutionPath,
            "--verify-no-changes",
            "--no-restore"
        )
    }

    Invoke-CheckedCommand -FilePath $dotnetCommand.Source -ArgumentList @(
        "build",
        $solutionPath,
        "--configuration",
        $Configuration,
        "--no-restore",
        "--property:TreatWarningsAsErrors=true"
    )

    # Orchard.Tests is intentionally a dependency-free executable test harness.
    Invoke-CheckedCommand -FilePath $dotnetCommand.Source -ArgumentList @(
        "run",
        "--project",
        $testProjectPath,
        "--configuration",
        $Configuration,
        "--no-build",
        "--no-restore"
    )

    Invoke-CheckedCommand -FilePath $dotnetCommand.Source -ArgumentList @(
        "run",
        "--project",
        $protocolTestProjectPath,
        "--configuration",
        $Configuration,
        "--no-build",
        "--no-restore"
    )

    Invoke-CheckedCommand -FilePath $dotnetCommand.Source -ArgumentList @(
        "run",
        "--project",
        $transportTestProjectPath,
        "--configuration",
        $Configuration,
        "--no-build",
        "--no-restore"
    )

    Invoke-CheckedCommand -FilePath $dotnetCommand.Source -ArgumentList @(
        "run",
        "--project",
        $runtimeTestProjectPath,
        "--configuration",
        $Configuration,
        "--no-build",
        "--no-restore"
    )

    Invoke-CheckedCommand -FilePath $dotnetCommand.Source -ArgumentList @(
        "run",
        "--project",
        $toolingProtocolTestProjectPath,
        "--configuration",
        $Configuration,
        "--no-build",
        "--no-restore"
    )

    New-Item -ItemType Directory -Path $validationDirectory -Force | Out-Null
    $sampleIrPath = Join-Path $validationDirectory "hello.orchard.json"
    Invoke-CheckedCommand -FilePath $dotnetCommand.Source -ArgumentList @(
        "run",
        "--project",
        $cliProjectPath,
        "--configuration",
        $Configuration,
        "--no-build",
        "--no-restore",
        "--",
        "build",
        $sampleProjectPath,
        "--output",
        $sampleIrPath
    )

    $sampleIr = Get-Content -Raw -LiteralPath $sampleIrPath | ConvertFrom-Json
    if ($sampleIr.schemaVersion -ne "0.1.0") {
        throw "The sample emitted Orchard IR '$($sampleIr.schemaVersion)' instead of '0.1.0'."
    }

    if ($sampleIr.applicationId -ne $sampleManifest.applicationId) {
        throw "The emitted sample application ID does not match orchard.json."
    }

    if ([string]::IsNullOrWhiteSpace([string] $sampleIr.rootView.id) -or
        [string]::IsNullOrWhiteSpace([string] $sampleIr.rootView.type)) {
        throw "The emitted sample IR has no usable root view."
    }

    if (-not $SkipSwift) {
        Write-Host "Verifying the pinned native Swift vertical slice."

        Invoke-CheckedCommand -FilePath $swiftLauncherPath -ArgumentList @("--version")
        Invoke-CheckedCommand -FilePath $swiftLauncherPath -ArgumentList @(
            "-Tool",
            "swift-format",
            "--version"
        )
        Invoke-CheckedCommand -FilePath $swiftLauncherPath -ArgumentList @(
            "-Tool",
            "sourcekit-lsp",
            "--help"
        )
        Invoke-CheckedCommand -FilePath $swiftLauncherPath -ArgumentList @(
            "-Tool",
            "lldb",
            "--version"
        )
        Invoke-CheckedCommand -FilePath $swiftLauncherPath -ArgumentList @(
            "-Tool",
            "lldb-dap",
            "--version"
        )

        if (-not $SkipFormat) {
            Invoke-CheckedCommand -FilePath $swiftLauncherPath -ArgumentList @(
                "format",
                "lint",
                "--recursive",
                "--strict",
                "sdk/swift/Package.swift",
                "sdk/swift/Sources",
                "sdk/swift/Tests",
                "samples/NativeHello/Package.swift",
                "samples/NativeHello/Sources"
            )
        }

        Invoke-CheckedCommand -FilePath $swiftLauncherPath -ArgumentList @(
            "test",
            "--package-path",
            $swiftPackagePath
        )

        Invoke-CheckedCommand -FilePath $swiftLauncherPath -ArgumentList @(
            "build",
            "--package-path",
            $swiftPackagePath,
            "--configuration",
            "release"
        )

        Invoke-CheckedCommand -FilePath $swiftLauncherPath -ArgumentList @(
            "build",
            "--package-path",
            $nativeSamplePath,
            "--configuration",
            "release"
        )

        Invoke-CheckedCommand -FilePath $dotnetCommand.Source -ArgumentList @(
            "run",
            "--project",
            $nativeHostTestProjectPath,
            "--configuration",
            $Configuration,
            "--no-build",
            "--no-restore"
        )

        $nativeRunArguments = @(
            "run",
            "--package-path",
            $nativeSamplePath,
            "--configuration",
            "release",
            "--skip-build"
        )
        $nativeFirst = (& $swiftLauncherPath @nativeRunArguments | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) {
            throw "The first NativeHello execution failed with exit code $LASTEXITCODE."
        }

        $nativeSecond = (& $swiftLauncherPath @nativeRunArguments | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) {
            throw "The second NativeHello execution failed with exit code $LASTEXITCODE."
        }

        if ($nativeFirst -cne $nativeSecond) {
            throw "NativeHello did not emit byte-identical deterministic IR across two executions."
        }

        $nativeIr = $nativeFirst | ConvertFrom-Json
        $expectedNativeCompatibilityPercent = 90.7
        $nativeCompatibilityDifference = [Math]::Abs(
            [double] $nativeIr.compatibility.localCompatibilityPercent - $expectedNativeCompatibilityPercent)
        $partialNativeUsages = @(
            $nativeIr.compatibility.apiUsages | Where-Object { $_.status -ceq "partial" })
        if ($nativeIr.schemaVersion -ne "0.1.0" -or
            $nativeIr.rootView.type -ne "NavigationStack" -or
            $nativeIr.state.Count -ne 2 -or
            $nativeCompatibilityDifference -gt 0.0001 -or
            $partialNativeUsages.Count -lt 1) {
            throw "NativeHello emitted an unexpected application envelope."
        }

        $nativeNodeCount = 0
        $pendingNodes = [System.Collections.Generic.Stack[object]]::new()
        $pendingNodes.Push($nativeIr.rootView)
        while ($pendingNodes.Count -gt 0) {
            $node = $pendingNodes.Pop()
            $nativeNodeCount++
            foreach ($child in $node.children) {
                $pendingNodes.Push($child)
            }
        }

        if ($nativeNodeCount -ne 8) {
            throw "NativeHello emitted $nativeNodeCount nodes instead of 8."
        }

        $nativeIrPath = Join-Path $validationDirectory "native-hello.orchard.json"
        [System.IO.File]::WriteAllText(
            $nativeIrPath,
            $nativeFirst + [Environment]::NewLine,
            [System.Text.UTF8Encoding]::new($false))

        Invoke-CheckedCommand -FilePath $dotnetCommand.Source -ArgumentList @(
            "run",
            "--project",
            $cliProjectPath,
            "--configuration",
            $Configuration,
            "--no-build",
            "--no-restore",
            "--",
            "inspect",
            $nativeIrPath
        )

        $nativeBytes = [System.Text.Encoding]::UTF8.GetBytes($nativeFirst)
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $nativeHash = ([BitConverter]::ToString($sha256.ComputeHash($nativeBytes))).Replace("-", "")
        }
        finally {
            $sha256.Dispose()
        }
        Write-Host "NativeHello deterministic IR: $nativeNodeCount nodes, SHA-256 $nativeHash."
    }

    Write-Host "Project Orchard verification passed."
}
finally {
    Pop-Location
}
