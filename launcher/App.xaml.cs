using System.Windows;

namespace AlienShooterLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();

        // "--snapshot file.png" renders the launcher off-screen and exits.
        if (e.Args.Length == 2 && e.Args[0] == "--snapshot")
        {
            window.RenderSnapshot(e.Args[1]);
            Shutdown();
            return;
        }
        window.Show();
    }
}
