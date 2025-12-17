using WindowPinTray.Services;

namespace WindowPinTray;

public partial class App : System.Windows.Application
{
    private AppHost? _host;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        _host = new AppHost();
        _host.Start();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}
