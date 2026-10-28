using System.Windows;
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

        var application = new App();
        application.InitializeComponent();
        application.Run();
    }
}
