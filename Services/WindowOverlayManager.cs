using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using WindowPinTray.Interop;
using WindowPinTray.Models;

namespace WindowPinTray.Services;

internal sealed class WindowOverlayManager : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly Dictionary<IntPtr, WindowPinController> _controllers = new();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _pollTimer;
    private readonly NativeMethods.WinEventDelegate _objectEventHandler;
    private readonly NativeMethods.WinEventDelegate _systemEventHandler;
    private AppSettings _settings;

    private IntPtr _objectHook = IntPtr.Zero;
    private IntPtr _systemHook = IntPtr.Zero;
    private bool _isRunning;

    public WindowOverlayManager(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _objectEventHandler = HandleObjectEvent;
        _systemEventHandler = HandleSystemEvent;
        _settingsService.SettingsChanged += OnSettingsChanged;
        _settings = _settingsService.CurrentSettings;

        _pollTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(8),
            DispatcherPriority.Render,
            (_, _) => PollControllers(),
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

        _objectHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_CREATE,
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero,
            _objectEventHandler,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT
            | NativeMethods.WINEVENT_SKIPOWNPROCESS
            | NativeMethods.WINEVENT_SKIPOWNTHREAD);

        _systemHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero,
            _systemEventHandler,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT
            | NativeMethods.WINEVENT_SKIPOWNPROCESS
            | NativeMethods.WINEVENT_SKIPOWNTHREAD);

        EnsurePolling();
    }

    public void Dispose()
    {
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _isRunning = false;

        if (_objectHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_objectHook);
            _objectHook = IntPtr.Zero;
        }

        if (_systemHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_systemHook);
            _systemHook = IntPtr.Zero;
        }

        foreach (var controller in _controllers.Values.ToList())
        {
            controller.Dispose();
        }

        _controllers.Clear();
        _pollTimer.Stop();
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
        if (!_isRunning || hwnd == IntPtr.Zero || idObject != NativeMethods.OBJID_WINDOW)
        {
            return;
        }

        _ = _dispatcher.BeginInvoke(new Action(() => ProcessWindowEvent(eventType, hwnd)));
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

        if (idObject != NativeMethods.OBJID_WINDOW && idObject != 0)
        {
            return;
        }

        _ = _dispatcher.BeginInvoke(new Action(() => ProcessWindowEvent(eventType, hwnd)));
    }

    private void ProcessWindowEvent(uint eventType, IntPtr hwnd)
    {
        switch (eventType)
        {
            case NativeMethods.EVENT_OBJECT_CREATE:
            case NativeMethods.EVENT_OBJECT_SHOW:
                AttachWindow(hwnd);
                break;

            case NativeMethods.EVENT_OBJECT_DESTROY:
            case NativeMethods.EVENT_OBJECT_HIDE:
                DetachWindow(hwnd);
                break;

            case NativeMethods.EVENT_OBJECT_LOCATIONCHANGE:
                UpdateWindow(hwnd);
                break;

            case NativeMethods.EVENT_SYSTEM_FOREGROUND:
                AttachWindow(hwnd);
                break;

            case NativeMethods.EVENT_SYSTEM_MINIMIZESTART:
                HideWindow(hwnd);
                break;

            case NativeMethods.EVENT_SYSTEM_MINIMIZEEND:
                AttachWindow(hwnd);
                break;

            case NativeMethods.EVENT_SYSTEM_MOVESIZESTART:
            case NativeMethods.EVENT_SYSTEM_MOVESIZEEND:
                UpdateWindow(hwnd);
                break;
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
            existing.UpdateSettings(_settings);
            existing.UpdatePosition();
            return;
        }

        var controller = new WindowPinController(hwnd, _settings);
        _controllers[hwnd] = controller;
        EnsurePolling();
    }

    private void UpdateWindow(IntPtr hwnd)
    {
        if (_controllers.TryGetValue(hwnd, out var controller))
        {
            if (!WindowUtilities.IsCandidateWindow(hwnd, _settings))
            {
                if (NativeMethods.IsIconic(hwnd) || !NativeMethods.IsWindowVisible(hwnd))
                {
                    controller.Hide();
                    return;
                }

                DetachWindow(hwnd);
                return;
            }

            controller.UpdatePosition();
            return;
        }

        AttachWindow(hwnd);
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

        EnsurePolling();
    }

    private void EnsurePolling()
    {
        if (!_pollTimer.IsEnabled)
        {
            _pollTimer.Start();
        }
    }

    private void PollControllers()
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

        foreach (var kvp in _controllers.ToArray())
        {
            if (!candidateSet.Contains(kvp.Key))
            {
                DetachWindow(kvp.Key);
            }
            else
            {
                var controller = kvp.Value;
                controller.UpdatePosition();
                controller.RefreshPinnedState();

                // Show button if window is exposed (not 50%+ covered) OR if it's pinned
                var isExposed = WindowUtilities.IsWindowExposed(kvp.Key);
                var shouldShow = isExposed || controller.IsPinned;

                // Log visibility decision on first poll after startup
                if (!controller.HasLoggedInitialState)
                {
                    var reason = controller.IsPinned ? "window is pinned" :
                                 isExposed ? "window is exposed (not 50%+ covered)" :
                                 "window is NOT exposed (50%+ covered)";
                    DebugLogger.LogWindowInfo(kvp.Key, $"Initial visibility: {(shouldShow ? "SHOWING" : "HIDING")} - {reason}");
                    controller.HasLoggedInitialState = true;
                }

                controller.ApplyVisibility(shouldShow);
            }
        }

        // keep polling active to discover brand new windows
    }
}
