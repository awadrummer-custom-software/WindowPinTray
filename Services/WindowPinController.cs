using System;
using System.Windows.Threading;
using WindowPinTray.Interop;
using WindowPinTray.Models;
using WindowPinTray.UI;

namespace WindowPinTray.Services;

internal sealed class WindowPinController : IDisposable
{
    private readonly IntPtr _targetHwnd;
    private readonly ElevatedHelperService _elevatedHelperService;
    private readonly OverlayButtonWindow _overlayWindow;
    private bool _isPinned;
    private DateTime _lastToggleTime = DateTime.MinValue;
    private DateTime _visibilityLockUntil = DateTime.MinValue;
    private static readonly TimeSpan SyncCooldown = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan VisibilityLockDuration = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PositionUpdateThrottle = TimeSpan.FromMilliseconds(33);
    private DateTime _lastPositionUpdate = DateTime.MinValue;
    private DispatcherTimer? _positionUpdateTimer;

    public bool IsPinned => _isPinned;
    public bool HasLoggedInitialState { get; set; }

    public WindowPinController(IntPtr targetHwnd, AppSettings settings, ElevatedHelperService elevatedHelperService)
    {
        _targetHwnd = targetHwnd;
        _elevatedHelperService = elevatedHelperService;
        _overlayWindow = new OverlayButtonWindow(targetHwnd, settings);
        _overlayWindow.PinRequested += HandlePinRequested;
        _isPinned = _elevatedHelperService.IsWindowTopMost(_targetHwnd);
        UpdatePosition();
    }

    public void UpdateSettings(AppSettings settings)
    {
        _overlayWindow.ApplySettings(settings);
        // Don't sync state here - let RefreshPinnedState handle it with cooldown protection
        UpdatePosition();
    }

    public void UpdatePosition()
    {
        _positionUpdateTimer?.Stop();
        _positionUpdateTimer = null;
        UpdatePositionNow();
    }

    public void RequestPositionUpdate()
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _lastPositionUpdate;
        if (elapsed >= PositionUpdateThrottle)
        {
            UpdatePositionNow();
            return;
        }

        _positionUpdateTimer ??= CreatePositionUpdateTimer();

        var remaining = PositionUpdateThrottle - elapsed;
        if (remaining < TimeSpan.FromMilliseconds(1))
        {
            remaining = TimeSpan.FromMilliseconds(1);
        }

        _positionUpdateTimer.Interval = remaining;
        if (!_positionUpdateTimer.IsEnabled)
        {
            _positionUpdateTimer.Start();
        }
    }

    public void BringToFront()
    {
        _overlayWindow.ForceZOrderUpdate();
    }

    private DispatcherTimer CreatePositionUpdateTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _overlayWindow.Dispatcher);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            UpdatePositionNow();
        };
        return timer;
    }

    private void UpdatePositionNow()
    {
        // Use visible bounds (excluding DWM shadow) for accurate positioning
        if (!NativeMethods.GetVisibleWindowRect(_targetHwnd, out var rect))
        {
            return;
        }

        _overlayWindow.UpdatePosition(rect);
        _lastPositionUpdate = DateTime.UtcNow;
    }

    public void ApplyVisibility(bool shouldShow)
    {
        // Only show if window is visible AND should show (exposed or pinned)
        var isWindowVisible = NativeMethods.IsWindowVisible(_targetHwnd) && !NativeMethods.IsIconic(_targetHwnd);

        if (!isWindowVisible || !shouldShow)
        {
            if (_overlayWindow.IsVisible)
            {
                _overlayWindow.Hide();
            }
            return;
        }

        // If we can't own the window, we must use TOPMOST to stay above it.
        // To avoid floating over other windows, only show if the button area is not obscured by a window above the target.
        if (!_overlayWindow.IsOwnerSet)
        {
            bool isTopMost = NativeMethods.IsWindowTopMost(_targetHwnd);
            
            // If it's already pinned (TopMost), we generally want to show it.
            // But if another TopMost window covers it, we might want to hide it?
            // For now, let's assume pinned windows should always show their button unless obscured.
            
            var buttonRect = _overlayWindow.GetButtonRect();
            bool isObscured = WindowUtilities.IsRectObscured(buttonRect, _targetHwnd, _overlayWindow.Handle);

            if (isObscured)
            {
                // If obscured, hide it.
                // Exception: If we are currently mousing over the button, and for some reason IsRectObscured thinks it's obscured
                // (maybe by a transparent window?), we might want to keep it?
                // But generally, if it's visually obscured, we should hide.
                
                if (_overlayWindow.IsVisible)
                {
                    _overlayWindow.Hide();
                }
                return;
            }
        }

        // Show the button
        if (!_overlayWindow.IsVisible)
        {
            _overlayWindow.Show();
        }
        else
        {
            _overlayWindow.Visibility = System.Windows.Visibility.Visible;
        }

        _overlayWindow.EnsureVisibleOnTop();
    }

    public void Hide()
    {
        if (_overlayWindow.IsVisible)
        {
            _overlayWindow.Hide();
        }
    }

    public void Dispose()
    {
        _overlayWindow.PinRequested -= HandlePinRequested;
        _overlayWindow.Close();
    }

    private void HandlePinRequested(object? sender, EventArgs e)
    {
        TogglePinned();
    }

    private void TogglePinned()
    {
        var originalPinned = _isPinned;
        var targetPinned = !_isPinned;
        var insertAfter = targetPinned ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST;

        DebugLogger.Log($"TogglePinned requested for 0x{_targetHwnd:X}. Current: {originalPinned}, Target: {targetPinned}");

        // Set state BEFORE calling SetWindowPos so any events triggered during the call
        // will see the correct pin state and won't hide the button
        var now = DateTime.UtcNow;
        _isPinned = targetPinned;
        _lastToggleTime = now;
        _visibilityLockUntil = now + VisibilityLockDuration;
        _overlayWindow.UpdatePinnedState(_isPinned);

        bool success;
        
        // Try using the elevated helper first if the window might be elevated
        // or if we're having trouble with normal SetWindowPos.
        // Task Manager and other system windows often require this.
        if (_elevatedHelperService.TrySetPinnedState(_targetHwnd, targetPinned))
        {
            DebugLogger.Log($"TogglePinned: ElevatedHelper successfully set state to {targetPinned}");
            success = true;
        }
        else
        {
            DebugLogger.Log($"TogglePinned: ElevatedHelper failed or unavailable, falling back to SetWindowPos");

            success = NativeMethods.SetWindowPos(
                _targetHwnd,
                insertAfter,
                0,
                0,
                0,
                0,
                NativeMethods.SetWindowPosFlags.SWP_NOMOVE
                | NativeMethods.SetWindowPosFlags.SWP_NOSIZE
                | NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE
                | NativeMethods.SetWindowPosFlags.SWP_FRAMECHANGED);

            // If we're not elevated, we can't unpin an elevated window.
            // But we can try to clear the style bit anyway just in case.
            if (!targetPinned)
            {
                // Explicitly clear style bit for unpinning fallback
                var exStyle = NativeMethods.GetWindowLongPtr(_targetHwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                if ((exStyle & NativeMethods.WS_EX_TOPMOST) != 0)
                {
                    NativeMethods.SetWindowLongPtr(_targetHwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle & ~NativeMethods.WS_EX_TOPMOST));
                    
                    // Force a frame change to apply the style change
                    NativeMethods.SetWindowPos(
                        _targetHwnd,
                        IntPtr.Zero,
                        0, 0, 0, 0,
                        NativeMethods.SetWindowPosFlags.SWP_NOMOVE | 
                        NativeMethods.SetWindowPosFlags.SWP_NOSIZE | 
                        NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE | 
                        NativeMethods.SetWindowPosFlags.SWP_NOZORDER | 
                        NativeMethods.SetWindowPosFlags.SWP_FRAMECHANGED);

                    // Verify if it's actually gone
                    var finalExStyle = NativeMethods.GetWindowLongPtr(_targetHwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                    if ((finalExStyle & NativeMethods.WS_EX_TOPMOST) == 0)
                    {
                        success = true;
                    }
                }
            }
            
            if (success)
            {
                DebugLogger.Log($"TogglePinned: SetWindowPos succeeded");
            }
            else
            {
                var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                DebugLogger.Log($"TogglePinned: SetWindowPos failed with error {error}");
                
                // If we failed with Access Denied (5) and it's an elevated window,
                // we really need that helper.
                if (error == 5)
                {
                    DebugLogger.Log("TogglePinned: Access Denied (5). This window is likely elevated and requires the signed ElevatedHelper in a trusted folder.");
                }
            }
        }

        if (!success)
        {
            DebugLogger.Log($"TogglePinned: Operation failed, reverting state");
            // Revert state if the operation failed
            _isPinned = originalPinned;
            _visibilityLockUntil = DateTime.MinValue;
            _overlayWindow.UpdatePinnedState(_isPinned);
            SyncPinnedState();
            return;
        }

        ApplyVisibility(true);
    }

    public void RefreshPinnedState()
    {
        SyncPinnedState();
    }

    private void SyncPinnedState()
    {
        // Don't sync immediately after toggling to avoid race conditions
        if (DateTime.UtcNow - _lastToggleTime < SyncCooldown)
        {
            return;
        }

        var actualPinned = _elevatedHelperService.IsWindowTopMost(_targetHwnd);
        if (actualPinned != _isPinned)
        {
            DebugLogger.Log($"SyncPinnedState: State mismatch for 0x{_targetHwnd:X}. Internal: {_isPinned}, Actual: {actualPinned}. Updating internal state.");
            _isPinned = actualPinned;
            _overlayWindow.UpdatePinnedState(_isPinned);
        }
    }
}
