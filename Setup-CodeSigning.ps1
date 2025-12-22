# Setup-CodeSigning.ps1
# Creates a self-signed certificate for UIAccess code signing
# Must be run as Administrator (one-time setup)

param(
    [switch]$CreateCertOnly,
    [switch]$SignOnly
)

$ErrorActionPreference = "Stop"

$CertName = "WindowPinTray Code Signing"
$CertSubject = "CN=$CertName"
$PfxPath = "$PSScriptRoot\WindowPinTray.pfx"
$PfxPassword = "WindowPinTray2024"  # Change this if you want

# Find signtool
$SignTool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -like "*x64*" } |
    Sort-Object { [version]($_.FullName -replace '.*\\(\d+\.\d+\.\d+\.\d+)\\.*', '$1') } -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $SignTool) {
    Write-Error "SignTool not found. Please install Windows SDK."
    exit 1
}

Write-Host "Using SignTool: $SignTool" -ForegroundColor Cyan

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Create-Certificate {
    Write-Host "`n=== Creating Code Signing Certificate ===" -ForegroundColor Green

    if (-not (Test-Admin)) {
        Write-Error "Administrator privileges required to create and install certificate."
        Write-Host "Please run this script as Administrator." -ForegroundColor Yellow
        exit 1
    }

    # Check if cert already exists
    $existingCert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $CertSubject }

    if ($existingCert) {
        Write-Host "Certificate already exists: $($existingCert.Thumbprint)" -ForegroundColor Yellow
        $response = Read-Host "Delete and recreate? (y/N)"
        if ($response -eq 'y') {
            $existingCert | Remove-Item
            # Also remove from Trusted Root if present
            Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.Subject -eq $CertSubject } | Remove-Item -ErrorAction SilentlyContinue
        } else {
            return $existingCert
        }
    }

    # Create self-signed code signing certificate
    Write-Host "Creating new certificate..." -ForegroundColor Cyan
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $CertSubject `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -NotAfter (Get-Date).AddYears(5) `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")

    Write-Host "Certificate created: $($cert.Thumbprint)" -ForegroundColor Green

    # Export to PFX for backup
    Write-Host "Exporting certificate to PFX..." -ForegroundColor Cyan
    $securePassword = ConvertTo-SecureString -String $PfxPassword -Force -AsPlainText
    Export-PfxCertificate -Cert $cert -FilePath $PfxPath -Password $securePassword | Out-Null
    Write-Host "Exported to: $PfxPath" -ForegroundColor Green

    # Install in Trusted Root (required for UIAccess)
    Write-Host "Installing certificate in Trusted Root store..." -ForegroundColor Cyan

    $rootStore = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root", "LocalMachine")
    $rootStore.Open("ReadWrite")
    $rootStore.Add($cert)
    $rootStore.Close()

    Write-Host "Certificate installed in Trusted Root store." -ForegroundColor Green

    return $cert
}

function Sign-Application {
    Write-Host "`n=== Signing Application ===" -ForegroundColor Green

    # Find certificate
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $CertSubject } | Select-Object -First 1

    if (-not $cert) {
        Write-Error "Certificate not found. Run with -CreateCertOnly first."
        exit 1
    }

    Write-Host "Using certificate: $($cert.Thumbprint)" -ForegroundColor Cyan

    # Build first
    # Write-Host "Building Release configuration..." -ForegroundColor Cyan
    # Push-Location $PSScriptRoot
    # dotnet build --configuration Release
    # if ($LASTEXITCODE -ne 0) {
    #     Write-Error "Build failed"
    #     Pop-Location
    #     exit 1
    # }
    # Pop-Location

    # Sign the executables
    $releaseDir = "$PSScriptRoot\bin\Release\net9.0-windows"
    $exes = @(
        "$releaseDir\WindowPinTray.exe",
        "$releaseDir\WindowPinTray.ElevatedHelper.exe"
    )

    foreach ($exePath in $exes) {
        if (-not (Test-Path $exePath)) {
            Write-Error "Executable not found: $exePath"
            exit 1
        }

        Write-Host "Signing: $exePath" -ForegroundColor Cyan
        & $SignTool sign /sha1 $cert.Thumbprint /fd SHA256 /t http://timestamp.digicert.com $exePath

        if ($LASTEXITCODE -ne 0) {
            Write-Error "Signing failed for $exePath"
            exit 1
        }
    }

    Write-Host "`nApplications signed successfully!" -ForegroundColor Green

    # Verify signatures
    Write-Host "`nVerifying signatures..." -ForegroundColor Cyan
    foreach ($exePath in $exes) {
        & $SignTool verify /pa $exePath
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Signature verified: $exePath" -ForegroundColor Green
        }
    }

    # Copy to Program Files
    $targetDir = "C:\Program Files\Window Pin Tray"
    if (Test-Path $targetDir) {
        Write-Host "`nCopying to $targetDir..." -ForegroundColor Cyan
        try {
            Copy-Item -Path "$releaseDir\*" -Destination $targetDir -Recurse -Force
            Write-Host "Copy successful!" -ForegroundColor Green
        } catch {
            Write-Host "Failed to copy to $targetDir. Ensure you are running as Administrator and the application is closed." -ForegroundColor Red
        }
    }
}

# Main
Write-Host "WindowPinTray Code Signing Setup" -ForegroundColor Magenta
Write-Host "=================================" -ForegroundColor Magenta

if ($CreateCertOnly) {
    Create-Certificate
} elseif ($SignOnly) {
    Sign-Application
} else {
    # Full setup
    Create-Certificate
    Sign-Application

    Write-Host "`n=== Setup Complete ===" -ForegroundColor Green
    Write-Host @"

IMPORTANT: For UIAccess to work, the signed executable must be:
1. Located in a 'secure location' (Program Files or Windows\System32)
   OR
2. The secure location check must be disabled via registry (see below)

To disable secure location requirement (for development):
  reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v EnableSecureUIAPaths /t REG_DWORD /d 0 /f

To re-enable (recommended for production):
  reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v EnableSecureUIAPaths /t REG_DWORD /d 1 /f

The signed executable is at:
  $PSScriptRoot\bin\Release\net9.0-windows\WindowPinTray.exe

"@ -ForegroundColor Yellow
}
