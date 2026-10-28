using System.Windows;

namespace XtremePlays.Setup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(argument => string.Equals(argument, "--elevated-install-components", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var driverRequested = e.Args.Any(argument => string.Equals(argument, "--install-driver", StringComparison.OrdinalIgnoreCase));
                var driverReady = !driverRequested;
                if (driverRequested)
                {
                    try
                    {
                        driverReady = InstallerOperations.InstallDriverIfNeeded() is null;
                    }
                    catch
                    {
                        // The app can still be installed; setup will explain that audio sharing needs driver attention.
                        driverReady = false;
                    }
                }

                InstallerOperations.RemoveLegacyMachineInstall();
                Shutdown(driverReady ? 0 : 10);
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, "X-TREME PLAY'S setup", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }

            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
