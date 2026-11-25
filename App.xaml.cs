using System.Windows;
using System.Windows.Threading;

namespace BackupVerify;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        var window = new MainWindow();
        MainWindow = window;
        if (args.Args is ["--screenshots", var directory])
        {
            window.ShowActivated = false;
            window.ContentRendered += (_, _) => window.Dispatcher.InvokeAsync(() =>
            {
                window.CapturePreviews(directory);
                Shutdown();
            }, DispatcherPriority.ApplicationIdle);
        }
        window.Show();
    }
}
