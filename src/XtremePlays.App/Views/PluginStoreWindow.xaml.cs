using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Web.WebView2.Core;
using XtremePlays.App.Services;

namespace XtremePlays.App.Views;

public partial class PluginStoreWindow : Window
{
    private readonly CoreWebView2Profile? _profile;
    private readonly Func<Task>? _beforeNativePluginRemoval;
    private readonly Action? _onPluginStateChanged;
    private PluginCatalog? _catalog;
    private string _selectedCategory = "All";

    public ObservableCollection<PluginCardViewModel> PluginCards { get; } = [];

    public PluginStoreWindow(
        CoreWebView2Profile? profile,
        Func<Task>? beforeNativePluginRemoval = null,
        Action? onPluginStateChanged = null)
    {
        InitializeComponent();
        _profile = profile;
        _beforeNativePluginRemoval = beforeNativePluginRemoval;
        _onPluginStateChanged = onPluginStateChanged;
        DataContext = this;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadCatalogAsync();

    private async Task LoadCatalogAsync()
    {
        PluginStatusText.Text = "Loading the X-TREME GitHub catalog…";
        PluginStatusText.Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush");
        PluginList.Visibility = Visibility.Visible;
        EmptyStatePanel.Visibility = Visibility.Collapsed;

        try
        {
            _catalog = await PluginCatalogService.LoadAsync();
            await RebuildCardsAsync();
            PluginStatusText.Text = $"{_catalog.Plugins.Count} plugins are listed in the X-TREME catalog. Packages are downloaded only when you install them.";
        }
        catch (Exception exception)
        {
            PluginCards.Clear();
            EmptyStateTitle.Text = "Plugin catalog unavailable";
            EmptyStateMessage.Text = "The catalog could not be loaded. Check your internet connection and try again.";
            EmptyStatePanel.Visibility = Visibility.Visible;
            PluginList.Visibility = Visibility.Collapsed;
            PluginStatusText.Text = exception.Message;
            PluginStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentDangerBrush");
        }
    }

    private async Task RebuildCardsAsync()
    {
        PluginCards.Clear();
        if (_catalog is null) return;
        IReadOnlyDictionary<string, InstalledPluginRecord> installed = PluginPackageInstaller.GetInstalledPlugins();
        IReadOnlyList<CoreWebView2BrowserExtension> browserExtensions = [];
        if (_profile is not null)
        {
            try { browserExtensions = await _profile.GetBrowserExtensionsAsync(); }
            catch { /* The store remains usable for catalog browsing if the profile cannot be read. */ }
        }

        var cards = new List<PluginCardViewModel>();
        foreach (var entry in _catalog.Plugins)
        {
            installed.TryGetValue(entry.Id, out var installedRecord);
            var extension = entry.Type == "browser-extension" && !string.IsNullOrWhiteSpace(entry.ExtensionId)
                ? browserExtensions.FirstOrDefault(item => string.Equals(item.Id, entry.ExtensionId, StringComparison.Ordinal))
                : null;
            cards.Add(new PluginCardViewModel(entry, installedRecord, extension));
        }
        foreach (var card in cards.OrderByDescending(item => item.IsPinned).ThenBy(item => item.Entry.Name, StringComparer.CurrentCultureIgnoreCase))
            PluginCards.Add(card);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var search = SearchBox.Text.Trim();
        var matching = PluginCards.Where(card =>
            (_selectedCategory == "All" || string.Equals(card.Entry.Category, _selectedCategory, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(search) ||
             card.Entry.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
             card.Entry.Description.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
             card.Entry.Publisher.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
             card.Entry.Capabilities.Any(capability => capability.Contains(search, StringComparison.CurrentCultureIgnoreCase)))).ToArray();

        PluginList.ItemsSource = matching;
        var showEmpty = matching.Length == 0;
        PluginList.Visibility = showEmpty ? Visibility.Collapsed : Visibility.Visible;
        EmptyStatePanel.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
        if (showEmpty)
        {
            EmptyStateTitle.Text = string.IsNullOrWhiteSpace(search) ? "No plugins in this category" : "No matching plugins";
            EmptyStateMessage.Text = string.IsNullOrWhiteSpace(search)
                ? "New plugins published to the X-TREME catalog will appear here."
                : "Try another name or choose a different category.";
        }
    }

    private void Category_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string category }) return;
        _selectedCategory = category;
        if (sender is Button selected)
        {
            foreach (var button in FindCategoryButtons())
                button.Background = ReferenceEquals(button, selected)
                    ? (System.Windows.Media.Brush)FindResource("NavigationActiveBrush")
                    : (System.Windows.Media.Brush)FindResource("SecondarySurfaceBrush");
        }
        ApplyFilter();
    }

    private IEnumerable<Button> FindCategoryButtons()
    {
        if (Content is not DependencyObject root) yield break;
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.TryPop(out var current))
        {
            if (current is Button { Tag: string } button) yield return button;
            for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(current); index++)
                stack.Push(System.Windows.Media.VisualTreeHelper.GetChild(current, index));
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async void RefreshCatalog_Click(object sender, RoutedEventArgs e) => await LoadCatalogAsync();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void PrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PluginCardViewModel card) return;
        if (card.Busy) return;

        try
        {
            card.Busy = true;
            if (card.IsInstalled && !card.IsUpdateAvailable)
            {
                var prompt = $"Remove {card.Entry.Name} from X-TREME PLAY'S?";
                if (MessageBox.Show(this, prompt, "Remove plugin", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
                if (card.Entry.Type == "native-adblock" && _beforeNativePluginRemoval is not null)
                    await _beforeNativePluginRemoval();
                await PluginPackageInstaller.RemoveAsync(card.Entry, _profile);
                PluginStatusText.Text = $"{card.Entry.Name} was removed.";
            }
            else
            {
                if (card.Entry.Type == "browser-extension" && _profile is null)
                    throw new InvalidOperationException("The WebView2 browser is not ready. Try again in a moment.");

                if (card.Entry.Type == "native-adblock" &&
                    MessageBox.Show(this,
                        $"Install {card.Entry.Name} from the X-TREME GitHub release? The app will verify the package checksum before installing it.\n\nIt will inspect new browser requests using the filter lists enabled in Settings.",
                        "Install ad-block plugin", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
                    return;

                if (card.Entry.Type == "native-adblock" && _beforeNativePluginRemoval is not null)
                    await _beforeNativePluginRemoval();

                PluginStatusText.Text = $"Preparing {card.Entry.Name}…";
                DownloadProgressHub.Report($"Preparing {card.Entry.Name}…", "Checking the package and verifying its source.");
                var progress = new Progress<string>(message =>
                {
                    PluginStatusText.Text = message;
                    DownloadProgressHub.Report($"Plugin · {card.Entry.Name}", message);
                });
                var downloadProgress = new Progress<int>(percent =>
                    DownloadProgressHub.Report($"Downloading {card.Entry.Name}…", "The package is being checked before it is installed.", percent));
                await PluginPackageInstaller.InstallAsync(card.Entry, _profile, ConfirmExtensionAccess, progress, downloadProgress);
                PluginStatusText.Text = $"{card.Entry.Name} is installed. You can manage it from this Plugins window.";
            }

            _onPluginStateChanged?.Invoke();
            await RebuildCardsAsync();
        }
        catch (OperationCanceledException)
        {
            PluginStatusText.Text = "Installation cancelled.";
        }
        catch (Exception exception)
        {
            PluginStatusText.Text = exception.Message;
            PluginStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentDangerBrush");
        }
        finally
        {
            DownloadProgressHub.Clear();
            card.Busy = false;
        }
    }

    private bool ConfirmExtensionAccess(ExtensionManifestInfo manifest)
    {
        var access = manifest.RequestedAccess.Count == 0
            ? "No site permissions were declared."
            : string.Join(Environment.NewLine, manifest.RequestedAccess.Take(18)) +
              (manifest.RequestedAccess.Count > 18 ? $"{Environment.NewLine}… and {manifest.RequestedAccess.Count - 18} more" : string.Empty);
        var prompt = $"Install {manifest.Name} in this app?\n\nRequested access:\n{access}\n\nBrowser extensions can read or change page data covered by these permissions. This package is from the X-TREME GitHub catalog. Continue only if you trust its access.";
        return MessageBox.Show(this, prompt, "Review extension permissions", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private async void ToggleExtension_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PluginCardViewModel card || _profile is null || card.Busy) return;
        try
        {
            var extensions = await _profile.GetBrowserExtensionsAsync();
            var extension = extensions.FirstOrDefault(item => string.Equals(item.Id, card.Entry.ExtensionId, StringComparison.Ordinal));
            if (extension is null) throw new InvalidOperationException("This extension is not currently installed in the browser profile.");
            var enable = !extension.IsEnabled;
            await extension.EnableAsync(enable);
            await RebuildCardsAsync();
            PluginStatusText.Text = $"{card.Entry.Name} is now {(enable ? "enabled" : "disabled")}.";
        }
        catch (Exception exception)
        {
            PluginStatusText.Text = exception.Message;
            PluginStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentDangerBrush");
        }
    }

    private async void PinExtension_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PluginCardViewModel card || string.IsNullOrWhiteSpace(card.Entry.ExtensionId)) return;
        try
        {
            var pinned = BrowserExtensionPreferences.TogglePinned(card.Entry.ExtensionId);
            await RebuildCardsAsync();
            PluginStatusText.Text = pinned
                ? $"{card.Entry.Name} is pinned at the top of Plugins."
                : $"{card.Entry.Name} was unpinned.";
        }
        catch (Exception exception)
        {
            PluginStatusText.Text = exception.Message;
            PluginStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentDangerBrush");
        }
    }

    private void OpenSource_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PluginCardViewModel card ||
            !Uri.TryCreate(card.Entry.SourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception exception)
        {
            PluginStatusText.Text = $"Could not open the source page: {exception.Message}";
            PluginStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentDangerBrush");
        }
    }
}

public sealed class PluginCardViewModel : INotifyPropertyChanged
{
    private readonly InstalledPluginRecord? _installed;
    private readonly CoreWebView2BrowserExtension? _extension;
    private bool _busy;

    public PluginCatalogEntry Entry { get; }
    public bool IsInstalled => _installed is not null || _extension is not null;
    public bool IsUpdateAvailable => _installed is not null && !string.Equals(_installed.Version, Entry.Version, StringComparison.Ordinal);
    public string Icon => Entry.Type == "browser-extension" ? "🧩" : Entry.Category == PluginCatalogService.CategoryAdBlockers ? "🛡" : "✦";
    public string CategoryLabel => Entry.Category;
    public string VersionLabel => $"Version {Entry.Version}";
    public string PrimaryActionText => Busy ? "Working…" : IsInstalled && !IsUpdateAvailable ? "Remove" : IsUpdateAvailable ? "Update" : "Install";
    public bool CanPrimaryAction => !Busy;
    public string ToggleActionText => _extension?.IsEnabled == true ? "Disable" : "Enable";
    public Visibility ToggleVisibility => IsInstalled && Entry.Type == "browser-extension" && _extension is not null ? Visibility.Visible : Visibility.Collapsed;
    public bool IsPinned => !string.IsNullOrWhiteSpace(Entry.ExtensionId) && BrowserExtensionPreferences.IsPinned(Entry.ExtensionId);
    public string PinActionText => IsPinned ? "Unpin" : "Pin";
    public Visibility PinVisibility => IsInstalled && Entry.Type == "browser-extension" && _extension is not null ? Visibility.Visible : Visibility.Collapsed;
    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PrimaryActionText));
            OnPropertyChanged(nameof(CanPrimaryAction));
        }
    }

    public PluginCardViewModel(PluginCatalogEntry entry, InstalledPluginRecord? installed, CoreWebView2BrowserExtension? extension)
    {
        Entry = entry;
        _installed = installed;
        _extension = extension;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
