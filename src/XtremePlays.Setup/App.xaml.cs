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
                InstallerOperations.InstallDriverIfNeeded();
                InstallerOperations.RemoveLegacyMachineInstall();
                Shutdown(0);
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
