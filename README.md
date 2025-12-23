# WindowPinTray

A Windows system tray application that adds pin buttons to windows, allowing you to easily pin windows to always stay on top.

## Deployment Instructions

To deploy the application after making changes:

1. **Build the release version:**
   ```
   dotnet build -c Release
   ```

2. **Sign both executables (required for ElevatedHelper UIAccess):**
   ```powershell
   $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Select-Object -First 1
   Set-AuthenticodeSignature -FilePath "bin\Release\net9.0-windows\WindowPinTray.exe" -Certificate $cert -TimestampServer "http://timestamp.digicert.com"
   Set-AuthenticodeSignature -FilePath "bin\Release\net9.0-windows\WindowPinTray.ElevatedHelper.exe" -Certificate $cert -TimestampServer "http://timestamp.digicert.com"
   ```

3. **Kill any running instances (may require admin for ElevatedHelper):**
   ```
   taskkill /IM WindowPinTray.exe /F
   taskkill /IM WindowPinTray.ElevatedHelper.exe /F
   ```

4. **Copy to Program Files (requires admin):**
   ```powershell
   Copy-Item -Path "bin\Release\net9.0-windows\*" -Destination "C:\Program Files\Window Pin Tray\" -Recurse -Force
   ```

5. **Run the application from:**
   ```
   C:\Program Files\Window Pin Tray\WindowPinTray.exe
   ```

## Quick Deploy Command (for AI assistants)

> Do a release build, kill any running WindowPinTray processes, copy the built app to `C:\Program Files\Window Pin Tray` and overwrite existing files, then launch the application.

## Project Structure

- **WindowPinTray** - Main application
- **ElevatedHelper** - Helper subprocess for pinning elevated windows (Task Manager, etc.)
- **Assets/** - Button images (Pinned.png, Unpinned.png, etc.)

## Settings

User settings are stored at: `%AppData%\WindowPinTray\settings.json`
