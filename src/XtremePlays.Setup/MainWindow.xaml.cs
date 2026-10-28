using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Navigation;

namespace XtremePlays.Setup;

public partial class MainWindow : Window
{
    private const string ProductName = "X-TREME PLAY'S";
    private const string AppLauncherFileName = ProductName + ".exe";
    private const string WebViewRuntimeKey = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    private bool _isInstalling;
    private bool _driverReady = true;
    private bool _driverInstallationRequested;

    private string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XtremePlays");
    private string AppLauncherPath => Path.Combine(InstallDirectory, AppLauncherFileName);
    private string DesktopShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{ProductName}.lnk");

    public MainWindow()
    {
        InitializeComponent();
        InstallPathText.Text = InstallDirectory;
        InstallVersionText.Text = $"Version {typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.3.0"} · x64";
    }

    private void DonationNoticeAccepted_Click(object sender, RoutedEventArgs e) =>
        UpdateInstallButtonState();

    private void InstallAudioDriverCheckBox_Click(object sender, RoutedEventArgs e) =>
        UpdateInstallButtonState();

    private void UpdateInstallButtonState() =>
        InstallButton.IsEnabled = DonationNoticeAccepted.IsChecked == true || InstallAudioDriverCheckBox.IsChecked != true;

    private void OpenLink_Click(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Could not open the link. {exception.Message}", ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        e.Handled = true;
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        _isInstalling = true;
        InstallButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        _driverReady = InstallerOperations.IsDriverServicePresent();
        _driverInstallationRequested = InstallAudioDriverCheckBox.IsChecked == true;
        try
        {
            if (Process.GetProcessesByName("XtremePlays").Length > 0)
                throw new InvalidOperationException("Please close X-TREME PLAY'S and select Install again.");

            var installDriver = InstallAudioDriverCheckBox.IsChecked == true && !InstallerOperations.IsDriverServicePresent();
            var legacyInstallPresent = InstallerOperations.IsLegacyMachineInstallPresent();
            if (InstallerOperations.RequiresElevation(installDriver))
            {
                StatusText.Text = installDriver
                    ? "Windows will ask for permission to install VB-CABLE. You can skip it and still install the app."
                    : "Windows will ask for permission to move the previous system-wide installation…";
                try
                {
                    var elevatedExitCode = await RunElevatedInstallComponentsAsync(installDriver);
                    if (elevatedExitCode is not 0 and not 10)
                        throw new InvalidOperationException("Windows could not complete the driver installation or previous-version migration.");
                    if (elevatedExitCode == 10)
                        _driverReady = false;
                }
                catch (Win32Exception) when (installDriver && !legacyInstallPresent)
                {
                    // Declining the optional driver permission must not block the user-level app install.
                    _driverReady = false;
                }
            }

            _driverReady &= InstallerOperations.IsDriverServicePresent();

            if (!IsWebViewRuntimeInstalled())
            {
                StatusText.Text = "Installing Microsoft's WebView2 Runtime. Internet is required for this step…";
                await Task.Run(InstallWebViewRuntime);
            }

            StatusText.Text = "Installing X-TREME PLAY'S…";
            await InstallApplicationFilesAsync();

            if (DesktopShortcutCheckBox.IsChecked != true)
                TryDeleteFile(DesktopShortcutPath);

            StatusText.Text = _driverReady
                ? "Setup complete. The app is installed and will check GitHub for updates automatically."
                : _driverInstallationRequested
                    ? "App installed. VB-CABLE is not ready yet; restart Windows if requested, then run Full Setup again to retry."
                    : "App installed. VB-CABLE was skipped. Run Full Setup again and select it if you want audio sharing.";
            InstallButton.Content = "Finish";
            InstallButton.IsEnabled = true;
            CloseButton.IsEnabled = true;
            _isInstalling = false;
            InstallButton.Click -= InstallButton_Click;
            InstallButton.Click += FinishButton_Click;
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            CloseButton.IsEnabled = true;
            _isInstalling = false;
            InstallButton.Content = "Retry";
            InstallButton.IsEnabled = true;
            MessageBox.Show(exception.Message, $"{ProductName} setup", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task<int> RunElevatedInstallComponentsAsync(bool installDriver)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("Could not locate the setup program to install the driver or migrate the previous version.");

        using var helper = Process.Start(new ProcessStartInfo(executable)
        {
            Arguments = installDriver ? "--elevated-install-components --install-driver" : "--elevated-install-components",
            UseShellExecute = true,
            Verb = "runas"
        }) ?? throw new InvalidOperationException("Could not request Windows administrator permission.");

        await Task.Run(helper.WaitForExit);
        return helper.ExitCode;
    }

    private async Task InstallApplicationFilesAsync()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"XtremePlays-Setup-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            var setupPath = Path.Combine(temporaryRoot, "XtremePlays-Setup.exe");
            using (var resource = GetResource("VelopackSetup.exe"))
            using (var output = File.Create(setupPath))
                await resource.CopyToAsync(output);

            using var appSetup = Process.Start(new ProcessStartInfo(setupPath)
            {
                Arguments = "--silent",
                UseShellExecute = true
            })
                ?? throw new InvalidOperationException("Could not launch the app installer.");
            await Task.Run(() =>
            {
                if (!appSetup.WaitForExit((int)TimeSpan.FromMinutes(10).TotalMilliseconds))
                {
                    appSetup.Kill(entireProcessTree: true);
                    throw new TimeoutException("The app installer did not finish within 10 minutes.");
                }
            });

            if (appSetup.ExitCode != 0)
                throw new InvalidOperationException($"The app installer exited with code {appSetup.ExitCode}. Run setup again; if the problem continues, share this code with support.");

            if (!File.Exists(AppLauncherPath))
                throw new InvalidOperationException($"The installer finished, but its app launcher was not found at {AppLauncherPath}. Run setup again and share this path if the problem continues.");
        }
        finally
        {
            TryDeleteDirectory(temporaryRoot);
        }
    }

    private static bool IsWebViewRuntimeInstalled()
    {
        using var machineKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(WebViewRuntimeKey);
        using var userKey = Registry.CurrentUser.OpenSubKey(WebViewRuntimeKey);
        return HasWebViewVersion(machineKey) || HasWebViewVersion(userKey);
    }

    private static bool HasWebViewVersion(RegistryKey? key)
    {
        var value = key?.GetValue("pv") as string;
        return Version.TryParse(value, out var version) && version > new Version(0, 0, 0, 0);
    }

    private static void InstallWebViewRuntime()
    {
        if (IsWebViewRuntimeInstalled()) return;
        const string bootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"XtremePlays-WebView2-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            var bootstrapperPath = Path.Combine(temporaryRoot, "MicrosoftEdgeWebview2Setup.exe");
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            using (var response = client.GetAsync(bootstrapperUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                using var source = response.Content.ReadAsStream();
                using var output = File.Create(bootstrapperPath);
                source.CopyTo(output);
            }

            VerifyMicrosoftSignature(bootstrapperPath);
            using var runtimeSetup = Process.Start(new ProcessStartInfo(bootstrapperPath)
            {
                Arguments = "/silent /install",
                UseShellExecute = true,
                WorkingDirectory = temporaryRoot
            }) ?? throw new InvalidOperationException("Could not launch Microsoft's WebView2 Runtime installer.");
            if (!runtimeSetup.WaitForExit((int)TimeSpan.FromMinutes(10).TotalMilliseconds))
            {
                runtimeSetup.Kill(entireProcessTree: true);
                throw new TimeoutException("Microsoft WebView2 Runtime setup did not finish within 10 minutes.");
            }

            if (!IsWebViewRuntimeInstalled())
                throw new InvalidOperationException("Microsoft WebView2 Runtime is still unavailable after setup. Check the internet connection and run this setup again.");
        }
        finally
        {
            TryDeleteDirectory(temporaryRoot);
        }
    }

    private static void VerifyMicrosoftSignature(string setupPath)
    {
        if (!AuthenticodeTrust.VerifyFile(setupPath))
            throw new InvalidOperationException("Windows could not verify the Microsoft signature. The runtime installer was not launched.");

#pragma warning disable SYSLIB0057
        using var certificate = X509Certificate.CreateFromSignedFile(setupPath);
#pragma warning restore SYSLIB0057
        if (!certificate.Subject.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The runtime installer signer did not match Microsoft Corporation. The installer was not launched.");
    }

    private static Stream GetResource(string resourceName) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
        ?? throw new InvalidOperationException($"Setup file is missing an embedded resource: {resourceName}.");

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private void FinishButton_Click(object sender, RoutedEventArgs e)
    {
        if (LaunchAfterInstallCheckBox.IsChecked == true)
        {
            try
            {
                Process.Start(new ProcessStartInfo(AppLauncherPath) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                MessageBox.Show($"The app was installed, but could not be opened automatically. You can start it from the desktop or Start menu.\n\n{exception.Message}", ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isInstalling) return;
        e.Cancel = true;
        MessageBox.Show("Finish or cancel the installer windows before closing setup.", ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
