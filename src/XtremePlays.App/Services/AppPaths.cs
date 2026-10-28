using System.IO;

namespace XtremePlays.App.Services;

public static class AppPaths
{
    private static bool _initialized;

    public static string DataDirectory { get; private set; } = string.Empty;
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string BrowserUserDataDirectory => Path.Combine(DataDirectory, "BrowserData");
    public static bool IsPortable { get; private set; }

    public static void Initialize(IEnumerable<string>? arguments = null)
    {
        if (_initialized)
        {
            return;
        }

        var portableFlag = Path.Combine(AppContext.BaseDirectory, "portable.flag");
        IsPortable = (arguments ?? []).Any(argument => string.Equals(argument, "--portable", StringComparison.OrdinalIgnoreCase))
                     || File.Exists(portableFlag);

        DataDirectory = IsPortable
            ? Path.Combine(AppContext.BaseDirectory, "portable-data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XtremeCorporation", "XtremePlays");

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(BrowserUserDataDirectory);
        _initialized = true;
    }
}
