# Builds Waypoint.exe, installs it for this user, starts it, and runs it at sign-in.
# Run it again to install an update. Your data and settings aren't touched.

$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Waypoint'
$exe = Join-Path $installDir 'Waypoint.exe'

# The Install profile builds one small exe that uses the installed .NET 10 Desktop runtime, so that runtime has to be
# here first. Checked before anything is built or stopped, so a missing runtime never leaves Waypoint stopped.
$desktopRuntime = & dotnet --list-runtimes 2>$null | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App 10\.' }
if (-not $desktopRuntime) {
    throw @'
The .NET 10 Desktop Runtime isn't installed, and Waypoint needs it to run. Nothing was changed.
Install ".NET Desktop Runtime 10" for x64 from https://dotnet.microsoft.com/download/dotnet/10.0
(or run: winget install Microsoft.DotNet.DesktopRuntime.10), then run this again.
'@
}

# Build into a staging folder first, so the running app is only stopped for the moment it takes to copy.
$staging = Join-Path ([IO.Path]::GetTempPath()) "waypoint-publish-$([Guid]::NewGuid().ToString('N'))"

dotnet publish (Join-Path $PSScriptRoot 'src\Waypoint.App') `
    -p:PublishProfile=Install `
    --configuration Release `
    --output $staging `
    --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# Stop only the installed copy, not a trial copy running from somewhere else, and retry in case it was just restarting.
New-Item -ItemType Directory -Force $installDir | Out-Null
for ($attempt = 1; ; $attempt++) {
    Get-CimInstance Win32_Process -Filter "Name = 'Waypoint.exe'" |
        Where-Object { $_.ExecutablePath -eq $exe } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 500
    try {
        Copy-Item -Path (Join-Path $staging '*') -Destination $installDir -Recurse -Force
        break
    } catch {
        if ($attempt -ge 10) { throw }
    }
}
Remove-Item -Recurse -Force $staging

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'Waypoint.lnk'))
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $installDir
$shortcut.Save()

Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Waypoint' -Value "`"$exe`""

Start-Process -FilePath $exe
Write-Host "Installed and started $exe"
