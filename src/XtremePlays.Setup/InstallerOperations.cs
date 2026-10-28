using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;

namespace XtremePlays.Setup;

internal static class InstallerOperations
{
    private const string ProductName = "X-TREME PLAY'S";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\XtremePlays";
    private const string DriverServiceRegistryPath = @"SYSTEM\CurrentControlSet\Services\VBAudioVACMME";
    private const string VbAudioSigningCertificateThumbprint = "A77952D93229D0EC36E2543081EEA7D125732B9C";

    public static bool RequiresElevation => !IsDriverServicePresent() || IsLegacyMachineInstallPresent();

    public static void InstallDriverIfNeeded()
    {
        if (IsDriverServicePresent()) return;

        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"XtremePlays-VBCABLE-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            var archivePath = Path.Combine(temporaryRoot, "VBCABLE_Driver_Pack45.zip");
            using (var resource = GetResource("VBCABLE_Driver_Pack45.zip"))
            using (var output = File.Create(archivePath)) resource.CopyTo(output);

            var packageDirectory = Path.Combine(temporaryRoot, "package");
            ZipFile.ExtractToDirectory(archivePath, packageDirectory);
            var setupPath = Path.Combine(packageDirectory, "VBCABLE_Setup_x64.exe");
            if (!File.Exists(setupPath)) throw new FileNotFoundException("The bundled 64-bit VB-CABLE installer was not found.", setupPath);
            VerifyDriverSignature(setupPath);

            using var driverSetup = Process.Start(new ProcessStartInfo(setupPath)
            {
                WorkingDirectory = packageDirectory,
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("Could not launch the included VB-CABLE setup.");
            driverSetup.WaitForExit();

            if (driverSetup.ExitCode != 0 && !IsDriverServicePresent())
                throw new InvalidOperationException($"The included VB-CABLE installer returned code {driverSetup.ExitCode}.");
            if (!IsDriverServicePresent())
                throw new InvalidOperationException("Windows did not register the VB-CABLE driver. Restart Windows if the driver installer requested it, then run this setup again.");
        }
        finally
        {
            TryDeleteDirectory(temporaryRoot);
        }
    }

    public static void RemoveLegacyMachineInstall()
    {
        var installDirectory = GetLegacyInstallDirectory();
        if (Process.GetProcessesByName("XtremePlays").Length > 0)
            throw new InvalidOperationException("Close X-TREME PLAY'S before migrating the old installation, then retry setup.");

        if (Directory.Exists(installDirectory))
            Directory.Delete(installDirectory, recursive: true);

        var startMenuDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ProductName);
        var desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), $"{ProductName}.lnk");
        TryDeleteDirectory(startMenuDirectory);
        TryDeleteFile(desktopShortcut);

        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        key.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
    }

    private static bool IsLegacyMachineInstallPresent()
    {
        if (Directory.Exists(GetLegacyInstallDirectory())) return true;
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var uninstallEntry = key.OpenSubKey(UninstallKey, writable: false);
            return uninstallEntry is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string GetLegacyInstallDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName);

    private static bool IsDriverServicePresent()
    {
        try
        {
            using var serviceKey = Registry.LocalMachine.OpenSubKey(DriverServiceRegistryPath, writable: false);
            return serviceKey is not null;
        }
        catch
        {
            return false;
        }
    }

    private static void VerifyDriverSignature(string setupPath)
    {
        if (!AuthenticodeTrust.VerifyFile(setupPath))
            throw new InvalidOperationException("Windows could not verify the VB-Audio signature. The driver installer was not launched.");

#pragma warning disable SYSLIB0057
        using var certificate = X509Certificate.CreateFromSignedFile(setupPath);
#pragma warning restore SYSLIB0057
        if (!string.Equals(certificate.GetCertHashString(), VbAudioSigningCertificateThumbprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The included VB-CABLE installer was signed by an unexpected certificate. It was not launched.");
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
}
