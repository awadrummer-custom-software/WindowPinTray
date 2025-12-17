using System;
using System.Windows.Threading;
using WindowPinTray.Interop;
using WindowPinTray.Models;
using WindowPinTray.UI;

namespace WindowPinTray.Services;

internal sealed class WindowPinController : IDisposable
{
    private readonly IntPtr _targetHwnd;
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

    public WindowPinController(IntPtr targetHwnd, AppSettings settings)
    {
        _targetHwnd = targetHwnd;
        _overlayWindow = new OverlayButtonWindow(targetHwnd, settings);
        _overlayWindow.PinRequested += HandlePinRequested;
        _isPinned = NativeMethods.IsWindowTopMost(_targetHwnd);
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
        if (!NativeMethods.GetWindowRect(_targetHwnd, out var rect))
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

        // Set state BEFORE calling SetWindowPos so any events triggered during the call
        // will see the correct pin state and won't hide the button
        var now = DateTime.UtcNow;
        _isPinned = targetPinned;
        _lastToggleTime = now;
        _visibilityLockUntil = now + VisibilityLockDuration;
        _overlayWindow.UpdatePinnedState(_isPinned);

        var success = NativeMethods.SetWindowPos(
            _targetHwnd,
            insertAfter,
            0,
            0,
            0,
            0,
            NativeMethods.SetWindowPosFlags.SWP_NOMOVE
            | NativeMethods.SetWindowPosFlags.SWP_NOSIZE
            | NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE);

        if (!success)
        {
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

        _isPinned = NativeMethods.IsWindowTopMost(_targetHwnd);
        _overlayWindow.UpdatePinnedState(_isPinned);
    }
}
