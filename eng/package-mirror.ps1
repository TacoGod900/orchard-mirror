<#
.SYNOPSIS
    Package Orchard Mirror into a self-contained folder that runs on another Windows PC with no
    .NET, no Python install, and no admin rights to launch.

.DESCRIPTION
    Produces dist\OrchardMirror\ containing:
        Orchard.Mirror.Windows.exe   - self-contained, carries its own .NET runtime
        agent\agent.py               - the CoreDevice control-plane agent
        python\python.exe            - an embedded Python with pymobiledevice3 installed
    ...and zips it to dist\OrchardMirror.zip.

    The .exe launcher (AgentLaunchOptions) looks for python\ and agent\ right next to itself, so
    this layout is what makes the copy-and-run work.

    HONEST WARNINGS -- read these before relying on the output:
      * This script has NOT been run by whoever wrote it into the repo. The .NET publish is the
        solid half; the embedded-Python half is the fragile half, because pymobiledevice3 pulls
        native wheels (sslpsk_pmd3 and friends) that must match the embedded interpreter. If the
        pip step fails, fall back to -SkipPython and install Python + `pip install pymobiledevice3`
        on the target laptop instead; the launcher will then find python on PATH.
      * Two things no bundle can carry, still needed once on the target PC:
          - Apple's USB driver (installing it needs admin one time), and
          - Developer Mode toggled on the iPhone.
      * Build first. If `eng\check-mirror.ps1` is not green, this packages a broken app.
#>
[CmdletBinding()]
param(
    [string] $Configuration = "Release",
    [string] $Runtime = "win-x64",
    [string] $PythonVersion = "3.11.9",
    [switch] $SkipPython
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$project = Join-Path $root "src\Orchard.Mirror.Windows\Orchard.Mirror.Windows.csproj"
$dist = Join-Path $root "dist\OrchardMirror"
$agentSource = Join-Path $root "src\Orchard.Mirror.Agent\agent.py"

Write-Host "== Orchard Mirror packager ==" -ForegroundColor Cyan
Write-Host "Output: $dist"

if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

# 1. Self-contained FOLDER publish: carries the .NET runtime (target needs nothing installed, no
#    admin to run), but as an ordinary folder of files rather than one self-extracting exe.
#
#    Why not single-file: a single-file publish is one .exe that unpacks its native libraries to a
#    temp folder on launch. That self-extraction is the specific behaviour antivirus flags, and on a
#    locked-down account it can be denied outright with "access is denied". A plain folder has no
#    self-extractor, writes nothing to temp, and just runs. The cost is that the app is now a folder
#    of files, not a lone exe -- orchard.mirror.exe still lives at the top of it and is still the one
#    thing you launch.
Write-Host "`n[1/4] Publishing self-contained app folder..." -ForegroundColor Yellow
dotnet publish $project -c $Configuration -r $Runtime --self-contained true -o $dist
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed. Fix the build (eng\check-mirror.ps1) first." }

# 2. The agent script, where the launcher looks for it: <exe>\agent\agent.py
Write-Host "`n[2/4] Staging the CoreDevice agent..." -ForegroundColor Yellow
$agentDir = Join-Path $dist "agent"
New-Item -ItemType Directory -Force -Path $agentDir | Out-Null
Copy-Item $agentSource (Join-Path $agentDir "agent.py")

# 3. Embedded Python with pymobiledevice3, at <exe>\python\python.exe. The fragile part.
if ($SkipPython) {
    Write-Host "`n[3/4] Skipping Python bundle (-SkipPython)." -ForegroundColor Yellow
    Write-Host "      On the target PC: install Python and run 'pip install pymobiledevice3'." -ForegroundColor DarkGray
}
else {
    Write-Host "`n[3/4] Building embedded Python (this is the step most likely to fail)..." -ForegroundColor Yellow
    $pythonDir = Join-Path $dist "python"
    New-Item -ItemType Directory -Force -Path $pythonDir | Out-Null

    $embedZip = Join-Path $env:TEMP "python-$PythonVersion-embed-amd64.zip"
    $embedUrl = "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip"
    Invoke-WebRequest -Uri $embedUrl -OutFile $embedZip
    Expand-Archive -Path $embedZip -DestinationPath $pythonDir -Force

    # Embedded Python ships with site imports disabled, which stops pip working. Uncomment the
    # 'import site' line in the ._pth so pip and installed packages are importable.
    $pthMinor = ($PythonVersion.Split('.')[0..1] -join '')
    $pth = Join-Path $pythonDir "python$pthMinor._pth"
    if (Test-Path $pth) {
        (Get-Content $pth) -replace '^#\s*import site', 'import site' | Set-Content $pth
    }

    $getPip = Join-Path $env:TEMP "get-pip.py"
    Invoke-WebRequest -Uri "https://bootstrap.pypa.io/get-pip.py" -OutFile $getPip
    & (Join-Path $pythonDir "python.exe") $getPip --no-warn-script-location
    if ($LASTEXITCODE -ne 0) { throw "Bootstrapping pip into embedded Python failed. Re-run with -SkipPython." }

    & (Join-Path $pythonDir "python.exe") -m pip install pymobiledevice3 --no-warn-script-location
    if ($LASTEXITCODE -ne 0) { throw "Installing pymobiledevice3 into embedded Python failed. Re-run with -SkipPython." }
}

# 4. Zip it for carrying to the other laptop.
Write-Host "`n[4/4] Zipping..." -ForegroundColor Yellow
$zip = Join-Path $root "dist\OrchardMirror.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path $dist -DestinationPath $zip

Write-Host "`nDone." -ForegroundColor Green
Write-Host "  Folder: $dist"
Write-Host "  Zip:    $zip"
Write-Host "`nOn the other laptop: unzip, open the folder, run orchard.mirror.exe (no admin)." -ForegroundColor Cyan
Write-Host "Still needed there once: Apple USB driver (admin), and Developer Mode on the iPhone." -ForegroundColor DarkGray
