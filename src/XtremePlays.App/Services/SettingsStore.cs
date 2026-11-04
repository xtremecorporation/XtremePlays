using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using XtremePlays.App.Models;

namespace XtremePlays.App.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task<(AppPreferences Preferences, string? Warning)> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(AppPaths.SettingsFile))
        {
            return (new AppPreferences(), null);
        }

        try
        {
            await using var stream = new FileStream(AppPaths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var preferences = await JsonSerializer.DeserializeAsync<AppPreferences>(stream, JsonOptions, cancellationToken);
            return (Normalize(preferences), null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return (new AppPreferences(), "Saved preferences could not be read. Default preferences are in use.");
        }
    }

    public async Task SaveAsync(AppPreferences preferences, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        var temporaryFile = AppPaths.SettingsFile + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, preferences, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryFile, AppPaths.SettingsFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }

            _writeLock.Release();
        }
    }

    private static AppPreferences Normalize(AppPreferences? preferences)
    {
        var result = preferences ?? new AppPreferences();
        if (result.WindowWidth is < 920 or > 10000 || double.IsNaN(result.WindowWidth)) result.WindowWidth = 1180;
        if (result.WindowHeight is < 600 or > 10000 || double.IsNaN(result.WindowHeight)) result.WindowHeight = 760;
        result.LastUrl = NormalizeWebUrl(result.LastUrl) ?? "https://www.youtube.com/";
        result.SelectedMicrophoneId = string.IsNullOrWhiteSpace(result.SelectedMicrophoneId)
            ? null
            : result.SelectedMicrophoneId.Trim()[..Math.Min(result.SelectedMicrophoneId.Trim().Length, 512)];
        result.SelectedAudioOutputId = string.IsNullOrWhiteSpace(result.SelectedAudioOutputId)
            ? null
            : result.SelectedAudioOutputId.Trim()[..Math.Min(result.SelectedAudioOutputId.Trim().Length, 512)];
        result.VoiceVolumePercent = Math.Clamp(result.VoiceVolumePercent, 0, 200);
        result.MusicVolumePercent = Math.Clamp(result.MusicVolumePercent, 0, 200);
        result.MasterVolumePercent = Math.Clamp(result.MasterVolumePercent, 0, 200);
        result.Bookmarks = (result.Bookmarks ?? []).Where(bookmark => bookmark is not null)
            .Select(bookmark => new Bookmark { Title = SanitizeTitle(bookmark.Title), Url = NormalizeWebUrl(bookmark.Url) ?? string.Empty })
            .Where(bookmark => !string.IsNullOrWhiteSpace(bookmark.Title) && bookmark.Url.Length > 0)
            .Take(100)
            .ToList();
        return result;
    }

    public static string SanitizeTitle(string? title) => string.IsNullOrWhiteSpace(title) ? string.Empty : title.Trim()[..Math.Min(title.Trim().Length, 80)];

    public static string? NormalizeWebUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(uri.Host) ? uri.AbsoluteUri : null;
    }
}
