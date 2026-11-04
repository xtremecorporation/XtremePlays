using System.Net.Http;
using System.Net;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XtremePlays.App.Services;

public sealed class PluginCatalog
{
    public int SchemaVersion { get; set; }
    public string UpdatedAt { get; set; } = string.Empty;
    public List<PluginCatalogEntry> Plugins { get; set; } = [];
}

public sealed class PluginCatalogEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string SourceLabel { get; set; } = "X-TREME catalog";
    public string? SourceUrl { get; set; }
    public string? PackageUrl { get; set; }
    public string? Sha256 { get; set; }
    public string? ReleaseTag { get; set; }
    public string? AssetName { get; set; }
    public string? PayloadFile { get; set; }
    public string License { get; set; } = string.Empty;
    public List<string> Capabilities { get; set; } = [];
    public string? ExtensionId { get; set; }
}

public sealed record ResolvedPluginPackage(Uri DownloadUri, string Sha256, long? Size);

public static class PluginCatalogService
{
    public const string CatalogUrl = "https://raw.githubusercontent.com/xtremecorporation/XtremePlays/main/plugins/catalog.json";
    public const string CategoryExtensions = "Extensions";
    public const string CategoryAdBlockers = "Ad blockers";
    public const string CategoryMusic = "Music";
    public const string CategoryTools = "Tools";
    private const int MaximumCatalogBytes = 2 * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    public static async Task<PluginCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        Exception? networkError = null;
        try
        {
            using var response = await Client.GetAsync(CatalogUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumCatalogBytes)
                throw new InvalidDataException("The plugin catalog is larger than the allowed size.");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (output.Length + read > MaximumCatalogBytes)
                    throw new InvalidDataException("The plugin catalog is larger than the allowed size.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            var catalog = Parse(output.ToArray());
            Directory.CreateDirectory(AppPaths.PluginsDirectory);
            await File.WriteAllBytesAsync(AppPaths.PluginCatalogCachePath, output.ToArray(), cancellationToken);
            return catalog;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or JsonException or UnauthorizedAccessException or TaskCanceledException)
        {
            networkError = exception;
        }

        try
        {
            if (File.Exists(AppPaths.PluginCatalogCachePath))
            {
                var file = new FileInfo(AppPaths.PluginCatalogCachePath);
                if (file.Length <= MaximumCatalogBytes)
                    return Parse(await File.ReadAllBytesAsync(file.FullName, cancellationToken));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            networkError = new IOException("The online plugin catalog and its saved copy could not be read.", exception);
        }

        try
        {
            const string resourceName = "XtremePlays.App.plugins.catalog.json";
            await using var stream = typeof(PluginCatalogService).Assembly.GetManifestResourceStream(resourceName)
                                    ?? throw new FileNotFoundException("The built-in plugin catalog is missing.");
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (output.Length + read > MaximumCatalogBytes)
                    throw new InvalidDataException("The built-in plugin catalog is larger than the allowed size.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            var bytes = output.ToArray();
            var catalog = Parse(bytes);
            Directory.CreateDirectory(AppPaths.PluginsDirectory);
            await File.WriteAllBytesAsync(AppPaths.PluginCatalogCachePath, bytes, cancellationToken);
            return catalog;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            networkError = new IOException("The online catalog, saved copy, and built-in catalog could not be read.", exception);
        }

        throw new IOException("The plugin catalog could not be loaded. Check your internet connection and try again.", networkError);
    }

    public static async Task<ResolvedPluginPackage> ResolvePackageAsync(PluginCatalogEntry plugin, CancellationToken cancellationToken = default)
    {
        string? downloadUrl = plugin.PackageUrl;
        string? digest = plugin.Sha256;
        long? size = null;

        if (!string.IsNullOrWhiteSpace(plugin.ReleaseTag) && !string.IsNullOrWhiteSpace(plugin.AssetName))
        {
            var tag = Uri.EscapeDataString(plugin.ReleaseTag);
            var apiUrl = $"https://api.github.com/repos/xtremecorporation/XtremePlays/releases/tags/{tag}";
            using var response = await Client.GetAsync(apiUrl, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                if (string.IsNullOrWhiteSpace(downloadUrl))
                    throw new FileNotFoundException($"The package for '{plugin.Name}' is not published yet.");
            }
            else
            {
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
                if (!document.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("The plugin release does not contain a readable asset list.");

                var asset = assets.EnumerateArray().FirstOrDefault(item =>
                    item.TryGetProperty("name", out var name) &&
                    string.Equals(name.GetString(), plugin.AssetName, StringComparison.Ordinal));
                if (asset.ValueKind == JsonValueKind.Object)
                {
                    downloadUrl = asset.TryGetProperty("browser_download_url", out var url) ? url.GetString() : null;
                    if (string.IsNullOrWhiteSpace(plugin.Sha256) && asset.TryGetProperty("digest", out var assetDigest) && assetDigest.ValueKind == JsonValueKind.String)
                        digest = assetDigest.GetString()?.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase);
                    if (asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var assetSize)) size = assetSize;
                }
                else if (string.IsNullOrWhiteSpace(downloadUrl))
                {
                    throw new FileNotFoundException($"The package for '{plugin.Name}' is not attached to its GitHub release yet.");
                }
            }
        }

        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var downloadUri) || !IsAllowedGitHubUrl(downloadUri))
            throw new InvalidDataException($"The package URL for '{plugin.Name}' is not an approved GitHub HTTPS URL.");
        if (string.IsNullOrWhiteSpace(digest) || digest.Length != 64 || digest.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException($"A valid SHA-256 checksum is not available for '{plugin.Name}'. The package has not been installed.");

        return new ResolvedPluginPackage(downloadUri, digest.ToUpperInvariant(), size);
    }

    private static PluginCatalog Parse(byte[] bytes)
    {
        var catalog = JsonSerializer.Deserialize<PluginCatalog>(bytes, JsonOptions)
                      ?? throw new InvalidDataException("The plugin catalog is empty.");
        if (catalog.SchemaVersion != 1 || catalog.Plugins.Count > 500)
            throw new InvalidDataException("The plugin catalog uses an unsupported format.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in catalog.Plugins)
        {
            if (string.IsNullOrWhiteSpace(plugin.Id) || plugin.Id.Length > 80 ||
                plugin.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
                throw new InvalidDataException("The plugin catalog contains an invalid plugin ID.");
            if (!ids.Add(plugin.Id)) throw new InvalidDataException($"The plugin catalog contains duplicate ID '{plugin.Id}'.");
            if (string.IsNullOrWhiteSpace(plugin.Name) || plugin.Name.Length > 120 ||
                string.IsNullOrWhiteSpace(plugin.Description) || plugin.Description.Length > 800)
                throw new InvalidDataException($"Plugin '{plugin.Id}' has invalid display information.");
            if (plugin.Type is not ("browser-extension" or "native-adblock"))
                throw new InvalidDataException($"Plugin '{plugin.Id}' has an unsupported plugin type.");
            if (plugin.Category is not (CategoryExtensions or CategoryAdBlockers or CategoryMusic or CategoryTools))
                throw new InvalidDataException($"Plugin '{plugin.Id}' has an unsupported category.");
            if (string.IsNullOrWhiteSpace(plugin.Version) || plugin.Version.Length > 40)
                throw new InvalidDataException($"Plugin '{plugin.Id}' has an invalid version.");
            if (plugin.Type == "browser-extension" && string.IsNullOrWhiteSpace(plugin.ExtensionId))
                throw new InvalidDataException($"Browser extension '{plugin.Id}' is missing its expected extension ID.");
            if (plugin.Capabilities.Count > 30 || plugin.Capabilities.Any(value => value.Length > 160))
                throw new InvalidDataException($"Plugin '{plugin.Id}' contains invalid capability labels.");
        }

        return catalog;
    }

    public static bool IsAllowedGitHubUrl(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XtremePlays-PluginStore/2.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
