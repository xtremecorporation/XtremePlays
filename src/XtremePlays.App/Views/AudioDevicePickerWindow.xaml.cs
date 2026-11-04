using System.Collections.ObjectModel;
using System.Windows;
using XtremePlays.App.Services;

namespace XtremePlays.App.Views;

public partial class AudioDevicePickerWindow : Window
{
    public ObservableCollection<AudioEndpointChoice> Microphones { get; } = [];
    public ObservableCollection<AudioEndpointChoice> Outputs { get; } = [];
    public IReadOnlyList<string> SelectedMicrophoneIds => Microphones.Where(item => item.IsSelected).Select(item => item.Id).ToArray();
    public IReadOnlyList<string> SelectedOutputIds => Outputs.Where(item => item.IsSelected).Select(item => item.Id).ToArray();

    public AudioDevicePickerWindow(
        IEnumerable<AudioEndpointInfo> microphones,
        IEnumerable<AudioEndpointInfo> outputs,
        IEnumerable<string> selectedMicrophoneIds,
        IEnumerable<string> selectedOutputIds)
    {
        InitializeComponent();
        var microphoneSelection = new HashSet<string>(selectedMicrophoneIds, StringComparer.Ordinal);
        var outputSelection = new HashSet<string>(selectedOutputIds, StringComparer.Ordinal);
        foreach (var endpoint in microphones)
            Microphones.Add(new AudioEndpointChoice(endpoint.Id, endpoint.Name, microphoneSelection.Contains(endpoint.Id)));
        foreach (var endpoint in outputs)
            Outputs.Add(new AudioEndpointChoice(endpoint.Id, endpoint.Name, outputSelection.Contains(endpoint.Id)));
        DataContext = this;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMicrophoneIds.Count == 0)
        {
            SelectionStatus.Text = "Select at least one active microphone.";
            return;
        }

        DialogResult = true;
        Close();
    }
}

public sealed class AudioEndpointChoice(string id, string name, bool isSelected)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public bool IsSelected { get; set; } = isSelected;
}
