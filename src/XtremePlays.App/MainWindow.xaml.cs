using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace XtremePlays.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void NavigationItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string destination })
        {
            return;
        }

        PageTitle.Text = destination;
        PageDescription.Text = destination switch
        {
            "Home" => "The native X-TREME PLAY'S shell is ready. Connect your favorite music services in the next phase.",
            "Audio Sharing" => "Audio routing is intentionally unavailable until a compatible device and a real routing path have been tested.",
            "Settings" => "Local preferences, privacy controls, and visual theme options will be introduced in a later phase.",
            "About" => "X-TREME PLAY'S is a lightweight Windows music companion by X-TREME CORPORATION.",
            _ => $"{destination} will be connected through the embedded music browser in the next phase."
        };
        StatusText.Text = $"Phase 1 • {destination} selected";
    }
}
