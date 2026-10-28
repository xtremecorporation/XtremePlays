namespace XtremePlays.App.Models;

public sealed class AppPreferences
{
    public bool DarkTheme { get; set; }
    public bool AlwaysOnTop { get; set; }
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 760;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public string LastUrl { get; set; } = "https://www.youtube.com/";
    public string? SelectedMicrophoneId { get; set; }
    public string? SelectedAudioOutputId { get; set; }
    public int VoiceVolumePercent { get; set; } = 100;
    public int MusicVolumePercent { get; set; } = 100;
    public int MasterVolumePercent { get; set; } = 100;
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
    public string VoiceMuteHotkey { get; set; } = "Ctrl+Alt+M";
    public string MusicMuteHotkey { get; set; } = "Ctrl+Alt+U";
    public List<Bookmark> Bookmarks { get; set; } = DefaultBookmarks();

    public static List<Bookmark> DefaultBookmarks() =>
    [
        new() { Title = "YouTube", Url = "https://www.youtube.com/" },
        new() { Title = "YouTube Music", Url = "https://music.youtube.com/" },
        new() { Title = "Spotify", Url = "https://open.spotify.com/" }
    ];
}
