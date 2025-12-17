using System;
using System.Drawing;
using System.Windows.Forms;

namespace WindowPinTray.Services;

internal sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripMenuItem _settingsMenuItem;
    private readonly ToolStripMenuItem _exitMenuItem;
    private readonly EventHandler _doubleClickHandler;

    public TrayService()
    {
        _contextMenu = new ContextMenuStrip();
        _settingsMenuItem = new ToolStripMenuItem("Settings...", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
        _exitMenuItem = new ToolStripMenuItem("Exit", null, (_, _) => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            System.Windows.Application.Current?.Shutdown();
        }));

        _contextMenu.Items.AddRange(new ToolStripItem[]
        {
            _settingsMenuItem,
            new ToolStripSeparator(),
            _exitMenuItem
        });

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Window Pin Manager",
            Visible = true,
            ContextMenuStrip = _contextMenu
        };

        _doubleClickHandler = (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _notifyIcon.DoubleClick += _doubleClickHandler;
    }

    public event EventHandler? SettingsRequested;

    public void Dispose()
    {
        _notifyIcon.DoubleClick -= _doubleClickHandler;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
    }
}
