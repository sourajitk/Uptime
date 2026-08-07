using System;
using System.Windows;

namespace UptimeInstaller;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var engine = new InstallerEngine();
        bool isSilent = engine.ParseCommandLine(Environment.GetCommandLineArgs());

        if (isSilent)
        {
            int exitCode = engine.RunSilent();
            Environment.Exit(exitCode);
            return;
        }

        var mainWindow = new MainWindow(engine);
        mainWindow.Show();
    }
}
