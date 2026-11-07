using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace AlienShooterLauncher;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        // "--update-now": check and install the latest release without a window, write the
        // result to "<launcher>.update.log" and exit. Used to test the updater.
        if (args.Contains("--update-now"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string log = Updater.ExePath + ".update.log";
            try
            {
                var update = await Updater.CheckAsync();
                if (update == null)
                {
                    File.WriteAllText(log, $"sin novedades ({Updater.Current})");
                }
                else
                {
                    await Updater.ApplyAsync(update, new Progress<string>());
                    File.WriteAllText(log, $"actualizado {Updater.Current} -> {update.Version}");
                }
            }
            catch (Exception ex)
            {
                File.WriteAllText(log, "error: " + ex.Message);
            }
            Shutdown();
            return;
        }

        // "--snapshot file.png [--fake-update X.Y.Z]" renders the launcher off-screen and exits.
        int snap = Array.IndexOf(args, "--snapshot");
        if (snap >= 0 && snap + 1 < args.Length)
        {
            var window = new MainWindow(checkUpdates: false);
            int fake = Array.IndexOf(args, "--fake-update");
            if (fake >= 0 && fake + 1 < args.Length)
                window.ShowUpdate(new UpdateInfo(new Version(args[fake + 1]), "", "", ""));
            window.RenderSnapshot(args[snap + 1]);
            Shutdown();
            return;
        }

        var main = new MainWindow(checkUpdates: true, justUpdated: args.Contains("--updated"));
        main.Show();
    }
}
