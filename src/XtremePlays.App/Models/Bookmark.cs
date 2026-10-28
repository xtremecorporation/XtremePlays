namespace XtremePlays.App.Models;

public sealed class Bookmark
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    public override string ToString() => Title;
}
