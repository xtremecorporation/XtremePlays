namespace XtremePlays.App.Models;

// Legacy cache shape retained only to import music links from an earlier preview.
public sealed class MusicLibraryTrack
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? ArtworkUrl { get; set; }
}
