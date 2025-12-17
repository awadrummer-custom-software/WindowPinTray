using System;
using WindowPinTray.UI;

namespace WindowPinTray.Services;

internal sealed class AppHost : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly TrayService _trayService;
    private readonly WindowOverlayManager _overlayManager;
    private readonly Lazy<SettingsWindow> _settingsWindow;

    public AppHost()
    {
        _settingsService = new SettingsService();
        _trayService = new TrayService();
        _overlayManager = new WindowOverlayManager(_settingsService);
        _settingsWindow = new Lazy<SettingsWindow>(() => new SettingsWindow(_settingsService));

        _trayService.SettingsRequested += HandleSettingsRequested;
    }

    public void Start()
    {
        _overlayManager.Start();
    }

    public void Dispose()
    {
        _trayService.SettingsRequested -= HandleSettingsRequested;
        _overlayManager.Dispose();
        _trayService.Dispose();
    }

    private void HandleSettingsRequested(object? sender, EventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            var window = _settingsWindow.Value;
            window.ShowFromTray();
        });
    }
}
