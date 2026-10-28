using System.Windows;
using XtremePlays.App.Services;
using Velopack;

namespace XtremePlays.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build()
            .SetAutoApplyOnStartup(true)
            .Run();

        using var singleInstance = SingleInstanceCoordinator.TryAcquireOrActivate();
        if (singleInstance is null)
        {
            return;
        }

        var application = new App();
        application.InitializeComponent();
        singleInstance.StartListening(application.Dispatcher, () => application.MainWindow);
        application.Run();
    }
}
