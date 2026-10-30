using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using XtremePlays.App.Models;
using XtremePlays.App.Services;
using XtremePlays.App.Views;

namespace XtremePlays.App.Views;

public partial class ShellWindow : Window
{
    private const string RuntimeDownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";
    private const string BookmarkBackupFormat = "XtremePlays.Bookmarks";
    private const int WindowMessageHotKey = 0x0312;
    private const int VoiceMuteHotkeyId = 0x584D;
    private const int MusicMuteHotkeyId = 0x5855;
    private const uint HotkeyModifierAlt = 0x0001;
    private const uint HotkeyModifierControl = 0x0002;
    private const uint HotkeyModifierShift = 0x0004;
    private const uint HotkeyModifierWindows = 0x0008;
    private const uint HotkeyModifierNoRepeat = 0x4000;
    private readonly SettingsStore _settingsStore = new();
    private readonly UpdateService _updateService = new();
    private readonly AudioMixingRouter _audioMixingRouter = new();
    private readonly DispatcherTimer _audioMeterTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly ObservableCollection<Bookmark> _bookmarks = [];
    private AppPreferences _preferences = new();
    private bool _settingsLoaded;
    private bool _browserAvailable;
    private bool _isFinishingClose;
    private bool _compactMode;
    private bool _topmostBeforeCompact;
    private bool _voiceMuted;
    private bool _musicMuted;
    private bool _voiceHotkeyRegistered;
    private bool _musicHotkeyRegistered;
    private string? _hotkeyCaptureTarget;
    private HwndSource? _windowSource;
    private double _normalWidth;
    private double _normalHeight;

    public ShellWindow()
    {
        InitializeComponent();
        AboutVersionText.Text = $"Version {typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.3.0"} · X-TREME CORPORATION";
        BookmarkList.ItemsSource = _bookmarks;
        QuickBookmarkSelector.ItemsSource = _bookmarks;
        _audioMeterTimer.Tick += AudioMeterTimer_Tick;
        PreviewKeyDown += ShellWindow_PreviewKeyDown;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AppPaths.Initialize(Environment.GetCommandLineArgs());
        var (preferences, warning) = await _settingsStore.LoadAsync();
        _preferences = preferences;
        _bookmarks.Clear();
        foreach (var bookmark in _preferences.Bookmarks)
        {
            _bookmarks.Add(bookmark);
        }

        _normalWidth = _preferences.WindowWidth;
        _normalHeight = _preferences.WindowHeight;
        Width = _normalWidth;
        Height = _normalHeight;
        if (IsValidRestorePosition(_preferences.WindowLeft, _preferences.WindowTop))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _preferences.WindowLeft!.Value;
            Top = _preferences.WindowTop!.Value;
        }

        DarkThemeCheckBox.IsChecked = _preferences.DarkTheme;
        AlwaysOnTopCheckBox.IsChecked = _preferences.AlwaysOnTop;
        VoiceGainSlider.Value = _preferences.VoiceVolumePercent;
        MusicGainSlider.Value = _preferences.MusicVolumePercent;
        MasterGainSlider.Value = _preferences.MasterVolumePercent;
        UpdateMixerGainLabels();
        Topmost = _preferences.AlwaysOnTop;
        DataLocationText.Text = AppPaths.IsPortable
            ? $"Portable data folder: {AppPaths.DataDirectory}"
            : $"Private local data folder: {AppPaths.DataDirectory}";
        _settingsLoaded = true;
        ApplyTheme(_preferences.DarkTheme);
        StartUpdateCheckIfDue();
        InitializeMuteHotkeys();
        UpdateMuteButtons();
        if (warning is not null)
        {
            SetStatus(warning, isError: true);
        }

        await InitializeBrowserAsync();
    }

    private void StartUpdateCheckIfDue()
    {
        var now = DateTimeOffset.UtcNow;
        if (_preferences.LastUpdateCheckUtc is { } lastCheck && now - lastCheck < TimeSpan.FromHours(24))
        {
            return;
        }

        _preferences.LastUpdateCheckUtc = now;
        _ = _settingsStore.SaveAsync(_preferences);
        _ = CheckAndDownloadUpdateAsync();
    }

    private async Task CheckAndDownloadUpdateAsync()
    {
        try
        {
            var availableVersion = await _updateService.CheckAndDownloadAsync();
            if (availableVersion is null || _isFinishingClose) return;

            SetStatus($"Version {availableVersion} is ready and will install automatically the next time you open X-TREME PLAY'S.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Update checks are best-effort; a GitHub or network issue must not interrupt music playback.
        }
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            var environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.BrowserUserDataDirectory);
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Browser.CoreWebView2.NavigationStarting += Browser_NavigationStarting;
            Browser.CoreWebView2.NavigationCompleted += Browser_NavigationCompleted;
            Browser.CoreWebView2.SourceChanged += Browser_SourceChanged;
            Browser.CoreWebView2.NewWindowRequested += Browser_NewWindowRequested;
            Browser.CoreWebView2.ProcessFailed += Browser_ProcessFailed;
            _browserAvailable = true;
            BrowserUnavailablePanel.Visibility = Visibility.Collapsed;
            Browser.Visibility = Visibility.Visible;
            AppStatusBadge.Text = "Browser ready";
            SetStatus("Ready. Choose a music service to begin.");
            UpdateNavigationButtons();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowBrowserUnavailable("Microsoft Edge WebView2 Runtime was not found. Install the free Evergreen Runtime, then reopen X-TREME PLAY'S.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or COMException)
        {
            ShowBrowserUnavailable($"The embedded browser could not start ({exception.Message}). Check that this folder is writable and WebView2 Runtime is installed.");
        }
        catch (Exception exception)
        {
            ShowBrowserUnavailable($"The embedded browser could not start. {exception.Message}");
        }
    }

    private void ShowBrowserUnavailable(string message)
    {
        _browserAvailable = false;
        BrowserErrorText.Text = message;
        Browser.Visibility = Visibility.Collapsed;
        BrowserUnavailablePanel.Visibility = Visibility.Visible;
        AppStatusBadge.Text = "Browser unavailable";
        SetStatus("Install WebView2 Runtime to browse music services.", isError: true);
    }

    private void NavigationItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string destination }) return;

        switch (destination)
        {
            case "Home": ShowPanel(HomePanel); SetStatus("Home"); break;
            case "YouTube": NavigateToService("YouTube", "https://www.youtube.com/"); break;
            case "YouTube Music": NavigateToService("YouTube Music", "https://music.youtube.com/"); break;
            case "Spotify": NavigateToService("Spotify", "https://open.spotify.com/"); break;
            case "Bookmarks": ShowPanel(BookmarksPanel); SetStatus("Bookmarks"); break;
            case "Audio Sharing":
                ShowPanel(AudioPanel);
                _ = RefreshAudioDevicesAsync();
                break;
            case "Settings": ShowPanel(SettingsPanel); SetStatus("Settings"); break;
            case "About": ShowPanel(AboutPanel); SetStatus("About X-TREME PLAY'S"); break;
        }
    }

    private void ShowPanel(UIElement panel)
    {
        if (_compactMode) ExitCompactMode();
        HomePanel.Visibility = Visibility.Collapsed;
        BrowserPanel.Visibility = Visibility.Collapsed;
        BookmarksPanel.Visibility = Visibility.Collapsed;
        AudioPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        AboutPanel.Visibility = Visibility.Collapsed;
        panel.Visibility = Visibility.Visible;
    }

    private void NavigateToService(string serviceName, string homeUrl)
    {
        if (_browserAvailable && Browser.Source is Uri currentUrl &&
            string.Equals(GetMusicService(currentUrl), serviceName, StringComparison.Ordinal))
        {
            ShowPanel(BrowserPanel);
            AddressBox.Text = currentUrl.AbsoluteUri;
            Browser.Focus();
            SetStatus($"Back to {serviceName}. The current page is still open.");
            return;
        }

        NavigateTo(homeUrl);
    }

    private static string? GetMusicService(Uri uri)
    {
        var host = uri.Host;
        if (host.Equals("music.youtube.com", StringComparison.OrdinalIgnoreCase)) return "YouTube Music";
        if (host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)) return "YouTube";
        if (host.Equals("spotify.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".spotify.com", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        return null;
    }

    private void NavigateTo(string? url)
    {
        if (_compactMode) ExitCompactMode();
        HomePanel.Visibility = Visibility.Collapsed;
        BookmarksPanel.Visibility = Visibility.Collapsed;
        AudioPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        AboutPanel.Visibility = Visibility.Collapsed;
        BrowserPanel.Visibility = Visibility.Visible;

        var normalizedUrl = SettingsStore.NormalizeWebUrl(url);
        if (normalizedUrl is null)
        {
            SetStatus("That website address is invalid. Use an http or https address.", isError: true);
            return;
        }

        AddressBox.Text = normalizedUrl;
        if (!_browserAvailable)
        {
            SetStatus("WebView2 Runtime is needed before a page can open.", isError: true);
            return;
        }

        Browser.CoreWebView2.Navigate(normalizedUrl);
        Browser.Focus();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_browserAvailable && Browser.CoreWebView2.CanGoBack) Browser.CoreWebView2.GoBack();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_browserAvailable && Browser.CoreWebView2.CanGoForward) Browser.CoreWebView2.GoForward();
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_browserAvailable) Browser.CoreWebView2.Reload();
    }

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel(HomePanel);
        SetStatus("Home");
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateFromAddress();
            e.Handled = true;
        }
    }

    private void NavigateAddress_Click(object sender, RoutedEventArgs e) => NavigateFromAddress();

    private void NavigateFromAddress()
    {
        var value = AddressBox.Text.Trim();
        if (!value.Contains("://", StringComparison.Ordinal) && !value.Contains(' ')) value = "https://" + value;
        var url = SettingsStore.NormalizeWebUrl(value);
        if (url is null && value.Length > 0)
        {
            url = "https://www.google.com/search?q=" + Uri.EscapeDataString(value);
        }

        NavigateTo(url);
    }

    private void Browser_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        BrowserLoadingOverlay.Visibility = Visibility.Visible;
        SetStatus("Loading page…");
        UpdateNavigationButtons();
    }

    private async void Browser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        BrowserLoadingOverlay.Visibility = Visibility.Collapsed;
        UpdateNavigationButtons();
        if (e.IsSuccess)
        {
            var currentUrl = Browser.Source?.AbsoluteUri;
            if (currentUrl is not null)
            {
                AddressBox.Text = currentUrl;
                _preferences.LastUrl = currentUrl;
                await SavePreferencesSafelyAsync();
            }

            var host = Browser.Source?.Host ?? "Website";
            SetStatus($"Opened {host}. Playback is controlled by the website.");
        }
        else
        {
            SetStatus($"Page could not finish loading ({e.WebErrorStatus}). Check the connection, then retry.", isError: true);
        }
    }

    private void Browser_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (Browser.Source is not null) AddressBox.Text = Browser.Source.AbsoluteUri;
        UpdateNavigationButtons();
    }

    private void Browser_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (SettingsStore.NormalizeWebUrl(e.Uri) is not null)
        {
            NavigateTo(e.Uri);
        }
        else
        {
            SetStatus("This page requested a link that is not a regular website address.", isError: true);
        }
    }

    private void Browser_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        BrowserLoadingOverlay.Visibility = Visibility.Collapsed;
        SetStatus("The browser page process stopped unexpectedly. Reload the page to recover.", isError: true);
    }

    private void UpdateNavigationButtons()
    {
        if (!_browserAvailable) return;
        BackButton.IsEnabled = Browser.CoreWebView2.CanGoBack;
        ForwardButton.IsEnabled = Browser.CoreWebView2.CanGoForward;
    }

    private void OpenSelectedBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (QuickBookmarkSelector.SelectedItem is Bookmark bookmark) NavigateTo(bookmark.Url);
    }

    private void AddCurrentBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (!_browserAvailable || Browser.Source is null)
        {
            SetStatus("Open a page before saving a bookmark.", isError: true);
            return;
        }

        var title = Browser.CoreWebView2.DocumentTitle;
        if (string.IsNullOrWhiteSpace(title)) title = Browser.Source.Host;
        AddBookmark(new Bookmark { Title = SettingsStore.SanitizeTitle(title), Url = Browser.Source.AbsoluteUri });
    }

    private void AddBookmark_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new BookmarkDialog { Owner = this };
        if (dialog.ShowDialog() == true) AddBookmark(dialog.Result);
    }

    private void AddBookmark(Bookmark bookmark)
    {
        if (_bookmarks.Count >= 100)
        {
            SetStatus("The bookmark limit is 100.", isError: true);
            return;
        }

        if (_bookmarks.Any(existing => string.Equals(existing.Url, bookmark.Url, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("That page is already bookmarked.");
            return;
        }

        _bookmarks.Add(bookmark);
        BookmarkList.SelectedItem = bookmark;
        QuickBookmarkSelector.SelectedItem = bookmark;
        PersistBookmarks();
        SetStatus($"Saved bookmark: {bookmark.Title}");
    }

    private void EditBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (BookmarkList.SelectedItem is not Bookmark selected) return;
        var index = _bookmarks.IndexOf(selected);
        var dialog = new BookmarkDialog(selected) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        if (_bookmarks.Where((_, i) => i != index).Any(item => string.Equals(item.Url, dialog.Result.Url, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("A bookmark with that address already exists.", isError: true);
            return;
        }

        _bookmarks[index] = dialog.Result;
        BookmarkList.SelectedItem = dialog.Result;
        PersistBookmarks();
        SetStatus("Bookmark updated.");
    }

    private void RemoveBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (BookmarkList.SelectedItem is not Bookmark selected) return;
        if (MessageBox.Show(this, $"Remove the bookmark ‘{selected.Title}’?", "Remove bookmark", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _bookmarks.Remove(selected);
        PersistBookmarks();
        SetStatus("Bookmark removed.");
    }

    private void ExportBookmarks_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Back up bookmarks",
            FileName = $"XtremePlays-Bookmarks-{DateTime.Now:yyyyMMdd}.json",
            DefaultExt = ".json",
            AddExtension = true,
            Filter = "X-TREME PLAY'S bookmark backup (*.json)|*.json|All files (*.*)|*.*",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var backup = new BookmarkBackup
            {
                Bookmarks = _bookmarks.Select<Bookmark, Bookmark?>(bookmark => new Bookmark { Title = bookmark.Title, Url = bookmark.Url }).ToList()
            };
            var json = JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dialog.FileName, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            SetStatus($"Exported {_bookmarks.Count} bookmark(s) to JSON.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            SetStatus($"The bookmark backup could not be saved: {exception.Message}", isError: true);
        }
    }

    private async void RestoreBookmarks_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Restore bookmarks",
            CheckFileExists = true,
            Filter = "X-TREME PLAY'S bookmark backup (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var fileInfo = new FileInfo(dialog.FileName);
            if (fileInfo.Length > 1_048_576) throw new InvalidDataException("The selected JSON file is larger than the 1 MB backup limit.");
            var json = await File.ReadAllTextAsync(dialog.FileName);
            var backup = JsonSerializer.Deserialize<BookmarkBackup>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (backup is null || !string.Equals(backup.Format, BookmarkBackupFormat, StringComparison.Ordinal) || backup.Version != 1 || backup.Bookmarks is null)
                throw new InvalidDataException("This file is not a supported X-TREME PLAY'S bookmark backup.");
            if (backup.Bookmarks.Count > 100) throw new InvalidDataException("The backup contains more than 100 bookmarks. Remove some entries and try again.");

            var restoredBookmarks = new List<Bookmark>(backup.Bookmarks.Count);
            var uniqueUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < backup.Bookmarks.Count; index++)
            {
                var entry = backup.Bookmarks[index] ?? throw new InvalidDataException($"Bookmark {index + 1} is empty or invalid.");
                var title = SettingsStore.SanitizeTitle(entry.Title);
                var url = SettingsStore.NormalizeWebUrl(entry.Url);
                if (title.Length == 0 || url is null) throw new InvalidDataException($"Bookmark {index + 1} needs a name and a full http:// or https:// address.");
                if (!uniqueUrls.Add(url)) throw new InvalidDataException($"The backup contains the same website more than once: {url}");
                restoredBookmarks.Add(new Bookmark { Title = title, Url = url });
            }

            if (MessageBox.Show(this,
                    $"Restore {restoredBookmarks.Count} bookmark(s) from this file? This replaces your current collection of {_bookmarks.Count} bookmark(s).",
                    "Restore bookmarks", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            var previousBookmarks = _bookmarks.Select(bookmark => new Bookmark { Title = bookmark.Title, Url = bookmark.Url }).ToList();
            _bookmarks.Clear();
            foreach (var bookmark in restoredBookmarks) _bookmarks.Add(bookmark);
            _preferences.Bookmarks = restoredBookmarks.Select(bookmark => new Bookmark { Title = bookmark.Title, Url = bookmark.Url }).ToList();
            QuickBookmarkSelector.SelectedItem = null;
            BookmarkList.SelectedItem = null;
            if (!await SavePreferencesSafelyAsync())
            {
                _bookmarks.Clear();
                foreach (var bookmark in previousBookmarks) _bookmarks.Add(bookmark);
                _preferences.Bookmarks = previousBookmarks;
                return;
            }

            SetStatus($"Restored {restoredBookmarks.Count} bookmark(s).");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            SetStatus($"The bookmark backup could not be restored: {exception.Message}", isError: true);
        }
    }

    private void MoveBookmarkUp_Click(object sender, RoutedEventArgs e) => MoveSelectedBookmark(-1);
    private void MoveBookmarkDown_Click(object sender, RoutedEventArgs e) => MoveSelectedBookmark(1);

    private void MoveSelectedBookmark(int offset)
    {
        if (BookmarkList.SelectedItem is not Bookmark selected) return;
        var currentIndex = _bookmarks.IndexOf(selected);
        var destinationIndex = currentIndex + offset;
        if (destinationIndex < 0 || destinationIndex >= _bookmarks.Count) return;
        _bookmarks.Move(currentIndex, destinationIndex);
        BookmarkList.SelectedItem = selected;
        PersistBookmarks();
    }

    private void BookmarkList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        BookmarkDetails.Text = BookmarkList.SelectedItem is Bookmark bookmark ? bookmark.Url : string.Empty;
    }

    private void PersistBookmarks()
    {
        _preferences.Bookmarks = _bookmarks.Select(bookmark => new Bookmark { Title = bookmark.Title, Url = bookmark.Url }).ToList();
        _ = SavePreferencesSafelyAsync();
    }

    private void QuickMicMute_Click(object sender, RoutedEventArgs e) => ToggleVoiceMute();
    private void QuickMusicMute_Click(object sender, RoutedEventArgs e) => ToggleMusicMute();

    private void ToggleVoiceMute()
    {
        _voiceMuted = !_voiceMuted;
        _audioMixingRouter.SetMutes(_voiceMuted, _musicMuted);
        UpdateMuteButtons();
        SetStatus(_audioMixingRouter.IsRunning
            ? (_voiceMuted ? "Microphone muted in the call output." : "Microphone unmuted in the call output.")
            : (_voiceMuted ? "Microphone will be muted the next time sharing starts." : "Microphone will be unmuted the next time sharing starts."));
    }

    private void ToggleMusicMute()
    {
        _musicMuted = !_musicMuted;
        _audioMixingRouter.SetMutes(_voiceMuted, _musicMuted);
        UpdateMuteButtons();
        SetStatus(_audioMixingRouter.IsRunning
            ? (_musicMuted ? "Music muted in the call output. Local browser playback is unchanged." : "Music unmuted in the call output.")
            : (_musicMuted ? "Music will be muted in the call output the next time sharing starts." : "Music will be unmuted in the call output the next time sharing starts."));
    }

    private void UpdateMuteButtons()
    {
        if (!IsInitialized) return;
        QuickMicMuteButton.Content = _voiceMuted ? "Mic MUTED" : "Mic ON";
        QuickMusicMuteButton.Content = _musicMuted ? "Music MUTED" : "Music ON";
        QuickMicMuteButton.Background = (Brush)FindResource(_voiceMuted ? "SecondarySurfaceBrush" : "PaleMintBrush");
        QuickMusicMuteButton.Background = (Brush)FindResource(_musicMuted ? "SecondarySurfaceBrush" : "SubtleBlueBrush");
        QuickMicMuteButton.ToolTip = $"Toggle microphone sent to the call output · {(_preferences.VoiceMuteHotkey ?? "Ctrl+Alt+M")}";
        QuickMusicMuteButton.ToolTip = $"Toggle music sent to the call output · {(_preferences.MusicMuteHotkey ?? "Ctrl+Alt+U")}";
    }

    private void InitializeMuteHotkeys()
    {
        if (!TryParseHotkey(_preferences.VoiceMuteHotkey, out _, out _)) _preferences.VoiceMuteHotkey = "Ctrl+Alt+M";
        if (!TryParseHotkey(_preferences.MusicMuteHotkey, out _, out _)) _preferences.MusicMuteHotkey = "Ctrl+Alt+U";
        if (string.Equals(_preferences.VoiceMuteHotkey, _preferences.MusicMuteHotkey, StringComparison.OrdinalIgnoreCase))
            _preferences.MusicMuteHotkey = string.Equals(_preferences.VoiceMuteHotkey, "Ctrl+Alt+U", StringComparison.OrdinalIgnoreCase)
                ? "Ctrl+Alt+M"
                : "Ctrl+Alt+U";

        var handle = new WindowInteropHelper(this).EnsureHandle();
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);
        UpdateHotkeyButtons();
        RegisterMuteHotkeys();
    }

    private void CaptureVoiceMuteHotkey_Click(object sender, RoutedEventArgs e) => BeginHotkeyCapture("Voice");
    private void CaptureMusicMuteHotkey_Click(object sender, RoutedEventArgs e) => BeginHotkeyCapture("Music");

    private void BeginHotkeyCapture(string target)
    {
        UnregisterMuteHotkeys();
        _hotkeyCaptureTarget = target;
        UpdateHotkeyButtons();
        (target == "Voice" ? VoiceMuteHotkeyButton : MusicMuteHotkeyButton).Focus();
        MuteHotkeyStatus.Text = "Press Ctrl or Alt plus another key. Press Esc to cancel.";
        SetStatus($"Choose a {target.ToLowerInvariant()} mute shortcut.");
    }

    private void UpdateHotkeyButtons()
    {
        if (!IsInitialized) return;
        VoiceMuteHotkeyButton.Content = _hotkeyCaptureTarget == "Voice" ? "Press a shortcut…" : _preferences.VoiceMuteHotkey;
        MusicMuteHotkeyButton.Content = _hotkeyCaptureTarget == "Music" ? "Press a shortcut…" : _preferences.MusicMuteHotkey;
        UpdateMuteButtons();
    }

    private bool HandleHotkeyCapture(KeyEventArgs e)
    {
        if (_hotkeyCaptureTarget is null) return false;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _hotkeyCaptureTarget = null;
            UpdateHotkeyButtons();
            RegisterMuteHotkeys();
            MuteHotkeyStatus.Text = "Shortcut change cancelled.";
            e.Handled = true;
            return true;
        }

        if (IsModifierKey(key))
        {
            e.Handled = true;
            return true;
        }

        var modifiers = Keyboard.Modifiers;
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
        {
            MuteHotkeyStatus.Text = "Include Ctrl or Alt so the shortcut works safely in the background.";
            e.Handled = true;
            return true;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey <= 0)
        {
            MuteHotkeyStatus.Text = "That key cannot be used as a shortcut. Try another key.";
            e.Handled = true;
            return true;
        }

        _ = CommitCapturedHotkeyAsync(_hotkeyCaptureTarget, FormatHotkey(key, modifiers));
        e.Handled = true;
        return true;
    }

    private async Task CommitCapturedHotkeyAsync(string target, string hotkey)
    {
        var isVoice = target == "Voice";
        var previous = isVoice ? _preferences.VoiceMuteHotkey : _preferences.MusicMuteHotkey;
        var other = isVoice ? _preferences.MusicMuteHotkey : _preferences.VoiceMuteHotkey;
        if (string.Equals(hotkey, other, StringComparison.OrdinalIgnoreCase))
        {
            _hotkeyCaptureTarget = null;
            UpdateHotkeyButtons();
            RegisterMuteHotkeys();
            MuteHotkeyStatus.Text = "Microphone and music need different shortcuts. Choose another combination.";
            return;
        }

        if (isVoice) _preferences.VoiceMuteHotkey = hotkey;
        else _preferences.MusicMuteHotkey = hotkey;
        _hotkeyCaptureTarget = null;
        UpdateHotkeyButtons();
        RegisterMuteHotkeys();

        var registered = isVoice ? _voiceHotkeyRegistered : _musicHotkeyRegistered;
        if (!registered)
        {
            if (isVoice) _preferences.VoiceMuteHotkey = previous;
            else _preferences.MusicMuteHotkey = previous;
            UpdateHotkeyButtons();
            RegisterMuteHotkeys();
            MuteHotkeyStatus.Text = "Windows could not reserve that shortcut. Another app may be using it; choose a different one.";
            return;
        }

        if (!await SavePreferencesSafelyAsync())
        {
            if (isVoice) _preferences.VoiceMuteHotkey = previous;
            else _preferences.MusicMuteHotkey = previous;
            UpdateHotkeyButtons();
            RegisterMuteHotkeys();
            return;
        }

        MuteHotkeyStatus.Text = $"Saved. Global shortcut active: {hotkey}.";
        SetStatus($"Saved {target.ToLowerInvariant()} mute shortcut: {hotkey}.");
    }

    private void RegisterMuteHotkeys()
    {
        UnregisterMuteHotkeys();
        if (_windowSource is null || _hotkeyCaptureTarget is not null) return;

        _voiceHotkeyRegistered = TryRegisterHotkey(VoiceMuteHotkeyId, _preferences.VoiceMuteHotkey);
        var duplicate = string.Equals(_preferences.VoiceMuteHotkey, _preferences.MusicMuteHotkey, StringComparison.OrdinalIgnoreCase);
        _musicHotkeyRegistered = !duplicate && TryRegisterHotkey(MusicMuteHotkeyId, _preferences.MusicMuteHotkey);
        UpdateMuteButtons();

        if (_voiceHotkeyRegistered && _musicHotkeyRegistered)
            MuteHotkeyStatus.Text = $"Global shortcuts active: {_preferences.VoiceMuteHotkey} (mic) · {_preferences.MusicMuteHotkey} (music).";
        else if (!_voiceHotkeyRegistered || !_musicHotkeyRegistered)
            MuteHotkeyStatus.Text = "A shortcut could not be reserved. Another app may use it; change it here. The mute buttons still work.";
    }

    private bool TryRegisterHotkey(int id, string hotkey)
    {
        if (_windowSource is null || !TryParseHotkey(hotkey, out var modifiers, out var virtualKey)) return false;
        return RegisterHotKey(_windowSource.Handle, id, modifiers | HotkeyModifierNoRepeat, virtualKey);
    }

    private void UnregisterMuteHotkeys()
    {
        if (_windowSource is null) return;
        if (_voiceHotkeyRegistered) _ = UnregisterHotKey(_windowSource.Handle, VoiceMuteHotkeyId);
        if (_musicHotkeyRegistered) _ = UnregisterHotKey(_windowSource.Handle, MusicMuteHotkeyId);
        _voiceHotkeyRegistered = false;
        _musicHotkeyRegistered = false;
    }

    private static bool TryParseHotkey(string? value, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;

        Key? key = null;
        foreach (var part in value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control": modifiers |= HotkeyModifierControl; break;
                case "alt": modifiers |= HotkeyModifierAlt; break;
                case "shift": modifiers |= HotkeyModifierShift; break;
                case "win":
                case "windows": modifiers |= HotkeyModifierWindows; break;
                default:
                    if (key is not null || !Enum.TryParse<Key>(part, ignoreCase: true, out var parsedKey) || parsedKey == Key.None || IsModifierKey(parsedKey)) return false;
                    key = parsedKey;
                    break;
            }
        }

        if (key is null || (modifiers & (HotkeyModifierControl | HotkeyModifierAlt)) == 0) return false;
        var keyValue = KeyInterop.VirtualKeyFromKey(key.Value);
        if (keyValue <= 0) return false;
        virtualKey = (uint)keyValue;
        return true;
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static string FormatHotkey(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join('+', parts);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WindowMessageHotKey) return IntPtr.Zero;

        switch (wParam.ToInt32())
        {
            case VoiceMuteHotkeyId: ToggleVoiceMute(); handled = true; break;
            case MusicMuteHotkeyId: ToggleMusicMute(); handled = true; break;
        }
        return IntPtr.Zero;
    }

    private async void RefreshAudioDevices_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAudioDevicesAsync();
    }

    private async Task RefreshAudioDevicesAsync()
    {
        if (_audioMixingRouter.IsRunning)
        {
            AudioRoutingStatus.Text = "Stop sharing before refreshing audio devices.";
            return;
        }

        AudioDiscoveryStatus.Text = "Checking active Windows audio endpoints…";
        AudioMicrophoneSelector.IsEnabled = false;
        AudioOutputSelector.IsEnabled = false;
        RefreshAudioDevicesButton.IsEnabled = false;
        AudioMicrophoneSelector.ItemsSource = null;
        AudioOutputSelector.ItemsSource = null;
        try
        {
            var endpoints = await AudioEndpointDiscovery.DiscoverAsync();
            var microphones = endpoints.Where(endpoint => endpoint.Direction == "Capture").ToArray();
            var virtualOutputs = endpoints.Where(IsVirtualAudioOutput).ToArray();

            AudioMicrophoneSelector.ItemsSource = microphones;
            AudioOutputSelector.ItemsSource = virtualOutputs;
            AudioMicrophoneSelector.SelectedItem = microphones.FirstOrDefault(endpoint =>
                string.Equals(endpoint.Id, _preferences.SelectedMicrophoneId, StringComparison.Ordinal))
                ?? microphones.FirstOrDefault(endpoint => endpoint.Name.Contains("Realtek", StringComparison.OrdinalIgnoreCase))
                ?? microphones.FirstOrDefault(endpoint => string.Equals(endpoint.Id,
                    AudioEndpointDiscovery.GetDefaultEndpointId(DataFlow.Capture, Role.Communications), StringComparison.Ordinal));
            AudioOutputSelector.SelectedItem = virtualOutputs.FirstOrDefault(endpoint =>
                string.Equals(endpoint.Id, _preferences.SelectedAudioOutputId, StringComparison.Ordinal))
                ?? virtualOutputs.FirstOrDefault(endpoint => endpoint.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
                ?? virtualOutputs.FirstOrDefault();

            AudioDiscoveryStatus.Text = virtualOutputs.Length == 0
                ? $"Found {microphones.Length} microphone(s), but no virtual cable output. Install VB-CABLE to send the mix to calls."
                : $"Found {microphones.Length} microphone(s) and {virtualOutputs.Length} virtual output(s). Choose the cable playback endpoint, then set its matching recording endpoint as your call microphone.";
            AudioMicrophoneSelector.IsEnabled = true;
            AudioOutputSelector.IsEnabled = true;
            RefreshAudioDevicesButton.IsEnabled = true;
            UpdateAudioControls();
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            AudioDiscoveryStatus.Text = $"Windows could not list audio endpoints: {exception.Message}";
            AudioMicrophoneSelector.IsEnabled = true;
            AudioOutputSelector.IsEnabled = true;
            RefreshAudioDevicesButton.IsEnabled = true;
            AudioRoutingStatus.Text = "Could not load audio devices. Refresh to try again.";
            UpdateAudioControls();
        }
        catch (Exception exception)
        {
            AudioDiscoveryStatus.Text = $"Audio endpoint discovery is unavailable: {exception.Message}";
            AudioMicrophoneSelector.IsEnabled = true;
            AudioOutputSelector.IsEnabled = true;
            RefreshAudioDevicesButton.IsEnabled = true;
            AudioRoutingStatus.Text = "Could not load audio devices. Refresh to try again.";
            UpdateAudioControls();
        }
    }

    private static bool IsVirtualAudioOutput(AudioEndpointInfo endpoint) =>
        endpoint.Direction == "Render" &&
        (endpoint.Name.Contains("virtual", StringComparison.OrdinalIgnoreCase)
         || endpoint.Name.Contains("cable", StringComparison.OrdinalIgnoreCase)
         || endpoint.Name.Contains("vb-audio", StringComparison.OrdinalIgnoreCase)
         || endpoint.Name.Contains("voicemeeter", StringComparison.OrdinalIgnoreCase));

    private void AudioMicrophoneSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAudioControls();
    }

    private void AudioOutputSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAudioControls();
    }

    private async void SaveAudioSelection_Click(object sender, RoutedEventArgs e)
    {
        if (AudioMicrophoneSelector.SelectedItem is not AudioEndpointInfo { Direction: "Capture" } selected)
        {
            SetStatus("Choose a Capture microphone before pressing OK.", isError: true);
            return;
        }

        var previousId = _preferences.SelectedMicrophoneId;
        _preferences.SelectedMicrophoneId = selected.Id;
        if (!await SavePreferencesSafelyAsync())
        {
            _preferences.SelectedMicrophoneId = previousId;
            UpdateAudioControls();
            return;
        }

        UpdateAudioControls();
        AudioRoutingStatus.Text = $"Saved microphone: {selected.Name}. Sharing is still stopped.";
        SetStatus("Microphone choice saved. Start sharing when you are ready.");
    }

    private void UpdateAudioControls()
    {
        bool sharing = _audioMixingRouter.IsRunning;
        var microphone = AudioMicrophoneSelector.SelectedItem as AudioEndpointInfo;
        var output = AudioOutputSelector.SelectedItem as AudioEndpointInfo;
        SaveAudioSelectionButton.IsEnabled = !sharing && microphone?.Direction == "Capture";
        AudioMicrophoneSelector.IsEnabled = !sharing;
        AudioOutputSelector.IsEnabled = !sharing;
        RefreshAudioDevicesButton.IsEnabled = !sharing;
        StartAudioRoutingButton.IsEnabled = !sharing
            && _browserAvailable
            && microphone?.Direction == "Capture"
            && output is not null
            && IsVirtualAudioOutput(output);
        StopAudioRoutingButton.IsEnabled = sharing;
        VoiceGainSlider.IsEnabled = true;
        MusicGainSlider.IsEnabled = true;
        MasterGainSlider.IsEnabled = true;

        if (!sharing)
        {
            if (microphone is null)
            {
                AudioRoutingStatus.Text = "Choose a microphone for your voice.";
            }
            else if (output is null)
            {
                AudioRoutingStatus.Text = "No virtual cable output is available. Install VB-CABLE, then refresh devices.";
            }
            else
            {
                AudioRoutingStatus.Text = $"Ready: {microphone.Name} + X-TREME PLAY'S music → {output.Name}. Press Start sharing.";
            }
        }
    }

    private async void StartAudioRouting_Click(object sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            AudioRoutingStatus.Text = "Voice and music routing requires Windows 10 version 2004 or later.";
            return;
        }

        if (AudioMicrophoneSelector.SelectedItem is not AudioEndpointInfo { Direction: "Capture" } microphone
            || AudioOutputSelector.SelectedItem is not AudioEndpointInfo output
            || !IsVirtualAudioOutput(output)
            || !_browserAvailable)
        {
            AudioRoutingStatus.Text = "Choose an active microphone and virtual cable output before starting.";
            return;
        }

        _preferences.SelectedMicrophoneId = microphone.Id;
        _preferences.SelectedAudioOutputId = output.Id;
        if (!await SavePreferencesSafelyAsync()) return;

        AudioRoutingStatus.Text = "Starting microphone and browser audio capture…";
        StartAudioRoutingButton.IsEnabled = false;
        try
        {
            await _audioMixingRouter.StartAsync(
                microphone.Id,
                output.Id,
                Browser.CoreWebView2.BrowserProcessId,
                (float)(VoiceGainSlider.Value / 100),
                (float)(MusicGainSlider.Value / 100),
                (float)(MasterGainSlider.Value / 100),
                _voiceMuted,
                _musicMuted);
            UpdateAudioControls();
            _audioMeterTimer.Start();
            AudioRoutingStatus.Text = $"SHARING ON · {microphone.Name} + browser music → {output.Name}. Press Stop to end microphone capture.";
            SetStatus("Audio sharing is on. Your microphone and browser music are mixed to the selected call output.");
        }
        catch (Exception exception)
        {
            _audioMixingRouter.Stop();
            UpdateAudioControls();
            AudioRoutingStatus.Text = $"Could not start audio sharing: {exception.Message}";
            SetStatus("Audio sharing could not start. Check the microphone and virtual cable, then retry.", isError: true);
        }
    }

    private void StopAudioRouting_Click(object sender, RoutedEventArgs e)
    {
        _audioMeterTimer.Stop();
        _audioMixingRouter.Stop();
        VoiceLevelMeter.Value = 0;
        MusicLevelMeter.Value = 0;
        OutputLevelMeter.Value = 0;
        UpdateAudioControls();
        AudioRoutingStatus.Text = "Sharing stopped. Microphone capture and routing are off.";
        SetStatus("Audio sharing stopped. Microphone capture is off.");
    }

    private void MixerGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;
        UpdateMixerGainLabels();
        _audioMixingRouter.SetGains(
            (float)(VoiceGainSlider.Value / 100),
            (float)(MusicGainSlider.Value / 100),
            (float)(MasterGainSlider.Value / 100));

        if (_settingsLoaded)
        {
            _preferences.VoiceVolumePercent = (int)Math.Round(VoiceGainSlider.Value);
            _preferences.MusicVolumePercent = (int)Math.Round(MusicGainSlider.Value);
            _preferences.MasterVolumePercent = (int)Math.Round(MasterGainSlider.Value);
        }
    }

    private void UpdateMixerGainLabels()
    {
        VoiceGainValue.Text = $"{VoiceGainSlider.Value:0}%";
        MusicGainValue.Text = $"{MusicGainSlider.Value:0}%";
        MasterGainValue.Text = $"{MasterGainSlider.Value:0}%";
    }

    private async void MixerGainSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_settingsLoaded) await SavePreferencesSafelyAsync();
    }

    private async void MixerGainSlider_KeyUp(object sender, KeyEventArgs e)
    {
        if (_settingsLoaded) await SavePreferencesSafelyAsync();
    }

    private void AudioMeterTimer_Tick(object? sender, EventArgs e)
    {
        VoiceLevelMeter.Value = Math.Clamp(_audioMixingRouter.VoiceLevel * 100, 0, 100);
        MusicLevelMeter.Value = Math.Clamp(_audioMixingRouter.MusicLevel * 100, 0, 100);
        OutputLevelMeter.Value = Math.Clamp(_audioMixingRouter.OutputLevel * 100, 0, 100);
    }

    private void PreferencesChanged(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded) return;
        _preferences.DarkTheme = DarkThemeCheckBox.IsChecked == true;
        _preferences.AlwaysOnTop = AlwaysOnTopCheckBox.IsChecked == true;
        Topmost = _preferences.AlwaysOnTop;
        ApplyTheme(_preferences.DarkTheme);
        _ = SavePreferencesSafelyAsync();
    }

    private void ApplyTheme(bool dark)
    {
        SetBrush("AppBackgroundBrush", dark ? "#101718" : "#F3F8FA");
        SetBrush("SurfaceBrush", dark ? "#1B2425" : "#FFFFFF");
        SetBrush("AccentBlueBrush", dark ? "#8EBBD0" : "#669CB5");
        SetBrush("AccentGreenBrush", dark ? "#80BD9B" : "#6BA889");
        SetBrush("PaleMintBrush", dark ? "#263D32" : "#E7F3EB");
        SetBrush("MainTextBrush", dark ? "#E6F1ED" : "#20332D");
        SetBrush("MutedTextBrush", dark ? "#AABAB4" : "#647570");
        SetBrush("SecondarySurfaceBrush", dark ? "#222F30" : "#F7FAFB");
        SetBrush("SecondaryTextBrush", dark ? "#C0D0CA" : "#49615A");
        SetBrush("NavigationActiveBrush", dark ? "#263D32" : "#E7F3EB");
        SetBrush("SubtleBlueBrush", dark ? "#23343A" : "#EEF5F8");
        SetBrush("SubtleBorderBrush", dark ? "#344344" : "#DDE8E8");
    }

    private static void SetBrush(string key, string hex)
    {
        if (ColorConverter.ConvertFromString(hex) is Color color)
        {
            Application.Current.Resources[key] = new SolidColorBrush(color);
        }
    }

    private async void ClearBrowserData_Click(object sender, RoutedEventArgs e)
    {
        if (!_browserAvailable)
        {
            SetStatus("Browser data can be cleared once WebView2 is available.", isError: true);
            return;
        }

        var confirmation = MessageBox.Show(this,
            "This clears website cookies, sign-ins, local storage, and cached data for X-TREME PLAY'S. You will be signed out of music websites. Continue?",
            "Clear browser data", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes) return;

        try
        {
            SetStatus("Clearing browser data…");
            await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            SetStatus("Browser data cleared. Websites may ask you to sign in again.");
        }
        catch (Exception exception)
        {
            SetStatus($"Browser data could not be cleared: {exception.Message}", isError: true);
        }
    }

    private async void ResetPreferences_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Reset theme, bookmarks, audio devices, and mixer levels to their defaults? Saved website sign-ins will remain.",
                "Reset app preferences", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _preferences = new AppPreferences();
        _bookmarks.Clear();
        foreach (var bookmark in _preferences.Bookmarks) _bookmarks.Add(bookmark);
        DarkThemeCheckBox.IsChecked = false;
        AlwaysOnTopCheckBox.IsChecked = false;
        VoiceGainSlider.Value = 100;
        MusicGainSlider.Value = 100;
        MasterGainSlider.Value = 100;
        _hotkeyCaptureTarget = null;
        UpdateHotkeyButtons();
        RegisterMuteHotkeys();
        Topmost = false;
        ApplyTheme(false);
        if (!_compactMode)
        {
            Width = _preferences.WindowWidth;
            Height = _preferences.WindowHeight;
            Left = Math.Max(0, (SystemParameters.PrimaryScreenWidth - Width) / 2);
            Top = Math.Max(0, (SystemParameters.PrimaryScreenHeight - Height) / 2);
        }

        await SavePreferencesSafelyAsync();
        SetStatus("App preferences reset. Website sign-ins were kept.");
    }

    private void ToggleCompactMode_Click(object sender, RoutedEventArgs e)
    {
        if (_compactMode) ExitCompactMode();
        else EnterCompactMode();
    }

    private void EnterCompactMode()
    {
        if (!_browserAvailable)
        {
            NavigateTo(_preferences.LastUrl);
            return;
        }

        _normalWidth = Width;
        _normalHeight = Height;
        _compactMode = true;
        _topmostBeforeCompact = _preferences.AlwaysOnTop;
        Topmost = true;
        SideNavigation.Visibility = Visibility.Collapsed;
        NavigationColumn.Width = new GridLength(0);
        BrandHeader.Visibility = Visibility.Collapsed;
        FooterPanel.Visibility = Visibility.Collapsed;
        BrowserPanel.Visibility = Visibility.Visible;
        HomePanel.Visibility = Visibility.Collapsed;
        BookmarksPanel.Visibility = Visibility.Collapsed;
        AudioPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        AboutPanel.Visibility = Visibility.Collapsed;
        Width = 560;
        Height = 430;
        MinWidth = 420;
        MinHeight = 340;
        CompactButtonText.Text = "Expand";
        if (Browser.Source is null) Browser.CoreWebView2.Navigate(_preferences.LastUrl);
        SetStatus("Compact view · always on top");
    }

    private void ExitCompactMode()
    {
        _compactMode = false;
        SideNavigation.Visibility = Visibility.Visible;
        NavigationColumn.Width = new GridLength(208);
        BrandHeader.Visibility = Visibility.Visible;
        FooterPanel.Visibility = Visibility.Visible;
        MinWidth = 920;
        MinHeight = 600;
        Width = _normalWidth < MinWidth ? 1180 : _normalWidth;
        Height = _normalHeight < MinHeight ? 760 : _normalHeight;
        Topmost = _topmostBeforeCompact;
        CompactButtonText.Text = "Mini";
        _ = SavePreferencesSafelyAsync();
    }

    private void InstallRuntime_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(RuntimeDownloadUrl) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SetStatus($"Could not open the WebView2 download page: {exception.Message}", isError: true);
        }
    }

    private void OpenDeveloperLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string target } ||
            !Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            SetStatus($"Opened {uri.Host}");
        }
        catch (Exception exception)
        {
            SetStatus($"Could not open {uri.Host}: {exception.Message}", isError: true);
        }
    }

    private void ShellWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleHotkeyCapture(e))
        {
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.L && BrowserPanel.Visibility == Visibility.Visible)
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && _browserAvailable && BrowserPanel.Visibility == Visibility.Visible)
        {
            Browser.CoreWebView2.Reload();
            e.Handled = true;
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isFinishingClose)
        {
            UnregisterMuteHotkeys();
            _windowSource?.RemoveHook(WindowMessageHook);
            _windowSource = null;
            return;
        }
        e.Cancel = true;
        _isFinishingClose = true;
        _audioMeterTimer.Stop();
        _audioMixingRouter.Stop();
        if (!_compactMode)
        {
            _preferences.WindowWidth = Width;
            _preferences.WindowHeight = Height;
            _preferences.WindowLeft = Left;
            _preferences.WindowTop = Top;
        }
        else
        {
            _preferences.AlwaysOnTop = _topmostBeforeCompact;
        }

        _preferences.Bookmarks = _bookmarks.Select(bookmark => new Bookmark { Title = bookmark.Title, Url = bookmark.Url }).ToList();
        try
        {
            await _settingsStore.SaveAsync(_preferences);
        }
        catch
        {
            // Preserve normal window shutdown if the preferences folder became unavailable.
        }

        Close();
    }

    private async Task<bool> SavePreferencesSafelyAsync()
    {
        try
        {
            var snapshot = new AppPreferences
            {
                DarkTheme = _preferences.DarkTheme,
                AlwaysOnTop = _preferences.AlwaysOnTop,
                WindowWidth = _preferences.WindowWidth,
                WindowHeight = _preferences.WindowHeight,
                WindowLeft = _preferences.WindowLeft,
                WindowTop = _preferences.WindowTop,
                LastUrl = _preferences.LastUrl,
                SelectedMicrophoneId = _preferences.SelectedMicrophoneId,
                SelectedAudioOutputId = _preferences.SelectedAudioOutputId,
                VoiceVolumePercent = _preferences.VoiceVolumePercent,
                MusicVolumePercent = _preferences.MusicVolumePercent,
                MasterVolumePercent = _preferences.MasterVolumePercent,
                VoiceMuteHotkey = _preferences.VoiceMuteHotkey,
                MusicMuteHotkey = _preferences.MusicMuteHotkey,
                Bookmarks = _preferences.Bookmarks.Select(bookmark => new Bookmark { Title = bookmark.Title, Url = bookmark.Url }).ToList()
            };
            await _settingsStore.SaveAsync(snapshot);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            SetStatus("Could not save preferences. Check that the data folder is writable.", isError: true);
            return false;
        }
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = (Brush)FindResource(isError ? "AccentBlueBrush" : "MutedTextBrush");
        StatusDot.Fill = (Brush)FindResource(isError ? "AccentBlueBrush" : "AccentGreenBrush");
    }

    private static bool IsValidRestorePosition(double? left, double? top)
    {
        if (left is null || top is null) return false;
        return left.Value > -SystemParameters.PrimaryScreenWidth && left.Value < SystemParameters.VirtualScreenWidth
               && top.Value > -SystemParameters.PrimaryScreenHeight && top.Value < SystemParameters.VirtualScreenHeight;
    }

    private sealed class BookmarkBackup
    {
        public string Format { get; set; } = BookmarkBackupFormat;
        public int Version { get; set; } = 1;
        public List<Bookmark?>? Bookmarks { get; set; } = [];
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int hotkeyId, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int hotkeyId);
}
