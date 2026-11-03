using System.IO;
using System.Text.Json;

namespace XtremePlays.App.Services;

public static class BrowserExtensionPreferences
{
    private const string FileName = "browser-extension-preferences.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static bool IsPinned(string extensionId) => Load().PinnedIds.Contains(extensionId, StringComparer.Ordinal);

    public static bool TogglePinned(string extensionId)
    {
        var state = Load();
        if (state.PinnedIds.RemoveAll(id => string.Equals(id, extensionId, StringComparison.Ordinal)) > 0)
        {
            Save(state);
            return false;
        }

        state.PinnedIds.Add(extensionId);
        Save(state);
        return true;
    }

    public static void Forget(string extensionId)
    {
        var state = Load();
        if (state.PinnedIds.RemoveAll(id => string.Equals(id, extensionId, StringComparison.Ordinal)) > 0)
            Save(state);
    }

    private static State Load()
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDirectory, FileName);
            if (!File.Exists(path)) return new State();
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new State();
            state.PinnedIds ??= [];
            return state;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new State();
        }
    }

    private static void Save(State state)
    {
        var path = Path.Combine(AppPaths.DataDirectory, FileName);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private sealed class State
    {
        public List<string> PinnedIds { get; set; } = [];
    }
}
