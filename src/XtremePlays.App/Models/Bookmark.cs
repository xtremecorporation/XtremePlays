using System.Text.Json.Serialization;

namespace XtremePlays.App.Models;

public sealed class Bookmark
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    [JsonIgnore]
    public string ProviderLabel
    {
        get
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri)) return "Website";
            var host = uri.Host.ToLowerInvariant();
            if (host == "music.youtube.com") return "YouTube Music";
            if (host == "youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal) || host == "youtu.be") return "YouTube";
            if (host == "spotify.com" || host.EndsWith(".spotify.com", StringComparison.Ordinal)) return "Spotify";
            return "Website";
        }
    }

    [JsonIgnore]
    public string UrlHost => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : Url;

    [JsonIgnore]
    public string ProviderGlyph => ProviderLabel switch
    {
        "YouTube" => "▶",
        "YouTube Music" => "♫",
        "Spotify" => "●",
        _ => "♪"
    };

    public override string ToString() => Title;
}
