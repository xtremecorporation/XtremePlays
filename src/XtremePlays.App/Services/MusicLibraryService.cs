using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XtremePlays.App.Models;

namespace XtremePlays.App.Services;

public static class MusicLibraryService
{
    private const int MaximumTracks = 1000;
    private const int MaximumYouTubePlaylists = 25;
    private const string SpotifyProvider = "Spotify";
    private const string YouTubeProvider = "YouTube";
    private const string YouTubeScope = "https://www.googleapis.com/auth/youtube.readonly";
    private static readonly TimeSpan OAuthTimeout = TimeSpan.FromMinutes(3);
    private static readonly HttpClient Client = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly SemaphoreSlim SyncLock = new(1, 1);
    private static List<MusicLibraryTrack> _spotifySessionTracks = [];

    public static IReadOnlyList<MusicLibraryTrack> LoadCachedTracks()
    {
        var tracks = LoadDiskTrackCache();
        tracks.AddRange(_spotifySessionTracks);
        return tracks.Where(IsValidTrack).GroupBy(track => track.Id, StringComparer.Ordinal).Select(group => group.First()).Take(MaximumTracks).ToList();
    }

    private static List<MusicLibraryTrack> LoadDiskTrackCache()
    {
        try
        {
            var file = new FileInfo(AppPaths.MusicLibraryCacheFile);
            if (!file.Exists || file.Length is <= 0 or > 5 * 1024 * 1024) return [];
            var tracks = JsonSerializer.Deserialize<List<MusicLibraryTrack>>(File.ReadAllBytes(file.FullName), JsonOptions) ?? [];
            return tracks.Where(track => track is not null && track.Provider == YouTubeProvider && IsValidTrack(track)).Take(MaximumTracks).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public static bool IsConnected(string provider) => LoadTokens().ContainsKey(provider);

    public static async Task ConnectSpotifyAsync(string clientId, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateClientId(clientId, "Spotify");
        var tokens = await AuthorizeAsync(
            SpotifyProvider,
            clientId.Trim(),
            "https://accounts.spotify.com/authorize",
            "https://accounts.spotify.com/api/token",
            "user-library-read",
            includeGoogleOfflineAccess: false,
            cancellationToken);
        var store = LoadTokens();
        store[SpotifyProvider] = tokens;
        SaveTokens(store);
        await SyncSpotifyAsync(clientId.Trim(), progress, cancellationToken);
    }

    public static async Task ConnectYouTubeAsync(string clientId, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateClientId(clientId, "Google OAuth");
        var tokens = await AuthorizeAsync(
            YouTubeProvider,
            clientId.Trim(),
            "https://accounts.google.com/o/oauth2/v2/auth",
            "https://oauth2.googleapis.com/token",
            YouTubeScope,
            includeGoogleOfflineAccess: true,
            cancellationToken);
        var store = LoadTokens();
        store[YouTubeProvider] = tokens;
        SaveTokens(store);
        await SyncYouTubeAsync(clientId.Trim(), progress, cancellationToken);
    }

    public static async Task<IReadOnlyList<MusicLibraryTrack>> SyncConnectedAsync(
        string spotifyClientId,
        string youTubeClientId,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await SyncLock.WaitAsync(cancellationToken);
        try
        {
            var store = LoadTokens();
            var combined = new List<MusicLibraryTrack>();
            if (store.ContainsKey(SpotifyProvider))
            {
                progress?.Report("Syncing Spotify saved tracks…");
                combined.AddRange(await SyncSpotifyCoreAsync(spotifyClientId, store, cancellationToken));
            }
            if (store.ContainsKey(YouTubeProvider) && combined.Count < MaximumTracks)
            {
                progress?.Report("Syncing YouTube playlists…");
                combined.AddRange(await SyncYouTubeCoreAsync(youTubeClientId, store, MaximumTracks - combined.Count, cancellationToken));
            }

            SaveTokens(store);
            SaveTrackCache(combined);
            _spotifySessionTracks = combined.Where(track => track.Provider == SpotifyProvider).ToList();
            progress?.Report($"Synced {combined.Count:N0} music items.");
            return combined;
        }
        finally
        {
            SyncLock.Release();
        }
    }

    public static void Disconnect(string provider)
    {
        var store = LoadTokens();
        if (!store.Remove(provider)) return;
        SaveTokens(store);
        if (provider == SpotifyProvider) _spotifySessionTracks = [];
        var remaining = LoadDiskTrackCache().Where(track => track.Provider != provider).ToList();
        SaveTrackCache(remaining);
    }

    private static async Task SyncSpotifyAsync(string clientId, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await SyncLock.WaitAsync(cancellationToken);
        try
        {
            var store = LoadTokens();
            progress?.Report("Syncing Spotify saved tracks…");
            var tracks = await SyncSpotifyCoreAsync(clientId, store, cancellationToken);
            SaveTokens(store);
            ReplaceProviderTracks(SpotifyProvider, tracks);
            progress?.Report($"Spotify synced · {tracks.Count:N0} saved tracks.");
        }
        finally
        {
            SyncLock.Release();
        }
    }

    private static async Task SyncYouTubeAsync(string clientId, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await SyncLock.WaitAsync(cancellationToken);
        try
        {
            var store = LoadTokens();
            progress?.Report("Syncing YouTube playlists…");
            var tracks = await SyncYouTubeCoreAsync(clientId, store, MaximumTracks, cancellationToken);
            SaveTokens(store);
            ReplaceProviderTracks(YouTubeProvider, tracks);
            progress?.Report($"YouTube synced · {tracks.Count:N0} playlist items.");
        }
        finally
        {
            SyncLock.Release();
        }
    }

    private static async Task<List<MusicLibraryTrack>> SyncSpotifyCoreAsync(
        string clientId,
        Dictionary<string, ProviderTokens> store,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new InvalidOperationException("Add your Spotify Client ID in Settings, then connect Spotify again.");
        if (!store.TryGetValue(SpotifyProvider, out var tokens)) throw new InvalidOperationException("Connect Spotify from Home before syncing it.");
        tokens = await GetFreshTokensAsync(SpotifyProvider, clientId, tokens, cancellationToken);
        store[SpotifyProvider] = tokens;

        var tracks = new List<MusicLibraryTrack>();
        for (var offset = 0; offset < MaximumTracks; offset += 50)
        {
            var uri = $"https://api.spotify.com/v1/me/tracks?limit=50&offset={offset}";
            var page = await GetApiJsonAsync<SpotifySavedTracksPage>(uri, tokens.AccessToken, cancellationToken);
            if (page.Items is null || page.Items.Count == 0) break;
            foreach (var saved in page.Items)
            {
                var track = saved.Track;
                if (track is null || string.IsNullOrWhiteSpace(track.Id) || string.IsNullOrWhiteSpace(track.Name)) continue;
                var url = track.ExternalUrls?.Spotify;
                if (!IsAllowedProviderUrl(url, SpotifyProvider)) continue;
                tracks.Add(new MusicLibraryTrack
                {
                    Id = $"spotify:{track.Id}",
                    Title = Limit(track.Name, 180),
                    Artist = Limit(string.Join(", ", track.Artists?.Where(artist => !string.IsNullOrWhiteSpace(artist.Name)).Select(artist => artist.Name) ?? []), 180),
                    Provider = SpotifyProvider,
                    Url = url!,
                    ArtworkUrl = NormalizeArtwork(track.Album?.Images?.FirstOrDefault()?.Url)
                });
                if (tracks.Count >= MaximumTracks) return tracks;
            }
            if (page.Items.Count < 50) break;
        }
        return tracks;
    }

    private static async Task<List<MusicLibraryTrack>> SyncYouTubeCoreAsync(
        string clientId,
        Dictionary<string, ProviderTokens> store,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit <= 0) return [];
        if (string.IsNullOrWhiteSpace(clientId)) throw new InvalidOperationException("Add your Google OAuth Client ID in Settings, then connect YouTube again.");
        if (!store.TryGetValue(YouTubeProvider, out var tokens)) throw new InvalidOperationException("Connect YouTube from Home before syncing it.");
        tokens = await GetFreshTokensAsync(YouTubeProvider, clientId, tokens, cancellationToken);
        store[YouTubeProvider] = tokens;

        var playlists = new List<YouTubePlaylistItem>();
        string? pageToken = null;
        while (playlists.Count < MaximumYouTubePlaylists)
        {
            var query = $"part=snippet&mine=true&maxResults=50{(pageToken is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(pageToken))}";
            var page = await GetApiJsonAsync<YouTubePlaylistsPage>($"https://www.googleapis.com/youtube/v3/playlists?{query}", tokens.AccessToken, cancellationToken);
            if (page.Items is not null) playlists.AddRange(page.Items.Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Snippet?.Title)));
            pageToken = page.NextPageToken;
            if (string.IsNullOrWhiteSpace(pageToken)) break;
        }

        var tracks = new List<MusicLibraryTrack>();
        foreach (var playlist in playlists.Take(MaximumYouTubePlaylists))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = await GetApiJsonAsync<YouTubePlaylistItemsPage>(
                $"https://www.googleapis.com/youtube/v3/playlistItems?part=snippet&playlistId={Uri.EscapeDataString(playlist.Id!)}&maxResults=50",
                tokens.AccessToken,
                cancellationToken);
            foreach (var item in items.Items ?? [])
            {
                var snippet = item.Snippet;
                var videoId = snippet?.ResourceId?.VideoId;
                if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(snippet?.Title) || snippet.Title == "Private video") continue;
                tracks.Add(new MusicLibraryTrack
                {
                    Id = $"youtube:{videoId}",
                    Title = Limit(snippet.Title, 180),
                    Artist = Limit(snippet.ChannelTitle ?? playlist.Snippet?.Title ?? "YouTube", 180),
                    Provider = YouTubeProvider,
                    Url = $"https://www.youtube.com/watch?v={Uri.EscapeDataString(videoId)}",
                    ArtworkUrl = NormalizeArtwork(snippet.Thumbnails?.High?.Url ?? snippet.Thumbnails?.Medium?.Url ?? snippet.Thumbnails?.Default?.Url)
                });
                if (tracks.Count >= limit) return tracks;
            }
        }
        return tracks;
    }

    private static async Task<ProviderTokens> AuthorizeAsync(
        string provider,
        string clientId,
        string authorizeEndpoint,
        string tokenEndpoint,
        string scope,
        bool includeGoogleOfflineAccess,
        CancellationToken cancellationToken)
    {
        using var listener = StartLoopbackListener(out var redirectUri);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = scope,
            ["state"] = state,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = challenge
        };
        if (includeGoogleOfflineAccess)
        {
            query["access_type"] = "offline";
            query["prompt"] = "consent";
        }

        var authorizationUrl = authorizeEndpoint + "?" + string.Join("&", query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        Process.Start(new ProcessStartInfo(authorizationUrl) { UseShellExecute = true });
        using var linkedTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedTimeout.CancelAfter(OAuthTimeout);
        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().WaitAsync(linkedTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{provider} sign-in timed out. Try connecting again.");
        }

        var returnedState = context.Request.QueryString["state"];
        var code = context.Request.QueryString["code"];
        var providerError = context.Request.QueryString["error"];
        await RespondToOAuthAsync(context, providerError is null && string.Equals(state, returnedState, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(providerError)) throw new InvalidOperationException($"{provider} sign-in was cancelled: {providerError}.");
        if (!string.Equals(state, returnedState, StringComparison.Ordinal)) throw new InvalidDataException("The sign-in response could not be verified. Try connecting again.");
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidDataException($"{provider} did not return an authorization code.");

        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier
        };
        using var response = await Client.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
        var token = await ReadTokenResponseAsync(response, provider, cancellationToken);
        if (string.IsNullOrWhiteSpace(token.AccessToken)) throw new InvalidDataException($"{provider} returned an empty access token.");
        return new ProviderTokens
        {
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken ?? string.Empty,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn - 60))
        };
    }

    private static async Task<ProviderTokens> GetFreshTokensAsync(string provider, string clientId, ProviderTokens tokens, CancellationToken cancellationToken)
    {
        if (tokens.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return tokens;
        if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
            throw new InvalidOperationException($"{provider} needs you to connect again. Choose Connect {provider} on Home.");

        var endpoint = provider == SpotifyProvider ? "https://accounts.spotify.com/api/token" : "https://oauth2.googleapis.com/token";
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.RefreshToken
        };
        using var response = await Client.PostAsync(endpoint, new FormUrlEncodedContent(form), cancellationToken);
        var fresh = await ReadTokenResponseAsync(response, provider, cancellationToken);
        return new ProviderTokens
        {
            AccessToken = fresh.AccessToken,
            RefreshToken = string.IsNullOrWhiteSpace(fresh.RefreshToken) ? tokens.RefreshToken : fresh.RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, fresh.ExpiresIn - 60))
        };
    }

    private static async Task<T> GetApiJsonAsync<T>(string url, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if ((int)response.StatusCode == 429)
                throw new HttpRequestException("The music provider is rate-limiting requests. Wait a little and sync again.", null, response.StatusCode);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new HttpRequestException("The music account authorization expired. Disconnect it, reconnect, and try again.", null, response.StatusCode);
            throw new HttpRequestException($"Music provider API returned {(int)response.StatusCode}: {Limit(body, 300)}", null, response.StatusCode);
        }

        return await JsonSerializer.DeserializeAsync<T>(await response.Content.ReadAsStreamAsync(cancellationToken), JsonOptions, cancellationToken)
               ?? throw new InvalidDataException("The music provider returned an empty response.");
    }

    private static async Task<TokenResponse> ReadTokenResponseAsync(HttpResponseMessage response, string provider, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"{provider} sign-in failed ({(int)response.StatusCode}): {Limit(body, 300)}", null, response.StatusCode);
        }
        return await JsonSerializer.DeserializeAsync<TokenResponse>(await response.Content.ReadAsStreamAsync(cancellationToken), JsonOptions, cancellationToken)
               ?? throw new InvalidDataException($"{provider} returned an empty token response.");
    }

    private static HttpListener StartLoopbackListener(out string redirectUri)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            var listener = new HttpListener();
            redirectUri = $"http://127.0.0.1:{port}/callback/";
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return listener;
            }
            catch (HttpListenerException) when (attempt < 4)
            {
                listener.Close();
            }
        }
        throw new IOException("A local sign-in callback could not be started. Close other apps using local web ports and try again.");
    }

    private static async Task RespondToOAuthAsync(HttpListenerContext context, bool validState)
    {
        var message = validState
            ? "Sign-in finished. You can close this browser tab and return to X-TREME PLAY'S."
            : "Sign-in did not complete. Return to X-TREME PLAY'S and try again.";
        var html = $"<!doctype html><meta charset=\"utf-8\"><title>X-TREME PLAY'S</title><body style=\"font:16px Segoe UI,sans-serif;background:#0b0b0f;color:#f5f5f7;padding:3rem\"><h2>X-TREME PLAY'S</h2><p>{WebUtility.HtmlEncode(message)}</p></body>";
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = validState ? 200 : 400;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static void ReplaceProviderTracks(string provider, IReadOnlyCollection<MusicLibraryTrack> newTracks)
    {
        if (provider == SpotifyProvider) _spotifySessionTracks = newTracks.ToList();
        var cacheableTracks = LoadDiskTrackCache().Where(track => track.Provider != provider)
            .Concat(provider == YouTubeProvider ? newTracks : [])
            .Where(IsValidTrack).GroupBy(track => track.Id, StringComparer.Ordinal).Select(group => group.First())
            .Take(MaximumTracks).ToList();
        SaveTrackCache(cacheableTracks);
    }

    private static Dictionary<string, ProviderTokens> LoadTokens()
    {
        try
        {
            if (!File.Exists(AppPaths.MusicTokensFile)) return new Dictionary<string, ProviderTokens>(StringComparer.Ordinal);
            var encrypted = File.ReadAllBytes(AppPaths.MusicTokensFile);
            if (encrypted.Length is <= 0 or > 64 * 1024) return new Dictionary<string, ProviderTokens>(StringComparer.Ordinal);
            var clear = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, ProviderTokens>>(clear, JsonOptions)
                   ?? new Dictionary<string, ProviderTokens>(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            return new Dictionary<string, ProviderTokens>(StringComparer.Ordinal);
        }
    }

    private static void SaveTokens(Dictionary<string, ProviderTokens> tokens)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var clear = JsonSerializer.SerializeToUtf8Bytes(tokens, JsonOptions);
        var encrypted = ProtectedData.Protect(clear, optionalEntropy: null, DataProtectionScope.CurrentUser);
        var temporary = AppPaths.MusicTokensFile + ".tmp";
        File.WriteAllBytes(temporary, encrypted);
        File.Move(temporary, AppPaths.MusicTokensFile, overwrite: true);
    }

    private static void SaveTrackCache(List<MusicLibraryTrack> tracks)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var temporary = AppPaths.MusicLibraryCacheFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(tracks.Where(track => track.Provider == YouTubeProvider).Take(MaximumTracks), JsonOptions), new UTF8Encoding(false));
        File.Move(temporary, AppPaths.MusicLibraryCacheFile, overwrite: true);
    }

    private static bool IsValidTrack(MusicLibraryTrack? track) =>
        track is not null && !string.IsNullOrWhiteSpace(track.Id) && track.Title is { Length: <= 180 } && track.Artist is { Length: <= 180 } &&
        track.Provider is SpotifyProvider or YouTubeProvider && IsAllowedProviderUrl(track.Url, track.Provider);

    private static bool IsAllowedProviderUrl(string? value, string provider)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        return provider == SpotifyProvider
            ? uri.Host.Equals("open.spotify.com", StringComparison.OrdinalIgnoreCase)
            : uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeArtwork(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.AbsoluteUri : null;

    private static string Limit(string? value, int maxLength) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : value.Trim()[..Math.Min(value.Trim().Length, maxLength)];

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void ValidateClientId(string clientId, string provider)
    {
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 256 || clientId.Any(char.IsWhiteSpace))
            throw new InvalidOperationException($"Add the {provider} Client ID in Settings before connecting the account.");
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XtremePlays/2.0.0");
        return client;
    }

    private sealed class ProviderTokens
    {
        public ProviderTokens() { }
        public string AccessToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public DateTimeOffset ExpiresAt { get; set; }
    }

    private sealed class TokenResponse
    {
        public TokenResponse() { }
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private sealed class SpotifySavedTracksPage
    {
        public SpotifySavedTracksPage() { }
        [JsonPropertyName("items")] public List<SpotifySavedTrack>? Items { get; set; }
    }
    private sealed class SpotifySavedTrack { public SpotifySavedTrack() { } [JsonPropertyName("track")] public SpotifyTrack? Track { get; set; } }
    private sealed class SpotifyTrack
    {
        public SpotifyTrack() { }
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("artists")] public List<SpotifyArtist>? Artists { get; set; }
        [JsonPropertyName("album")] public SpotifyAlbum? Album { get; set; }
        [JsonPropertyName("external_urls")] public SpotifyExternalUrls? ExternalUrls { get; set; }
    }
    private sealed class SpotifyArtist { public SpotifyArtist() { } [JsonPropertyName("name")] public string? Name { get; set; } }
    private sealed class SpotifyAlbum { public SpotifyAlbum() { } [JsonPropertyName("images")] public List<SpotifyImage>? Images { get; set; } }
    private sealed class SpotifyImage { public SpotifyImage() { } [JsonPropertyName("url")] public string? Url { get; set; } }
    private sealed class SpotifyExternalUrls { public SpotifyExternalUrls() { } [JsonPropertyName("spotify")] public string? Spotify { get; set; } }

    private sealed class YouTubePlaylistsPage
    {
        public YouTubePlaylistsPage() { }
        [JsonPropertyName("items")] public List<YouTubePlaylistItem>? Items { get; set; }
        [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
    }
    private sealed class YouTubePlaylistItem
    {
        public YouTubePlaylistItem() { }
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("snippet")] public YouTubeSnippet? Snippet { get; set; }
    }
    private sealed class YouTubePlaylistItemsPage { public YouTubePlaylistItemsPage() { } [JsonPropertyName("items")] public List<YouTubeVideoItem>? Items { get; set; } }
    private sealed class YouTubeVideoItem { public YouTubeVideoItem() { } [JsonPropertyName("snippet")] public YouTubeSnippet? Snippet { get; set; } }
    private sealed class YouTubeSnippet
    {
        public YouTubeSnippet() { }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("channelTitle")] public string? ChannelTitle { get; set; }
        [JsonPropertyName("resourceId")] public YouTubeResourceId? ResourceId { get; set; }
        [JsonPropertyName("thumbnails")] public YouTubeThumbnails? Thumbnails { get; set; }
    }
    private sealed class YouTubeResourceId { public YouTubeResourceId() { } [JsonPropertyName("videoId")] public string? VideoId { get; set; } }
    private sealed class YouTubeThumbnails
    {
        public YouTubeThumbnails() { }
        [JsonPropertyName("default")] public YouTubeThumbnail? Default { get; set; }
        [JsonPropertyName("medium")] public YouTubeThumbnail? Medium { get; set; }
        [JsonPropertyName("high")] public YouTubeThumbnail? High { get; set; }
    }
    private sealed class YouTubeThumbnail { public YouTubeThumbnail() { } [JsonPropertyName("url")] public string? Url { get; set; } }
}
