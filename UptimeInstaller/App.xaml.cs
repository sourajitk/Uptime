using System.Windows;

namespace UptimeInstaller;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var engine = new InstallerEngine();
        if (engine.ParseCommandLine(e.Args))
        {
            Environment.Exit(engine.RunSilent());
            return;
        }

        new MainWindow(engine).Show();
    }
}
