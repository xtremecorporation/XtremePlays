namespace XtremePlays.App.Services;

public sealed record DownloadProgressUpdate(string Title, string Detail, int? Percent);

public static class DownloadProgressHub
{
    public static event Action<DownloadProgressUpdate?>? Changed;

    public static void Report(string title, string detail, int? percent = null) =>
        Changed?.Invoke(new DownloadProgressUpdate(title, detail, percent is null ? null : Math.Clamp(percent.Value, 0, 100)));

    public static void Clear() => Changed?.Invoke(null);
}
