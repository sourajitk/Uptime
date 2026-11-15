using System.Windows;

namespace UptimeInstaller;

public partial class App : Application
{
    private InstallerEngine? _engine;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _engine = new InstallerEngine();
        if (_engine.ParseCommandLine(e.Args))
        {
            int exitCode = _engine.RunSilent();
            _engine.RunPendingSelfDelete();
            Environment.Exit(exitCode);
            return;
        }

        new MainWindow(_engine).Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _engine?.RunPendingSelfDelete();
        base.OnExit(e);
    }
}
