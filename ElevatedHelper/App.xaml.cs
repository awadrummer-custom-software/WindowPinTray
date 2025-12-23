using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Runtime.InteropServices;

namespace WindowPinTray.ElevatedHelper;

public partial class App : Application
{
    private const string PipeName = "WindowPinTray_ElevatedHelper";
    private readonly ConcurrentDictionary<IntPtr, OverlayWindow> _overlays = new();
    private CancellationTokenSource? _cts;
    private HelperSettings _settings = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Start named pipe server
        _cts = new CancellationTokenSource();
        Task.Run(() => RunPipeServer(_cts.Token));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _cts?.Cancel();
        foreach (var overlay in _overlays.Values)
        {
            overlay.Close();
        }
        base.OnExit(e);
    }

    private async Task RunPipeServer(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token);

                using var reader = new StreamReader(server);
                using var writer = new StreamWriter(server) { AutoFlush = true };

                while (server.IsConnected && !token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(token);
                    if (string.IsNullOrEmpty(line)) break;

                    var response = await ProcessCommand(line);
                    await writer.WriteLineAsync(response);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Pipe error: {ex.Message}");
                await Task.Delay(100, token);
            }
        }
    }

    private Task<string> ProcessCommand(string json)
    {
        try
        {
            var cmd = JsonSerializer.Deserialize<HelperCommand>(json);
            if (cmd == null) return Task.FromResult("ERROR:Invalid command");

            return cmd.Action switch
            {
                "ADD" => AddOverlay(cmd),
                "REMOVE" => RemoveOverlay(cmd),
                "UPDATE_POSITION" => UpdatePosition(cmd),
                "UPDATE_PINNED" => UpdatePinned(cmd),
                "UPDATE_SETTINGS" => UpdateSettings(cmd),
                "SET_PIN_STATE" => SetPinState(cmd),
                "GET_PIN_STATE" => GetPinState(cmd),
                "PING" => Task.FromResult("PONG"),
                "SHUTDOWN" => DoShutdown(),
                _ => Task.FromResult($"ERROR:Unknown action {cmd.Action}")
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult($"ERROR:{ex.Message}");
        }
    }

    private Task<string> AddOverlay(HelperCommand cmd)
    {
        var hwnd = new IntPtr(cmd.WindowHandle);

        if (_overlays.ContainsKey(hwnd))
        {
            return Task.FromResult("OK:Already exists");
        }

        Dispatcher.Invoke(() =>
        {
            var overlay = new OverlayWindow(hwnd, _settings);
            overlay.PinRequested += (s, e) => SendPinRequest(hwnd);
            _overlays[hwnd] = overlay;
            overlay.Show();
        });

        return Task.FromResult("OK");
    }

    private Task<string> RemoveOverlay(HelperCommand cmd)
    {
        var hwnd = new IntPtr(cmd.WindowHandle);

        if (_overlays.TryRemove(hwnd, out var overlay))
        {
            Dispatcher.Invoke(() => overlay.Close());
        }

        return Task.FromResult("OK");
    }

    private Task<string> UpdatePosition(HelperCommand cmd)
    {
        var hwnd = new IntPtr(cmd.WindowHandle);

        if (_overlays.TryGetValue(hwnd, out var overlay))
        {
            Dispatcher.Invoke(() =>
            {
                overlay.UpdatePosition(new NativeMethods.RECT
                {
                    Left = cmd.Left,
                    Top = cmd.Top,
                    Right = cmd.Right,
                    Bottom = cmd.Bottom
                });
            });
        }

        return Task.FromResult("OK");
    }

    private Task<string> UpdatePinned(HelperCommand cmd)
    {
        var hwnd = new IntPtr(cmd.WindowHandle);

        if (_overlays.TryGetValue(hwnd, out var overlay))
        {
            Dispatcher.Invoke(() => overlay.UpdatePinnedState(cmd.IsPinned));
        }

        return Task.FromResult("OK");
    }

    private Task<string> UpdateSettings(HelperCommand cmd)
    {
        if (cmd.Settings != null)
        {
            _settings = cmd.Settings;
            Dispatcher.Invoke(() =>
            {
                foreach (var overlay in _overlays.Values)
                {
                    overlay.ApplySettings(_settings);
                }
            });
        }

        return Task.FromResult("OK");
    }

    private Task<string> SetPinState(HelperCommand cmd)
    {
        var hwnd = new IntPtr(cmd.WindowHandle);

        if (!NativeMethods.IsWindow(hwnd))
        {
            return Task.FromResult("ERROR:Invalid window");
        }

        var insertAfter = cmd.IsPinned ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST;

        // Use SetWindowPos as the primary method for changing topmost state
        NativeMethods.SetWindowPos(
            hwnd,
            insertAfter,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE
            | NativeMethods.SWP_NOSIZE
            | NativeMethods.SWP_NOACTIVATE
            | NativeMethods.SWP_FRAMECHANGED);

        // Verify the state was actually applied
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        var isNowTopmost = (exStyle & NativeMethods.WS_EX_TOPMOST) != 0;

        if (cmd.IsPinned && !isNowTopmost)
        {
            // Pinning failed - return error so main app can try fallback
            return Task.FromResult("ERROR:TOPMOST not applied");
        }

        if (!cmd.IsPinned && isNowTopmost)
        {
            // Unpinning via SetWindowPos didn't work, try clearing the style bit directly
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle & ~NativeMethods.WS_EX_TOPMOST));

            // Force a frame change to apply the style change
            NativeMethods.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);

            // Verify again
            var finalExStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            if ((finalExStyle & NativeMethods.WS_EX_TOPMOST) != 0)
            {
                return Task.FromResult("ERROR:TOPMOST not cleared");
            }
        }

        return Task.FromResult("OK");
    }

    private Task<string> GetPinState(HelperCommand cmd)
    {
        var hwnd = new IntPtr(cmd.WindowHandle);
        if (!NativeMethods.IsWindow(hwnd)) return Task.FromResult("ERROR:Invalid window");

        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        var isTopMost = (exStyle & NativeMethods.WS_EX_TOPMOST) == NativeMethods.WS_EX_TOPMOST;
        return Task.FromResult($"OK:{isTopMost}");
    }

    private Task<string> DoShutdown()
    {
        Dispatcher.Invoke(() => Application.Current.Shutdown(0));
        return Task.FromResult("OK");
    }

    private void SendPinRequest(IntPtr hwnd)
    {
        // Send pin request back to main app via separate pipe connection
        Task.Run(async () =>
        {
            try
            {
                using var client = new NamedPipeClientStream(".", "WindowPinTray_Main", PipeDirection.Out);
                await client.ConnectAsync(1000);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                await writer.WriteLineAsync($"PIN:{hwnd.ToInt64()}");
            }
            catch
            {
                // Main app not listening, ignore
            }
        });
    }
}

public class HelperCommand
{
    public string Action { get; set; } = "";
    public long WindowHandle { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public bool IsPinned { get; set; }
    public HelperSettings? Settings { get; set; }
}

public class HelperSettings
{
    public int ButtonWidth { get; set; } = 24;
    public int ButtonHeight { get; set; } = 24;
    public int ButtonOffsetX { get; set; } = 8;
    public int ButtonOffsetY { get; set; } = 8;
    public bool CenterButton { get; set; } = false;
    public string? ButtonImagePath { get; set; }
    public string? ButtonHoverImagePath { get; set; }
    public string? ButtonPinnedImagePath { get; set; }
    public string? ButtonPinnedHoverImagePath { get; set; }
    public string? PinnedHighlightColor { get; set; }
}

    internal static class NativeMethods
    {
        internal const int GWL_EXSTYLE = -20;
        internal const int WS_EX_TOOLWINDOW = 0x00000080;
        internal const int WS_EX_NOACTIVATE = 0x08000000;
        internal const int WS_EX_TOPMOST = 0x00000008;
        internal const int WM_MOUSEACTIVATE = 0x21;
        internal const int MA_NOACTIVATE = 0x0003;
        internal static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        internal static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOZORDER = 0x0004;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_FRAMECHANGED = 0x0020;
        internal const uint SWP_NOOWNERZORDER = 0x0200;
        internal const uint SWP_NOSENDCHANGING = 0x0400;
        internal const uint SWP_ASYNCWINDOWPOS = 0x4000;

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy,
        uint uFlags);
}
