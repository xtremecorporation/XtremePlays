using Microsoft.Web.WebView2.Core;

namespace XtremePlays.App.Services;

public static class UnwantedBrowserExtensionCleanup
{
    private static readonly HashSet<string> UnwantedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft Clipboard Extension",
        "Microsoft Edge PDF Viewer"
    };

    public static async Task<IReadOnlyList<string>> RemoveAsync(CoreWebView2Profile profile)
    {
        IReadOnlyList<CoreWebView2BrowserExtension> installed;
        try
        {
            installed = await profile.GetBrowserExtensionsAsync();
        }
        catch (Exception exception)
        {
            return [$"The unwanted Microsoft extensions could not be checked: {exception.Message}"];
        }

        var notices = new List<string>();
        foreach (var extension in installed.Where(item => UnwantedNames.Contains(item.Name)))
        {
            try
            {
                await extension.RemoveAsync();
                try
                {
                    BrowserExtensionPreferences.Forget(extension.Id);
                }
                catch (Exception exception)
                {
                    notices.Add($"{extension.Name} was removed, but its pin preference could not be cleared: {exception.Message}");
                }
                try
                {
                    BrowserExtensionImportService.ForgetAndDeleteSource(extension.Id);
                }
                catch (Exception exception)
                {
                    notices.Add($"{extension.Name} was removed, but its local extension files could not be cleaned up: {exception.Message}");
                }
            }
            catch (Exception exception)
            {
                notices.Add($"{extension.Name} could not be removed automatically: {exception.Message}");
            }
        }

        return notices;
    }
}
