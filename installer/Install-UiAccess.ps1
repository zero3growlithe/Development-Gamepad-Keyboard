# Install-UiAccess.ps1 — puts the keyboard overlay ABOVE the Start menu.
#
# Windows keeps the Start menu (and other shell surfaces) in a z-order band that
# is unreachable by plain "always on top" windows. The only clean, non-injection
# way for a normal app to float above them is a UIAccess token: an Authenticode-
# signed executable whose manifest carries uiAccess="true", installed in a
# secure location (Program Files). This script automates all of that:
#
#   1. copies the uiAccess build (zip subfolder "uiaccess") to C:\Program Files
#   2. creates a self-signed code-signing certificate (or reuses an old one)
#      and trusts it on the machine
#   3. signs the installed executable
#   4. creates an "Development Gamepad Keyboard" Start-menu shortcut
#
# Run the uiaccess build only via the installed copy or the created shortcut —
# an unsigned uiAccess exe will not start.

$ErrorActionPreference = 'Stop'

$Subject = 'CN=Development Gamepad Keyboard UIAccess'
$InstallDir = Join-Path $env:ProgramFiles 'DevelopmentGamepadKeyboard'
$ShortcutPath = Join-Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs') 'Development Gamepad Keyboard.lnk'

# ── Self-elevation (needs admin for Program Files + machine cert store) ──
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
{
    Write-Host 'Requesting administrator rights...'
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"'
    Start-Process 'powershell.exe' -ArgumentList $arguments -Verb RunAs
    exit 0
}

# ── Locate the uiAccess build shipped in the zip ────────────────────────────
$sourceDir = Join-Path $PSScriptRoot 'uiaccess'
$sourceExe = Join-Path $sourceDir 'DevelopmentGamepadKeyboard.exe'
if (-not (Test-Path $sourceExe))
{
    Write-Error ("Cannot find '{0}'. Run this script from the app zip (uiaccess subfolder must sit next to install-uiaccess.bat)." -f $sourceExe)
    exit 1
}

Write-Host ("Stopping running instances (if any)...")
Get-Process -Name 'DevelopmentGamepadKeyboard' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

# ── 1. Copy into Program Files ──────────────────────────────────────────────
Write-Host ("Copying app files to '{0}'..." -f $InstallDir)
if (-not (Test-Path $InstallDir))
{
    New-Item -ItemType Directory -Path $InstallDir | Out-Null
}
Copy-Item -Path (Join-Path $sourceDir '*') -Destination $InstallDir -Force
$installedExe = Join-Path $InstallDir 'DevelopmentGamepadKeyboard.exe'

# ── 2. Code-signing certificate: reuse or create ────────────────────────────
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

# ── 3. Sign the installed executable ────────────────────────────────────────
Write-Host ("Signing '{0}'..." -f $installedExe)
$signature = Set-AuthenticodeSignature -FilePath $installedExe -Certificate $cert
if ($signature.Status -ne 'Valid')
{
    Write-Host ("Signature status after signing: {0}" -f $signature.Status)
    Write-Error 'Signing failed - the uiAccess executable would refuse to start.'
    exit 1
}
Write-Host 'Signature OK.'

# ── 4. Start-menu shortcut to the installed copy ────────────────────────────
Write-Host 'Creating the Start menu shortcut...'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($ShortcutPath)
$shortcut.TargetPath = $installedExe
$shortcut.WorkingDirectory = $InstallDir
$shortcut.Description = 'Development Gamepad Keyboard (keyboard overlay above the Start menu)'
$shortcut.Save()

Write-Host ''
Write-Host 'Done. Launch "Development Gamepad Keyboard" from the Start menu (or'
Write-Host ("directly: {0}" -f $installedExe)
Write-Host 'The keyboard overlay now floats above the Start menu - gamepad typing'
Write-Host 'works in Start search. If you used "Run on startup", toggle it off and'
Write-Host 'on again in Settings so the scheduled task points at the installed copy.'

exit 0