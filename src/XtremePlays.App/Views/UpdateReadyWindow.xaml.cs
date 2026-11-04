using System.Windows;

namespace XtremePlays.App.Views;

public partial class UpdateReadyWindow : Window
{
    public UpdateReadyWindow(string version)
    {
        InitializeComponent();
        VersionText.Text = $"Version {version} is ready";
    }

    private void InstallNow_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void InstallLater_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
