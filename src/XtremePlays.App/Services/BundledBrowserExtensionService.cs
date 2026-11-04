using System.IO.Compression;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace XtremePlays.App.Services;

public static class BundledBrowserExtensionService
{
    private const long MaximumExpandedBytes = 600L * 1024 * 1024;
    private const int MaximumFileCount = 10_000;
    private const string StateFileName = "bundled-extensions.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly Definition[] Definitions =
    [
        new(
            "gighmmpiobklfepjocnamgkkbiglidom",
            "AdBlock",
            "6.52.0",
            "AdBlock-6.52.0.zip",
            "9EA759C52998A8364D9DD5C5A6BBC8A8C54E0CF1C0A71E11981AAF459F6C9797"),
        new(
            "bgnkhhnnamicmpeenaelnjfhikgbkllg",
            "AdGuard AdBlocker",
            "5.5.3.42",
            "AdGuard-5.5.3.42.zip",
            "86F52A9D84162A7106A892E2369F89C48856276C0DD6111BB1151E1E851FA2A9")
    ];

    public static bool IsBundled(string extensionId) => Definitions.Any(definition =>
        string.Equals(definition.Id, extensionId, StringComparison.Ordinal));

    public static bool HasRemovedExtensions => LoadState().RemovedIds.Count > 0;

    public static async Task<IReadOnlyList<string>> RemoveLegacyDefaultsAsync(CoreWebView2Profile profile)
    {
        var state = LoadState();
        var notices = new List<string>();
        if (state.InstalledVersions.Count == 0) return notices;

        IReadOnlyList<CoreWebView2BrowserExtension> installed;
        try
        {
            installed = await profile.GetBrowserExtensionsAsync();
        }
        catch (Exception exception)
        {
            return [$"Earlier included extensions could not be migrated: {exception.Message}"];
        }

        foreach (var definition in Definitions)
        {
            if (!state.InstalledVersions.ContainsKey(definition.Id) || state.RemovedIds.Contains(definition.Id, StringComparer.Ordinal))
                continue;
            try
            {
                var extension = installed.FirstOrDefault(item => string.Equals(item.Id, definition.Id, StringComparison.Ordinal));
                if (extension is not null) await extension.RemoveAsync();
                state.InstalledVersions.Remove(definition.Id);
                state.RemovedIds.Add(definition.Id);
                DeleteExtensionFolder(GetExtensionFolder(definition));
                SaveState(state);
            }
            catch (Exception exception)
            {
                notices.Add($"The previous {definition.Name} install could not be migrated: {exception.Message}");
            }
        }

        return notices;
    }

    public static async Task<IReadOnlyList<string>> EnsureInstalledAsync(CoreWebView2Profile profile, Action<string>? statusCallback = null)
    {
        var state = LoadState();
        var notices = new List<string>();
        Dictionary<string, CoreWebView2BrowserExtension> installed;
        try
        {
            installed = (await profile.GetBrowserExtensionsAsync()).ToDictionary(item => item.Id, StringComparer.Ordinal);
        }
        catch (Exception exception)
        {
            return [$"The included extensions could not be checked in this WebView2 profile: {exception.Message}"];
        }

        foreach (var definition in Definitions)
        {
            if (state.RemovedIds.Contains(definition.Id, StringComparer.Ordinal)) continue;

            try
            {
                var hasKnownVersion = state.InstalledVersions.TryGetValue(definition.Id, out var knownVersion);
                installed.TryGetValue(definition.Id, out var currentExtension);

                if (currentExtension is not null && hasKnownVersion &&
                    string.Equals(knownVersion, definition.Version, StringComparison.Ordinal) &&
                    IsMatchingExtractedFolder(GetExtensionFolder(definition), definition))
                {
                    continue;
                }

                if (currentExtension is not null && !hasKnownVersion)
                {
                    notices.Add($"{definition.Name} is already installed from another source, so the bundled copy was left untouched.");
                    continue;
                }

                statusCallback?.Invoke($"Preparing {definition.Name}…");
                var extensionFolder = await EnsureExtractedAsync(definition);
                var extension = await profile.AddBrowserExtensionAsync(extensionFolder);
                if (!string.Equals(extension.Id, definition.Id, StringComparison.Ordinal))
                {
                    await extension.RemoveAsync();
                    throw new InvalidDataException($"The bundled {definition.Name} package returned an unexpected extension ID.");
                }

                state.InstalledVersions[definition.Id] = definition.Version;
                SaveState(state);
                installed[definition.Id] = extension;

                if (hasKnownVersion && !string.Equals(knownVersion, definition.Version, StringComparison.Ordinal) &&
                    IsSafeVersionFolder(knownVersion))
                {
                    try
                    {
                        DeleteExtensionFolder(Path.Combine(AppPaths.BundledExtensionSourcesDirectory, definition.Id, knownVersion!));
                    }
                    catch (Exception cleanupException)
                    {
                        notices.Add($"{definition.Name} was updated, but old extension files could not be removed: {cleanupException.Message}");
                    }
                }
            }
            catch (Exception exception)
            {
                notices.Add($"{definition.Name} could not be installed automatically: {exception.Message}");
            }
        }

        return notices;
    }

    public static void MarkRemoved(string extensionId)
    {
        if (!IsBundled(extensionId)) return;
        var state = LoadState();
        if (!state.RemovedIds.Contains(extensionId, StringComparer.Ordinal))
        {
            state.RemovedIds.Add(extensionId);
            SaveState(state);
            var definition = Definitions.First(item => string.Equals(item.Id, extensionId, StringComparison.Ordinal));
            DeleteExtensionFolder(GetExtensionFolder(definition));
        }
    }

    public static void RestoreRemovedExtensions()
    {
        var state = LoadState();
        state.RemovedIds.Clear();
        SaveState(state);
    }

    private static async Task<string> EnsureExtractedAsync(Definition definition)
    {
        var target = GetExtensionFolder(definition);
        if (IsMatchingExtractedFolder(target, definition)) return target;
        DeleteExtensionFolder(target);

        var archivePath = Path.Combine(AppContext.BaseDirectory, "BundledExtensions", definition.ArchiveFileName);
        if (!File.Exists(archivePath)) throw new FileNotFoundException("The included extension package is missing from the app installation.", archivePath);

        await using (var hashStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true))
        {
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream));
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash), Convert.FromHexString(definition.Sha256)))
            {
                throw new InvalidDataException($"The included {definition.Name} package failed its integrity check.");
            }
        }

        Directory.CreateDirectory(target);
        try
        {
            await using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            long expandedBytes = 0;
            var fileCount = 0;

            foreach (var entry in archive.Entries)
            {
                var segments = entry.FullName.Replace('\\', '/')
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Where(segment => segment != ".")
                    .ToArray();
                if (segments.Length == 0) continue;
                if (segments.Any(segment => segment == ".." || segment.Contains(':') || segment.Contains('\0')))
                    throw new InvalidDataException("The bundled extension archive contains an unsafe path.");

                var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
                if (unixFileType == 0xA000) throw new InvalidDataException("The bundled extension archive contains a symbolic link.");

                var relativePath = Path.Combine(segments);
                var outputPath = Path.GetFullPath(Path.Combine(target, relativePath));
                if (!outputPath.StartsWith(Path.TrimEndingDirectorySeparator(target) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The bundled extension archive contains an unsafe path.");

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(outputPath);
                    continue;
                }

                expandedBytes = checked(expandedBytes + entry.Length);
                fileCount++;
                if (expandedBytes > MaximumExpandedBytes) throw new InvalidDataException("The bundled extension is larger than the supported extraction limit.");
                if (fileCount > MaximumFileCount) throw new InvalidDataException("The bundled extension contains too many files.");

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await using var input = entry.Open();
                await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
                await input.CopyToAsync(output);
            }

            if (!IsMatchingExtractedFolder(target, definition))
                throw new InvalidDataException($"The included {definition.Name} files did not match their declared version.");
            return target;
        }
        catch
        {
            DeleteExtensionFolder(target);
            throw;
        }
    }

    private static bool IsMatchingExtractedFolder(string folder, Definition definition)
    {
        try
        {
            var manifestPath = Path.Combine(folder, "manifest.json");
            if (!File.Exists(manifestPath)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return document.RootElement.TryGetProperty("version", out var version) &&
                   version.ValueKind == JsonValueKind.String &&
                   string.Equals(version.GetString(), definition.Version, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static string GetExtensionFolder(Definition definition) => Path.Combine(
        AppPaths.BundledExtensionSourcesDirectory,
        definition.Id,
        definition.Version);

    private static bool IsSafeVersionFolder(string? version) =>
        !string.IsNullOrWhiteSpace(version) &&
        version.All(character => char.IsAsciiDigit(character) || character == '.') &&
        version.Any(char.IsAsciiDigit);

    private static State LoadState()
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDirectory, StateFileName);
            if (!File.Exists(path)) return new State();
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new State();
            state.RemovedIds ??= [];
            state.InstalledVersions ??= new Dictionary<string, string>(StringComparer.Ordinal);
            return state;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new State();
        }
    }

    private static void SaveState(State state)
    {
        var path = Path.Combine(AppPaths.DataDirectory, StateFileName);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void DeleteExtensionFolder(string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.BundledExtensionSourcesDirectory));
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase) ||
            !fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        if (Directory.Exists(fullPath)) DeleteTreeWithoutFollowingReparsePoints(fullPath);
    }

    private static void DeleteTreeWithoutFollowingReparsePoints(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path, recursive: false);
            else File.Delete(path);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var childAttributes = File.GetAttributes(entry);
            if ((childAttributes & FileAttributes.Directory) != 0)
            {
                DeleteTreeWithoutFollowingReparsePoints(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(path, recursive: false);
    }

    private sealed record Definition(string Id, string Name, string Version, string ArchiveFileName, string Sha256);

    private sealed class State
    {
        public List<string> RemovedIds { get; set; } = [];
        public Dictionary<string, string> InstalledVersions { get; set; } = new(StringComparer.Ordinal);
    }
}
