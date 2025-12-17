using System;
using System.IO;
using System.Reflection;

namespace WindowPinTray.Services;

internal static class DebugLogger
{
    private static readonly string LogFilePath;
    private static readonly object LockObject = new();

    static DebugLogger()
    {
        var exeDir = AppContext.BaseDirectory;
        LogFilePath = Path.Combine(exeDir, "WindowPinTray_Debug.log");

        try
        {
            // Clear old log on startup
            File.WriteAllText(LogFilePath, $"=== WindowPinTray Debug Log - Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ==={Environment.NewLine}");
        }
        catch
        {
            // Ignore if can't write
        }
    }

    public static void Log(string message)
    {
        try
        {
            lock (LockObject)
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                File.AppendAllText(LogFilePath, $"[{timestamp}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Ignore write errors
        }
    }

    public static void LogWindowInfo(IntPtr hwnd, string context)
    {
        var title = WindowPinTray.Interop.NativeMethods.GetWindowText(hwnd);
        var processId = 0u;
        WindowPinTray.Interop.NativeMethods.GetWindowThreadProcessId(hwnd, out processId);

        string processName = "Unknown";
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            processName = process.ProcessName;
        }
        catch
        {
            // Ignore
        }

        Log($"{context} - HWND: 0x{hwnd:X}, Title: \"{title}\", Process: {processName} (PID: {processId})");
    }
}
