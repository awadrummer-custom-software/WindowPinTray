using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace WindowPinTray.Services;

internal sealed class StartupRegistrationService
{
    private readonly string _shortcutPath;

    public StartupRegistrationService()
    {
        var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        _shortcutPath = Path.Combine(startupFolder, "Window Pin Tray.lnk");
    }

    public bool IsEnabled()
    {
        try
        {
            return File.Exists(_shortcutPath);
        }
        catch
        {
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            CreateShortcut();
        }
        else
        {
            RemoveShortcut();
        }
    }

    private void CreateShortcut()
    {
        try
        {
            var targetPath = GetExecutablePath();
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return;
            }

            var directory = Path.GetDirectoryName(_shortcutPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return;
            }

            dynamic? shell = null;
            dynamic? shortcut = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                if (shell is null)
                {
                    return;
                }

                shortcut = shell.CreateShortcut(_shortcutPath);
                shortcut.TargetPath = targetPath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
                shortcut.WindowStyle = 1;
                shortcut.Save();
            }
            finally
            {
                ReleaseComObject(shortcut);
                ReleaseComObject(shell);
            }
        }
        catch
        {
            // ignore failures; user can revisit setting.
        }
    }

    private void RemoveShortcut()
    {
        try
        {
            if (File.Exists(_shortcutPath))
            {
                File.Delete(_shortcutPath);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static string? GetExecutablePath()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static void ReleaseComObject(object? obj)
    {
        if (obj is null)
        {
            return;
        }

        try
        {
            Marshal.FinalReleaseComObject(obj);
        }
        catch
        {
            // ignore
        }
    }
}
