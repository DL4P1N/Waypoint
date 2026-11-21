# Builds Waypoint.exe, installs it for this user, starts it, and runs it at sign-in.
# Run it again to install an update. Your data and settings aren't touched.
#
# Each build is kept whole in versions\<time>-<commit>, checked against what was built, and only then swapped in as
# the one Waypoint.exe that the Start Menu shortcut and the sign-in entry start. If the new version doesn't stay
# running, the previous one is put back by itself. The current version and the one running before it are kept.
#
#   .\install.ps1              test, build, install, and start
#   .\install.ps1 -SkipTests   build, install, and start without running the tests first
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
    # Builds without running tests\Waypoint.Core.Tests and tests\Waypoint.Server.Tests first. They're skipped anyway
    # with -PublishedFolder, since nothing is built.
    [switch]$SkipTests,
    # How long a new version has to stay running before it counts as started.
    [int]$StartCheckSeconds = 8,
    # How long a running copy that has started closing may take to save before the install gives up and leaves it be.
    [int]$CloseLimitSeconds = 120
)

$ErrorActionPreference = 'Stop'

$realInstall = -not $InstallRoot
if ($realInstall) {
    $InstallRoot = Join-Path $env:LOCALAPPDATA 'Programs\Waypoint'
} else {
    $InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
    # Installing removes files the version doesn't have, so a folder with other things in it is never used.
    if ((Test-Path -LiteralPath $InstallRoot) -and (Get-ChildItem -LiteralPath $InstallRoot -Force | Select-Object -First 1) -and
        -not ((Test-Path -LiteralPath (Join-Path $InstallRoot 'Waypoint.exe')) -or (Test-Path -LiteralPath (Join-Path $InstallRoot 'current.txt')))) {
        throw "$InstallRoot already has other things in it and isn't a Waypoint install, so nothing was changed. Give -InstallRoot an empty or new folder."
    }
    if (-not $DataDir) { $DataDir = Join-Path $InstallRoot 'data' }
    $DataDir = [IO.Path]::GetFullPath($DataDir)
    New-Item -ItemType Directory -Force $DataDir | Out-Null
}
# Given only to the copy this starts (see Test-Start), never left in this session: a trial gets its own data folder,
# its own single-instance name, and no alerts or sending, and a real install gets neither, even after a trial.
$childEnvironment = if ($realInstall) { @{ WAYPOINT_DATA_DIR = $null; WAYPOINT_TRIAL = $null } } else { @{ WAYPOINT_DATA_DIR = $DataDir; WAYPOINT_TRIAL = '1' } }

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
    if (-not $childEnvironment.WAYPOINT_DATA_DIR) { return 'Local\Waypoint' }
    $key = [IO.Path]::GetFullPath($childEnvironment.WAYPOINT_DATA_DIR).ToUpperInvariant()
    $hash = [Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($key))
    'Local\Waypoint-' + ([BitConverter]::ToString($hash) -replace '-', '').Substring(0, 16)
}

function Get-InstalledProcesses {
    $prefix = $InstallRoot.TrimEnd('\') + '\'
    @(Get-CimInstance Win32_Process -Filter "Name = 'Waypoint.exe'" |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
}

# Asks a running copy from this install to close, so it saves drafts, finishes a recording, and syncs notes a last
# time. A copy that says it has started closing is given up to -CloseLimitSeconds, and if it's still closing then,
# the install stops with nothing switched and leaves it to finish, so nothing it's saving is lost. Only a copy that
# never answers is stopped: one with no closing signal after 20 seconds, and one from before the close request at once.
function Stop-Installed {
    if (@(Get-InstalledProcesses).Count -eq 0) { return }
    $instance = Get-InstanceName
    $request = $null
    if ([Threading.EventWaitHandle]::TryOpenExisting($instance + '.Exit', [ref]$request)) {
        # Opened before asking, so the signal is still there to see even if the copy has gone by the time it's checked.
        $closing = $null
        $answers = [Threading.EventWaitHandle]::TryOpenExisting($instance + '.Closing', [ref]$closing)
        Write-Host 'Asking Waypoint to close...'
        [void]$request.Set()
        $request.Dispose()
        try {
            $deadline = (Get-Date).AddSeconds(20)
            $answered = $false
            while (@(Get-InstalledProcesses).Count -gt 0 -and (Get-Date) -lt $deadline) {
                if ($answers -and $closing.WaitOne(250)) { $answered = $true; break }
                if (-not $answers) { Start-Sleep -Milliseconds 250 }
            }
            if ($answered) {
                Write-Host 'Waypoint is closing. Waiting while it saves...'
                $deadline = (Get-Date).AddSeconds($CloseLimitSeconds)
                while (@(Get-InstalledProcesses).Count -gt 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
                if (@(Get-InstalledProcesses).Count -gt 0) {
                    $script:closeRefused = $true
                    throw "Waypoint is still closing after $CloseLimitSeconds seconds, so nothing was switched and it was left to finish saving. Run this again once it has closed."
                }
            }
        } finally {
            if ($closing) { $closing.Dispose() }
        }
        if (@(Get-InstalledProcesses).Count -eq 0) { Write-Host 'Waypoint closed.' }
    }
    for ($attempt = 1; @(Get-InstalledProcesses).Count -gt 0 -and $attempt -le 20; $attempt++) {
        if ($attempt -eq 1) { Write-Host 'Stopping Waypoint...' }
        Get-InstalledProcesses | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Milliseconds 500
    }
    if (@(Get-InstalledProcesses).Count -gt 0) {
        $script:closeRefused = $true
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

    try {
        Stop-Installed
    } catch {
        # Nothing was switched, so the copies made beside the running files go and it's left as it was.
        foreach ($relative in $files) { Remove-Item -LiteralPath ((Join-Path $InstallRoot $relative) + '.new') -Force -ErrorAction SilentlyContinue }
        throw
    }

    # From here Waypoint is closed, so every old file is moved aside as .old rather than replaced or removed. If any
    # step fails, those moves are undone, current.txt goes back, and the version that was running is started again.
    $oldCurrent = Get-Current
    $movedAside = New-Object System.Collections.Generic.List[string]
    $added = New-Object System.Collections.Generic.List[string]
    $rootLength = $InstallRoot.TrimEnd('\').Length + 1
    try {
        foreach ($relative in $files) {
            $target = Join-Path $InstallRoot $relative
            if (Test-Path -LiteralPath $target) {
                Move-Item -LiteralPath $target -Destination "$target.old" -Force
                $movedAside.Add($relative)
            } else {
                $added.Add($relative)
            }
            Move-Item -LiteralPath "$target.new" -Destination $target
        }

        $keep = @($files) + @('Waypoint.previous.exe', 'current.txt') + @($movedAside | ForEach-Object { "$_.old" })
        $stray = @(Get-ChildItem -LiteralPath $InstallRoot -Recurse -File |
            Where-Object { -not $_.FullName.StartsWith($versionsDir + '\', [StringComparison]::OrdinalIgnoreCase) } |
            Where-Object { $realInstall -or -not $_.FullName.StartsWith($DataDir + '\', [StringComparison]::OrdinalIgnoreCase) } |
            Where-Object { $keep -notcontains $_.FullName.Substring($rootLength) } |
            ForEach-Object { $_.FullName.Substring($rootLength) })
        foreach ($relative in $stray) {
            $target = Join-Path $InstallRoot $relative
            Move-Item -LiteralPath $target -Destination "$target.old" -Force
            $movedAside.Add($relative)
        }
        Set-Current $name
    } catch {
        $failure = $_
        $script:switchUndone = $true
        Write-Host "Switching to version $name failed after Waypoint was closed: $failure"
        Write-Host 'Putting back the files that were there...'
        foreach ($relative in $added) { Remove-Item -LiteralPath (Join-Path $InstallRoot $relative) -Force -ErrorAction SilentlyContinue }
        foreach ($relative in $movedAside) {
            $target = Join-Path $InstallRoot $relative
            if (Test-Path -LiteralPath "$target.old") {
                if (Test-Path -LiteralPath $target) { Move-Item -LiteralPath $target -Destination "$target.new" -Force -ErrorAction SilentlyContinue }
                Move-Item -LiteralPath "$target.old" -Destination $target -Force -ErrorAction SilentlyContinue
            }
        }
        foreach ($relative in $files) { Remove-Item -LiteralPath ((Join-Path $InstallRoot $relative) + '.new') -Force -ErrorAction SilentlyContinue }
        if ($oldCurrent) { Set-Current $oldCurrent } else { Remove-Item -LiteralPath $currentFile -Force -ErrorAction SilentlyContinue }
        if ((Test-Path -LiteralPath $exe) -and (Test-Start)) {
            throw "Version $name couldn't be switched in ($failure), so the files that were there were put back and Waypoint was started again. Nothing was changed."
        }
        throw "Version $name couldn't be switched in ($failure), and the files that were there were put back, but Waypoint didn't start again. Run -Rollback, or start Waypoint.exe in $InstallRoot."
    }

    # Switched. The old Waypoint.exe stays as Waypoint.previous.exe, and the rest of the old files go.
    foreach ($relative in $movedAside) {
        $old = (Join-Path $InstallRoot $relative) + '.old'
        if ($relative -eq 'Waypoint.exe') {
            Move-Item -LiteralPath $old -Destination $previousExe -Force -ErrorAction SilentlyContinue
        } else {
            Remove-Item -LiteralPath $old -Force -ErrorAction SilentlyContinue
            if ($files -notcontains $relative) { Write-Host "Removed $relative, which version $name doesn't have" }
        }
    }
}

# Starts Waypoint.exe and checks it's still running after a while.
function Test-Start {
    # Start-Process passes this session's environment on, so it's set for the moment of starting and then put back.
    $saved = @{}
    foreach ($variable in $childEnvironment.Keys) {
        $saved[$variable] = [Environment]::GetEnvironmentVariable($variable, 'Process')
        [Environment]::SetEnvironmentVariable($variable, $childEnvironment[$variable], 'Process')
    }
    try {
        $process = Start-Process -FilePath $exe -WorkingDirectory $InstallRoot -PassThru
    } finally {
        foreach ($variable in $saved.Keys) { [Environment]::SetEnvironmentVariable($variable, $saved[$variable], 'Process') }
    }
    Start-Sleep -Seconds $StartCheckSeconds
    $process.Refresh()
    -not $process.HasExited
}

# Keeps the current version and the one that was running just before it, which is what -Rollback goes back to, even
# when a newer one was gone back from.
function Remove-OldVersions([string]$runningBefore) {
    $current = Get-Current
    Get-Versions | Where-Object { $_ -ne $current -and $_ -ne $runningBefore } | ForEach-Object {
        Remove-Item -LiteralPath (Join-Path $versionsDir $_) -Recurse -Force
        Write-Host "Removed old version $_"
    }
}

function Set-Launchers {
    if (-not $realInstall) {
        Write-Host 'Left the Start Menu shortcut and sign-in entry alone, since this install has its own -InstallRoot.'
        return
    }
    # Waypoint is closed by now, so a problem here is only reported and it's still started.
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'Waypoint.lnk'))
        $shortcut.TargetPath = $exe
        $shortcut.WorkingDirectory = $InstallRoot
        $shortcut.Save()

        Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Waypoint' -Value "`"$exe`""
    } catch {
        Write-Warning "Couldn't set the Start Menu shortcut or sign-in entry: $_"
    }
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
    if ($SkipTests) {
        Write-Host 'Skipped the tests, since -SkipTests was given.'
    } else {
        foreach ($project in 'tests\Waypoint.Core.Tests', 'tests\Waypoint.Server.Tests') {
            Write-Host "Running $project..."
            dotnet test (Join-Path $PSScriptRoot $project) --nologo --verbosity quiet
            if ($LASTEXITCODE -ne 0) { throw "$project failed. Nothing was changed. Fix it, or run with -SkipTests to install anyway." }
        }
    }
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
    if ($LASTEXITCODE -eq 0 -and $commit) {
        $name += "-$commit"
        # Built from changes that aren't committed yet, so the commit alone doesn't say what's in it.
        $changes = & git -C $PSScriptRoot status --porcelain 2>$null
        if ($LASTEXITCODE -eq 0 -and $changes) { $name += '-dirty' }
    }
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
$script:closeRefused = $false
$script:switchUndone = $false
try {
    Switch-To $name
} catch {
    # The running copy wasn't switched, or was put back, so the new version goes too, and -Rollback still goes back
    # from the running one.
    if ($script:closeRefused -or $script:switchUndone) { Remove-Item -LiteralPath (Join-Path $versionsDir $name) -Recurse -Force -ErrorAction SilentlyContinue }
    throw
}
Set-Launchers

if (Test-Start) {
    Remove-OldVersions $previous
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
