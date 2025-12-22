using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using WindowPinTray.Interop;
using WindowPinTray.Models;

namespace WindowPinTray.Services;

internal sealed class WindowOverlayManager : IDisposable
{
    [Flags]
    private enum PendingWindowAction
    {
        None = 0,
        Attach = 1,
        Update = 2,
        Hide = 4,
        Detach = 8,
        MoveStart = 16,
        MoveEnd = 32
    }

    private readonly SettingsService _settingsService;
    private readonly ElevatedHelperService _elevatedHelperService;
    private readonly Dictionary<IntPtr, WindowPinController> _controllers = new();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _syncTimer;
    private readonly DispatcherTimer _foregroundPollTimer;
    private readonly DispatcherTimer _movePollTimer;
    private readonly DispatcherTimer _positionPollTimer;
    private readonly List<IntPtr> _eventHooks = new();
    private readonly object _pendingLock = new();
    private readonly Dictionary<IntPtr, PendingWindowAction> _pendingActions = new();
    private bool _pendingFlushScheduled;
    private readonly NativeMethods.WinEventDelegate _objectEventHandler;
    private readonly NativeMethods.WinEventDelegate _systemEventHandler;
    private AppSettings _settings;
    private readonly HashSet<IntPtr> _movingWindows = new();

    private bool _isRunning;

    public WindowOverlayManager(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _elevatedHelperService = new ElevatedHelperService();
        _dispatcher = Dispatcher.CurrentDispatcher;
        _objectEventHandler = HandleObjectEvent;
        _systemEventHandler = HandleSystemEvent;
        _settingsService.SettingsChanged += OnSettingsChanged;
        _settings = _settingsService.CurrentSettings;

        _syncTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(2),
            DispatcherPriority.Background,
            (_, _) => SyncControllers(),
            _dispatcher)
        {
            IsEnabled = false
        };

        _foregroundPollTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(33),
            DispatcherPriority.Background,
            (_, _) => PollForegroundWindow(),
            _dispatcher)
        {
            IsEnabled = false
        };

        _movePollTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(33),
            DispatcherPriority.Background,
            (_, _) => PollMovingWindows(),
            _dispatcher)
        {
            IsEnabled = false
        };

        _positionPollTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(50),
            DispatcherPriority.Background,
            (_, _) => PollAllControllerPositions(),
            _dispatcher)
        {
            IsEnabled = false
        };
    }

    public void Start()
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        _settings = _settingsService.CurrentSettings;

        DebugLogger.Log("=== WindowOverlayManager Starting ===");
        var candidates = WindowUtilities.EnumerateCandidateWindows(_settings);
        DebugLogger.Log($"Found {candidates.Count} candidate windows at startup");

        foreach (var hwnd in candidates)
        {
            AttachWindow(hwnd);
        }

        InstallHooks();
        EnsureSyncTimer();

        if (!_foregroundPollTimer.IsEnabled)
        {
            _foregroundPollTimer.Start();
        }

        if (!_positionPollTimer.IsEnabled)
        {
            _positionPollTimer.Start();
        }
    }

    public void Dispose()
    {
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _isRunning = false;

        UninstallHooks();

        foreach (var controller in _controllers.Values.ToList())
        {
            controller.Dispose();
        }

        _controllers.Clear();
        _syncTimer.Stop();
        _foregroundPollTimer.Stop();
        _movePollTimer.Stop();
        _positionPollTimer.Stop();

        _elevatedHelperService.Dispose();
    }

    private void HandleObjectEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        if (!_isRunning || hwnd == IntPtr.Zero)
        {
            return;
        }

        // Some apps (including GPU-heavy ones) often raise LOCATIONCHANGE for client objects rather than OBJID_WINDOW.
        // We still want to track the window position, so allow LOCATIONCHANGE through regardless of idObject.
        if (eventType != NativeMethods.EVENT_OBJECT_LOCATIONCHANGE && idObject != NativeMethods.OBJID_WINDOW)
        {
            return;
        }

        QueueWindowEvent(eventType, hwnd);
    }

    private void HandleSystemEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        if (!_isRunning || hwnd == IntPtr.Zero)
        {
            return;
        }

        QueueWindowEvent(eventType, hwnd);
    }

    private void QueueWindowEvent(uint eventType, IntPtr hwnd)
    {
        var action = eventType switch
        {
            NativeMethods.EVENT_OBJECT_CREATE => PendingWindowAction.Attach,
            NativeMethods.EVENT_OBJECT_SHOW => PendingWindowAction.Attach,
            NativeMethods.EVENT_OBJECT_HIDE => PendingWindowAction.Detach,
            NativeMethods.EVENT_OBJECT_DESTROY => PendingWindowAction.Detach,
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE => PendingWindowAction.Update,
            NativeMethods.EVENT_SYSTEM_FOREGROUND => PendingWindowAction.Attach,
            NativeMethods.EVENT_SYSTEM_MINIMIZESTART => PendingWindowAction.Hide,
            NativeMethods.EVENT_SYSTEM_MINIMIZEEND => PendingWindowAction.Attach,
            NativeMethods.EVENT_SYSTEM_MOVESIZESTART => PendingWindowAction.MoveStart | PendingWindowAction.Update,
            NativeMethods.EVENT_SYSTEM_MOVESIZEEND => PendingWindowAction.MoveEnd | PendingWindowAction.Update,
            _ => PendingWindowAction.None
        };

        if (action == PendingWindowAction.None)
        {
            return;
        }

        // LOCATIONCHANGE and MOVESIZE events are frequently raised for child HWNDs / client objects; normalize
        // to the root owner so the overlay attached to the top-level window can still track movement.
        if (action.HasFlag(PendingWindowAction.Update) || action.HasFlag(PendingWindowAction.MoveStart) || action.HasFlag(PendingWindowAction.MoveEnd))
        {
            hwnd = WindowUtilities.NormalizeUpdateHwnd(hwnd);
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            if (action == PendingWindowAction.Update && !_controllers.ContainsKey(hwnd))
            {
                return;
            }
        }

        lock (_pendingLock)
        {
            if (_pendingActions.TryGetValue(hwnd, out var existing))
            {
                _pendingActions[hwnd] = existing | action;
            }
            else
            {
                _pendingActions[hwnd] = action;
            }

            if (_pendingFlushScheduled)
            {
                return;
            }

            _pendingFlushScheduled = true;
        }

        _ = _dispatcher.BeginInvoke(new Action(FlushPendingEvents), DispatcherPriority.Background);
    }

    private void FlushPendingEvents()
    {
        Dictionary<IntPtr, PendingWindowAction> snapshot;

        lock (_pendingLock)
        {
            snapshot = new Dictionary<IntPtr, PendingWindowAction>(_pendingActions);
            _pendingActions.Clear();
            _pendingFlushScheduled = false;
        }

        foreach (var (hwnd, action) in snapshot)
        {
            ProcessPendingAction(hwnd, action);
        }
    }

    private void ProcessPendingAction(IntPtr hwnd, PendingWindowAction action)
    {
        if (action.HasFlag(PendingWindowAction.Detach))
        {
            StopMoveTracking(hwnd);
            DetachWindow(hwnd);
            return;
        }

        if (action.HasFlag(PendingWindowAction.Hide))
        {
            StopMoveTracking(hwnd);
            HideWindow(hwnd);
            return;
        }

        if (action.HasFlag(PendingWindowAction.Attach))
        {
            AttachWindow(hwnd);
            return;
        }

        if (action.HasFlag(PendingWindowAction.MoveStart))
        {
            StartMoveTracking(hwnd);
        }

        if (action.HasFlag(PendingWindowAction.Update))
        {
            UpdateWindow(hwnd);
        }

        if (action.HasFlag(PendingWindowAction.MoveEnd))
        {
            StopMoveTracking(hwnd);
        }
    }

    private void AttachWindow(IntPtr hwnd)
    {
        if (!WindowUtilities.IsCandidateWindow(hwnd, _settings))
        {
            DetachWindow(hwnd);
            return;
        }

        if (_controllers.TryGetValue(hwnd, out var existing))
        {
            existing.RefreshPinnedState();
            existing.RequestPositionUpdate();
            existing.BringToFront(); // Force z-order update when window gains focus
            existing.ApplyVisibility(true);
            return;
        }

        var controller = new WindowPinController(hwnd, _settings, _elevatedHelperService);
        _controllers[hwnd] = controller;
        controller.ApplyVisibility(true);
    }

    private void UpdateWindow(IntPtr hwnd)
    {
        if (_controllers.TryGetValue(hwnd, out var controller))
        {
            if (NativeMethods.IsIconic(hwnd) || !NativeMethods.IsWindowVisible(hwnd))
            {
                controller.Hide();
                return;
            }

            controller.RequestPositionUpdate();
        }
    }

    private void StartMoveTracking(IntPtr hwnd)
    {
        if (!_controllers.ContainsKey(hwnd))
        {
            return;
        }

        if (_movingWindows.Add(hwnd) && !_movePollTimer.IsEnabled)
        {
            _movePollTimer.Start();
        }
    }

    private void StopMoveTracking(IntPtr hwnd)
    {
        if (_movingWindows.Remove(hwnd) && _movingWindows.Count == 0)
        {
            _movePollTimer.Stop();
        }
    }

    private void PollMovingWindows()
    {
        if (_movingWindows.Count == 0)
        {
            _movePollTimer.Stop();
            return;
        }

        foreach (var hwnd in _movingWindows.ToList())
        {
            if (_controllers.TryGetValue(hwnd, out var controller))
            {
                controller.UpdatePosition();
            }
            else
            {
                _movingWindows.Remove(hwnd);
            }
        }
    }

    private void HideWindow(IntPtr hwnd)
    {
        if (_controllers.TryGetValue(hwnd, out var controller))
        {
            controller.Hide();
        }
    }

    private void DetachWindow(IntPtr hwnd)
    {
        if (_controllers.Remove(hwnd, out var controller))
        {
            controller.Dispose();
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings e)
    {
        _settings = e.Clone();

        foreach (var hwnd in _controllers.Keys.ToList())
        {
            if (!WindowUtilities.IsCandidateWindow(hwnd, _settings))
            {
                DetachWindow(hwnd);
            }
            else if (_controllers.TryGetValue(hwnd, out var controller))
            {
                controller.UpdateSettings(_settings);
            }
        }

        foreach (var hwnd in WindowUtilities.EnumerateCandidateWindows(_settings))
        {
            if (!_controllers.ContainsKey(hwnd))
            {
                AttachWindow(hwnd);
            }
        }

        EnsureSyncTimer();
    }

    private void EnsureSyncTimer()
    {
        if (!_syncTimer.IsEnabled)
        {
            _syncTimer.Start();
        }
    }

    private void InstallHooks()
    {
        UninstallHooks();

        const int flags = NativeMethods.WINEVENT_OUTOFCONTEXT
                          | NativeMethods.WINEVENT_SKIPOWNPROCESS
                          | NativeMethods.WINEVENT_SKIPOWNTHREAD;

        // Hook only the specific events we handle to avoid event storms in complex apps (e.g., Illustrator).
        foreach (var evt in new[]
                 {
                     NativeMethods.EVENT_OBJECT_CREATE,
                     NativeMethods.EVENT_OBJECT_SHOW,
                     NativeMethods.EVENT_OBJECT_HIDE,
                     NativeMethods.EVENT_OBJECT_DESTROY
                 })
        {
            var hook = NativeMethods.SetWinEventHook(
                evt,
                evt,
                IntPtr.Zero,
                _objectEventHandler,
                0,
                0,
                flags);

            if (hook != IntPtr.Zero)
            {
                _eventHooks.Add(hook);
            }
        }

        foreach (var evt in new[]
                 {
                     NativeMethods.EVENT_SYSTEM_FOREGROUND,
                     NativeMethods.EVENT_SYSTEM_MINIMIZESTART,
                     NativeMethods.EVENT_SYSTEM_MINIMIZEEND
                 })
        {
            var hook = NativeMethods.SetWinEventHook(
                evt,
                evt,
                IntPtr.Zero,
                _systemEventHandler,
                0,
                0,
                flags);

            if (hook != IntPtr.Zero)
            {
                _eventHooks.Add(hook);
            }
        }
    }

    private void UninstallHooks()
    {
        foreach (var hook in _eventHooks)
        {
            if (hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(hook);
            }
        }

        _eventHooks.Clear();
    }

    private void SyncControllers()
    {
        var candidates = WindowUtilities.EnumerateCandidateWindows(_settings);
        var candidateSet = new HashSet<IntPtr>();

        foreach (var hwnd in candidates)
        {
            if (hwnd == IntPtr.Zero)
            {
                continue;
            }

            candidateSet.Add(hwnd);

            if (!_controllers.ContainsKey(hwnd))
            {
                AttachWindow(hwnd);
            }
        }

        foreach (var hwnd in _controllers.Keys.ToList())
        {
            if (!candidateSet.Contains(hwnd))
            {
                DetachWindow(hwnd);
            }
        }
    }

    private void PollForegroundWindow()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        hwnd = WindowUtilities.NormalizeUpdateHwnd(hwnd);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // When foreground changes, immediately re-evaluate visibility for all controllers
        // to ensure background overlays hide and the new foreground overlay shows without delay.
        foreach (var c in _controllers.Values)
        {
            c.ApplyVisibility(true);
        }

        if (_controllers.TryGetValue(hwnd, out var controller))
        {
            controller.RequestPositionUpdate();
        }
    }

    private void PollAllControllerPositions()
    {
        foreach (var controller in _controllers.Values.ToList())
        {
            controller.UpdatePosition();
            controller.ApplyVisibility(true);
        }
    }
}
