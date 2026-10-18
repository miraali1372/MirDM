using System;
using System.Windows;
using MirDM.Views;

namespace MirDM;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            // Prevent unhandled background crash
        };

        DispatcherUnhandledException += (s, args) =>
        {
            args.Handled = true;
        };

        var mainWindow = new MainWindow();
        mainWindow.Show();
    }
}
