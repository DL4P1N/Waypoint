# Builds Waypoint.exe, installs it for this user, starts it, and runs it at sign-in.
# Run it again to install an update. Your data and settings aren't touched.
#
# Each build is kept whole in versions\<time>-<commit>, checked against what was built, and only then swapped in as
# the one Waypoint.exe that the Start Menu shortcut and the sign-in entry start. If the new version doesn't stay
# running, the previous one is put back by itself. The last two versions are kept.
#
#   .\install.ps1              build, install, and start
#   .\install.ps1 -Rollback    go back to the previous version
#
# -InstallRoot installs somewhere else, for trying the installer itself. It leaves the shortcut and sign-in entry
# alone and starts that copy as a trial run on its own data folder (-DataDir, or data inside the install root), so it
# never opens your real data or meets the installed copy.

param(
    [string]$InstallRoot,
    [string]$DataDir,
    [switch]$Rollback,
    # Installs this already-built folder instead of building. Used to try the installer quickly.
    [string]$PublishedFolder,
    # How long a new version has to stay running before it counts as started.
    [int]$StartCheckSeconds = 8
)

$ErrorActionPreference = 'Stop'

$realInstall = -not $InstallRoot
if ($realInstall) {
    $InstallRoot = Join-Path $env:LOCALAPPDATA 'Programs\Waypoint'
} else {
    $InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
    if (-not $DataDir) { $DataDir = Join-Path $InstallRoot 'data' }
    $DataDir = [IO.Path]::GetFullPath($DataDir)
    New-Item -ItemType Directory -Force $DataDir | Out-Null
    # Inherited by the copy this starts: its own data folder, its own single-instance name, and no alerts or sending.
    $env:WAYPOINT_DATA_DIR = $DataDir
    $env:WAYPOINT_TRIAL = '1'
}

$exe = Join-Path $InstallRoot 'Waypoint.exe'
$previousExe = Join-Path $InstallRoot 'Waypoint.previous.exe'
$versionsDir = Join-Path $InstallRoot 'versions'
$currentFile = Join-Path $InstallRoot 'current.txt'

function Get-RelativeFiles([string]$folder) {
    $full = (Resolve-Path -LiteralPath $folder).ProviderPath.TrimEnd('\') + '\'
    Get-ChildItem -LiteralPath $folder -Recurse -File | ForEach-Object { $_.FullName.Substring($full.Length) }
}

function Get-Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }

# Checks every file in $from has the same bytes in $to.
function Assert-SameFiles([string]$from, [string]$to) {
    foreach ($relative in Get-RelativeFiles $from) {
        $copy = Join-Path $to $relative
        if (-not (Test-Path -LiteralPath $copy) -or (Get-Hash (Join-Path $from $relative)) -ne (Get-Hash $copy)) {
            throw "The copy of $relative in $to doesn't match what was built. Nothing was switched."
        }
    }
}

# The name the app's single-instance lock uses, so a running copy can be asked to close the usual way.
function Get-InstanceName {
    if (-not $env:WAYPOINT_DATA_DIR) { return 'Local\Waypoint' }
    $key = [IO.Path]::GetFullPath($env:WAYPOINT_DATA_DIR).ToUpperInvariant()
    $hash = [Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($key))
    'Local\Waypoint-' + ([BitConverter]::ToString($hash) -replace '-', '').Substring(0, 16)
}

function Get-InstalledProcesses {
    $prefix = $InstallRoot.TrimEnd('\') + '\'
    @(Get-CimInstance Win32_Process -Filter "Name = 'Waypoint.exe'" |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
}

# Asks a running copy from this install to close, so it saves drafts and finishes a recording, and stops it if it
# hasn't closed within 20 seconds. A version from before the close request is simply stopped.
function Stop-Installed {
    if ((Get-InstalledProcesses).Count -eq 0) { return }
    $request = $null
    if ([Threading.EventWaitHandle]::TryOpenExisting((Get-InstanceName) + '.Exit', [ref]$request)) {
        Write-Host 'Asking Waypoint to close...'
        [void]$request.Set()
        $request.Dispose()
        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-InstalledProcesses).Count -gt 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
        if ((Get-InstalledProcesses).Count -eq 0) { Write-Host 'Waypoint closed.' }
    }
    for ($attempt = 1; (Get-InstalledProcesses).Count -gt 0 -and $attempt -le 20; $attempt++) {
        if ($attempt -eq 1) { Write-Host 'Stopping Waypoint...' }
        Get-InstalledProcesses | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Milliseconds 500
    }
    if ((Get-InstalledProcesses).Count -gt 0) {
        throw "Waypoint is still running from $InstallRoot and couldn't be stopped. Nothing was switched."
    }
}

function Get-Current {
    if (Test-Path -LiteralPath $currentFile) { (Get-Content -LiteralPath $currentFile -Raw).Trim() } else { $null }
}

function Set-Current([string]$name) {
    $temporary = "$currentFile.new"
    [IO.File]::WriteAllText($temporary, $name)
    Move-Item -LiteralPath $temporary -Destination $currentFile -Force
}

# Version folders, newest first. A half-made one (still .partial) isn't a version.
function Get-Versions {
    if (-not (Test-Path -LiteralPath $versionsDir)) { return @() }
    @(Get-ChildItem -LiteralPath $versionsDir -Directory | Where-Object { $_.Name -notlike '*.partial' } |
        Sort-Object Name -Descending | ForEach-Object { $_.Name })
}

# Makes the files at the install root the given version's. Each file is copied beside its old one as .new and checked
# first, then switched in by rename, with the old Waypoint.exe kept as Waypoint.previous.exe. Files the version
# doesn't have are removed afterwards.
function Switch-To([string]$name) {
    $source = Join-Path $versionsDir $name
    $files = @(Get-RelativeFiles $source)
    foreach ($relative in $files) {
        $target = Join-Path $InstallRoot $relative
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Copy-Item -LiteralPath (Join-Path $source $relative) -Destination "$target.new" -Force
        if ((Get-Hash (Join-Path $source $relative)) -ne (Get-Hash "$target.new")) {
            throw "The copy of $relative doesn't match version $name. Nothing was switched."
        }
    }

    Stop-Installed

    foreach ($relative in $files) {
        $target = Join-Path $InstallRoot $relative
        if ($relative -eq 'Waypoint.exe' -and (Test-Path -LiteralPath $target)) {
            Remove-Item -LiteralPath $previousExe -Force -ErrorAction SilentlyContinue
            [IO.File]::Replace("$target.new", $target, $previousExe)
        } else {
            Move-Item -LiteralPath "$target.new" -Destination $target -Force
        }
    }
    Set-Current $name

    $rootLength = $InstallRoot.TrimEnd('\').Length + 1
    $keep = @($files) + @('Waypoint.previous.exe', 'current.txt')
    Get-ChildItem -LiteralPath $InstallRoot -Recurse -File |
        Where-Object { -not $_.FullName.StartsWith($versionsDir + '\', [StringComparison]::OrdinalIgnoreCase) } |
        Where-Object { $realInstall -or -not $_.FullName.StartsWith($DataDir + '\', [StringComparison]::OrdinalIgnoreCase) } |
        Where-Object { $keep -notcontains $_.FullName.Substring($rootLength) } |
        ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Force
            Write-Host "Removed $($_.FullName.Substring($rootLength)), which version $name doesn't have"
        }
}

# Starts Waypoint.exe and checks it's still running after a while.
function Test-Start {
    $process = Start-Process -FilePath $exe -WorkingDirectory $InstallRoot -PassThru
    Start-Sleep -Seconds $StartCheckSeconds
    $process.Refresh()
    -not $process.HasExited
}

function Remove-OldVersions {
    $current = Get-Current
    Get-Versions | Where-Object { $_ -ne $current } | Select-Object -Skip 1 | ForEach-Object {
        Remove-Item -LiteralPath (Join-Path $versionsDir $_) -Recurse -Force
        Write-Host "Removed old version $_"
    }
}

function Set-Launchers {
    if (-not $realInstall) {
        Write-Host 'Left the Start Menu shortcut and sign-in entry alone, since this install has its own -InstallRoot.'
        return
    }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'Waypoint.lnk'))
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $InstallRoot
    $shortcut.Save()

    Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Waypoint' -Value "`"$exe`""
}

# A Waypoint.exe installed before versions were kept becomes a version itself, so it can be gone back to.
function Save-UnversionedInstall {
    if ((Get-Current) -or -not (Test-Path -LiteralPath $exe)) { return }
    $name = (Get-Item -LiteralPath $exe).LastWriteTimeUtc.ToString('yyyyMMdd-HHmmss') + '-before'
    $partial = Join-Path $versionsDir "$name.partial"
    Remove-Item -LiteralPath $partial -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $partial | Out-Null
    Get-ChildItem -LiteralPath $InstallRoot -File |
        Where-Object { $_.Name -ne 'Waypoint.previous.exe' -and $_.Name -notlike '*.new' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $partial }
    Assert-SameFiles $partial $InstallRoot
    Rename-Item -LiteralPath $partial -NewName $name
    Set-Current $name
    Write-Host "Kept the Waypoint.exe already installed as version $name"
}

if ($Rollback) {
    $current = Get-Current
    $previous = Get-Versions | Where-Object { $_ -ne $current } | Select-Object -First 1
    if (-not $previous) { throw "There's no previous version in $versionsDir to go back to. Nothing was changed." }
    Switch-To $previous
    Set-Launchers
    if (Test-Start) {
        Write-Host "Went back from version $current to $previous, and it's running."
        exit 0
    }
    throw "Went back from version $current to $previous, but it didn't stay running. Run -Rollback again to return to $current."
}

if ($realInstall) {
    # The Install profile builds one small exe that uses the installed .NET 10 Desktop runtime, so that runtime has
    # to be here first. Checked before anything is built or stopped, so a missing runtime never leaves Waypoint
    # stopped.
    $desktopRuntime = & dotnet --list-runtimes 2>$null | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App 10\.' }
    if (-not $desktopRuntime) {
        throw @'
The .NET 10 Desktop Runtime isn't installed, and Waypoint needs it to run. Nothing was changed.
Install ".NET Desktop Runtime 10" for x64 from https://dotnet.microsoft.com/download/dotnet/10.0
(or run: winget install Microsoft.DotNet.DesktopRuntime.10), then run this again.
'@
    }
}

# Build into a staging folder first, so the running app is only stopped for the moment it takes to switch.
$staging = $PublishedFolder
$ownStaging = -not $staging
if ($ownStaging) {
    $staging = Join-Path ([IO.Path]::GetTempPath()) "waypoint-publish-$([Guid]::NewGuid().ToString('N'))"
    dotnet publish (Join-Path $PSScriptRoot 'src\Waypoint.App') `
        -p:PublishProfile=Install `
        --configuration Release `
        --output $staging `
        --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed. Nothing was changed.' }
}
$built = Join-Path $staging 'Waypoint.exe'
if (-not (Test-Path -LiteralPath $built) -or (Get-Item -LiteralPath $built).Length -eq 0) {
    throw "The build has no Waypoint.exe in $staging. Nothing was changed."
}

# The new version goes in a folder of its own, named for when it was built and from which commit, and is checked
# against the build before anything else happens.
$name = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
try {
    $commit = & git -C $PSScriptRoot rev-parse --short HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $commit) { $name += "-$commit" }
} catch {
    # Without git the time alone names it.
}
New-Item -ItemType Directory -Force $versionsDir | Out-Null
Save-UnversionedInstall
$partial = Join-Path $versionsDir "$name.partial"
Remove-Item -LiteralPath $partial -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $partial | Out-Null
Copy-Item -Path (Join-Path $staging '*') -Destination $partial -Recurse -Force
Assert-SameFiles $staging $partial
Rename-Item -LiteralPath $partial -NewName $name
if ($ownStaging) { Remove-Item -Recurse -Force $staging }

$previous = Get-Current
Switch-To $name
Set-Launchers

if (Test-Start) {
    Remove-OldVersions
    Write-Host "Installed and started version $name from $exe"
    exit 0
}

# It didn't stay running, so the version before it goes back and the one that failed is removed.
Stop-Installed
if (-not $previous) {
    throw "Version $name didn't stay running after $StartCheckSeconds seconds, and there's no earlier version to go back to. Look in errors.log in the data folder."
}
Write-Host "Version $name didn't stay running after $StartCheckSeconds seconds. Going back to version $previous..."
Switch-To $previous
# Waypoint.previous.exe is now the version that failed, and nothing should go back to it.
Remove-Item -LiteralPath $previousExe -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $versionsDir $name) -Recurse -Force
if (Test-Start) {
    throw "Version $name didn't stay running, so it was removed and version $previous was put back. It's running."
}
throw "Version $name didn't stay running, so it was removed and version $previous was put back, but that didn't stay running either."
