# Driver-uninstaller.ps1 (v4) — removes what Drivers-installer installed:
#
#   1) UIAccess            installed copy in Program Files, Start-menu shortcut,
#                          the self-signed code-signing certificate + its trust
#                          in the machine Trusted Root store
#   2) Interception driver the kernel driver itself (via the official
#                          install-interception.exe /uninstall) and the
#                          interception.dll copies next to the app
#   3) Both
#
# Normally started through Driver-uninstaller.bat (one-keypress menu). It also
# accepts flags, which is how the elevated relaunch re-passes the choice:
#   powershell -ExecutionPolicy Bypass -File Driver-uninstaller.ps1 -UiAccess
#   powershell -ExecutionPolicy Bypass -File Driver-uninstaller.ps1 -Interception
#   powershell -ExecutionPolicy Bypass -File Driver-uninstaller.ps1 -UiAccess -Interception
#
# Notes:
#   - The stock (non-uiAccess) build in the app zip is untouched and keeps working;
#     this only removes the elevated/installed flavor and the driver.
#   - The Interception uninstall needs the official package (author-hosted, LGPL,
#     not bundled): put an Interception.zip next to Driver-uninstaller.bat for the
#     offline path, otherwise it is downloaded again.
#   - Removing the Interception driver needs a REBOOT to fully unload.

param(
    [switch]$UiAccess,
    [switch]$Interception
)

$ErrorActionPreference = 'Stop'
$Subject = 'CN=Development Gamepad Keyboard UIAccess'
$InstallDir = Join-Path $env:ProgramFiles 'DevelopmentGamepadKeyboard'
$ShortcutPath = Join-Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs') 'Development Gamepad Keyboard.lnk'
$InterceptionDownloadUrl = 'https://github.com/oblitum/Interception/releases/download/v1.0.1/Interception.zip'

# ── Resolve what to remove BEFORE any re-launch, so the choice survives UAC ──
$doUiAccess = $UiAccess.IsPresent
$doInterception = $Interception.IsPresent
if (-not ($doUiAccess -or $doInterception))
{
    Write-Host ''
    Write-Host 'What do you want to uninstall?'
    Write-Host '  1) UIAccess             installed copy + shortcut + signing certificate'
    Write-Host '  2) Interception driver  kernel driver + interception.dll copies (reboot needed)'
    Write-Host '  3) Both'
    Write-Host '  Q) Quit'
    $answer = (Read-Host 'Choose 1/2/3/Q').Trim()
    switch -Regex ($answer)
    {
        '^1$' { $doUiAccess = $true }
        '^2$' { $doInterception = $true }
        '^3$' { $doUiAccess = $true; $doInterception = $true }
        '^[Qq]' { exit 0 }
        default
        {
            Write-Host 'Nothing selected - exiting.'
            Read-Host 'Press Enter to close'
            exit 1
        }
    }
}

# ── Self-elevation (needs admin for Program Files + machine cert store) ─────
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
{
    Write-Host 'Requesting administrator rights...'
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"'
    if ($doUiAccess) { $arguments += ' -UiAccess' }
    if ($doInterception) { $arguments += ' -Interception' }
    Start-Process 'powershell.exe' -ArgumentList $arguments -Verb RunAs -Wait
    exit 0
}

$failures = 0

# ══ 1) UIAccess: installed copy, shortcut, certificate ══════════════════════
if ($doUiAccess)
{
    Write-Host ''
    Write-Host '── UIAccess: removing the installed copy ──'

    Write-Host 'Stopping running app instances (if any)...'
    Get-Process -Name 'DevelopmentGamepadKeyboard' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800

    # 1a: Start-menu shortcut
    if (Test-Path $ShortcutPath)
    {
        Remove-Item $ShortcutPath -Force
        Write-Host ("Removed the Start menu shortcut: {0}" -f $ShortcutPath)
    }
    else
    {
        Write-Host 'No Start menu shortcut found (already removed?).'
    }

    # 1b: Program Files install dir
    if (Test-Path $InstallDir)
    {
        try
        {
            Remove-Item $InstallDir -Recurse -Force
            Write-Host ("Removed the install folder: {0}" -f $InstallDir)
        }
        catch
        {
            $failures++
            Write-Host ("FAILED to remove '{0}': {1}" -f $InstallDir, $_.Exception.Message)
            Write-Host 'Close the app (tray icon -> Exit) and run the uninstaller again.'
        }
    }
    else
    {
        Write-Host 'No install folder found (already removed?).'
    }

    # 1c: The self-signed certificate used for signing + its root trust
    Write-Host 'Removing the code-signing certificate and its trust...'
    $certStores = @('Cert:\CurrentUser\My', 'Cert:\LocalMachine\My', 'Cert:\LocalMachine\Root')
    $removedAnyCertificate = $false
    foreach ($store in $certStores)
    {
        $foundCertificates = Get-ChildItem $store -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -eq $Subject }
        foreach ($certificate in $foundCertificates)
        {
            $thumbprint = $certificate.Thumbprint
            try
            {
                Remove-Item -Path $certificate.PSPath -Force
                $removedAnyCertificate = $true
                Write-Host ("  removed {0} from {1}" -f $thumbprint, $store)
            }
            catch
            {
                $failures++
                Write-Host ("  FAILED to remove {0} from {1}: {2}" -f $thumbprint, $store, $_.Exception.Message)
            }
        }
    }
    if (-not $removedAnyCertificate)
    {
        Write-Host '  no certificate with this subject found (already removed?)'
    }

    Write-Host ''
    Write-Host 'UIAccess removal done. The stock build in the app zip still works normally.'
    Write-Host 'If "Run on startup" pointed at the removed copy, run the zip build once and toggle it off/on again.'
}

# ══ 2) Interception driver + DLL copies ═════════════════════════════════════
if ($doInterception)
{
    Write-Host ''
    Write-Host '── Interception driver (Parsec keyboard support) ──'
    $workDir = Join-Path $env:TEMP 'DevelopmentGamepadKeyboard-Interception'
    try
    {
        $zipPath = Join-Path $workDir 'Interception.zip'
        $localZip = Join-Path $PSScriptRoot 'Interception.zip'
        if (Test-Path $localZip)
        {
            Write-Host ("Using the Interception.zip found next to the uninstaller: {0}" -f $localZip)
            New-Item -ItemType Directory -Force -Path $workDir | Out-Null
            Copy-Item $localZip -Destination $zipPath -Force
        }
        else
        {
            Write-Host ("Downloading the official Interception package: {0}" -f $InterceptionDownloadUrl)
            New-Item -ItemType Directory -Force -Path $workDir | Out-Null
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -Uri $InterceptionDownloadUrl -OutFile $zipPath -UseBasicParsing
        }

        Write-Host 'Extracting...'
        if (Test-Path (Join-Path $workDir 'Interception'))
        {
            Remove-Item (Join-Path $workDir 'Interception') -Recurse -Force
        }
        Expand-Archive -Path $zipPath -DestinationPath $workDir -Force

        $installerExe = Join-Path $workDir 'Interception\command line installer\install-interception.exe'
        if (-not (Test-Path $installerExe))
        {
            throw 'The package is missing command line installer\install-interception.exe.'
        }

        Write-Host 'Running the driver uninstaller (silent)...'
        & $installerExe '/uninstall'
        $driverExitCode = $LASTEXITCODE
        if ($driverExitCode -ne 0)
        {
            Write-Host ("install-interception.exe exited with code {0} - it may simply mean the driver was not installed." -f $driverExitCode)
        }
        else
        {
            Write-Host 'Driver uninstalled.'
        }

        Write-Host 'Deleting the interception.dll copies...'
        $dllTargets = @($PSScriptRoot, $InstallDir)
        $removedAnyDll = $false
        foreach ($folder in $dllTargets)
        {
            $dllPath = Join-Path $folder 'interception.dll'
            if (Test-Path $dllPath)
            {
                try
                {
                    Remove-Item $dllPath -Force
                    $removedAnyDll = $true
                    Write-Host ("  removed {0}" -f $dllPath)
                }
                catch
                {
                    $failures++
                    Write-Host ("  FAILED to remove {0}: {1}" -f $dllPath, $_.Exception.Message)
                }
            }
        }
        if (-not $removedAnyDll)
        {
            Write-Host '  no interception.dll copies found (already removed?)'
        }

        Write-Host ''
        Write-Host 'REBOOT REQUIRED - the driver stays loaded until the machine restarts.'
        Write-Host 'After rebooting: Settings > Keyboard > turn OFF "Send keys through the Interception driver".'
        Write-Host 'With the driver gone the app falls back to SendInput automatically.'
    }
    catch
    {
        Write-Host ("Interception step FAILED: {0}" -f $_.Exception.Message)
        Write-Host 'Manually: get the official zip from https://github.com/oblitum/Interception/releases,'
        Write-Host 'place it next to Driver-uninstaller.bat as Interception.zip and run this uninstaller again.'
        Write-Host 'Then reboot to unload the driver.'
    }
    finally
    {
        Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
if ($failures -eq 0)
{
    Write-Host 'Done - everything selected was removed.'
}
else
{
    Write-Host ("Done WITH {0} PROBLEM(S) - see the FAILED lines above." -f $failures)
}
Read-Host 'Press Enter to close'
exit 0
