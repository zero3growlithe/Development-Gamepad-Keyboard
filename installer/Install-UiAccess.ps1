# Install-UiAccess.ps1 — does two things:
#
#   A) puts the keyboard overlay ABOVE the Start menu   (always, no prompt)
#   B) optionally installs the Interception driver used by the "Send keys
#      through the Interception driver" toggle (Parsec-host keyboards)
#      — asked once at start, answer Y/N.
#
# Why these need an installer:
#   A) Windows keeps the Start menu in a z-order band that plain always-on-top
#      windows can never reach. The supported workaround is a UIAccess token:
#      an Authenticode-signed executable whose manifest carries
#      uiAccess="true", installed in a secure location (Program Files). Its
#      windows land in the topmost ZBID_UIACCESS band (same mechanism as the
#      built-in touch keyboard / osk.exe). This script:
#        1. copies the uiAccess build (zip subfolder "uiaccess") to
#           C:\Program Files\DevelopmentGamepadKeyboard
#        2. creates a self-signed code-signing certificate (or reuses one)
#           and trusts it on the machine
#        3. signs the installed executable
#        4. creates a "Development Gamepad Keyboard" Start-menu shortcut
#      Run the uiAccess build only via the installed copy or the created
#      shortcut — an unsigned uiAccess exe will not start.
#
#   B) The Interception driver (github.com/oblitum/Interception) re-introduces
#      real keyboard-device input, which the Parsec host forwards to games
#      (SendInput keys are filtered there as "injected"). The binaries belong
#      to the author (LGPL for non-commercial use) and are NOT bundled: this
#      step downloads the official Interception.zip release, runs its silent
#      /install (driver; REBOOT required) and copies interception.dll next to
#      the app executable. Afterwards enable the toggle in
#      Settings > Keyboard > "Send keys through the Interception driver".
#
# Offline fallback: put an Interception.zip next to install-uiaccess.bat and
# the driver step uses it instead of downloading.

param(
    [switch]$InstallInterception,
    [switch]$SkipInterception
)

$ErrorActionPreference = 'Stop'
$Subject = 'CN=Development Gamepad Keyboard UIAccess'
$InstallDir = Join-Path $env:ProgramFiles 'DevelopmentGamepadKeyboard'
$ShortcutPath = Join-Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs') 'Development Gamepad Keyboard.lnk'
$InterceptionDownloadUrl = 'https://github.com/oblitum/Interception/releases/download/v1.0.1/Interception.zip'

# ── Resolve the driver toggle BEFORE any re-launch, so the choice survives ──
$shouldInstallDriver = $false
if ($InstallInterception) { $shouldInstallDriver = $true }
elseif ($SkipInterception) { $shouldInstallDriver = $false }
else
{
    Write-Host ''
    Write-Host 'Optional: install the Interception driver for keyboard input over Parsec?'
    Write-Host '(downloads the official package, needs a REBOOT afterwards; Y/N)'
    $answer = Read-Host 'Install Interception driver [y/N]'
    $shouldInstallDriver = $answer -match '^[Yy]'
}

# ── Self-elevation (needs admin for Program Files + machine cert store) ─────
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
{
    Write-Host 'Requesting administrator rights...'
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"'
    if ($shouldInstallDriver) { $arguments += ' -InstallInterception' } else { $arguments += ' -SkipInterception' }
    Start-Process 'powershell.exe' -ArgumentList $arguments -Verb RunAs -Wait
    exit 0
}

# ── Locate the uiAccess build shipped in the zip ────────────────────────────
$sourceDir = Join-Path $PSScriptRoot 'uiaccess'
$sourceExe = Join-Path $sourceDir 'DevelopmentGamepadKeyboard.exe'
if (-not (Test-Path $sourceExe))
{
    Write-Host ("ERROR: cannot find '{0}'." -f $sourceExe)
    Write-Host 'Run this script from the app zip: the uiaccess subfolder must sit next to install-uiaccess.bat.'
    Read-Host 'Press Enter to close'
    exit 1
}

Write-Host 'Stopping running app instances (if any)...'
Get-Process -Name 'DevelopmentGamepadKeyboard' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

# ── A/1: Copy into Program Files ────────────────────────────────────────────
Write-Host ("Copying app files to '{0}'..." -f $InstallDir)
if (-not (Test-Path $InstallDir))
{
    New-Item -ItemType Directory -Path $InstallDir | Out-Null
}
Copy-Item -Path (Join-Path $sourceDir '*') -Destination $InstallDir -Force
$installedExe = Join-Path $InstallDir 'DevelopmentGamepadKeyboard.exe'

# ── A/2: Code-signing certificate: reuse or create ──────────────────────────
Write-Host 'Preparing the code-signing certificate...'
$existing = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $Subject } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if ($existing)
{
    $cert = $existing
    Write-Host ("Reusing existing certificate {0} (valid until {1:yyyy-MM-dd})." -f $cert.Thumbprint, $cert.NotAfter)
}
else
{
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject `
        -KeyUsage DigitalSignature -KeySpec Signature -KeyExportPolicy Exportable `
        -NotAfter (Get-Date).AddYears(20) -CertStoreLocation Cert:\CurrentUser\My
    Write-Host ("Created certificate {0} (valid until {1:yyyy-MM-dd})." -f $cert.Thumbprint, $cert.NotAfter)
}

# Trust the certificate on the machine (the signature must chain to a trusted root).
$cerPath = Join-Path $env:TEMP 'DevelopmentGamepadKeyboard-signer.cer'
Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT -Force | Out-Null
$rootStore = Get-ChildItem Cert:\LocalMachine\Root |
    Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
if (-not $rootStore)
{
    Write-Host 'Adding the certificate to the machine Trusted Root store...'
    Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
}
Remove-Item $cerPath -Force -ErrorAction SilentlyContinue

# ── A/3: Sign the installed executable ──────────────────────────────────────
Write-Host ("Signing '{0}'..." -f $installedExe)
$signature = Set-AuthenticodeSignature -FilePath $installedExe -Certificate $cert
if ($signature.Status -ne 'Valid')
{
    Write-Host ("Signature status after signing: {0}" -f $signature.Status)
    Write-Host 'ERROR: signing failed - the uiAccess executable would refuse to start.'
    Read-Host 'Press Enter to close'
    exit 1
}
Write-Host 'Signature OK.'

# ── A/4: Start-menu shortcut to the installed copy ──────────────────────────
Write-Host 'Creating the Start menu shortcut...'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($ShortcutPath)
$shortcut.TargetPath = $installedExe
$shortcut.WorkingDirectory = $InstallDir
$shortcut.Description = 'Development Gamepad Keyboard (keyboard overlay above the Start menu)'
$shortcut.Save()

Write-Host ''
Write-Host ("Keyboard overlay above the Start menu: installed -> {0}" -f $installedExe)
Write-Host 'Launch "Development Gamepad Keyboard" from the Start menu (the zip copy keeps stock behaviour).'
Write-Host 'If you used "Run on startup", toggle it off and on again in Settings so the scheduled task points at the installed copy.'

# ── B: Optional Interception driver (Parsec keyboard support) ───────────────
if ($shouldInstallDriver)
{
    Write-Host ''
    Write-Host '── Interception driver (Parsec keyboard support) ──'
    $workDir = Join-Path $env:TEMP 'DevelopmentGamepadKeyboard-Interception'
    try
    {
        # Prefer a zip the user dropped next to install-uiaccess.bat (offline path);
        # otherwise download the official release (author-hosted LGPL binaries).
        $zipPath = Join-Path $workDir 'Interception.zip'
        $localZip = Join-Path $PSScriptRoot 'Interception.zip'
        if (Test-Path $localZip)
        {
            Write-Host ("Using the Interception.zip found next to the installer: {0}" -f $localZip)
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
        $driverDll = Join-Path $workDir 'Interception\library\x64\interception.dll'
        if (-not ((Test-Path $installerExe) -and (Test-Path $driverDll)))
        {
            throw 'The downloaded package is missing install-interception.exe or library\x64\interception.dll.'
        }

        Write-Host 'Running the driver installer (silent)...'
        & $installerExe '/install'
        $driverExitCode = $LASTEXITCODE
        if ($driverExitCode -ne 0)
        {
            Write-Host ("install-interception.exe exited with code {0} - check the message above." -f $driverExitCode)
        }

        Write-Host 'Copying interception.dll into the app folders...'
        Copy-Item $driverDll -Destination $InstallDir -Force
        Copy-Item $driverDll -Destination $PSScriptRoot -Force

        Write-Host ''
        Write-Host 'REBOOT REQUIRED - the Interception driver loads at boot.'
        Write-Host 'After rebooting: Settings > Keyboard > enable'
        Write-Host '"Send keys through the Interception driver (physical input; fixes Parsec host)".'
        Write-Host 'Until that toggle is on the app keeps using SendInput.'
    }
    catch
    {
        Write-Host ("Interception step FAILED: {0}" -f $_.Exception.Message)
        Write-Host 'Manually: get Interception.zip from https://github.com/oblitum/Interception/releases (or https://oblita.com/interception.html),'
        Write-Host 'place it next to install-uiaccess.bat as Interception.zip and run this installer again.'
        Write-Host 'The rest of the installation is complete.'
    }
    finally
    {
        Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Read-Host 'Press Enter to close'
exit 0