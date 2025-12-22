using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WindowPinTray.Interop;
using WindowPinTray.Models;

namespace WindowPinTray.Services;

/// <summary>
/// Manages communication with the ElevatedHelper process for handling
/// overlays on elevated windows without affecting GetAsyncKeyState in other apps.
/// </summary>
public sealed class ElevatedHelperService : IDisposable
{
    private const string PipeName = "WindowPinTray_ElevatedHelper";
    private const string MainPipeName = "WindowPinTray_Main";
    private Process? _helperProcess;
    private readonly SemaphoreSlim _pipeLock = new(1, 1);
    private CancellationTokenSource? _listenerCts;
    private bool _disposed;
    private DateTime _nextStartAttemptUtc = DateTime.MinValue;
    private string? _lastStartError;

    public event EventHandler<IntPtr>? PinRequested;

    public async Task<bool> EnsureHelperRunningAsync()
    {
        // Avoid spamming start attempts (and potential UAC prompts) if startup recently failed
        if (DateTime.UtcNow < _nextStartAttemptUtc)
        {
            return _helperProcess != null && !_helperProcess.HasExited;
        }

        if (_helperProcess != null && !_helperProcess.HasExited)
        {
            // Ping to verify it's responsive
            var response = await SendCommandAsync(new HelperCommand { Action = "PING" }).ConfigureAwait(false);
            if (response == "PONG") return true;

            // Not responsive, kill and restart
            try { _helperProcess.Kill(); } catch { }
            _helperProcess = null;
        }

        var helperPath = GetHelperPath();
        if (!File.Exists(helperPath))
        {
            DebugLogger.Log($"ElevatedHelper not found at: {helperPath}");
            return false;
        }

        try
        {
            // UIAccess helper: must be signed and located in a trusted path (e.g., Program Files) with cert in Trusted Root/Publishers
            // "A referral was returned from the server" (Error 740 or 1332) usually means uiAccess=true is set 
            // but the binary is not signed or not in a trusted location.
            _helperProcess = Process.Start(new ProcessStartInfo
            {
                FileName = helperPath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(helperPath) ?? string.Empty,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (System.ComponentModel.Win32Exception wex) when (wex.NativeErrorCode is 740 or 5 or 87 or 1008 or 1332)
        {
            _lastStartError = $"Failed to start ElevatedHelper (UIAccess). Error {wex.NativeErrorCode}: {wex.Message}. Ensure the helper exe is signed and in a trusted location (e.g., Program Files).";
            DebugLogger.Log(_lastStartError);
            _nextStartAttemptUtc = DateTime.UtcNow.AddSeconds(10); // Shorter retry for debugging
            return false;
        }
        catch (Exception ex)
        {
            _lastStartError = $"Failed to start ElevatedHelper: {ex.Message}";
            DebugLogger.Log(_lastStartError);
            _nextStartAttemptUtc = DateTime.UtcNow.AddSeconds(60);
            return false;
        }

        if (_helperProcess == null)
        {
            DebugLogger.Log("Failed to start ElevatedHelper: Process was null after start attempt.");
            return false;
        }

        // Wait for helper to start listening
        await Task.Delay(500).ConfigureAwait(false);

        // Start listening for pin requests from helper
        StartPinRequestListener();

        _lastStartError = null;
        _nextStartAttemptUtc = DateTime.MinValue;
        DebugLogger.Log($"ElevatedHelper started, PID: {_helperProcess.Id}");
        return true;
    }

    public async Task AddOverlayAsync(IntPtr hwnd)
    {
        await SendCommandAsync(new HelperCommand
        {
            Action = "ADD",
            WindowHandle = hwnd.ToInt64()
        }).ConfigureAwait(false);
    }

    public async Task RemoveOverlayAsync(IntPtr hwnd)
    {
        await SendCommandAsync(new HelperCommand
        {
            Action = "REMOVE",
            WindowHandle = hwnd.ToInt64()
        }).ConfigureAwait(false);
    }

    public async Task UpdatePositionAsync(IntPtr hwnd, int left, int top, int right, int bottom)
    {
        await SendCommandAsync(new HelperCommand
        {
            Action = "UPDATE_POSITION",
            WindowHandle = hwnd.ToInt64(),
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom
        }).ConfigureAwait(false);
    }

    public async Task UpdatePinnedStateAsync(IntPtr hwnd, bool isPinned)
    {
        await SendCommandAsync(new HelperCommand
        {
            Action = "UPDATE_PINNED",
            WindowHandle = hwnd.ToInt64(),
            IsPinned = isPinned
        }).ConfigureAwait(false);
    }

    public bool TrySetPinnedState(IntPtr hwnd, bool isPinned)
    {
        try
        {
            var helperReady = EnsureHelperRunningAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            if (!helperReady)
            {
                if (!string.IsNullOrEmpty(_lastStartError))
                {
                    DebugLogger.Log($"ElevatedHelper unavailable: {_lastStartError}");
                }
                return false;
            }

            var response = SendCommandAsync(new HelperCommand
            {
                Action = "SET_PIN_STATE",
                WindowHandle = hwnd.ToInt64(),
                IsPinned = isPinned
            }).ConfigureAwait(false).GetAwaiter().GetResult();

            return response.StartsWith("OK", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"ElevatedHelper pin fallback failed for 0x{hwnd:X}: {ex.Message}");
            return false;
        }
    }

    public bool IsWindowTopMost(IntPtr hwnd)
    {
        try
        {
            var helperReady = EnsureHelperRunningAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            if (!helperReady) return NativeMethods.IsWindowTopMost(hwnd);

            var response = SendCommandAsync(new HelperCommand
            {
                Action = "GET_PIN_STATE",
                WindowHandle = hwnd.ToInt64()
            }).ConfigureAwait(false).GetAwaiter().GetResult();

            if (response.StartsWith("OK:", StringComparison.OrdinalIgnoreCase))
            {
                return bool.Parse(response.Substring(3));
            }
        }
        catch { }

        return NativeMethods.IsWindowTopMost(hwnd);
    }

    public async Task UpdateSettingsAsync(AppSettings settings)
    {
        await SendCommandAsync(new HelperCommand
        {
            Action = "UPDATE_SETTINGS",
            Settings = new HelperSettings
            {
                ButtonWidth = settings.ButtonWidth,
                ButtonHeight = settings.ButtonHeight,
                ButtonOffsetX = settings.ButtonOffsetX,
                ButtonOffsetY = settings.ButtonOffsetY,
                CenterButton = settings.CenterButton,
                ButtonImagePath = settings.ButtonImagePath,
                ButtonHoverImagePath = settings.ButtonHoverImagePath,
                ButtonPinnedImagePath = settings.ButtonPinnedImagePath,
                ButtonPinnedHoverImagePath = settings.ButtonPinnedHoverImagePath,
                PinnedHighlightColor = settings.PinnedHighlightColor
            }
        }).ConfigureAwait(false);
    }

    public async Task ShutdownHelperAsync()
    {
        try
        {
            await SendCommandAsync(new HelperCommand { Action = "SHUTDOWN" }).ConfigureAwait(false);
        }
        catch { }

        _listenerCts?.Cancel();

        if (_helperProcess != null && !_helperProcess.HasExited)
        {
            try
            {
                _helperProcess.WaitForExit(1000);
                if (!_helperProcess.HasExited)
                    _helperProcess.Kill();
            }
            catch { }
        }
        _helperProcess = null;
    }

    private async Task<string> SendCommandAsync(HelperCommand command)
    {
        await _pipeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            await client.ConnectAsync(2000).ConfigureAwait(false);

            using var reader = new StreamReader(client);
            using var writer = new StreamWriter(client) { AutoFlush = true };

            var json = JsonSerializer.Serialize(command);
            DebugLogger.Log($"ElevatedHelper: Sending command {command.Action} for 0x{command.WindowHandle:X}");
            await writer.WriteLineAsync(json).ConfigureAwait(false);

            var response = await reader.ReadLineAsync().ConfigureAwait(false);
            DebugLogger.Log($"ElevatedHelper: Received response: {response}");
            return response ?? "ERROR:No response";
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"ElevatedHelper command failed: {ex.Message}");
            return $"ERROR:{ex.Message}";
        }
        finally
        {
            _pipeLock.Release();
        }
    }

    private void StartPinRequestListener()
    {
        _listenerCts?.Cancel();
        _listenerCts = new CancellationTokenSource();

        Task.Run(async () =>
        {
            while (!_listenerCts.Token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        MainPipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(_listenerCts.Token).ConfigureAwait(false);

                    using var reader = new StreamReader(server);
                    var line = await reader.ReadLineAsync(_listenerCts.Token).ConfigureAwait(false);

                    if (line?.StartsWith("PIN:") == true)
                    {
                        if (long.TryParse(line[4..], out var hwndValue))
                        {
                            PinRequested?.Invoke(this, new IntPtr(hwndValue));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"Pin listener error: {ex.Message}");
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }
        });
    }

    private static string GetHelperPath()
    {
        var exeDir = AppContext.BaseDirectory;
        return Path.Combine(exeDir, "WindowPinTray.ElevatedHelper.exe");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _listenerCts?.Cancel();
        _listenerCts?.Dispose();
        _pipeLock.Dispose();

        try
        {
            if (_helperProcess != null && !_helperProcess.HasExited)
            {
                _helperProcess.Kill();
            }
            _helperProcess?.Dispose();
        }
        catch { }
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
    public int ButtonWidth { get; set; }
    public int ButtonHeight { get; set; }
    public int ButtonOffsetX { get; set; }
    public int ButtonOffsetY { get; set; }
    public bool CenterButton { get; set; }
    public string? ButtonImagePath { get; set; }
    public string? ButtonHoverImagePath { get; set; }
    public string? ButtonPinnedImagePath { get; set; }
    public string? ButtonPinnedHoverImagePath { get; set; }
    public string? PinnedHighlightColor { get; set; }
}
