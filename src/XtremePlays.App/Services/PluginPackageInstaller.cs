using System.IO.Compression;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace XtremePlays.App.Services;

public sealed class InstalledPluginRecord
{
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? ExtensionId { get; set; }
    public string InstallDirectory { get; set; } = string.Empty;
    public DateTimeOffset InstalledAt { get; set; }
}

public static class PluginPackageInstaller
{
    private const long MaximumPackageBytes = 220L * 1024 * 1024;
    private const long MaximumExpandedBytes = 800L * 1024 * 1024;
    private const int MaximumFileCount = 20_000;
    private static readonly SemaphoreSlim InstallLock = new(1, 1);
    private static readonly HttpClient DownloadClient = CreateDownloadClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static IReadOnlyDictionary<string, InstalledPluginRecord> GetInstalledPlugins() => LoadState();

    public static bool IsInstalled(PluginCatalogEntry plugin) =>
        LoadState().TryGetValue(plugin.Id, out var installed) &&
        string.Equals(installed.Version, plugin.Version, StringComparison.Ordinal);

    public static string? GetPayloadFile(string pluginId, string fileName)
    {
        var state = LoadState();
        if (!state.TryGetValue(pluginId, out var installed)) return null;
        if (!IsSafeInstallRecord(pluginId, installed)) return null;
        if (Path.GetFileName(fileName) != fileName) return null;
        var directory = Path.GetFullPath(installed.InstallDirectory);
        var candidate = Path.GetFullPath(Path.Combine(directory, fileName));
        return IsInside(InstalledRoot, directory) && File.Exists(candidate) && IsInside(directory, candidate) ? candidate : null;
    }

    public static async Task<InstalledPluginRecord> InstallAsync(
        PluginCatalogEntry plugin,
        CoreWebView2Profile? profile,
        Func<ExtensionManifestInfo, bool>? confirmExtensionAccess = null,
        IProgress<string>? progress = null,
        IProgress<int>? downloadProgress = null,
        CancellationToken cancellationToken = default)
    {
        await InstallLock.WaitAsync(cancellationToken);
        string? downloadPath = null;
        string? stagingDirectory = null;
        string? finalDirectory = null;
        var createdFinalDirectory = false;
        string? previousExtensionSource = null;
        CoreWebView2BrowserExtension? installedExtension = null;
        try
        {
            if (plugin.Type == "browser-extension" && profile is null)
                throw new InvalidOperationException("The browser is not ready to install extensions yet.");

            progress?.Report($"Checking the {plugin.Name} package…");
            var package = await PluginCatalogService.ResolvePackageAsync(plugin, cancellationToken);
            if (package.Size is > MaximumPackageBytes)
                throw new InvalidDataException("This plugin package is larger than the 220 MB install limit.");

            Directory.CreateDirectory(AppPaths.PluginsDirectory);
            Directory.CreateDirectory(InstalledRoot);
            downloadPath = Path.Combine(AppPaths.PluginsDirectory, $"download-{Guid.NewGuid():N}.zip");
            await DownloadAsync(package, downloadPath, progress, downloadProgress, cancellationToken);

            stagingDirectory = Path.Combine(AppPaths.PluginsDirectory, $"staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDirectory);
            progress?.Report($"Preparing {plugin.Name}…");
            await ExtractSafelyAsync(downloadPath, stagingDirectory, cancellationToken);
            var payloadRoot = FindPayloadRoot(stagingDirectory, plugin);
            finalDirectory = GetInstallDirectory(plugin);
            if (Directory.Exists(finalDirectory))
                throw new IOException("A folder for this plugin version already exists. Remove that plugin version from the manager, then retry.");
            Directory.CreateDirectory(Path.GetDirectoryName(finalDirectory)!);
            Directory.Move(payloadRoot, finalDirectory);
            createdFinalDirectory = true;

            string? installedExtensionId = null;
            if (plugin.Type == "browser-extension")
            {
                progress?.Report($"Installing {plugin.Name} in the private browser profile…");
                var manifest = BrowserExtensionImportService.Inspect(finalDirectory);
                if (confirmExtensionAccess is not null && !confirmExtensionAccess(manifest))
                    throw new OperationCanceledException("The extension install was cancelled before it requested access.", cancellationToken);

                var existingExtension = (await profile!.GetBrowserExtensionsAsync())
                    .FirstOrDefault(item => string.Equals(item.Id, plugin.ExtensionId, StringComparison.Ordinal));
                if (existingExtension is not null)
                {
                    previousExtensionSource = BrowserExtensionImportService.GetRememberedSource(existingExtension.Id);
                    if (previousExtensionSource is null)
                        throw new InvalidOperationException("An extension with this ID is already installed. Remove that copy before installing the X-TREME catalog version.");
                    await existingExtension.RemoveAsync();
                }

                installedExtension = await profile.AddBrowserExtensionAsync(finalDirectory);
                if (!string.Equals(installedExtension.Id, plugin.ExtensionId, StringComparison.Ordinal))
                {
                    await installedExtension.RemoveAsync();
                    installedExtension = null;
                    throw new InvalidDataException("The installed extension ID does not match the ID in the X-TREME catalog.");
                }
                installedExtensionId = installedExtension.Id;
                BrowserExtensionImportService.RememberSource(installedExtension.Id, finalDirectory);
            }
            else if (plugin.Type == "native-adblock")
            {
                var payloadFile = plugin.PayloadFile;
                if (string.IsNullOrWhiteSpace(payloadFile) || Path.GetFileName(payloadFile) != payloadFile ||
                    !File.Exists(Path.Combine(finalDirectory, payloadFile)))
                    throw new InvalidDataException("The ad-block plugin package is missing its declared engine file.");
            }

            var record = new InstalledPluginRecord
            {
                Id = plugin.Id,
                Version = plugin.Version,
                Type = plugin.Type,
                ExtensionId = installedExtensionId ?? plugin.ExtensionId,
                InstallDirectory = finalDirectory,
                InstalledAt = DateTimeOffset.UtcNow
            };
            var state = LoadState();
            state.TryGetValue(plugin.Id, out var previousRecord);
            state[plugin.Id] = record;
            SaveState(state);
            try
            {
                if (previousRecord is not null && IsSafeInstallRecord(plugin.Id, previousRecord) &&
                    !string.Equals(previousRecord.InstallDirectory, record.InstallDirectory, StringComparison.OrdinalIgnoreCase))
                    DeleteManagedDirectory(previousRecord.InstallDirectory);
                if (previousExtensionSource is not null) BrowserExtensionImportService.DiscardManagedFolder(previousExtensionSource);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The new version is installed; an older managed copy can be cleaned up later.
            }
            progress?.Report($"{plugin.Name} is installed.");
            return record;
        }
        catch
        {
            if (installedExtension is not null)
            {
                try
                {
                    await installedExtension.RemoveAsync();
                    BrowserExtensionImportService.ForgetAndDeleteSource(installedExtension.Id);
                }
                catch (Exception) { }
            }
            if (previousExtensionSource is not null && Directory.Exists(previousExtensionSource) && profile is not null)
            {
                try
                {
                    await profile.AddBrowserExtensionAsync(previousExtensionSource);
                    if (!string.IsNullOrWhiteSpace(plugin.ExtensionId))
                        BrowserExtensionImportService.RememberSource(plugin.ExtensionId, previousExtensionSource);
                }
                catch (Exception) { }
            }
            if (createdFinalDirectory && finalDirectory is not null) DeleteManagedDirectory(finalDirectory);
            if (stagingDirectory is not null) DeleteManagedDirectory(stagingDirectory);
            throw;
        }
        finally
        {
            if (downloadPath is not null) DeleteManagedFile(downloadPath);
            InstallLock.Release();
        }
    }

    public static async Task RemoveAsync(
        PluginCatalogEntry plugin,
        CoreWebView2Profile? profile,
        CancellationToken cancellationToken = default)
    {
        await InstallLock.WaitAsync(cancellationToken);
        try
        {
            var state = LoadState();
            if (!state.TryGetValue(plugin.Id, out var installed))
            {
                if (plugin.Type == "browser-extension" && profile is not null && !string.IsNullOrWhiteSpace(plugin.ExtensionId))
                {
                    var existing = (await profile.GetBrowserExtensionsAsync())
                        .FirstOrDefault(item => string.Equals(item.Id, plugin.ExtensionId, StringComparison.Ordinal));
                    if (existing is not null)
                    {
                        await existing.RemoveAsync();
                        BrowserExtensionPreferences.Forget(existing.Id);
                        BrowserExtensionImportService.ForgetAndDeleteSource(existing.Id);
                    }
                }
                return;
            }
            if (!IsSafeInstallRecord(plugin.Id, installed))
                throw new InvalidDataException("The saved plugin install record points outside its managed install folder. It was left untouched.");

            if (installed.Type == "browser-extension" && !string.IsNullOrWhiteSpace(installed.ExtensionId) && profile is not null)
            {
                var extensions = await profile.GetBrowserExtensionsAsync();
                var extension = extensions.FirstOrDefault(item => string.Equals(item.Id, installed.ExtensionId, StringComparison.Ordinal));
                if (extension is not null) await extension.RemoveAsync();
                BrowserExtensionPreferences.Forget(installed.ExtensionId);
                BrowserExtensionImportService.ForgetAndDeleteSource(installed.ExtensionId);
            }
            else if (installed.Type == "browser-extension")
            {
                throw new InvalidOperationException("The browser must be ready before an extension can be removed.");
            }

            state.Remove(plugin.Id);
            SaveState(state);
            DeleteManagedDirectory(installed.InstallDirectory);
        }
        finally
        {
            InstallLock.Release();
        }
    }

    private static async Task DownloadAsync(ResolvedPluginPackage package, string destination, IProgress<string>? progress, IProgress<int>? downloadProgress, CancellationToken cancellationToken)
    {
        using var response = await DownloadClient.GetAsync(package.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } finalUri && !PluginCatalogService.IsAllowedGitHubUrl(finalUri))
            throw new InvalidDataException("The download was redirected away from GitHub; the package was rejected.");
        if (response.Content.Headers.ContentLength is > MaximumPackageBytes)
            throw new InvalidDataException("The plugin package is larger than the 220 MB install limit.");

        progress?.Report("Downloading the selected plugin from GitHub…");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        var contentLength = response.Content.Headers.ContentLength;
        long downloadedBytes = 0;
        var lastPercent = -1;
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
        {
            var buffer = new byte[128 * 1024];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                downloadedBytes += read;
                if (downloadedBytes > MaximumPackageBytes) throw new InvalidDataException("The plugin package is larger than the 220 MB install limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                if (contentLength is > 0)
                {
                    var percent = (int)Math.Clamp(downloadedBytes * 100 / contentLength.Value, 0, 100);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        downloadProgress?.Report(percent);
                    }
                }
            }
        }

        downloadProgress?.Report(100);

        await using var packageStream = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(packageStream, cancellationToken));
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualHash), Convert.FromHexString(package.Sha256)))
            throw new InvalidDataException("The downloaded package failed its SHA-256 verification and was not installed.");
    }

    private static async Task ExtractSafelyAsync(string archivePath, string destination, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        long expandedBytes = 0;
        var fileCount = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segments = entry.FullName.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(segment => segment != ".")
                .ToArray();
            if (segments.Length == 0) continue;
            if (segments.Any(segment => segment == ".." || segment.Contains(':') || segment.Contains('\0')))
                throw new InvalidDataException("The plugin archive contains an unsafe file path.");
            var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixFileType == 0xA000) throw new InvalidDataException("The plugin archive contains a symbolic link.");

            var outputPath = Path.GetFullPath(Path.Combine(destination, Path.Combine(segments)));
            if (!IsInside(destination, outputPath)) throw new InvalidDataException("The plugin archive contains an unsafe file path.");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }

            expandedBytes = checked(expandedBytes + entry.Length);
            fileCount++;
            if (expandedBytes > MaximumExpandedBytes) throw new InvalidDataException("The expanded plugin is larger than the 800 MB install limit.");
            if (fileCount > MaximumFileCount) throw new InvalidDataException("The plugin package contains too many files.");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await using var input = entry.Open();
            await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    private static string FindPayloadRoot(string stagingDirectory, PluginCatalogEntry plugin)
    {
        if (plugin.Type == "browser-extension")
        {
            var manifests = Directory.EnumerateFiles(stagingDirectory, "manifest.json", SearchOption.AllDirectories).Take(2).ToArray();
            if (manifests.Length != 1) throw new InvalidDataException("The extension package must contain exactly one manifest.json file.");
            var folder = Path.GetDirectoryName(manifests[0])!;
            var manifest = BrowserExtensionImportService.Inspect(folder);
            if (string.IsNullOrWhiteSpace(manifest.Name)) throw new InvalidDataException("The extension manifest has no display name.");
            return folder;
        }

        var payloadFile = plugin.PayloadFile;
        if (string.IsNullOrWhiteSpace(payloadFile) || Path.GetFileName(payloadFile) != payloadFile)
            throw new InvalidDataException("The native plugin catalog entry has an invalid payload filename.");
        var matches = Directory.EnumerateFiles(stagingDirectory, payloadFile, SearchOption.AllDirectories).Take(2).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("The plugin archive does not contain exactly one declared engine file.");
        return Path.GetDirectoryName(matches[0])!;
    }

    private static string GetInstallDirectory(PluginCatalogEntry plugin) => Path.Combine(
        InstalledRoot,
        plugin.Id,
        plugin.Version);

    private static bool IsSafeInstallRecord(string pluginId, InstalledPluginRecord record)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || !string.Equals(record.Id, pluginId, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(record.Version) || record.Version.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            return false;
        var expected = Path.GetFullPath(Path.Combine(InstalledRoot, pluginId, record.Version));
        return string.Equals(Path.GetFullPath(record.InstallDirectory), expected, StringComparison.OrdinalIgnoreCase) && IsInside(InstalledRoot, expected);
    }

    private static string InstalledRoot => Path.Combine(AppPaths.PluginsDirectory, "Installed");

    private static Dictionary<string, InstalledPluginRecord> LoadState()
    {
        try
        {
            if (!File.Exists(AppPaths.PluginStateFile)) return new Dictionary<string, InstalledPluginRecord>(StringComparer.OrdinalIgnoreCase);
            return JsonSerializer.Deserialize<Dictionary<string, InstalledPluginRecord>>(File.ReadAllText(AppPaths.PluginStateFile), JsonOptions)
                   ?? new Dictionary<string, InstalledPluginRecord>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, InstalledPluginRecord>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveState(Dictionary<string, InstalledPluginRecord> state)
    {
        Directory.CreateDirectory(AppPaths.PluginsDirectory);
        var temporaryPath = AppPaths.PluginStateFile + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporaryPath, AppPaths.PluginStateFile, overwrite: true);
    }

    private static bool IsInside(string parent, string child)
    {
        var fullParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var fullChild = Path.GetFullPath(child);
        return string.Equals(fullParent, fullChild, StringComparison.OrdinalIgnoreCase) ||
               fullChild.StartsWith(fullParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XtremePlays-PluginStore/2.0");
        return client;
    }

    private static void DeleteManagedDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!IsInside(AppPaths.PluginsDirectory, fullPath) || string.Equals(fullPath, Path.GetFullPath(AppPaths.PluginsDirectory), StringComparison.OrdinalIgnoreCase)) return;
        if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
    }

    private static void DeleteManagedFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!IsInside(AppPaths.PluginsDirectory, fullPath) || string.Equals(fullPath, Path.GetFullPath(AppPaths.PluginsDirectory), StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(fullPath)) File.Delete(fullPath);
    }
}
