using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace XtremePlays.App.Services;

public sealed record ExtensionManifestInfo(string Name, string Description, int ManifestVersion, IReadOnlyList<string> RequestedAccess);

public sealed class BrowserExtensionItem(CoreWebView2BrowserExtension extension)
{
    public CoreWebView2BrowserExtension Extension { get; } = extension;
    public string Id => Extension.Id;
    public string Name => Extension.Name;
    public bool IsEnabled => Extension.IsEnabled;
    public bool IsBundled => BundledBrowserExtensionService.IsBundled(Id);
    public string DisplayName => $"{Name}  ·  {(IsBundled ? "Included" : "Added")}  ·  {(IsEnabled ? "Enabled" : "Disabled")}";
}

public static class BrowserExtensionImportService
{
    private const long MaximumExtensionBytes = 150L * 1024 * 1024;
    private const int MaximumExtensionFiles = 5000;
    private const string RegistryFileName = "extension-sources.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static ExtensionManifestInfo Inspect(string folderPath)
    {
        var folder = Path.GetFullPath(folderPath);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("The selected extension folder could not be found.");

        var manifestPath = Path.Combine(folder, "manifest.json");
        var manifestFile = new FileInfo(manifestPath);
        if (!manifestFile.Exists) throw new InvalidDataException("Choose the extension's unpacked top-level folder. It must contain manifest.json.");
        if (manifestFile.Length > 1024 * 1024) throw new InvalidDataException("The extension manifest is larger than 1 MB.");

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifest = document.RootElement;
        if (manifest.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The extension manifest is not a JSON object.");
        if (!manifest.TryGetProperty("manifest_version", out var versionElement) || !versionElement.TryGetInt32(out var version) || version is not (2 or 3))
            throw new InvalidDataException("This app supports browser extensions with manifest version 2 or 3.");

        var name = GetString(manifest, "name");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200) throw new InvalidDataException("The extension manifest needs a valid name (up to 200 characters).");

        var description = GetString(manifest, "description");
        var requestedAccess = new List<string>();
        AddArrayStrings(manifest, "permissions", requestedAccess);
        AddArrayStrings(manifest, "host_permissions", requestedAccess, "Website access");
        AddArrayStrings(manifest, "optional_permissions", requestedAccess, "Optional permission");
        AddArrayStrings(manifest, "optional_host_permissions", requestedAccess, "Optional website access");
        if (manifest.TryGetProperty("content_scripts", out var contentScripts) && contentScripts.ValueKind == JsonValueKind.Array)
        {
            foreach (var contentScript in contentScripts.EnumerateArray()) AddArrayStrings(contentScript, "matches", requestedAccess, "Page access");
        }

        return new ExtensionManifestInfo(name, description, version, requestedAccess.Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList());
    }

    public static string CopyToManagedFolder(string sourceFolder)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceFolder));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.ExtensionSourcesDirectory));
        if (IsSameOrChildPath(root, source) || IsSameOrChildPath(source, root))
            throw new InvalidDataException("Choose an extension folder outside X-TREME PLAY'S app data folder.");

        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        try
        {
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Choose a regular folder, not a symbolic link or reparse point.");

            var pending = new Stack<(string Source, string Destination)>();
            pending.Push((source, destination));
            long totalBytes = 0;
            var fileCount = 0;

            while (pending.TryPop(out var current))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current.Source))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("The extension folder contains a symbolic link or reparse point. Remove it and try again.");

                    var target = Path.Combine(current.Destination, Path.GetFileName(entry));
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        Directory.CreateDirectory(target);
                        pending.Push((entry, target));
                    }
                    else
                    {
                        var file = new FileInfo(entry);
                        totalBytes = checked(totalBytes + file.Length);
                        fileCount++;
                        if (totalBytes > MaximumExtensionBytes) throw new InvalidDataException("The extension is larger than the 150 MB import limit.");
                        if (fileCount > MaximumExtensionFiles) throw new InvalidDataException("The extension contains too many files to import safely.");
                        File.Copy(entry, target, overwrite: false);
                    }
                }
            }

            _ = Inspect(destination);
            return destination;
        }
        catch
        {
            DeleteManagedFolder(destination);
            throw;
        }
    }

    public static void RememberSource(string extensionId, string folderPath)
    {
        var sources = LoadSources();
        sources[extensionId] = Path.GetFullPath(folderPath);
        SaveSources(sources);
    }

    public static void ForgetAndDeleteSource(string extensionId)
    {
        var sources = LoadSources();
        if (!sources.Remove(extensionId, out var folderPath)) return;
        SaveSources(sources);
        DeleteManagedFolder(folderPath);
    }

    public static void DiscardManagedFolder(string folderPath) => DeleteManagedFolder(folderPath);

    private static Dictionary<string, string> LoadSources()
    {
        try
        {
            var file = Path.Combine(AppPaths.DataDirectory, RegistryFileName);
            if (!File.Exists(file)) return new Dictionary<string, string>(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static void SaveSources(Dictionary<string, string> sources)
    {
        var path = Path.Combine(AppPaths.DataDirectory, RegistryFileName);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(sources, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void DeleteManagedFolder(string folderPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.ExtensionSourcesDirectory));
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        if (!IsSameOrChildPath(root, folder) || string.Equals(root, folder, StringComparison.OrdinalIgnoreCase)) return;
        if (Directory.Exists(folder)) DeleteManagedTree(folder);
    }

    private static void DeleteManagedTree(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path, recursive: false);
            else File.Delete(path);
            return;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) DeleteManagedTree(child);
            Directory.Delete(path, recursive: false);
        }
        else
        {
            File.Delete(path);
        }
    }

    private static bool IsSameOrChildPath(string root, string candidate)
    {
        return string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetString(JsonElement parent, string propertyName)
    {
        return parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static void AddArrayStrings(JsonElement parent, string propertyName, List<string> values, string prefix = "Permission")
    {
        if (!parent.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array) return;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value)) values.Add($"{prefix}: {value}");
        }
    }
}
