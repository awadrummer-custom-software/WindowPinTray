# WindowPinTray

A Windows system tray application that adds pin buttons to windows, allowing you to easily pin windows to always stay on top.

## Deployment Instructions

To deploy the application after making changes:

1. **Build the release version:**
   ```
   dotnet build -c Release
   ```

2. **Kill any running instances:**
   ```
   taskkill /IM WindowPinTray.exe /F
   taskkill /IM WindowPinTray.ElevatedHelper.exe /F
   ```

3. **Copy to Program Files (requires admin):**
   ```powershell
   Copy-Item -Path "bin\Release\net9.0-windows\*" -Destination "C:\Program Files\Window Pin Tray\" -Recurse -Force
   ```

4. **Run the application from:**
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
